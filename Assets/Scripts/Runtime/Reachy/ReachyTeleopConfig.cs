using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    [CreateAssetMenu(menuName = "Reachy Mini/Teleop Config", fileName = "ReachyTeleopConfig")]
    public sealed class ReachyTeleopConfig : ScriptableObject
    {
        [Header("Transport")]
        public string endpoint = "tcp://localhost:40000";
        public string identity = "body";
        public float heartbeatInterval = 2f;

        [Header("Publishing")]
        [Min(0.01f)]
        public float sendRateHz = 10f;
        public bool sendBodyYaw = true;
        public bool sendAntennas = true;

        [Header("Body Yaw")]
        public float bodyYawLimitDegrees = 45f;
        public float bodyYawSign = 1f;

        [Header("Antenna Mapping")]
        public float fingerToHeadPlaneMaxDegrees = 90f;
        public float antennaMaxDegrees = 90f;
        public bool invertLeftAntenna = true;
        public bool invertRightAntenna = true;
    }
}

