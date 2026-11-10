using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakWorkshop.Plugin
{
    internal sealed partial class GameAdapter
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private readonly Action<string> _log;
        private FieldInfo _localCharacter;
        private FieldInfo _characterData;
        private FieldInfo _movementCharacter;
        private PropertyInfo _isLocal;
        private PropertyInfo _isInitialized;
        private FieldInfo _refsInitialized;
        private PropertyInfo _dead;
        private FieldInfo _passedOut;
        private FieldInfo _fullyPassedOut;
        private object _current;
        private bool _ready;
        private bool _effectsAllowed;
        private FeatureSettings _requestedSettings = FeatureSettings.Disabled;
        private FeatureSettings _settings = FeatureSettings.Disabled;
        private FeatureSettings _resolvedSettings;
        private int _resolvedCapabilityRevision = -1;
        private static GameAdapter _active;
        public bool SpeedAvailable { get; private set; }
        public bool StaminaAvailable { get; private set; }
        public string CompatibilityMessage { get; private set; } = "尚未检查游戏程序集。";
        public bool Ready => _ready;
        public string ReadinessMessage { get; private set; } = "尚未生成本地角色。";
        public object Current => _current;
        // User choices survive temporary actor, scene and connection transitions.
        // Hooks read _settings, which contains only the currently permitted effects.
        public FeatureSettings Settings => _requestedSettings;
        public bool EffectsActive => _settings.AnyEnabled;

        public GameAdapter(Action<string> log) { _log = log; }

        public void Initialize(Harmony harmony)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            if (assembly == null) throw new InvalidOperationException("游戏程序集尚未加载。");
            var character = assembly.GetType("Character", true);
            var movement = assembly.GetType("CharacterMovement", true);
            var data = assembly.GetType("CharacterData", true);
            _localCharacter = character.GetField("localCharacter", All);
            _characterData = character.GetField("data", All);
            _movementCharacter = movement.GetField("character", All);
            _isLocal = character.GetProperty("IsLocal", All);
            _isInitialized = character.GetProperty("IsInitialized", All);
            _refsInitialized = character.GetField("_refsInitialized", All);
            _dead = data.GetProperty("dead", All);
            _passedOut = data.GetField("passedOut", All);
            _fullyPassedOut = data.GetField("fullyPassedOut", All);
            if (_localCharacter == null || _characterData == null || _movementCharacter == null ||
                _isLocal == null || _isInitialized == null || _refsInitialized?.FieldType != typeof(bool) ||
                _dead == null || _passedOut == null || _fullyPassedOut == null)
                throw new MissingMemberException("当前游戏缺少预期的角色生命周期成员。");
            _active = this;
            InitializeActor(assembly, harmony);
            InitializeClearStatus(assembly);
            InitializeWorld(assembly, harmony);
            SpeedAvailable = IsAvailable("speed");
            StaminaAvailable = IsAvailable("stamina");
            CompatibilityMessage = "游戏接口检查完成；各项功能按实际接口支持情况启用，仍需游戏内验证。";
            _log(CompatibilityMessage);
        }

        public bool PollCharacter()
        {
            object next = _localCharacter?.GetValue(null);
            if (!Alive(next)) next = null;
            bool changed = !ReferenceEquals(next, _current);
            if (changed)
            {
                SuspendEffects();
                RestoreActorCharacter(_current);
            }
            _current = next;
            string reason = CharacterBlockReason(next);
            if (reason.Length != 0 && _ready) SuspendEffects();
            _ready = reason.Length == 0;
            string status = _ready ? "本地角色就绪。" : reason;
            if (status != ReadinessMessage)
            {
                ReadinessMessage = status;
                _log("角色状态：" + status);
            }
            return changed;
        }

        public void Apply(FeatureSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _requestedSettings = settings;
            RefreshEffects(_effectsAllowed);
        }

        public void Tick()
        {
            TickWorld();
            RefreshEffects(_effectsAllowed);
        }

        public void RefreshEffects(bool allowed)
        {
            if (!allowed || !_ready)
            {
                SuspendEffects();
                return;
            }
            _effectsAllowed = true;
            if (ReferenceEquals(_resolvedSettings, _requestedSettings) &&
                _resolvedCapabilityRevision == _capabilityRevision) return;
            var next = _requestedSettings.DisableUnavailable(IsAvailable, out _);
            var previous = _settings;
            _settings = next;
            _resolvedSettings = _requestedSettings;
            _resolvedCapabilityRevision = _capabilityRevision;
            ActorSettingsChanged(previous);
        }

        public void SuspendEffects()
        {
            var previous = _settings;
            _effectsAllowed = false;
            _settings = FeatureSettings.Disabled;
            _resolvedSettings = null;
            _resolvedCapabilityRevision = -1;
            try { ResetWorld(); }
            finally
            {
                // World restore failures must not skip recalculating derived actor state.
                if (previous.Enabled("weight.disabled")) ActorRecalculateWeight(_current, true);
            }
        }

        public void Reset()
        {
            _requestedSettings = FeatureSettings.Disabled;
            SuspendEffects();
        }
        public void Dispose()
        {
            try { Reset(); }
            finally
            {
                try { DisposeWorld(); }
                finally
                {
                    // Always make installed hooks inert and release game object references,
                    // even if a destroyed world object rejects restoration during shutdown.
                    if (ReferenceEquals(_active, this)) _active = null;
                    _current = null;
                    _ready = false;
                    ReadinessMessage = "插件已停用。";
                    _actorClimbDepth = _actorStatusDepth = 0;
                    _actorOwners.Clear();
                    _actorReadHelpers.Clear();
                }
            }
        }

        private static bool Alive(object value)
        {
            return value != null && value is UnityEngine.Object obj && obj != null;
        }

        internal bool Eligible(object character)
        {
            return _effectsAllowed && _ready && CharacterBlockReason(character).Length == 0;
        }

        private string CharacterBlockReason(object character)
        {
            if (!Alive(character)) return "尚未生成本地角色。";
            if (!ReferenceEquals(character, _current)) return "角色已变化，正在重新识别。";
            try
            {
                if (!(bool)_isLocal.GetValue(character)) return "当前对象不是本地角色。";
                if (!(bool)_refsInitialized.GetValue(character)) return "角色组件尚未完成初始化。";
                var data = _characterData.GetValue(character);
                if (!Alive(data)) return "角色数据尚未就绪。";
                if ((bool)_dead.GetValue(data)) return "本地角色已死亡，请等待复活。";
                if ((bool)_passedOut.GetValue(data) || (bool)_fullyPassedOut.GetValue(data))
                    return "本地角色已倒地或昏迷，请等待恢复。";
                // Character.IsInitialized also waits for the run's statistics. The
                // game disables those statistics in Airport, where players can
                // already move. Keep full run initialization in every other scene.
                if (!(bool)_isInitialized.GetValue(character) &&
                    !string.Equals(SceneManager.GetActiveScene().name, "Airport", StringComparison.Ordinal))
                    return "正在等待关卡初始化完成。";
                return "";
            }
            catch { return "读取角色状态失败，功能保持关闭。"; }
        }

        private static void MovementPostfix(object __instance, ref float __result)
        {
            var adapter = _active;
            if (adapter == null || !adapter._settings.SpeedEnabled) return;
            try
            {
                var owner = adapter._movementCharacter.GetValue(__instance);
                __result = adapter._settings.ApplyMovement(__result, adapter.Eligible(owner));
            }
            catch { /* A missing/destroyed character must leave the game's result unchanged. */ }
        }

        private static void StaminaPostfix(object __instance, ref bool __result)
        {
            var adapter = _active;
            if (adapter == null || !adapter._settings.StaminaEnabled) return;
            __result = adapter._settings.ApplyStamina(__result, adapter.Eligible(__instance));
        }
    }
}
