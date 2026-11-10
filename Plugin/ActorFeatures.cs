using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Plugin
{
    internal sealed partial class GameAdapter
    {
        private readonly Dictionary<Type, FieldInfo> _actorOwners = new Dictionary<Type, FieldInfo>();
        private readonly Dictionary<FieldInfo, MethodInfo> _actorReadHelpers = new Dictionary<FieldInfo, MethodInfo>();
        private Type _actorCharacterType;
        private FieldInfo _actorCharacterRefs, _actorRefsAfflictions;
        private FieldInfo _actorDataClimbing, _actorDataRope, _actorDataVine;
        private MethodInfo _actorGetStatus, _actorUpdateWeight;
        private PropertyInfo _actorPetrifyAmount;
        private bool _actorRestoring;
        [ThreadStatic] private static int _actorClimbDepth;
        [ThreadStatic] private static int _actorStatusDepth;

        private void InitializeActor(Assembly assembly, Harmony harmony)
        {
            _actorCharacterType = assembly.GetType("Character", true);
            var movement = assembly.GetType("CharacterMovement", true);
            var climbing = assembly.GetType("CharacterClimbing", true);
            var rope = assembly.GetType("CharacterRopeHandling", true);
            var vine = assembly.GetType("CharacterVineClimbing", true);
            var data = assembly.GetType("CharacterData", true);
            var afflictions = assembly.GetType("CharacterAfflictions", true);
            var status = afflictions.GetNestedType("STATUSTYPE", All)
                ?? throw new MissingMemberException("CharacterAfflictions.STATUSTYPE");
            foreach (var type in new[] { movement, climbing, rope, vine, data, afflictions })
            {
                var owner = type.GetField("character", All);
                if (owner != null && owner.FieldType == _actorCharacterType) _actorOwners[type] = owner;
            }
            _actorCharacterRefs = _actorCharacterType.GetField("refs", All);
            _actorRefsAfflictions = _actorCharacterRefs?.FieldType.GetField("afflictions", All);
            _actorDataClimbing = data.GetField("isClimbing", All);
            _actorDataRope = data.GetField("isRopeClimbing", All);
            _actorDataVine = data.GetField("isVineClimbing", All);
            _actorPetrifyAmount = data.GetProperty("petrifyAmount", All);

            bool speed = ActorPatch(harmony, ActorMethod(movement, "GetMovementForce", typeof(float)), postfix: nameof(MovementPostfix));
            ActorCapability("speed", speed);
            bool stamina = ActorPatch(harmony, _actorCharacterType.GetProperty("infiniteStam", All)?.GetGetMethod(true), postfix: nameof(StaminaPostfix));
            ActorCapability("stamina", stamina);
            bool consumption = ActorPatch(harmony, ActorMethod(_actorCharacterType, "UseStamina", typeof(bool), typeof(float), typeof(bool), typeof(bool)), prefix: nameof(ActorUseStaminaPrefix));
            ActorCapability("stamina.consume", consumption);
            ActorCapability("stamina.regen", ActorPatch(harmony, ActorMethod(_actorCharacterType, "AddStamina", typeof(void), typeof(float)), prefix: nameof(ActorRegenPrefix)));

            var delay = ActorMethod(_actorCharacterType, "CanRegenStamina", typeof(bool));
            bool delaySupported = delay != null && ActorOriginalInstructions(delay).Count(i => i.opcode == OpCodes.Stloc_0) == 1;
            ActorCapability("stamina.delay", delaySupported && ActorPatch(harmony, delay, transpiler: nameof(ActorDelayTranspiler)));
            ActorCapability("fall.threshold", ActorPatch(harmony, ActorMethod(movement, "FallTime", typeof(float)), postfix: nameof(ActorFallTimePostfix)));
            ActorCapability("fall.disabled", ActorPatch(harmony, ActorMethod(_actorCharacterType, "Fall", typeof(void), typeof(float), typeof(float)), prefix: nameof(ActorFallPrefix)));

            var climbSpeed = climbing.GetField("climbSpeed", All);
            var ropeSpeed = rope.GetField("climbSpeed", All);
            var vineSpeed = vine.GetField("climbSpeed", All);
            var slippy = data.GetField("slippy", All);
            var onlySlide = assembly.GetType("ClimbModifierSurface", true).GetField("onlySlideDown", All);
            ActorRegisterRead(climbSpeed, nameof(ActorScaleClimbSpeed));
            ActorRegisterRead(ropeSpeed, nameof(ActorScaleClimbSpeed));
            ActorRegisterRead(vineSpeed, nameof(ActorScaleClimbSpeed));
            ActorRegisterRead(slippy, nameof(ActorReadSlippy));
            ActorRegisterRead(onlySlide, nameof(ActorReadOnlySlideDown));
            var requested = ActorMethod(climbing, "GetRequestedPostition", typeof(UnityEngine.Vector3));
            var ropeUpdate = ActorMethod(rope, "Update", typeof(void));
            var vineFixed = ActorMethod(vine, "FixedUpdate", typeof(void));
            bool requestedPatched = ActorContainsRead(requested, climbSpeed) && ActorContainsRead(requested, slippy) && ActorContainsRead(requested, onlySlide)
                && ActorPatch(harmony, requested, prefix: nameof(ActorClimbScopePrefix), transpiler: nameof(ActorReadTranspiler), finalizer: nameof(ActorClimbScopeFinalizer));
            bool ropePatched = ActorContainsRead(ropeUpdate, ropeSpeed) && ActorPatch(harmony, ropeUpdate,
                prefix: nameof(ActorClimbScopePrefix), transpiler: nameof(ActorReadTranspiler), finalizer: nameof(ActorClimbScopeFinalizer));
            bool vinePatched = ActorContainsRead(vineFixed, vineSpeed) && ActorPatch(harmony, vineFixed, transpiler: nameof(ActorReadTranspiler));
            ActorCapability("climb.speed", requestedPatched && ropePatched && vinePatched);
            ActorCapability("slippery.disabled", requestedPatched);

            bool climbConsumption = consumption && ropePatched;
            foreach (var pair in new[]
            {
                Tuple.Create(climbing, "Update"), Tuple.Create(climbing, "RPCA_ClimbJump"),
                Tuple.Create(climbing, "StartClimbRpc"), Tuple.Create(vine, "Update"), Tuple.Create(vine, "GrabVineRpc")
            })
            {
                var method = pair.Item1.GetMethods(All).SingleOrDefault(m => m.Name == pair.Item2 && !m.IsStatic && m.ReturnType == typeof(void));
                climbConsumption &= ActorPatch(harmony, method, prefix: nameof(ActorClimbScopePrefix), finalizer: nameof(ActorClimbScopeFinalizer));
            }
            ActorCapability("climb.consume", climbConsumption && _actorDataClimbing != null && _actorDataRope != null && _actorDataVine != null);

            var jumpImpulse = movement.GetField("jumpImpulse", All);
            ActorRegisterRead(jumpImpulse, nameof(ActorScaleJumpImpulse));
            var jumpMethods = ActorNestedTypes(movement).SelectMany(t => t.GetMethods(All | BindingFlags.DeclaredOnly))
                .Where(m => m.Name == "MoveNext" && m.ReturnType == typeof(bool) && m.DeclaringType.Name.IndexOf("JumpRpc", StringComparison.Ordinal) >= 0 && ActorContainsRead(m, jumpImpulse)).ToArray();
            bool jump = jumpMethods.Length > 0;
            foreach (var method in jumpMethods) jump &= ActorPatch(harmony, method, transpiler: nameof(ActorReadTranspiler));
            ActorCapability("jump", jump);

            _actorGetStatus = ActorMethod(afflictions, "GetCurrentStatus", typeof(float), status);
            _actorUpdateWeight = ActorMethod(afflictions, "UpdateWeight", typeof(void));
            bool statusNames = status.IsEnum && Enum.GetUnderlyingType(status) == typeof(int)
                && FeatureCatalog.StatusNames.SequenceEqual(Enum.GetNames(status));
            bool addStatus = statusNames && ActorPatch(harmony, ActorMethod(afflictions, "AddStatus", typeof(bool), status, typeof(float), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)),
                prefix: nameof(ActorStatusPrefix), finalizer: nameof(ActorStatusFinalizer));
            bool setStatus = statusNames && _actorGetStatus != null && ActorPatch(harmony, ActorMethod(afflictions, "SetStatus", typeof(void), status, typeof(float), typeof(bool)), prefix: nameof(ActorSetStatusPrefix));
            bool petrify = statusNames && _actorPetrifyAmount?.PropertyType == typeof(int)
                && ActorPatch(harmony, ActorMethod(afflictions, "AddPetrify", typeof(void), typeof(int)), prefix: nameof(ActorPetrifyPrefix));
            ActorCapability("status.all.block", addStatus && setStatus && petrify);
            ActorCapability("status.all.scale", addStatus && setStatus && petrify);
            foreach (var name in FeatureCatalog.StatusNames)
            {
                bool available = addStatus && setStatus && (name != "Petrify" || petrify);
                ActorCapability("status." + name + ".block", available);
                ActorCapability("status." + name + ".scale", available);
            }
            ActorCapability("weight.disabled", setStatus && _actorUpdateWeight != null && _actorCharacterRefs != null && _actorRefsAfflictions != null);
        }

        private void ActorCapability(string id, bool available)
        {
            SetAvailable(id, available, available ? "" : "所需游戏成员或补丁未完整建立，请查看插件日志。");
            _log("角色能力 " + id + "：" + (available ? "补丁已建立，待游戏内验证" : "不可用"));
        }

        private static MethodInfo ActorMethod(Type type, string name, Type result, params Type[] parameters)
        {
            var method = type?.GetMethod(name, All, null, parameters, null);
            return method != null && !method.IsStatic && method.ReturnType == result ? method : null;
        }

        private bool ActorPatch(Harmony harmony, MethodInfo method, string prefix = null, string postfix = null, string transpiler = null, string finalizer = null)
        {
            if (method == null) return false;
            try
            {
                harmony.Patch(method,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(GameAdapter).GetMethod(prefix, All)),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(GameAdapter).GetMethod(postfix, All)),
                    transpiler: transpiler == null ? null : new HarmonyMethod(typeof(GameAdapter).GetMethod(transpiler, All)),
                    finalizer: finalizer == null ? null : new HarmonyMethod(typeof(GameAdapter).GetMethod(finalizer, All)),
                    ilmanipulator: null);
                return true;
            }
            catch (Exception ex)
            {
                _log("角色补丁失败 " + method.DeclaringType?.FullName + "." + method.Name + "：" + ex);
                return false;
            }
        }

        private static IEnumerable<Type> ActorNestedTypes(Type type)
        {
            foreach (var child in type.GetNestedTypes(All))
            {
                yield return child;
                foreach (var nested in ActorNestedTypes(child)) yield return nested;
            }
        }

        private static bool ActorContainsRead(MethodInfo method, FieldInfo field)
        {
            return method != null && field != null && ActorOriginalInstructions(method)
                .Any(i => i.opcode == OpCodes.Ldfld && Equals(i.operand, field));
        }

        private static IEnumerable<CodeInstruction> ActorOriginalInstructions(MethodInfo method)
        {
            // HarmonyX exposes ILGenerator in this API's signature. That type is not part of
            // this netstandard target's contract, although it exists in the game's Mono.
            // Invoke the public overload without statically linking ILGenerator or Label.
            var reader = typeof(PatchProcessor).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == "GetOriginalInstructions" && m.GetParameters().Length == 2 && !m.GetParameters()[1].ParameterType.IsByRef);
            return (IEnumerable<CodeInstruction>)reader.Invoke(null, new object[] { method, null });
        }

        private void ActorRegisterRead(FieldInfo field, string helper)
        {
            if (field != null) _actorReadHelpers[field] = typeof(GameAdapter).GetMethod(helper, All);
        }

        private static IEnumerable<CodeInstruction> ActorReadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var adapter = _active;
            foreach (var instruction in instructions)
            {
                if (adapter != null && instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field && adapter._actorReadHelpers.TryGetValue(field, out var helper))
                {
                    // Preserve the owning reference and the game's original field read. The helper
                    // receives (owner, original), so it still passes through if the plugin is stopped.
                    var duplicate = new CodeInstruction(OpCodes.Dup);
                    instruction.MoveLabelsTo(duplicate);
                    instruction.MoveBlocksTo(duplicate);
                    yield return duplicate;
                    yield return instruction;
                    yield return new CodeInstruction(OpCodes.Call, helper);
                }
                else yield return instruction;
            }
        }

        private static IEnumerable<CodeInstruction> ActorDelayTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int found = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Stloc_0)
                {
                    found++;
                    var owner = new CodeInstruction(OpCodes.Ldarg_0);
                    instruction.MoveLabelsTo(owner);
                    instruction.MoveBlocksTo(owner);
                    yield return owner;
                    yield return new CodeInstruction(OpCodes.Call, typeof(GameAdapter).GetMethod(nameof(ActorDelaySeconds), All));
                }
                yield return instruction;
            }
            if (found != 1) throw new InvalidOperationException("体力恢复等待时间 IL 位置不匹配。");
        }

        private object ActorOwner(object component)
        {
            if (component == null) return null;
            if (_actorCharacterType.IsInstanceOfType(component)) return component;
            foreach (var pair in _actorOwners)
                if (pair.Key.IsInstanceOfType(component)) return pair.Value.GetValue(component);
            return null;
        }

        private bool ActorLocal(object component)
        {
            return !_actorRestoring && Eligible(ActorOwner(component));
        }

        private static float ActorScaleClimbSpeed(object component, float original)
        {
            var adapter = _active;
            return adapter != null && adapter._settings.Enabled("climb.speed") && adapter.ActorLocal(component)
                ? original * adapter._settings.Value("climb.speed") : original;
        }

        private static float ActorScaleJumpImpulse(object component, float original)
        {
            var adapter = _active;
            return adapter != null && adapter._settings.Enabled("jump") && adapter.ActorLocal(component)
                ? original * adapter._settings.Value("jump") : original;
        }

        private static float ActorReadSlippy(object data, float original)
        {
            var adapter = _active;
            return adapter != null && adapter._settings.Enabled("slippery.disabled") && adapter.ActorLocal(data) ? 0f : original;
        }

        private static bool ActorReadOnlySlideDown(object surface, bool original)
        {
            var adapter = _active;
            return adapter != null && _actorClimbDepth > 0 && adapter._settings.Enabled("slippery.disabled") ? false : original;
        }

        private static float ActorDelaySeconds(float original, object character)
        {
            var adapter = _active;
            return adapter != null && adapter._settings.Enabled("stamina.delay") && adapter.Eligible(character)
                ? adapter._settings.Value("stamina.delay") : original;
        }

        private static void ActorClimbScopePrefix(object __instance, ref bool __state)
        {
            var adapter = _active;
            __state = adapter != null && adapter.ActorLocal(__instance);
            if (__state) _actorClimbDepth++;
        }

        private static Exception ActorClimbScopeFinalizer(Exception __exception, bool __state)
        {
            if (__state && _actorClimbDepth > 0) _actorClimbDepth--;
            return __exception;
        }

        private static bool ActorUseStaminaPrefix(object __instance, ref float __0, ref bool __result)
        {
            var adapter = _active;
            if (adapter == null || __0 <= 0 || !adapter.Eligible(__instance)) return true;
            float amount = __0 * adapter._settings.Factor("stamina.consume");
            var data = adapter._characterData.GetValue(__instance);
            bool climbing = _actorClimbDepth > 0 || (data != null && (
                (bool)(adapter._actorDataClimbing?.GetValue(data) ?? false) ||
                (bool)(adapter._actorDataRope?.GetValue(data) ?? false) ||
                (bool)(adapter._actorDataVine?.GetValue(data) ?? false)));
            if (climbing) amount *= adapter._settings.Factor("climb.consume");
            __0 = amount;
            if (amount > 0) return true;
            // The game's original UseStamina(0) returns false; a free action must still succeed.
            __result = true;
            return false;
        }

        private static void ActorRegenPrefix(object __instance, ref float __0)
        {
            var adapter = _active;
            if (adapter != null && __0 > 0 && adapter.Eligible(__instance)) __0 *= adapter._settings.Factor("stamina.regen");
        }

        private static void ActorFallTimePostfix(object __instance, ref float __result)
        {
            var adapter = _active;
            if (adapter != null && adapter._settings.Enabled("fall.threshold") && adapter.ActorLocal(__instance))
                __result /= adapter._settings.Value("fall.threshold");
        }

        private static bool ActorFallPrefix(object __instance)
        {
            var adapter = _active;
            return adapter == null || !adapter._settings.Enabled("fall.disabled") || !adapter.Eligible(__instance);
        }

        private float ActorStatusFactor(string name)
        {
            return _settings.Factor("status.all.scale") * _settings.Factor("status." + name + ".scale");
        }

        private bool ActorStatusBlocked(string name)
        {
            return _settings.Enabled("status.all.block") || _settings.Enabled("status." + name + ".block");
        }

        private static bool ActorStatusPrefix(object __instance, object __0, ref float __1, ref bool __result, ref bool __state)
        {
            __state = false;
            var adapter = _active;
            if (adapter == null || !adapter.ActorLocal(__instance)) return true;
            string name = __0.ToString();
            if (__1 > 0)
            {
                if (adapter.ActorStatusBlocked(name) || (name == "Weight" && adapter._settings.Enabled("weight.disabled")))
                {
                    __result = false;
                    return false;
                }
                __1 *= adapter.ActorStatusFactor(name);
                if (__1 <= 0) { __result = false; return false; }
            }
            _actorStatusDepth++;
            __state = true;
            return true;
        }

        private static Exception ActorStatusFinalizer(Exception __exception, bool __state)
        {
            if (__state && _actorStatusDepth > 0) _actorStatusDepth--;
            return __exception;
        }

        private static void ActorSetStatusPrefix(object __instance, object __0, ref float __1)
        {
            var adapter = _active;
            if (adapter == null || !adapter.ActorLocal(__instance)) return;
            string name = __0.ToString();
            if (name == "Weight" && adapter._settings.Enabled("weight.disabled")) { __1 = 0; return; }
            if (_actorStatusDepth > 0) return;
            float current = name == "Petrify" && adapter._actorPetrifyAmount != null
                ? (int)adapter._actorPetrifyAmount.GetValue(adapter._characterData.GetValue(adapter.ActorOwner(__instance))) / 100f
                : (float)adapter._actorGetStatus.Invoke(__instance, new[] { __0 });
            // UpdateWeight repeatedly assigns totals derived from physical inventory/thorns/arrows.
            // Scale those totals directly; scaling their delta every frame would drift or oscillate.
            if (name == "Weight" || name == "Thorns" || name == "Arrow")
            {
                __1 *= adapter.ActorStatusFactor(name);
                if (__1 > current && adapter.ActorStatusBlocked(name)) __1 = current;
                return;
            }
            float increase = __1 - current;
            if (increase > 0) __1 = current + (adapter.ActorStatusBlocked(name) ? 0f : increase * adapter.ActorStatusFactor(name));
        }

        private static void ActorPetrifyPrefix(object __instance, ref int __0)
        {
            var adapter = _active;
            if (__0 <= 0 || _actorStatusDepth > 0 || adapter == null || !adapter.ActorLocal(__instance)) return;
            __0 = adapter.ActorStatusBlocked("Petrify") ? 0 : (int)Math.Min(int.MaxValue, Math.Floor(__0 * (double)adapter.ActorStatusFactor("Petrify")));
        }

        private void ActorSettingsChanged(FeatureSettings previous)
        {
            if (previous.Enabled("weight.disabled") != _settings.Enabled("weight.disabled")) ActorRecalculateWeight(_current, false);
        }

        private void RestoreActorCharacter(object character)
        {
            if (_settings.Enabled("weight.disabled") || _actorRestoring) ActorRecalculateWeight(character, true);
        }

        private void ActorRecalculateWeight(object character, bool restore)
        {
            if (!Alive(character) || _actorUpdateWeight == null) return;
            bool before = _actorRestoring;
            _actorRestoring = restore;
            try
            {
                var refs = _actorCharacterRefs?.GetValue(character);
                var afflictions = refs == null ? null : _actorRefsAfflictions?.GetValue(refs);
                if (Alive(afflictions)) _actorUpdateWeight.Invoke(afflictions, null);
            }
            catch (Exception ex) { _log("重新计算负重失败：" + ex.Message); }
            finally { _actorRestoring = before; }
        }
    }
}
