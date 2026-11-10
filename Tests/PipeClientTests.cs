using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PeakWorkshop.Desktop;
using PeakWorkshop.Protocol;

internal static class PipeClientTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    private static readonly int TestPid = Environment.ProcessId;

    public static List<(string Name, Func<Task> Body)> Create() => new()
    {
        ("Pipe round-trips all 51 features at raised caps and resets through fragmented frames", StateSetResetAsync),
        ("Pipe rejects a response for a different request", () => RejectResponseAsync(r => r.requestId = "different-request", "当前请求不匹配")),
        ("Pipe rejects an incompatible protocol", () => RejectResponseAsync(r => r.protocol = 99, "协议版本不匹配")),
        ("Pipe rejects a missing feature array", () => RejectResponseAsync(r => r.features = null!, "功能状态不完整")),
        ("Pipe rejects duplicate feature IDs", () => RejectResponseAsync(r => r.features[0].id = r.features[1].id, "功能状态重复")),
        ("Pipe rejects a feature parameter above the raised movement cap", () => RejectResponseAsync(r => r.features[0].value = MathF.BitIncrement(FeatureCatalog.Find("speed")!.Max), "功能状态参数无效")),
        ("Pipe rejects a gesture parameter above its raised cap", () => RejectResponseAsync(r => Array.Find(r.features,v=>v.id=="ping.size")!.value = MathF.BitIncrement(FeatureCatalog.Find("ping.size")!.Max), "功能状态参数无效")),
        ("Pipe rejects the legacy speed scalar above its raised cap", () => RejectResponseAsync(r => r.speedMultiplier = MathF.BitIncrement(FeatureCatalog.Find("speed")!.Max), "插件响应状态无效")),
        ("Pipe rejects an overflowing feature number without retaining a connection", () => RejectOverflowingNumberAsync(false)),
        ("Pipe rejects an overflowing legacy speed number without retaining a connection", () => RejectOverflowingNumberAsync(true)),
        ("Pipe rejects oversized response lengths", () => RejectLengthAsync(WireProtocol.MaxMessageBytes + 1)),
        ("Pipe rejects negative response lengths", () => RejectLengthAsync(-1)),
        ("Pipe rejects malformed JSON without keeping a stale connection", MalformedJsonAsync),
        ("Pipe detects a peer closing in the middle of a frame", TruncatedFrameAsync),
        ("Pipe reconnects successfully after a timed out response", TimeoutReconnectAsync),
        ("Pipe keeps one window identity across reconnects and separates different windows", WindowIdentityAsync),
        ("Pipe serializes twelve simultaneous requests without mixing replies", ConcurrentRequestsAsync),
        ("Pipe does not attempt a connection when no game PID is available", NoGameAsync)
    };

    private static async Task WindowIdentityAsync()
    {
        using var first=NewClient();string id="";
        for(int connection=0;connection<2;connection++)
        {
            await WithServerAsync(async(server,token)=>{
                var request=await ReadAsync(server,token);
                Assert(Guid.TryParseExact(request.controllerId,"N",out _),"Missing window identity");
                if(id.Length==0)id=request.controllerId;
                Assert(id==request.controllerId,"Window identity changed on reconnect");
                await WriteAsync(server,Response(request),token);
            },async()=>{await first.SendAsync(new WireMessage());});
            first.Disconnect();
        }
        using var second=NewClient();
        await WithServerAsync(async(server,token)=>{
            var request=await ReadAsync(server,token);
            Assert(request.controllerId!=id,"New window reused an old identity");
            await WriteAsync(server,Response(request),token);
        },async()=>{await second.SendAsync(new WireMessage());});
    }

    private static async Task StateSetResetAsync()
    {
        using var client = NewClient();
        var ids = new HashSet<string>();
        string controller = "";
        await WithServerAsync(async (server, token) =>
        {
            bool speed = false, stamina = false;
            float multiplier = 1f;
            var features = PeakWorkshop.Plugin.FeatureSettings.Defaults();
            for (int i = 0; i < 3; i++)
            {
                var request = await ReadAsync(server, token);
                Assert(request.protocol == WireProtocol.Version, "Client did not set the protocol.");
                Assert(!string.IsNullOrEmpty(request.requestId) && ids.Add(request.requestId), "Request ID absent or repeated.");
                Assert(Guid.TryParseExact(request.controllerId,"N",out _),"Window identity missing");
                if(controller.Length==0)controller=request.controllerId;
                Assert(request.controllerId==controller,"Window identity changed between requests");
                if (request.kind == "set") { speed = request.speedEnabled; stamina = request.staminaEnabled; multiplier = request.speedMultiplier; features = new PeakWorkshop.Plugin.FeatureSettings(request.features).ToWire(); }
                if (request.kind == "reset") { speed = false; stamina = false; multiplier = 1f; features = PeakWorkshop.Plugin.FeatureSettings.Defaults(); }
                var response = Response(request);
                response.speedEnabled = speed;
                response.staminaEnabled = stamina;
                response.speedMultiplier = multiplier;
                response.features = features;
                response.message = "角色已就绪";
                await WriteFragmentedAsync(server, response, token);
            }
        }, async () =>
        {
            var initial = await client.SendAsync(new WireMessage { kind = "state" });
            Assert(initial.ok && !initial.speedEnabled && initial.message == "角色已就绪", "Initial state corrupted.");
            var desired = PeakWorkshop.Plugin.FeatureSettings.Defaults();
            foreach(var feature in desired) feature.enabled = true;
            foreach(string id in new[]{"speed","climb.speed","jump","fall.threshold","ping.size"})
                Array.Find(desired,v=>v.id==id)!.value=FeatureCatalog.Find(id)!.Max;
            var applied = await client.SendAsync(new WireMessage { kind = "set", sessionId = initial.sessionId, speedEnabled = true, speedMultiplier = 200f, staminaEnabled = true, features = desired });
            Assert(applied.speedEnabled && applied.staminaEnabled && applied.speedMultiplier == 200f, "Raised legacy speed setting not returned.");
            Assert(applied.features.Length == 51 && applied.features.All(v=>v.enabled), "Full enabled feature state not returned.");
            Assert(applied.features.Select(v=>(v.id,v.value)).SequenceEqual(desired.Select(v=>(v.id,v.value))), "Raised caps or remaining feature values changed during round-trip.");
            var reset = await client.SendAsync(new WireMessage { kind = "reset", sessionId = initial.sessionId });
            Assert(!reset.speedEnabled && !reset.staminaEnabled && reset.speedMultiplier == 1f, "Reset did not return disabled state.");
            Assert(reset.features.Length == 51 && reset.features.All(v=>!v.enabled), "Reset left some full-state features enabled.");
            Assert(reset.features.All(v=>v.value==FeatureCatalog.Find(v.id)!.Default), "Reset kept a modified feature value.");
        });
    }

    private static async Task RejectOverflowingNumberAsync(bool legacyScalar)
    {
        using var client=NewClient();
        await WithServerAsync(async (server,token)=>
        {
            var response=Response(await ReadAsync(server,token));
            if(legacyScalar) response.speedMultiplier=123.25f;
            else response.features[0].value=123.25f;
            string field=legacyScalar?"speedMultiplier":"value";
            string payload=JsonSerializer.Serialize(response,Json).Replace("\""+field+"\":123.25","\""+field+"\":1e400",StringComparison.Ordinal);
            Assert(payload.Contains("1e400",StringComparison.Ordinal),"Overflow fixture was not applied.");
            await WriteBytesAsync(server,Encoding.UTF8.GetBytes(payload),token);
        },async ()=>
        {
            _=await ExpectAsync<IOException>(()=>client.SendAsync(new WireMessage()));
            Assert(!client.IsConnected && client.ProcessId==0,"Overflowing number left a stale connection.");
        });
    }

    private static async Task RejectResponseAsync(Action<WireMessage> alter, string expectedMessage)
    {
        using var client = NewClient();
        await WithServerAsync(async (server, token) =>
        {
            var response = Response(await ReadAsync(server, token));
            alter(response);
            await WriteAsync(server, response, token);
        }, async () =>
        {
            var exception = await ExpectAsync<IOException>(() => client.SendAsync(new WireMessage()));
            Assert(exception.Message.Contains(expectedMessage), "Wrong response rejection reason.");
            Assert(!client.IsConnected && client.ProcessId == 0, "Rejected response left a stale connection.");
        });
    }

    private static async Task RejectLengthAsync(int length)
    {
        using var client = NewClient();
        await WithServerAsync(async (server, token) =>
        {
            _ = await ReadAsync(server, token);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await server.WriteAsync(header, token);
        }, async () =>
        {
            var exception = await ExpectAsync<IOException>(() => client.SendAsync(new WireMessage()));
            Assert(exception.Message.Contains("长度无效"), "Wrong invalid length rejection reason.");
            Assert(!client.IsConnected, "Invalid response length left connection open.");
        });
    }

    private static async Task MalformedJsonAsync()
    {
        using var client = NewClient();
        await WithServerAsync(async (server, token) =>
        {
            _ = await ReadAsync(server, token);
            await WriteBytesAsync(server, Encoding.UTF8.GetBytes("{broken-json"), token);
        }, async () =>
        {
            _ = await ExpectAsync<JsonException>(() => client.SendAsync(new WireMessage()));
            Assert(!client.IsConnected, "Malformed JSON left connection open.");
        });
    }

    private static async Task TruncatedFrameAsync()
    {
        using var client = NewClient();
        await WithServerAsync(async (server, token) =>
        {
            _ = await ReadAsync(server, token);
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, 100);
            await server.WriteAsync(header, token);
            await server.WriteAsync(new byte[] { 123, 34 }, token);
        }, async () =>
        {
            _ = await ExpectAsync<IOException>(() => client.SendAsync(new WireMessage()));
            Assert(!client.IsConnected, "Truncated response left connection open.");
        });
    }

    private static async Task TimeoutReconnectAsync()
    {
        using var client = NewClient();
        await WithServerAsync(async (server, token) =>
        {
            _ = await ReadAsync(server, token);
            await Task.Delay(300, token);
        }, async () =>
        {
            var exception = await ExpectAsync<IOException>(() => client.SendAsync(new WireMessage(), 80));
            Assert(exception.Message.Contains("超时"), "Timeout was not reported.");
            Assert(!client.IsConnected, "Timeout left a stale connection.");
        });
        await WithServerAsync(async (server, token) =>
        {
            var response = Response(await ReadAsync(server, token));
            response.sessionId = "replacement-session";
            await WriteAsync(server, response, token);
        }, async () =>
        {
            var response = await client.SendAsync(new WireMessage());
            Assert(response.ok && response.sessionId == "replacement-session", "Connection did not recover after timeout.");
        });
    }

    private static async Task ConcurrentRequestsAsync()
    {
        using var client = NewClient();
        var received = new List<int>();
        await WithServerAsync(async (server, token) =>
        {
            for (int i = 0; i < 12; i++)
            {
                var request = await ReadAsync(server, token);
                received.Add(int.Parse(request.message));
                await Task.Delay(10, token);
                var response = Response(request);
                response.message = request.message;
                await WriteAsync(server, response, token);
            }
        }, async () =>
        {
            var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
            {
                var response = await client.SendAsync(new WireMessage { message = i.ToString() });
                Assert(response.message == i.ToString(), "Concurrent request got a different request's reply.");
                return response;
            }));
            Assert(responses.Length == 12 && received.Distinct().Count() == 12, "A concurrent request was lost or repeated.");
        });
    }

    private static async Task NoGameAsync()
    {
        using var client = new PipeClient(() => 0);
        var exception = await ExpectAsync<IOException>(() => client.SendAsync(new WireMessage()));
        Assert(exception.Message.Contains("尚未检测到"), "Missing game error was not reported.");
        Assert(!client.IsConnected && client.ProcessId == 0, "Missing game left connection state.");
    }

    private static PipeClient NewClient() => new(() => TestPid);

    private static WireMessage Response(WireMessage request) => new()
    {
        protocol = WireProtocol.Version, kind = "state", requestId = request.requestId, features = PeakWorkshop.Plugin.FeatureSettings.Defaults(),
        sessionId = "test-session", ok = true, characterReady = true, speedAvailable = true, staminaAvailable = true
    };

    private static async Task WithServerAsync(Func<NamedPipeServerStream, CancellationToken, Task> serve, Func<Task> useClient)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var server = new NamedPipeServerStream(WireProtocol.PipePrefix + TestPid, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        async Task RunAsync()
        {
            await server.WaitForConnectionAsync(timeout.Token);
            try { await serve(server, timeout.Token); }
            finally { if (server.IsConnected) server.Disconnect(); }
        }
        var serverTask = RunAsync();
        try
        {
            await useClient().WaitAsync(timeout.Token);
            await serverTask;
        }
        finally
        {
            timeout.Cancel();
            server.Dispose();
            try { await serverTask; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<WireMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert(count > 0 && count <= WireProtocol.MaxMessageBytes, "Invalid client frame length.");
        byte[] payload = new byte[count];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<WireMessage>(payload, Json)!;
    }

    private static Task WriteAsync(Stream stream, WireMessage response, CancellationToken token) =>
        WriteBytesAsync(stream, JsonSerializer.SerializeToUtf8Bytes(response, Json), token);

    private static async Task WriteBytesAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task WriteFragmentedAsync(Stream stream, WireMessage response, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(response, Json);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        foreach (byte b in header)
        {
            await stream.WriteAsync(new[] { b }, token);
            await Task.Delay(1, token);
        }
        for (int i = 0; i < payload.Length; i += 137)
        {
            await stream.WriteAsync(payload.AsMemory(i, Math.Min(137, payload.Length - i)), token);
            await Task.Delay(1, token);
        }
        await stream.FlushAsync(token);
    }

    private static async Task<T> ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T expected) { return expected; }
        throw new Exception("Expected exception " + typeof(T).Name + ".");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
