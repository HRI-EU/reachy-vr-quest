using ReachyMiniTeleop.Reachy;
using UnityEngine;

namespace ReachyMiniTeleop.Debugging
{
    public sealed class DebugReachyPayloadLogger : MonoBehaviour
    {
        public ReachyHeadCommandPublisher publisher;
        public bool logPayloads = true;

        private void Awake()
        {
            if (publisher == null)
                publisher = FindFirstObjectByType<ReachyHeadCommandPublisher>();
        }

        private void OnEnable()
        {
            if (publisher != null)
                publisher.OnPayloadJsonBuilt += HandlePayloadJsonBuilt;
        }

        private void OnDisable()
        {
            if (publisher != null)
                publisher.OnPayloadJsonBuilt -= HandlePayloadJsonBuilt;
        }

        private void HandlePayloadJsonBuilt(string payload)
        {
            if (logPayloads)
                UnityEngine.Debug.Log($"[DebugReachyPayloadLogger] {payload}");
        }
    }
}

