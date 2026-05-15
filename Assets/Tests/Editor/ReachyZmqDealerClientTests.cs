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
        private ReachyDaemonTargetWebSocketClient _daemonClient;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("ReachyZmqDealerClientTests");
            _client = _go.AddComponent<ReachyZmqDealerClient>();
            _client.autoStart = false;
            _daemonClient = _go.AddComponent<ReachyDaemonTargetWebSocketClient>();
            _daemonClient.autoStart = false;
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
        public void IsValidTargetWebSocketUrl_AcceptsDaemonSetTargetEndpoint()
        {
            Assert.IsTrue(ReachyDaemonTargetWebSocketClient.IsValidTargetWebSocketUrl(
                "ws://localhost:8000/api/move/ws/set_target",
                out string url));

            Assert.AreEqual("ws://localhost:8000/api/move/ws/set_target", url);
        }

        [Test]
        public void IsValidTargetWebSocketUrl_RejectsWrongPath()
        {
            Assert.IsFalse(ReachyDaemonTargetWebSocketClient.IsValidTargetWebSocketUrl(
                "ws://localhost:8000/api/state/ws/full",
                out _));
        }

        [Test]
        public void TrySetEndpoint_AcceptsDaemonTargetEndpoint()
        {
            Assert.IsTrue(_daemonClient.TrySetEndpoint("ws://192.168.1.20:8000/api/move/ws/set_target", false));

            Assert.AreEqual("ws://192.168.1.20:8000/api/move/ws/set_target", _daemonClient.targetWebSocketUrl);
        }

        [Test]
        public void TryBuildDaemonTargetWebSocketUrlFromHost_UsesDaemonApiPortAndPath()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryBuildDaemonTargetWebSocketUrlFromHost(
                "192.168.1.20",
                ReachyEndpointInputController.DefaultApiPort,
                out string endpoint));

            Assert.AreEqual("ws://192.168.1.20:8000/api/move/ws/set_target", endpoint);
        }

        [Test]
        public void TryBuildDaemonApiBaseUrlFromHost_UsesApiBasePath()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryBuildDaemonApiBaseUrlFromHost(
                "192.168.1.20",
                ReachyEndpointInputController.DefaultApiPort,
                out string apiBaseUrl));

            Assert.AreEqual("http://192.168.1.20:8000/api", apiBaseUrl);
        }

        [Test]
        public void TryBuildDaemonTargetWebSocketUrlFromHost_TrimsInput()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryBuildDaemonTargetWebSocketUrlFromHost(
                "  192.168.1.20  ",
                ReachyEndpointInputController.DefaultApiPort,
                out string endpoint));

            Assert.AreEqual("ws://192.168.1.20:8000/api/move/ws/set_target", endpoint);
        }

        [Test]
        public void TryBuildDaemonTargetWebSocketUrlFromHost_RejectsInvalidHostInput()
        {
            Assert.IsFalse(ReachyEndpointInputController.TryBuildDaemonTargetWebSocketUrlFromHost(
                "",
                ReachyEndpointInputController.DefaultApiPort,
                out _));
            Assert.IsFalse(ReachyEndpointInputController.TryBuildDaemonTargetWebSocketUrlFromHost(
                "tcp://192.168.1.20",
                ReachyEndpointInputController.DefaultApiPort,
                out _));
            Assert.IsFalse(ReachyEndpointInputController.TryBuildDaemonTargetWebSocketUrlFromHost(
                "192.168.1.20:8000",
                ReachyEndpointInputController.DefaultApiPort,
                out _));
            Assert.IsFalse(ReachyEndpointInputController.TryBuildDaemonTargetWebSocketUrlFromHost(
                "not a host?",
                ReachyEndpointInputController.DefaultApiPort,
                out _));
        }

        [Test]
        public void TryBuildSignalingUrlFromHost_UsesVideoPort()
        {
            Assert.IsTrue(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "192.168.1.20",
                ReachyVideoInputController.DefaultSignalingPort,
                out string signalingUrl));

            Assert.AreEqual("ws://192.168.1.20:8443", signalingUrl);
        }

        [Test]
        public void TryBuildSignalingUrlFromHost_AcceptsLocalhost()
        {
            Assert.IsTrue(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "localhost",
                ReachyVideoInputController.DefaultSignalingPort,
                out string signalingUrl));

            Assert.AreEqual("ws://localhost:8443", signalingUrl);
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
                "192.168.1.20:8443",
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

        [Test]
        public void DaemonSendMessageToServer_QueuesNonEmptyPayload()
        {
            _daemonClient.SendMessageToServer("{\"ok\":true}");

            Assert.AreEqual(1, _daemonClient.PendingSendCount);
        }

        [Test]
        public void DaemonSendMessageToServer_DropsOldMessagesWhenQueueIsFull()
        {
            _daemonClient.maxQueuedMessages = 1;

            _daemonClient.SendMessageToServer("{\"n\":1}");
            _daemonClient.SendMessageToServer("{\"n\":2}");

            Assert.AreEqual(1, _daemonClient.PendingSendCount);
        }
    }
}
