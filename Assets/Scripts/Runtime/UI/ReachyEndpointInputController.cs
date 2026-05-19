using System;
using ReachyMiniTeleop.Reachy;
using ReachyMiniTeleop.Transport;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReachyMiniTeleop.UI
{
    public sealed class ReachyEndpointInputController : MonoBehaviour
    {
        public const int DefaultApiPort = 8000;
        public const string DefaultTargetPath = ReachyDaemonTargetWebSocketClient.DefaultTargetPath;
        public const string PlayerPrefsKey = "ReachyMiniTeleop.LastRobotIp";
        public const string PosePortPlayerPrefsKey = "ReachyMiniTeleop.LastPosePort";
        public const string WebRtcPortPlayerPrefsKey = "ReachyMiniTeleop.LastWebRtcPort";

        [Header("References")]
        public TMP_InputField robotIpInput;
        public TMP_InputField robotPosePortInput;
        public TMP_InputField webRtcPortInput;
        public TextMeshProUGUI statusLabel;
        public Toggle connectPoseToggle;
        public ReachyDaemonTargetWebSocketClient daemonClient;
        [System.Obsolete("Use daemonClient. Kept for older scenes that still serialize the ZMQ field.")]
        public ReachyZmqDealerClient zmqDealerClient;
        public ReachyHeadCommandPublisher publisher;
        public ReachyTeleopConfig config;

        [Header("Daemon Endpoint")]
        public int apiPort = DefaultApiPort;
        public string fallbackHost = "localhost";
        public string placeholderHost = "192.168.1.20";
        public bool saveSuccessfulHost = true;

        [Header("Quest Keyboard")]
        public bool showSceneKeypadOnSelect = true;
        public bool openTouchScreenKeyboardOnSelect = false;
        public string keyboardPlaceholder = "192.168.1.20";
        public GameObject keypadRoot;

        private bool _suppressToggleEvents;
        private bool _suppressInputEvents;
        private TouchScreenKeyboard _touchScreenKeyboard;
        private TMP_InputField _activeKeyboardInput;
        private EventTrigger.Entry _robotIpKeyboardClickEntry;
        private EventTrigger.Entry _robotPosePortKeyboardClickEntry;
        private EventTrigger.Entry _webRtcPortKeyboardClickEntry;

        private void Awake()
        {
            if (daemonClient == null)
                daemonClient = FindFirstObjectByType<ReachyDaemonTargetWebSocketClient>();

            if (publisher == null)
                publisher = FindFirstObjectByType<ReachyHeadCommandPublisher>();
        }

        private void OnEnable()
        {
            InitializeInputText();
            InitializePortInputs();
            EnsureSceneKeypad();

            if (connectPoseToggle != null)
                connectPoseToggle.onValueChanged.AddListener(OnConnectPoseToggleChanged);

            if (robotIpInput != null)
            {
                robotIpInput.onSelect.AddListener(OnRobotIpInputSelected);
                robotIpInput.onEndEdit.AddListener(OnRobotIpInputEndEdit);
                InstallKeyboardPointerTrigger(robotIpInput, ref _robotIpKeyboardClickEntry);
            }

            if (robotPosePortInput != null)
            {
                robotPosePortInput.onSelect.AddListener(OnRobotPosePortInputSelected);
                robotPosePortInput.onEndEdit.AddListener(OnRobotPosePortInputEndEdit);
                InstallKeyboardPointerTrigger(robotPosePortInput, ref _robotPosePortKeyboardClickEntry);
            }

            if (webRtcPortInput != null)
            {
                webRtcPortInput.onSelect.AddListener(OnWebRtcPortInputSelected);
                webRtcPortInput.onEndEdit.AddListener(OnWebRtcPortInputEndEdit);
                InstallKeyboardPointerTrigger(webRtcPortInput, ref _webRtcPortKeyboardClickEntry);
            }

            SetStatus("Disconnected");
        }

        private void Update()
        {
            SyncTouchScreenKeyboard();
        }

        private void OnDisable()
        {
            if (connectPoseToggle != null)
                connectPoseToggle.onValueChanged.RemoveListener(OnConnectPoseToggleChanged);

            if (robotIpInput != null)
            {
                robotIpInput.onSelect.RemoveListener(OnRobotIpInputSelected);
                robotIpInput.onEndEdit.RemoveListener(OnRobotIpInputEndEdit);
                RemoveKeyboardPointerTrigger(robotIpInput, ref _robotIpKeyboardClickEntry);
            }

            if (robotPosePortInput != null)
            {
                robotPosePortInput.onSelect.RemoveListener(OnRobotPosePortInputSelected);
                robotPosePortInput.onEndEdit.RemoveListener(OnRobotPosePortInputEndEdit);
                RemoveKeyboardPointerTrigger(robotPosePortInput, ref _robotPosePortKeyboardClickEntry);
            }

            if (webRtcPortInput != null)
            {
                webRtcPortInput.onSelect.RemoveListener(OnWebRtcPortInputSelected);
                webRtcPortInput.onEndEdit.RemoveListener(OnWebRtcPortInputEndEdit);
                RemoveKeyboardPointerTrigger(webRtcPortInput, ref _webRtcPortKeyboardClickEntry);
            }

            _touchScreenKeyboard = null;
            _activeKeyboardInput = null;
        }

        public void ConnectFromCurrentInput()
        {
            TryConnectFromCurrentInput();
        }

        public bool TryConnectFromCurrentInput()
        {
            string rawHost = robotIpInput != null ? robotIpInput.text : string.Empty;
            if (!TryGetPortFromInput(robotPosePortInput, apiPort, out int posePort))
            {
                SetStatus("Enter a valid pose port");
                SetConnectToggleWithoutNotify(false);
                return false;
            }

            if (!TryBuildDaemonTargetWebSocketUrlFromHost(rawHost, posePort, out string endpoint, out string host) ||
                !TryBuildDaemonApiBaseUrlFromHost(host, posePort, out string apiBaseUrl))
            {
                SetStatus("Enter a valid robot IP");
                SetConnectToggleWithoutNotify(false);
                return false;
            }

            if (daemonClient == null)
            {
                SetStatus("Daemon WS client missing");
                SetConnectToggleWithoutNotify(false);
                return false;
            }

            publisher?.StopPublishing();

            if (!daemonClient.TrySetEndpoint(endpoint, true) ||
                !daemonClient.TrySetApiBaseUrl(apiBaseUrl))
            {
                SetStatus("Invalid endpoint");
                SetConnectToggleWithoutNotify(false);
                return false;
            }

            daemonClient.StartClient();
            publisher?.StartPublishing();

            if (saveSuccessfulHost)
            {
                PlayerPrefs.SetString(PlayerPrefsKey, host);
                PlayerPrefs.SetInt(PosePortPlayerPrefsKey, posePort);
                PlayerPrefs.Save();
            }

            if (robotIpInput != null)
                robotIpInput.SetTextWithoutNotify(host);

            if (robotPosePortInput != null)
                robotPosePortInput.SetTextWithoutNotify(posePort.ToString());

            HideSceneKeypad();
            SetStatus($"Connecting to {host}:{posePort}");
            return true;
        }

        public void Disconnect()
        {
            publisher?.StopPublishing();
            daemonClient?.StopClient();
            SetStatus("Disconnected");
        }

        public static bool TryBuildDaemonTargetWebSocketUrlFromHost(string hostInput, int port, out string targetUrl)
        {
            return TryBuildDaemonTargetWebSocketUrlFromHost(hostInput, port, out targetUrl, out _);
        }

        public static bool TryBuildDaemonTargetWebSocketUrlFromHost(string hostInput, int port, out string targetUrl, out string host)
        {
            targetUrl = null;
            host = null;

            if (!TryNormalizeHostInput(hostInput, out host) || port <= 0 || port > 65535)
                return false;

            string candidate = $"ws://{host}:{port}{DefaultTargetPath}";
            return ReachyDaemonTargetWebSocketClient.IsValidTargetWebSocketUrl(candidate, out targetUrl);
        }

        public static bool TryBuildDaemonApiBaseUrlFromHost(string hostInput, int port, out string apiBaseUrl)
        {
            apiBaseUrl = null;

            if (!TryNormalizeHostInput(hostInput, out string host) || port <= 0 || port > 65535)
                return false;

            string candidate = $"http://{host}:{port}{ReachyDaemonTargetWebSocketClient.DefaultApiPath}";
            return ReachyDaemonTargetWebSocketClient.IsValidApiBaseUrl(candidate, out apiBaseUrl);
        }

        public static bool TryBuildTcpEndpointFromHost(string hostInput, int port, out string endpoint)
        {
            return TryBuildTcpEndpointFromHost(hostInput, port, out endpoint, out _);
        }

        public static bool TryBuildTcpEndpointFromHost(string hostInput, int port, out string endpoint, out string host)
        {
            endpoint = null;
            host = null;

            if (port <= 0 || port > 65535 || !TryNormalizeHostInput(hostInput, out host))
                return false;

            string candidate = $"tcp://{host}:{port}";
            if (!ReachyZmqDealerClient.IsValidTcpEndpoint(candidate))
                return false;

            endpoint = candidate;
            return true;
        }

        public static bool TryParsePortInput(string portInput, int fallbackPort, out int port)
        {
            port = 0;

            if (fallbackPort <= 0 || fallbackPort > 65535)
                return false;

            if (string.IsNullOrWhiteSpace(portInput))
            {
                port = fallbackPort;
                return true;
            }

            string trimmed = portInput.Trim();
            if (!int.TryParse(trimmed, out port))
                return false;

            return port > 0 && port <= 65535;
        }

        public static bool TryGetSavedPort(string playerPrefsKey, int fallbackPort, out int port)
        {
            port = fallbackPort;

            if (PlayerPrefs.HasKey(playerPrefsKey))
                return TryParsePortInput(PlayerPrefs.GetInt(playerPrefsKey).ToString(), fallbackPort, out port);

            return TryParsePortInput(string.Empty, fallbackPort, out port);
        }

        public static bool TryExtractHost(string endpoint, out string host)
        {
            host = null;

            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
                return false;

            host = uri.Host;
            return !string.IsNullOrWhiteSpace(host);
        }

        private void OnConnectPoseToggleChanged(bool isOn)
        {
            if (_suppressToggleEvents)
                return;

            if (isOn)
                TryConnectFromCurrentInput();
            else
                Disconnect();
        }

        private void OnRobotIpInputSelected(string _)
        {
            OpenKeyboardInput(robotIpInput);
        }

        private void OnRobotIpInputEndEdit(string value)
        {
            if (_suppressInputEvents)
                return;

            if (robotIpInput != null)
                robotIpInput.SetTextWithoutNotify(value.Trim());
        }

        private void OnRobotPosePortInputSelected(string _)
        {
            OpenKeyboardInput(robotPosePortInput);
        }

        private void OnRobotPosePortInputEndEdit(string value)
        {
            NormalizePortInput(robotPosePortInput, value, apiPort);
        }

        private void OnWebRtcPortInputSelected(string _)
        {
            OpenKeyboardInput(webRtcPortInput);
        }

        private void OnWebRtcPortInputEndEdit(string value)
        {
            NormalizePortInput(webRtcPortInput, value, ReachyVideoInputController.DefaultSignalingPort);
        }

        private void InitializeInputText()
        {
            if (robotIpInput == null)
                return;

            string host = null;
            if (saveSuccessfulHost && PlayerPrefs.HasKey(PlayerPrefsKey))
                host = PlayerPrefs.GetString(PlayerPrefsKey);

            if (string.IsNullOrWhiteSpace(host))
                TryExtractHost(daemonClient != null ? daemonClient.targetWebSocketUrl : null, out host);

            if (string.IsNullOrWhiteSpace(host))
                TryExtractHost(config != null ? config.endpoint : null, out host);

            if (string.IsNullOrWhiteSpace(host))
                host = fallbackHost;

            robotIpInput.SetTextWithoutNotify(host);

            if (robotIpInput.placeholder is TMP_Text placeholder)
                placeholder.text = placeholderHost;
        }

        private void InitializePortInputs()
        {
            InitializePortInput(robotPosePortInput, PosePortPlayerPrefsKey, apiPort);
            InitializePortInput(webRtcPortInput, WebRtcPortPlayerPrefsKey, ReachyVideoInputController.DefaultSignalingPort);
        }

        private void InitializePortInput(TMP_InputField input, string playerPrefsKey, int fallbackPort)
        {
            if (input == null)
                return;

            if (!TryGetSavedPort(playerPrefsKey, fallbackPort, out int port))
                port = fallbackPort;

            input.contentType = TMP_InputField.ContentType.IntegerNumber;
            input.characterLimit = 5;
            input.SetTextWithoutNotify(port.ToString());

            if (input.placeholder is TMP_Text placeholder)
                placeholder.text = fallbackPort.ToString();
        }

        public void ShowSceneKeypad()
        {
            if (keypadRoot != null)
                keypadRoot.SetActive(true);
        }

        public void HideSceneKeypad()
        {
            if (keypadRoot != null)
                keypadRoot.SetActive(false);
        }

        public void AppendKeypadText(string value)
        {
            TMP_InputField input = GetActiveKeyboardInput();
            if (input == null || string.IsNullOrEmpty(value))
                return;

            if (IsPortInput(input) && !char.IsDigit(value[0]))
                return;

            input.SetTextWithoutNotify((input.text + value).Trim());
            input.caretPosition = input.text.Length;
        }

        public void BackspaceKeypadText()
        {
            TMP_InputField input = GetActiveKeyboardInput();
            if (input == null || string.IsNullOrEmpty(input.text))
                return;

            input.SetTextWithoutNotify(input.text.Substring(0, input.text.Length - 1));
            input.caretPosition = input.text.Length;
        }

        public void ClearKeypadText()
        {
            TMP_InputField input = GetActiveKeyboardInput();
            if (input == null)
                return;

            input.SetTextWithoutNotify(string.Empty);
        }

        private void OpenKeyboardInput(TMP_InputField input)
        {
            if (input == null)
                return;

            _activeKeyboardInput = input;

            if (showSceneKeypadOnSelect)
                ShowSceneKeypad();

            OpenTouchScreenKeyboard(input);
        }

        private void OpenTouchScreenKeyboard(TMP_InputField input)
        {
            if (!openTouchScreenKeyboardOnSelect || input == null)
                return;

            if (Application.platform != RuntimePlatform.Android && !Application.isMobilePlatform)
                return;

            _touchScreenKeyboard = TouchScreenKeyboard.Open(
                input.text,
                IsPortInput(input) ? TouchScreenKeyboardType.NumberPad : TouchScreenKeyboardType.NumbersAndPunctuation,
                false,
                false,
                false,
                false,
                IsPortInput(input) ? GetDefaultPortForInput(input).ToString() : keyboardPlaceholder,
                Mathf.Max(input.characterLimit, 0));

            if (_touchScreenKeyboard != null)
                _touchScreenKeyboard.active = true;
        }

        private void SyncTouchScreenKeyboard()
        {
            TMP_InputField input = GetActiveKeyboardInput();
            if (_touchScreenKeyboard == null || input == null)
                return;

            if (input.text != _touchScreenKeyboard.text)
            {
                _suppressInputEvents = true;
                input.SetTextWithoutNotify(_touchScreenKeyboard.text);
                input.caretPosition = input.text.Length;
                _suppressInputEvents = false;
            }

            if (_touchScreenKeyboard.status == TouchScreenKeyboard.Status.Done ||
                _touchScreenKeyboard.status == TouchScreenKeyboard.Status.Canceled ||
                _touchScreenKeyboard.status == TouchScreenKeyboard.Status.LostFocus)
            {
                input.SetTextWithoutNotify(input.text.Trim());
                _touchScreenKeyboard = null;
            }
        }

        private void InstallKeyboardPointerTrigger(TMP_InputField input, ref EventTrigger.Entry entry)
        {
            if (input == null)
                return;

            var trigger = input.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = input.gameObject.AddComponent<EventTrigger>();

            if (entry != null)
                return;

            TMP_InputField capturedInput = input;
            entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener(_ => OpenKeyboardInput(capturedInput));
            trigger.triggers.Add(entry);
        }

        private void RemoveKeyboardPointerTrigger(TMP_InputField input, ref EventTrigger.Entry entry)
        {
            if (input == null || entry == null)
                return;

            var trigger = input.GetComponent<EventTrigger>();
            if (trigger != null)
                trigger.triggers.Remove(entry);

            entry = null;
        }

        private void SetConnectToggleWithoutNotify(bool value)
        {
            if (connectPoseToggle == null)
                return;

            _suppressToggleEvents = true;
            connectPoseToggle.SetIsOnWithoutNotify(value);
            _suppressToggleEvents = false;
        }

        private void SetStatus(string message)
        {
            if (statusLabel != null)
                statusLabel.text = message;
        }

        private bool TryGetPortFromInput(TMP_InputField input, int fallbackPort, out int port)
        {
            string rawPort = input != null ? input.text : string.Empty;
            return TryParsePortInput(rawPort, fallbackPort, out port);
        }

        private void NormalizePortInput(TMP_InputField input, string value, int fallbackPort)
        {
            if (_suppressInputEvents || input == null)
                return;

            string trimmed = value.Trim();
            if (TryParsePortInput(trimmed, fallbackPort, out int port))
                input.SetTextWithoutNotify(port.ToString());
            else
                input.SetTextWithoutNotify(trimmed);
        }

        private TMP_InputField GetActiveKeyboardInput()
        {
            if (_activeKeyboardInput != null)
                return _activeKeyboardInput;

            return robotIpInput;
        }

        private bool IsPortInput(TMP_InputField input)
        {
            return input != null && (input == robotPosePortInput || input == webRtcPortInput);
        }

        private int GetDefaultPortForInput(TMP_InputField input)
        {
            if (input == webRtcPortInput)
                return ReachyVideoInputController.DefaultSignalingPort;

            return apiPort;
        }

        public static bool TryNormalizeHostInput(string hostInput, out string host)
        {
            host = null;

            if (string.IsNullOrWhiteSpace(hostInput))
                return false;

            string trimmedHost = hostInput.Trim();
            if (trimmedHost.Contains("://") ||
                trimmedHost.Contains(":") ||
                trimmedHost.Contains("/") ||
                trimmedHost.Contains("\\") ||
                Uri.CheckHostName(trimmedHost) == UriHostNameType.Unknown)
            {
                return false;
            }

            host = trimmedHost;
            return true;
        }

        private void EnsureSceneKeypad()
        {
            if (keypadRoot != null || robotIpInput == null)
                return;

            var parent = transform;
            keypadRoot = new GameObject("RobotIpSceneKeypad", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(GridLayoutGroup));
            keypadRoot.transform.SetParent(parent, false);

            var rect = keypadRoot.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(24f, -228f);
            rect.sizeDelta = new Vector2(544f, 172f);

            var image = keypadRoot.GetComponent<Image>();
            image.color = new Color(0.08f, 0.08f, 0.08f, 0.92f);

            var grid = keypadRoot.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(128f, 36f);
            grid.spacing = new Vector2(8f, 8f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 4;
            grid.padding = new RectOffset(8, 8, 8, 8);

            string[] keys =
            {
                "1", "2", "3", "Back",
                "4", "5", "6", "Clear",
                "7", "8", "9", "OK",
                ".", "0"
            };

            foreach (string key in keys)
                CreateKeypadButton(keypadRoot.transform, key);

            keypadRoot.SetActive(false);
        }

        private void CreateKeypadButton(Transform parent, string key)
        {
            var buttonObject = new GameObject($"Key_{key}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(parent, false);

            var image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.28f, 0.28f, 0.28f, 1f);

            var button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(() => HandleKeypadButton(key));

            var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            labelObject.transform.SetParent(buttonObject.transform, false);
            var labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;

            var label = labelObject.GetComponent<TextMeshProUGUI>();
            label.text = key;
            label.fontSize = 16f;
            label.color = new Color(1f, 1f, 1f, 0.92f);
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
        }

        private void HandleKeypadButton(string key)
        {
            switch (key)
            {
                case "Back":
                    BackspaceKeypadText();
                    break;
                case "Clear":
                    ClearKeypadText();
                    break;
                case "OK":
                    HideSceneKeypad();
                    break;
                default:
                    AppendKeypadText(key);
                    break;
            }
        }
    }
}
