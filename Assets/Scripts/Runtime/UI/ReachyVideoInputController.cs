using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ReachyMiniTeleop.UI
{
    public sealed class ReachyVideoInputController : MonoBehaviour
    {
        public const int DefaultSignalingPort = 8443;

        [Header("References")]
        public Toggle connectVideoToggle;
        public TMP_InputField robotIpInput;
        public WebRTCClient webRtcClient;

        [Header("Signaling")]
        public int signalingPort = DefaultSignalingPort;

        private bool _suppressToggleEvents;

        private void Awake()
        {
            if (webRtcClient == null)
                webRtcClient = FindFirstObjectByType<WebRTCClient>();
        }

        private void OnEnable()
        {
            if (webRtcClient != null)
                webRtcClient.ConnectionStateChanged += OnWebRtcConnectionStateChanged;

            if (connectVideoToggle != null)
                connectVideoToggle.onValueChanged.AddListener(OnConnectVideoToggleChanged);

            SetToggleWithoutNotify(webRtcClient != null && webRtcClient.IsConnectedOrConnecting);
        }

        private void OnDisable()
        {
            if (connectVideoToggle != null)
                connectVideoToggle.onValueChanged.RemoveListener(OnConnectVideoToggleChanged);

            if (webRtcClient != null)
                webRtcClient.ConnectionStateChanged -= OnWebRtcConnectionStateChanged;
        }

        public void ConnectFromCurrentInput()
        {
            if (!TryConnectFromCurrentInput())
                SetToggleWithoutNotify(false);
        }

        public bool TryConnectFromCurrentInput()
        {
            if (webRtcClient == null)
            {
                Debug.LogError("[ReachyVideoInput] WebRTC client missing.");
                return false;
            }

            string rawHost = robotIpInput != null ? robotIpInput.text : string.Empty;
            if (!TryBuildSignalingUrlFromHost(rawHost, signalingPort, out string signalingUrl))
            {
                Debug.LogError("[ReachyVideoInput] Enter a valid robot IP before connecting video.");
                return false;
            }

            if (webRtcClient.IsConnectedOrConnecting)
                return true;

            return webRtcClient.Connect(signalingUrl);
        }

        public void Disconnect()
        {
            webRtcClient?.Disconnect();
        }

        public static bool TryBuildSignalingUrlFromHost(string hostInput, int port, out string signalingUrl)
        {
            signalingUrl = null;

            if (!ReachyEndpointInputController.TryNormalizeHostInput(hostInput, out string host) ||
                port <= 0 ||
                port > 65535)
            {
                return false;
            }

            string candidate = $"ws://{host}:{port}";
            return WebRTCClient.IsValidSignalingUrl(candidate, out signalingUrl);
        }

        private void OnConnectVideoToggleChanged(bool isOn)
        {
            if (_suppressToggleEvents)
                return;

            if (isOn)
            {
                if (!TryConnectFromCurrentInput())
                    SetToggleWithoutNotify(false);
            }
            else
            {
                Disconnect();
            }
        }

        private void OnWebRtcConnectionStateChanged(bool isConnectedOrConnecting)
        {
            if (!isConnectedOrConnecting)
                SetToggleWithoutNotify(false);
        }

        private void SetToggleWithoutNotify(bool value)
        {
            if (connectVideoToggle == null)
                return;

            _suppressToggleEvents = true;
            connectVideoToggle.SetIsOnWithoutNotify(value);
            _suppressToggleEvents = false;
        }
    }
}
