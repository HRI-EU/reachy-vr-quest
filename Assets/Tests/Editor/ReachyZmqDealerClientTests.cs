using NUnit.Framework;
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
