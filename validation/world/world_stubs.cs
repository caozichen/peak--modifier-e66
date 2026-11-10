// Managed substitutes for isolated branch/restore tests. These do not execute Unity or apply Harmony patches.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace UnityEngine
{
    public class Object { public bool Destroyed; public static bool operator ==(Object a,Object b)=>ReferenceEquals(a,b)|| (ReferenceEquals(a,null)&&b?.Destroyed==true)||(ReferenceEquals(b,null)&&a?.Destroyed==true); public static bool operator !=(Object a,Object b)=>!(a==b); public override bool Equals(object o)=>ReferenceEquals(this,o);public override int GetHashCode()=>base.GetHashCode(); }
    public class Component:Object { public Transform transform=new Transform(); }
    public class Transform { public Vector3 localScale; }
    public struct Vector3 { public float x,y,z; public Vector3(float v){x=y=z=v;} public static Vector3 operator *(Vector3 a,float f)=>new Vector3{x=a.x*f,y=a.y*f,z=a.z*f};public static bool operator ==(Vector3 a,Vector3 b)=>a.x==b.x&&a.y==b.y&&a.z==b.z;public static bool operator !=(Vector3 a,Vector3 b)=>!(a==b);public override bool Equals(object o)=>o is Vector3 v&&this==v;public override int GetHashCode()=>HashCode.Combine(x,y,z); }
    public class MaterialPropertyBlock { internal Dictionary<int,float> Values=new(); public float GetFloat(int id)=>Values.TryGetValue(id,out float v)?v:0;public void SetFloat(int id,float value)=>Values[id]=value; }
    public class Renderer:Object { private readonly Dictionary<int,float> Values=new();public void GetPropertyBlock(MaterialPropertyBlock block){block.Values=new Dictionary<int,float>(Values);}public void SetPropertyBlock(MaterialPropertyBlock block){Values.Clear();foreach(var pair in block.Values)Values[pair.Key]=pair.Value;} }
    public static class Time { public static float deltaTime=>0.02f; }
    public static class Mathf { public static bool Approximately(float a,float b)=>Math.Abs(a-b)<0.000001f; }
}
namespace HarmonyLib
{
    public class HarmonyMethod { public HarmonyMethod(MethodInfo m){} }
    public class Harmony { public void Patch(MethodInfo m,HarmonyMethod prefix,HarmonyMethod postfix,HarmonyMethod transpiler,HarmonyMethod finalizer,object ilmanipulator){} }
    public class CodeInstruction { public OpCode opcode; public object operand;public CodeInstruction(OpCode code,object operand=null){opcode=code;this.operand=operand;} public bool Calls(MethodInfo m)=> (opcode==OpCodes.Call||opcode==OpCodes.Callvirt)&&Equals(operand,m); }
    public static class AccessTools { public static MethodInfo PropertyGetter(Type t,string name)=>t.GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static)?.GetGetMethod(true);public static MethodInfo Method(Type t,string name)=>t.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static); }
}
public class WorldCharacter:UnityEngine.Object { public bool Eligible=true; }
public class WorldView:UnityEngine.Object { public bool IsMine{get;set;}=true; }
public class WorldItem:UnityEngine.Object { public object trueHolderCharacter{get;set;}public object holderCharacter{get;set;}public WorldView photonView{get;set;}=new WorldView();public Action OnConsumed;public bool consuming;public int OriginalRuns;public IEnumerator ConsumeDelayed(bool ignoreActions){OriginalRuns++;consuming=true;if(!ignoreActions)OnConsumed?.Invoke();yield return null;} }
public class WorldLantern:UnityEngine.Object { public WorldItem item; }
public class WorldAction:UnityEngine.Object { public WorldItem item; }
public class WorldPing:UnityEngine.Component { public WorldCharacter character; }
public class WorldFog:UnityEngine.Object { public float speed;public void Move(){} }
public class WorldSphere:UnityEngine.Object { public float REVEAL_AMOUNT; public UnityEngine.Renderer rend=new UnityEngine.Renderer();public static int RevealAmount=17; public void SetSharderVars(){} }
public class WorldSession { public bool InRoom{get;set;}=true;public bool IsHost{get;set;}=true; }
public static class WorldSessionHolder { public static WorldSession Session{get;set;}=new WorldSession(); }
