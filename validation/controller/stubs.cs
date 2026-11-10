using System.Reflection;
using System.Text.Json;
using PeakWorkshop.Protocol;

namespace BepInEx
{
    public class BepInPlugin:Attribute { public BepInPlugin(string id,string name,string version){} }
    public class BaseUnityPlugin { public bool enabled=true; public Logger Logger=new(); }
    public class Logger { public readonly List<string> Entries=new(); public void LogInfo(object text)=>Entries.Add(text.ToString());public void LogWarning(object text)=>Entries.Add(text.ToString());public void LogError(object text)=>Entries.Add(text.ToString()); }
}
namespace UnityEngine
{
    public static class Application { public static string version="test",unityVersion="managed substitute"; }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { public int handle; public string name; }
    public static class SceneManager { public static Scene Current=new(){handle=1,name="test"};public static Scene GetActiveScene()=>Current; }
}
namespace HarmonyLib
{
    public class PatchInfo { public List<string> Owners=new(); }
    public class Harmony
    {
        public readonly string Id;public int UnpatchCalls; public static readonly PatchInfo Info=new();
        public Harmony(string id){Id=id;}
        public void UnpatchSelf(){UnpatchCalls++;Info.Owners.Remove(Id);}
        public static IEnumerable<MethodBase> GetAllPatchedMethods()=>Info.Owners.Count>0 ? new[]{typeof(Harmony).GetMethod("UnpatchSelf")} : Array.Empty<MethodBase>();
        public static PatchInfo GetPatchInfo(MethodBase method)=>Info;
    }
}
namespace PeakWorkshop.Plugin
{
    internal class GameAdapter
    {
        public FeatureSettings Settings=FeatureSettings.Disabled; public bool Ready=true,Changed,Disposed,ResetThrows; public int Resets, Suspends; public bool EffectsAllowed; public FeatureSettings Effective=FeatureSettings.Disabled;
        public string ReadinessMessage => Ready ? "本地角色就绪。" : "正在等待关卡初始化完成。";
        public bool ClearStatusSupported=true, ClearStatusFails; public int ClearStatusCalls;
        public bool ClearStatusAvailable=>!Disposed && Ready && ClearStatusSupported;
        public string ClearStatusUnavailableReason=>Disposed ? "插件已停用。" : !Ready ? ReadinessMessage : !ClearStatusSupported ? "missing status method" : "";
        public void ClearCurrentStatus(){ClearStatusCalls++;if(ClearStatusFails)throw new InvalidOperationException("status clear fault");}
        public HashSet<string> Unavailable=new(); public bool SpeedAvailable=>IsAvailable("speed"); public bool StaminaAvailable=>IsAvailable("stamina");public string CompatibilityMessage=>"test";
        public GameAdapter(Action<string> log){}
        public void Initialize(HarmonyLib.Harmony harmony){}
        public bool PollCharacter(){bool changed=Changed;Changed=false;return changed;}
        public void Tick(){RefreshEffects(EffectsAllowed);}
        public bool IsAvailable(string id)=>!Disposed && !Unavailable.Contains(id);
        public string AvailabilityReason(string id)=>"test unavailable";
        public FeatureValue[] SnapshotFeatures(bool stopped){var values=Settings.ToWire();foreach(var value in values)value.available=!stopped&&IsAvailable(value.id);return values;}
        public void Apply(FeatureSettings value){Settings=value;RefreshEffects(EffectsAllowed);}
        public void Reset(){Resets++;Settings=FeatureSettings.Disabled;SuspendEffects();if(ResetThrows)throw new InvalidOperationException("restore fault");}
        public void Dispose(){Disposed=true;Settings=FeatureSettings.Disabled;SuspendEffects();}
        public void SuspendEffects(){Suspends++;EffectsAllowed=false;Effective=FeatureSettings.Disabled;} public void RefreshEffects(bool allowed){EffectsAllowed=allowed&&Ready&&!Disposed;Effective=EffectsAllowed?Settings.DisableUnavailable(IsAvailable,out _):FeatureSettings.Disabled;}
    }
}
