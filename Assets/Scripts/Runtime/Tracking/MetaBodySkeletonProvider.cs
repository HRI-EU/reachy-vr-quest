using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Meta.XR.Movement.Retargeting;
using ReachyMiniTeleop.Reachy;
using Unity.Collections;
using UnityEngine;
using static Meta.XR.Movement.MSDKUtility;

namespace ReachyMiniTeleop.Tracking
{
    public sealed class MetaBodySkeletonProvider : MonoBehaviour, IReachySkeletonProvider
    {
        public static MetaBodySkeletonProvider Instance { get; private set; }

        [Header("Head References")]
        public Transform centerCamAnchor;
        public Transform centerEyeFront;
        public Transform centerEyeUp;
        public Transform leftEyeAnchor;
        public Transform rightEyeAnchor;

        [Header("OpenXR Hand Stitching")]
        public OVRSkeleton leftOpenXRSkeleton;
        public OVRSkeleton rightOpenXRSkeleton;
        public OVRHand leftOVRHand;
        public OVRHand rightOVRHand;

        [Header("Stitch Overwrite")]
        public bool overwriteFingers = true;
        public bool overwritePalm = false;
        public bool overwriteWrist = false;

        [Header("Tracking Validation")]
        [SerializeField] private bool verboseLogging = true;
        [SerializeField] private float validBodyTrackingDelay = 0.5f;
        [SerializeField] private bool requireHighConfidence = false;
        [SerializeField] private float minDistanceThreshold = 0.001f;

        [Header("Debug Visualization")]
        [SerializeField] private bool enableBoneVisualization = false;
        [SerializeField] private float boneMarkerSize = 0.02f;
        [SerializeField] private Material debugMarkerMaterial;

        public bool SkeletonReady { get; private set; }
        public Dictionary<string, Transform> BodySkeletonTransformDict { get; private set; }

        private MetaSourceDataProvider _source;
        private Transform _runtimeJointsRoot;
        private readonly List<Transform> _jointTransforms = new List<Transform>();
        private readonly Dictionary<string, GameObject> _markers = new Dictionary<string, GameObject>();
        private Dictionary<string, OVRSkeleton.BoneId> _leftMap;
        private Dictionary<string, OVRSkeleton.BoneId> _rightMap;
        private Dictionary<OVRSkeleton.BoneId, Transform> _leftBoneTf;
        private Dictionary<OVRSkeleton.BoneId, Transform> _rightBoneTf;
        private GameObject _debugContainer;
        private float _validTimer;
        private bool _poseStable;

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
                return;
            }

            BodySkeletonTransformDict = new Dictionary<string, Transform>();
            BuildHandStitchMaps();
        }

        private void Start()
        {
            StartCoroutine(InitAndRun());
        }

        private void Update()
        {
            if (!SkeletonReady || _source == null)
                return;

            bool providerValid = _source.IsPoseValid();
            if (providerValid && !_poseStable)
            {
                _validTimer += Time.smoothDeltaTime;
                if (_validTimer >= validBodyTrackingDelay)
                    _poseStable = true;
            }
            else if (!providerValid)
            {
                _validTimer = 0f;
                _poseStable = false;
            }

            NativeArray<NativeTransform> pose = _source.GetSkeletonPose();
            if (!pose.IsCreated || pose.Length == 0)
            {
                if (pose.IsCreated)
                    pose.Dispose();
                return;
            }

            ApplyPoseToRuntimeTransforms(pose);
            pose.Dispose();

            ApplyHandStitch();

            if (enableBoneVisualization)
                UpdateMarkers();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;

            if (_debugContainer != null)
                Destroy(_debugContainer);
            if (_runtimeJointsRoot != null)
                Destroy(_runtimeJointsRoot.gameObject);
        }

        public bool TryGetTransform(string key, out Transform boneTransform)
        {
            boneTransform = null;
            return BodySkeletonTransformDict != null &&
                   BodySkeletonTransformDict.TryGetValue(key, out boneTransform) &&
                   boneTransform != null;
        }

        public bool IsLeftHandTracked()
        {
            return leftOVRHand == null || (leftOVRHand.IsTracked && leftOVRHand.IsDataValid);
        }

        public bool IsRightHandTracked()
        {
            return rightOVRHand == null || (rightOVRHand.IsTracked && rightOVRHand.IsDataValid);
        }

        public bool IsBodyTrackingActive()
        {
            return _source != null && _source.IsPoseValid() && _poseStable;
        }

        private IEnumerator InitAndRun()
        {
            SkeletonReady = false;
            _validTimer = 0f;
            _poseStable = false;

            float timeout = 8f;
            float elapsed = 0f;
            while (_source == null && elapsed < timeout)
            {
                _source = FindAnyComponent<MetaSourceDataProvider>();
                if (_source != null)
                    break;

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_source == null)
            {
                Debug.LogError("[MetaBodySkeletonProvider] MetaSourceDataProvider not found in scene.");
                SkeletonReady = true;
                yield break;
            }

            float poseTimeout = 10f;
            elapsed = 0f;
            bool gotPose = false;
            while (elapsed < poseTimeout)
            {
                NativeArray<NativeTransform> pose = _source.GetSkeletonPose();
                gotPose = pose.IsCreated && pose.Length > 0;
                if (pose.IsCreated)
                    pose.Dispose();

                if (gotPose)
                    break;

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!gotPose)
            {
                Debug.LogError("[MetaBodySkeletonProvider] Timed out waiting for a Movement SDK skeleton pose.");
                SkeletonReady = true;
                yield break;
            }

            BuildRuntimeJointTransforms();
            CacheHeadReferenceTransforms();

            if (enableBoneVisualization)
                CreateMarkers();

            SkeletonReady = true;

            if (verboseLogging)
            {
                Debug.Log(
                    $"[MetaBodySkeletonProvider] Ready. Runtime joints={_jointTransforms.Count}, " +
                    $"alias keys={BodySkeletonTransformDict.Count}, skeleton={_source.ProvidedSkeletonType}");
            }
        }

        private void BuildRuntimeJointTransforms()
        {
            if (_runtimeJointsRoot != null)
                Destroy(_runtimeJointsRoot.gameObject);

            _runtimeJointsRoot = new GameObject("RuntimeJoints_FromMetaSourcePose").transform;
            _runtimeJointsRoot.SetParent(transform, false);

            _jointTransforms.Clear();
            BodySkeletonTransformDict.Clear();

            NativeArray<NativeTransform> pose = _source.GetSkeletonPose();
            int jointCount = pose.IsCreated ? pose.Length : 0;
            if (pose.IsCreated)
                pose.Dispose();

            for (int i = 0; i < jointCount; i++)
            {
                var joint = new GameObject(GetInternalJointName(i)).transform;
                joint.SetParent(_runtimeJointsRoot, false);
                _jointTransforms.Add(joint);

                string aliasKey = GetAliasKeyForMapper(i);
                if (!string.IsNullOrEmpty(aliasKey) && !BodySkeletonTransformDict.ContainsKey(aliasKey))
                    BodySkeletonTransformDict[aliasKey] = joint;
            }
        }

        private void ApplyPoseToRuntimeTransforms(NativeArray<NativeTransform> pose)
        {
            int count = Mathf.Min(pose.Length, _jointTransforms.Count);
            Transform sourceTransform = _source.transform;

            for (int i = 0; i < count; i++)
            {
                NativeTransform nativeTransform = pose[i];
                Transform joint = _jointTransforms[i];
                joint.position = sourceTransform.TransformPoint(nativeTransform.Position);
                joint.rotation = sourceTransform.rotation * nativeTransform.Orientation;
            }
        }

        private void CacheHeadReferenceTransforms()
        {
            if (centerCamAnchor != null)
                BodySkeletonTransformDict["CenterCamAnchor"] = centerCamAnchor;
            if (centerEyeFront != null)
            {
                BodySkeletonTransformDict["CenterEyeFront"] = centerEyeFront;
                BodySkeletonTransformDict["CenterCamOrientation"] = centerEyeFront;
            }
            if (centerEyeUp != null)
                BodySkeletonTransformDict["CenterEyeUp"] = centerEyeUp;
            if (leftEyeAnchor != null)
                BodySkeletonTransformDict["LeftEyeAnchor"] = leftEyeAnchor;
            if (rightEyeAnchor != null)
                BodySkeletonTransformDict["RightEyeAnchor"] = rightEyeAnchor;
        }

        private string GetInternalJointName(int index)
        {
            string prefix = _source != null && _source.ProvidedSkeletonType == OVRPlugin.BodyJointSet.UpperBody
                ? "UpperBody_Joint"
                : "FullBody_Joint";
            return $"{prefix}_{index}";
        }

        private static string GetAliasKeyForMapper(int index)
        {
            return index switch
            {
                1 => "FullBody_Hips",
                2 => "FullBody_SpineLower",
                5 => "FullBody_Chest",
                6 => "FullBody_Neck",
                7 => "FullBody_Head",
                8 => "FullBody_LeftShoulder",
                10 => "FullBody_LeftArmUpper",
                11 => "FullBody_LeftArmLower",
                13 => "FullBody_RightShoulder",
                15 => "FullBody_RightArmUpper",
                16 => "FullBody_RightArmLower",
                18 => "FullBody_LeftHandPalm",
                19 => "FullBody_LeftHandWrist",
                20 => "FullBody_LeftHandThumbMetacarpal",
                21 => "FullBody_LeftHandThumbProximal",
                22 => "FullBody_LeftHandThumbDistal",
                23 => "FullBody_LeftHandThumbTip",
                24 => "FullBody_LeftHandIndexMetacarpal",
                25 => "FullBody_LeftHandIndexProximal",
                26 => "FullBody_LeftHandIndexIntermediate",
                27 => "FullBody_LeftHandIndexDistal",
                28 => "FullBody_LeftHandIndexTip",
                29 => "FullBody_LeftHandMiddleMetacarpal",
                30 => "FullBody_LeftHandMiddleProximal",
                31 => "FullBody_LeftHandMiddleIntermediate",
                32 => "FullBody_LeftHandMiddleDistal",
                33 => "FullBody_LeftHandMiddleTip",
                34 => "FullBody_LeftHandRingMetacarpal",
                35 => "FullBody_LeftHandRingProximal",
                36 => "FullBody_LeftHandRingIntermediate",
                37 => "FullBody_LeftHandRingDistal",
                38 => "FullBody_LeftHandRingTip",
                39 => "FullBody_LeftHandLittleMetacarpal",
                40 => "FullBody_LeftHandLittleProximal",
                41 => "FullBody_LeftHandLittleIntermediate",
                42 => "FullBody_LeftHandLittleDistal",
                43 => "FullBody_LeftHandLittleTip",
                44 => "FullBody_RightHandPalm",
                45 => "FullBody_RightHandWrist",
                46 => "FullBody_RightHandThumbMetacarpal",
                47 => "FullBody_RightHandThumbProximal",
                48 => "FullBody_RightHandThumbDistal",
                49 => "FullBody_RightHandThumbTip",
                50 => "FullBody_RightHandIndexMetacarpal",
                51 => "FullBody_RightHandIndexProximal",
                52 => "FullBody_RightHandIndexIntermediate",
                53 => "FullBody_RightHandIndexDistal",
                54 => "FullBody_RightHandIndexTip",
                55 => "FullBody_RightHandMiddleMetacarpal",
                56 => "FullBody_RightHandMiddleProximal",
                57 => "FullBody_RightHandMiddleIntermediate",
                58 => "FullBody_RightHandMiddleDistal",
                59 => "FullBody_RightHandMiddleTip",
                60 => "FullBody_RightHandRingMetacarpal",
                61 => "FullBody_RightHandRingProximal",
                62 => "FullBody_RightHandRingIntermediate",
                63 => "FullBody_RightHandRingDistal",
                64 => "FullBody_RightHandRingTip",
                65 => "FullBody_RightHandLittleMetacarpal",
                66 => "FullBody_RightHandLittleProximal",
                67 => "FullBody_RightHandLittleIntermediate",
                68 => "FullBody_RightHandLittleDistal",
                69 => "FullBody_RightHandLittleTip",
                70 => "FullBody_LeftUpperLeg",
                71 => "FullBody_LeftLowerLeg",
                73 => "FullBody_LeftFootAnkle",
                76 => "FullBody_LeftFootBall",
                77 => "FullBody_RightUpperLeg",
                78 => "FullBody_RightLowerLeg",
                80 => "FullBody_RightFootAnkle",
                83 => "FullBody_RightFootBall",
                _ => null
            };
        }

        private T FindAnyComponent<T>() where T : MonoBehaviour
        {
            var components = UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var component in components)
            {
                if (component != null && !string.IsNullOrEmpty(component.gameObject.scene.name))
                    return component;
            }

            return null;
        }

        private void BuildHandStitchMaps()
        {
            _leftMap = BuildHandMap("Left");
            _rightMap = BuildHandMap("Right");
        }

        private static Dictionary<string, OVRSkeleton.BoneId> BuildHandMap(string side)
        {
            return new Dictionary<string, OVRSkeleton.BoneId>
            {
                { $"FullBody_{side}HandWrist", OVRSkeleton.BoneId.XRHand_Wrist },
                { $"FullBody_{side}HandPalm", OVRSkeleton.BoneId.XRHand_Palm },
                { $"FullBody_{side}HandThumbMetacarpal", OVRSkeleton.BoneId.XRHand_ThumbMetacarpal },
                { $"FullBody_{side}HandThumbProximal", OVRSkeleton.BoneId.XRHand_ThumbProximal },
                { $"FullBody_{side}HandThumbDistal", OVRSkeleton.BoneId.XRHand_ThumbDistal },
                { $"FullBody_{side}HandThumbTip", OVRSkeleton.BoneId.XRHand_ThumbTip },
                { $"FullBody_{side}HandIndexMetacarpal", OVRSkeleton.BoneId.XRHand_IndexMetacarpal },
                { $"FullBody_{side}HandIndexProximal", OVRSkeleton.BoneId.XRHand_IndexProximal },
                { $"FullBody_{side}HandIndexIntermediate", OVRSkeleton.BoneId.XRHand_IndexIntermediate },
                { $"FullBody_{side}HandIndexDistal", OVRSkeleton.BoneId.XRHand_IndexDistal },
                { $"FullBody_{side}HandIndexTip", OVRSkeleton.BoneId.XRHand_IndexTip },
                { $"FullBody_{side}HandMiddleMetacarpal", OVRSkeleton.BoneId.XRHand_MiddleMetacarpal },
                { $"FullBody_{side}HandMiddleProximal", OVRSkeleton.BoneId.XRHand_MiddleProximal },
                { $"FullBody_{side}HandMiddleIntermediate", OVRSkeleton.BoneId.XRHand_MiddleIntermediate },
                { $"FullBody_{side}HandMiddleDistal", OVRSkeleton.BoneId.XRHand_MiddleDistal },
                { $"FullBody_{side}HandMiddleTip", OVRSkeleton.BoneId.XRHand_MiddleTip },
                { $"FullBody_{side}HandRingMetacarpal", OVRSkeleton.BoneId.XRHand_RingMetacarpal },
                { $"FullBody_{side}HandRingProximal", OVRSkeleton.BoneId.XRHand_RingProximal },
                { $"FullBody_{side}HandRingIntermediate", OVRSkeleton.BoneId.XRHand_RingIntermediate },
                { $"FullBody_{side}HandRingDistal", OVRSkeleton.BoneId.XRHand_RingDistal },
                { $"FullBody_{side}HandRingTip", OVRSkeleton.BoneId.XRHand_RingTip },
                { $"FullBody_{side}HandLittleMetacarpal", OVRSkeleton.BoneId.XRHand_LittleMetacarpal },
                { $"FullBody_{side}HandLittleProximal", OVRSkeleton.BoneId.XRHand_LittleProximal },
                { $"FullBody_{side}HandLittleIntermediate", OVRSkeleton.BoneId.XRHand_LittleIntermediate },
                { $"FullBody_{side}HandLittleDistal", OVRSkeleton.BoneId.XRHand_LittleDistal },
                { $"FullBody_{side}HandLittleTip", OVRSkeleton.BoneId.XRHand_LittleTip },
            };
        }

        private void ApplyHandStitch()
        {
            if (IsOpenXRHandReady(leftOpenXRSkeleton, leftOVRHand))
            {
                _leftBoneTf ??= BuildBoneLookup(leftOpenXRSkeleton);
                ApplyStitchMap(_leftMap, _leftBoneTf);
            }
            else
            {
                _leftBoneTf = null;
            }

            if (IsOpenXRHandReady(rightOpenXRSkeleton, rightOVRHand))
            {
                _rightBoneTf ??= BuildBoneLookup(rightOpenXRSkeleton);
                ApplyStitchMap(_rightMap, _rightBoneTf);
            }
            else
            {
                _rightBoneTf = null;
            }
        }

        private bool IsOpenXRHandReady(OVRSkeleton skeleton, OVRHand hand)
        {
            if (skeleton == null || !skeleton.IsInitialized || skeleton.Bones == null || skeleton.Bones.Count == 0)
                return false;

            if (hand == null)
                return true;

            if (!hand.IsDataValid || !hand.IsTracked)
                return false;
            if (requireHighConfidence && !hand.IsDataHighConfidence)
                return false;

            var wristBone = skeleton.Bones.FirstOrDefault(bone => bone.Id == OVRSkeleton.BoneId.XRHand_Wrist);
            return wristBone == null ||
                   wristBone.Transform == null ||
                   wristBone.Transform.position.magnitude >= minDistanceThreshold;
        }

        private static Dictionary<OVRSkeleton.BoneId, Transform> BuildBoneLookup(OVRSkeleton skeleton)
        {
            var lookup = new Dictionary<OVRSkeleton.BoneId, Transform>();
            foreach (var bone in skeleton.Bones)
            {
                if (bone.Transform != null && !lookup.ContainsKey(bone.Id))
                    lookup.Add(bone.Id, bone.Transform);
            }

            return lookup;
        }

        private void ApplyStitchMap(Dictionary<string, OVRSkeleton.BoneId> map, Dictionary<OVRSkeleton.BoneId, Transform> lookup)
        {
            if (map == null || lookup == null)
                return;

            foreach (var kvp in map)
            {
                bool isWristKey = kvp.Key.Contains("HandWrist");
                bool isPalmKey = kvp.Key.Contains("HandPalm");

                if (isWristKey && !overwriteWrist)
                    continue;
                if (isPalmKey && !overwritePalm)
                    continue;
                if (!isWristKey && !isPalmKey && !overwriteFingers)
                    continue;

                if (!BodySkeletonTransformDict.TryGetValue(kvp.Key, out var targetTransform) ||
                    !lookup.TryGetValue(kvp.Value, out var sourceTransform) ||
                    targetTransform == null ||
                    sourceTransform == null)
                {
                    continue;
                }

                targetTransform.position = sourceTransform.position;
                targetTransform.rotation = sourceTransform.rotation;
            }
        }

        private void CreateMarkers()
        {
            if (_debugContainer != null)
                Destroy(_debugContainer);

            _debugContainer = new GameObject("BoneMarkers_Debug");
            _debugContainer.transform.SetParent(transform, false);
            _markers.Clear();

            foreach (var kvp in BodySkeletonTransformDict)
            {
                if (kvp.Value == null)
                    continue;

                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = $"Marker_{kvp.Key}";
                marker.transform.localScale = Vector3.one * boneMarkerSize;
                marker.transform.SetParent(_debugContainer.transform, false);

                var collider = marker.GetComponent<Collider>();
                if (collider != null)
                    Destroy(collider);

                var renderer = marker.GetComponent<Renderer>();
                if (renderer != null && debugMarkerMaterial != null)
                    renderer.sharedMaterial = debugMarkerMaterial;

                _markers[kvp.Key] = marker;
            }
        }

        private void UpdateMarkers()
        {
            foreach (var kvp in _markers)
            {
                if (kvp.Value != null && BodySkeletonTransformDict.TryGetValue(kvp.Key, out var transformValue) && transformValue != null)
                {
                    kvp.Value.transform.position = transformValue.position;
                    kvp.Value.transform.rotation = transformValue.rotation;
                }
            }
        }
    }
}

