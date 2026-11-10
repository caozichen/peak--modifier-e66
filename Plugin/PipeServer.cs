#nullable disable
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Plugin
{
    internal sealed class PipeReply
    {
        public byte[] Payload = Array.Empty<byte>();
        public bool StopAfterReply = false;
    }

    // This class has no Unity references. The exact production transport is exercised by tests.
    internal sealed class PipeServer : IDisposable
    {
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly string _name;
        private readonly int _timeoutMs;
        private readonly Func<string, CancellationToken, Task<PipeReply>> _dispatch;
        private readonly Action _disconnected;
        private readonly Action<string> _log;
        private NamedPipeServerStream _pipe;
        private int _stopRequested;
        private long _lastErrorLog;
        public Task Completion { get; }

        public PipeServer(string name, Func<string, CancellationToken, Task<PipeReply>> dispatch,
            Action disconnected, Action<string> log, int timeoutMs = 3000)
        {
            _name = name;
            _dispatch = dispatch;
            _disconnected = disconnected;
            _log = log;
            _timeoutMs = timeoutMs;
            Completion = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            var lifetime = _stop.Token;
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    bool accepted = false;
                    string stage = "create-secure-pipe";
                    try
                    {
                        using (var pipe = SecurePipeFactory.Create(_name))
                        {
                            lock (_sync)
                            {
                                if (_stopRequested != 0) return;
                                _pipe = pipe;
                            }
                            stage = "wait-for-client";
                            await pipe.WaitForConnectionAsync(lifetime).ConfigureAwait(false);
                            stage = "verify-local-client";
                            PipeIdentity.RequireLocalClient(pipe);
                            accepted = true;
                            while (!lifetime.IsCancellationRequested && pipe.IsConnected)
                            {
                                using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
                                {
                                    requestTimeout.CancelAfter(_timeoutMs);
                                    var token = requestTimeout.Token;
                                    stage = "read-header";
                                    byte[] header = await ReadExactlyAsync(pipe, 4, token).ConfigureAwait(false);
                                    int size = header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24;
                                    if (size <= 0 || size > WireProtocol.MaxMessageBytes) throw new InvalidDataException("消息长度非法。");
                                    stage = "read-payload";
                                    byte[] data = await ReadExactlyAsync(pipe, size, token).ConfigureAwait(false);
                                    string json = new UTF8Encoding(false, true).GetString(data);
                                    stage = "dispatch";
                                    PipeReply reply = await _dispatch(json, token).ConfigureAwait(false);
                                    token.ThrowIfCancellationRequested();
                                    if (reply?.Payload == null || reply.Payload.Length == 0 || reply.Payload.Length > WireProtocol.MaxMessageBytes)
                                        throw new InvalidDataException("响应长度非法。");
                                    int count = reply.Payload.Length;
                                    var prefix = new[] { (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24) };
                                    stage = "write-reply";
                                    await pipe.WriteAsync(prefix, 0, 4, token).ConfigureAwait(false);
                                    await pipe.WriteAsync(reply.Payload, 0, count, token).ConfigureAwait(false);
                                    stage = "flush-reply";
                                    await pipe.FlushAsync(token).ConfigureAwait(false);
                                    if (reply.StopAfterReply) { Stop(); return; }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!lifetime.IsCancellationRequested && !(ex is EndOfStreamException) && !(ex is OperationCanceledException) && !(ex is ObjectDisposedException) &&
                            DateTime.UtcNow.Ticks - _lastErrorLog > TimeSpan.TicksPerSecond * 5)
                        { _log("IPC 阶段=" + stage + "；传输=Win32 ACL / Mono overlapped；完整异常：" + ex); _lastErrorLog = DateTime.UtcNow.Ticks; }
                    }
                    finally
                    {
                        lock (_sync) _pipe = null;
                        if (accepted) _disconnected();
                    }
                    if (!lifetime.IsCancellationRequested) await Task.Delay(100, lifetime).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                Interlocked.Exchange(ref _stopRequested, 1);
                _stop.Dispose();
            }
        }

        private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken token)
        {
            var data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(data, offset, count - offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
            return data;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            lock (_sync)
            {
                try { _pipe?.Dispose(); } catch (IOException) { }
            }
        }

        public void Dispose() { Stop(); }
    }
}
