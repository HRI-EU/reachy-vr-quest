using UnityEngine;

namespace ReachyMiniTeleop.UI
{
    public sealed class HeadFollowMenu : MonoBehaviour
    {
        [Header("References")]
        public Transform target;

        [Header("Layout")]
        public Vector3 viewOffset = new Vector3(0f, -0.45f, 0.75f);
        public float pitchDegrees = 40f;

        [Header("Follow")]
        public float angularDeadZoneDegrees = 8f;
        public float positionDeadZoneMeters = 0.05f;
        public float positionSmoothTime = 0.18f;
        public float rotationSmoothSpeed = 10f;
        public bool snapOnEnable = true;

        private Quaternion _yawAnchor = Quaternion.identity;
        private Vector3 _positionVelocity;
        private bool _hasYawAnchor;

        private void OnEnable()
        {
            _positionVelocity = Vector3.zero;
            _hasYawAnchor = false;

            if (snapOnEnable)
                RecenterNow();
        }

        private void LateUpdate()
        {
            Transform followTarget = ResolveTarget();
            if (followTarget == null)
                return;

            if (!_hasYawAnchor)
            {
                SetYawAnchor(followTarget);
                return;
            }

            float deltaTime = Time.unscaledDeltaTime;
            float rotationAlpha = GetExponentialAlpha(rotationSmoothSpeed, deltaTime);
            Quaternion targetYaw = ComputeYawRotation(followTarget.forward);
            _yawAnchor = UpdateYawAnchor(_yawAnchor, targetYaw, angularDeadZoneDegrees, rotationAlpha);

            Vector3 desiredPosition = CalculateDesiredPosition(followTarget.position, _yawAnchor, viewOffset);
            float positionDelta = Vector3.Distance(transform.position, desiredPosition);
            if (positionDelta > Mathf.Max(0f, positionDeadZoneMeters))
            {
                transform.position = Vector3.SmoothDamp(
                    transform.position,
                    desiredPosition,
                    ref _positionVelocity,
                    Mathf.Max(0.0001f, positionSmoothTime),
                    Mathf.Infinity,
                    deltaTime);
            }
            else
            {
                _positionVelocity = Vector3.zero;
            }

            Quaternion desiredRotation = CalculateDesiredRotation(_yawAnchor, pitchDegrees);
            transform.rotation = Quaternion.Slerp(transform.rotation, desiredRotation, rotationAlpha);
        }

        public void RecenterNow()
        {
            Transform followTarget = ResolveTarget();
            if (followTarget == null)
                return;

            _yawAnchor = ComputeYawRotation(followTarget.forward);
            _hasYawAnchor = true;
            _positionVelocity = Vector3.zero;
            transform.SetPositionAndRotation(
                CalculateDesiredPosition(followTarget.position, _yawAnchor, viewOffset),
                CalculateDesiredRotation(_yawAnchor, pitchDegrees));
        }

        public static Quaternion ComputeYawRotation(Vector3 forward)
        {
            Vector3 flatForward = Vector3.ProjectOnPlane(forward, Vector3.up);
            if (flatForward.sqrMagnitude < 0.000001f)
                flatForward = Vector3.forward;

            return Quaternion.LookRotation(flatForward.normalized, Vector3.up);
        }

        public static Quaternion UpdateYawAnchor(
            Quaternion currentAnchor,
            Quaternion targetYaw,
            float deadZoneDegrees,
            float interpolation)
        {
            if (Quaternion.Angle(currentAnchor, targetYaw) <= Mathf.Max(0f, deadZoneDegrees))
                return currentAnchor;

            return Quaternion.Slerp(currentAnchor, targetYaw, Mathf.Clamp01(interpolation));
        }

        public static Vector3 CalculateDesiredPosition(Vector3 targetPosition, Quaternion yawRotation, Vector3 offset)
        {
            return targetPosition + yawRotation * offset;
        }

        public static Quaternion CalculateDesiredRotation(Quaternion yawRotation, float pitchDegrees)
        {
            return yawRotation * Quaternion.Euler(pitchDegrees, 0f, 0f);
        }

        private Transform ResolveTarget()
        {
            if (target != null)
                return target;

            Camera mainCamera = Camera.main;
            return mainCamera != null ? mainCamera.transform : null;
        }

        private void SetYawAnchor(Transform followTarget)
        {
            _yawAnchor = ComputeYawRotation(followTarget.forward);
            _hasYawAnchor = true;
        }

        private static float GetExponentialAlpha(float speed, float deltaTime)
        {
            if (speed <= 0f || deltaTime <= 0f)
                return 1f;

            return 1f - Mathf.Exp(-speed * deltaTime);
        }
    }
}
