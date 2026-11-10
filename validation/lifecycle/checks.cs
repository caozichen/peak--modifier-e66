using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PeakWorkshop.Plugin;
using UnityEngine.SceneManagement;

internal static class Program
{
    private const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly List<object> Results = new();
    private static int _failed;

    private static int Main(string[] args)
    {
        string resultPath = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Result path required");
        Check("Airport permits bound living local character before run statistics initialize", () =>
        {
            var (adapter, actor, _) = Fixture();
            Require(adapter.Eligible(actor), "Airport actor rejected");
            Require(!actor.StatisticsInitialized, "Test must not initialize statistics");
            Require(!adapter.Settings.AnyEnabled, "Readiness must not enable features");
        });
        Check("Normal gameplay continues to require statistics initialization", () =>
        {
            var (adapter, actor, _) = Fixture();
            SceneManager.ActiveSceneName = "Level_0";
            Reject(adapter, actor);
            actor.StatisticsInitialized = true;
            Require(adapter.Eligible(actor), "Initialized gameplay actor rejected");
        });
        Check("Airport exception is exact and does not cover unrelated scenes", () =>
        {
            var (adapter, actor, _) = Fixture();
            foreach (string scene in new[] { "MainMenu", "Loading", "airport", "AirportPreview", "", null })
            {
                SceneManager.ActiveSceneName = scene;
                Reject(adapter, actor);
            }
        });
        Check("Missing reference initialization rejects both airport and initialized gameplay", () =>
        {
            var (adapter, actor, _) = Fixture();
            actor._refsInitialized = false;
            Reject(adapter, actor);
            actor.StatisticsInitialized = true;
            SceneManager.ActiveSceneName = "Level_0";
            Reject(adapter, actor);
        });
        Check("Non-local actor is rejected", () =>
        {
            var (adapter, actor, _) = Fixture();
            actor.AllowLocal = false;
            Reject(adapter, actor);
        });
        Check("Stale actor reference is rejected", () =>
        {
            var (adapter, _, _) = Fixture();
            var stale = new TestCharacter { StatisticsInitialized = true };
            Reject(adapter, stale);
        });
        Check("Local ownership change before next poll rejects previous character", () =>
        {
            var (adapter, actor, _) = Fixture();
            TestCharacter.localCharacter = new TestCharacter();
            Reject(adapter, actor);
        });
        Check("Null and Unity-destroyed characters are rejected", () =>
        {
            var (adapter, actor, _) = Fixture();
            Reject(adapter, null);
            actor.Destroyed = true;
            Reject(adapter, actor);
        });
        Check("Missing and Unity-destroyed character data are rejected", () =>
        {
            var (adapter, actor, _) = Fixture();
            actor.data = null;
            Reject(adapter, actor);
            actor.data = new TestCharacterData { Destroyed = true };
            Reject(adapter, actor);
        });
        Check("Death remains a readiness barrier", () =>
        {
            var (adapter, actor, _) = Fixture();
            actor.data.IsDead = true;
            Reject(adapter, actor);
        });
        Check("Passed-out state remains a readiness barrier", () =>
        {
            var (adapter, actor, _) = Fixture();
            actor.data.passedOut = true;
            Reject(adapter, actor);
        });
        Check("Fully-passed-out state remains a readiness barrier", () =>
        {
            var (adapter, actor, _) = Fixture();
            actor.data.fullyPassedOut = true;
            Reject(adapter, actor);
        });
        Check("Reflection getter exceptions fail closed with a diagnostic", () =>
        {
            var (adapter, actor, _) = Fixture();
            actor.ThrowLocal = true;
            Reject(adapter, actor);
            actor.ThrowLocal = false;
            actor.ThrowInitialized = true;
            Reject(adapter, actor);
            actor.ThrowInitialized = false;
            actor.data.ThrowDead = true;
            Reject(adapter, actor);
        });
        Check("Missing lifecycle reflection binding fails closed", () =>
        {
            var (adapter, actor, _) = Fixture();
            Set(adapter, "_refsInitialized", null);
            Reject(adapter, actor);
        });
        Check("PollCharacter initially waits without a character and does not repeat logs", () =>
        {
            var (adapter, _, logs) = Fixture(false);
            Require(!adapter.Ready, "Adapter must start unavailable");
            Require(ReadinessMessage(adapter).Length > 0, "Initial waiting diagnostic missing");
            TestCharacter.localCharacter = null;
            Require(!adapter.PollCharacter(), "No-character initial poll changed identity");
            Require(!adapter.Ready && adapter.Current == null, "Missing actor became ready");
            int baseline = logs.Count;
            adapter.PollCharacter();
            adapter.PollCharacter();
            Require(logs.Count == baseline, "Unchanged waiting reason floods logs");
        });
        Check("Polling refreshes readiness and diagnostics on death and recovery", () =>
        {
            var (adapter, actor, logs) = Fixture(false);
            Require(adapter.PollCharacter(), "New local actor must change identity");
            Require(adapter.Ready && ReferenceEquals(adapter.Current, actor), "Poll did not publish ready actor");
            int readyLogs = logs.Count;
            adapter.PollCharacter();
            Require(logs.Count == readyLogs, "Repeated ready state floods logs");
            actor.data.IsDead = true;
            Require(!adapter.PollCharacter(), "Death must not fabricate identity change");
            Require(!adapter.Ready && ReadinessMessage(adapter).Length > 0, "Death diagnostic unavailable");
            Require(logs.Count > readyLogs, "Death transition not logged");
            int deadLogs = logs.Count;
            adapter.PollCharacter();
            Require(logs.Count == deadLogs, "Repeated death state floods logs");
            actor.data.IsDead = false;
            adapter.PollCharacter();
            Require(adapter.Ready, "Recovery did not restore readiness");
            Require(logs.Count > deadLogs, "Recovery transition not logged");
        });
        Check("Polling restores previous actor when replaced or destroyed", () =>
        {
            var (adapter, first, _) = Fixture(false);
            adapter.PollCharacter();
            var second = new TestCharacter();
            TestCharacter.localCharacter = second;
            Require(adapter.PollCharacter(), "Actor replacement not detected");
            Require(adapter.Ready && ReferenceEquals(adapter.Current, second), "Replacement actor not published");
            Require(adapter.RestoredCharacters.Any(c => ReferenceEquals(c, first)), "Previous actor restoration skipped");
            second.Destroyed = true;
            Require(adapter.PollCharacter(), "Destroyed actor removal not detected");
            Require(!adapter.Ready && adapter.Current == null, "Destroyed actor retained as ready");
            Require(adapter.RestoredCharacters.Any(c => ReferenceEquals(c, second)), "Destroyed actor cleanup path skipped");
        });
        Check("Current active scene immediately governs airport exception", () =>
        {
            var (adapter, actor, _) = Fixture(false);
            adapter.PollCharacter();
            Require(adapter.Ready, "Airport setup failed");
            SceneManager.ActiveSceneName = "Level_0";
            Require(!adapter.Eligible(actor), "Cached airport status leaked across scene transition");
            adapter.PollCharacter();
            Require(!adapter.Ready, "Poll retained airport readiness outside airport");
            actor.StatisticsInitialized = true;
            adapter.PollCharacter();
            Require(adapter.Ready, "Gameplay initialization did not allow readiness");
        });

        AdapterRegression.Run();
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Plugin/GameAdapter.cs"));
        var report = new
        {
            total = Results.Count, passed = Results.Count - _failed, failed = _failed,
            createdUtc = DateTime.UtcNow,
            productionSource = source,
            productionSourceSha256 = File.Exists(source) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) : "",
            scope = "Linked production GameAdapter.cs readiness and PollCharacter. Unity lifetime, game character members, active scene and unrelated feature partials are substitutes; no actual Unity/game methods or Harmony patches execute.",
            gameExecuted = false, patchesInstalled = false, results = Results
        };
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
        File.WriteAllText(resultPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT {Results.Count - _failed}/{Results.Count}; {resultPath}");
        return _failed == 0 ? 0 : 1;
    }

    internal static (GameAdapter Adapter, TestCharacter Actor, List<string> Logs) Fixture(bool bindCurrent = true)
    {
        var logs = new List<string>();
        var adapter = new GameAdapter(logs.Add);
        var actor = new TestCharacter();
        TestCharacter.localCharacter = actor;
        SceneManager.ActiveSceneName = "Airport";
        Set(adapter, "_localCharacter", typeof(TestCharacter).GetField("localCharacter", Fields));
        Set(adapter, "_characterData", typeof(TestCharacter).GetField("data", Fields));
        Set(adapter, "_isLocal", typeof(TestCharacter).GetProperty("IsLocal", Fields));
        Set(adapter, "_isInitialized", typeof(TestCharacter).GetProperty("IsInitialized", Fields));
        Set(adapter, "_refsInitialized", typeof(TestCharacter).GetField("_refsInitialized", Fields));
        Set(adapter, "_dead", typeof(TestCharacterData).GetProperty("dead", Fields));
        Set(adapter, "_passedOut", typeof(TestCharacterData).GetField("passedOut", Fields));
        Set(adapter, "_fullyPassedOut", typeof(TestCharacterData).GetField("fullyPassedOut", Fields));
        foreach(var definition in PeakWorkshop.Protocol.FeatureCatalog.All)adapter.TestAvailable(definition.Id,true);
        if (bindCurrent){Set(adapter,"_current",actor);adapter.PollCharacter();adapter.RefreshEffects(true);}
        return (adapter, actor, logs);
    }

    internal static void Set(GameAdapter adapter, string name, object value)
    {
        var field = typeof(GameAdapter).GetField(name, Fields) ?? throw new InvalidOperationException("Production field missing: " + name);
        field.SetValue(adapter, value);
    }

    private static string ReadinessMessage(GameAdapter adapter) =>
        (string)(typeof(GameAdapter).GetProperty("ReadinessMessage", Fields)?.GetValue(adapter)
            ?? throw new InvalidOperationException("Production ReadinessMessage missing"));

    private static void Reject(GameAdapter adapter, object actor)
    {
        Require(!adapter.Eligible(actor), "Unsafe readiness condition accepted");
        var method = typeof(GameAdapter).GetMethod("CharacterBlockReason", Fields)
            ?? throw new InvalidOperationException("Production CharacterBlockReason missing");
        string failure = (string)method.Invoke(adapter, new[] { actor });
        Require(!string.IsNullOrWhiteSpace(failure), "Rejected state has no diagnostic");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Check(string name, Action action)
    {
        try { action(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Results.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL " + name + ": " + error); }
    }
}
