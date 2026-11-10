using System.Diagnostics;
using PeakWorkshop.Protocol;
using UnityEngine.SceneManagement;
using static Checks;

internal static class ControllerRegression
{
    internal static IEnumerable<(string Name,Action Body)> Create()
    {
        yield return ("Airport to map to airport preserves every selected feature and parameter",()=>{
            var x=New();var request=Request(x.Plugin);foreach(var value in request.features){value.enabled=true;value.value=FeatureCatalog.Find(value.id).Max;}
            Assert(Handle(x.Plugin,request).ok,"Initial configuration rejected");
            var expected=x.Adapter.Settings.ToWire().Select(v=>(v.id,v.enabled,v.value)).ToArray();
            int scene=10;
            foreach(string name in new[]{"Airport","Loading","Level_0","Loading","Airport"})
            {
                SceneManager.Current=new Scene{handle=scene++,name=name};x.Adapter.Changed=true;x.Adapter.Ready=name!="Loading";Tick(x.Plugin);
                Assert(x.Adapter.Settings.ToWire().Select(v=>(v.id,v.enabled,v.value)).SequenceEqual(expected),"Scene reset selected values: "+name);
                Assert(x.Adapter.Effective.AnyEnabled==x.Adapter.Ready,"Effects did not follow actor readiness: "+name);
                var reply=Dispatch(x.Plugin,new WireMessage()).Reply;
                Assert(reply.features.Select(v=>(v.id,v.enabled,v.value)).SequenceEqual(expected),"JSON snapshot lost selections: "+name);
            }
        });
        yield return ("Same window query resumes after disconnect while idle ticks keep effects suspended",()=>{
            var x=New();Handle(x.Plugin,Request(x.Plugin));Set(x.Plugin,"_disconnectRequested",1);Tick(x.Plugin);
            for(int i=0;i<8;i++)Tick(x.Plugin);
            Assert(x.Adapter.Settings.SpeedEnabled&&!x.Adapter.Effective.AnyEnabled,"Disconnected effects resumed without a query");
            var state=Handle(x.Plugin,new WireMessage());
            Assert(state.features.First(v=>v.id=="speed").enabled&&x.Adapter.Effective.SpeedEnabled,"Reconnected window lost selection or effect");
        });
        yield return ("Same window identity survives reconnection and a different window starts disabled",()=>{
            var x=New();var a=Handle(x.Plugin,new WireMessage{controllerId="window-a"});
            var set=Request(x.Plugin);set.features[0].value=13.6f;Assert(Handle(x.Plugin,set).ok,"Window A cannot enable");
            Set(x.Plugin,"_disconnectRequested",1);Tick(x.Plugin);
            var recovered=Handle(x.Plugin,new WireMessage{controllerId="window-a"});
            Assert(recovered.speedEnabled&&recovered.speedMultiplier==13.6f&&x.Adapter.Effective.SpeedEnabled,"Same window recovery lost values");
            var fresh=Handle(x.Plugin,new WireMessage{controllerId="window-b"});
            Assert(!fresh.speedEnabled&&!x.Adapter.Settings.AnyEnabled&&!x.Adapter.Effective.AnyEnabled,"New window inherited previous window's enables");
            Assert(!Handle(x.Plugin,set).ok,"Old window mutation accepted after ownership changed");
        });
        yield return ("Manual reset during loading permanently clears the retained configuration",()=>{
            var x=New();Handle(x.Plugin,Request(x.Plugin));x.Adapter.Ready=false;Tick(x.Plugin);
            Assert(x.Adapter.Settings.SpeedEnabled&&!x.Adapter.Effective.AnyEnabled,"Loading did not retain configuration");
            var reset=Handle(x.Plugin,Request(x.Plugin,"reset"));Assert(reset.ok&&!x.Adapter.Settings.AnyEnabled,"Manual reset failed while loading");
            x.Adapter.Ready=true;Tick(x.Plugin);Assert(!x.Adapter.Settings.AnyEnabled&&!x.Adapter.Effective.AnyEnabled,"Cleared configuration returned on actor recovery");
        });
        yield return ("Manual reset invalidates previously queued changes before effects can return",()=>{
            var x=New();var stale=Request(x.Plugin);Handle(x.Plugin,stale);
            Handle(x.Plugin,Request(x.Plugin,"reset"));
            Assert(!Handle(x.Plugin,stale,stale.stateRevision).ok&&!x.Adapter.Settings.AnyEnabled&&!x.Adapter.Effective.AnyEnabled,"A queued setting restored a manually cleared choice");
        });
        yield return ("Retained unavailable choices do not prevent editing another available feature",()=>{
            var x=New();Handle(x.Plugin,Request(x.Plugin,"set","fog.stop"));x.Adapter.Unavailable.Add("fog.stop");Tick(x.Plugin);
            var request=Request(x.Plugin);request.features=x.Adapter.Settings.ToWire();request.features.First(v=>v.id=="speed").enabled=true;
            Assert(Handle(x.Plugin,request).ok&&x.Adapter.Settings.Enabled("fog.stop")&&x.Adapter.Effective.SpeedEnabled&&!x.Adapter.Effective.Enabled("fog.stop"),"Retained capability blocked an unrelated edit");
        });
        yield return ("Timeout invalidates old commands and polling restores selected settings",()=>{
            var x=New();var stale=Request(x.Plugin);Handle(x.Plugin,stale);Set(x.Plugin,"_lastSeen",Stopwatch.GetTimestamp()-4*Stopwatch.Frequency);Tick(x.Plugin);
            Assert(x.Adapter.Settings.SpeedEnabled&&!x.Adapter.Effective.SpeedEnabled&&!Handle(x.Plugin,stale).ok,"Timeout lost choice, kept effects or replayed old command");
            Handle(x.Plugin,new WireMessage());Assert(x.Adapter.Effective.SpeedEnabled,"Timeout recovery failed");
        });
    }
}
