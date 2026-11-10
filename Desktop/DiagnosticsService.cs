using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PeakWorkshop.Desktop;

internal static class DiagnosticsService
{
    internal const int MaxLogBytes = 1024 * 1024;

    internal static string Collect(string gameRoot)
    {
        PortableEnvironment.EnsureWritable();
        string destination = Path.Combine(PortableEnvironment.LogsPath, "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
        NoLinks(destination);
        Directory.CreateDirectory(destination);
        var notes = new List<string>();
        CopyTail(AppLog.FilePath, Path.Combine(destination, "desktop.log"), notes);
        string assemblyHash = "";
        if (!string.IsNullOrWhiteSpace(gameRoot))
        {
            try
            {
                string root = SteamDiscovery.ValidateGameDirectory(gameRoot);
                NoLinks(root);
                string log = Path.Combine(root, "BepInEx", "LogOutput.log");
                CopyTail(log, Path.Combine(destination, "BepInEx.LogOutput.log"), notes);
                string assembly = Path.Combine(root, "PEAK_Data", "Managed", "Assembly-CSharp.dll");
                NoLinks(assembly);
                using var input = File.OpenRead(assembly);
                assemblyHash = Convert.ToHexString(SHA256.HashData(input));
            }
            catch (Exception ex) { notes.Add("读取游戏诊断失败：" + ex.Message); }
        }
        File.WriteAllText(Path.Combine(destination, "diagnostics.json"), JsonSerializer.Serialize(new
        {
            desktopVersion = "0.3.2", bundledPluginVersion = "0.2.6", createdUtc = DateTime.UtcNow,
            gamePath = gameRoot, gameAssemblySha256 = assemblyHash,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            logTailLimitBytes = MaxLogBytes, notes
        }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        return destination;
    }

    private static void CopyTail(string source, string target, List<string> notes)
    {
        try
        {
            NoLinks(source);
            if (!File.Exists(source)) { notes.Add("未找到日志：" + Path.GetFileName(source)); return; }
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaxLogBytes) input.Seek(-MaxLogBytes, SeekOrigin.End);
            // Read at most a fixed tail even if the live game keeps appending.
            byte[] buffer = new byte[MaxLogBytes];
            int total = 0, count;
            while (total < buffer.Length && (count = input.Read(buffer, total, buffer.Length - total)) > 0) total += count;
            File.WriteAllText(target, Encoding.UTF8.GetString(buffer, 0, total), Encoding.UTF8);
        }
        catch (Exception ex) { notes.Add(Path.GetFileName(source) + "：" + ex.Message); }
    }

    private static void NoLinks(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("诊断路径包含目录链接，请使用普通文件夹。");
            current = Path.GetDirectoryName(current);
        }
    }
}
