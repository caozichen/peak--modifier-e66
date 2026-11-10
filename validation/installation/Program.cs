using System.Diagnostics;
using System.Text.Json;
using PeakWorkshop.Desktop;

var tests = new List<(string Name, Action Body)>();
string runs = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runs);
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Reject(Action action) { try { action(); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; } throw new Exception("Unsafe action was accepted"); }
Fixture Make() => new(runs);
string Read(string file) => File.ReadAllText(file);
void MutateManifest(Fixture f, Action<InstallationService.PayloadManifest> edit)
{
    var manifest = JsonSerializer.Deserialize<InstallationService.PayloadManifest>(Read(f.Manifest))!;
    edit(manifest); File.WriteAllText(f.Manifest, JsonSerializer.Serialize(manifest));
}
void CopyLoader(Fixture f)
{
    foreach (var path in f.Policy.Files.Keys.Where(p => p != InstallationService.PluginPath))
    { string target = f.Target(path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(f.Payload(path), target); }
}
void WriteJunction(string path, string target)
{
    var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in new[] { "-NoProfile", "-Command", "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null" }) info.ArgumentList.Add(arg);
    using var process = Process.Start(info)!; string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd(); process.WaitForExit();
    Assert(process.ExitCode == 0, "Junction fixture failed: " + output);
}

tests.Add(("Production policy pins the exact 23 payload files and keeps analysis baseline as metadata", () => {
    Assert(InstallationPolicy.Supported.Files.Count == 23 && InstallationPolicy.Supported.GameSha256 == InstallationPolicy.SupportedGameHash && InstallationPolicy.Supported.PluginVersion == "0.2.6", "Production policy changed");
    string plugin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Plugin/bin/Release/netstandard2.1/PeakWorkshop.Plugin.dll"));
    string actual = InstallationService.Hash(plugin);
    Assert(InstallationPolicy.Supported.Files[InstallationService.PluginPath] == actual, "Plugin integrity hash does not match this checkout's Release build; run scripts/Build.ps1 first");
}));

tests.Add(("A different game assembly can be inspected, installed, used and uninstalled", () => {
    var f=Make();File.AppendAllText(f.Assembly,"new game version");
    Assert(f.Engine.Inspect(f.Game).CanInstall,"Different game version blocked installation");
    f.Engine.Install(f.Game);var state=f.Engine.Inspect(f.Game);
    Assert(state.Installed && state.CanUninstall,"Different game version blocked installed status");
    f.Engine.Uninstall(f.Game);Assert(!File.Exists(f.Target(InstallationService.PluginPath)),"Different game version blocked uninstall");
}));
tests.Add(("Inspect checks write access and leaves no persistent changes in a fresh supported game", () => {
    var f=Make(); var state=f.Engine.Inspect(f.Game); Assert(state.CanInstall && !state.CanUninstall && !state.Installed,"Fresh state wrong");
    Assert(!Directory.Exists(Path.Combine(f.Root,"backups")) && !Directory.Exists(f.Target("BepInEx")),"Inspect wrote files");
}));
tests.Add(("Blocked subdirectory is detected before installation and write probes leave no files",()=>{
    var f=Make();File.WriteAllText(f.Target("BepInEx"),"foreign ordinary file");var status=f.Engine.Inspect(f.Game);Assert(!status.CanInstall,"Blocked path offered installation");Assert(Read(f.Target("BepInEx"))=="foreign ordinary file","Blocked file changed");Assert(!Directory.GetFiles(f.Game,".peakworkshop-write-test-*",SearchOption.TopDirectoryOnly).Any(),"Write probe leaked");
}));
tests.Add(("Managed plugin can be removed after another loader binary changes",()=>{
    var f=Make();CopyLoader(f);f.Engine.Install(f.Game);File.AppendAllText(f.Target("winhttp.dll"),"changed foreign loader");string before=InstallationService.Hash(f.Target("winhttp.dll"));var status=f.Engine.Inspect(f.Game);Assert(!status.CanInstall&&status.CanUninstall,"Changed loader trapped managed plugin");f.Engine.Uninstall(f.Game);Assert(!File.Exists(f.Target(InstallationService.PluginPath))&&InstallationService.Hash(f.Target("winhttp.dll"))==before,"Uninstall changed unrelated loader");
}));
tests.Add(("Fresh install validates all 23 bytes and uninstall removes only owned unchanged files", () => {
    var f=Make();f.Engine.Install(f.Game);var state=f.Engine.Inspect(f.Game);Assert(state.Installed&&!state.CanInstall&&state.CanUninstall,"Install state wrong");
    foreach(var file in f.Policy.Files)Assert(InstallationService.Hash(f.Target(file.Key))==file.Value,"Installed hash differs");
    File.WriteAllText(f.Target("user-save.txt"),"retain");Directory.CreateDirectory(f.Target("BepInEx/config"));File.WriteAllText(f.Target("BepInEx/config/custom.cfg"),"custom");
    f.Engine.Uninstall(f.Game);foreach(string file in f.Policy.Files.Keys)Assert(!File.Exists(f.Target(file)),"Owned file retained");
    Assert(Read(f.Target("user-save.txt"))=="retain"&&Read(f.Target("BepInEx/config/custom.cfg"))=="custom","User data removed");
}));
tests.Add(("Second install is idempotent and repeated uninstall is rejected", () => {
    var f=Make();f.Engine.Install(f.Game);string manifest=f.FindRecord();string before=Read(manifest);f.Engine.Install(f.Game);Assert(Read(manifest)==before,"Repeated install rewrote ownership");f.Engine.Uninstall(f.Game);Reject(()=>f.Engine.Uninstall(f.Game));
}));
tests.Add(("Fresh reinstall after uninstall tolerates retained generated folders and configs", () => {
    var f=Make();f.Engine.Install(f.Game);Directory.CreateDirectory(f.Target("BepInEx/config"));File.WriteAllText(f.Target("BepInEx/config/user.cfg"),"keep");f.Engine.Uninstall(f.Game);Assert(f.Engine.Inspect(f.Game).CanInstall,"Retained BepInEx directory blocked reinstall");f.Engine.Install(f.Game);Assert(f.Engine.Inspect(f.Game).Installed&&Read(f.Target("BepInEx/config/user.cfg"))=="keep","Reinstall failed or erased generated config");
}));
tests.Add(("Compatible existing loader and custom configuration are preserved", () => {
    var f=Make();CopyLoader(f);File.AppendAllText(f.Target("doorstop_config.ini"),"\n# user comment\n");string before=Read(f.Target("doorstop_config.ini"));f.Engine.Install(f.Game);
    Assert(f.Engine.Inspect(f.Game).CanUninstall,"Own plugin not managed");var record=JsonSerializer.Deserialize<InstallationService.InstallationRecord>(Read(f.FindRecord()))!;Assert(record.Files.Count==1&&record.Files[0].Path==InstallationService.PluginPath,"Claimed preexisting loader");
    f.Engine.Uninstall(f.Game);Assert(Read(f.Target("doorstop_config.ini"))==before&&File.Exists(f.Target("winhttp.dll")),"Preexisting loader removed");
}));
tests.Add(("An exact preexisting plugin is recognized without taking ownership", () => {
    var f=Make();CopyLoader(f);Directory.CreateDirectory(Path.GetDirectoryName(f.Target(InstallationService.PluginPath))!);File.Copy(f.Payload(InstallationService.PluginPath),f.Target(InstallationService.PluginPath));
    var state=f.Engine.Inspect(f.Game);Assert(state.Installed&&!state.CanInstall&&!state.CanUninstall,"Unmanaged state wrong");f.Engine.Install(f.Game);Reject(()=>f.Engine.Uninstall(f.Game));Assert(File.Exists(f.Target(InstallationService.PluginPath)),"Unmanaged plugin removed");
}));
tests.Add(("Unknown or incomplete existing loaders fail before any game mutation", () => {
    foreach(bool incomplete in new[]{false,true}){var f=Make();if(incomplete)Directory.CreateDirectory(f.Target("BepInEx"));else{CopyLoader(f);File.AppendAllText(f.Target("winhttp.dll"),"foreign");}Reject(()=>f.Engine.Install(f.Game));Assert(!File.Exists(f.Target(InstallationService.PluginPath)),"Plugin deployed on incompatible loader");}
}));
tests.Add(("Disabled or redirected doorstop configurations are never overwritten", () => {
    foreach(string config in new[]{"[General]\nenabled=false\ntarget_assembly=BepInEx/core/BepInEx.Preloader.dll","[General]\nenabled=true\ntarget_assembly=other.dll","[General]\nenabled=true\nenabled=false\ntarget_assembly=BepInEx/core/BepInEx.Preloader.dll"}){var f=Make();CopyLoader(f);File.WriteAllText(f.Target("doorstop_config.ini"),config);Reject(()=>f.Engine.Install(f.Game));Assert(Read(f.Target("doorstop_config.ini"))==config,"Configuration overwritten");}
}));
tests.Add(("Unknown same-name plugin cannot be overwritten", () => {
    var f=Make();CopyLoader(f);Directory.CreateDirectory(Path.GetDirectoryName(f.Target(InstallationService.PluginPath))!);File.WriteAllText(f.Target(InstallationService.PluginPath),"foreign plugin");Reject(()=>f.Engine.Install(f.Game));Assert(Read(f.Target(InstallationService.PluginPath))=="foreign plugin","Foreign plugin overwritten");
}));
tests.Add(("Payload integrity still rejects edited manifests and binaries", () => {
    var f=Make();MutateManifest(f,m=>m.supportedAssemblySha256=new string('0',64));Reject(()=>f.Engine.Install(f.Game));
    f=Make();File.AppendAllText(f.Payload("winhttp.dll"),"modified");var x=f;MutateManifest(f,m=>m.files.Single(p=>p.path=="winhttp.dll").sha256=InstallationService.Hash(x.Payload("winhttp.dll")));Reject(()=>f.Engine.Install(f.Game));Assert(!File.Exists(f.Target("winhttp.dll")),"Tampered payload deployed");
}));
tests.Add(("Malformed manifests with traversal, absolute paths, duplicates or nulls fail closed", () => {
    foreach(string bad in new[]{"../outside.dll","C:/outside.dll","BepInEx//evil.dll","BepInEx\\evil.dll"}){var f=Make();MutateManifest(f,m=>m.files[0].path=bad);Reject(()=>f.Engine.Install(f.Game));}
    var duplicate=Make();MutateManifest(duplicate,m=>m.files[0]=m.files[1]);Reject(()=>duplicate.Engine.Install(duplicate.Game));
    var nulls=Make();File.WriteAllText(nulls.Manifest,"{\"schema\":1,\"files\":null}");Reject(()=>nulls.Engine.Install(nulls.Game));
}));
tests.Add(("Unexpected payload files are rejected", () => {var f=Make();File.WriteAllText(Path.Combine(f.Root,"data/game-files/extra.dll"),"extra");Reject(()=>f.Engine.Install(f.Game));}));
tests.Add(("Game must be closed before install and uninstall", () => {
    var f=Make();f.Running=true;Assert(!f.Engine.Inspect(f.Game).CanInstall,"Running game allows install");Reject(()=>f.Engine.Install(f.Game));f.Running=false;f.Engine.Install(f.Game);f.Running=true;Assert(!f.Engine.Inspect(f.Game).CanUninstall,"Running game allows uninstall");Reject(()=>f.Engine.Uninstall(f.Game));
}));
tests.Add(("Changed installed files prevent removal before any file is deleted", () => {
    var f=Make();f.Engine.Install(f.Game);File.AppendAllText(f.Target("winhttp.dll"),"third party change");Reject(()=>f.Engine.Uninstall(f.Game));Assert(File.Exists(f.Target(InstallationService.PluginPath))&&f.Policy.Files.Keys.All(p=>File.Exists(f.Target(p))),"Partial uninstall occurred");
}));
tests.Add(("Foreign plugins retain shared loader while own plugin is removed", () => {
    var f=Make();f.Engine.Install(f.Game);File.WriteAllText(f.Target("BepInEx/plugins/OtherMod.dll"),"foreign");f.Engine.Uninstall(f.Game);Assert(!File.Exists(f.Target(InstallationService.PluginPath))&&File.Exists(f.Target("winhttp.dll"))&&Read(f.Target("BepInEx/plugins/OtherMod.dll"))=="foreign","Shared or foreign files removed");
}));
tests.Add(("Malformed owned manifest cannot escape exact known whitelist", () => {
    var f=Make();f.Engine.Install(f.Game);string record=f.FindRecord();var value=JsonSerializer.Deserialize<InstallationService.InstallationRecord>(Read(record))!;value.Files[0].Path="../outside.dll";File.WriteAllText(record,JsonSerializer.Serialize(value));Reject(()=>f.Engine.Uninstall(f.Game));Assert(File.Exists(f.Target(InstallationService.PluginPath)),"Manifest traversal mutated game");
}));
tests.Add(("Install failure rolls back copied game files and manifest", () => {
    var f=Make();int count=0;f.Engine.TestMutationHook=_=>{if(++count==5)throw new IOException("fixture write failure");};Reject(()=>f.Engine.Install(f.Game));Assert(f.Policy.Files.Keys.All(p=>!File.Exists(f.Target(p))),"Rollback left installed files");Assert(!File.Exists(f.FindRecord()),"Rollback left active manifest");
}));
tests.Add(("Uninstall failure restores every removed byte and prior manifest", () => {
    var f=Make();f.Engine.Install(f.Game);string before=Read(f.FindRecord());int count=0;f.Engine.TestMutationHook=_=>{if(++count==5)throw new IOException("fixture delete failure");};Reject(()=>f.Engine.Uninstall(f.Game));Assert(Read(f.FindRecord())==before,"Manifest not restored");foreach(var file in f.Policy.Files)Assert(InstallationService.Hash(f.Target(file.Key))==file.Value,"Rollback did not restore bytes");
}));
tests.Add(("Game starting during mutation preserves journal and supports later recovery", () => {
    var f=Make();int count=0;f.Engine.TestMutationHook=_=>{if(++count==2)f.Running=true;};Reject(()=>f.Engine.Install(f.Game));Assert(Directory.GetFiles(Path.Combine(f.Root,"backups"),"pending.json",SearchOption.AllDirectories).Length==1,"Recovery journal lost");
    f.Running=false;f.Engine.TestMutationHook=null;Assert(f.Engine.Inspect(f.Game).InstallLabel=="恢复上次操作","Recovery action absent");f.Engine.Install(f.Game);Assert(f.Policy.Files.Keys.All(p=>!File.Exists(f.Target(p))),"Recovery did not restore clean game");Assert(f.Engine.Inspect(f.Game).CanInstall,"Recovered game cannot install");f.Engine.Install(f.Game);Assert(f.Engine.Inspect(f.Game).Installed,"Install after recovery failed");
}));
tests.Add(("Recovery refuses to overwrite a later third-party edit", () => {
    var f=Make();f.Engine.TestMutationHook=_=>f.Running=true;Reject(()=>f.Engine.Install(f.Game));f.Running=false;f.Engine.TestMutationHook=null;string first=f.Policy.Files.Keys.First();File.WriteAllText(f.Target(first),"later third party edit");Reject(()=>f.Engine.Install(f.Game));Assert(Read(f.Target(first))=="later third party edit","Recovery clobbered later edits");
}));
tests.Add(("Recovery removes only journal-tracked partial copy files after interrupted staging", () => {
    var f=Make();string? temporary=null;f.Engine.TestMutationHook=_=>{string pending=Directory.GetFiles(Path.Combine(f.Root,"backups"),"pending.json",SearchOption.AllDirectories).Single();var journal=JsonSerializer.Deserialize<InstallationService.Journal>(Read(pending))!;temporary=f.Target(journal.Changes[0].TemporaryPath);File.WriteAllText(temporary,"partial copy fixture");f.Running=true;};Reject(()=>f.Engine.Install(f.Game));Assert(temporary!=null&&File.Exists(temporary),"Interrupted staged copy absent");f.Running=false;f.Engine.TestMutationHook=null;f.Engine.Install(f.Game);Assert(!File.Exists(temporary)&&f.Policy.Files.Keys.All(p=>!File.Exists(f.Target(p))),"Tracked partial copy not recovered");
}));
tests.Add(("Managed plugin update backs up prior version and can uninstall current version", () => {
    var f=Make();f.Engine.Install(f.Game);string old=Read(f.Target(InstallationService.PluginPath));File.WriteAllText(f.Payload(InstallationService.PluginPath),"fixture newer plugin");var files=new Dictionary<string,string>(f.Policy.Files){[InstallationService.PluginPath]=InstallationService.Hash(f.Payload(InstallationService.PluginPath))};var policy=new InstallationPolicy{Files=files,GameSha256=f.Policy.GameSha256,PluginVersion="0.2.5"};
    MutateManifest(f,m=>{m.pluginVersion="0.2.5";m.files.Single(p=>p.path==InstallationService.PluginPath).sha256=files[InstallationService.PluginPath];});var newer=new InstallationService(f.Root,()=>false,policy);Assert(newer.Inspect(f.Game).CanInstall,"Managed update not available");newer.Install(f.Game);Assert(Read(f.Target(InstallationService.PluginPath))=="fixture newer plugin","Update absent");Assert(Directory.GetFiles(Path.Combine(f.Root,"backups"),"PeakWorkshop.Plugin.dll",SearchOption.AllDirectories).Any(p=>Read(p)==old),"Previous version backup missing");newer.Uninstall(f.Game);Assert(!File.Exists(f.Target(InstallationService.PluginPath)),"Updated plugin not removed");
}));
tests.Add(("After game update an owned plugin can still be safely uninstalled", () => {
    var f=Make();f.Engine.Install(f.Game);File.AppendAllText(f.Assembly,"game updated");var status=f.Engine.Inspect(f.Game);Assert(status.Installed&&status.CanUninstall,"Game update blocked an installed plugin");f.Engine.Uninstall(f.Game);Assert(!File.Exists(f.Target(InstallationService.PluginPath)),"Old plugin retained");
}));
tests.Add(("Moving portable folder preserves relative backup ownership records", () => {
    var f=Make();f.Engine.Install(f.Game);string moved=f.Root+"-moved";Directory.Move(f.Root,moved);var engine=new InstallationService(moved,()=>false,f.Policy);Assert(engine.Inspect(f.Game).CanUninstall,"Portable move broke records");engine.Uninstall(f.Game);Assert(!File.Exists(f.Target(InstallationService.PluginPath)),"Moved package cannot uninstall");
}));
tests.Add(("Concurrent operation lease rejects a second installer", () => {
    var f=Make();f.Engine.Install(f.Game);string store=Path.GetDirectoryName(f.FindRecord())!;using var lease=new FileStream(Path.Combine(store,"operation.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);Reject(()=>f.Engine.Uninstall(f.Game));Assert(File.Exists(f.Target(InstallationService.PluginPath)),"Concurrent operation mutated files");
}));
tests.Add(("Game root junction and payload junction are rejected", () => {
    var f=Make();string link=Path.Combine(Path.GetDirectoryName(f.Game)!,"game-link");WriteJunction(link,f.Game);Reject(()=>f.Engine.Install(link));
    f=Make();string nested=Path.Combine(f.Root,"data/game-files/link");WriteJunction(nested,f.Game);Reject(()=>f.Engine.Install(f.Game));
}));
tests.Add(("A junction in the game plugin path is rejected before install", () => {
    var f=Make();CopyLoader(f);string outside=Path.Combine(Path.GetDirectoryName(f.Game)!,"outside");Directory.CreateDirectory(outside);Directory.CreateDirectory(f.Target("BepInEx/plugins"));WriteJunction(f.Target("BepInEx/plugins/PeakWorkshop"),outside);Reject(()=>f.Engine.Install(f.Game));Assert(!File.Exists(Path.Combine(outside,"PeakWorkshop.Plugin.dll")),"Junction followed into foreign path");
}));
tests.Add(("Overlapping game and portable folders are rejected", () => {
    var f=Make();Reject(()=>f.Engine.Install(f.Root));Assert(!f.Engine.Inspect(f.Root).CanInstall,"Overlapping roots accepted");
}));

var results=new List<object>();int failed=0;
foreach(var test in tests){try{test.Body();Console.WriteLine("PASS "+test.Name);results.Add(new{name=test.Name,passed=true});}catch(Exception ex){failed++;Console.WriteLine("FAIL "+test.Name+": "+ex);results.Add(new{name=test.Name,passed=false,error=ex.ToString()});}}
string report=args.Length>0?args[0]:Path.Combine(AppContext.BaseDirectory,"installation-tests-030.json");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
File.WriteAllText(report,JsonSerializer.Serialize(new{total=tests.Count,passed=tests.Count-failed,failed,createdUtc=DateTime.UtcNow,method="Production installer with isolated fixture game/payload and pinned production policy checks; never writes real game",fixtureRoot=runs,results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"RESULT {tests.Count-failed}/{tests.Count}");return failed==0?0:1;

internal sealed class Fixture
{
    public readonly string Root,Game,Assembly,Manifest;
    public readonly InstallationPolicy Policy;
    public readonly InstallationService Engine;
    public bool Running;
    public Fixture(string runs)
    {
        string scope=Path.Combine(runs,Guid.NewGuid().ToString("N"));Root=Path.Combine(scope,"portable");Game=Path.Combine(scope,"game");Directory.CreateDirectory(Root);Directory.CreateDirectory(Game);
        Assembly=Target("PEAK_Data/Managed/Assembly-CSharp.dll");Directory.CreateDirectory(Path.GetDirectoryName(Assembly)!);File.WriteAllText(Assembly,"fixture supported game");File.WriteAllText(Target("PEAK.exe"),"fixture only");
        var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string path in InstallationPolicy.Supported.Files.Keys){string target=Payload(path);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllText(target,path=="doorstop_config.ini"?"[General]\nenabled = true\ntarget_assembly=BepInEx\\core\\BepInEx.Preloader.dll\n":"fixture payload: "+path);files[path]=InstallationService.Hash(target);}
        Policy=new InstallationPolicy{Files=files,GameSha256=InstallationService.Hash(Assembly)};Manifest=Path.Combine(Root,"data/payload.json");File.WriteAllText(Manifest,JsonSerializer.Serialize(new InstallationService.PayloadManifest{schema=1,pluginVersion=Policy.PluginVersion,supportedAssemblySha256=Policy.GameSha256,files=files.Select(f=>new InstallationService.PayloadFile{path=f.Key,sha256=f.Value}).ToList()}));Engine=new InstallationService(Root,()=>Running,Policy);
    }
    public string Target(string relative)=>Path.Combine(Game,relative.Replace('/',Path.DirectorySeparatorChar));
    public string Payload(string relative)=>Path.Combine(Root,"data/game-files",relative.Replace('/',Path.DirectorySeparatorChar));
    public string FindRecord(){string path=Path.Combine(Root,"backups");return Directory.Exists(path)?Directory.GetFiles(path,"installed-files.json",SearchOption.AllDirectories).FirstOrDefault()??Path.Combine(path,"absent.json"):Path.Combine(path,"absent.json");}
}
