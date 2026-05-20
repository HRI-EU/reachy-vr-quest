using System;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using ReachyMiniTeleop.UI;
using ReachyMiniTeleop.Transport;
using UnityEngine;
using UnityEngine.TestTools;
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
        public void TryBuildApiEndpointUrl_AppendsMediaPath()
        {
            Assert.IsTrue(ReachyDaemonTargetWebSocketClient.TryBuildApiEndpointUrl(
                "http://192.168.1.20:8000/api",
                ReachyDaemonTargetWebSocketClient.DefaultPlaySoundPath,
                out string endpointUrl));

            Assert.AreEqual("http://192.168.1.20:8000/api/media/play_sound", endpointUrl);
        }

        [Test]
        public void TryBuildApiEndpointUrl_NormalizesTrailingSlashAndRelativePath()
        {
            Assert.IsTrue(ReachyDaemonTargetWebSocketClient.TryBuildApiEndpointUrl(
                "http://192.168.1.20:8000/api/",
                "media/stop_sound",
                out string endpointUrl));

            Assert.AreEqual("http://192.168.1.20:8000/api/media/stop_sound", endpointUrl);
        }

        [Test]
        public void TryBuildPlaySoundJson_UsesFileField()
        {
            Assert.IsTrue(ReachyDaemonTargetWebSocketClient.TryBuildPlaySoundJson(
                " wake_up.wav ",
                out string json));

            Assert.AreEqual("{\"file\":\"wake_up.wav\"}", json);
        }

        [Test]
        public void TryBuildPlaySoundJson_RejectsEmptyFilename()
        {
            Assert.IsFalse(ReachyDaemonTargetWebSocketClient.TryBuildPlaySoundJson("", out _));
            Assert.IsFalse(ReachyDaemonTargetWebSocketClient.TryBuildPlaySoundJson("   ", out _));
        }

        [Test]
        public void FormatHttpResultStatus_ReportsSuccessfulSoundRequest()
        {
            string status = ReachyDaemonTargetWebSocketClient.FormatHttpResultStatus(
                "Face sound",
                200,
                null,
                "{\"status\":\"ok\"}",
                true);

            Assert.AreEqual("Face sound HTTP 200: ok", status);
        }

        [Test]
        public void FormatHttpResultStatus_ExtractsFastApiDetail()
        {
            string status = ReachyDaemonTargetWebSocketClient.FormatHttpResultStatus(
                "Face sound",
                503,
                null,
                "{\"detail\":\"Backend not running\"}",
                false);

            Assert.AreEqual("Face sound HTTP 503: Backend not running", status);
        }

        [Test]
        public void FormatHttpResultStatus_ReportsNetworkFailureWithoutStatusCode()
        {
            string status = ReachyDaemonTargetWebSocketClient.FormatHttpResultStatus(
                "Face sound",
                0,
                "Cannot connect to destination host",
                null,
                false);

            Assert.AreEqual("Face sound HTTP network: Cannot connect to destination host", status);
        }

        [Test]
        public void FormatHttpRequestStatus_IncludesTargetUrl()
        {
            string status = ReachyDaemonTargetWebSocketClient.FormatHttpRequestStatus(
                "Face sound",
                "http://10.0.71.91:8000/api/media/play_sound");

            Assert.AreEqual(
                "Face sound request: http://10.0.71.91:8000/api/media/play_sound",
                status);
        }

        [Test]
        public void DaemonPlaySound_DoesNotQueueHttpWorkWhenSoundDisabled()
        {
            _daemonClient.soundEnabled = false;
            bool receivedResult = false;
            _daemonClient.HttpRequestCompleted += result =>
            {
                receivedResult = !result.Success && result.Error == "Sound disabled";
            };

            _daemonClient.PlaySound("count.wav");
            InvokeDaemonUpdate();

            Assert.IsTrue(receivedResult);
            Assert.AreEqual(0, GetDaemonField<int>("httpWorkerStartCountForTests"));
        }

        [Test]
        public void TryBuildDaemonTargetWebSocketUrlFromHost_UsesCustomPosePort()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryBuildDaemonTargetWebSocketUrlFromHost(
                "192.168.1.20",
                9000,
                out string endpoint));

            Assert.AreEqual("ws://192.168.1.20:9000/api/move/ws/set_target", endpoint);
        }

        [Test]
        public void TryBuildDaemonApiBaseUrlFromHost_UsesCustomPosePort()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryBuildDaemonApiBaseUrlFromHost(
                "192.168.1.20",
                9000,
                out string apiBaseUrl));

            Assert.AreEqual("http://192.168.1.20:9000/api", apiBaseUrl);
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
        public void TryBuildSignalingUrlFromHost_UsesCustomVideoPort()
        {
            Assert.IsTrue(ReachyVideoInputController.TryBuildSignalingUrlFromHost(
                "192.168.1.20",
                9443,
                out string signalingUrl));

            Assert.AreEqual("ws://192.168.1.20:9443", signalingUrl);
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
        public void TryParsePortInput_UsesFallbackForEmptyInput()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryParsePortInput(
                "",
                ReachyEndpointInputController.DefaultApiPort,
                out int port));

            Assert.AreEqual(ReachyEndpointInputController.DefaultApiPort, port);
        }

        [Test]
        public void TryParsePortInput_AcceptsValidPort()
        {
            Assert.IsTrue(ReachyEndpointInputController.TryParsePortInput("9443", 8443, out int port));

            Assert.AreEqual(9443, port);
        }

        [Test]
        public void TryParsePortInput_RejectsInvalidPorts()
        {
            Assert.IsFalse(ReachyEndpointInputController.TryParsePortInput("0", 8000, out _));
            Assert.IsFalse(ReachyEndpointInputController.TryParsePortInput("65536", 8000, out _));
            Assert.IsFalse(ReachyEndpointInputController.TryParsePortInput("abc", 8000, out _));
            Assert.IsFalse(ReachyEndpointInputController.TryParsePortInput("ws://192.168.1.20", 8000, out _));
            Assert.IsFalse(ReachyEndpointInputController.TryParsePortInput("192.168.1.20:8000", 8000, out _));
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

        [Test]
        public void DaemonStartClient_StartsWorkerThreadAndSetsConnectingState()
        {
            _daemonClient.autoWakeOnStart = false;
            using var workerStarted = new ManualResetEventSlim(false);
            using var releaseWorker = new ManualResetEventSlim(false);
            SetDaemonWorkerOverride((_, _) =>
            {
                workerStarted.Set();
                releaseWorker.Wait(1000);
            });

            _daemonClient.StartClient();

            Assert.IsTrue(workerStarted.Wait(1000));
            Assert.IsTrue(_daemonClient.IsConnectedOrConnecting);

            releaseWorker.Set();
            _daemonClient.StopClient();
        }

        [Test]
        public void DaemonSendMessageToServer_SignalsWorker()
        {
            _daemonClient.SendMessageToServer("{\"ok\":true}");

            Assert.AreEqual(1, GetDaemonField<int>("sendSignalSetCountForTests"));
        }

        [Test]
        public void DaemonPlaySound_UsesSingleHttpWorkerAndCoalescesPendingRequests()
        {
            _daemonClient.apiBaseUrl = "http://127.0.0.1:8000/api";
            using var firstRequestStarted = new ManualResetEventSlim(false);
            using var releaseRequest = new ManualResetEventSlim(false);
            int rawCallCount = 0;

            SetDaemonField(
                "rawHttpResponseCodeOverrideForTests",
                new Func<Uri, string, int, long>((_, _, _) =>
                {
                    int call = Interlocked.Increment(ref rawCallCount);
                    if (call == 1)
                    {
                        firstRequestStarted.Set();
                        releaseRequest.Wait(1000);
                    }

                    return 200;
                }));

            _daemonClient.PlaySound("count.wav");
            Assert.IsTrue(firstRequestStarted.Wait(1000));

            _daemonClient.PlaySound("dance1.wav");
            _daemonClient.PlaySound("wake_up.wav");

            Assert.IsTrue(InvokeTryGetQueuedSoundHttpWork(out _, out _, out string pendingBody));
            StringAssert.Contains("wake_up.wav", pendingBody);

            releaseRequest.Set();
            WaitUntil(() => GetDaemonField<int>("rawHttpPostStartCountForTests") >= 2, 1000);

            Assert.AreEqual(1, GetDaemonField<int>("httpWorkerStartCountForTests"));
            Assert.AreEqual(1, GetDaemonField<int>("rawHttpPostMaxConcurrentForTests"));
            Assert.LessOrEqual(GetDaemonField<int>("rawHttpPostStartCountForTests"), 2);
        }

        [Test]
        public void DaemonPlaySound_ReportsHttpResultOnMainThreadUpdate()
        {
            _daemonClient.apiBaseUrl = "http://127.0.0.1:8000/api";
            SetDaemonField(
                "rawHttpResponseCodeOverrideForTests",
                new Func<Uri, string, int, long>((_, _, _) => 200));

            bool receivedResult = false;
            _daemonClient.HttpRequestCompleted += result =>
            {
                receivedResult = result.Success && result.ResponseCode == 200;
            };

            _daemonClient.PlaySound("count.wav");
            WaitUntil(() => GetDaemonField<int>("rawHttpPostStartCountForTests") >= 1, 1000);

            Assert.IsFalse(receivedResult);

            WaitUntil(() =>
            {
                InvokeDaemonUpdate();
                return receivedResult;
            }, 1000);

            Assert.IsTrue(receivedResult);
        }

        [Test]
        public void DaemonWorker_DequeuesNewestPayloadOnly()
        {
            _daemonClient.SendMessageToServer("{\"n\":1}");
            _daemonClient.SendMessageToServer("{\"n\":2}");

            Assert.IsTrue(InvokeTryDequeueLatestOutgoing(out string latest));

            Assert.AreEqual("{\"n\":2}", latest);
            Assert.AreEqual(0, _daemonClient.PendingSendCount);
        }

        [Test]
        public void DaemonStopClient_RequestsWorkerStopAndClearsState()
        {
            _daemonClient.autoWakeOnStart = false;
            var stopSignal = GetDaemonField<ManualResetEventSlim>("_stopSignal");
            using var workerStarted = new ManualResetEventSlim(false);
            using var workerStopped = new ManualResetEventSlim(false);
            SetDaemonWorkerOverride((_, _) =>
            {
                workerStarted.Set();
                stopSignal.Wait(1000);
                workerStopped.Set();
            });

            _daemonClient.StartClient();
            Assert.IsTrue(workerStarted.Wait(1000));

            _daemonClient.StopClient();

            Assert.IsTrue(workerStopped.Wait(1000));
            Assert.IsFalse(_daemonClient.IsConnectedOrConnecting);
        }

        [Test]
        public void DaemonWorkerError_QueuesWarningForMainThread()
        {
            _daemonClient.autoWakeOnStart = false;
            SetDaemonWorkerOverride((_, _) => throw new InvalidOperationException("boom"));

            _daemonClient.StartClient();
            WaitForDaemonWorkerExit();

            LogAssert.Expect(LogType.Warning, "[ReachyDaemonTargetWebSocketClient] Worker error: boom");
            InvokeDaemonUpdate();
        }

        private void SetDaemonWorkerOverride(Action<string, int> workerLoop)
        {
            SetDaemonField("workerLoopOverride", workerLoop);
        }

        private bool InvokeTryDequeueLatestOutgoing(out string latest)
        {
            MethodInfo method = typeof(ReachyDaemonTargetWebSocketClient).GetMethod(
                "TryDequeueLatestOutgoing",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, "Missing private TryDequeueLatestOutgoing method.");

            object[] args = { null };
            bool result = (bool)method.Invoke(_daemonClient, args);
            latest = (string)args[0];
            return result;
        }

        private bool InvokeTryGetQueuedSoundHttpWork(out string operationName, out string url, out string body)
        {
            MethodInfo method = typeof(ReachyDaemonTargetWebSocketClient).GetMethod(
                "TryGetQueuedSoundHttpWorkForTests",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, "Missing private TryGetQueuedSoundHttpWorkForTests method.");

            object[] args = { null, null, null };
            bool result = (bool)method.Invoke(_daemonClient, args);
            operationName = (string)args[0];
            url = (string)args[1];
            body = (string)args[2];
            return result;
        }

        private void InvokeDaemonUpdate()
        {
            MethodInfo update = typeof(ReachyDaemonTargetWebSocketClient).GetMethod(
                "Update",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(update, "Missing private Update method.");
            update.Invoke(_daemonClient, null);
        }

        private T GetDaemonField<T>(string fieldName)
        {
            FieldInfo field = typeof(ReachyDaemonTargetWebSocketClient).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            return (T)field.GetValue(_daemonClient);
        }

        private void SetDaemonField(string fieldName, object value)
        {
            FieldInfo field = typeof(ReachyDaemonTargetWebSocketClient).GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(_daemonClient, value);
        }

        private void WaitForDaemonWorkerExit()
        {
            Thread worker = GetDaemonField<Thread>("_workerThread");
            if (worker != null)
                worker.Join(1000);
        }

        private static void WaitUntil(Func<bool> predicate, int timeoutMilliseconds)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                    return;

                Thread.Sleep(10);
            }

            Assert.Fail("Timed out waiting for condition.");
        }
    }
}
