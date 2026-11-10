using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Plugin
{
    internal sealed partial class GameAdapter
    {
        private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static GameAdapter _active;
        private FeatureSettings _settings=FeatureSettings.Disabled;
        private object _current;
        private Action<string> _log=_=>{};
        private static bool Alive(object value)=>value is UnityEngine.Object o&&o!=null;
        private bool Eligible(object actor)=>Alive(actor)&&ReferenceEquals(actor,_current)&&((WorldCharacter)actor).Eligible;
        private void SetAvailable(string id,bool available,string reason=""){}
        private static GameAdapter New()
        {
            var a=new GameAdapter();_active=a;a._current=new WorldCharacter();
            a._worldTrueHolder=typeof(WorldItem).GetProperty("trueHolderCharacter");a._worldHolder=typeof(WorldItem).GetProperty("holderCharacter");a._worldItemPhotonView=typeof(WorldItem).GetProperty("photonView");a._worldViewIsMine=typeof(WorldView).GetProperty("IsMine");
            a._worldLanternItem=typeof(WorldLantern).GetField("item");a._worldActionItem=typeof(WorldAction).GetField("item");a._worldOnConsumed=typeof(WorldItem).GetField("OnConsumed");a._worldConsumeDelayed=typeof(WorldItem).GetMethod("ConsumeDelayed");
            a._worldPingCharacter=typeof(WorldPing).GetField("character");a._worldRevealAmount=typeof(WorldSphere).GetField("REVEAL_AMOUNT");a._worldFogRenderer=typeof(WorldSphere).GetField("rend");a._worldRevealId=typeof(WorldSphere).GetField("RevealAmount");
            a._worldSession=typeof(WorldSessionHolder).GetProperty("Session");a._worldIsHost=typeof(WorldSession).GetProperty("IsHost");a._worldInRoom=typeof(WorldSession).GetProperty("InRoom");WorldSessionHolder.Session=new WorldSession();
            a._worldFuelHooked=a._worldItemsHooked=a._worldPingHooked=a._worldFogHooked=a._worldRevealHooked=true;return a;
        }
        private void Set(params (string Id,float Value)[] features){var values=FeatureSettings.Defaults();foreach(var f in features){var entry=values.First(v=>v.id==f.Id);entry.enabled=true;entry.value=f.Value;}_settings=new FeatureSettings(values);}
        private WorldItem Item()=>new WorldItem{trueHolderCharacter=_current,holderCharacter=_current};
        private static void Assert(bool value,string message){if(!value)throw new Exception(message);}
        public static int Run(string output)
        {
            var tests=new List<(string Name,Action Check)> {
                ("Fuel preserves vanilla when disabled and obeys local view ownership",()=>{var a=New();var item=a.Item();var lamp=new WorldLantern{item=item};Assert(WorldFuelDelta(2,lamp)==2,"disabled changed");a.Set(("fuel.consume",3));Assert(WorldFuelDelta(2,lamp)==6,"multiplier missing");item.photonView.IsMine=false;Assert(WorldFuelDelta(2,lamp)==2,"foreign view changed");item.photonView.IsMine=true;item.trueHolderCharacter=new WorldCharacter();Assert(WorldFuelDelta(2,lamp)==2,"foreign actor changed");}),
                ("Infinite fuel wins over multiplier, death and detach restore vanilla",()=>{var a=New();var lamp=new WorldLantern{item=a.Item()};a.Set(("fuel.consume",3),("fuel.unlimited",1));Assert(WorldFuelDelta(2,lamp)==0,"infinite priority");((WorldCharacter)a._current).Eligible=false;Assert(WorldFuelDelta(2,lamp)==2,"ineligible actor");((WorldCharacter)a._current).Eligible=true;a.DisposeWorld();Assert(WorldFuelDelta(2,lamp)==2,"detach changed fuel");}),
                ("Unlimited uses skip only owned self uses and leave consuming untouched",()=>{var a=New();a.Set(("items.unlimited",1));var item=a.Item();Assert(!WorldReducePrefix(new WorldAction{item=item}),"owned use decremented");item.holderCharacter=new WorldCharacter();Assert(WorldReducePrefix(new WorldAction{item=item}),"feeding modified");item.holderCharacter=item.trueHolderCharacter;item.photonView.IsMine=false;Assert(WorldReducePrefix(new WorldAction{item=item}),"foreign view modified");Assert(!item.consuming,"consuming touched");}),
                ("Unlimited consumable callback runs once with one frame and no persistent consuming",()=>{var a=New();a.Set(("items.unlimited",1));var item=a.Item();int calls=0;item.OnConsumed=()=>calls++;IEnumerator result=null;Assert(!WorldConsumeDelayedPrefix(item,false,ref result),"prefix passed vanilla");Assert(result.MoveNext()&&calls==1&&!item.consuming&&item.OriginalRuns==0,"first iteration mismatch");Assert(!result.MoveNext()&&calls==1&&!item.consuming,"iterator not complete");}),
                ("Saved iterator rechecks disable before first move and delegates vanilla exactly once",()=>{var a=New();a.Set(("items.unlimited",1));var item=a.Item();int calls=0;item.OnConsumed=()=>calls++;IEnumerator result=null;WorldConsumeDelayedPrefix(item,false,ref result);a._settings=FeatureSettings.Disabled;Assert(result.MoveNext()&&item.OriginalRuns==1&&calls==1&&item.consuming,"vanilla fallback missing");Assert(!result.MoveNext()&&item.OriginalRuns==1,"vanilla repeated");}),
                ("IgnoreActions and an already-started unlimited use remain finite after disable",()=>{var a=New();a.Set(("items.unlimited",1));var item=a.Item();int calls=0;item.OnConsumed=()=>calls++;var it=a.WorldUseWithoutConsuming(item,true);Assert(it.MoveNext()&&calls==0,"ignored callback ran");a._settings=FeatureSettings.Disabled;a.DisposeWorld();Assert(!it.MoveNext()&&!item.consuming,"disabled use leaked state");}),
                ("Fog stop and speed require a living local host on every invocation",()=>{var a=New();var fog=new WorldFog();a.Set(("fog.speed",3));Assert(WorldFogSpeed(2,fog)==6,"speed missing");a.Set(("fog.speed",3),("fog.stop",1));Assert(WorldFogSpeed(2,fog)==0,"pause priority missing");WorldSessionHolder.Session.IsHost=false;Assert(WorldFogSpeed(2,fog)==2,"guest changed fog");WorldSessionHolder.Session.IsHost=true;WorldSessionHolder.Session.InRoom=false;Assert(WorldFogSpeed(2,fog)==2,"outside room changed fog");WorldSessionHolder.Session.InRoom=true;((WorldCharacter)a._current).Eligible=false;Assert(WorldFogSpeed(2,fog)==2,"dead changed fog");}),
                ("Ping scale does not accumulate over fresh calculations and restores on reset",()=>{var a=New();a.Set(("ping.size",3));var ping=new WorldPing{character=(WorldCharacter)a._current};for(int i=0;i<1000;i++){ping.transform.localScale=new Vector3(2);WorldPingPostfix(ping);Assert(ping.transform.localScale==new Vector3(6),"accumulation");}Assert(a._worldPings.Count==1,"duplicate cache");a.ResetWorld();Assert(ping.transform.localScale==new Vector3(2)&&a._worldPings.Count==0,"reset failed");}),
                ("Ping cache bound restores evicted live object and preserves foreign edits",()=>{var a=New();a.Set(("ping.size",2));var first=new WorldPing{character=(WorldCharacter)a._current};first.transform.localScale=new Vector3(3);WorldPingPostfix(first);for(int i=0;i<64;i++){var p=new WorldPing{character=(WorldCharacter)a._current};p.transform.localScale=new Vector3(1);WorldPingPostfix(p);}Assert(a._worldPings.Count==64&&first.transform.localScale==new Vector3(3),"eviction did not restore");var latest=a._worldPings.Last().Target.Target as WorldPing;latest.transform.localScale=new Vector3(9);a.ResetWorld();Assert(latest.transform.localScale==new Vector3(9),"foreign scale overwritten");GC.KeepAlive(first);GC.KeepAlive(latest);}),
                ("Ping loses actor eligibility without retained current scene references",()=>{var a=New();a.Set(("ping.size",2));var p=new WorldPing{character=(WorldCharacter)a._current};p.transform.localScale=new Vector3(2);WorldPingPostfix(p);((WorldCharacter)a._current).Eligible=false;a.TickWorld();Assert(p.transform.localScale==new Vector3(2)&&a._worldPings.Count==0,"ineligible ping retained");}),
                ("Reveal changes shader input only then restores current vanilla while preserving other keys",()=>{var a=New();a.Set(("fog.reveal",1));var s=new WorldSphere{REVEAL_AMOUNT=0.2f};float v=WorldRevealValue(s.REVEAL_AMOUNT,s);Assert(v==1&&s.REVEAL_AMOUNT==0.2f,"field overwritten");var block=new MaterialPropertyBlock();block.SetFloat(WorldSphere.RevealAmount,v);block.SetFloat(99,7);s.rend.SetPropertyBlock(block);s.REVEAL_AMOUNT=0.4f;a.ResetWorld();s.rend.GetPropertyBlock(block);Assert(block.GetFloat(WorldSphere.RevealAmount)==0.4f&&block.GetFloat(99)==7&&a._worldRevealed.Count==0,"reveal restore changed keys or historical value");}),
                ("Reveal cache is bounded and later foreign shader edits survive disable",()=>{var a=New();a.Set(("fog.reveal",1));var roots=new List<WorldSphere>();for(int i=0;i<100;i++){var s=new WorldSphere{REVEAL_AMOUNT=0.3f};roots.Add(s);var block=new MaterialPropertyBlock();block.SetFloat(WorldSphere.RevealAmount,WorldRevealValue(0.3f,s));s.rend.SetPropertyBlock(block);}Assert(a._worldRevealed.Count==16,"unbounded reveal cache");var last=roots.Last();var changed=new MaterialPropertyBlock();changed.SetFloat(WorldSphere.RevealAmount,0.8f);last.rend.SetPropertyBlock(changed);a.ResetWorld();last.rend.GetPropertyBlock(changed);Assert(changed.GetFloat(WorldSphere.RevealAmount)==0.8f,"foreign reveal overwritten");GC.KeepAlive(roots);}),
                ("Each transpiler inserts exactly a value-and-owner helper and rejects ambiguous IL",()=>{New();var delta=AccessTools.PropertyGetter(typeof(Time),"deltaTime");var input=new[]{new CodeInstruction(OpCodes.Call,delta),new CodeInstruction(OpCodes.Sub)};var output=WorldFuelTranspiler(input).ToArray();Assert(output.Length==4&&output[1].opcode==OpCodes.Ldarg_0&&output[2].operand is MethodInfo m&&m.Name=="WorldFuelDelta","fuel injection mismatch");var fogMethod=typeof(WorldFog).GetMethod("Move");var fog=WorldFogTranspiler(new[]{new CodeInstruction(OpCodes.Ldfld,typeof(WorldFog).GetField("speed"))},fogMethod).ToArray();Assert(fog.Length==3&&((MethodInfo)fog[2].operand).Name=="WorldFogSpeed","fog injection mismatch");var reveal=WorldRevealTranspiler(new[]{new CodeInstruction(OpCodes.Ldfld,typeof(WorldSphere).GetField("REVEAL_AMOUNT"))},typeof(WorldSphere).GetMethod("SetSharderVars")).ToArray();Assert(reveal.Length==3&&((MethodInfo)reveal[2].operand).Name=="WorldRevealValue","reveal injection mismatch");bool rejected=false;try{WorldFuelTranspiler(new[]{new CodeInstruction(OpCodes.Call,delta),new CodeInstruction(OpCodes.Call,delta)}).ToArray();}catch(InvalidOperationException){rejected=true;}Assert(rejected,"ambiguous IL accepted");})
            };
            var results=new List<object>();int failed=0;foreach(var test in tests){try{test.Check();Console.WriteLine("PASS "+test.Name);results.Add(new{name=test.Name,passed=true});}catch(Exception ex){failed++;Console.WriteLine("FAIL "+test.Name+" "+ex);results.Add(new{name=test.Name,passed=false,error=ex.ToString()});}}
            File.WriteAllText(output,JsonSerializer.Serialize(new{total=tests.Count,passed=tests.Count-failed,failed,method="Actual production World partial with managed Unity/Harmony substitutes; no game effects tested",createdUtc=DateTime.UtcNow,results},new JsonSerializerOptions{WriteIndented=true}));return failed==0?0:1;
        }
    }
}
internal static class Program { public static int Main(string[] args)=>PeakWorkshop.Plugin.GameAdapter.Run(args.Length>0?args[0]:"world_results.json"); }
