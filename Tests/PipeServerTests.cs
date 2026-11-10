using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PeakWorkshop.Desktop;
using PeakWorkshop.Plugin;
using PeakWorkshop.Protocol;

internal static class PipeServerTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true };
    public static readonly List<object> Resources = new();
    public static List<(string Name, Func<Task> Body)> Create() => new()
    {
        ("Production server can stop before accepting a client in 100 repeated races", StopBeforeConnectAsync),
        ("Production server stops while a client sends only a partial header", StopPartialReadAsync),
        ("Idle client is evicted and a new local client can connect", IdleEvictionAsync),
        ("Stopping server cancels a pending dispatcher without a waiting thread", CancelDispatcherAsync),
        ("Detach acknowledgement is delivered before production server stops", DetachAckAsync),
        ("A queued modifying command is cancelled after a previous request disconnects", StaleModificationAsync),
        ("Disposing client cancels an in-flight read and queued operations", DisposeClientAsync),
        ("Client rejects a pipe whose server process does not match the game PID", WrongProcessAsync),
        ("Confirmed detach releases the desktop pipe handle and clears connection state", DetachClientAsync),
        ("8000 production pipe requests and 100 reconnects retain bounded handles and managed memory", ResourceStressAsync)
    };

    private static string Name() => WireProtocol.PipePrefix + Environment.ProcessId + ".audit." + Guid.NewGuid().ToString("N");
    private static PipeServer Server(string name, int timeout = 1000, Func<string,CancellationToken,Task<PipeReply>>? handle = null) =>
        new(name, handle ?? ((json,token) => Task.FromResult(Reply(json))), () => { }, Console.WriteLine, timeout);
    private static NamedPipeClientStream Client(string name) => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private static PipeReply Reply(string json, bool stop = false)
    {
        var request = JsonSerializer.Deserialize<WireMessage>(json, Json)!;
        return new PipeReply { StopAfterReply = stop, Payload = JsonSerializer.SerializeToUtf8Bytes(new WireMessage
        { requestId=request.requestId, sessionId="audit-session", features=FeatureSettings.Defaults(), ok=true, pluginStopped=stop, message=request.message }, Json) };
    }
    private static async Task SendAsync(Stream client, string message)
    {
        var bytes=Encoding.UTF8.GetBytes(message);
        var header=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await client.WriteAsync(header); await client.WriteAsync(bytes); await client.FlushAsync();
    }
    private static async Task<WireMessage> ReadAsync(Stream client)
    {
        using var timeout=new CancellationTokenSource(3000);
        var header=new byte[4]; await client.ReadExactlyAsync(header,timeout.Token);
        var payload=new byte[BinaryPrimitives.ReadInt32LittleEndian(header)];
        await client.ReadExactlyAsync(payload,timeout.Token);
        return JsonSerializer.Deserialize<WireMessage>(payload,Json)!;
    }
    private static void Assert(bool value,string detail) { if(!value) throw new Exception(detail); }

    private static async Task StopBeforeConnectAsync()
    {
        for(int i=0;i<100;i++) { using var server=Server(Name()); if(i%2==0) await Task.Yield(); server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }
    private static async Task StopPartialReadAsync()
    {
        string name=Name(); using var server=Server(name); using var client=Client(name);
        await client.ConnectAsync(2000); await client.WriteAsync(new byte[]{3});
        server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task IdleEvictionAsync()
    {
        string name=Name(); using var server=Server(name,150);
        using(var idle=Client(name))
        {
            await idle.ConnectAsync(2000);
            using var timeout=new CancellationTokenSource(2000);
            try { int read=await idle.ReadAsync(new byte[1],timeout.Token); Assert(read==0,"Idle peer wasn't closed."); }
            catch(IOException) { }
        }
        using(var next=Client(name))
        {
            await next.ConnectAsync(2000); await SendAsync(next,JsonSerializer.Serialize(new WireMessage{requestId="next"},Json));
            Assert((await ReadAsync(next)).requestId=="next","Server did not recover after idle eviction.");
        }
        server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task CancelDispatcherAsync()
    {
        string name=Name(); var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server=Server(name,10000,async (json,token)=> { entered.SetResult(); await Task.Delay(Timeout.Infinite,token); return Reply(json); });
        using var client=Client(name); await client.ConnectAsync(2000); await SendAsync(client,"{}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); server.Stop();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task DetachAckAsync()
    {
        string name=Name(); using var server=Server(name,1000,(json,token)=>Task.FromResult(Reply(json,true)));
        using var client=Client(name); await client.ConnectAsync(2000);
        await SendAsync(client,JsonSerializer.Serialize(new WireMessage{kind="detach",requestId="stop"},Json));
        var response=await ReadAsync(client); Assert(response.ok&&response.pluginStopped&&response.requestId=="stop","Detach acknowledgement lost.");
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task StaleModificationAsync()
    {
        int sets=0; string name=WireProtocol.PipePrefix+Environment.ProcessId;
        using var server=Server(name,1000,async (json,token)=>
        {
            var request=JsonSerializer.Deserialize<WireMessage>(json,Json)!;
            if(request.kind=="set") Interlocked.Increment(ref sets);
            if(request.message=="slow") await Task.Delay(250,token);
            return Reply(json);
        });
        using var client=new PipeClient(()=>Environment.ProcessId);
        await client.SendAsync(new WireMessage());
        var slow=client.SendAsync(new WireMessage{message="slow"},80);
        await Task.Delay(10);
        var stale=client.SendAsync(new WireMessage{kind="set",sessionId="audit-session"},1000);
        try { await slow; throw new Exception("Expected read timeout."); } catch(IOException) { }
        try { await stale; throw new Exception("Queued stale set was not rejected."); } catch(IOException) { }
        Assert(sets==0,"A stale set reached the server.");
        server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task DisposeClientAsync()
    {
        string name=WireProtocol.PipePrefix+Environment.ProcessId;
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server=Server(name,10000,async(json,token)=> { entered.TrySetResult(); await Task.Delay(Timeout.Infinite,token); return Reply(json); });
        var client=new PipeClient(()=>Environment.ProcessId); var active=client.SendAsync(new WireMessage(),5000);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued=client.SendAsync(new WireMessage(),5000); client.Dispose();
        foreach(var pending in new[]{active,queued})
        {
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("Disposed operation completed unexpectedly."); }
            catch(Exception e) when(e is IOException || e is OperationCanceledException || e is ObjectDisposedException) { }
        }
        client.Dispose(); server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static (long Heap,int Handles,int Threads) Sample()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var p=Process.GetCurrentProcess(); p.Refresh();
        long heap = GC.GetGCMemoryInfo().HeapSizeBytes;
        Assert(heap > 0, "GC heap size measurement is unavailable.");
        return (heap,p.HandleCount,p.Threads.Count);
    }
    private static async Task WrongProcessAsync()
    {
        int wrongPid=Environment.ProcessId+100;
        using var server=Server(WireProtocol.PipePrefix+wrongPid);
        using var client=new PipeClient(()=>wrongPid);
        try { await client.SendAsync(new WireMessage()); throw new Exception("Wrong server PID accepted."); }
        catch(IOException ex) { Assert(ex.Message.Contains("不是当前游戏进程"),"Wrong identity rejection reason."); }
        server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task DetachClientAsync()
    {
        string name=WireProtocol.PipePrefix+Environment.ProcessId;
        using var server=Server(name,1000,(json,token)=>
        {
            var request=JsonSerializer.Deserialize<WireMessage>(json,Json)!;
            return Task.FromResult(Reply(json,request.kind=="detach"));
        });
        using var client=new PipeClient(()=>Environment.ProcessId);
        await client.SendAsync(new WireMessage());
        var stopped=await client.SendAsync(new WireMessage{kind="detach",sessionId="audit-session"});
        Assert(stopped.pluginStopped,"Detach wasn't confirmed.");
        client.Disconnect();
        Assert(!client.IsConnected&&client.ProcessId==0,"Desktop kept the detached pipe connection.");
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task ResourceStressAsync()
    {
        string name=Name(); using var server=Server(name,5000);
        using(var warm=Client(name))
        {
            await warm.ConnectAsync(2000);
            for(int i=0;i<300;i++) { await SendAsync(warm,"{}"); _=await ReadAsync(warm); }
            var baseline=Sample();
            Resources.Add(new{stage="after-warmup",requests=300,managedBytes=baseline.Heap,handles=baseline.Handles,threads=baseline.Threads});
            for(int batch=1;batch<=4;batch++)
            {
                for(int i=0;i<2000;i++) { await SendAsync(warm,"{}"); _=await ReadAsync(warm); }
                var sample=Sample();
                Resources.Add(new{stage="requests",requests=batch*2000,managedBytes=sample.Heap,handles=sample.Handles,threads=sample.Threads});
                Assert(sample.Handles-baseline.Handles<32,"Pipe requests accumulated OS handles.");
                Assert(sample.Heap-baseline.Heap<4*1024*1024,"Retained managed memory grew beyond the stress-test bound.");
            }
        }
        var before=Sample();
        for(int i=0;i<100;i++)
        {
            using var client=Client(name); await client.ConnectAsync(3000); await SendAsync(client,"{}"); _=await ReadAsync(client);
        }
        server.Stop(); await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        var after=Sample();
        Resources.Add(new{stage="100-reconnects-and-stop",managedBytes=after.Heap,handles=after.Handles,threads=after.Threads,handleDelta=after.Handles-before.Handles,managedDelta=after.Heap-before.Heap});
        Assert(after.Handles-before.Handles<32,"Reconnects accumulated OS handles.");
        Assert(after.Heap-before.Heap<4*1024*1024,"Reconnects retained excessive managed memory.");
    }
}
