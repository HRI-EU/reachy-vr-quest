using System.Collections;
using Newtonsoft.Json;
using ReachyMiniTeleop.Transport;
using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    public sealed class ReachyHeadCommandPublisher : MonoBehaviour
    {
        [Header("References")]
        public MonoBehaviour skeletonProviderBehaviour;
        public MonoBehaviour messageSenderBehaviour;
        [System.Obsolete("Use messageSenderBehaviour. Kept for existing scenes that still reference the ZMQ client.")]
        public ReachyZmqDealerClient zmqDealerClient;
        public ReachyTeleopConfig config;

        [Header("Runtime")]
        public bool publishOnEnable = true;
        public bool verbose = false;

        private readonly ReachyHeadCommandBuilder _builder = new ReachyHeadCommandBuilder();
        private Coroutine _publishRoutine;
        private ReachyTeleopConfig _runtimeConfig;
        private float _lastBuildFailureLogTime = float.NegativeInfinity;

        public event System.Action<string> OnPayloadJsonBuilt;

        private IReachySkeletonProvider SkeletonProvider => skeletonProviderBehaviour as IReachySkeletonProvider;
        private IReachyMessageSender MessageSender => messageSenderBehaviour as IReachyMessageSender ?? zmqDealerClient;
        private ReachyTeleopConfig Config => _runtimeConfig != null ? _runtimeConfig : config;

        private void Awake()
        {
            if (config == null)
            {
                _runtimeConfig = ScriptableObject.CreateInstance<ReachyTeleopConfig>();
            }

            if (messageSenderBehaviour == null)
                messageSenderBehaviour = FindFirstObjectByType<ReachyDaemonTargetWebSocketClient>();

            if (messageSenderBehaviour == null && zmqDealerClient == null)
                zmqDealerClient = FindFirstObjectByType<ReachyZmqDealerClient>();

            if (!IsUsableSkeletonProvider(skeletonProviderBehaviour))
            {
                MonoBehaviour activeProvider = FindActiveSkeletonProvider();
                if (activeProvider != null)
                {
                    if (skeletonProviderBehaviour != null && verbose)
                        Debug.LogWarning(
                            $"[ReachyHeadCommandPublisher] Ignoring inactive skeleton provider " +
                            $"{skeletonProviderBehaviour.name}; using active provider {activeProvider.name}.");

                    skeletonProviderBehaviour = activeProvider;
                }
            }
        }

        private void OnEnable()
        {
            if (publishOnEnable && Application.isPlaying)
                StartPublishing();
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
                StopPublishing();
        }

        private void OnDestroy()
        {
            if (_runtimeConfig != null)
                Destroy(_runtimeConfig);
        }

        public void StartPublishing()
        {
            if (_publishRoutine != null)
                return;

            _publishRoutine = StartCoroutine(PublishRoutine());
        }

        public void StopPublishing()
        {
            if (_publishRoutine == null)
                return;

            StopCoroutine(_publishRoutine);
            _publishRoutine = null;
        }

        public bool TryBuildPayload(out ReachyHeadPayload payload, out ReachyAntennaDebugInfo antennaDebugInfo)
        {
            payload = null;
            antennaDebugInfo = default;

            var provider = SkeletonProvider;
            var activeConfig = Config;
            bool success = provider != null &&
                           activeConfig != null &&
                           _builder.TryBuildPayload(provider, activeConfig, out payload, out antennaDebugInfo);

            if (!success && verbose)
                LogBuildFailure(provider, activeConfig);

            return success;
        }

        public bool PublishOnce()
        {
            if (!TryBuildPayload(out var payload, out var antennaDebugInfo))
                return false;

            var sender = MessageSender;
            string json = sender is ReachyDaemonTargetWebSocketClient
                ? ReachyDaemonTargetAdapter.ToJson(payload)
                : JsonConvert.SerializeObject(payload);
            OnPayloadJsonBuilt?.Invoke(json);

            if (verbose)
            {
                Debug.Log(
                    $"[ReachyHeadCommandPublisher] body_yaw_degrees={payload.bodyYawDegrees:F2}, " +
                    $"left_antenna={(antennaDebugInfo.leftComputed ? payload.antennas.left.ToString("F3") : "centered")}, " +
                    $"right_antenna={(antennaDebugInfo.rightComputed ? payload.antennas.right.ToString("F3") : "centered")}, " +
                    $"payload={json}");
            }

            sender?.SendMessageToServer(json);
            return true;
        }

        private IEnumerator PublishRoutine()
        {
            while (SkeletonProvider == null || !SkeletonProvider.SkeletonReady)
                yield return new WaitForSeconds(0.5f);

            while (true)
            {
                float delay = 1f / Mathf.Max(Config.sendRateHz, 0.01f);
                yield return new WaitForSeconds(delay);
                PublishOnce();
            }
        }

        private static bool IsUsableSkeletonProvider(MonoBehaviour behaviour)
        {
            return behaviour != null &&
                   behaviour is IReachySkeletonProvider &&
                   behaviour.isActiveAndEnabled;
        }

        private static MonoBehaviour FindActiveSkeletonProvider()
        {
            foreach (var candidate in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (candidate is IReachySkeletonProvider && candidate.isActiveAndEnabled)
                    return candidate;
            }

            return null;
        }

        private void LogBuildFailure(IReachySkeletonProvider provider, ReachyTeleopConfig activeConfig)
        {
            if (Time.unscaledTime - _lastBuildFailureLogTime < 2f)
                return;

            _lastBuildFailureLogTime = Time.unscaledTime;

            string providerName = skeletonProviderBehaviour != null ? skeletonProviderBehaviour.name : "none";
            bool providerActive = skeletonProviderBehaviour != null && skeletonProviderBehaviour.isActiveAndEnabled;
            bool providerReady = provider != null && provider.SkeletonReady;
            bool hasHead = provider != null && provider.TryGetTransform(ReachyHeadCommandBuilder.CenterCamAnchorKey, out _);
            bool hasHeadFront = provider != null &&
                                ReachyHeadCommandBuilder.TryGetHeadReferenceTransforms(provider, out _, out _);
            bool hasShoulders = provider != null &&
                                provider.TryGetTransform(ReachyHeadCommandBuilder.LeftShoulderKey, out _) &&
                                provider.TryGetTransform(ReachyHeadCommandBuilder.RightShoulderKey, out _);

            Debug.LogWarning(
                $"[ReachyHeadCommandPublisher] Payload not sent. " +
                $"provider={providerName}, active={providerActive}, ready={providerReady}, " +
                $"config={(activeConfig != null)}, head={hasHead}, headReference={hasHeadFront}, shoulders={hasShoulders}.");
        }
    }
}
