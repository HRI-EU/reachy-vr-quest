using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using ReachyMiniTeleop.Reachy;
using UnityEngine;
using UnityEngine.Networking;
using WebSocketSharp;

namespace ReachyMiniTeleop.Transport
{
    public sealed class ReachyDaemonTargetWebSocketClient : MonoBehaviour, IReachyMessageSender
    {
        public const int DefaultApiPort = 9000;
        public const string DefaultTargetPath = "/api/move/ws/set_target";
        public const string DefaultApiPath = "/api";

        [Header("Reachy Daemon")]
        public string targetWebSocketUrl = "ws://192.168.0.210:9000/api/move/ws/set_target";
        public string apiBaseUrl = "http://192.168.0.210:9000/api";
        public bool autoStart = false;
        public bool autoWakeOnStart = true;
        public bool verboseLogging = false;

        [Header("Queue")]
        public int maxQueuedMessages = 5;
        [Tooltip("Legacy sync-send cap. Worker-thread sending drains the queue to the newest payload instead.")]
        public int maxSendsPerFrame = 1;

        [Header("HTTP")]
        public int requestTimeoutSeconds = 3;

        public event Action<string> OnReceiveData;
        public event Action<bool> ConnectionStateChanged;

        private readonly ConcurrentQueue<string> _outgoingQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _incomingQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<MainThreadLog> _mainThreadLogs = new ConcurrentQueue<MainThreadLog>();
        private readonly ConcurrentQueue<bool> _connectionStateQueue = new ConcurrentQueue<bool>();
        private readonly ManualResetEventSlim _stopSignal = new ManualResetEventSlim(false);
        private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);
        private Coroutine _wakeCoroutine;
        private Thread _workerThread;
        private volatile bool _running;
        private volatile bool _isConnecting;
        private volatile bool _isAlive;
        private int _workerGeneration;

#if UNITY_EDITOR
        internal Action<string, int> workerLoopOverride;
        internal int sendSignalSetCountForTests;
#endif

        public bool IsRunning => _isAlive;
        public bool IsConnectedOrConnecting => _isConnecting || _isAlive;
        public int PendingSendCount => _outgoingQueue.Count;

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

            while (_mainThreadLogs.TryDequeue(out var log))
            {
                if (log.isWarning)
                    Debug.LogWarning(log.message);
                else
                    Debug.Log(log.message);
            }

            while (_connectionStateQueue.TryDequeue(out bool isConnectedOrConnecting))
            {
                ConnectionStateChanged?.Invoke(isConnectedOrConnecting);
            }
        }

        private void OnDestroy()
        {
            StopClient();
        }

        public void SendMessageToServer(string data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return;

            int limit = Mathf.Max(1, maxQueuedMessages);
            while (_outgoingQueue.Count >= limit && _outgoingQueue.TryDequeue(out _))
            {
            }

            _outgoingQueue.Enqueue(data);
            SignalWorkerForSend();
        }

        public bool TrySetEndpoint(string value, bool restartIfRunning)
        {
            if (!IsValidTargetWebSocketUrl(value, out string normalizedUrl))
                return false;

            bool wasRunning = IsConnectedOrConnecting;
            if (wasRunning)
            {
                if (!restartIfRunning)
                    return false;

                StopClient();
            }

            targetWebSocketUrl = normalizedUrl;

            if (wasRunning)
                StartClient();

            return true;
        }

        public bool TrySetApiBaseUrl(string value)
        {
            if (!IsValidApiBaseUrl(value, out string normalizedUrl))
                return false;

            apiBaseUrl = normalizedUrl;
            return true;
        }

        public void StartClient()
        {
            if (IsConnectedOrConnecting)
                return;

            if (!IsValidTargetWebSocketUrl(targetWebSocketUrl, out string normalizedTargetUrl))
            {
                Debug.LogError($"[ReachyDaemonTargetWebSocketClient] Invalid target WebSocket URL: {targetWebSocketUrl}");
                return;
            }

            targetWebSocketUrl = normalizedTargetUrl;
            _stopSignal.Reset();
            _running = true;
            _isConnecting = true;
            _isAlive = false;
            int generation = Interlocked.Increment(ref _workerGeneration);
            ConnectionStateChanged?.Invoke(true);

            _workerThread = new Thread(() => WorkerLoop(targetWebSocketUrl, generation))
            {
                IsBackground = true,
                Name = "ReachyDaemonTargetWebSocketThread"
            };
            _workerThread.Start();

            if (autoWakeOnStart)
            {
                if (_wakeCoroutine != null)
                    StopCoroutine(_wakeCoroutine);
                _wakeCoroutine = StartCoroutine(WakeIfNeededRoutine());
            }
        }

        public void StopClient()
        {
            if (_wakeCoroutine != null)
            {
                StopCoroutine(_wakeCoroutine);
                _wakeCoroutine = null;
            }

            if (!_running && !_isConnecting && !_isAlive)
                return;

            _running = false;
            _isConnecting = false;
            _isAlive = false;
            Interlocked.Increment(ref _workerGeneration);
            _stopSignal.Set();
            _sendSignal.Set();

            try
            {
                if (_workerThread != null && _workerThread.IsAlive && _workerThread != Thread.CurrentThread)
                    _workerThread.Join(2000);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] Worker stop error: {ex.Message}");
            }
            finally
            {
                if (_workerThread != null && !_workerThread.IsAlive)
                    _workerThread = null;
                ConnectionStateChanged?.Invoke(false);
            }
        }

        private void WorkerLoop(string url, int generation)
        {
#if UNITY_EDITOR
            if (workerLoopOverride != null)
            {
                try
                {
                    workerLoopOverride(url, generation);
                }
                catch (Exception ex)
                {
                    if (IsCurrentWorker(generation))
                        EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] Worker error: {ex.Message}", true);
                }
                finally
                {
                    FinishWorker(generation);
                }

                return;
            }
#endif

            WebSocket webSocket = null;

            try
            {
                webSocket = CreateWebSocket(url, generation);
                webSocket.Connect();

                while (_running && !_stopSignal.IsSet && webSocket.IsAlive)
                {
                    WaitHandle.WaitAny(new[] { _stopSignal.WaitHandle, _sendSignal }, 50);

                    if (!_running || _stopSignal.IsSet || !webSocket.IsAlive)
                        break;

                    if (!TryDequeueLatestOutgoing(out string data))
                        continue;

                    try
                    {
                        webSocket.Send(data);

                        if (verboseLogging)
                            EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] Sent: {data}", false);
                    }
                    catch (Exception ex)
                    {
                        EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] Send error: {ex.Message}", true);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                if (IsCurrentWorker(generation) && _running)
                    EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] Connection exception: {ex.Message}", true);
            }
            finally
            {
                try
                {
                    if (webSocket != null && webSocket.IsAlive)
                        webSocket.Close();
                }
                catch (Exception ex)
                {
                    if (IsCurrentWorker(generation))
                        EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] Close error: {ex.Message}", true);
                }

                FinishWorker(generation);
            }
        }

        private WebSocket CreateWebSocket(string url, int generation)
        {
            var webSocket = new WebSocket(url)
            {
                EmitOnPing = true,
                WaitTime = TimeSpan.FromSeconds(Math.Max(1, requestTimeoutSeconds))
            };

            webSocket.OnOpen += (_, _) =>
            {
                if (!IsCurrentWorker(generation))
                    return;

                _isConnecting = false;
                _isAlive = true;

                if (verboseLogging)
                    EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] Connected to {url}", false);
            };
            webSocket.OnMessage += (_, args) =>
            {
                string data = args.Data;
                if (!string.IsNullOrEmpty(data))
                    _incomingQueue.Enqueue(data);
            };
            webSocket.OnError += (_, args) =>
            {
                if (!IsCurrentWorker(generation))
                    return;

                _isConnecting = false;
                _isAlive = false;
                EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] WebSocket error: {args.Message}", true);
                EnqueueConnectionState(false);
            };
            webSocket.OnClose += (_, args) =>
            {
                if (!IsCurrentWorker(generation))
                    return;

                _isConnecting = false;
                _isAlive = false;

                if (verboseLogging)
                    EnqueueMainThreadLog($"[ReachyDaemonTargetWebSocketClient] Closed: {args.Reason}", false);

                EnqueueConnectionState(false);
            };

            return webSocket;
        }

        private bool TryDequeueLatestOutgoing(out string latest)
        {
            latest = null;

            while (_outgoingQueue.TryDequeue(out var data))
                latest = data;

            return latest != null;
        }

        private void FinishWorker(int generation)
        {
            if (!IsCurrentWorker(generation))
                return;

            _running = false;
            _isConnecting = false;
            _isAlive = false;
            EnqueueConnectionState(false);
        }

        private bool IsCurrentWorker(int generation)
        {
            return generation == Volatile.Read(ref _workerGeneration);
        }

        private void SignalWorkerForSend()
        {
#if UNITY_EDITOR
            Interlocked.Increment(ref sendSignalSetCountForTests);
#endif
            _sendSignal.Set();
        }

        private void EnqueueConnectionState(bool isConnectedOrConnecting)
        {
            _connectionStateQueue.Enqueue(isConnectedOrConnecting);
        }

        private void EnqueueMainThreadLog(string message, bool isWarning)
        {
            _mainThreadLogs.Enqueue(new MainThreadLog(message, isWarning));
        }

        public static bool IsValidTargetWebSocketUrl(string url, out string normalizedUrl)
        {
            normalizedUrl = null;
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.Port > 65535)
                return false;

            normalizedUrl = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + uri.AbsolutePath;
            return normalizedUrl.EndsWith(DefaultTargetPath, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsValidApiBaseUrl(string url, out string normalizedUrl)
        {
            normalizedUrl = null;
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return false;

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0 || uri.Port > 65535)
                return false;

            normalizedUrl = (uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped) + uri.AbsolutePath).TrimEnd('/');
            return normalizedUrl.EndsWith(DefaultApiPath, StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerator WakeIfNeededRoutine()
        {
            if (!IsValidApiBaseUrl(apiBaseUrl, out string baseUrl))
            {
                _wakeCoroutine = null;
                yield break;
            }

            using (UnityWebRequest stateRequest = UnityWebRequest.Get($"{baseUrl}/state/full"))
            {
                stateRequest.timeout = Mathf.Max(1, requestTimeoutSeconds);
                yield return stateRequest.SendWebRequest();

                if (stateRequest.result != UnityWebRequest.Result.Success)
                {
                    if (verboseLogging)
                        Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] State check failed: {stateRequest.error}");
                    _wakeCoroutine = null;
                    yield break;
                }

                string stateJson = stateRequest.downloadHandler != null ? stateRequest.downloadHandler.text : string.Empty;
                if (stateJson.IndexOf("\"control_mode\":\"disabled\"", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _wakeCoroutine = null;
                    yield break;
                }
            }

            yield return PostEmpty($"{baseUrl}/motors/set_mode/enabled");
            yield return PostEmpty($"{baseUrl}/move/play/wake_up");
            _wakeCoroutine = null;
        }

        private IEnumerator PostEmpty(string url)
        {
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.uploadHandler = new UploadHandlerRaw(Array.Empty<byte>());
                request.timeout = Mathf.Max(1, requestTimeoutSeconds);
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success && verboseLogging)
                    Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] POST {url} failed: {request.error}");
            }
        }

        private readonly struct MainThreadLog
        {
            public readonly string message;
            public readonly bool isWarning;

            public MainThreadLog(string message, bool isWarning)
            {
                this.message = message;
                this.isWarning = isWarning;
            }
        }
    }
}
