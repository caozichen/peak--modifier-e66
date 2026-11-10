using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Security.Principal;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Desktop;

internal sealed class PipeClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _stream;
    private int _pid;
    private bool _disposed;
    private long _connectionEpoch;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly JsonSerializerOptions _json = new() { IncludeFields = true };
    private readonly Func<int> _resolveProcessId;
    private readonly string _controllerId = Guid.NewGuid().ToString("N");

    internal PipeClient(Func<int>? resolveProcessId = null)
    {
        _resolveProcessId = resolveProcessId ?? FindGameProcessId;
    }

    public int ProcessId => _pid;
    public bool IsConnected => _stream?.IsConnected == true;

    public async Task<WireMessage> SendAsync(WireMessage request, int timeoutMs = 2500)
    {
        bool isQuery = request.kind == "state";
        long queuedEpoch = Interlocked.Read(ref _connectionEpoch);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(timeoutMs);
        await _gate.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                if (isQuery) await EnsureConnectedAsync(timeout.Token);
                else if (!IsConnected || queuedEpoch != Interlocked.Read(ref _connectionEpoch))
                    throw new IOException("连接已变化，旧操作已取消。请重新确认状态后操作。");
                var stream = _stream ?? throw new ObjectDisposedException(nameof(PipeClient));
                request.protocol = WireProtocol.Version;
                request.controllerId = _controllerId;
                request.requestId = Guid.NewGuid().ToString("N");
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request, _json);
                if (bytes.Length > WireProtocol.MaxMessageBytes) throw new IOException("请求超过协议长度上限。");
                byte[] header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(bytes, timeout.Token);
                await stream.FlushAsync(timeout.Token);
                await stream.ReadExactlyAsync(header, timeout.Token);
                int length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > WireProtocol.MaxMessageBytes) throw new IOException("插件响应长度无效。");
                byte[] reply = new byte[length];
                await stream.ReadExactlyAsync(reply, timeout.Token);
                var response = JsonSerializer.Deserialize<WireMessage>(reply, _json) ?? throw new IOException("插件响应为空。");
                if (response.protocol != WireProtocol.Version) throw new IOException("插件协议版本不匹配，请更新桌面端和插件。");
                if (response.requestId != request.requestId) throw new IOException("插件响应与当前请求不匹配。");
                var speedDefinition = FeatureCatalog.Find("speed")!;
                if (response.kind != "state" || string.IsNullOrWhiteSpace(response.sessionId) ||
                    !float.IsFinite(response.speedMultiplier) || response.speedMultiplier < speedDefinition.Min || response.speedMultiplier > speedDefinition.Max)
                    throw new IOException("插件响应状态无效。");
                ValidateFeatureState(response.features);
                return response;
            }
            catch (OperationCanceledException)
            {
                Disconnect();
                throw new IOException("游戏已运行，但插件连接超时。请检查加载器与插件日志。");
            }
            catch
            {
                Disconnect();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private static void ValidateFeatureState(FeatureValue[]? values)
    {
        if (values == null || values.Length != FeatureCatalog.All.Length) throw new IOException("插件功能状态不完整，请更新桌面端和插件。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value == null || string.IsNullOrWhiteSpace(value.id) || !seen.Add(value.id)) throw new IOException("插件功能状态重复或缺少标识。");
            var definition = FeatureCatalog.Find(value.id);
            if (definition == null || !float.IsFinite(value.value) || value.value < definition.Min || value.value > definition.Max)
                throw new IOException("插件功能状态参数无效。");
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken token)
    {
        int foundPid = _resolveProcessId();
        if (foundPid == 0)
        {
            Disconnect();
            throw new IOException("尚未检测到 PEAK。请先启动已部署插件的游戏。");
        }
        if (_stream?.IsConnected == true && _pid == foundPid) return;
        Disconnect();
        _pid = foundPid;
        _stream = new NamedPipeClientStream(".", WireProtocol.PipePrefix + foundPid, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Anonymous);
        await _stream.ConnectAsync(token);
        PipeIdentity.RequireServerProcess(_stream, foundPid);
    }

    internal static int FindGameProcessId()
    {
        Process[] processes = Process.GetProcessesByName("PEAK");
        try { return processes.OrderBy(p => p.Id).Select(p => p.Id).FirstOrDefault(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal void Disconnect()
    {
        try { _stream?.Dispose(); } catch { }
        _stream = null;
        _pid = 0;
        Interlocked.Increment(ref _connectionEpoch);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        Disconnect();
        // Dispose the CTS only after queued operations drain; no native semaphore handle is used.
        _ = ReleaseAfterDrainAsync();
    }

    private async Task ReleaseAfterDrainAsync()
    {
        await _gate.WaitAsync();
        _lifetime.Dispose();
        _gate.Release();
    }
}
