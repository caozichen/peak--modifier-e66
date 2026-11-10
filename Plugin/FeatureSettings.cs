using System;
using System.Collections.Generic;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Plugin
{
    // Immutable validated state. Capability flags from the client are never trusted.
    public sealed class FeatureSettings
    {
        private readonly Dictionary<string, FeatureValue> _values = new Dictionary<string, FeatureValue>(StringComparer.Ordinal);
        public static readonly FeatureSettings Disabled = new FeatureSettings(Defaults());
        public bool AnyEnabled { get; private set; }
        public bool SpeedEnabled => Enabled("speed");
        public float SpeedMultiplier => Value("speed");
        public bool StaminaEnabled => Enabled("stamina");

        public FeatureSettings(bool speedEnabled, float speedMultiplier, bool staminaEnabled) : this(Legacy(speedEnabled,speedMultiplier,staminaEnabled)) { }
        public FeatureSettings(FeatureValue[] values)
        {
            if(values == null || values.Length != FeatureCatalog.All.Length) throw new ArgumentException("必须提交完整功能状态。");
            foreach(var input in values)
            {
                if(input == null || string.IsNullOrEmpty(input.id)) throw new ArgumentException("缺少功能标识。");
                var definition=FeatureCatalog.Find(input.id);
                if(definition == null || _values.ContainsKey(input.id)) throw new ArgumentException("功能标识未知或重复："+input.id);
                if(float.IsNaN(input.value) || float.IsInfinity(input.value) || input.value < definition.Min || input.value > definition.Max)
                    throw new ArgumentOutOfRangeException(input.id,"功能参数超出允许范围。");
                _values.Add(input.id,new FeatureValue { id=input.id, enabled=input.enabled, value=input.value });
                AnyEnabled |= input.enabled;
            }
        }
        public bool Enabled(string id) => _values.TryGetValue(id,out var value) && value.enabled;
        public bool IsEnabled(string id) => Enabled(id);
        public float Value(string id) => _values.TryGetValue(id,out var value) ? value.value : 1f;
        public float GetValue(string id) => Value(id);
        public float Factor(string id) => Enabled(id) ? Value(id) : 1f;
        public FeatureValue[] ToWire()
        {
            var result=new FeatureValue[FeatureCatalog.All.Length];
            for(int i=0;i<result.Length;i++) { var definition=FeatureCatalog.All[i]; var value=_values[definition.Id]; result[i]=new FeatureValue { id=value.id, enabled=value.enabled, value=value.value }; }
            return result;
        }
        public float ApplyMovement(float original,bool eligible) => SpeedEnabled && eligible ? original*SpeedMultiplier : original;
        public bool ApplyStamina(bool original,bool eligible) => original || (StaminaEnabled && eligible);
        public float ApplyScale(string id,float original,bool eligible) => eligible && Enabled(id) ? original*Value(id) : original;
        public FeatureSettings DisableUnavailable(Func<string,bool> available, out bool changed)
        {
            if (available == null) throw new ArgumentNullException(nameof(available));
            changed = false;
            var values = ToWire();
            foreach (var value in values)
                if (value.enabled && !available(value.id)) { value.enabled = false; changed = true; }
            return changed ? new FeatureSettings(values) : this;
        }
        public static FeatureValue[] Defaults()
        {
            var result=new FeatureValue[FeatureCatalog.All.Length];
            for(int i=0;i<result.Length;i++) { var definition=FeatureCatalog.All[i]; result[i]=new FeatureValue { id=definition.Id, value=definition.Default }; }
            return result;
        }
        private static FeatureValue[] Legacy(bool speed,float multiplier,bool stamina)
        {
            var result=Defaults();
            foreach(var value in result) { if(value.id=="speed") { value.enabled=speed;value.value=multiplier; } if(value.id=="stamina") value.enabled=stamina; }
            return result;
        }
    }
}
