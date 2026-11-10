using System.Security.Cryptography;
using System.Text;

namespace PeakWorkshop.Desktop;

internal sealed record GameVersionInfo(string Build, string AssemblySha256, string Detail)
{
    internal static string ShortHash(string value) => value.Length == 64 ? value[..12] + "…" : "未读取";

    internal static GameVersionInfo Read(string gamePath, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return new("未选择游戏", "", "请先选择游戏目录。");
        string root = SteamDiscovery.ValidateGameDirectory(gamePath);
        string build = "未知";
        var notes = new List<string>();
        token.ThrowIfCancellationRequested();
        try
        {
            string? steamApps = Directory.GetParent(root)?.Parent?.FullName;
            string manifest = Path.Combine(steamApps ?? root, "appmanifest_" + SteamDiscovery.PeakAppId + ".acf");
            using var stream = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int limit = 1024 * 1024;
            if (stream.Length > limit) throw new IOException("Steam 安装信息过大。");
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var buffer = new char[limit + 1];
            int count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count > limit) throw new IOException("Steam 安装信息过大。");
            var app = SteamDiscovery.ParseVdf(new string(buffer, 0, count)).Child("AppState");
            string? installDir = app?.Child("installdir")?.Value;
            string? value = app?.Child("buildid")?.Value;
            // A nearby manifest is used only when it actually describes this game directory.
            if (app?.Child("appid")?.Value == SteamDiscovery.PeakAppId &&
                !string.IsNullOrEmpty(installDir) && !Path.IsPathRooted(installDir) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(steamApps!, "common", installDir))), root, StringComparison.OrdinalIgnoreCase) &&
                value is { Length: > 0 and <= 20 } && value.All(char.IsAsciiDigit))
                build = value;
            else notes.Add("Steam 清单未提供此游戏目录的有效 Build。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { notes.Add("Build 未读取：" + ex.Message); }

        token.ThrowIfCancellationRequested();
        string hash = "";
        try
        {
            using var stream = new FileStream(Path.Combine(root, "PEAK_Data", "Managed", "Assembly-CSharp.dll"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { notes.Add("程序集校验值未读取：" + ex.Message); }
        token.ThrowIfCancellationRequested();
        return new(build, hash, string.Join("\n", notes));
    }
}
