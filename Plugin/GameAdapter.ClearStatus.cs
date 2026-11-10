using System;
using System.Reflection;
using System.Threading;

namespace PeakWorkshop.Plugin
{
    internal sealed partial class GameAdapter
    {
        private FieldInfo _clearCharacterRefs, _clearRefsAfflictions, _clearAfflictionsOwner, _clearDataOwner;
        private MethodInfo _clearPetrify, _clearThorns, _clearStatuses;
        private int _clearThreadId;

        private void InitializeClearStatus(Assembly assembly)
        {
            var character = assembly.GetType("Character", true);
            var data = assembly.GetType("CharacterData", true);
            var refs = character.GetNestedType("CharacterRefs", All)
                ?? throw new MissingMemberException("Character.CharacterRefs");
            var afflictions = assembly.GetType("CharacterAfflictions", true);
            _clearCharacterRefs = ClearField(character, "refs", refs);
            _clearRefsAfflictions = ClearField(refs, "afflictions", afflictions);
            _clearAfflictionsOwner = ClearField(afflictions, "character", character);
            _clearDataOwner = ClearField(data, "character", character);
            _clearPetrify = ClearMethod(data, "SetPetrify", typeof(int));
            _clearThorns = ClearMethod(afflictions, "RemoveAllThorns");
            _clearStatuses = ClearMethod(afflictions, "ClearAllStatus", typeof(bool), typeof(bool));
            _clearThreadId = Thread.CurrentThread.ManagedThreadId;
            _log("清除当前状态：" + (ClearMembersAvailable ? "游戏原生清除方法已确认，待游戏内验证" : "所需游戏成员不完整，保持不可用"));
        }

        private static FieldInfo ClearField(Type owner, string name, Type expectedType)
        {
            var field = owner.GetField(name, All);
            return field != null && !field.IsStatic && field.FieldType == expectedType ? field : null;
        }

        private static MethodInfo ClearMethod(Type owner, string name, params Type[] parameters)
        {
            var method = owner.GetMethod(name, All, null, parameters, null);
            return method != null && !method.IsStatic && !method.ContainsGenericParameters && method.ReturnType == typeof(void) ? method : null;
        }

        private bool ClearMembersAvailable => _clearCharacterRefs != null && _clearRefsAfflictions != null &&
            _clearAfflictionsOwner != null && _clearDataOwner != null && _clearPetrify != null && _clearThorns != null && _clearStatuses != null;

        public bool ClearStatusAvailable => ClearStatusUnavailableReason.Length == 0;

        public string ClearStatusUnavailableReason
        {
            get
            {
                object data, afflictions;
                return ResolveClearTargets(out data, out afflictions);
            }
        }

        private string ResolveClearTargets(out object data, out object afflictions)
        {
            data = afflictions = null;
            if (!ReferenceEquals(_active, this)) return "插件未完成适配或已停用。";
            if (!ClearMembersAvailable) return "当前游戏版本缺少清除状态所需方法或组件。";
            if (Thread.CurrentThread.ManagedThreadId != _clearThreadId) return "清除状态只能在游戏主线程执行。";
            if (!Ready) return ReadinessMessage;
            try
            {
                if (!Eligible(_current) || !ReferenceEquals(_localCharacter?.GetValue(null), _current))
                    return "本地角色已变化或当前不可操作。";
                data = _characterData?.GetValue(_current);
                var refs = _clearCharacterRefs.GetValue(_current);
                afflictions = refs == null ? null : _clearRefsAfflictions.GetValue(refs);
                if (!Alive(data) || !Alive(afflictions)) return "本地角色状态组件尚未就绪。";
                if (!ReferenceEquals(_clearDataOwner.GetValue(data), _current) ||
                    !ReferenceEquals(_clearAfflictionsOwner.GetValue(afflictions), _current))
                    return "状态组件不属于当前本地角色。";
                return "";
            }
            catch { return "读取本地角色状态组件失败。"; }
        }

        private void RequireCurrentClearTargets(object expectedData, object expectedAfflictions)
        {
            object data, afflictions;
            string reason = ResolveClearTargets(out data, out afflictions);
            if (reason.Length != 0) throw new InvalidOperationException(reason);
            if (!ReferenceEquals(expectedData, data) || !ReferenceEquals(expectedAfflictions, afflictions))
                throw new InvalidOperationException("本地角色或状态组件已变化，本次清除已停止。");
        }

        public void ClearCurrentStatus()
        {
            object data, afflictions;
            string reason = ResolveClearTargets(out data, out afflictions);
            if (reason.Length != 0) throw new InvalidOperationException(reason);
            bool restoring = _actorRestoring;
            try
            {
                // Follow native CharacterAfflictions.ClearAll, with each instance
                // resolved and checked against the current local owner. Never call
                // the static helper that re-reads a global character mid-action.
                _actorRestoring = true;
                _clearPetrify.Invoke(data, new object[] { 0 });
                RequireCurrentClearTargets(data, afflictions);
                _clearThorns.Invoke(afflictions, null);
                RequireCurrentClearTargets(data, afflictions);
                // false/false includes curse and petrification. Native ClearAllStatus
                // deliberately excludes inventory weight and attached-item states.
                _clearStatuses.Invoke(afflictions, new object[] { false, false });
            }
            catch (TargetInvocationException ex)
            {
                _log("清除当前状态失败：" + (ex.InnerException ?? ex));
                throw new InvalidOperationException("清除状态未能全部完成，请检查游戏内结果和插件日志。", ex.InnerException ?? ex);
            }
            finally { _actorRestoring = restoring; }
        }
    }
}
