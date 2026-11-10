using System.Reflection;
using PeakWorkshop.Plugin;
using PeakWorkshop.Protocol;
using UnityEngine.SceneManagement;
using static Program;

internal static class AdapterRegression
{
    private sealed class Movement { public TestCharacter character; }
    private static float Move(GameAdapter adapter,TestCharacter actor,float original=7f)
    {
        Set(adapter,"_active",adapter);
        Set(adapter,"_movementCharacter",typeof(Movement).GetField("character"));
        object[] args={new Movement{character=actor},original};
        typeof(GameAdapter).GetMethod("MovementPostfix",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,args);
        return (float)args[1];
    }
    private static FeatureSettings Choices()
    {
        var values=FeatureSettings.Defaults();
        foreach(var value in values)
        {
            value.enabled=true;
            if(value.id=="speed")value.value=13.6f;
            if(value.id=="climb.speed")value.value=17.4f;
            if(value.id=="jump")value.value=10.2f;
        }
        return new FeatureSettings(values);
    }
    internal static void Run()
    {
        Check("Production movement calculation pauses without clearing selected settings",()=>{
            var (adapter,actor,_)=Fixture();var choices=Choices();adapter.Apply(choices);
            Require(Move(adapter,actor)==7f*13.6f,"Enabled movement did not use selected multiplier");
            adapter.SuspendEffects();Require(ReferenceEquals(adapter.Settings,choices)&&!adapter.EffectsActive,"Suspension cleared choices or left effects active");
            Require(Move(adapter,actor)==7f,"Suspended movement still modified calculation");
            adapter.RefreshEffects(true);Require(Move(adapter,actor)==7f*13.6f,"Resume did not restore original multiplier");
        });
        Check("Production adapter survives repeated airport-map-airport actor replacements",()=>{
            var (adapter,actor,_)=Fixture();var choices=Choices();adapter.Apply(choices);
            for(int trip=0;trip<40;trip++)
            {
                adapter.SuspendEffects();TestCharacter.localCharacter=null;adapter.PollCharacter();adapter.RefreshEffects(true);adapter.Tick();
                Require(ReferenceEquals(adapter.Settings,choices)&&!adapter.EffectsActive,"Actor unload lost selected state");
                foreach(string scene in new[]{"Level_0","Airport"})
                {
                    SceneManager.ActiveSceneName=scene;actor=new TestCharacter{StatisticsInitialized=scene!="Airport"};TestCharacter.localCharacter=actor;
                    adapter.PollCharacter();adapter.RefreshEffects(true);adapter.Tick();
                    Require(adapter.Ready&&adapter.EffectsActive&&Move(adapter,actor)==7f*13.6f,"Replacement did not restore effective movement: "+scene);
                    Require(ReferenceEquals(adapter.Settings,choices)&&adapter.Settings.Value("climb.speed")==17.4f&&adapter.Settings.Value("jump")==10.2f,"Other selected multipliers changed");
                }
            }
        });
        Check("Production suspension restores old actor weight and resume recalculates new actor weight",()=>{
            var (adapter,oldActor,_)=Fixture();adapter.Apply(Choices());adapter.WeightCalls.Clear();
            var next=new TestCharacter();TestCharacter.localCharacter=next;adapter.PollCharacter();
            Require(adapter.WeightCalls.Any(v=>ReferenceEquals(v.Actor,oldActor)&&v.Restore),"Old actor weight was not restored");
            Require(!adapter.EffectsActive,"New actor inherited effects before refresh");
            adapter.RefreshEffects(true);Require(adapter.WeightCalls.Any(v=>ReferenceEquals(v.Actor,next)&&!v.Restore),"New actor weight was not recalculated");
        });
        Check("Death and recovery preserve choices and prevent modification while the actor is dead",()=>{
            var (adapter,actor,_)=Fixture();adapter.Apply(Choices());actor.data.IsDead=true;adapter.PollCharacter();adapter.RefreshEffects(true);
            Require(!adapter.Ready&&!adapter.EffectsActive&&adapter.Settings.AnyEnabled&&Move(adapter,actor)==7f,"Dead actor effects or choice handling failed");
            actor.data.IsDead=false;adapter.PollCharacter();adapter.RefreshEffects(true);
            Require(adapter.Ready&&Move(adapter,actor)==7f*13.6f,"Recovery did not reapply selected values");
        });
        Check("Permission loss suspends only affected effects while the selected switch stays on",()=>{
            var (adapter,actor,_)=Fixture();adapter.Apply(Choices());adapter.TestAvailable("fog.stop",false);adapter.Tick();
            Require(adapter.Settings.Enabled("fog.stop")&&!adapter.Effective.Enabled("fog.stop")&&Move(adapter,actor)==7f*13.6f,"Unavailable effect changed another selection");
            Require(adapter.SnapshotFeatures(false).First(v=>v.id=="fog.stop").enabled,"Snapshot unchecked unavailable selected feature");
            adapter.TestAvailable("fog.stop",true);adapter.Tick();Require(adapter.Effective.Enabled("fog.stop"),"Regained capability failed to resume");
        });
        Check("Manual reset while suspended prevents subsequent automatic reenable",()=>{
            var (adapter,actor,_)=Fixture();adapter.Apply(Choices());adapter.SuspendEffects();adapter.Reset();adapter.RefreshEffects(true);
            Require(!adapter.Settings.AnyEnabled&&!adapter.EffectsActive&&Move(adapter,actor)==7f,"Reset choices were replayed");
        });
        Check("Suspension drops applied world state while retaining only immutable user choices",()=>{
            var (adapter,_,_)=Fixture();adapter.Apply(Choices());int before=adapter.WorldResets;adapter.SuspendEffects();
            Require(adapter.WorldResets>before&&!adapter.EffectsActive&&adapter.Settings.AnyEnabled,"World cleanup and selected state remain bound");
            adapter.Dispose();Require(!adapter.Settings.AnyEnabled&&!adapter.EffectsActive&&adapter.Current==null,"Dispose retained active state or actor");
        });
        Check("Steady state effect refresh does not allocate per-frame settings arrays",()=>{
            var (adapter,_,_)=Fixture();adapter.Apply(Choices());for(int i=0;i<100;i++)adapter.Tick();
            long start=GC.GetAllocatedBytesForCurrentThread();for(int i=0;i<10000;i++)adapter.Tick();long allocated=GC.GetAllocatedBytesForCurrentThread()-start;
            Require(allocated<1024,"Steady state settings refresh allocated "+allocated+" bytes");
        });
    }
}
