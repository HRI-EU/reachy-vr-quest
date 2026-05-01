using Newtonsoft.Json;
using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    [System.Serializable]
    public sealed class ReachyVectorPayload
    {
        public float x;
        public float y;
        public float z;

        public ReachyVectorPayload()
        {
        }

        public ReachyVectorPayload(float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    [System.Serializable]
    public sealed class ReachyQuaternionPayload
    {
        public float x;
        public float y;
        public float z;
        public float w;

        public ReachyQuaternionPayload()
        {
        }

        public ReachyQuaternionPayload(Quaternion rotation)
        {
            x = rotation.x;
            y = rotation.y;
            z = rotation.z;
            w = rotation.w;
        }

        public Quaternion ToQuaternion()
        {
            return new Quaternion(x, y, z, w);
        }
    }

    [System.Serializable]
    public sealed class ReachyAntennaPayload
    {
        public float right;
        public float left;
    }

    [System.Serializable]
    public sealed class ReachyHeadPayload
    {
        [JsonProperty("body_yaw_degrees")]
        public float bodyYawDegrees;

        [JsonProperty("head_position")]
        public ReachyVectorPayload headPosition;

        [JsonProperty("head_rotation")]
        public ReachyQuaternionPayload headRotation;

        [JsonProperty("antennas")]
        public ReachyAntennaPayload antennas;
    }
}

