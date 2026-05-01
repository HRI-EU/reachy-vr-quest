using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    public static class CoordinateFrameUtil
    {
        private static readonly Matrix4x4 UnityRufToRobotFluMatrix = new Matrix4x4(
            new Vector4(0f, -1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f),
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 0f, 0f, 1f));

        public static Vector3 UnityRufToRobotFlu(Vector3 position)
        {
            return new Vector3(position.z, -position.x, position.y);
        }

        public static Quaternion UnityRufToRobotFlu(Quaternion rotation)
        {
            Matrix4x4 unityRotation = Matrix4x4.Rotate(rotation);
            Matrix4x4 converted = UnityRufToRobotFluMatrix * unityRotation * UnityRufToRobotFluMatrix.inverse;

            Vector3 forward = converted.MultiplyVector(Vector3.forward);
            Vector3 up = converted.MultiplyVector(Vector3.up);
            if (forward.sqrMagnitude < 1e-6f || up.sqrMagnitude < 1e-6f)
                return Quaternion.identity;

            return Normalize(Quaternion.LookRotation(forward, up));
        }

        public static Quaternion Normalize(Quaternion rotation)
        {
            float magnitude = Mathf.Sqrt(
                rotation.x * rotation.x +
                rotation.y * rotation.y +
                rotation.z * rotation.z +
                rotation.w * rotation.w);

            if (magnitude < 1e-6f)
                return Quaternion.identity;

            return new Quaternion(
                rotation.x / magnitude,
                rotation.y / magnitude,
                rotation.z / magnitude,
                rotation.w / magnitude);
        }
    }
}

