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
        public const int DefaultPort = 40000;
        public const string PlayerPrefsKey = "ReachyMiniTeleop.LastRobotIp";

        [Header("References")]
        public TMP_InputField robotIpInput;
        public TextMeshProUGUI statusLabel;
        public Toggle connectPoseToggle;
        public ReachyZmqDealerClient zmqDealerClient;
        public ReachyHeadCommandPublisher publisher;
        public ReachyTeleopConfig config;

        [Header("Endpoint")]
        public int port = DefaultPort;
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
        private EventTrigger.Entry _keyboardClickEntry;

        private void Awake()
        {
            if (zmqDealerClient == null)
                zmqDealerClient = FindFirstObjectByType<ReachyZmqDealerClient>();

            if (publisher == null)
                publisher = FindFirstObjectByType<ReachyHeadCommandPublisher>();
        }

        private void OnEnable()
        {
            InitializeInputText();
            EnsureSceneKeypad();

            if (connectPoseToggle != null)
                connectPoseToggle.onValueChanged.AddListener(OnConnectPoseToggleChanged);

            if (robotIpInput != null)
            {
                robotIpInput.onSelect.AddListener(OnRobotIpInputSelected);
                robotIpInput.onEndEdit.AddListener(OnRobotIpInputEndEdit);
                InstallKeyboardPointerTrigger();
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
                RemoveKeyboardPointerTrigger();
            }

            _touchScreenKeyboard = null;
        }

        public void ConnectFromCurrentInput()
        {
            TryConnectFromCurrentInput();
        }

        public bool TryConnectFromCurrentInput()
        {
            string rawHost = robotIpInput != null ? robotIpInput.text : string.Empty;
            if (!TryBuildTcpEndpointFromHost(rawHost, port, out string endpoint, out string host))
            {
                SetStatus("Enter a valid robot IP");
                SetConnectToggleWithoutNotify(false);
                return false;
            }

            if (zmqDealerClient == null)
            {
                SetStatus("ZMQ client missing");
                SetConnectToggleWithoutNotify(false);
                return false;
            }

            publisher?.StopPublishing();

            if (!zmqDealerClient.TrySetEndpoint(endpoint, true))
            {
                SetStatus("Invalid endpoint");
                SetConnectToggleWithoutNotify(false);
                return false;
            }

            zmqDealerClient.StartClient();
            publisher?.StartPublishing();

            if (saveSuccessfulHost)
            {
                PlayerPrefs.SetString(PlayerPrefsKey, host);
                PlayerPrefs.Save();
            }

            if (robotIpInput != null)
                robotIpInput.SetTextWithoutNotify(host);

            HideSceneKeypad();
            SetStatus($"Connecting to {host}:{port}");
            return true;
        }

        public void Disconnect()
        {
            publisher?.StopPublishing();
            zmqDealerClient?.StopClient();
            SetStatus("Disconnected");
        }

        public static bool TryBuildTcpEndpointFromHost(string hostInput, int port, out string endpoint)
        {
            return TryBuildTcpEndpointFromHost(hostInput, port, out endpoint, out _);
        }

        public static bool TryBuildTcpEndpointFromHost(string hostInput, int port, out string endpoint, out string host)
        {
            endpoint = null;
            host = null;

            if (port <= 0 || port > 65535 || string.IsNullOrWhiteSpace(hostInput))
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

            string candidate = $"tcp://{trimmedHost}:{port}";
            if (!ReachyZmqDealerClient.IsValidTcpEndpoint(candidate))
                return false;

            endpoint = candidate;
            host = trimmedHost;
            return true;
        }

        public static bool TryExtractHost(string endpoint, out string host)
        {
            host = null;

            if (!ReachyZmqDealerClient.IsValidTcpEndpoint(endpoint))
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
            OpenKeyboardInput();
        }

        private void OnRobotIpInputEndEdit(string value)
        {
            if (_suppressInputEvents)
                return;

            if (robotIpInput != null)
                robotIpInput.SetTextWithoutNotify(value.Trim());
        }

        private void InitializeInputText()
        {
            if (robotIpInput == null)
                return;

            string host = null;
            if (saveSuccessfulHost && PlayerPrefs.HasKey(PlayerPrefsKey))
                host = PlayerPrefs.GetString(PlayerPrefsKey);

            if (string.IsNullOrWhiteSpace(host))
                TryExtractHost(config != null ? config.endpoint : null, out host);

            if (string.IsNullOrWhiteSpace(host))
                TryExtractHost(zmqDealerClient != null ? zmqDealerClient.endpoint : null, out host);

            if (string.IsNullOrWhiteSpace(host))
                host = fallbackHost;

            robotIpInput.SetTextWithoutNotify(host);

            if (robotIpInput.placeholder is TMP_Text placeholder)
                placeholder.text = placeholderHost;
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
            if (robotIpInput == null || string.IsNullOrEmpty(value))
                return;

            robotIpInput.SetTextWithoutNotify((robotIpInput.text + value).Trim());
            robotIpInput.caretPosition = robotIpInput.text.Length;
        }

        public void BackspaceKeypadText()
        {
            if (robotIpInput == null || string.IsNullOrEmpty(robotIpInput.text))
                return;

            robotIpInput.SetTextWithoutNotify(robotIpInput.text.Substring(0, robotIpInput.text.Length - 1));
            robotIpInput.caretPosition = robotIpInput.text.Length;
        }

        public void ClearKeypadText()
        {
            if (robotIpInput == null)
                return;

            robotIpInput.SetTextWithoutNotify(string.Empty);
        }

        private void OpenKeyboardInput()
        {
            if (showSceneKeypadOnSelect)
                ShowSceneKeypad();

            OpenTouchScreenKeyboard();
        }

        private void OpenTouchScreenKeyboard()
        {
            if (!openTouchScreenKeyboardOnSelect || robotIpInput == null)
                return;

            if (Application.platform != RuntimePlatform.Android && !Application.isMobilePlatform)
                return;

            _touchScreenKeyboard = TouchScreenKeyboard.Open(
                robotIpInput.text,
                TouchScreenKeyboardType.NumbersAndPunctuation,
                false,
                false,
                false,
                false,
                keyboardPlaceholder,
                Mathf.Max(robotIpInput.characterLimit, 0));

            if (_touchScreenKeyboard != null)
                _touchScreenKeyboard.active = true;
        }

        private void SyncTouchScreenKeyboard()
        {
            if (_touchScreenKeyboard == null || robotIpInput == null)
                return;

            if (robotIpInput.text != _touchScreenKeyboard.text)
            {
                _suppressInputEvents = true;
                robotIpInput.SetTextWithoutNotify(_touchScreenKeyboard.text);
                robotIpInput.caretPosition = robotIpInput.text.Length;
                _suppressInputEvents = false;
            }

            if (_touchScreenKeyboard.status == TouchScreenKeyboard.Status.Done ||
                _touchScreenKeyboard.status == TouchScreenKeyboard.Status.Canceled ||
                _touchScreenKeyboard.status == TouchScreenKeyboard.Status.LostFocus)
            {
                robotIpInput.SetTextWithoutNotify(robotIpInput.text.Trim());
                _touchScreenKeyboard = null;
            }
        }

        private void InstallKeyboardPointerTrigger()
        {
            if (robotIpInput == null)
                return;

            var trigger = robotIpInput.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = robotIpInput.gameObject.AddComponent<EventTrigger>();

            if (_keyboardClickEntry != null)
                return;

            _keyboardClickEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            _keyboardClickEntry.callback.AddListener(_ => OpenKeyboardInput());
            trigger.triggers.Add(_keyboardClickEntry);
        }

        private void RemoveKeyboardPointerTrigger()
        {
            if (robotIpInput == null || _keyboardClickEntry == null)
                return;

            var trigger = robotIpInput.GetComponent<EventTrigger>();
            if (trigger != null)
                trigger.triggers.Remove(_keyboardClickEntry);

            _keyboardClickEntry = null;
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
