using System.Collections.Generic;
using ReachyMiniTeleop.Reachy;
using UnityEngine;

namespace ReachyMiniTeleop.Debugging
{
    public sealed class MockReachySkeletonProvider : MonoBehaviour, IReachySkeletonProvider
    {
        public bool skeletonReady = true;
        public bool leftHandTracked = true;
        public bool rightHandTracked = true;
        public bool animateHead = true;

        private readonly Dictionary<string, Transform> _transforms = new Dictionary<string, Transform>();

        public bool SkeletonReady => skeletonReady;

        private void Awake()
        {
            EnsureTransform(ReachyHeadCommandBuilder.LeftShoulderKey, new Vector3(-0.18f, 1.35f, 0f));
            EnsureTransform(ReachyHeadCommandBuilder.RightShoulderKey, new Vector3(0.18f, 1.35f, 0f));
            EnsureTransform(ReachyHeadCommandBuilder.CenterCamAnchorKey, new Vector3(0f, 1.65f, 0f));
            EnsureTransform(ReachyHeadCommandBuilder.CenterEyeFrontKey, new Vector3(0f, 1.65f, 0.12f));
            EnsureTransform(ReachyHeadCommandBuilder.CenterEyeUpKey, new Vector3(0f, 1.77f, 0f));
            EnsureTransform(ReachyHeadCommandBuilder.LeftHandIndexMetacarpalKey, new Vector3(-0.25f, 1.25f, 0.3f));
            EnsureTransform(ReachyHeadCommandBuilder.LeftHandIndexTipKey, new Vector3(-0.12f, 1.3f, 0.35f));
            EnsureTransform(ReachyHeadCommandBuilder.RightHandIndexMetacarpalKey, new Vector3(0.25f, 1.25f, 0.3f));
            EnsureTransform(ReachyHeadCommandBuilder.RightHandIndexTipKey, new Vector3(0.12f, 1.3f, 0.35f));
        }

        private void Update()
        {
            if (!animateHead)
                return;

            float yaw = Mathf.Sin(Time.time * 0.5f) * 20f;
            float roll = Mathf.Sin(Time.time * 0.8f) * 12f;
            Quaternion headRotation = Quaternion.Euler(0f, yaw, roll);
            Transform head = _transforms[ReachyHeadCommandBuilder.CenterCamAnchorKey];
            _transforms[ReachyHeadCommandBuilder.CenterEyeFrontKey].position = head.position + headRotation * Vector3.forward * 0.12f;
            _transforms[ReachyHeadCommandBuilder.CenterEyeUpKey].position = head.position + headRotation * Vector3.up * 0.12f;
        }

        public bool TryGetTransform(string key, out Transform boneTransform)
        {
            return _transforms.TryGetValue(key, out boneTransform) && boneTransform != null;
        }

        public bool IsLeftHandTracked()
        {
            return leftHandTracked;
        }

        public bool IsRightHandTracked()
        {
            return rightHandTracked;
        }

        private void EnsureTransform(string key, Vector3 position)
        {
            var child = new GameObject(key).transform;
            child.SetParent(transform, false);
            child.position = position;
            _transforms[key] = child;
        }
    }
}

