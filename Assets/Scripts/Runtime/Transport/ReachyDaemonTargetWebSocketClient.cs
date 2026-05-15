using System;
using System.Collections;
using System.Collections.Concurrent;
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

        [Header("Reachy Daemon")]
        public string targetWebSocketUrl = "ws://localhost:8000/api/move/ws/set_target";
        public string apiBaseUrl = "http://localhost:8000/api";
        public bool autoStart = false;
        public bool autoWakeOnStart = true;
        public bool verboseLogging = false;

        [Header("Queue")]
        public int maxQueuedMessages = 5;
        public int maxSendsPerFrame = 1;

        [Header("HTTP")]
        public int requestTimeoutSeconds = 3;

        public event Action<string> OnReceiveData;
        public event Action<bool> ConnectionStateChanged;

        private readonly ConcurrentQueue<string> _outgoingQueue = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> _incomingQueue = new ConcurrentQueue<string>();
        private WebSocket _webSocket;
        private Coroutine _wakeCoroutine;
        private bool _isConnecting;

        public bool IsRunning => _webSocket != null && _webSocket.IsAlive;
        public bool IsConnectedOrConnecting => _isConnecting || IsRunning;
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

            if (!IsRunning)
                return;

            int sent = 0;
            while (sent < Mathf.Max(1, maxSendsPerFrame) && _outgoingQueue.TryDequeue(out var data))
            {
                try
                {
                    _webSocket.Send(data);
                    if (verboseLogging)
                        Debug.Log($"[ReachyDaemonTargetWebSocketClient] Sent: {data}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] Send error: {ex.Message}");
                    break;
                }

                sent++;
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
            _isConnecting = true;
            ConnectionStateChanged?.Invoke(true);

            _webSocket = new WebSocket(targetWebSocketUrl)
            {
                EmitOnPing = true
            };

            _webSocket.OnOpen += (_, _) =>
            {
                _isConnecting = false;
                if (verboseLogging)
                    Debug.Log($"[ReachyDaemonTargetWebSocketClient] Connected to {targetWebSocketUrl}");
            };
            _webSocket.OnMessage += (_, args) =>
            {
                string data = args.Data;
                if (!string.IsNullOrEmpty(data))
                    _incomingQueue.Enqueue(data);
            };
            _webSocket.OnError += (_, args) =>
            {
                _isConnecting = false;
                Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] WebSocket error: {args.Message}");
                ConnectionStateChanged?.Invoke(false);
            };
            _webSocket.OnClose += (_, args) =>
            {
                _isConnecting = false;
                if (verboseLogging)
                    Debug.Log($"[ReachyDaemonTargetWebSocketClient] Closed: {args.Reason}");
                ConnectionStateChanged?.Invoke(false);
            };

            _webSocket.ConnectAsync();

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

            _isConnecting = false;

            try
            {
                if (_webSocket != null && _webSocket.IsAlive)
                    _webSocket.CloseAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ReachyDaemonTargetWebSocketClient] Close error: {ex.Message}");
            }
            finally
            {
                _webSocket = null;
                ConnectionStateChanged?.Invoke(false);
            }
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
    }
}
