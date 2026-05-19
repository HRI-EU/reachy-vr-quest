using System;
using System.Globalization;
using ReachyMiniTeleop.UI;
using ReachyMiniTeleop.Transport;
using TMPro;
using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    public sealed class ReachyFaceSoundTrigger : MonoBehaviour
    {
        [Header("References")]
        public OVRFaceExpressions faceExpressions;
        public ReachyDaemonTargetWebSocketClient daemonClient;
        public TextMeshProUGUI statusLabel;
        public bool autoFindReferences = true;

        [Header("Triggering")]
        public bool triggerEnabled = true;
        [Min(0f)]
        public float globalCooldownSeconds = 2f;
        public FaceSoundBinding[] bindings = FaceSoundBinding.CreateDefaults();

        [Header("Debug Status")]
        public bool showDebugStatus = true;
        [Min(0.1f)]
        public float statusMessageSeconds = 2f;
        [Min(0.05f)]
        public float statusPollIntervalSeconds = 0.25f;

        private TriggerGate[] _gates;
        private ReachyDaemonTargetWebSocketClient _subscribedDaemonClient;
        private float _cooldownUntil;
        private float _statusHoldUntil;
        private float _nextPollStatusAt;

        public string LastStatusMessage { get; private set; }
        public OVRFaceExpressions.FaceExpression LastTriggeredExpression { get; private set; }
        public float LastTriggeredWeight { get; private set; }
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
            if (bindings == null || bindings.Length == 0)
                bindings = FaceSoundBinding.CreateDefaults();

            foreach (var binding in bindings)
                binding?.ClampThresholds();

            EnsureGateCount();
        }

        private void Update()
        {
            if (!triggerEnabled)
            {
                ShowPollingStatus("Face sound: disabled", Time.unscaledTime);
                return;
            }

            EnsureReferences();
            EnsureDaemonSubscription();
            EnsureGateCount();

            float now = Time.unscaledTime;
            if (daemonClient == null)
            {
                ShowPollingStatus("Face sound: missing daemon client", now);
                return;
            }

            if (faceExpressions == null)
            {
                ShowPollingStatus("Face tracking: missing OVRFaceExpressions", now);
                return;
            }

            if (bindings == null || bindings.Length == 0)
            {
                ShowPollingStatus("Face sound: no bindings", now);
                return;
            }

            if (!faceExpressions.ValidExpressions)
            {
                ShowPollingStatus(FormatNoValidExpressionsStatus(), now);
                return;
            }

            bool sawCooldown = false;
            FaceSoundBinding strongestBinding = null;
            float strongestWeight = 0f;

            for (int i = 0; i < bindings.Length; i++)
            {
                FaceSoundBinding binding = bindings[i];
                if (binding == null || string.IsNullOrWhiteSpace(binding.soundFile))
                    continue;

                float weight = faceExpressions.GetWeight(binding.expression);
                if (strongestBinding == null || weight > strongestWeight)
                {
                    strongestBinding = binding;
                    strongestWeight = weight;
                }

                if (!_gates[i].Evaluate(weight, true, binding.triggerThreshold, binding.resetThreshold, now, _cooldownUntil))
                {
                    if (now < _cooldownUntil && weight >= binding.triggerThreshold && !_gates[i].IsArmed)
                        sawCooldown = true;

                    continue;
                }

                LastTriggeredExpression = binding.expression;
                LastTriggeredWeight = weight;
                LastTriggeredSoundFile = binding.soundFile;
                ShowStatus(FormatTriggerRequestStatus(binding.expression, weight, binding.soundFile, daemonClient.apiBaseUrl), now, true);
                daemonClient.PlaySound(binding.soundFile);
                _cooldownUntil = now + Mathf.Max(0f, globalCooldownSeconds);
                break;
            }

            if (sawCooldown)
                ShowPollingStatus(FormatCooldownStatus(), now);
            else if (strongestBinding != null)
                ShowPollingStatus(FormatTrackingStatus(strongestBinding.expression, strongestWeight), now);
        }

        private void EnsureReferences()
        {
            if (!autoFindReferences)
                return;

            if (faceExpressions == null)
                faceExpressions = FindFirstObjectByType<OVRFaceExpressions>();

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
            _subscribedDaemonClient.HttpRequestStarted += OnDaemonHttpRequestStarted;
            _subscribedDaemonClient.HttpRequestCompleted += OnDaemonHttpRequestCompleted;
        }

        private void ClearDaemonSubscription()
        {
            if (_subscribedDaemonClient == null)
                return;

            _subscribedDaemonClient.HttpRequestStarted -= OnDaemonHttpRequestStarted;
            _subscribedDaemonClient.HttpRequestCompleted -= OnDaemonHttpRequestCompleted;
            _subscribedDaemonClient = null;
        }

        private void OnDaemonHttpRequestStarted(ReachyDaemonTargetWebSocketClient.DaemonHttpRequest request)
        {
            if (!string.Equals(request.OperationName, "Face sound", StringComparison.Ordinal))
                return;

            ShowStatus(request.StatusMessage, Time.unscaledTime, true);
        }

        private void OnDaemonHttpRequestCompleted(ReachyDaemonTargetWebSocketClient.DaemonHttpResult result)
        {
            if (!string.Equals(result.OperationName, "Face sound", StringComparison.Ordinal))
                return;

            ShowStatus(result.StatusMessage, Time.unscaledTime, true);
        }

        private void ShowPollingStatus(string message, float now)
        {
            if (now < _statusHoldUntil || now < _nextPollStatusAt)
                return;

            ShowStatus(message, now, false);
        }

        private void ShowStatus(string message, float now, bool hold)
        {
            LastStatusMessage = message;
            if (showDebugStatus && statusLabel != null)
                statusLabel.text = message;

            if (hold)
                _statusHoldUntil = now + Mathf.Max(0.1f, statusMessageSeconds);

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

        [Serializable]
        public sealed class FaceSoundBinding
        {
            public OVRFaceExpressions.FaceExpression expression;
            public string soundFile;
            [Range(0f, 1f)]
            public float triggerThreshold = 0.75f;
            [Range(0f, 1f)]
            public float resetThreshold = 0.45f;

            public FaceSoundBinding(OVRFaceExpressions.FaceExpression expression, string soundFile)
            {
                this.expression = expression;
                this.soundFile = soundFile;
            }

            public void ClampThresholds()
            {
                triggerThreshold = Mathf.Clamp01(triggerThreshold);
                resetThreshold = Mathf.Clamp(resetThreshold, 0f, triggerThreshold);
            }

            public static FaceSoundBinding[] CreateDefaults()
            {
                return new[]
                {
                    new FaceSoundBinding(OVRFaceExpressions.FaceExpression.JawDrop, "count.wav"),
                    new FaceSoundBinding(OVRFaceExpressions.FaceExpression.LipCornerPullerL, "dance1.wav"),
                    new FaceSoundBinding(OVRFaceExpressions.FaceExpression.BrowLowererL, "confused1.wav"),
                    new FaceSoundBinding(OVRFaceExpressions.FaceExpression.TongueOut, "impatient1.wav")
                };
            }
        }

        public sealed class TriggerGate
        {
            public bool IsArmed { get; private set; } = true;

            public bool Evaluate(
                float weight,
                bool expressionsValid,
                float triggerThreshold,
                float resetThreshold,
                float now,
                float cooldownUntil)
            {
                if (!expressionsValid)
                    return false;

                triggerThreshold = Mathf.Clamp01(triggerThreshold);
                resetThreshold = Mathf.Clamp(resetThreshold, 0f, triggerThreshold);
                weight = Mathf.Clamp01(weight);

                if (weight <= resetThreshold)
                {
                    IsArmed = true;
                    return false;
                }

                if (!IsArmed || weight < triggerThreshold)
                    return false;

                IsArmed = false;
                return now >= cooldownUntil;
            }
        }

        public static string FormatTriggerStatus(
            OVRFaceExpressions.FaceExpression expression,
            float weight,
            string soundFile)
        {
            return $"Face sound: {expression} {FormatWeight(weight)} -> {soundFile}";
        }

        public static string FormatTriggerRequestStatus(
            OVRFaceExpressions.FaceExpression expression,
            float weight,
            string soundFile,
            string apiBaseUrl)
        {
            string host = FormatApiHost(apiBaseUrl);
            return $"Face sound request: {expression} {FormatWeight(weight)} -> {soundFile} @ {host}";
        }

        public static string FormatTrackingStatus(OVRFaceExpressions.FaceExpression expression, float weight)
        {
            return $"Face tracking: {expression} {FormatWeight(weight)}";
        }

        public static string FormatNoValidExpressionsStatus()
        {
            return "Face tracking: no valid expressions";
        }

        public static string FormatCooldownStatus()
        {
            return "Face sound: cooldown";
        }

        private static string FormatWeight(float weight)
        {
            return Mathf.Clamp01(weight).ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string FormatApiHost(string apiBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
                return "unknown";

            if (!Uri.TryCreate(apiBaseUrl.Trim(), UriKind.Absolute, out var uri))
                return apiBaseUrl.Trim();

            return uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        }
    }
}
