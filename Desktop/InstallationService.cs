using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PeakWorkshop.Desktop;

internal sealed record InstallationStatus(bool CanInstall, bool CanUninstall, bool Installed, string Message, string InstallLabel);

internal sealed class InstallationService
{
    internal const string PluginPath = "BepInEx/plugins/PeakWorkshop/PeakWorkshop.Plugin.dll";
    private readonly string _root, _payloadRoot;
    private readonly Func<bool> _isGameRunning;
    private readonly InstallationPolicy _policy;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false, WriteIndented = true };
    internal Action<string>? TestMutationHook { get; set; }

    public InstallationService(string portableRoot, Func<bool>? isGameRunning = null)
        : this(portableRoot, isGameRunning, InstallationPolicy.Supported) { }

    internal InstallationService(string portableRoot, Func<bool>? isGameRunning, InstallationPolicy policy)
    {
        _root = PathSafety.Normalize(portableRoot);
        _payloadRoot = PathSafety.Child(_root, "data/game-files");
        _isGameRunning = isGameRunning ?? GameRunning;
        _policy = policy;
    }

    private static bool GameRunning()
    {
        Process[] processes = Process.GetProcessesByName("PEAK");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public InstallationStatus Inspect(string gameRoot)
    {
        try
        {
            string game = ValidateGame(gameRoot);
            ValidatePayload();
            string store = Store(game);
            if (File.Exists(PathSafety.Child(store, "pending.json")))
                return new(!_isGameRunning(), false, false, "上次安装操作未完成。请关闭游戏后恢复，再重新检查。", "恢复上次操作");
            var record = ReadRecord(game);
            ValidateOwnedFiles(game, record);
            bool loaderPresent;
            try { loaderPresent = EnsureCompatibleLoader(game); }
            catch (IOException ex) when (record?.Installed == true)
            {
                return new(false, !_isGameRunning(), false, ex.Message + "\n仍可卸载本目录管理且未被修改的文件。", "安装游戏插件");
            }
            string plugin = PathSafety.Child(game, PluginPath);
            bool exact = File.Exists(plugin) && Hash(plugin) == _policy.Files[PluginPath];
            bool owned = record?.Installed == true;
            if (exact)
                return new(false, owned && !_isGameRunning(), true,
                    owned ? "插件已安装并校验通过。可从 Steam 启动游戏。" : "已检测到相同插件，可直接连接；此便携目录未安装它，因此不会提供卸载。", "插件已安装");
            if (File.Exists(plugin) && !(owned && record!.Files.Any(f => f.Path == PluginPath)))
                throw new IOException("已有来源不明或版本不同的同名插件，未覆盖。请先由原安装方式处理。");
            bool running = _isGameRunning();
            if (!running) CheckWriteAccess(game, loaderPresent ? new[] { PluginPath } : _policy.Files.Keys);
            return new(!running, owned && !running, false,
                running ? "请先正常退出 PEAK，再安装或更新游戏插件。" : owned ? "检测到本程序管理的旧版插件，可以备份后更新。" : "游戏目录与写入检查通过，可以安装插件；已有兼容加载器和配置会保留。",
                owned ? "更新游戏插件" : "安装游戏插件");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new(false, false, false, ex.Message, "安装游戏插件"); }
    }

    private static void CheckWriteAccess(string game, IEnumerable<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relative in paths)
        {
            string target = PathSafety.Child(game, relative);
            if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("需要更新的文件为只读，请先检查目录权限：" + relative);
            string? parent = Path.GetDirectoryName(target);
            while (parent != null && !Directory.Exists(parent))
            {
                if (File.Exists(parent)) throw new IOException("需要的游戏子目录被同名文件占用，未修改文件：" + parent);
                parent = Path.GetDirectoryName(parent);
            }
            if (parent != null) directories.Add(parent);
        }
        try
        {
            foreach (string directory in directories)
            {
                PathSafety.NoReparse(directory);
                string probe = Path.Combine(directory, ".peakworkshop-write-test-" + Guid.NewGuid().ToString("N"));
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("游戏目录没有写入权限。请关闭程序，再右键 PeakWorkshop.exe 选择“以管理员身份运行”后重试。", ex);
        }
    }

    public void Install(string gameRoot)
    {
        string game = ValidateGame(gameRoot);
        using var lease = AcquireLease(game);
        RequireStopped();
        ValidatePayload();
        if (RecoverIfNeeded(game)) return;
        ValidateGame(game);
        var old = ReadRecord(game);
        ValidateOwnedFiles(game, old);
        bool loaderPresent = EnsureCompatibleLoader(game);
        string plugin = PathSafety.Child(game, PluginPath);
        if (File.Exists(plugin) && Hash(plugin) == _policy.Files[PluginPath]) return;
        bool ownedPlugin = old?.Installed == true && old.Files.Any(f => f.Path == PluginPath);
        if (File.Exists(plugin) && !ownedPlugin)
            throw new IOException("已有来源不明或版本不同的同名插件，未覆盖。");
        var paths = loaderPresent ? new[] { PluginPath } : _policy.Files.Keys.ToArray();
        foreach (string path in paths)
        {
            string target = PathSafety.Child(game, path);
            if (File.Exists(target) && path != PluginPath)
                throw new IOException("已有同名文件，安装已停止：" + path);
            if (Directory.Exists(target)) throw new IOException("安装目标是目录：" + path);
        }
        var next = old?.Installed == true ? old : new InstallationRecord { GameRoot = game };
        next.PluginVersion = _policy.PluginVersion;
        next.Installed = true;
        foreach (string path in paths)
        {
            next.Files.RemoveAll(f => f.Path == path);
            next.Files.Add(new OwnedFile { Path = path, Sha256 = _policy.Files[path] });
        }
        Execute(game, paths.Select(path => new Change { Path = path, AfterHash = _policy.Files[path] }).ToList(), next, "install");
    }

    public void Uninstall(string gameRoot)
    {
        string game = ValidateGame(gameRoot);
        using var lease = AcquireLease(game);
        RequireStopped();
        ValidatePayload();
        if (RecoverIfNeeded(game)) return;
        var record = ReadRecord(game);
        if (record?.Installed != true) throw new IOException("本便携目录没有管理这次安装，未移除任何文件。");
        ValidateOwnedFiles(game, record);
        bool foreign = ForeignModsExist(game, record);
        var remove = record.Files.Where(f => !foreign || f.Path == PluginPath).Select(f => new Change { Path = f.Path }).ToList();
        var next = new InstallationRecord { GameRoot = game, Installed = false, PluginVersion = record.PluginVersion,
            SharedLoaderRetained = foreign, Files = record.Files.Where(f => remove.All(c => c.Path != f.Path)).ToList() };
        Execute(game, remove, next, "uninstall");
    }

    private string ValidateGame(string value)
    {
        string game = PathSafety.Normalize(value);
        PathSafety.NoReparse(game);
        if (IsWithin(game, _root) || IsWithin(_root, game))
            throw new IOException("便携目录与游戏目录不能互相包含，请将修改器解压到单独文件夹。");
        string exe = PathSafety.Child(game, "PEAK.exe");
        string assembly = PathSafety.Child(game, "PEAK_Data/Managed/Assembly-CSharp.dll");
        if (!File.Exists(exe) || !File.Exists(assembly)) throw new IOException("所选目录没有完整的 PEAK 游戏程序与数据文件。");
        return game;
    }

    private void ValidatePayload()
    {
        PathSafety.NoReparse(_root);
        var manifest = ReadJson<PayloadManifest>(PathSafety.Child(_root, "data/payload.json"));
        if (manifest.schema != 1 || manifest.pluginVersion != _policy.PluginVersion || manifest.supportedAssemblySha256 != _policy.GameSha256 || manifest.files == null || manifest.files.Count != _policy.Files.Count)
            throw new IOException("随包插件清单不匹配，请重新完整解压压缩包。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.files)
        {
            if (file == null) throw new IOException("随包文件清单含有空项目。");
            string relative = PathSafety.Relative(file.path);
            if (!seen.Add(relative) || !_policy.Files.TryGetValue(relative, out string? trusted) || file.sha256 != trusted)
                throw new IOException("随包文件清单存在未知、重复或被修改的文件。");
            string path = PathSafety.Child(_payloadRoot, relative);
            if (!File.Exists(path) || Hash(path) != trusted) throw new IOException("随包文件缺失或校验失败，请重新解压：" + relative);
        }
        foreach (string path in SafeFiles(_payloadRoot))
            if (!_policy.Files.ContainsKey(Path.GetRelativePath(_payloadRoot, path).Replace('\\', '/')))
                throw new IOException("随包插件目录含有额外文件，请使用完整的原始压缩包。");
    }

    // Existing loaders are reused only when every runtime binary is identical.
    // XML/doc files can differ or be absent. User configuration is read, never rewritten.
    private bool EnsureCompatibleLoader(string game)
    {
        bool exists = Directory.Exists(PathSafety.Child(game, "BepInEx")) || File.Exists(PathSafety.Child(game, "winhttp.dll")) || File.Exists(PathSafety.Child(game, "doorstop_config.ini"));
        if (!exists) return false;
        // Uninstall intentionally leaves generated config/log folders. A prior
        // successful uninstall may therefore leave BepInEx without its loader.
        // Only our own completed uninstall record allows that clean reinstall.
        var prior = ReadRecord(game);
        if (prior?.Installed == false && !prior.SharedLoaderRetained &&
            _policy.Files.Keys.All(path => !File.Exists(PathSafety.Child(game, path))))
        {
            string bepinex = PathSafety.Child(game, "BepInEx");
            if (!Directory.Exists(bepinex) || !SafeFiles(bepinex).Any(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) return false;
        }
        foreach (var file in _policy.Files.Where(f => f.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && f.Key != PluginPath))
        {
            string path = PathSafety.Child(game, file.Key);
            if (!File.Exists(path) || Hash(path) != file.Value) throw new IOException("现有 BepInEx 加载器版本不兼容或不完整，未覆盖已有文件。请使用匹配的加载器环境。");
        }
        string config = PathSafety.Child(game, "doorstop_config.ini");
        if (!File.Exists(config)) throw new IOException("现有加载器缺少 doorstop 配置，未自动修改。");
        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inGeneral = false;
        foreach (string raw in ReadBounded(config, 64 * 1024).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[')) { inGeneral = line.Equals("[General]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!inGeneral) continue;
            int equal = line.IndexOf('=');
            if (equal <= 0 || !general.TryAdd(line[..equal].Trim(), line[(equal + 1)..].Trim())) throw new IOException("加载器配置不明确，未自动修改。");
        }
        if (!general.TryGetValue("enabled", out string? enabled) || !enabled.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            !general.TryGetValue("target_assembly", out string? target) || !target.Replace('\\', '/').Equals("BepInEx/core/BepInEx.Preloader.dll", StringComparison.OrdinalIgnoreCase))
            throw new IOException("现有加载器未启用或入口配置不同，未改动配置。请先处理现有加载器。");
        return true;
    }

    private string Store(string game) => PathSafety.Child(_root, "backups/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToUpperInvariant())))[..24]);
    private string RecordPath(string game) => PathSafety.Child(Store(game), "installed-files.json");
    private InstallationRecord? ReadRecord(string game)
    {
        string path = RecordPath(game);
        if (!File.Exists(path)) return null;
        var record = ReadJson<InstallationRecord>(path);
        if (record.Schema != 1 || !string.Equals(record.GameRoot, game, StringComparison.OrdinalIgnoreCase) || record.Files == null || record.Files.Count > _policy.Files.Count)
            throw new IOException("本地安装记录无效或属于其他游戏目录，未修改文件。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in record.Files)
            if (file == null || !_policy.Files.ContainsKey(PathSafety.Relative(file.Path)) || !seen.Add(file.Path) || !ValidHash(file.Sha256)) throw new IOException("安装记录含有不允许的文件，未修改文件。");
        return record;
    }

    private void ValidateOwnedFiles(string game, InstallationRecord? record)
    {
        if (record?.Installed != true) return;
        foreach (var file in record.Files)
        {
            string target = PathSafety.Child(game, file.Path);
            if (!File.Exists(target) || Hash(target) != file.Sha256)
                throw new IOException("已安装文件被修改或丢失，未自动覆盖或卸载：" + file.Path);
        }
    }

    private bool ForeignModsExist(string game, InstallationRecord record)
    {
        var known = record.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[] { "BepInEx/plugins", "BepInEx/patchers", "BepInEx/core" })
        {
            string path = PathSafety.Child(game, directory);
            if (!Directory.Exists(path)) continue;
            foreach (string file in SafeFiles(path))
                if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !known.Contains(Path.GetRelativePath(game, file).Replace('\\', '/'))) return true;
        }
        return false;
    }

    private IDisposable AcquireLease(string game)
    {
        string store = Store(game);
        PathSafety.CreateDirectory(store);
        try { return new FileStream(PathSafety.Child(store, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("另一个安装操作正在处理此游戏，请稍后重试。", ex); }
    }

    private void Execute(string game, List<Change> changes, InstallationRecord next, string operation)
    {
        RequireStopped();
        string store = Store(game);
        string transaction = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
        string backup = PathSafety.Child(store, transaction);
        PathSafety.CreateDirectory(backup);
        string recordPath = RecordPath(game);
        var journal = new Journal { GameRoot = game, Operation = operation, Changes = changes,
            OldManifest = File.Exists(recordPath) ? ReadBounded(recordPath, 256 * 1024) : null, Transaction = transaction };
        foreach (var change in changes)
        {
            string target = PathSafety.Child(game, change.Path);
            change.BeforeHash = File.Exists(target) ? Hash(target) : null;
            change.BackupPath = transaction + "/" + change.Path;
            change.TemporaryPath = change.Path + ".peakworkshop-" + transaction + ".tmp";
            if (File.Exists(PathSafety.Child(game, change.TemporaryPath))) throw new IOException("安装临时路径已存在，未修改游戏。");
            if (change.BeforeHash != null)
            {
                string copy = PathSafety.Child(store, change.BackupPath);
                PathSafety.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(target, copy, false);
                if (Hash(copy) != change.BeforeHash) throw new IOException("安装备份校验失败，游戏文件尚未更改。");
            }
        }
        string pending = PathSafety.Child(store, "pending.json");
        WriteAtomic(pending, JsonSerializer.Serialize(journal, Json));
        try
        {
            foreach (var change in changes)
            {
                RequireStopped();
                string target = PathSafety.Child(game, change.Path);
                RequireCurrent(target, change.BeforeHash);
                TestMutationHook?.Invoke(change.Path);
                RequireStopped();
                if (change.AfterHash == null) File.Delete(target);
                else
                {
                    string source = PathSafety.Child(_payloadRoot, change.Path);
                    if (Hash(source) != change.AfterHash) throw new IOException("安装源文件在检查后发生变化。");
                    CopyAtomic(source, target, change.AfterHash, change.BeforeHash != null, PathSafety.Child(game, change.TemporaryPath));
                }
            }
            RequireStopped();
            next.UpdatedUtc = DateTime.UtcNow.ToString("o");
            WriteAtomic(recordPath, JsonSerializer.Serialize(next, Json));
            // The manifest and journal form a small transaction. A crash before
            // this delete is recovered to the pre-operation state on next launch.
            File.Delete(pending);
        }
        catch (Exception original)
        {
            try { Rollback(game, journal); File.Delete(pending); }
            catch (Exception recovery) { throw new IOException("操作未完成，恢复记录已保留。请关闭游戏后点击“恢复上次操作”。原因：" + original.Message + "；恢复：" + recovery.Message, original); }
            throw new IOException("操作未完成，已恢复原文件和安装记录：" + original.Message, original);
        }
    }

    private bool RecoverIfNeeded(string game)
    {
        string pending = PathSafety.Child(Store(game), "pending.json");
        if (!File.Exists(pending)) return false;
        var journal = ReadJson<Journal>(pending);
        ValidateJournal(game, journal);
        Rollback(game, journal);
        File.Delete(pending);
        return true;
    }

    private void ValidateJournal(string game, Journal journal)
    {
        if (!string.Equals(journal.GameRoot, game, StringComparison.OrdinalIgnoreCase) || journal.Schema != 1 || journal.Changes == null || journal.Changes.Count > _policy.Files.Count ||
            journal.Transaction == null || journal.Transaction.Length != 52 || journal.Transaction.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new IOException("未完成操作的恢复记录无效，未修改游戏。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in journal.Changes)
        {
            if (change == null || !_policy.Files.ContainsKey(PathSafety.Relative(change.Path)) || !seen.Add(change.Path) || change.BackupPath != journal.Transaction + "/" + change.Path ||
                change.TemporaryPath != change.Path + ".peakworkshop-" + journal.Transaction + ".tmp" ||
                change.BeforeHash != null && !ValidHash(change.BeforeHash) || change.AfterHash != null && !ValidHash(change.AfterHash))
                throw new IOException("恢复记录含有不允许的文件，未修改游戏。");
            if (change.BeforeHash != null && Hash(PathSafety.Child(Store(game), change.BackupPath)) != change.BeforeHash)
                throw new IOException("恢复备份校验失败，未修改游戏。");
        }
        if (journal.OldManifest != null)
        {
            var old = JsonSerializer.Deserialize<InstallationRecord>(journal.OldManifest, Json) ?? throw new IOException("旧安装清单为空。");
            if (!string.Equals(old.GameRoot, game, StringComparison.OrdinalIgnoreCase)) throw new IOException("旧安装清单属于其他目录。");
        }
    }

    private void Rollback(string game, Journal journal)
    {
        RequireStopped();
        ValidateJournal(game, journal);
        // Validate every current file before changing any, so later user edits
        // never get silently replaced by recovery from an old transaction.
        foreach (var change in journal.Changes)
        {
            string target = PathSafety.Child(game, change.Path);
            string? actual = File.Exists(target) ? Hash(target) : null;
            if (actual != change.BeforeHash && actual != change.AfterHash) throw new IOException("恢复目标已被其他程序修改：" + change.Path);
        }
        foreach (var change in journal.Changes.AsEnumerable().Reverse())
        {
            RequireStopped();
            string target = PathSafety.Child(game, change.Path);
            string temporary = PathSafety.Child(game, change.TemporaryPath);
            if (File.Exists(temporary)) File.Delete(temporary);
            if (change.BeforeHash == null) { if (File.Exists(target)) File.Delete(target); }
            else CopyAtomic(PathSafety.Child(Store(game), change.BackupPath), target, change.BeforeHash, true, temporary);
        }
        string manifest = RecordPath(game);
        if (journal.OldManifest == null) { if (File.Exists(manifest)) File.Delete(manifest); }
        else WriteAtomic(manifest, journal.OldManifest);
    }

    private void RequireStopped() { if (_isGameRunning()) throw new IOException("请先正常退出 PEAK，再安装、更新、卸载或恢复插件。"); }
    private static bool IsWithin(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void RequireCurrent(string path, string? expected) { if ((File.Exists(path) ? Hash(path) : null) != expected) throw new IOException("文件在检查后发生变化，操作已停止。"); }
    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(ReadBounded(path, 256 * 1024), Json) ?? throw new IOException("文件清单为空。");
    private static string ReadBounded(string path, long max) { PathSafety.NoReparse(path); using var stream = File.OpenRead(path); if (stream.Length > max) throw new IOException("文件清单超过长度限制。"); using var reader = new StreamReader(stream, Encoding.UTF8, true); return reader.ReadToEnd(); }
    private static IEnumerable<string> SafeFiles(string directory)
    {
        PathSafety.NoReparse(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            PathSafety.NoReparse(path);
            if (Directory.Exists(path)) { foreach (string nested in SafeFiles(path)) yield return nested; }
            else yield return path;
        }
    }
    private static void WriteAtomic(string path, string text)
    {
        PathSafety.NoReparse(path);
        PathSafety.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { byte[] data = Encoding.UTF8.GetBytes(text); stream.Write(data); stream.Flush(true); } File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void CopyAtomic(string source, string target, string expectedHash, bool overwrite, string temporary)
    {
        PathSafety.NoReparse(source); PathSafety.NoReparse(target);
        PathSafety.CreateDirectory(Path.GetDirectoryName(target)!);
        PathSafety.NoReparse(temporary);
        if (File.Exists(temporary)) throw new IOException("事务临时文件已存在，未覆盖。");
        try { File.Copy(source, temporary, false); if (Hash(temporary) != expectedHash) throw new IOException("复制后文件校验失败。"); RequireStopped(); File.Move(temporary, target, overwrite); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal sealed class PayloadManifest { public int schema { get; set; } public string pluginVersion { get; set; } = ""; public string supportedAssemblySha256 { get; set; } = ""; public List<PayloadFile> files { get; set; } = new(); }
    internal sealed class PayloadFile { public string path { get; set; } = ""; public string sha256 { get; set; } = ""; }
    internal sealed class InstallationRecord { public int Schema { get; set; } = 1; public string GameRoot { get; set; } = ""; public string PluginVersion { get; set; } = ""; public bool Installed { get; set; } public bool SharedLoaderRetained { get; set; } public string UpdatedUtc { get; set; } = ""; public List<OwnedFile> Files { get; set; } = new(); }
    internal sealed class OwnedFile { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; }
    internal sealed class Change { public string Path { get; set; } = ""; public string? BeforeHash { get; set; } public string? AfterHash { get; set; } public string BackupPath { get; set; } = ""; public string TemporaryPath { get; set; } = ""; }
    internal sealed class Journal { public int Schema { get; set; } = 1; public string GameRoot { get; set; } = ""; public string Operation { get; set; } = ""; public string Transaction { get; set; } = ""; public string? OldManifest { get; set; } public List<Change> Changes { get; set; } = new(); }
}

internal static class PathSafety
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new IOException("请先选择有效的游戏或便携目录。");
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.Length < 4) throw new IOException("请选择本地磁盘上的独立目录。");
        return full;
    }
    public static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path) || path.Contains(':') || path.Split('/').Any(s => s is "" or "." or ".." || s.EndsWith('.') || s.EndsWith(' ')))
            throw new IOException("文件清单含有不安全的相对路径。");
        return path;
    }
    public static string Child(string root, string relative)
    {
        Relative(relative);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("文件路径超出允许目录。");
        NoReparse(full);
        return full;
    }
    public static void NoReparse(string path)
    {
        string? walk = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(walk))
        {
            try { if ((File.GetAttributes(walk) & FileAttributes.ReparsePoint) != 0) throw new IOException("检测到符号链接或目录联接，未自动修改文件。"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            walk = Path.GetDirectoryName(walk);
        }
    }
    public static void CreateDirectory(string path) { NoReparse(path); Directory.CreateDirectory(path); NoReparse(path); }
}
