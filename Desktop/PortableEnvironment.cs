using System.IO;
using System.Text.Json;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Desktop;

internal static class PortableEnvironment
{
    public static string Root => AppContext.BaseDirectory;
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string LogsPath => Path.Combine(Root, "logs");
    public static string BackupsPath => Path.Combine(Root, "backups");
    public static string DataPath => Path.Combine(Root, "data");

    public static void EnsureWritable() => EnsureWritable(Root);

    internal static void EnsureWritable(string root)
    {
        string? probe = null;
        try
        {
            EnsureSafePath(root);
            if (!Directory.Exists(root)) throw new IOException("程序文件夹不存在。");
            foreach (string name in new[] { "logs", "backups" })
            {
                string directory = Path.Combine(root, name);
                EnsureSafePath(directory);
                Directory.CreateDirectory(directory);
            }
            EnsureSafePath(Path.Combine(root, "settings.json"));
            probe = Path.Combine(root, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { throw new IOException("便携目录无法保存设置、日志或备份。请将整个压缩包解压到可写文件夹，再打开程序。\n" + ex.Message, ex); }
        finally
        {
            if (probe != null && File.Exists(probe))
            {
                try { File.Delete(probe); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void EnsureSafePath(string path)
    {
        string full = Path.GetFullPath(path);
        string? current = full;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("便携文件夹或其路径包含链接，不能自动写入：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
}

internal sealed class PortableSettings
{
    private const int MaxSettingsBytes = 256 * 1024;
    private static readonly object Gate = new();
    public string GamePath { get; set; } = "";
    public Dictionary<string, float> Values { get; set; } = new(StringComparer.Ordinal);

    public static PortableSettings Load(string? filePath = null)
    {
        string path = filePath ?? PortableEnvironment.SettingsPath;
        lock (Gate)
        {
            PortableEnvironment.EnsureSafePath(path);
            if (!File.Exists(path)) return new PortableSettings();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxSettingsBytes) throw new IOException("设置文件过大，请检查 settings.json。");
            var bytes = new byte[MaxSettingsBytes + 1];
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) != 0) count += read;
            if (count > MaxSettingsBytes) throw new IOException("设置文件过大，请检查 settings.json。");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("设置文件格式无效。");
            var result = new PortableSettings();
            if (document.RootElement.TryGetProperty("gamePath", out var game) && game.ValueKind == JsonValueKind.String)
                result.GamePath = game.GetString() ?? "";
            if (document.RootElement.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Object)
                foreach (var entry in values.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Number && entry.Value.TryGetSingle(out float value))
                        AddValidValue(result.Values, entry.Name, value);
            return result;
        }
    }

    public void Save(string? filePath = null)
    {
        string path = Path.GetFullPath(filePath ?? PortableEnvironment.SettingsPath);
        lock (Gate)
        {
            PortableEnvironment.EnsureSafePath(path);
            var valid = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var entry in Values) AddValidValue(valid, entry.Key, entry.Value);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { gamePath = GamePath, values = valid }, new JsonSerializerOptions { WriteIndented = true });
            if (bytes.Length > MaxSettingsBytes) throw new IOException("设置内容过大，未写入。");
            string parent = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(parent)) throw new IOException("设置文件夹不存在。");
            string temp = Path.Combine(parent, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
                PortableEnvironment.EnsureSafePath(path);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static void AddValidValue(Dictionary<string, float> target, string id, float value)
    {
        var feature = FeatureCatalog.Find(id);
        if (feature?.HasValue == true && float.IsFinite(value))
            target[id] = Math.Clamp(value, feature.Min, feature.Max);
    }
}
