using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Web.Script.Serialization;
using HarmonyLib;

// Isolated process: inspect actual game IL and invoke production transformers only.
// Never invokes Harmony.Patch or any Unity/game method.
internal static class OfflineChecks
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static Assembly game, plugin;
    static Type adapterType;
    static object adapter;
    static readonly List<object> results=new List<object>();
    static int failures;
    static void Main(string[] args)
    {
        string managed=args[0], core=args[1], dll=args[2];
        AppDomain.CurrentDomain.AssemblyResolve += (sender,eventArgs) => {
            var name=new AssemblyName(eventArgs.Name).Name+".dll";
            foreach(var dir in new[]{managed,core,Path.GetDirectoryName(dll)})
            { string path=Path.Combine(dir,name);if(File.Exists(path))return Assembly.LoadFrom(path); }
            return null;
        };
        game=Assembly.LoadFrom(Path.Combine(managed,"Assembly-CSharp.dll"));
        plugin=Assembly.LoadFrom(dll);
        adapterType=plugin.GetType("PeakWorkshop.Plugin.GameAdapter",true);
        adapter=Activator.CreateInstance(adapterType,All,null,new object[]{new Action<string>(Console.WriteLine)},null);
        adapterType.GetField("_active",All).SetValue(null,adapter);
        Console.WriteLine("ACTUAL STATUS TYPE: "+game.GetType("CharacterAfflictions").GetMethod("AddStatus",All).GetParameters()[0].ParameterType.FullName);
        Check("Current status enum exactly matches all 15 catalog entries",()=>{
            var names=(string[])plugin.GetType("PeakWorkshop.Protocol.FeatureCatalog").GetField("StatusNames").GetValue(null);
            Assert(names.SequenceEqual(Enum.GetNames(game.GetType("CharacterAfflictions").GetMethod("AddStatus",All).GetParameters()[0].ParameterType)),"Status mismatch");
        });
        Check("Production actor resolves the actual nested status enum",()=>{
            using(var metadata=Mono.Cecil.AssemblyDefinition.ReadAssembly(dll))
            {
                var input=metadata.MainModule.GetType("PeakWorkshop.Plugin.GameAdapter").Methods.Single(m=>m.Name=="InitializeActor").Body.Instructions;
                Assert(input.Any(i=>(i.Operand as Mono.Cecil.MethodReference)?.Name=="GetNestedType"),"Initializer still assumes a top-level status type");
            }
            Assert(game.GetType("CharacterAfflictions").GetNestedType("STATUSTYPE",All)!=null,"Nested enum missing");
        });
        Check("All production lifecycle and actor-owner fields exist with correct types",()=>{
            var character=game.GetType("Character",true);var data=game.GetType("CharacterData",true);
            Assert(character.GetField("localCharacter",All)?.FieldType==character,"Local character field");
            Assert(character.GetField("data",All)?.FieldType==data,"Character data field");
            foreach(var name in new[]{"IsLocal","IsInitialized"})Assert(character.GetProperty(name,All)?.PropertyType==typeof(bool),name);
            Assert(character.GetField("_refsInitialized",All)?.FieldType==typeof(bool),"Base reference initialization field");
            Assert(data.GetProperty("dead",All)?.PropertyType==typeof(bool),"dead");
            foreach(var name in new[]{"passedOut","fullyPassedOut","isClimbing","isRopeClimbing","isVineClimbing"})Assert(data.GetField(name,All)?.FieldType==typeof(bool),name);
            foreach(var name in new[]{"CharacterMovement","CharacterClimbing","CharacterRopeHandling","CharacterVineClimbing","CharacterData","CharacterAfflictions"})
                Assert(game.GetType(name).GetField("character",All)?.FieldType==character,"Owner field: "+name);
            Assert(data.GetProperty("petrifyAmount",All)?.PropertyType==typeof(int),"Petrify property");
            var refs=character.GetField("refs",All);Assert(refs?.FieldType.GetField("afflictions",All)?.FieldType==game.GetType("CharacterAfflictions"),"refs.afflictions");
            var unityObject=Assembly.LoadFrom(Path.Combine(managed,"UnityEngine.CoreModule.dll")).GetType("UnityEngine.Object",true);
            Assert(unityObject.IsAssignableFrom(character) && unityObject.IsAssignableFrom(data),"Lifecycle objects must support Unity destroyed semantics");
        });
        Check("World ownership and shader reflection bindings exist",()=>{
            var item=game.GetType("Item");var character=game.GetType("Character");
            foreach(var name in new[]{"trueHolderCharacter","holderCharacter"})Assert(item.GetProperty(name,All)?.PropertyType==character,name);
            Assert(item.GetProperty("photonView",All)?.PropertyType.GetProperty("IsMine",All)?.PropertyType==typeof(bool),"Photon ownership");
            Assert(item.GetField("OnConsumed",All)?.FieldType==typeof(Action),"OnConsumed event");
            Assert(game.GetType("ItemActionBase").GetField("item",All)?.FieldType==item,"Action owner");
            Assert(game.GetType("Lantern").GetField("item",All|BindingFlags.DeclaredOnly)?.FieldType==item,"Lantern owner");
            var sphere=game.GetType("FogSphere");Assert(sphere.GetField("REVEAL_AMOUNT",All)?.FieldType==typeof(float),"Reveal field");
            Assert(sphere.GetField("RevealAmount",All)?.IsStatic==true,"Reveal shader ID");
            // The game's interface has default methods unsupported by .NET Framework.
            // Verify its metadata without asking this host to materialize that interface.
            using(var metadata=Mono.Cecil.AssemblyDefinition.ReadAssembly(Path.Combine(managed,"Assembly-CSharp.dll")))
            {
                var session=metadata.MainModule.GetType("Peak.Network.ISessionAPI");
                foreach(var name in new[]{"IsHost","InRoom"})Assert(session.Properties.Single(p=>p.Name==name).PropertyType.FullName=="System.Boolean",name);
                Assert(metadata.MainModule.GetType("Peak.Network.NetCode").Properties.Any(p=>p.Name=="Session"),"Session API");
            }
        });
        string[,] methodSpecs={
            {"CharacterData","SetPetrify","Void","Int32"},
            {"CharacterAfflictions","RemoveAllThorns","Void",""},
            {"CharacterAfflictions","ClearAllStatus","Void","Boolean,Boolean"},
            {"CharacterMovement","GetMovementForce","Single",""}, {"Character","get_infiniteStam","Boolean",""},
            {"Character","UseStamina","Boolean","Single,Boolean,Boolean"},{"Character","AddStamina","Void","Single"},
            {"CharacterMovement","FallTime","Single",""},{"Character","Fall","Void","Single,Single"},
            {"CharacterAfflictions","AddStatus","Boolean","STATUSTYPE,Single,Boolean,Boolean,Boolean,Boolean,Boolean"},
            {"CharacterAfflictions","SetStatus","Void","STATUSTYPE,Single,Boolean"},
            {"CharacterAfflictions","GetCurrentStatus","Single","STATUSTYPE"},{"CharacterAfflictions","AddPetrify","Void","Int32"},
            {"CharacterAfflictions","UpdateWeight","Void",""},{"Action_ReduceUses","RunAction","Void",""},
            {"Item","ConsumeDelayed","IEnumerator","Boolean"},{"PointPing","Go","Void",""}
        };
        for(int i=0;i<methodSpecs.GetLength(0);i++)
        {
            int index=i;
            Check("Game signature "+methodSpecs[i,0]+"."+methodSpecs[i,1],()=>{
                var method=game.GetType(methodSpecs[index,0],true).GetMethods(All).Single(m=>m.Name==methodSpecs[index,1] && string.Join(",",m.GetParameters().Select(p=>p.ParameterType.Name))==methodSpecs[index,3]);
                Assert(!method.IsStatic && method.ReturnType.Name==methodSpecs[index,2],"Return/static mismatch");
            });
        }
        Check("Production clear-status initializer binds every required native member",()=>{
            adapterType.GetMethod("InitializeClearStatus",All).Invoke(adapter,new object[]{game});
            Assert((bool)adapterType.GetProperty("ClearMembersAvailable",All).GetValue(adapter,null),"Native status clearing bindings incomplete");
        });
        Read("CharacterClimbing","climbSpeed","ActorScaleClimbSpeed");
        Read("CharacterRopeHandling","climbSpeed","ActorScaleClimbSpeed");
        Read("CharacterVineClimbing","climbSpeed","ActorScaleClimbSpeed");
        Read("CharacterData","slippy","ActorReadSlippy");
        Read("ClimbModifierSurface","onlySlideDown","ActorReadOnlySlideDown");
        Read("CharacterMovement","jumpImpulse","ActorScaleJumpImpulse");
        foreach(var pair in new[]{new[]{"CharacterClimbing","GetRequestedPostition"},new[]{"CharacterRopeHandling","Update"},new[]{"CharacterVineClimbing","FixedUpdate"}})
            Check("Production actor transformer on "+pair[0]+"."+pair[1],()=>{
                MethodInfo method=game.GetType(pair[0]).GetMethod(pair[1],All);
                var input=Original(method);
                var fields=new HashSet<FieldInfo>(((IDictionary)adapterType.GetField("_actorReadHelpers",All).GetValue(adapter)).Keys.Cast<FieldInfo>());
                int required=input.Count(i=>i.opcode==OpCodes.Ldfld && fields.Contains(i.operand as FieldInfo));
                Assert(required>0,"No matched game field reads");
                var output=Transform("ActorReadTranspiler",input);
                Assert(output.Count==input.Count+required*2,"Unexpected inserted instruction count");
                Assert(output.Count(i=>i.opcode==OpCodes.Call && (i.operand as MethodInfo)?.DeclaringType==adapterType)==required,"Helper count mismatch");
            });
        Check("Production jump transformer matches actual delayed coroutine",()=>{
            var movement=game.GetType("CharacterMovement");
            var impulse=movement.GetField("jumpImpulse",All);
            var methods=Nested(movement).SelectMany(t=>t.GetMethods(All|BindingFlags.DeclaredOnly)).Where(m=>m.Name=="MoveNext" && m.DeclaringType.Name.Contains("JumpRpc") && Original(m).Any(i=>i.opcode==OpCodes.Ldfld && Equals(i.operand,impulse))).ToArray();
            Assert(methods.Length>0,"No delayed jump reads");
            foreach(var method in methods)
            {var input=Original(method);var output=Transform("ActorReadTranspiler",input);Assert(output.Count>input.Count,"Delayed jump was not transformed");}
        });
        Check("Production delay transformer matches actual CanRegenStamina",()=>{
            var input=Original(game.GetType("Character").GetMethod("CanRegenStamina",All));
            Assert(input.Count(i=>i.opcode==OpCodes.Stloc_0)==1,"Delay pattern changed");
            var output=Transform("ActorDelayTranspiler",input);Assert(output.Count==input.Count+2,"Missing delay helper");
        });
        Check("Production delay transformer rejects ambiguous IL",()=>{
            Reject(()=>Transform("ActorDelayTranspiler",new List<CodeInstruction>{new CodeInstruction(OpCodes.Stloc_0),new CodeInstruction(OpCodes.Stloc_0)}));
        });
        foreach(var spec in new[]{new[]{"Lantern","UpdateFuel","WorldFuelTranspiler"},new[]{"OrbFogHandler","Move","WorldFogTranspiler"},new[]{"FogSphere","SetSharderVars","WorldRevealTranspiler"}})
        {
            Check("Production world transformer on "+spec[0]+"."+spec[1],()=>{
                var method=game.GetType(spec[0]).GetMethod(spec[1],All);var input=Original(method);
                var output=Transform(spec[2],input,method);Assert(output.Count==input.Count+2,"World helper insertion mismatch");
            });
            Check("Production "+spec[2]+" rejects missing IL pattern",()=>Reject(()=>Transform(spec[2],new List<CodeInstruction>{new CodeInstruction(OpCodes.Ret)},game.GetType(spec[0]).GetMethod(spec[1],All))));
        }
        Check("Production actor transformer preserves branch labels and exception boundaries",()=>{
            var generator=new DynamicMethod("labels",typeof(void),Type.EmptyTypes).GetILGenerator();var label=generator.DefineLabel();
            var instruction=new CodeInstruction(OpCodes.Ldfld,game.GetType("CharacterMovement").GetField("jumpImpulse",All));
            instruction.labels.Add(label);instruction.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            var transformed=Transform("ActorReadTranspiler",new List<CodeInstruction>{instruction});
            Assert(transformed[0].opcode==OpCodes.Dup && transformed[0].labels.Contains(label) && transformed[0].blocks.Count==1,"Entry metadata lost");
            Assert(transformed[1].labels.Count==0 && transformed[1].blocks.Count==0,"Entry metadata duplicated");
        });
        adapterType.GetField("_active",All).SetValue(null,null);
        File.WriteAllText(args[3],new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(new{total=results.Count,passed=results.Count-failures,failed=failures,createdUtc=DateTime.UtcNow,gameExecuted=false,patchesInstalled=false,results}));
        Console.WriteLine("RESULT "+(results.Count-failures)+"/"+results.Count);Environment.ExitCode=failures==0?0:1;
    }
    static IEnumerable<Type> Nested(Type t){foreach(var child in t.GetNestedTypes(All)){yield return child;foreach(var item in Nested(child))yield return item;}}
    static void Read(string type,string field,string helper){adapterType.GetMethod("ActorRegisterRead",All).Invoke(adapter,new object[]{game.GetType(type).GetField(field,All),helper});}
    static List<CodeInstruction> Original(MethodInfo m){return PatchProcessor.GetOriginalInstructions(m).ToList();}
    static List<CodeInstruction> Transform(string name,List<CodeInstruction> input,MethodBase original=null){var method=adapterType.GetMethod(name,All);return ((IEnumerable<CodeInstruction>)method.Invoke(null,method.GetParameters().Length==1?new object[]{input}:new object[]{input,original})).ToList();}
    static void Reject(Action body){try{body();}catch(InvalidOperationException){return;}catch(TargetInvocationException e){if(e.InnerException is InvalidOperationException)return;throw;}throw new Exception("Ambiguous pattern accepted");}
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    static void Check(string name,Action body){try{body();Console.WriteLine("PASS "+name);results.Add(new{name,passed=true});}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e);results.Add(new{name,passed=false,error=e.ToString()});}}
}
