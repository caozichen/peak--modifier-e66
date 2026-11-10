#nullable disable
using System;

namespace PeakWorkshop.Protocol
{
    [Serializable]
    public sealed class WireMessage
    {
        public int protocol = WireProtocol.Version;
        public string kind = "state";
        public string requestId = "";
        public string sessionId = "";
        public string controllerId = "";
        public bool ok;
        public string message = "";
        public string error = "";
        public string pluginVersion = "";
        public string gameVersion = "";
        public string scene = "";
        public bool characterReady;
        public string characterReadinessMessage = "";
        public bool clearStatusAvailable;
        public string clearStatusReason = "";
        public bool speedAvailable;
        public bool staminaAvailable;
        public bool speedEnabled;
        public float speedMultiplier = 1f;
        public bool staminaEnabled;
        public bool detachAvailable;
        public bool pluginStopped;
        public int stateRevision;
        public FeatureValue[] features;
    }

    public static class WireProtocol
    {
        public const int Version = 2;
        public const int MaxMessageBytes = 65536;
        public const string PipePrefix = "PeakWorkshop.v2.";
    }
}
