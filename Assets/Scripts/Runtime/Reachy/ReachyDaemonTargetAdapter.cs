using Newtonsoft.Json;
using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    public static class ReachyDaemonTargetAdapter
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore
        };

        public static ReachyDaemonFullBodyTarget FromHeadPayload(ReachyHeadPayload payload)
        {
            return FromHeadPayload(payload, payload != null ? payload.bodyYawDegrees : (float?)null);
        }

        public static ReachyDaemonFullBodyTarget FromHeadPayload(ReachyHeadPayload payload, float? bodyYawDegrees)
        {
            if (payload == null)
                return null;

            float bodyYawForHeadPoseDegrees = bodyYawDegrees.HasValue ? bodyYawDegrees.Value : payload.bodyYawDegrees;
            float bodyYawForHeadPoseRadians = bodyYawForHeadPoseDegrees * Mathf.Deg2Rad;
            Matrix4x4 worldHeadPose = ComposeWorldHeadPose(payload.headPosition, payload.headRotation, bodyYawForHeadPoseRadians);

            return new ReachyDaemonFullBodyTarget
            {
                targetHeadPose = new ReachyDaemonMatrixPose
                {
                    m = FlattenRowMajor(worldHeadPose)
                },
                targetAntennas = payload.antennas != null
                    ? new[] { payload.antennas.right, payload.antennas.left }
                    : null,
                targetBodyYaw = bodyYawDegrees.HasValue ? bodyYawDegrees.Value * Mathf.Deg2Rad : (float?)null
            };
        }

        public static string ToJson(ReachyHeadPayload payload)
        {
            return ToJson(FromHeadPayload(payload));
        }

        public static string ToJson(ReachyDaemonFullBodyTarget target)
        {
            return JsonConvert.SerializeObject(target, JsonSettings);
        }

        public static Matrix4x4 ComposeWorldHeadPose(
            ReachyVectorPayload localHeadPosition,
            ReachyQuaternionPayload localHeadRotation,
            float bodyYawRadians)
        {
            Vector3 position = localHeadPosition != null
                ? new Vector3(localHeadPosition.x, localHeadPosition.y, localHeadPosition.z)
                : Vector3.zero;
            Quaternion rotation = localHeadRotation != null
                ? CoordinateFrameUtil.Normalize(localHeadRotation.ToQuaternion())
                : Quaternion.identity;

            Matrix4x4 bodyYawPose = CreateRobotZYawMatrix(bodyYawRadians);
            Matrix4x4 localHeadPose = Matrix4x4.TRS(position, rotation, Vector3.one);
            return bodyYawPose * localHeadPose;
        }

        public static float[] FlattenRowMajor(Matrix4x4 matrix)
        {
            var values = new float[16];
            int index = 0;
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    values[index++] = matrix[row, column];
                }
            }

            return values;
        }

        private static Matrix4x4 CreateRobotZYawMatrix(float yawRadians)
        {
            float cos = Mathf.Cos(yawRadians);
            float sin = Mathf.Sin(yawRadians);
            Matrix4x4 matrix = Matrix4x4.identity;
            matrix[0, 0] = cos;
            matrix[0, 1] = -sin;
            matrix[1, 0] = sin;
            matrix[1, 1] = cos;
            return matrix;
        }
    }
}
