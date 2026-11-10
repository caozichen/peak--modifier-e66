using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PeakWorkshop.Desktop;

var reports = new List<object>();
int failed = 0;
string fixtureRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[0]))!, "discovery-fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixtureRoot);
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Rejected(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or FormatException or JsonException) { return; } throw new Exception("Invalid input accepted"); }
void Test(string name, Action body)
{
    try { body(); reports.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; reports.Add(new { name, passed = false, error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
string Root(string name) { string path = Path.Combine(fixtureRoot, name); Directory.CreateDirectory(path); return path; }
string Steam(string name) { string path = Root(name); Directory.CreateDirectory(Path.Combine(path, "steamapps")); return path; }
string Game(string parent, string name = "PEAK")
{
    string path = Path.Combine(parent, "steamapps", "common", name);
    Directory.CreateDirectory(Path.Combine(path, "PEAK_Data", "Managed"));
    File.WriteAllText(Path.Combine(path, "PEAK.exe"), "fixture; never executed");
    File.WriteAllText(Path.Combine(path, "PEAK_Data", "Managed", "Assembly-CSharp.dll"), "fixture; never loaded");
    return path;
}
string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
void Library(string steam, string content) => File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), content);
void Manifest(string steam, string dir, string appid = "3527290") => File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_3527290.acf"), "\"AppState\" { \"appid\" " + Quote(appid) + " \"installdir\" " + Quote(dir) + " }");

Test("New Steam library format discovers each valid external library", () =>
{
    string main = Steam("new-main"), second = Steam("new-second"), third = Steam("new-third");
    string a = Game(second), b = Game(third);
    Library(main, "\"libraryfolders\" { \"0\" { \"path\" " + Quote(main) + " } \"1\" { \"path\" " + Quote(second) + " } \"2\" { \"path\" " + Quote(third) + " } }");
    var found = SteamDiscovery.FindInstalledGames(null, new[] { main });
    Assert(found.Count == 2 && found.Any(x => x.Path == a) && found.Any(x => x.Path == b), "External libraries missed");
});
Test("Old Steam library format discovers string-valued library paths", () =>
{
    string main = Steam("old-main"), external = Steam("old-other"); string game = Game(external);
    Library(main, "\"LibraryFolders\" { \"TimeNextStatsReport\" \"123\" \"1\" " + Quote(external) + " }");
    Assert(SteamDiscovery.FindInstalledGames(null, new[] { main }).Single().Path == game, "Old libraries missed");
});
Test("Saved selection is first and normalized duplicate candidates collapse", () =>
{
    string main = Steam("dedupe"); string game = Game(main);
    Library(main, "\"libraryfolders\" { \"0\" { \"path\" " + Quote(main) + " } \"1\" " + Quote(main + Path.DirectorySeparatorChar) + " }");
    var found = SteamDiscovery.FindInstalledGames(game + Path.DirectorySeparatorChar, new[] { main, main.ToUpperInvariant() });
    Assert(found.Count == 1 && found[0].Path == game && found[0].Source == "上次选择", "Saved/deduplication behavior changed");
});
Test("Escaped Windows paths with spaces and Unicode are parsed", () =>
{
    string main = Steam("spaces-main"), other = Steam("中文 游戏库 with spaces"); string game = Game(other);
    Library(main, "// header\n\"libraryfolders\" { // opening\n \"1\" { \"path\" " + Quote(other) + " \"label\" \"label with \\\"quote\\\" and { brace\" } } // done");
    Assert(SteamDiscovery.FindInstalledGames(null, new[] { main }).Single().Path == game, "Quoted path lost");
});
Test("Nested application metadata cannot masquerade as a library path", () =>
{
    string main = Steam("nested-main"), other = Steam("nested-other"); Game(other);
    Library(main, "\"libraryfolders\" { \"0\" { \"apps\" { \"path\" " + Quote(other) + " } } }");
    Assert(SteamDiscovery.FindInstalledGames(null, new[] { main }).Count == 0, "Unrelated path selected");
});
Test("Malformed VDF strings objects duplicate keys and extra braces are rejected", () =>
{
    foreach (string text in new[] { "\"a\" {", "\"a\" \"unterminated", "}", "\"a\"", "{ \"a\" \"b\" }", "\"a\" \"x\" \"A\" \"y\"" })
        Rejected(() => SteamDiscovery.ParseVdf(text));
});
Test("Bounded VDF size and nesting reject excessive metadata", () =>
{
    Rejected(() => SteamDiscovery.ParseVdf(new string('x', 1024 * 1024 + 1)));
    Rejected(() => SteamDiscovery.ParseVdf(string.Concat(Enumerable.Repeat("\"a\" {", 34)) + string.Concat(Enumerable.Repeat("}", 34))));
});
Test("Unavailable and malformed roots do not suppress a valid later library", () =>
{
    string broken = Steam("broken"); Library(broken, "\"libraryfolders\" {");
    string valid = Steam("after-broken"); string game = Game(valid);
    var found = SteamDiscovery.FindInstalledGames(null, new[] { Path.Combine(fixtureRoot, "absent"), "relative", broken, valid });
    Assert(found.Count == 1 && found[0].Path == game, "Valid library suppressed");
});
Test("Manifest uses actual PEAK app id and supports a moved installation name", () =>
{
    string library = Steam("manifest-ok"); string game = Game(library, "PEAK moved with spaces"); Manifest(library, "PEAK moved with spaces");
    Assert(SteamDiscovery.FindInstalledGames(null, new[] { library }).Single().Path == game, "Manifest install name ignored");
});
Test("A different app id cannot select its installation as PEAK", () =>
{
    string library = Steam("manifest-wrong"); Game(library, "different"); Manifest(library, "different", "12345");
    Assert(SteamDiscovery.FindInstalledGames(null, new[] { library }).Count == 0, "Wrong app selected");
});
Test("Manifest traversal absolute and drive-relative install directories are rejected", () =>
{
    string library = Steam("manifest-traversal"); string outside = Game(Steam("outside"), "outside");
    foreach (string dir in new[] { "..\\..\\outside\\steamapps\\common\\outside", outside, "C:outside", ".\\outside", "nested\\..\\outside" })
    { Manifest(library, dir); Assert(SteamDiscovery.FindInstalledGames(null, new[] { library }).Count == 0, "Manifest escaped common: " + dir); }
});
Test("Manual selection requires both executable and actual managed assembly", () =>
{
    string root = Root("fake-game"); Rejected(() => SteamDiscovery.ValidateGameDirectory(root));
    File.WriteAllText(Path.Combine(root, "PEAK.exe"), "fixture"); Rejected(() => SteamDiscovery.ValidateGameDirectory(root));
    Rejected(() => SteamDiscovery.ValidateGameDirectory("relative\\PEAK"));
});
Test("Multiple valid libraries remain separate candidates without choosing one", () =>
{
    string a = Steam("multi-a"), b = Steam("multi-b"); Game(a); Game(b);
    Assert(SteamDiscovery.FindInstalledGames(null, new[] { a, b }).Count == 2, "Multiple candidates collapsed");
});
Test("Steam fallback checks PEAK inside a known library when metadata is absent", () =>
{
    string library = Steam("fallback"); string game = Game(library);
    Assert(SteamDiscovery.FindInstalledGames(null, new[] { library }).Single().Path == game, "Fallback missed known library");
});
Test("Portable paths follow executable directory rather than current directory", () =>
{
    string before = Environment.CurrentDirectory;
    try { Environment.CurrentDirectory = Root("other-cwd"); Assert(PortableEnvironment.SettingsPath == Path.Combine(AppContext.BaseDirectory, "settings.json") && PortableEnvironment.LogsPath.StartsWith(AppContext.BaseDirectory), "CWD affects paths"); }
    finally { Environment.CurrentDirectory = before; }
});
Test("Writable portable folder creates only its own folders and removes write probe", () =>
{
    string root = Root("writable"); PortableEnvironment.EnsureWritable(root);
    Assert(Directory.Exists(Path.Combine(root, "logs")) && Directory.Exists(Path.Combine(root, "backups")) && Directory.GetFiles(root).Length == 0, "Probe/folders incorrect");
});
Test("Non-directory log target produces an explicit portable write failure", () =>
{
    string root = Root("write-failure"); File.WriteAllText(Path.Combine(root, "logs"), "foreign file");
    Rejected(() => PortableEnvironment.EnsureWritable(root)); Assert(File.ReadAllText(Path.Combine(root, "logs")) == "foreign file", "Existing file changed");
});
Test("Portable state rejects directory junctions before writing outside root", () =>
{
    string root = Root("junction-root"), outside = Root("junction-outside"), link = Path.Combine(root, "logs");
    var command = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + outside.Replace("'", "''") + "' -ErrorAction Stop | Out-Null" }) command.ArgumentList.Add(argument);
    using var process = Process.Start(command)!;
    process.WaitForExit(); Assert(process.ExitCode == 0, "Junction fixture unavailable: " + process.StandardError.ReadToEnd());
    Rejected(() => PortableEnvironment.EnsureWritable(root)); Assert(Directory.GetFiles(outside).Length == 0, "Junction target modified");
});
Test("Missing settings return empty disabled preferences", () =>
{
    var state = PortableSettings.Load(Path.Combine(Root("settings-empty"), "settings.json"));
    Assert(state.GamePath == "" && state.Values.Count == 0, "Missing settings enabled/persisted anything");
});
Test("Settings clamp finite known numeric values and discard toggles unknown IDs and text", () =>
{
    string path = Path.Combine(Root("settings-filter"), "settings.json");
    File.WriteAllText(path, "{\"gamePath\":\"selected game\",\"enabled\":true,\"values\":{\"speed\":999,\"jump\":-1,\"ping.size\":50,\"fog.stop\":1,\"unknown\":10,\"climb.speed\":\"200\"}}");
    var state = PortableSettings.Load(path);
    Assert(state.GamePath == "selected game" && state.Values.Count == 3 && state.Values["speed"] == 200 && state.Values["jump"] == .2f && state.Values["ping.size"] == 50, "Value filtering incorrect");
});
Test("Atomic settings replacement preserves values but never serializes enabled state", () =>
{
    string root = Root("settings-save"), path = Path.Combine(root, "settings.json");
    var first = new PortableSettings { GamePath = "first" }; first.Values["speed"] = 1.5f; first.Save(path);
    first.GamePath = "second"; first.Values["speed"] = 200; first.Values["fog.stop"] = 1; first.Values["jump"] = float.NaN; first.Save(path);
    var actual = PortableSettings.Load(path);
    Assert(actual.GamePath == "second" && actual.Values.Count == 1 && actual.Values["speed"] == 200, "Atomic replacement lost state");
    Assert(!File.ReadAllText(path).Contains("enabled", StringComparison.OrdinalIgnoreCase) && Directory.GetFiles(root).Length == 1, "State contains enables or leftover temporary file");
});
Test("Malformed oversized and deep settings are rejected without modification", () =>
{
    string path = Path.Combine(Root("settings-invalid"), "settings.json");
    foreach (string value in new[] { "{", "[]", "{\"a\":\"" + new string('x', 256 * 1024) + "\"}", string.Concat(Enumerable.Repeat("{\"a\":", 10)) + "0" + new string('}', 10) })
    { File.WriteAllText(path, value); Rejected(() => PortableSettings.Load(path)); Assert(File.ReadAllText(path) == value, "Rejected file rewritten"); }
});
Test("Nonfinite numeric preferences cannot survive save and excess settings do not overwrite", () =>
{
    string path = Path.Combine(Root("settings-finite"), "settings.json");
    var state = new PortableSettings(); state.Values["speed"] = float.PositiveInfinity; state.Save(path);
    string before = File.ReadAllText(path); Assert(PortableSettings.Load(path).Values.Count == 0, "Infinity persisted");
    state.GamePath = new string('x', 256 * 1024 + 1); Rejected(() => state.Save(path)); Assert(File.ReadAllText(path) == before, "Oversized save overwrote prior file");
});

var sourceHashes = new Dictionary<string, string>();
foreach (string name in new[] { "SteamDiscovery.cs", "PortableEnvironment.cs" })
{
    string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Desktop", name));
    sourceHashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
File.WriteAllText(args[0], JsonSerializer.Serialize(new { total = reports.Count, passed = reports.Count - failed, failed, createdUtc = DateTime.UtcNow, method = "Production Steam discovery and portable state helpers against independent filesystem fixtures; no game operations", productionSourceHashes = sourceHashes, results = reports }, new JsonSerializerOptions { WriteIndented = true }));
return failed == 0 ? 0 : 1;
