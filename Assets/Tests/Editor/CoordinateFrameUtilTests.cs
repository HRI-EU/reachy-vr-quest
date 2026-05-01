using NUnit.Framework;
using ReachyMiniTeleop.Reachy;
using UnityEngine;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class CoordinateFrameUtilTests
    {
        [Test]
        public void UnityRufToRobotFlu_Position_MapsForwardLeftUp()
        {
            Vector3 result = CoordinateFrameUtil.UnityRufToRobotFlu(new Vector3(1f, 2f, 3f));

            Assert.AreEqual(new Vector3(3f, -1f, 2f), result);
        }

        [Test]
        public void UnityRufToRobotFlu_QuaternionIdentity_StaysIdentity()
        {
            Quaternion result = CoordinateFrameUtil.UnityRufToRobotFlu(Quaternion.identity);

            Assert.AreEqual(1f, Mathf.Abs(Quaternion.Dot(Quaternion.identity, result)), 0.0001f);
        }

        [Test]
        public void UnityRufToRobotFlu_Quaternion_RotatesConvertedForwardVector()
        {
            Quaternion unityRotation = Quaternion.Euler(0f, 30f, 0f);
            Quaternion convertedRotation = CoordinateFrameUtil.UnityRufToRobotFlu(unityRotation);

            Vector3 expectedForward = CoordinateFrameUtil.UnityRufToRobotFlu(unityRotation * Vector3.forward).normalized;
            Vector3 actualForward = (convertedRotation * Vector3.right).normalized;

            Assert.Less(Vector3.Angle(expectedForward, actualForward), 0.01f);
        }
    }
}
