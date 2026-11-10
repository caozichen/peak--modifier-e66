using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using PeakWorkshop.Plugin;
using PeakWorkshop.Protocol;

internal static class WireJsonTests
{
    private static readonly JsonSerializerOptions DesktopJson = new() { IncludeFields = true };

    public static List<(string Name, Action Body)> Create() => new()
    {
        ("Production JSON serializes all 51 nested feature values for the real desktop decoder", SnapshotToDesktop),
        ("Production JSON parses the desktop's complete set request without losing settings or identifiers", DesktopSetToPlugin),
        ("Production JSON state requests preserve safe defaults and never enable a feature", SafeQueryDefaults),
        ("Production JSON rejects duplicate root fields before controller dispatch", () => RejectJson("{\"protocol\":2,\"protocol\":2,\"kind\":\"state\"}")),
        ("Production JSON rejects duplicate nested feature fields before settings construction", DuplicateNestedField),
        ("Production JSON keeps full-state settings validation for missing, duplicate and unknown feature IDs", InvalidFullStates),
        ("Production JSON rejects excessive nesting and non-object root values", DepthAndRoot),
        ("Production JSON rejects extra documents while accepting trailing whitespace", AdditionalContent),
        ("Production JSON bounds input by UTF-8 bytes and bounds serialized output", LengthLimits),
        ("Production JSON rejects quoted numbers, quoted booleans and fractional revisions", InvalidTokenTypes),
        ("Production JSON rejects case aliases that could bypass field and duplicate validation", CaseAliases),
        ("Production JSON and settings reject nonfinite or overflowing feature parameters", NonfiniteValues),
        ("Production JSON never resolves a client-supplied runtime type name", RuntimeTypeMetadata)
    };

    private static void SnapshotToDesktop()
    {
        var values = FeatureSettings.Defaults();
        for (int i = 0; i < values.Length; i++)
        {
            values[i].available = i % 2 == 0;
            values[i].reason = "验证中文\"引号\"、反斜线\\和换行\n" + i;
        }
        var original = new WireMessage
        {
            protocol = WireProtocol.Version, kind = "state", requestId = "snapshot-request",
            sessionId = "snapshot-session", controllerId = "window-session", stateRevision = 42, pluginVersion = "codec-test",
            gameVersion = "PEAK", scene = "机场", ok = true, detachAvailable = true,
            speedAvailable = true, staminaAvailable = true, features = values
        };
        string encoded = WireJson.Serialize(original);
        Assert(Encoding.UTF8.GetByteCount(encoded) < WireProtocol.MaxMessageBytes, "Full reply exceeds framing limit.");
        var desktop = System.Text.Json.JsonSerializer.Deserialize<WireMessage>(encoded, DesktopJson)!;
        Assert(desktop.features?.Length == FeatureCatalog.All.Length, "Production serializer dropped the nested array.");
        Assert(desktop.requestId == original.requestId && desktop.sessionId == original.sessionId && desktop.controllerId == original.controllerId && desktop.stateRevision == 42,
            "Production serializer lost request/session/revision identity.");
        Assert(desktop.ok && desktop.detachAvailable && !desktop.pluginStopped, "Scalar lifecycle flags changed.");
        for (int i = 0; i < values.Length; i++)
        {
            var actual = desktop.features![i];
            var expected = values[i];
            Assert(actual.id == expected.id && actual.enabled == expected.enabled && actual.value == expected.value &&
                actual.available == expected.available && actual.reason == expected.reason, "Feature was lost or changed: " + expected.id);
        }
        Assert(!new FeatureSettings(desktop.features!).AnyEnabled, "Serializing a disabled snapshot enabled a feature.");
        var parsedAgain = WireJson.Deserialize(encoded);
        Assert(parsedAgain.features.Length == 51 && !new FeatureSettings(parsedAgain.features).AnyEnabled,
            "Production parser cannot read its own complete snapshot.");
    }

    private static void DesktopSetToPlugin()
    {
        var request = SetRequest();
        request.controllerId = "window-session";
        request.features[0].enabled = true;
        request.features[0].value = 1.5f;
        foreach (var value in request.features) { value.available = true; value.reason = "forged client capability"; }
        var decoded = WireJson.Deserialize(System.Text.Json.JsonSerializer.Serialize(request, DesktopJson));
        Assert(decoded.kind == "set" && decoded.protocol == WireProtocol.Version && decoded.requestId == request.requestId &&
            decoded.sessionId == request.sessionId && decoded.controllerId == request.controllerId && decoded.stateRevision == request.stateRevision, "Command identity changed.");
        var settings = new FeatureSettings(decoded.features);
        Assert(settings.SpeedEnabled && settings.SpeedMultiplier == 1.5f, "Enabled speed was lost on inbound deserialization.");
        Assert(settings.ToWire().Count(value => value.enabled) == 1, "Inbound deserialization enabled other features.");
        Assert(settings.ToWire().All(value => !value.available && value.reason == ""), "Forged client capabilities reached settings.");
    }

    private static void SafeQueryDefaults()
    {
        var query = WireJson.Deserialize("{\"protocol\":2,\"kind\":\"state\",\"requestId\":\"query\"}");
        Assert(!query.speedEnabled && !query.staminaEnabled && !query.characterReady && !query.pluginStopped,
            "Minimal query changed a safety default.");
        Assert(query.speedMultiplier == 1f && query.features == null, "Missing fields should preserve disabled defaults.");
        Assert(query.requestId == "query" && query.kind == "state", "Query identity was lost.");
    }

    private static void DuplicateNestedField()
    {
        string valid = System.Text.Json.JsonSerializer.Serialize(SetRequest(), DesktopJson);
        const string marker = "\"id\":\"speed\"";
        Assert(valid.Contains(marker), "Fixture did not contain speed ID.");
        RejectJson(valid.Replace(marker, marker + ",\"id\":\"stamina\""));
        RejectJson("{\"features\":[{\"id\":\"speed\",\"enabled\":false,\"enabled\":true,\"value\":1}]}");
    }

    private static void InvalidFullStates()
    {
        foreach (string raw in new[] { "{\"kind\":\"set\"}", "{\"kind\":\"set\",\"features\":null}", "{\"kind\":\"set\",\"features\":[]}" })
            RejectSettings(WireJson.Deserialize(raw).features);
        var duplicate = SetRequest(); duplicate.features[0].id = duplicate.features[1].id;
        RejectSettings(WireJson.Deserialize(System.Text.Json.JsonSerializer.Serialize(duplicate, DesktopJson)).features);
        var unknown = SetRequest(); unknown.features[0].id = "unknown-control";
        RejectSettings(WireJson.Deserialize(System.Text.Json.JsonSerializer.Serialize(unknown, DesktopJson)).features);
        var missing = SetRequest(); missing.features[0] = null!;
        RejectSettings(WireJson.Deserialize(System.Text.Json.JsonSerializer.Serialize(missing, DesktopJson)).features);
    }

    private static void DepthAndRoot()
    {
        RejectJson("{\"unknown\":" + new string('[', 12) + "0" + new string(']', 12) + "}");
        foreach (string raw in new[] { "null", "[]", "17", "true", "\"state\"" }) RejectJson(raw);
    }

    private static void AdditionalContent()
    {
        RejectJson("{\"kind\":\"state\"} {\"kind\":\"detach\"}");
        RejectJson("{\"kind\":\"state\"} true");
        var valid = WireJson.Deserialize("{\"kind\":\"state\"} \r\n\t");
        Assert(valid.kind == "state", "Trailing JSON whitespace was rejected.");
    }

    private static void LengthLimits()
    {
        RejectJson("{\"message\":\"" + new string('x', WireProtocol.MaxMessageBytes) + "\"}");
        string multibyte = "{\"message\":\"" + new string('汉', WireProtocol.MaxMessageBytes / 3 + 20) + "\"}";
        Assert(multibyte.Length < WireProtocol.MaxMessageBytes && Encoding.UTF8.GetByteCount(multibyte) > WireProtocol.MaxMessageBytes,
            "Multibyte length fixture does not distinguish bytes from characters.");
        RejectJson(multibyte);
        Reject(() => WireJson.Serialize(new WireMessage { message = new string('x', WireProtocol.MaxMessageBytes) }),
            "Oversized serialized response was accepted.");
    }

    private static void InvalidTokenTypes()
    {
        RejectJson("{\"stateRevision\":1.5}");
        RejectJson("{\"speedEnabled\":\"true\"}");
        RejectJson("{\"speedMultiplier\":\"1.5\"}");
        RejectJson("{\"features\":[{\"id\":\"speed\",\"enabled\":\"true\",\"value\":1}]}");
        RejectJson("{\"features\":[{\"id\":\"speed\",\"enabled\":false,\"value\":\"1\"}]}");
    }

    private static void NonfiniteValues()
    {
        string valid = System.Text.Json.JsonSerializer.Serialize(SetRequest(), DesktopJson);
        const string marker = "\"id\":\"speed\",\"enabled\":false,\"value\":1";
        Assert(valid.Contains(marker), "Nonfinite fixture does not contain expected speed value.");
        foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "1e100" })
        {
            string malformed = valid.Replace(marker, "\"id\":\"speed\",\"enabled\":false,\"value\":" + invalid);
            // Either the production parser or validated settings must reject it
            // before a modifying request can reach GameAdapter.Apply.
            Reject(() => new FeatureSettings(WireJson.Deserialize(malformed).features), "Nonfinite parameter accepted: " + invalid);
        }
    }

    private static void CaseAliases()
    {
        foreach (string raw in new[]
        {
            "{\"StateRevision\":\"17\"}", "{\"Features\":[]}",
            "{\"features\":[],\"Features\":[]}",
            "{\"features\":[{\"id\":\"speed\",\"Enabled\":\"true\"}]}",
            "{\"features\":[{\"id\":\"speed\",\"Id\":\"stamina\"}]}"
        }) RejectJson(raw);
    }

    private static void RuntimeTypeMetadata()
    {
        var result = WireJson.Deserialize("{\"$type\":\"Does.Not.Exist, Missing.Assembly\",\"kind\":\"state\",\"requestId\":\"safe\"}");
        Assert(result.GetType() == typeof(WireMessage) && result.requestId == "safe" && !result.speedEnabled,
            "Client metadata selected a runtime type or changed safety defaults.");
    }

    private static WireMessage SetRequest() => new()
    {
        protocol = WireProtocol.Version, kind = "set", requestId = "desktop-request", sessionId = "live-session",
        stateRevision = 17, features = FeatureSettings.Defaults()
    };

    private static void RejectJson(string value) => Reject(() => WireJson.Deserialize(value), "Malformed JSON request was accepted.");
    private static void RejectSettings(FeatureValue[] values) => Reject(() => new FeatureSettings(values), "Invalid full state reached settings.");
    private static void Reject(Action action, string failure)
    {
        try { action(); }
        catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is OverflowException || ex is IOException) { return; }
        throw new Exception(failure);
    }
    private static void Assert(bool condition, string detail) { if (!condition) throw new Exception(detail); }
}
