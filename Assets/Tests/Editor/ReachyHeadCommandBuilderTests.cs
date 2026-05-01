using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ReachyMiniTeleop.Reachy;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class ReachyHeadCommandBuilderTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private ReachyTeleopConfig _config;
        private ReachyHeadCommandBuilder _builder;

        [SetUp]
        public void SetUp()
        {
            _config = ScriptableObject.CreateInstance<ReachyTeleopConfig>();
            _builder = new ReachyHeadCommandBuilder();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var createdObject in _createdObjects)
            {
                if (createdObject != null)
                    Object.DestroyImmediate(createdObject);
            }

            _createdObjects.Clear();

            if (_config != null)
                Object.DestroyImmediate(_config);
        }

        [Test]
        public void TryBuildPayload_WhenRequiredKeyMissing_ReturnsFalse()
        {
            var provider = new TestSkeletonProvider();

            bool result = _builder.TryBuildPayload(provider, _config, out _, out _);

            Assert.IsFalse(result);
        }

        [Test]
        public void TryBuildPayload_BuildsRelativeHeadQuaternionAndBodyYaw()
        {
            Quaternion shoulderRotation = Quaternion.Euler(0f, 30f, 0f);
            Quaternion headRotation = Quaternion.Euler(10f, 0f, 60f);
            Quaternion expectedLocalRotation = Quaternion.Inverse(shoulderRotation) * headRotation;

            var provider = CreateBaseProvider(shoulderRotation, headRotation);

            bool result = _builder.TryBuildPayload(provider, _config, out var payload, out _);

            Assert.IsTrue(result);
            Assert.AreEqual(-30f, payload.bodyYawDegrees, 0.01f);
            Assert.AreEqual(0f, payload.headPosition.x, 0.001f);
            AssertConvertedQuaternionEquals(expectedLocalRotation, payload.headRotation.ToQuaternion(), 0.0001f);
        }

        [Test]
        public void TryBuildPayload_SerializesOpenPayloadShape()
        {
            var provider = CreateBaseProvider(Quaternion.Euler(0f, 30f, 0f), Quaternion.Euler(0f, 30f, 0f));

            bool result = _builder.TryBuildPayload(provider, _config, out var payload, out _);

            Assert.IsTrue(result);
            JObject json = JObject.Parse(JsonConvert.SerializeObject(payload));
            Assert.IsNotNull(json["body_yaw_degrees"]);
            Assert.IsNotNull(json["head_position"]);
            Assert.IsNotNull(json["head_rotation"]);
            Assert.IsNotNull(json["antennas"]);
        }

        [Test]
        public void TryBuildPayload_WhenBodyYawDisabled_StillSendsRelativeHeadRotation()
        {
            _config.sendBodyYaw = false;
            Quaternion shoulderRotation = Quaternion.Euler(0f, 20f, 0f);
            Quaternion headRotation = Quaternion.Euler(0f, 0f, 35f);
            Quaternion expectedLocalRotation = Quaternion.Inverse(shoulderRotation) * headRotation;
            var provider = CreateBaseProvider(shoulderRotation, headRotation);

            bool result = _builder.TryBuildPayload(provider, _config, out var payload, out _);

            Assert.IsTrue(result);
            Assert.AreEqual(0f, payload.bodyYawDegrees, 0.01f);
            AssertConvertedQuaternionEquals(expectedLocalRotation, payload.headRotation.ToQuaternion(), 0.0001f);
        }

        [Test]
        public void TryBuildPayload_WhenShouldersOverlap_ReturnsFalse()
        {
            var provider = CreateBaseProvider(Quaternion.identity, Quaternion.identity);
            provider.Set(ReachyHeadCommandBuilder.LeftShoulderKey, provider.Get(ReachyHeadCommandBuilder.RightShoulderKey));

            bool result = _builder.TryBuildPayload(provider, _config, out _, out _);

            Assert.IsFalse(result);
        }

        [Test]
        public void TryBuildPayload_WhenHandMissing_CentersAntenna()
        {
            var provider = CreateBaseProvider(Quaternion.identity, Quaternion.identity);
            provider.Remove(ReachyHeadCommandBuilder.LeftHandIndexMetacarpalKey);
            provider.Remove(ReachyHeadCommandBuilder.RightHandIndexTipKey);

            bool result = _builder.TryBuildPayload(provider, _config, out var payload, out var debugInfo);

            Assert.IsTrue(result);
            Assert.AreEqual(0f, payload.antennas.left, 0.001f);
            Assert.AreEqual(0f, payload.antennas.right, 0.001f);
            Assert.IsFalse(debugInfo.leftComputed);
            Assert.IsFalse(debugInfo.rightComputed);
        }

        [Test]
        public void TryBuildPayload_ComputesAntennaRadiansWhenHandsTracked()
        {
            var provider = CreateBaseProvider(Quaternion.identity, Quaternion.identity);
            provider.Set(ReachyHeadCommandBuilder.LeftHandIndexMetacarpalKey, CreateTransform("LeftIndexMcp", new Vector3(-0.2f, 1.2f, 0.2f)));
            provider.Set(ReachyHeadCommandBuilder.LeftHandIndexTipKey, CreateTransform("LeftIndexTip", new Vector3(-0.1f, 1.2f, 0.2f)));

            bool result = _builder.TryBuildPayload(provider, _config, out var payload, out var debugInfo);

            Assert.IsTrue(result);
            Assert.IsTrue(debugInfo.leftComputed);
            Assert.Less(payload.antennas.left, 0f);
        }

        [Test]
        public void ApplyBodyYawSignAndClamp_ClampsToConfiguredLimit()
        {
            float result = ReachyHeadCommandBuilder.ApplyBodyYawSignAndClamp(90f, -1f, 45f);

            Assert.AreEqual(-45f, result, 0.001f);
        }

        private TestSkeletonProvider CreateBaseProvider(Quaternion shoulderRotation, Quaternion headRotation)
        {
            Vector3 shoulderRight = shoulderRotation * Vector3.right;
            Vector3 headCenter = new Vector3(0f, 1.6f, 0f);
            var provider = new TestSkeletonProvider();

            provider.Set(ReachyHeadCommandBuilder.LeftShoulderKey, CreateTransform("LeftShoulder", -shoulderRight * 0.2f + Vector3.up));
            provider.Set(ReachyHeadCommandBuilder.RightShoulderKey, CreateTransform("RightShoulder", shoulderRight * 0.2f + Vector3.up));
            provider.Set(ReachyHeadCommandBuilder.CenterCamAnchorKey, CreateTransform("CenterCamAnchor", headCenter));
            provider.Set(ReachyHeadCommandBuilder.CenterEyeFrontKey, CreateTransform("CenterEyeFront", headCenter + headRotation * Vector3.forward * 0.1f));
            provider.Set(ReachyHeadCommandBuilder.CenterEyeUpKey, CreateTransform("CenterEyeUp", headCenter + headRotation * Vector3.up * 0.1f));
            provider.Set(ReachyHeadCommandBuilder.LeftHandIndexMetacarpalKey, CreateTransform("LeftIndexMcp", new Vector3(-0.2f, 1.2f, 0.2f)));
            provider.Set(ReachyHeadCommandBuilder.LeftHandIndexTipKey, CreateTransform("LeftIndexTip", new Vector3(-0.2f, 1.3f, 0.2f)));
            provider.Set(ReachyHeadCommandBuilder.RightHandIndexMetacarpalKey, CreateTransform("RightIndexMcp", new Vector3(0.2f, 1.2f, 0.2f)));
            provider.Set(ReachyHeadCommandBuilder.RightHandIndexTipKey, CreateTransform("RightIndexTip", new Vector3(0.2f, 1.3f, 0.2f)));

            return provider;
        }

        private Transform CreateTransform(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            _createdObjects.Add(go);
            return go.transform;
        }

        private static void AssertConvertedQuaternionEquals(Quaternion expectedUnityQuaternion, Quaternion actualFluQuaternion, float tolerance)
        {
            Quaternion expectedFlu = CoordinateFrameUtil.UnityRufToRobotFlu(expectedUnityQuaternion);
            float dot = Mathf.Abs(Quaternion.Dot(expectedFlu.normalized, actualFluQuaternion.normalized));
            Assert.GreaterOrEqual(dot, 1f - tolerance);
        }

        private sealed class TestSkeletonProvider : IReachySkeletonProvider
        {
            private readonly Dictionary<string, Transform> _transforms = new Dictionary<string, Transform>();

            public bool SkeletonReady => true;

            public void Set(string key, Transform value)
            {
                _transforms[key] = value;
            }

            public Transform Get(string key)
            {
                return _transforms[key];
            }

            public void Remove(string key)
            {
                _transforms.Remove(key);
            }

            public bool TryGetTransform(string key, out Transform boneTransform)
            {
                return _transforms.TryGetValue(key, out boneTransform) && boneTransform != null;
            }

            public bool IsLeftHandTracked()
            {
                return true;
            }

            public bool IsRightHandTracked()
            {
                return true;
            }
        }
    }
}

