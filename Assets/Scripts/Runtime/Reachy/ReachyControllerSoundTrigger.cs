using System;
using ReachyMiniTeleop.UI;
using ReachyMiniTeleop.Transport;
using TMPro;
using UnityEngine;
using UnityEngine.XR;

namespace ReachyMiniTeleop.Reachy
{
    public sealed class ReachyControllerSoundTrigger : MonoBehaviour
    {
        private const string OperationName = "Controller sound";

        [Header("References")]
        public ReachyDaemonTargetWebSocketClient daemonClient;
        public TextMeshProUGUI statusLabel;
        public bool autoFindReferences = true;

        [Header("Triggering")]
        public bool triggerEnabled = true;
        [Min(0f)]
        public float globalCooldownSeconds = 1f;
        [Range(0f, 1f)]
        public float triggerThreshold = 0.7f;
        [Range(0f, 1f)]
        public float resetThreshold = 0.35f;
        public bool useOvrInputFallback = true;
        public ControllerSoundBinding[] bindings = ControllerSoundBinding.CreateDefaults();

        [Header("Debug Status")]
        public bool showDebugStatus = true;
        [Min(0.1f)]
        public float statusMessageSeconds = 2f;
        public bool showHttpResultStatus = true;
        [Min(0.1f)]
        public float httpResultStatusSeconds = 0.75f;
        [Min(0.05f)]
        public float statusPollIntervalSeconds = 0.25f;
        public bool waitForSoundRequestCompletion = true;

        private TriggerGate[] _gates;
        private ReachyDaemonTargetWebSocketClient _subscribedDaemonClient;
        private float _cooldownUntil;
        private float _statusHoldUntil;
        private float _nextPollStatusAt;

        public string LastStatusMessage { get; private set; }
        public ControllerSoundDirection LastTriggeredDirection { get; private set; }
        public string LastTriggeredSoundFile { get; private set; }

        private void Awake()
        {
            EnsureReferences();
            EnsureDaemonSubscription();
            EnsureGateCount();
        }

        private void OnEnable()
        {
            EnsureReferences();
            EnsureDaemonSubscription();
        }

        private void OnDisable()
        {
            ClearDaemonSubscription();
        }

        private void OnValidate()
        {
            triggerThreshold = Mathf.Clamp01(triggerThreshold);
            resetThreshold = Mathf.Clamp(resetThreshold, 0f, triggerThreshold);

            if (bindings == null || bindings.Length == 0)
                bindings = ControllerSoundBinding.CreateDefaults();

            EnsureGateCount();
        }

        private void Update()
        {
            if (!triggerEnabled)
            {
                ShowPollingStatus("Controller sound: disabled", Time.unscaledTime);
                return;
            }

            EnsureReferences();
            EnsureDaemonSubscription();
            EnsureGateCount();

            float now = Time.unscaledTime;
            if (daemonClient == null)
            {
                ShowPollingStatus("Controller sound: missing daemon client", now);
                return;
            }

            if (!daemonClient.soundEnabled)
            {
                ShowPollingStatus("Controller sound: transport disabled", now);
                return;
            }

            if (bindings == null || bindings.Length == 0)
            {
                ShowPollingStatus("Controller sound: no bindings", now);
                return;
            }

            if (waitForSoundRequestCompletion && daemonClient.IsSoundHttpRequestInFlight)
            {
                ShowPollingStatus(FormatRequestPendingStatus(), now);
                return;
            }

            bool sawInput = false;
            bool sawCooldown = false;
            sawCooldown |= EvaluateController(XRNode.LeftHand, now, ref sawInput);
            sawCooldown |= EvaluateController(XRNode.RightHand, now, ref sawInput);

            if (sawCooldown)
                ShowPollingStatus(FormatCooldownStatus(), now);
            else if (!sawInput)
                ShowPollingStatus(FormatNoThumbstickDataStatus(), now);
        }

        private bool EvaluateController(XRNode node, float now, ref bool sawInput)
        {
            if (!TryGetPrimary2DAxis(node, useOvrInputFallback, out Vector2 axis))
                return false;

            sawInput = true;
            float clampedTriggerThreshold = Mathf.Clamp01(triggerThreshold);
            float clampedResetThreshold = Mathf.Clamp(resetThreshold, 0f, clampedTriggerThreshold);
            ControllerSoundDirection activeDirection = ResolveDirection(node, axis, clampedTriggerThreshold);
            bool isReset = IsReset(axis, clampedResetThreshold);
            bool sawCooldown = false;

            for (int i = 0; i < bindings.Length; i++)
            {
                ControllerSoundBinding binding = bindings[i];
                if (binding == null || !IsDirectionForNode(binding.direction, node))
                    continue;

                bool isActive = activeDirection == binding.direction;
                bool triggered = _gates[i].Evaluate(isActive, isReset, now, _cooldownUntil);
                if (!triggered)
                {
                    if (isActive && now < _cooldownUntil && !_gates[i].IsArmed)
                        sawCooldown = true;

                    continue;
                }

                if (!IsSoundBindingEnabled(binding.soundFile))
                    continue;

                LastTriggeredDirection = binding.direction;
                LastTriggeredSoundFile = binding.soundFile;
                ShowStatus(FormatTriggerRequestStatus(binding.direction, binding.soundFile, daemonClient.apiBaseUrl), now, true);
                daemonClient.PlaySound(binding.soundFile, OperationName);
                _cooldownUntil = now + Mathf.Max(0f, globalCooldownSeconds);
                break;
            }

            return sawCooldown;
        }

        private void EnsureReferences()
        {
            if (!autoFindReferences)
                return;

            if (daemonClient == null)
                daemonClient = FindFirstObjectByType<ReachyDaemonTargetWebSocketClient>();

            if (statusLabel == null)
            {
                ReachyEndpointInputController endpointInput = FindFirstObjectByType<ReachyEndpointInputController>();
                if (endpointInput != null)
                    statusLabel = endpointInput.statusLabel;
            }
        }

        private void EnsureDaemonSubscription()
        {
            if (_subscribedDaemonClient == daemonClient)
                return;

            ClearDaemonSubscription();

            if (daemonClient == null)
                return;

            _subscribedDaemonClient = daemonClient;
            _subscribedDaemonClient.HttpRequestCompleted += OnDaemonHttpRequestCompleted;
        }

        private void ClearDaemonSubscription()
        {
            if (_subscribedDaemonClient == null)
                return;

            _subscribedDaemonClient.HttpRequestCompleted -= OnDaemonHttpRequestCompleted;
            _subscribedDaemonClient = null;
        }

        private void OnDaemonHttpRequestCompleted(ReachyDaemonTargetWebSocketClient.DaemonHttpResult result)
        {
            if (!string.Equals(result.OperationName, OperationName, StringComparison.Ordinal))
                return;

            if (showHttpResultStatus)
                ShowStatus(result.StatusMessage, Time.unscaledTime, true, httpResultStatusSeconds);
        }

        private void ShowPollingStatus(string message, float now)
        {
            if (now < _statusHoldUntil || now < _nextPollStatusAt)
                return;

            ShowStatus(message, now, false);
        }

        private void ShowStatus(string message, float now, bool hold)
        {
            ShowStatus(message, now, hold, statusMessageSeconds);
        }

        private void ShowStatus(string message, float now, bool hold, float holdSeconds)
        {
            LastStatusMessage = message;
            if (showDebugStatus &&
                statusLabel != null &&
                !string.Equals(statusLabel.text, message, StringComparison.Ordinal))
            {
                statusLabel.text = message;
            }

            if (hold)
                _statusHoldUntil = now + Mathf.Max(0.1f, holdSeconds);

            _nextPollStatusAt = now + Mathf.Max(0.05f, statusPollIntervalSeconds);
        }

        private void EnsureGateCount()
        {
            int count = bindings != null ? bindings.Length : 0;
            if (_gates != null && _gates.Length == count)
                return;

            _gates = new TriggerGate[count];
            for (int i = 0; i < _gates.Length; i++)
                _gates[i] = new TriggerGate();
        }

        private static bool TryGetPrimary2DAxis(XRNode node, bool useOvrFallback, out Vector2 axis)
        {
            axis = Vector2.zero;
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetFeatureValue(CommonUsages.primary2DAxis, out axis))
                return true;

            return useOvrFallback && TryGetOvrPrimaryThumbstick(node, out axis);
        }

        public static bool TryGetOvrPrimaryThumbstick(XRNode node, out Vector2 axis)
        {
            axis = Vector2.zero;
            if (node == XRNode.LeftHand)
            {
                axis = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
                return OVRInput.IsControllerConnected(OVRInput.Controller.LTouch) || axis.sqrMagnitude > 0.0001f;
            }

            if (node == XRNode.RightHand)
            {
                axis = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
                return OVRInput.IsControllerConnected(OVRInput.Controller.RTouch) || axis.sqrMagnitude > 0.0001f;
            }

            return false;
        }

        public static ControllerSoundDirection ResolveDirection(XRNode node, Vector2 axis, float threshold)
        {
            threshold = Mathf.Clamp01(threshold);
            float absX = Mathf.Abs(axis.x);
            float absY = Mathf.Abs(axis.y);
            if (Mathf.Max(absX, absY) < threshold)
                return ControllerSoundDirection.None;

            bool vertical = absY >= absX;
            if (node == XRNode.LeftHand)
                return vertical
                    ? axis.y >= 0f ? ControllerSoundDirection.LeftUp : ControllerSoundDirection.LeftDown
                    : axis.x >= 0f ? ControllerSoundDirection.LeftRight : ControllerSoundDirection.LeftLeft;

            if (node == XRNode.RightHand)
                return vertical
                    ? axis.y >= 0f ? ControllerSoundDirection.RightUp : ControllerSoundDirection.RightDown
                    : axis.x >= 0f ? ControllerSoundDirection.RightRight : ControllerSoundDirection.RightLeft;

            return ControllerSoundDirection.None;
        }

        public static bool IsReset(Vector2 axis, float resetThreshold)
        {
            resetThreshold = Mathf.Clamp01(resetThreshold);
            return Mathf.Abs(axis.x) <= resetThreshold && Mathf.Abs(axis.y) <= resetThreshold;
        }

        public static bool IsDirectionForNode(ControllerSoundDirection direction, XRNode node)
        {
            return node == XRNode.LeftHand
                ? direction >= ControllerSoundDirection.LeftUp && direction <= ControllerSoundDirection.LeftRight
                : node == XRNode.RightHand &&
                  direction >= ControllerSoundDirection.RightUp &&
                  direction <= ControllerSoundDirection.RightRight;
        }

        public static bool IsSoundBindingEnabled(string soundFile)
        {
            return !string.IsNullOrWhiteSpace(soundFile);
        }

        public static string FormatTriggerRequestStatus(
            ControllerSoundDirection direction,
            string soundFile,
            string apiBaseUrl)
        {
            string host = FormatApiHost(apiBaseUrl);
            return $"Controller sound request: {direction} -> {soundFile} @ {host}";
        }

        public static string FormatCooldownStatus()
        {
            return "Controller sound: cooldown";
        }

        public static string FormatRequestPendingStatus()
        {
            return "Controller sound: request pending";
        }

        public static string FormatNoThumbstickDataStatus()
        {
            return "Controller input: no thumbstick data";
        }

        private static string FormatApiHost(string apiBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
                return "unknown";

            if (!Uri.TryCreate(apiBaseUrl.Trim(), UriKind.Absolute, out var uri))
                return apiBaseUrl.Trim();

            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }

        [Serializable]
        public sealed class ControllerSoundBinding
        {
            public ControllerSoundDirection direction;
            public string soundFile;

            public ControllerSoundBinding(ControllerSoundDirection direction, string soundFile)
            {
                this.direction = direction;
                this.soundFile = soundFile;
            }

            public static ControllerSoundBinding[] CreateDefaults()
            {
                return new[]
                {
                    new ControllerSoundBinding(ControllerSoundDirection.LeftUp, "wake_up.wav"),
                    new ControllerSoundBinding(ControllerSoundDirection.LeftDown, "go_sleep.wav"),
                    new ControllerSoundBinding(ControllerSoundDirection.LeftLeft, "impatient1.wav"),
                    new ControllerSoundBinding(ControllerSoundDirection.LeftRight, "confused1.wav"),
                    new ControllerSoundBinding(ControllerSoundDirection.RightUp, "count.wav"),
                    new ControllerSoundBinding(ControllerSoundDirection.RightDown, "dance1.wav"),
                    new ControllerSoundBinding(ControllerSoundDirection.RightLeft, "wake_up.wav"),
                    new ControllerSoundBinding(ControllerSoundDirection.RightRight, "go_sleep.wav")
                };
            }
        }

        public sealed class TriggerGate
        {
            public bool IsArmed { get; private set; } = true;

            public bool Evaluate(bool isActive, bool isReset, float now, float cooldownUntil)
            {
                if (isReset)
                {
                    IsArmed = true;
                    return false;
                }

                if (!isActive || !IsArmed)
                    return false;

                IsArmed = false;
                return now >= cooldownUntil;
            }
        }
    }

    public enum ControllerSoundDirection
    {
        None = 0,
        LeftUp = 1,
        LeftDown = 2,
        LeftLeft = 3,
        LeftRight = 4,
        RightUp = 5,
        RightDown = 6,
        RightLeft = 7,
        RightRight = 8
    }
}
