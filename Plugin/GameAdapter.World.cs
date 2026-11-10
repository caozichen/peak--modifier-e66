using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace PeakWorkshop.Plugin
{
    internal sealed partial class GameAdapter
    {
        private PropertyInfo _worldTrueHolder, _worldHolder, _worldItemPhotonView, _worldViewIsMine;
        private FieldInfo _worldLanternItem, _worldActionItem, _worldOnConsumed;
        private MethodInfo _worldConsumeDelayed;
        private FieldInfo _worldPingCharacter, _worldFogSpeed, _worldRevealAmount, _worldFogRenderer, _worldRevealId;
        private PropertyInfo _worldSession, _worldIsHost, _worldInRoom, _worldPhotonMaster, _worldPhotonInRoom;
        private bool _worldFuelHooked, _worldItemsHooked, _worldPingHooked, _worldFogHooked, _worldRevealHooked;
        private bool _worldFuelTranspiled, _worldFogTranspiled, _worldRevealTranspiled;
        private readonly List<WorldPingState> _worldPings = new List<WorldPingState>();
        private readonly List<WeakReference> _worldRevealed = new List<WeakReference>();
        private const int WorldPingLimit = 64, WorldRevealLimit = 16;

        private sealed class WorldPingState
        {
            internal WeakReference Target;
            internal Vector3 Vanilla, Applied;
        }

        private void InitializeWorld(Assembly assembly, Harmony harmony)
        {
            try
            {
                var item = assembly.GetType("Item", true);
                _worldTrueHolder = item.GetProperty("trueHolderCharacter", All);
                _worldHolder = item.GetProperty("holderCharacter", All);
                _worldItemPhotonView = item.GetProperty("photonView", All);
                _worldViewIsMine = _worldItemPhotonView?.PropertyType.GetProperty("IsMine", All);
                if (_worldTrueHolder == null || _worldHolder == null || _worldItemPhotonView == null || _worldViewIsMine?.PropertyType != typeof(bool))
                    throw new MissingMemberException("缺少道具持有人或网络所有权成员。");
            }
            catch (Exception ex)
            {
                WorldUnavailable(new[] { "fuel.unlimited", "fuel.consume", "items.unlimited" }, ex);
            }

            WorldTry(new[] { "fuel.unlimited", "fuel.consume" }, () =>
            {
                WorldRequireItemOwnership();
                var lantern = assembly.GetType("Lantern", true);
                _worldLanternItem = lantern.GetField("item", All | BindingFlags.DeclaredOnly);
                if (_worldLanternItem == null || _worldLanternItem.FieldType.Name != "Item")
                    throw new MissingFieldException("Lantern.item");
                var update = WorldMethod(lantern, "UpdateFuel", typeof(void));
                WorldPatch(harmony, update, transpiler: nameof(WorldFuelTranspiler));
                if (!_worldFuelTranspiled) throw new InvalidOperationException("燃料消耗指令未匹配。");
                _worldFuelHooked = true;
            });

            WorldTry(new[] { "items.unlimited" }, () =>
            {
                WorldRequireItemOwnership();
                var item = assembly.GetType("Item", true);
                _worldActionItem = assembly.GetType("ItemActionBase", true).GetField("item", All);
                _worldOnConsumed = item.GetField("OnConsumed", All);
                if (_worldActionItem == null || _worldOnConsumed?.FieldType != typeof(Action))
                    throw new MissingMemberException("缺少道具消耗回调成员。");
                var reduce = assembly.GetType("Action_ReduceUses", true);
                // Suppress only locally initiated decrements. Receiving RPC handlers stay
                // vanilla so a different player's legitimate use remains synchronized.
                WorldPatch(harmony, WorldMethod(reduce, "RunAction", typeof(void)), prefix: nameof(WorldReducePrefix));
                _worldConsumeDelayed = WorldMethod(item, "ConsumeDelayed", typeof(IEnumerator), typeof(bool));
                WorldPatch(harmony, _worldConsumeDelayed, prefix: nameof(WorldConsumeDelayedPrefix));
                _worldItemsHooked = true;
            });

            WorldTry(new[] { "ping.size" }, () =>
            {
                var ping = assembly.GetType("PointPing", true);
                _worldPingCharacter = ping.GetField("character", All);
                if (_worldPingCharacter == null || !typeof(Component).IsAssignableFrom(ping))
                    throw new MissingMemberException("PointPing.character/Component");
                WorldPatch(harmony, WorldMethod(ping, "Go", typeof(void)), postfix: nameof(WorldPingPostfix));
                _worldPingHooked = true;
            });

            WorldTry(new[] { "fog.stop", "fog.speed" }, () =>
            {
                var sessionType = assembly.GetType("Peak.Network.ISessionAPI", true);
                _worldSession = assembly.GetType("Peak.Network.NetCode", true).GetProperty("Session", All);
                _worldIsHost = sessionType.GetProperty("IsHost", All);
                _worldInRoom = sessionType.GetProperty("InRoom", All);
                var photon = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Photon.Pun.PhotonNetwork", false)).FirstOrDefault(t => t != null);
                _worldPhotonMaster = photon?.GetProperty("IsMasterClient", All);
                _worldPhotonInRoom = photon?.GetProperty("InRoom", All);
                if (_worldSession == null || _worldIsHost?.PropertyType != typeof(bool) || _worldInRoom?.PropertyType != typeof(bool))
                    throw new MissingMemberException("缺少房主及房间状态成员。");
                var fog = assembly.GetType("OrbFogHandler", true);
                _worldFogSpeed = fog.GetField("speed", All);
                if (_worldFogSpeed?.FieldType != typeof(float)) throw new MissingFieldException("OrbFogHandler.speed");
                WorldPatch(harmony, WorldMethod(fog, "Move", typeof(void)), transpiler: nameof(WorldFogTranspiler));
                if (!_worldFogTranspiled) throw new InvalidOperationException("浓雾推进指令未匹配。");
                _worldFogHooked = true;
            });

            WorldTry(new[] { "fog.reveal" }, () =>
            {
                var sphere = assembly.GetType("FogSphere", true);
                _worldRevealAmount = sphere.GetField("REVEAL_AMOUNT", All);
                _worldFogRenderer = sphere.GetField("rend", All);
                _worldRevealId = sphere.GetField("RevealAmount", All);
                if (_worldRevealAmount?.FieldType != typeof(float) || _worldFogRenderer?.FieldType != typeof(Renderer) ||
                    _worldRevealId?.FieldType != typeof(int) || !_worldRevealId.IsStatic)
                    throw new MissingMemberException("缺少浓雾渲染成员。");
                // Only replace the value read by the shader setter. The game's field,
                // fog progression, cold/status processing and event flags stay intact.
                WorldPatch(harmony, WorldMethod(sphere, "SetSharderVars", typeof(void)), transpiler: nameof(WorldRevealTranspiler));
                if (!_worldRevealTranspiled) throw new InvalidOperationException("浓雾显示指令未匹配。");
                _worldRevealHooked = true;
            });
            TickWorld();
        }

        private void WorldRequireItemOwnership()
        {
            if (_worldTrueHolder == null || _worldHolder == null || _worldItemPhotonView == null || _worldViewIsMine?.PropertyType != typeof(bool))
                throw new MissingMemberException("道具所有权检查不可用。");
        }

        private static MethodInfo WorldMethod(Type type, string name, Type returns, params Type[] args)
        {
            var method = type.GetMethod(name, All, null, args, null);
            if (method == null || method.IsStatic || method.ReturnType != returns)
                throw new MissingMethodException(type.FullName, name);
            return method;
        }

        private static void WorldPatch(Harmony harmony, MethodInfo method, string prefix = null, string postfix = null, string transpiler = null)
        {
            HarmonyMethod Hook(string name) => name == null ? null : new HarmonyMethod(typeof(GameAdapter).GetMethod(name, All));
            harmony.Patch(method, Hook(prefix), Hook(postfix), Hook(transpiler), finalizer: null, ilmanipulator: null);
        }

        private void WorldTry(string[] ids, Action install)
        {
            try { install(); foreach (var id in ids) SetAvailable(id, true); }
            catch (Exception ex) { WorldUnavailable(ids, ex); }
        }

        private void WorldUnavailable(string[] ids, Exception ex)
        {
            var reason = "游戏补丁未安装：" + ex.GetBaseException().Message;
            foreach (var id in ids) SetAvailable(id, false, reason);
            _log(string.Join(",", ids) + "：" + reason);
        }

        private bool WorldOwnsItem(object item)
        {
            if (!Alive(item)) return false;
            try
            {
                if (!Eligible(_worldTrueHolder.GetValue(item))) return false;
                var view = _worldItemPhotonView.GetValue(item);
                return Alive(view) && (bool)_worldViewIsMine.GetValue(view);
            }
            catch { return false; }
        }

        private bool WorldUnlimitedItem(object item)
        {
            if (!_worldItemsHooked || !_settings.Enabled("items.unlimited") || !WorldOwnsItem(item)) return false;
            try { return ReferenceEquals(_worldHolder.GetValue(item), _worldTrueHolder.GetValue(item)); }
            catch { return false; }
        }

        private static IEnumerable<CodeInstruction> WorldFuelTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var delta = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            var matches = list.Select((instruction, index) => new { instruction, index })
                .Where(p => p.instruction.Calls(delta)).Select(p => p.index).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Lantern.UpdateFuel deltaTime 数量不匹配。");
            list.InsertRange(matches[0] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GameAdapter), nameof(WorldFuelDelta))) });
            if (_active != null) _active._worldFuelTranspiled = true;
            return list;
        }

        private static float WorldFuelDelta(float vanilla, object lantern)
        {
            var adapter = _active;
            if (adapter == null || !adapter._worldFuelHooked) return vanilla;
            try
            {
                if (!adapter.WorldOwnsItem(adapter._worldLanternItem.GetValue(lantern))) return vanilla;
                if (adapter._settings.Enabled("fuel.unlimited")) return 0f;
                return vanilla * adapter._settings.Factor("fuel.consume");
            }
            catch { return vanilla; }
        }

        private static bool WorldReducePrefix(object __instance)
        {
            var adapter = _active;
            if (adapter == null || !adapter._worldItemsHooked) return true;
            try { return !adapter.WorldUnlimitedItem(adapter._worldActionItem.GetValue(__instance)); }
            catch { return true; }
        }

        private static bool WorldConsumeDelayedPrefix(object __instance, bool __0, ref IEnumerator __result)
        {
            var adapter = _active;
            if (adapter == null || !adapter.WorldUnlimitedItem(__instance)) return true;
            // The original iterator sets consuming=true permanently and broadcasts Consume
            // one frame later. Keep its callback and one-frame timing, but send no removal RPC.
            __result = adapter.WorldUseWithoutConsuming(__instance, __0);
            return false;
        }

        private IEnumerator WorldUseWithoutConsuming(object item, bool ignoreActions)
        {
            if (!Alive(item)) yield break;
            if (!WorldUnlimitedItem(item))
            {
                // StartCoroutine normally advances immediately. A caller may nevertheless
                // save an iterator until after disable or an ownership change.
                var vanilla = (IEnumerator)_worldConsumeDelayed.Invoke(item, new object[] { ignoreActions });
                try { while (vanilla.MoveNext()) yield return vanilla.Current; }
                finally { (vanilla as IDisposable)?.Dispose(); }
                yield break;
            }
            // Do not set consuming: the vanilla flag has no reset and also controls the
            // temporary feed-recipient override. Nothing persistent needs restoration.
            if (!ignoreActions) (_worldOnConsumed.GetValue(item) as Action)?.Invoke();
            yield return null;
        }

        private static void WorldPingPostfix(object __instance)
        {
            var adapter = _active;
            if (adapter == null || !adapter._worldPingHooked || !adapter._settings.Enabled("ping.size")) return;
            try
            {
                if (!adapter.Eligible(adapter._worldPingCharacter.GetValue(__instance))) return;
                var component = __instance as Component;
                if (component == null) return;
                var transform = component.transform;
                var vanilla = transform.localScale;
                var applied = vanilla * adapter._settings.Value("ping.size");
                var state = adapter._worldPings.FirstOrDefault(p => ReferenceEquals(p.Target.Target, __instance));
                if (state == null)
                {
                    if (adapter._worldPings.Count >= WorldPingLimit)
                    {
                        adapter.WorldRestorePing(adapter._worldPings[0]);
                        adapter._worldPings.RemoveAt(0);
                    }
                    state = new WorldPingState { Target = new WeakReference(__instance) };
                    adapter._worldPings.Add(state);
                }
                state.Vanilla = vanilla;
                state.Applied = applied;
                transform.localScale = applied;
            }
            catch { /* Destroyed pings leave vanilla rendering intact. */ }
        }

        private static IEnumerable<CodeInstruction> WorldFogTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var list = instructions.ToList();
            var speed = __originalMethod.DeclaringType.GetField("speed", All);
            var matches = list.Select((instruction, index) => new { instruction, index })
                .Where(p => p.instruction.opcode == OpCodes.Ldfld && Equals(p.instruction.operand, speed)).Select(p => p.index).ToArray();
            if (speed == null || matches.Length != 1) throw new InvalidOperationException("OrbFogHandler.Move speed 数量不匹配。");
            list.InsertRange(matches[0] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GameAdapter), nameof(WorldFogSpeed))) });
            if (_active != null) _active._worldFogTranspiled = true;
            return list;
        }

        private bool WorldIsHost()
        {
            try
            {
                var session = _worldSession?.GetValue(null);
                if (session != null)
                    return (bool)_worldInRoom.GetValue(session) && (bool)_worldIsHost.GetValue(session);
                return _worldPhotonMaster != null && _worldPhotonInRoom != null &&
                    (bool)_worldPhotonInRoom.GetValue(null) && (bool)_worldPhotonMaster.GetValue(null);
            }
            catch { return false; }
        }

        private static float WorldFogSpeed(float vanilla, object fog)
        {
            var adapter = _active;
            if (adapter == null || !adapter._worldFogHooked || !Alive(fog) ||
                !adapter.Eligible(adapter._current) || !adapter.WorldIsHost()) return vanilla;
            if (adapter._settings.Enabled("fog.stop")) return 0f;
            return vanilla * adapter._settings.Factor("fog.speed");
        }

        private static IEnumerable<CodeInstruction> WorldRevealTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var list = instructions.ToList();
            var reveal = __originalMethod.DeclaringType.GetField("REVEAL_AMOUNT", All);
            var matches = list.Select((instruction, index) => new { instruction, index })
                .Where(p => p.instruction.opcode == OpCodes.Ldfld && Equals(p.instruction.operand, reveal)).Select(p => p.index).ToArray();
            if (reveal == null || matches.Length != 1) throw new InvalidOperationException("FogSphere.SetSharderVars reveal 数量不匹配。");
            list.InsertRange(matches[0] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GameAdapter), nameof(WorldRevealValue))) });
            if (_active != null) _active._worldRevealTranspiled = true;
            return list;
        }

        private static float WorldRevealValue(float vanilla, object sphere)
        {
            var adapter = _active;
            if (adapter == null || !adapter._worldRevealHooked || !adapter._settings.Enabled("fog.reveal") ||
                !adapter.Eligible(adapter._current) || !Alive(sphere)) return vanilla;
            if (!adapter._worldRevealed.Any(p => ReferenceEquals(p.Target, sphere)))
            {
                if (adapter._worldRevealed.Count >= WorldRevealLimit)
                {
                    adapter.WorldRestoreReveal(adapter._worldRevealed[0].Target);
                    adapter._worldRevealed.RemoveAt(0);
                }
                adapter._worldRevealed.Add(new WeakReference(sphere));
            }
            return 1f;
        }

        private void WorldRestorePing(WorldPingState state)
        {
            try
            {
                var component = state.Target.Target as Component;
                if (component != null && component.transform.localScale == state.Applied)
                    component.transform.localScale = state.Vanilla;
            }
            catch { }
        }

        private bool WorldPingEligible(object ping)
        {
            if (!Alive(ping)) return false;
            try { return Eligible(_worldPingCharacter.GetValue(ping)); }
            catch { return false; }
        }

        private void WorldRestoreReveal(object sphere)
        {
            if (!Alive(sphere)) return;
            try
            {
                var renderer = _worldFogRenderer.GetValue(sphere) as Renderer;
                if (renderer == null) return;
                int id = (int)_worldRevealId.GetValue(null);
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                if (!Mathf.Approximately(block.GetFloat(id), 1f)) return;
                block.SetFloat(id, (float)_worldRevealAmount.GetValue(sphere));
                renderer.SetPropertyBlock(block);
            }
            catch { }
        }

        private void TickWorld()
        {
            bool eligible = Eligible(_current);
            bool keepPing = eligible && _worldPingHooked && _settings.Enabled("ping.size");
            for (int i = _worldPings.Count - 1; i >= 0; i--)
            {
                if (keepPing && WorldPingEligible(_worldPings[i].Target.Target)) continue;
                WorldRestorePing(_worldPings[i]);
                _worldPings.RemoveAt(i);
            }
            bool keepReveal = eligible && _worldRevealHooked && _settings.Enabled("fog.reveal");
            for (int i = _worldRevealed.Count - 1; i >= 0; i--)
            {
                if (keepReveal && Alive(_worldRevealed[i].Target)) continue;
                WorldRestoreReveal(_worldRevealed[i].Target);
                _worldRevealed.RemoveAt(i);
            }
            if (_worldFogHooked)
            {
                bool host = WorldIsHost();
                SetAvailable("fog.stop", host, "浓雾推进仅可由当前房主控制。");
                SetAvailable("fog.speed", host, "浓雾推进仅可由当前房主控制。");
            }
        }

        private void ResetWorld()
        {
            foreach (var state in _worldPings) WorldRestorePing(state);
            _worldPings.Clear();
            foreach (var sphere in _worldRevealed) WorldRestoreReveal(sphere.Target);
            _worldRevealed.Clear();
        }

        private void DisposeWorld()
        {
            ResetWorld();
            _worldFuelHooked = _worldItemsHooked = _worldPingHooked = _worldFogHooked = _worldRevealHooked = false;
        }
    }
}
