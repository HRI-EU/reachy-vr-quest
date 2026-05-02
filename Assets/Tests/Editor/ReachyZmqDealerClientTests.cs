using NUnit.Framework;
using ReachyMiniTeleop.UI;
using ReachyMiniTeleop.Transport;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ReachyMiniTeleop.Tests.Editor
{
    public sealed class ReachyZmqDealerClientTests
    {
        private GameObject _go;
        private ReachyZmqDealerClient _client;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ReachyZmqDealerClientTests");
            _client = _go.AddComponent<ReachyZmqDealerClient>();
            _client.autoStart = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        [Test]
        public void IsValidTcpEndpoint_AcceptsTcpHostPort()
        {
            Assert.IsTrue(ReachyZmqDealerClient.IsValidTcpEndpoint("tcp://localhost:40000"));
            Assert.IsTrue(ReachyZmqDealerClient.IsValidTcpEndpoint("tcp://192.168.1.10:40000"));
        }

        [Test]
        public void IsValidTcpEndpoint_RejectsBadValues()
        {
            Assert.IsFalse(ReachyZmqDealerClient.IsValidTcpEndpoint(""));
            Assert.IsFalse(ReachyZmqDealerClient.IsValidTcpEndpoint("localhost:40000"));
            Assert.IsFalse(ReachyZmqDealerClient.IsValidTcpEndpoint("udp://localhost:40000"));
        }

        [Test]
        public void TrySetEndpoint_AcceptsValidEndpoint()
        {
            Assert.IsTrue(_client.TrySetEndpoint("tcp://192.168.1.20:40000", false));

            Assert.AreEqual("tcp://192.168.1.20:40000", _client.endpoint);
        }

        [Test]
        public void TrySetEndpoint_RejectsInvalidEndpointAndKeepsExistingValue()
        {
            _client.endpoint = "tcp://localhost:40000";

            Assert.IsFalse(_client.TrySetEndpoint("localhost:40000", false));

            Assert.AreEqual("tcp://localhost:40000", _client.endpoint);
        }

        [Test]
        public void TryBuildTcpEndpointFromHost_UsesFixedPort()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryBuildTcpEndpointFromHost(
                "192.168.1.20",
                ReachyEndpointInputController.DefaultPort,
                out string endpoint));

            Assert.AreEqual("tcp://192.168.1.20:40000", endpoint);
        }

        [Test]
        public void TryBuildTcpEndpointFromHost_TrimsInput()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryBuildTcpEndpointFromHost(
                "  192.168.1.20  ",
                ReachyEndpointInputController.DefaultPort,
                out string endpoint));

            Assert.AreEqual("tcp://192.168.1.20:40000", endpoint);
        }

        [Test]
        public void TryBuildTcpEndpointFromHost_RejectsInvalidHostInput()
        {
            Assert.IsFalse(ReachyEndpointInputController.TryBuildTcpEndpointFromHost(
                "",
                ReachyEndpointInputController.DefaultPort,
                out _));
            Assert.IsFalse(ReachyEndpointInputController.TryBuildTcpEndpointFromHost(
                "tcp://192.168.1.20",
                ReachyEndpointInputController.DefaultPort,
                out _));
            Assert.IsFalse(ReachyEndpointInputController.TryBuildTcpEndpointFromHost(
                "192.168.1.20:40000",
                ReachyEndpointInputController.DefaultPort,
                out _));
            Assert.IsFalse(ReachyEndpointInputController.TryBuildTcpEndpointFromHost(
                "not a host?",
                ReachyEndpointInputController.DefaultPort,
                out _));
        }

        [Test]
        public void TryBuildSignalingUrlFromHost_UsesVideoPort()
        {
            Assert.IsTrue(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "192.168.1.20",
                ReachyVideoInputController.DefaultSignalingPort,
                out string signalingUrl));

            Assert.AreEqual("ws://192.168.1.20:8766", signalingUrl);
        }

        [Test]
        public void TryBuildSignalingUrlFromHost_AcceptsLocalhost()
        {
            Assert.IsTrue(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "localhost",
                ReachyVideoInputController.DefaultSignalingPort,
                out string signalingUrl));

            Assert.AreEqual("ws://localhost:8766", signalingUrl);
        }

        [Test]
        public void TryBuildSignalingUrlFromHost_RejectsInvalidInput()
        {
            Assert.IsFalse(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "",
                ReachyVideoInputController.DefaultSignalingPort,
                out _));
            Assert.IsFalse(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "ws://192.168.1.20",
                ReachyVideoInputController.DefaultSignalingPort,
                out _));
            Assert.IsFalse(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "192.168.1.20:8766",
                ReachyVideoInputController.DefaultSignalingPort,
                out _));
            Assert.IsFalse(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "192.168.1.20/",
                ReachyVideoInputController.DefaultSignalingPort,
                out _));
            Assert.IsFalse(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "192.168.1.20",
                0,
                out _));
        }

        [Test]
        public void SendMessageToServer_QueuesNonEmptyPayload()
        {
            _client.SendMessageToServer("{\"ok\":true}");

            Assert.AreEqual(1, _client.PendingSendCount);
        }

        [Test]
        public void SendMessageToServer_IgnoresEmptyPayload()
        {
            _client.SendMessageToServer("");

            Assert.AreEqual(0, _client.PendingSendCount);
        }
    }
}
