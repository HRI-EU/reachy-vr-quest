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
        public TMP_InputField webRtcPortInput;
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
            InitializePortInput();

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
            if (!TryGetSignalingPort(out int port))
            {
                Debug.LogError("[ReachyVideoInput] Enter a valid WebRTC port before connecting video.");
                return false;
            }

            if (!TryBuildSignalingUrlFromHost(rawHost, port, out string signalingUrl))
            {
                Debug.LogError("[ReachyVideoInput] Enter a valid robot IP before connecting video.");
                return false;
            }

            if (webRtcClient.IsConnectedOrConnecting)
                return true;

            bool started = webRtcClient.Connect(signalingUrl);
            if (started)
            {
                if (ReachyEndpointInputController.TryNormalizeHostInput(rawHost, out string host))
                {
                    PlayerPrefs.SetString(ReachyEndpointInputController.PlayerPrefsKey, host);

                    if (robotIpInput != null)
                        robotIpInput.SetTextWithoutNotify(host);
                }

                PlayerPrefs.SetInt(ReachyEndpointInputController.WebRtcPortPlayerPrefsKey, port);
                PlayerPrefs.Save();

                if (webRtcPortInput != null)
                    webRtcPortInput.SetTextWithoutNotify(port.ToString());
            }

            return started;
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

        private void InitializePortInput()
        {
            if (webRtcPortInput == null)
                return;

            if (!ReachyEndpointInputController.TryGetSavedPort(
                    ReachyEndpointInputController.WebRtcPortPlayerPrefsKey,
                    signalingPort,
                    out int port))
            {
                port = signalingPort;
            }

            webRtcPortInput.contentType = TMP_InputField.ContentType.IntegerNumber;
            webRtcPortInput.characterLimit = 5;
            webRtcPortInput.SetTextWithoutNotify(port.ToString());

            if (webRtcPortInput.placeholder is TMP_Text placeholder)
                placeholder.text = signalingPort.ToString();
        }

        private bool TryGetSignalingPort(out int port)
        {
            string rawPort = webRtcPortInput != null ? webRtcPortInput.text : string.Empty;
            return ReachyEndpointInputController.TryParsePortInput(rawPort, signalingPort, out port);
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
