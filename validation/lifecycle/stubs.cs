using System.Reflection;

namespace UnityEngine
{
    // Only the native Unity lifetime is substituted. Production Alive and readiness
    // reflection still execute unchanged in the linked GameAdapter.cs.
    public class Object
    {
        public bool Destroyed;
        public static bool operator ==(Object left, Object right)
        {
            bool leftNull = ReferenceEquals(left, null);
            bool rightNull = ReferenceEquals(right, null);
            if (leftNull && rightNull) return true;
            if (leftNull) return right.Destroyed;
            if (rightNull) return left.Destroyed;
            return ReferenceEquals(left, right);
        }
        public static bool operator !=(Object left, Object right) => !(left == right);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => base.GetHashCode();
    }
}

namespace UnityEngine.SceneManagement
{
    public struct Scene { public string name; }
    public static class SceneManager
    {
        public static string ActiveSceneName = "Airport";
        public static Scene GetActiveScene() => new Scene { name = ActiveSceneName };
    }
}

namespace HarmonyLib { public class Harmony { } }

internal sealed class TestCharacter : UnityEngine.Object
{
    public static TestCharacter localCharacter;
    public TestCharacterData data = new();
    public bool _refsInitialized = true;
    public bool StatisticsInitialized;
    public bool AllowLocal = true;
    public bool ThrowLocal;
    public bool ThrowInitialized;
    public bool IsLocal => ThrowLocal ? throw new InvalidOperationException("local getter unavailable")
        : AllowLocal && ReferenceEquals(localCharacter, this);
    public bool IsInitialized => ThrowInitialized ? throw new InvalidOperationException("stats getter unavailable")
        : _refsInitialized && StatisticsInitialized;
}

internal sealed class TestCharacterData : UnityEngine.Object
{
    public bool IsDead;
    public bool ThrowDead;
    public bool dead => ThrowDead ? throw new InvalidOperationException("data getter unavailable") : IsDead;
    public bool passedOut;
    public bool fullyPassedOut;
}

namespace PeakWorkshop.Plugin
{
    internal sealed partial class GameAdapter
    {
        // Feature patching and game execution are deliberately out of this test's scope.
        internal readonly List<object> RestoredCharacters = new();
        private int _actorClimbDepth, _actorStatusDepth;
        internal int TestScopeDepth => _actorClimbDepth + _actorStatusDepth;
        private readonly Dictionary<Type, FieldInfo> _actorOwners = new();
        private readonly Dictionary<FieldInfo, MethodInfo> _actorReadHelpers = new();
        private void InitializeActor(Assembly game, HarmonyLib.Harmony harmony) { }
        private void InitializeClearStatus(Assembly game) { }
        private void InitializeWorld(Assembly game, HarmonyLib.Harmony harmony) { }
        internal void TestAvailable(string id,bool value) => SetAvailable(id,value,"fixture unavailable"); internal FeatureSettings Effective => _settings; internal int WorldResets; internal readonly List<(object Actor,bool Restore)> WeightCalls=new();
        private void RestoreActorCharacter(object character) => RestoredCharacters.Add(character);
        private void ActorSettingsChanged(FeatureSettings previous) { if(previous.Enabled("weight.disabled")!=_settings.Enabled("weight.disabled"))ActorRecalculateWeight(_current,false); }
        private void TickWorld() { }
        private void ResetWorld() { WorldResets++; }
        private void DisposeWorld() { }
        private void ActorRecalculateWeight(object character, bool force) { WeightCalls.Add((character,force)); }
    }
}
