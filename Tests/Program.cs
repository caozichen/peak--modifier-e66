using System.Text;
using System.Text.Json;
using PeakWorkshop.Plugin;
using PeakWorkshop.Protocol;

var tests = new List<(string Name, Action Body)>();
var jsonOptions = new JsonSerializerOptions { IncludeFields = true };

tests.Add(("All wire fields preserve their values in UTF-8 JSON round-trip", () =>
{
    var sent = new WireMessage
    {
        protocol = 1, kind = "state", requestId = "request-17", sessionId = "game-session-2",
        ok = false, message = "角色未就绪：攀爬场景", error = "character_not_ready",
        pluginVersion = "0.1.0", gameVersion = "PEAK / Unity 6000.3.15f1", scene = "中文场景",
        characterReady = true, speedAvailable = true, staminaAvailable = true,
        speedEnabled = true, speedMultiplier = 2.3f, staminaEnabled = true
    };
    byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(sent, jsonOptions);
    var received = JsonSerializer.Deserialize<WireMessage>(encoded, jsonOptions)!;
    foreach (var field in typeof(WireMessage).GetFields())
        Equal(field.GetValue(sent), field.GetValue(received), "wire field " + field.Name);
}));

tests.Add(("Expected plugin JSON field names deserialize correctly", () =>
{
    const string raw = """{"protocol":1,"kind":"state","requestId":"a","sessionId":"b","ok":true,"characterReady":true,"speedAvailable":true,"staminaAvailable":false,"speedEnabled":true,"speedMultiplier":1.7,"staminaEnabled":false,"message":"设置已应用"}""";
    var result = JsonSerializer.Deserialize<WireMessage>(raw, jsonOptions)!;
    Equal(true, result.characterReady, "characterReady");
    Equal(true, result.speedEnabled, "speedEnabled");
    Equal(false, result.staminaAvailable, "staminaAvailable");
    Equal(1.7f, result.speedMultiplier, "speedMultiplier");
    Equal("设置已应用", result.message, "message");
}));

tests.Add(("Wire defaults never enable a feature", () =>
{
    var result = new WireMessage();
    Equal(false, result.speedEnabled, "speed default");
    Equal(false, result.staminaEnabled, "stamina default");
    Equal(false, result.characterReady, "ready default");
    Equal(1f, result.speedMultiplier, "multiplier default");
    Equal(WireProtocol.Version, result.protocol, "protocol default");
}));

tests.Add(("UTF-8 message length is a byte count even for non-ASCII text", () =>
{
    var message = new WireMessage { message = "测试角色就绪" };
    var options = new JsonSerializerOptions(jsonOptions) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    string payload = JsonSerializer.Serialize(message, options);
    int byteCount = Encoding.UTF8.GetByteCount(payload);
    if (byteCount <= payload.Length) throw new Exception("Non-ASCII payload unexpectedly has no byte/character length difference.");
    if (byteCount >= WireProtocol.MaxMessageBytes) throw new Exception("Ordinary state does not fit protocol cap.");
}));

foreach (float multiplier in new[] { 0.5f, 1f, 1.7f, 3f, 200f })
{
    var m = multiplier;
    tests.Add(($"Allowed multiplier {m} applies only to the eligible local actor", () =>
    {
        var settings = new FeatureSettings(true, m, true);
        Equal(12f * m, settings.ApplyMovement(12f, true), "eligible movement");
        Equal(12f, settings.ApplyMovement(12f, false), "other actor movement");
        Equal(false, settings.ApplyStamina(false, false), "other actor stamina");
    }));
}

foreach (float multiplier in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -1f, 0.4999f, 200.0001f, float.MaxValue })
{
    var m = multiplier;
    tests.Add(($"Invalid multiplier {m} is rejected before constructing feature state", () =>
    {
        try { _ = new FeatureSettings(true, m, true); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new Exception("Expected ArgumentOutOfRangeException.");
    }));
}

tests.Add(("Disabled features preserve the game's result for every eligibility flag", () =>
{
    foreach (bool eligible in new[] { false, true })
    {
        foreach (float original in new[] { -4f, 0f, 12f })
            Equal(original, FeatureSettings.Disabled.ApplyMovement(original, eligible), "movement");
        foreach (bool original in new[] { false, true })
            Equal(original, FeatureSettings.Disabled.ApplyStamina(original, eligible), "stamina");
    }
}));

tests.Add(("Fresh game calculations do not accumulate the multiplier", () =>
{
    var enabled = new FeatureSettings(true, 2f, false);
    for (int frame = 0; frame < 5000; frame++)
        Equal(24f, enabled.ApplyMovement(12f, true), "fresh frame");
    Equal(12f, FeatureSettings.Disabled.ApplyMovement(12f, true), "restored frame");
}));

tests.Add(("Stamina toggles preserve an original true value from the game", () =>
{
    foreach (bool enabled in new[] { false, true })
    foreach (bool eligible in new[] { false, true })
        Equal(true, new FeatureSettings(false, 1f, enabled).ApplyStamina(true, eligible), "existing flag");
    Equal(true, new FeatureSettings(false, 1f, true).ApplyStamina(false, true), "enabled local");
    Equal(false, FeatureSettings.Disabled.ApplyStamina(false, true), "restored local");
}));

tests.Add(("Changing one feature never implicitly enables the other feature", () =>
{
    var movementOnly = new FeatureSettings(true, 2f, false);
    Equal(false, movementOnly.ApplyStamina(false, true), "movement-only stamina");
    var staminaOnly = new FeatureSettings(false, 2f, true);
    Equal(12f, staminaOnly.ApplyMovement(12f, true), "stamina-only movement");
}));

tests.AddRange(FeatureTests.Create());
tests.AddRange(WireJsonTests.Create());
int failed = 0;
var results = new List<object>();
foreach (var test in tests)
{
    try { test.Body(); Console.WriteLine("PASS " + test.Name); results.Add(new { name = test.Name, passed = true }); }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine("FAIL " + test.Name + ": " + exception.Message);
        results.Add(new { name = test.Name, passed = false, error = exception.ToString() });
    }
}
var pipeTests = PipeClientTests.Create();
pipeTests.AddRange(PipeServerTests.Create());
pipeTests.AddRange(SecurePipeTests.Create());
foreach (var test in pipeTests)
{
    try { await test.Body(); Console.WriteLine("PASS " + test.Name); results.Add(new { name = test.Name, passed = true }); }
    catch (Exception exception)
    {
        failed++;
        Console.WriteLine("FAIL " + test.Name + ": " + exception.Message);
        results.Add(new { name = test.Name, passed = false, error = exception.ToString() });
    }
}
int total = tests.Count + pipeTests.Count;
Console.WriteLine($"RESULT {total - failed}/{total} passed");
if (args.Length == 1)
{
    string reportPath = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new { total, passed = total - failed, failed, createdUtc = DateTime.UtcNow, resources=PipeServerTests.Resources, results }, new JsonSerializerOptions { WriteIndented = true }));
}
return failed == 0 ? 0 : 1;

static void Equal(object? expected, object? actual, string label)
{
    if (!Equals(expected, actual)) throw new Exception($"{label}: expected {expected}, actual {actual}");
}
