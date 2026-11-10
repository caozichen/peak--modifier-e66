using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using PeakWorkshop.Protocol;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakWorkshop.Plugin
{
    [BepInPlugin(PluginId, "PEAK Workshop", PluginVersion)]
    public sealed class TrainerPlugin : BaseUnityPlugin
    {
        public const string PluginId = "local.peakworkshop.trainer";
        public const string PluginVersion = "0.2.6";
        private readonly ConcurrentQueue<PendingRequest> _requests = new ConcurrentQueue<PendingRequest>();
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private GameAdapter _game;
        private Harmony _harmony;
        private PipeServer _server;
        private int _deactivated;
        private int _disconnectRequested;
        private int _queued;
        private int _revision;
        private long _lastSeen;
        private bool _controlActive;
        private string _controllerId = "";
        private long _lastErrorLog;
        private int _sceneHandle = int.MinValue;
        private string _sceneName = "";
        private string _gameVersion = "";
        private string _lastNotice = "功能默认关闭。";
        private bool _unpatchSucceeded;

        private sealed class PendingRequest
        {
            public string Json;
            public int Revision;
            public CancellationToken Token;
            public readonly TaskCompletionSource<PipeReply> Completion = new TaskCompletionSource<PipeReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void Awake()
        {
            _game = new GameAdapter(message => Logger.LogInfo(message));
            _harmony = new Harmony(PluginId);
            _gameVersion = Application.version + " / Unity " + Application.unityVersion;
            try { _game.Initialize(_harmony); }
            catch (Exception ex) { Logger.LogError("适配初始化失败，功能保持禁用：" + ex); }
            _lastSeen = Stopwatch.GetTimestamp();
            _server = new PipeServer(WireProtocol.PipePrefix + Process.GetCurrentProcess().Id,
                DispatchAsync, () => Interlocked.Exchange(ref _disconnectRequested, 1),
                message => Logger.LogWarning(message));
            Logger.LogInfo("PEAK Workshop " + PluginVersion + " 已初始化；功能默认关闭。会话=" + _sessionId);
        }

        private async Task<PipeReply> DispatchAsync(string json, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _deactivated) != 0) throw new OperationCanceledException();
            if (Interlocked.Increment(ref _queued) > 8)
            {
                Interlocked.Decrement(ref _queued);
                throw new InvalidOperationException("请求队列已满。");
            }
            var pending = new PendingRequest { Json = json, Revision = Volatile.Read(ref _revision), Token = token };
            _requests.Enqueue(pending);
            if (Volatile.Read(ref _deactivated) != 0) CancelPending();
            using (token.Register(() => pending.Completion.TrySetCanceled()))
                return await pending.Completion.Task.ConfigureAwait(false);
        }

        private void Update()
        {
            if (Volatile.Read(ref _deactivated) != 0)
            {
                // Wait until the final IPC acknowledgement is delivered, then stop even empty Update callbacks.
                if (_server == null || _server.Completion.IsCompleted) enabled = false;
                return;
            }
            if (_game == null) return;
            try
            {
                var scene = SceneManager.GetActiveScene();
                bool changed = _sceneHandle != scene.handle;
                if (changed) _game.SuspendEffects();
                _sceneHandle = scene.handle;
                _sceneName = scene.name ?? "";
                bool wasReady = _game.Ready;
                changed |= _game.PollCharacter();
                changed |= wasReady != _game.Ready;
                if (changed)
                {
                    Interlocked.Increment(ref _revision);
                    _lastNotice = _game.Ready ? "角色已就绪，正在恢复已保留的开关和倍率。" : "正在切换场景或等待角色，开关和倍率已保留。";
                    if (_game.Settings.AnyEnabled) Logger.LogInfo(_lastNotice);
                }
                if (Interlocked.Exchange(ref _disconnectRequested, 0) != 0)
                {
                    Interlocked.Increment(ref _revision);
                    _controlActive = false;
                    _game.SuspendEffects();
                    _lastNotice = "连接暂时中断，效果已暂停，开关和倍率已保留。";
                }
                double inactive = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastSeen)) / (double)Stopwatch.Frequency;
                if (inactive > 3 && _controlActive)
                {
                    Interlocked.Increment(ref _revision);
                    _controlActive = false;
                    _game.SuspendEffects();
                    _lastNotice = "等待控制窗口重新连接，效果已暂停，开关和倍率已保留。";
                }
                _game.RefreshEffects(_controlActive);
                _game.Tick();

                int budget = 8;
                while (budget-- > 0 && _requests.TryDequeue(out var pending))
                {
                    Interlocked.Decrement(ref _queued);
                    if (pending.Token.IsCancellationRequested) { pending.Completion.TrySetCanceled(); continue; }
                    WireMessage request = null;
                    WireMessage response;
                    try
                    {
                        request = WireJson.Deserialize(pending.Json);
                        response = Handle(request, pending.Revision);
                    }
                    catch (Exception ex)
                    {
                        response = Snapshot(request, false, "invalid_request", "无法处理请求：" + ex.Message);
                    }
                    pending.Completion.TrySetResult(new PipeReply
                    {
                        Payload = Encoding.UTF8.GetBytes(WireJson.Serialize(response)),
                        StopAfterReply = response.pluginStopped
                    });
                    if (response.pluginStopped) { CancelPending(); return; }
                }
            }
            catch (Exception ex)
            {
                try { _game.Reset(); }
                catch (Exception cleanup) { Logger.LogWarning("异常后的功能恢复：" + cleanup.Message); }
                Interlocked.Increment(ref _revision);
                CancelPending();
                _lastNotice = "插件处理异常，已关闭功能。";
                long now = Stopwatch.GetTimestamp();
                if (now - _lastErrorLog > Stopwatch.Frequency * 5) { Logger.LogError(ex); _lastErrorLog = now; }
            }
        }

        private WireMessage Handle(WireMessage request, int queuedRevision)
        {
            if (request == null || request.protocol != WireProtocol.Version)
                return Snapshot(request, false, "protocol_mismatch", "协议版本不匹配。");
            if (request.kind != "state" && request.kind != "set" && request.kind != "reset" && request.kind != "detach" && request.kind != "clear_status")
                return Snapshot(request, false, "unknown_command", "不支持的命令。");
            if (Volatile.Read(ref _deactivated) != 0 && request.kind != "state")
                return Snapshot(request, false, "plugin_stopped", "插件已停用，请重启游戏后重新连接。");
            string controllerId = request.controllerId ?? "";
            if (!string.Equals(controllerId, _controllerId, StringComparison.Ordinal))
            {
                if (request.kind != "state")
                    return Snapshot(request, false, "controller_changed", "控制窗口已变化，请先重新查询游戏状态。");
                ResetFeatures("新的控制窗口已连接，功能默认关闭。");
                _controllerId = controllerId;
                Interlocked.Increment(ref _revision);
            }
            if (request.kind != "state" && (string.IsNullOrEmpty(request.sessionId) || request.sessionId != _sessionId))
                return Snapshot(request, false, "session_mismatch", "请先重新查询当前游戏会话。");
            if ((request.kind == "set" || request.kind == "clear_status") && (request.stateRevision != _revision || queuedRevision != _revision))
                return Snapshot(request, false, "state_changed", "角色、场景或连接已变化，请确认状态后重新操作。");
            Interlocked.Exchange(ref _lastSeen, Stopwatch.GetTimestamp());
            if (request.kind == "state" || request.kind == "set" || request.kind == "clear_status")
            {
                _controlActive = true;
                _game.RefreshEffects(true);
            }
            if (request.kind == "detach")
            {
                Deactivate(false);
                return Snapshot(request, _unpatchSucceeded, _unpatchSucceeded ? "" : "unpatch_failed",
                    _unpatchSucceeded ? "已关闭功能并撤销本插件补丁，通信将在此响应后停止。DLL 随游戏退出释放。" : "功能已关闭，但补丁撤销未能确认，请退出游戏完成清理。");
            }
            if (request.kind == "reset")
            {
                Interlocked.Increment(ref _revision);
                ResetFeatures("已关闭本插件的全部功能。");
            }
            if (request.kind == "clear_status")
            {
                if (!_game.Ready) return Snapshot(request, false, "character_not_ready", _game.ReadinessMessage);
                if (!_game.ClearStatusAvailable) return Snapshot(request, false, "clear_status_unavailable", _game.ClearStatusUnavailableReason);
                // Invalidate already queued operations, including duplicate action requests.
                // Clearing is a single operation and must not alter persistent feature settings.
                Interlocked.Increment(ref _revision);
                try
                {
                    _game.ClearCurrentStatus();
                    _lastNotice = "已执行一次清除：清除可治疗状态（含诅咒、石化）并拔除附着荆棘。螃蟹、携带物品负重及持续来源需要另行解除，状态可能再次产生。";
                    return Snapshot(request, true, "", _lastNotice);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("一键清除当前状态：" + ex);
                    _lastNotice = "清除未能全部完成，请检查游戏内结果和插件日志；本次操作不会自动重试。";
                    return Snapshot(request, false, "clear_status_failed", _lastNotice);
                }
            }
            if (request.kind == "set")
            {
                FeatureSettings next;
                try { next = new FeatureSettings(request.features); }
                catch (ArgumentException ex) { return Snapshot(request, false, "invalid_features", ex.Message); }
                if (next.AnyEnabled && !_game.Ready)
                    return Snapshot(request, false, "character_not_ready", _game.ReadinessMessage);
                foreach (var feature in next.ToWire())
                    if (feature.enabled && !_game.IsAvailable(feature.id) &&
                        (!_game.Settings.Enabled(feature.id) || _game.Settings.Value(feature.id) != feature.value))
                        return Snapshot(request, false, "feature_unavailable", feature.id + "：" + _game.AvailabilityReason(feature.id));
                _game.Apply(next);
                _lastNotice = "设置已应用；实际游戏效果请测试确认。";
            }
            return Snapshot(request, true, "", _lastNotice);
        }

        private WireMessage Snapshot(WireMessage request, bool ok, string error, string message)
        {
            var settings = _game.Settings;
            bool stopped = Volatile.Read(ref _deactivated) != 0;
            return new WireMessage
            {
                kind = "state", requestId = request?.requestId ?? "", sessionId = _sessionId,
                protocol = WireProtocol.Version, controllerId = _controllerId, ok = ok, error = error ?? "", message = message ?? "",
                pluginVersion = PluginVersion, gameVersion = _gameVersion, scene = _sceneName,
                characterReady = !stopped && _game.Ready, speedAvailable = !stopped && _game.SpeedAvailable,
                characterReadinessMessage = stopped ? "插件已停用。" : _game.ReadinessMessage,
                clearStatusAvailable = !stopped && _game.ClearStatusAvailable,
                clearStatusReason = stopped ? "插件已停用。" : _game.ClearStatusUnavailableReason,
                staminaAvailable = !stopped && _game.StaminaAvailable, speedEnabled = settings.SpeedEnabled,
                speedMultiplier = settings.SpeedMultiplier, staminaEnabled = settings.StaminaEnabled,
                pluginStopped = stopped, detachAvailable = !stopped, stateRevision = _revision, features = _game.SnapshotFeatures(stopped)
            };
        }

        private void ResetFeatures(string reason)
        {
            bool wasEnabled = _game.Settings.AnyEnabled;
            _game.Reset();
            _lastNotice = reason;
            if (wasEnabled) Logger.LogInfo(reason);
        }

        private void CancelPending()
        {
            while (_requests.TryDequeue(out var pending))
            {
                Interlocked.Decrement(ref _queued);
                pending.Completion.TrySetCanceled();
            }
        }

        private void Deactivate(bool stopTransport)
        {
            if (Interlocked.Exchange(ref _deactivated, 1) == 0)
            {
                bool cleanupSucceeded = true;
                try { _game?.Reset(); }
                catch (Exception ex) { cleanupSucceeded = false; Logger.LogWarning("功能恢复：" + ex.Message); }
                try { _game?.Dispose(); }
                catch (Exception ex) { cleanupSucceeded = false; Logger.LogWarning("适配器清理：" + ex.Message); }
                try
                {
                    _harmony?.UnpatchSelf();
                    _unpatchSucceeded = cleanupSucceeded;
                    foreach (var method in Harmony.GetAllPatchedMethods())
                        if (Harmony.GetPatchInfo(method)?.Owners.Contains(PluginId) == true) { _unpatchSucceeded = false; break; }
                }
                catch (Exception ex) { Logger.LogWarning("补丁清理：" + ex.Message); }
            }
            if (stopTransport) { _server?.Stop(); CancelPending(); }
        }

        private void OnDisable() { Deactivate(true); }
        private void OnDestroy() { Deactivate(true); }
    }
}
