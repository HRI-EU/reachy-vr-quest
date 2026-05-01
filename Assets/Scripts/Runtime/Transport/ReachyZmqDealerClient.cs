using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using NetMQ;
using NetMQ.Sockets;
using ReachyMiniTeleop.Reachy;
using UnityEngine;

namespace ReachyMiniTeleop.Transport
{
    public sealed class ReachyZmqDealerClient : MonoBehaviour, IReachyMessageSender
    {
        private static int _instanceCount;
        private static readonly object InstanceLock = new object();

        [Header("ZMQ DEALER Settings")]
        public string endpoint = "tcp://localhost:40000";
        public string identity = "body";
        public int recvHWM = 150;
        public int sendHWM = 10;
        public bool autoStart = true;
        public bool verboseLogging = false;
        public ReachyTeleopConfig config;

        [Header("Heartbeat")]
        public float heartbeatInterval = 2f;

        public event Action<string> OnReceiveData;

        private readonly ConcurrentQueue<string> _outgoingQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _incomingQueue = new ConcurrentQueue<string>();
        private readonly ManualResetEventSlim _stopSignal = new ManualResetEventSlim(false);
        private readonly object _pollerLock = new object();

        private Thread _workerThread;
        private volatile bool _running;
        private DealerSocket _dealerSocket;
        private NetMQPoller _poller;
        private bool _netMqCleanedUp;

        public bool IsRunning => _running;
        public int PendingSendCount => _outgoingQueue.Count;

        private void Awake()
        {
            lock (InstanceLock)
            {
                _instanceCount++;
            }

            ApplyConfig(config);
        }

        private void Start()
        {
            if (autoStart)
                StartClient();
        }

        private void Update()
        {
            while (_incomingQueue.TryDequeue(out var message))
            {
                if (!string.IsNullOrEmpty(message))
                    OnReceiveData?.Invoke(message);
            }
        }

        private void OnDestroy()
        {
            StopClient();

            lock (InstanceLock)
            {
                _instanceCount--;
                if (_instanceCount == 0)
                    CleanupNetMqOnce();
            }
        }

        public void ApplyConfig(ReachyTeleopConfig config)
        {
            if (config == null)
                return;

            endpoint = config.endpoint;
            identity = config.identity;
            heartbeatInterval = config.heartbeatInterval;
        }

        public void SendMessageToServer(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return;
            }

            _outgoingQueue.Enqueue(data);
        }

        public static bool IsValidTcpEndpoint(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
                return false;

            return uri.Scheme == "tcp" && !string.IsNullOrWhiteSpace(uri.Host) && uri.Port > 0 && uri.Port <= 65535;
        }

        public void StartClient()
        {
            if (_running)
                return;

            if (!IsValidTcpEndpoint(endpoint))
            {
                Debug.LogError($"[ReachyZmqDealerClient] Invalid endpoint: {endpoint}");
                return;
            }

            _stopSignal.Reset();
            _running = true;
            _workerThread = new Thread(DealerWorker)
            {
                IsBackground = true,
                Name = "ReachyZmqDealerThread"
            };
            _workerThread.Start();
        }

        public void StopClient()
        {
            if (!_running)
                return;

            _running = false;
            _stopSignal.Set();

            lock (_pollerLock)
            {
                try { _poller?.StopAsync(); }
                catch (Exception ex) { Debug.LogWarning($"[ReachyZmqDealerClient] Poller stop exception: {ex.Message}"); }
            }

            try { _workerThread?.Join(2000); }
            catch { /* ignored */ }
            _workerThread = null;

            try { _dealerSocket?.Dispose(); }
            catch { /* ignored */ }
            _dealerSocket = null;
        }

        private void CleanupNetMqOnce()
        {
            if (_netMqCleanedUp)
                return;

            try
            {
                NetMQConfig.Cleanup(false);
                _netMqCleanedUp = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ReachyZmqDealerClient] NetMQ cleanup exception: {ex.Message}");
            }
        }

        private void DealerWorker()
        {
            AsyncIO.ForceDotNet.Force();

            while (_running && !_stopSignal.IsSet)
            {
                DealerSocket dealer = null;
                NetMQPoller localPoller = null;
                NetMQTimer heartbeatTimer = null;

                try
                {
                    dealer = new DealerSocket();
                    _dealerSocket = dealer;
                    dealer.Options.Identity = Encoding.UTF8.GetBytes(identity);
                    dealer.Options.Linger = TimeSpan.Zero;
                    dealer.Options.ReceiveHighWatermark = recvHWM;
                    dealer.Options.SendHighWatermark = sendHWM;
                    dealer.Connect(endpoint);

                    if (verboseLogging)
                        Debug.Log($"[ReachyZmqDealerClient] DEALER connected to {endpoint} as {identity}");

                    dealer.ReceiveReady += (_, args) =>
                    {
                        if (!_running || _stopSignal.IsSet)
                            return;

                        try
                        {
                            var message = new NetMQMessage();
                            if (!args.Socket.TryReceiveMultipartMessage(TimeSpan.FromMilliseconds(100), ref message) ||
                                message.FrameCount == 0)
                            {
                                return;
                            }

                            string payload = message.FrameCount > 1 && message[0].BufferSize == 0
                                ? message[1].ConvertToString()
                                : message[0].ConvertToString();
                            _incomingQueue.Enqueue(payload);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[ReachyZmqDealerClient] Receive error: {ex.Message}");
                        }
                    };

                    var sendTimer = new NetMQTimer(TimeSpan.FromMilliseconds(5));
                    sendTimer.Elapsed += (_, _) =>
                    {
                        if (!_running || _stopSignal.IsSet)
                            return;

                        int sent = 0;
                        while (_outgoingQueue.TryDequeue(out var data) && sent < 50)
                        {
                            try
                            {
                                var outMessage = new NetMQMessage();
                                outMessage.AppendEmptyFrame();
                                outMessage.Append(data);
                                dealer.TrySendMultipartMessage(outMessage);

                                if (verboseLogging)
                                    Debug.Log($"[ReachyZmqDealerClient] Sent: {data}");
                            }
                            catch (Exception ex)
                            {
                                Debug.LogWarning($"[ReachyZmqDealerClient] Send error: {ex.Message}");
                                break;
                            }

                            sent++;
                        }
                    };

                    if (heartbeatInterval > 0f)
                    {
                        heartbeatTimer = new NetMQTimer(TimeSpan.FromSeconds(heartbeatInterval));
                        heartbeatTimer.Elapsed += (_, _) =>
                        {
                            if (!_running || _stopSignal.IsSet)
                                return;

                            var heartbeat = new NetMQMessage();
                            heartbeat.AppendEmptyFrame();
                            heartbeat.Append("{\"type\":\"heartbeat\"}");
                            dealer.TrySendMultipartMessage(heartbeat);
                        };
                    }

                    localPoller = heartbeatTimer != null
                        ? new NetMQPoller { dealer, sendTimer, heartbeatTimer }
                        : new NetMQPoller { dealer, sendTimer };

                    lock (_pollerLock)
                    {
                        _poller = localPoller;
                    }

                    localPoller.RunAsync();

                    while (_running && !_stopSignal.IsSet)
                        Thread.Sleep(100);

                    if (localPoller.IsRunning)
                        localPoller.Stop();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ReachyZmqDealerClient] Connection exception: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        if (localPoller != null)
                        {
                            if (localPoller.IsRunning)
                                localPoller.Stop();
                            localPoller.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ReachyZmqDealerClient] Poller cleanup exception: {ex.Message}");
                    }
                    finally
                    {
                        lock (_pollerLock)
                        {
                            if (ReferenceEquals(_poller, localPoller))
                                _poller = null;
                        }
                    }

                    try
                    {
                        if (dealer != null)
                        {
                            dealer.Disconnect(endpoint);
                            dealer.Close();
                            dealer.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ReachyZmqDealerClient] Socket cleanup exception: {ex.Message}");
                    }

                    _dealerSocket = null;
                }

                if (_running && !_stopSignal.IsSet)
                    Thread.Sleep(1000);
            }
        }
    }
}
