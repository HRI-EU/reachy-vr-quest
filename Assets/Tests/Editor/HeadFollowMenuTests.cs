using NUnit.Framework;
using ReachyMiniTeleop.UI;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class HeadFollowMenuTests
    {
        private GameObject _targetObject;
        private GameObject _menuObject;

        [TearDown]
        public void TearDown()
        {
            if (_menuObject != null)
                Object.DestroyImmediate(_menuObject);

            if (_targetObject != null)
                Object.DestroyImmediate(_targetObject);
        }

        [Test]
        public void CalculateDesiredPosition_PlacesMenuBelowAndInFrontOfTarget()
        {
            Quaternion yaw = HeadFollowMenu.ComputeYawRotation(Vector3.forward);

            Vector3 result = HeadFollowMenu.CalculateDesiredPosition(
                Vector3.zero,
                yaw,
                new Vector3(0f, -0.45f, 0.75f));

            Assert.AreEqual(0f, result.x, 0.0001f);
            Assert.AreEqual(-0.45f, result.y, 0.0001f);
            Assert.AreEqual(0.75f, result.z, 0.0001f);
        }

        [Test]
        public void UpdateYawAnchor_WhenInsideDeadZone_KeepsCurrentAnchor()
        {
            Quaternion anchor = Quaternion.identity;
            Quaternion targetYaw = Quaternion.Euler(0f, 5f, 0f);

            Quaternion result = HeadFollowMenu.UpdateYawAnchor(anchor, targetYaw, 8f, 1f);

            Assert.Less(Quaternion.Angle(anchor, result), 0.0001f);
        }

        [Test]
        public void UpdateYawAnchor_WhenOutsideDeadZone_MovesTowardTarget()
        {
            Quaternion anchor = Quaternion.identity;
            Quaternion targetYaw = Quaternion.Euler(0f, 30f, 0f);

            Quaternion result = HeadFollowMenu.UpdateYawAnchor(anchor, targetYaw, 8f, 0.5f);

            Assert.Greater(Quaternion.Angle(anchor, result), 0.01f);
            Assert.Less(Quaternion.Angle(result, targetYaw), Quaternion.Angle(anchor, targetYaw));
        }

        [Test]
        public void RecenterNow_SnapsMenuToCurrentTargetYaw()
        {
            _targetObject = new GameObject("Target");
            _menuObject = new GameObject("Menu");

            _targetObject.transform.SetPositionAndRotation(
                new Vector3(1f, 2f, 3f),
                Quaternion.Euler(15f, 45f, 0f));

            var followMenu = _menuObject.AddComponent<HeadFollowMenu>();
            followMenu.target = _targetObject.transform;
            followMenu.viewOffset = new Vector3(0f, -0.45f, 0.75f);
            followMenu.pitchDegrees = 40f;

            followMenu.RecenterNow();

            Quaternion expectedYaw = HeadFollowMenu.ComputeYawRotation(_targetObject.transform.forward);
            Vector3 expectedPosition = HeadFollowMenu.CalculateDesiredPosition(
                _targetObject.transform.position,
                expectedYaw,
                followMenu.viewOffset);
            Quaternion expectedRotation = HeadFollowMenu.CalculateDesiredRotation(expectedYaw, followMenu.pitchDegrees);

            Assert.Less(Vector3.Distance(expectedPosition, _menuObject.transform.position), 0.0001f);
            Assert.Less(Quaternion.Angle(expectedRotation, _menuObject.transform.rotation), 0.0001f);
        }
    }
}
