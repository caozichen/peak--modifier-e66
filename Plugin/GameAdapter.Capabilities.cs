using System.Collections.Generic;
using PeakWorkshop.Protocol;

namespace PeakWorkshop.Plugin
{
    internal sealed partial class GameAdapter
    {
        private readonly Dictionary<string,string> _capabilities = new Dictionary<string,string>(System.StringComparer.Ordinal);
        private int _capabilityRevision;
        public bool IsAvailable(string id) => _capabilities.TryGetValue(id,out var reason) && reason.Length==0;
        public string AvailabilityReason(string id) => _capabilities.TryGetValue(id,out var reason) ? reason : CompatibilityMessage;
        private void SetAvailable(string id, bool available, string reason = "")
        {
            string value = available ? "" : (string.IsNullOrEmpty(reason) ? "当前游戏不支持此功能。" : reason);
            if (_capabilities.TryGetValue(id, out var previous) && previous == value) return;
            _capabilities[id] = value;
            _capabilityRevision++;
        }
        public FeatureValue[] SnapshotFeatures(bool stopped)
        {
            var values=Settings.ToWire();
            foreach(var value in values) { value.available=!stopped && IsAvailable(value.id); value.reason=stopped ? "插件已停用。" : AvailabilityReason(value.id); }
            return values;
        }
    }
}
