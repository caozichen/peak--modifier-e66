using System.IO;
using System.Text;
using Microsoft.Win32;

namespace PeakWorkshop.Desktop;

internal sealed record GameCandidate(string Path, string Source)
{
    public override string ToString() => Path;
}

internal static class SteamDiscovery
{
    internal const string PeakAppId = "3527290";
    private const int MaxVdfBytes = 1024 * 1024;

    public static IReadOnlyList<GameCandidate> FindInstalledGames(string? savedPath = null) =>
        FindInstalledGames(savedPath, SteamRoots());

    internal static IReadOnlyList<GameCandidate> FindInstalledGames(string? savedPath, IEnumerable<string> steamRoots)
    {
        var result = new List<GameCandidate>();
        var seenGames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddGame(string? path, string source)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string valid = ValidateGameDirectory(path);
                if (seenGames.Add(valid)) result.Add(new GameCandidate(valid, source));
            }
            catch (Exception ex) when (IsFilesystemError(ex)) { }
        }

        AddGame(savedPath, "上次选择");
        foreach (string root in steamRoots.Take(32))
        {
            try
            {
                string steam = Normalize(root);
                if (!Directory.Exists(Path.Combine(steam, "steamapps"))) continue;
                libraries.Add(steam);
                var folders = ParseFile(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
                var entries = folders.Child("libraryfolders");
                if (entries == null) continue;
                foreach (var entry in entries.Entries)
                {
                    if (!uint.TryParse(entry.Key, out _)) continue;
                    string? path = entry.Value.Value ?? entry.Value.Child("path")?.Value;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    try { libraries.Add(Normalize(path)); }
                    catch (Exception ex) when (IsFilesystemError(ex)) { }
                }
            }
            catch (Exception ex) when (IsFilesystemError(ex) || ex is FormatException) { }
        }

        foreach (string library in libraries.Take(256))
        {
            try
            {
                string apps = Path.Combine(library, "steamapps");
                string manifestPath = Path.Combine(apps, "appmanifest_" + PeakAppId + ".acf");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var app = ParseFile(manifestPath).Child("AppState");
                        if (app?.Child("appid")?.Value == PeakAppId)
                        {
                            string? installDir = app.Child("installdir")?.Value;
                            if (IsSafeInstallDirectory(installDir))
                            {
                                string common = Normalize(Path.Combine(apps, "common"));
                                string candidate = Normalize(Path.Combine(common, installDir!));
                                if (candidate.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                                    AddGame(candidate, "Steam 游戏库");
                            }
                        }
                    }
                    catch (Exception ex) when (IsFilesystemError(ex) || ex is FormatException) { }
                }
                // This fallback is limited to libraries already reported by Steam.
                AddGame(Path.Combine(apps, "common", "PEAK"), "Steam 游戏库");
            }
            catch (Exception ex) when (IsFilesystemError(ex)) { }
        }
        return result;
    }

    public static string ValidateGameDirectory(string path)
    {
        string root;
        try { root = Normalize(path); }
        catch (Exception ex) when (IsFilesystemError(ex))
        { throw new IOException("游戏目录无效，请选择解压后的真实 PEAK 游戏文件夹。", ex); }
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "PEAK.exe")) ||
            !File.Exists(Path.Combine(root, "PEAK_Data", "Managed", "Assembly-CSharp.dll")))
            throw new IOException("所选目录不是完整的 PEAK 游戏目录：需要 PEAK.exe 和 PEAK_Data/Managed/Assembly-CSharp.dll。");
        return root;
    }

    private static bool IsSafeInstallDirectory(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathRooted(value) && !value.Contains(':') &&
        !value.Split(new[] { '/', '\\' }).Any(part => part is "" or "." or "..");

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new IOException("需要完整的本地文件夹路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool IsFilesystemError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static IEnumerable<string> SteamRoots()
    {
        var paths = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var (hive, view, keyName) in new[]
            {
                (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Valve\Steam"),
                (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Valve\Steam"),
                (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Valve\Steam")
            })
            {
                try
                {
                    using var hiveKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = hiveKey.OpenSubKey(keyName);
                    foreach (string valueName in new[] { "SteamPath", "InstallPath", "SteamExe" })
                        if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                            paths.Add(valueName == "SteamExe" ? Path.GetDirectoryName(value)! : value);
                }
                catch (Exception ex) when (IsFilesystemError(ex)) { }
            }
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) paths.Add(Path.Combine(path, "Steam"));
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static VdfNode ParseFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxVdfBytes) throw new FormatException("Steam 安装信息过大。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var text = new char[MaxVdfBytes + 1];
        int count = reader.ReadBlock(text, 0, text.Length);
        if (count > MaxVdfBytes) throw new FormatException("Steam 安装信息过大。");
        return ParseVdf(new string(text, 0, count));
    }

    internal sealed class VdfNode
    {
        internal string? Value;
        internal readonly Dictionary<string, VdfNode> Entries = new(StringComparer.OrdinalIgnoreCase);
        internal VdfNode? Child(string key) => Entries.GetValueOrDefault(key);
    }

    internal static VdfNode ParseVdf(string text)
    {
        if (text.Length > MaxVdfBytes) throw new FormatException("Steam 安装信息过大。");
        int offset = 0, tokenCount = 0;
        void Skip()
        {
            while (offset < text.Length)
            {
                if (char.IsWhiteSpace(text[offset]) || text[offset] == '\uFEFF') { offset++; continue; }
                if (offset + 1 < text.Length && text[offset] == '/' && text[offset + 1] == '/')
                { while (offset < text.Length && text[offset] != '\n') offset++; continue; }
                break;
            }
        }
        string? Token()
        {
            Skip();
            if (offset == text.Length) return null;
            if (++tokenCount > 100000) throw new FormatException("Steam 安装信息项目过多。");
            char first = text[offset++];
            if (first is '{' or '}') return first.ToString();
            var token = new StringBuilder();
            if (first == '"')
            {
                while (offset < text.Length)
                {
                    char c = text[offset++];
                    if (c == '"') return "\0" + token;
                    if (c == '\\' && offset < text.Length)
                    {
                        char next = text[offset++];
                        if (next is '\\' or '"') token.Append(next);
                        else { token.Append('\\'); token.Append(next); }
                    }
                    else token.Append(c);
                }
                throw new FormatException("Steam 安装信息字符串未结束。");
            }
            token.Append(first);
            while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] is not '{' and not '}')
                token.Append(text[offset++]);
            return "\0" + token;
        }
        VdfNode Object(int depth, bool nested)
        {
            if (depth > 32) throw new FormatException("Steam 安装信息嵌套过深。");
            var result = new VdfNode();
            while (true)
            {
                string? key = Token();
                if (key == null)
                {
                    if (nested) throw new FormatException("Steam 安装信息对象未结束。");
                    return result;
                }
                if (key == "}")
                {
                    if (!nested) throw new FormatException("Steam 安装信息包含多余右括号。");
                    return result;
                }
                if (key == "{") throw new FormatException("Steam 安装信息缺少项目名。");
                string? value = Token();
                if (value == null || value == "}") throw new FormatException("Steam 安装信息缺少项目值。");
                var node = value == "{" ? Object(depth + 1, true) : new VdfNode { Value = value[1..] };
                if (!result.Entries.TryAdd(key[1..], node)) throw new FormatException("Steam 安装信息包含重复项目。");
            }
        }
        return Object(0, false);
    }
}
