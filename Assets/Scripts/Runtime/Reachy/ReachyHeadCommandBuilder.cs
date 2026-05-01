using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    public sealed class ReachyHeadCommandBuilder
    {
        public const string CenterCamAnchorKey = "CenterCamAnchor";
        public const string CenterEyeFrontKey = "CenterEyeFront";
        public const string CenterEyeUpKey = "CenterEyeUp";
        public const string LegacyCenterCamOrientationKey = "CenterCamOrientation";
        public const string LeftShoulderKey = "FullBody_LeftShoulder";
        public const string RightShoulderKey = "FullBody_RightShoulder";
        public const string LeftHandIndexMetacarpalKey = "FullBody_LeftHandIndexMetacarpal";
        public const string LeftHandIndexTipKey = "FullBody_LeftHandIndexTip";
        public const string RightHandIndexMetacarpalKey = "FullBody_RightHandIndexMetacarpal";
        public const string RightHandIndexTipKey = "FullBody_RightHandIndexTip";

        public bool TryBuildPayload(
            IReachySkeletonProvider provider,
            ReachyTeleopConfig config,
            out ReachyHeadPayload payload,
            out ReachyAntennaDebugInfo antennaDebugInfo)
        {
            payload = null;
            antennaDebugInfo = new ReachyAntennaDebugInfo();

            if (provider == null || !provider.SkeletonReady)
                return false;

            if (!provider.TryGetTransform(CenterCamAnchorKey, out var centerCamAnchor))
                return false;

            if (!TryGetHeadReferenceTransforms(provider, out var centerEyeFront, out var centerEyeUp))
                return false;

            if (!provider.TryGetTransform(LeftShoulderKey, out var leftShoulder) ||
                !provider.TryGetTransform(RightShoulderKey, out var rightShoulder))
            {
                return false;
            }

            if (!TryComputeShoulderRotation(leftShoulder.position, rightShoulder.position, out var shoulderRotation))
                return false;

            Vector3 headUpReferencePosition = centerEyeUp != null
                ? centerEyeUp.position
                : centerCamAnchor.position + Vector3.up;

            if (!TryComputeHeadReferenceFrame(
                    centerCamAnchor.position,
                    centerEyeFront.position,
                    headUpReferencePosition,
                    out var headForward,
                    out var headUp,
                    out var headRight))
            {
                return false;
            }

            Quaternion headRotation = Quaternion.LookRotation(headForward, headUp);
            Quaternion relativeHeadRotation = CoordinateFrameUtil.Normalize(Quaternion.Inverse(shoulderRotation) * headRotation);
            Quaternion relativeHeadRotationFlu = CoordinateFrameUtil.UnityRufToRobotFlu(relativeHeadRotation);

            BuildAntennaPayload(provider, config, headRight, out var antennas, out antennaDebugInfo);

            payload = new ReachyHeadPayload
            {
                bodyYawDegrees = config.sendBodyYaw && TryComputeBodyYawDegrees(shoulderRotation, out var rawBodyYaw)
                    ? ApplyBodyYawSignAndClamp(rawBodyYaw, config.bodyYawSign, config.bodyYawLimitDegrees)
                    : 0f,
                headPosition = new ReachyVectorPayload(0f, 0f, 0f),
                headRotation = new ReachyQuaternionPayload(relativeHeadRotationFlu),
                antennas = antennas
            };

            return true;
        }

        public static bool TryGetHeadReferenceTransforms(
            IReachySkeletonProvider provider,
            out Transform centerEyeFront,
            out Transform centerEyeUp)
        {
            centerEyeUp = null;
            if (!provider.TryGetTransform(CenterEyeFrontKey, out centerEyeFront) &&
                !provider.TryGetTransform(LegacyCenterCamOrientationKey, out centerEyeFront))
            {
                return false;
            }

            provider.TryGetTransform(CenterEyeUpKey, out centerEyeUp);
            return true;
        }

        public static bool TryComputeShoulderRotation(
            Vector3 leftShoulderPos,
            Vector3 rightShoulderPos,
            out Quaternion shoulderRotation)
        {
            shoulderRotation = Quaternion.identity;

            Vector3 right = rightShoulderPos - leftShoulderPos;
            if (right.sqrMagnitude < 1e-6f)
                return false;

            right.Normalize();
            Vector3 up = Vector3.up;
            Vector3 forward = Vector3.Cross(right, up).normalized;
            if (forward.sqrMagnitude < 1e-6f)
                return false;

            up = Vector3.Cross(forward, right).normalized;
            shoulderRotation = Quaternion.LookRotation(forward, up);
            return true;
        }

        public static bool TryComputeHeadReferenceFrame(
            Vector3 headCenterPos,
            Vector3 headForwardReferencePos,
            Vector3 headUpReferencePos,
            out Vector3 headForward,
            out Vector3 headUp,
            out Vector3 headRight)
        {
            headForward = headForwardReferencePos - headCenterPos;
            headUp = Vector3.zero;
            headRight = Vector3.zero;

            if (headForward.sqrMagnitude < 1e-6f)
                return false;
            headForward.Normalize();

            headUp = headUpReferencePos - headCenterPos;
            headUp = Vector3.ProjectOnPlane(headUp, headForward);
            if (headUp.sqrMagnitude < 1e-6f)
                return false;
            headUp.Normalize();

            headRight = Vector3.Cross(headUp, headForward);
            if (headRight.sqrMagnitude < 1e-6f)
                return false;
            headRight.Normalize();

            headUp = Vector3.Cross(headForward, headRight);
            if (headUp.sqrMagnitude < 1e-6f)
                return false;
            headUp.Normalize();

            return true;
        }

        public static bool TryComputeBodyYawDegrees(
            Quaternion shoulderRotation,
            out float bodyYawDegrees)
        {
            bodyYawDegrees = 0f;

            Vector3 shoulderForwardFlu = CoordinateFrameUtil.UnityRufToRobotFlu(shoulderRotation * Vector3.forward);
            Vector2 horizontalForwardFlu = new Vector2(shoulderForwardFlu.x, shoulderForwardFlu.y);
            if (horizontalForwardFlu.sqrMagnitude < 1e-6f)
                return false;

            horizontalForwardFlu.Normalize();
            bodyYawDegrees = NormalizeAngle(Mathf.Atan2(horizontalForwardFlu.y, horizontalForwardFlu.x) * Mathf.Rad2Deg);
            return true;
        }

        public static float ApplyBodyYawSignAndClamp(float angleDegrees, float sign, float limit)
        {
            float signed = angleDegrees * sign;
            return Mathf.Clamp(signed, -Mathf.Abs(limit), Mathf.Abs(limit));
        }

        public static float NormalizeAngle(float angle)
        {
            if (angle > 180f)
                angle -= 360f;
            if (angle < -180f)
                angle += 360f;
            return angle;
        }

        private static void BuildAntennaPayload(
            IReachySkeletonProvider provider,
            ReachyTeleopConfig config,
            Vector3 headPlaneNormal,
            out ReachyAntennaPayload antennas,
            out ReachyAntennaDebugInfo debugInfo)
        {
            antennas = new ReachyAntennaPayload();
            debugInfo = new ReachyAntennaDebugInfo();

            if (!config.sendAntennas)
                return;

            if (provider.IsLeftHandTracked() &&
                TryComputeAntennaRadians(
                    provider,
                    LeftHandIndexMetacarpalKey,
                    LeftHandIndexTipKey,
                    headPlaneNormal,
                    config.fingerToHeadPlaneMaxDegrees,
                    config.antennaMaxDegrees,
                    config.invertLeftAntenna,
                    out debugInfo.leftPlaneAngleDegrees,
                    out antennas.left))
            {
                debugInfo.leftComputed = true;
            }

            if (provider.IsRightHandTracked() &&
                TryComputeAntennaRadians(
                    provider,
                    RightHandIndexMetacarpalKey,
                    RightHandIndexTipKey,
                    headPlaneNormal,
                    config.fingerToHeadPlaneMaxDegrees,
                    config.antennaMaxDegrees,
                    config.invertRightAntenna,
                    out debugInfo.rightPlaneAngleDegrees,
                    out antennas.right))
            {
                debugInfo.rightComputed = true;
            }
        }

        private static bool TryComputeAntennaRadians(
            IReachySkeletonProvider provider,
            string indexMetacarpalKey,
            string indexTipKey,
            Vector3 headPlaneNormal,
            float maxFingerToHeadPlaneDegrees,
            float maxAntennaDegrees,
            bool invertAntenna,
            out float fingerToHeadPlaneDegrees,
            out float antennaRadians)
        {
            fingerToHeadPlaneDegrees = 0f;
            antennaRadians = 0f;

            if (!provider.TryGetTransform(indexMetacarpalKey, out var indexMetacarpal) ||
                !provider.TryGetTransform(indexTipKey, out var indexTip))
            {
                return false;
            }

            if (!TryComputeFingerToHeadPlaneAngleDegrees(
                    indexMetacarpal.position,
                    indexTip.position,
                    headPlaneNormal,
                    out fingerToHeadPlaneDegrees))
            {
                return false;
            }

            float safeRange = Mathf.Max(Mathf.Abs(maxFingerToHeadPlaneDegrees), 1e-3f);
            float normalizedAngle = Mathf.Clamp(fingerToHeadPlaneDegrees / safeRange, -1f, 1f);
            float antennaDegrees = normalizedAngle * Mathf.Abs(maxAntennaDegrees);
            if (invertAntenna)
                antennaDegrees = -antennaDegrees;

            antennaRadians = Mathf.Clamp(antennaDegrees * Mathf.Deg2Rad, -Mathf.PI, Mathf.PI);
            return true;
        }

        private static bool TryComputeFingerToHeadPlaneAngleDegrees(
            Vector3 indexMetacarpalPosition,
            Vector3 indexTipPosition,
            Vector3 headPlaneNormal,
            out float fingerToHeadPlaneDegrees)
        {
            fingerToHeadPlaneDegrees = 0f;

            Vector3 fingerDirection = indexTipPosition - indexMetacarpalPosition;
            if (fingerDirection.sqrMagnitude < 1e-6f)
                return false;
            fingerDirection.Normalize();

            if (headPlaneNormal.sqrMagnitude < 1e-6f)
                return false;
            headPlaneNormal.Normalize();

            float signedDistance = Mathf.Clamp(Vector3.Dot(fingerDirection, headPlaneNormal), -1f, 1f);
            fingerToHeadPlaneDegrees = Mathf.Asin(signedDistance) * Mathf.Rad2Deg;
            return true;
        }
    }

    public struct ReachyAntennaDebugInfo
    {
        public bool leftComputed;
        public float leftPlaneAngleDegrees;
        public bool rightComputed;
        public float rightPlaneAngleDegrees;
    }
}

