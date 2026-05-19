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
        public const int DefaultApiPort = 8000;
        public const string DefaultTargetPath = "/api/move/ws/set_target";
        public const string DefaultApiPath = "/api";
        public const string DefaultPlaySoundPath = "/media/play_sound";
        public const string DefaultStopSoundPath = "/media/stop_sound";

        [Header("Reachy Daemon")]
        public string targetWebSocketUrl = "ws://192.168.0.210:8000/api/move/ws/set_target";
        public string apiBaseUrl = "http://192.168.0.210:8000/api";
        public bool autoStart = false;
        public bool autoWakeOnStart = true;
        public bool verboseLogging = false;

        [Header("Queue")]
        public int maxQueuedMessages = 5;
        [Tooltip("Legacy sync-send cap. Worker-thread sending drains the queue to the newest payload instead.")]
        public int maxSendsPerFrame = 1;

        [Header("HTTP")]
        public int requestTimeoutSeconds = 3;

        [Header("Sound")]
        public string defaultSoundFile = "wake_up.wav";

        public event Action<string> OnReceiveData;
        public event Action<bool> ConnectionStateChanged;
        public event Action<DaemonHttpRequest> HttpRequestStarted;
        public event Action<DaemonHttpResult> HttpRequestCompleted;

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

        public void PlayDefaultSound()
        {
            PlaySound(defaultSoundFile);
        }

        public void PlaySound(string file)
        {
            if (!TryBuildApiEndpointUrl(apiBaseUrl, DefaultPlaySoundPath, out string url))
            {
                Debug.LogError($"[ReachyDaemonTargetWebSocketClient] Invalid daemon API base URL: {apiBaseUrl}");
                return;
            }

            if (!TryBuildPlaySoundJson(file, out string json))
            {
                Debug.LogError("[ReachyDaemonTargetWebSocketClient] Enter a sound filename before playing sound.");
                return;
            }

            StartCoroutine(PostJson(url, json, "Face sound"));
        }

        public void StopSound()
        {
            if (!TryBuildApiEndpointUrl(apiBaseUrl, DefaultStopSoundPath, out string url))
            {
                Debug.LogError($"[ReachyDaemonTargetWebSocketClient] Invalid daemon API base URL: {apiBaseUrl}");
                return;
            }

            StartCoroutine(PostEmpty(url, "Stop sound"));
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

        public static bool TryBuildApiEndpointUrl(string apiBaseUrl, string endpointPath, out string endpointUrl)
        {
            endpointUrl = null;

            if (!IsValidApiBaseUrl(apiBaseUrl, out string normalizedBaseUrl) ||
                string.IsNullOrWhiteSpace(endpointPath))
            {
                return false;
            }

            string normalizedPath = endpointPath.Trim();
            if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
                normalizedPath = "/" + normalizedPath;

            endpointUrl = normalizedBaseUrl + normalizedPath;
            return Uri.TryCreate(endpointUrl, UriKind.Absolute, out _);
        }

        public static bool TryBuildPlaySoundJson(string file, out string json)
        {
            json = null;
            if (string.IsNullOrWhiteSpace(file))
                return false;

            json = JsonUtility.ToJson(new PlaySoundRequest(file.Trim()));
            return true;
        }

        public static string FormatHttpResultStatus(
            string operationName,
            long responseCode,
            string error,
            string detail,
            bool success)
        {
            string operation = string.IsNullOrWhiteSpace(operationName) ? "HTTP" : operationName.Trim();
            string code = responseCode > 0 ? responseCode.ToString() : "network";
            if (success)
                return $"{operation} HTTP {code}: ok";

            string message = ExtractHttpDetail(detail);
            if (string.IsNullOrWhiteSpace(message))
                message = string.IsNullOrWhiteSpace(error) ? "failed" : error.Trim();

            return $"{operation} HTTP {code}: {TrimHttpMessage(message)}";
        }

        public static string FormatHttpRequestStatus(string operationName, string url)
        {
            string operation = string.IsNullOrWhiteSpace(operationName) ? "HTTP" : operationName.Trim();
            string target = string.IsNullOrWhiteSpace(url) ? "unknown URL" : url.Trim();
            return $"{operation} request: {target}";
        }

        private static string ExtractHttpDetail(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail))
                return string.Empty;

            string trimmed = detail.Trim();
            const string detailToken = "\"detail\":\"";
            int detailStart = trimmed.IndexOf(detailToken, StringComparison.OrdinalIgnoreCase);
            if (detailStart >= 0)
            {
                int valueStart = detailStart + detailToken.Length;
                int valueEnd = trimmed.IndexOf('"', valueStart);
                if (valueEnd > valueStart)
                    return trimmed.Substring(valueStart, valueEnd - valueStart);
            }

            return trimmed;
        }

        private static string TrimHttpMessage(string message)
        {
            string trimmed = message.Trim();
            const int maxLength = 96;
            if (trimmed.Length <= maxLength)
                return trimmed;

            return trimmed.Substring(0, maxLength - 3) + "...";
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
            yield return PostEmpty($"{baseUrl}/move/play/wake_up", "wake up");
            _wakeCoroutine = null;
        }

        private IEnumerator PostEmpty(string url, string operationName = "POST")
        {
            ReportHttpRequest(operationName, url, null);

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                request.uploadHandler = new UploadHandlerRaw(Array.Empty<byte>());
                request.timeout = Mathf.Max(1, requestTimeoutSeconds);
                yield return request.SendWebRequest();

                bool success = request.result == UnityWebRequest.Result.Success;
                string detail = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                ReportHttpResult(operationName, request.responseCode, request.error, detail, success);

                if (!success && verboseLogging)
                    Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] {FormatHttpResultStatus(operationName, request.responseCode, request.error, detail, false)}");
            }
        }

        private IEnumerator PostJson(string url, string json, string operationName)
        {
            ReportHttpRequest(operationName, url, json);

            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                byte[] body = System.Text.Encoding.UTF8.GetBytes(json);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.uploadHandler = new UploadHandlerRaw(body);
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = Mathf.Max(1, requestTimeoutSeconds);
                yield return request.SendWebRequest();

                bool success = request.result == UnityWebRequest.Result.Success;
                string detail = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                ReportHttpResult(operationName, request.responseCode, request.error, detail, success);

                if (!success)
                {
                    Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] {FormatHttpResultStatus(operationName, request.responseCode, request.error, detail, false)}");
                }
                else if (verboseLogging)
                {
                    Debug.Log($"[ReachyDaemonTargetWebSocketClient] {FormatHttpResultStatus(operationName, request.responseCode, request.error, detail, true)} sent to {url}");
                }
            }
        }

        private void ReportHttpRequest(string operationName, string url, string body)
        {
            HttpRequestStarted?.Invoke(new DaemonHttpRequest(operationName, url, body));
        }

        private void ReportHttpResult(
            string operationName,
            long responseCode,
            string error,
            string detail,
            bool success)
        {
            HttpRequestCompleted?.Invoke(new DaemonHttpResult(
                operationName,
                responseCode,
                error,
                detail,
                success));
        }

        [Serializable]
        private sealed class PlaySoundRequest
        {
            public string file;

            public PlaySoundRequest(string file)
            {
                this.file = file;
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

        public readonly struct DaemonHttpRequest
        {
            public readonly string OperationName;
            public readonly string Url;
            public readonly string Body;

            public DaemonHttpRequest(string operationName, string url, string body)
            {
                OperationName = operationName;
                Url = url;
                Body = body;
            }

            public string StatusMessage => FormatHttpRequestStatus(OperationName, Url);
        }

        public readonly struct DaemonHttpResult
        {
            public readonly string OperationName;
            public readonly long ResponseCode;
            public readonly string Error;
            public readonly string Detail;
            public readonly bool Success;

            public DaemonHttpResult(
                string operationName,
                long responseCode,
                string error,
                string detail,
                bool success)
            {
                OperationName = operationName;
                ResponseCode = responseCode;
                Error = error;
                Detail = detail;
                Success = success;
            }

            public string StatusMessage =>
                FormatHttpResultStatus(OperationName, ResponseCode, Error, Detail, Success);
        }
    }
}
