using System.Text;
using System.Text.Json;
using PeakWorkshop.Plugin;
using PeakWorkshop.Protocol;

internal static class FeatureTests
{
    public static List<(string Name,Action Body)> Create() => new()
    {
        ("Losing host capability disables fog controls without reviving on host regain",()=>{
            var values=FeatureSettings.Defaults();
            foreach(var value in values) if(value.id=="fog.stop" || value.id=="fog.speed" || value.id=="speed") value.enabled=true;
            var initial=new FeatureSettings(values);
            var lost=initial.DisableUnavailable(id=>!id.StartsWith("fog.",StringComparison.Ordinal),out bool changed);
            Assert(changed && !lost.Enabled("fog.stop") && !lost.Enabled("fog.speed") && lost.SpeedEnabled,"Capability loss not isolated");
            var regained=lost.DisableUnavailable(id=>true,out changed);
            Assert(!changed && !regained.Enabled("fog.stop") && !regained.Enabled("fog.speed"),"Regaining host revived old settings");
            Assert(initial.Enabled("fog.stop"),"Filtering mutated original settings");
        }),
        ("All 51 controls including 15 status pairs fit in one protocol frame", () => {
            Assert(FeatureCatalog.All.Length==51,"Missing catalog features");
            Assert(FeatureCatalog.StatusNames.Length==15,"Status schema changed");
            Assert(FeatureCatalog.All.Select(d=>d.Id).Distinct(StringComparer.Ordinal).Count()==51,"Duplicate feature IDs");
            var values=FeatureSettings.Defaults();
            foreach(var value in values) { value.enabled=true;value.available=true;value.reason="支持：当前程序集方法已确认，等待游戏测试。"; }
            var message=new WireMessage{features=values};
            var options=new JsonSerializerOptions{IncludeFields=true};
            byte[] raw=JsonSerializer.SerializeToUtf8Bytes(message,options);
            Assert(raw.Length<WireProtocol.MaxMessageBytes,"Full state exceeds bounded transport");
            var received=JsonSerializer.Deserialize<WireMessage>(raw,options)!;
            Assert(new FeatureSettings(received.features).AnyEnabled,"Feature array lost in JSON");
            Assert(received.features.Select(v=>v.id).SequenceEqual(values.Select(v=>v.id)),"Features reordered or missing");
        }),
        ("Every feature defaults off and reset contains no enabled feature",()=>{
            Assert(!FeatureSettings.Disabled.AnyEnabled,"Reset enabled features");
            foreach(var v in FeatureSettings.Disabled.ToWire()) Assert(!v.enabled,"Default on: "+v.id);
        }),
        ("All per-feature bounds reject nonfinite and invalid values even when disabled",()=>{
            foreach(var d in FeatureCatalog.All)
                foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,d.Min-1,d.Max+1})
                { var values=FeatureSettings.Defaults();Array.Find(values,v=>v.id==d.Id)!.value=invalid;Reject(()=>new FeatureSettings(values)); }
        }),
        ("Raised movement and gesture limits accept original minima and new maxima with adjacent values rejected",()=>{
            foreach(var limit in new[]{("speed",0.5f,200f),("climb.speed",0.2f,200f),("jump",0.2f,200f),("fall.threshold",0.1f,200f),("ping.size",0.1f,50f)})
            {
                var definition=FeatureCatalog.Find(limit.Item1)!;
                Assert(definition.Min==limit.Item2 && definition.Max==limit.Item3,"Requested range changed: "+limit.Item1);
                foreach(bool enabled in new[]{false,true})
                {
                    foreach(float boundary in new[]{limit.Item2,limit.Item3})
                    {
                        var values=FeatureSettings.Defaults();var value=Array.Find(values,v=>v.id==limit.Item1)!;
                        value.value=boundary;value.enabled=enabled;
                        var state=new FeatureSettings(values);
                        var actual=Array.Find(state.ToWire(),v=>v.id==limit.Item1)!;
                        Assert(actual.value==boundary && actual.enabled==enabled,"Boundary lost: "+limit.Item1);
                    }
                    foreach(float invalid in new[]{MathF.BitDecrement(limit.Item2),MathF.BitIncrement(limit.Item3)})
                    {
                        var values=FeatureSettings.Defaults();var value=Array.Find(values,v=>v.id==limit.Item1)!;
                        value.value=invalid;value.enabled=enabled;Reject(()=>new FeatureSettings(values));
                    }
                }
            }
        }),
        ("Malformed full states reject missing, duplicate, null, and unknown IDs",()=>{
            Reject(()=>new FeatureSettings((FeatureValue[])null!));
            Reject(()=>new FeatureSettings(FeatureSettings.Defaults()[1..]));
            var values=FeatureSettings.Defaults();values[0]=null!;Reject(()=>new FeatureSettings(values));
            values=FeatureSettings.Defaults();values[0].id=values[1].id;Reject(()=>new FeatureSettings(values));
            values=FeatureSettings.Defaults();values[0].id="unknown";Reject(()=>new FeatureSettings(values));
        }),
        ("Settings isolate mutable client and snapshot arrays",()=>{
            var source=FeatureSettings.Defaults();source[0].enabled=true;source[0].value=2;
            var state=new FeatureSettings(source);source[0].enabled=false;source[0].value=3;
            Assert(state.SpeedEnabled && state.SpeedMultiplier==2,"Input array mutated active settings");
            var snapshot=state.ToWire();snapshot[0].enabled=false;snapshot[0].value=0.5f;
            Assert(state.SpeedEnabled && state.SpeedMultiplier==2,"Snapshot mutated active settings");
        }),
        ("Enabling each control leaves every other control disabled",()=>{
            foreach(var d in FeatureCatalog.All)
            { var values=FeatureSettings.Defaults();Array.Find(values,v=>v.id==d.Id)!.enabled=true;var state=new FeatureSettings(values);
              foreach(var other in FeatureCatalog.All)Assert(state.Enabled(other.Id)==(other.Id==d.Id),"Cross-enabled "+other.Id); }
        }),
        ("Client capability flags cannot mutate feature settings",()=>{
            var values=FeatureSettings.Defaults();foreach(var v in values){v.available=true;v.reason="forged";}
            var state=new FeatureSettings(values);Assert(state.ToWire().All(v=>!v.available && v.reason==""),"Client capability flags retained");
        })
    };
    private static void Assert(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Reject(Action create){try{create();}catch(ArgumentException){return;}throw new Exception("Invalid state accepted");}
}
