using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace AR80sRetro
{
    /// <summary>
    /// Samples AR Foundation environment depth inside a YOLO detection box and
    /// converts the median depth sample into a world-space initialization point.
    /// </summary>
    [DefaultExecutionOrder(-30000)]
    public sealed class ARDepthFrameProvider : MonoBehaviour
    {
        private readonly struct DepthCandidate
        {
            public DepthCandidate(Vector2 topLeftScreenPoint, float depth, float confidence)
            {
                TopLeftScreenPoint = topLeftScreenPoint;
                Depth = depth;
                Confidence = confidence;
            }

            public Vector2 TopLeftScreenPoint { get; }
            public float Depth { get; }
            public float Confidence { get; }
        }

        [SerializeField] private AROcclusionManager occlusionManager;
        [SerializeField] private ARCameraManager cameraManager;
        [SerializeField] private Camera arCamera;
        [SerializeField] private EnvironmentDepthMode requestedDepthMode =
            EnvironmentDepthMode.Fastest;
        [SerializeField] private bool requestTemporalSmoothing;
        [Tooltip("Keep ARKit depth for measurement but prevent the real object depth from drawing over its virtual replacement.")]
        [SerializeField] private bool disableEnvironmentOcclusionRendering = true;
        [SerializeField, Range(0f, 0.2f)] private float sampleRadiusNormalized = 0.025f;
        [SerializeField, Range(0f, 1f)] private float minDepthMeters = 0.15f;
        [SerializeField, Range(1f, 20f)] private float maxDepthMeters = 8f;
        [SerializeField, Range(0, 255)] private int minConfidence = 1;
        [SerializeField] private bool flipDepthX;
        [SerializeField] private bool flipDepthY;
        [SerializeField] private bool logDepthAvailability;
        [Tooltip("Keep the assigned AROcclusionManager on its current GameObject so all mask-pose components share one depth producer.")]
        [SerializeField] private bool keepAssignedOcclusionManagerInPlace;

        [Header("Masked point cloud")]
        [SerializeField, Range(0.01f, 0.2f)] private float minimumDepthClusterMeters = 0.035f;
        [SerializeField, Range(1f, 8f)] private float madMultiplier = 3f;
        [SerializeField, Range(0.04f, 0.5f)] private float maximumDepthClusterMeters = 0.22f;
        [SerializeField, Range(1, 255)] private int maskThreshold = 128;

        private readonly List<float> depthSamples = new List<float>(25);
        private readonly List<float> absoluteDeviations = new List<float>(1024);
        private readonly List<DepthCandidate> depthCandidates = new List<DepthCandidate>(1024);
        private Matrix4x4 latestDisplayMatrix = Matrix4x4.identity;
        private bool hasDisplayMatrix;
        private int lastAvailabilityLogFrame = -120;

        public bool IsDepthActive => occlusionManager != null
            && occlusionManager.currentEnvironmentDepthMode != EnvironmentDepthMode.Disabled;

        private void Awake()
        {
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }

            if (cameraManager == null && arCamera != null)
            {
                cameraManager = arCamera.GetComponent<ARCameraManager>();
            }

            if (cameraManager == null)
            {
                cameraManager = FindObjectOfType<ARCameraManager>();
            }

            EnsureDepthManager();
        }

        private void Reset()
        {
            arCamera = Camera.main;
            cameraManager = arCamera != null
                ? arCamera.GetComponent<ARCameraManager>()
                : FindObjectOfType<ARCameraManager>();
            occlusionManager = FindObjectOfType<AROcclusionManager>();
        }

        private void OnEnable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived += HandleCameraFrameReceived;
            }

            ApplyDepthSettings();
        }

        private void OnDisable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived -= HandleCameraFrameReceived;
            }

            hasDisplayMatrix = false;
        }

        private void Update()
        {
            ApplyDepthSettings();
        }

        public bool TrySampleWorldPoint(
            DetectionResult detection,
            Vector2 normalizedAnchorInBox,
            out Vector3 worldPoint,
            out float depthMeters,
            out float confidence)
        {
            worldPoint = default;
            depthMeters = 0f;
            confidence = 0f;

            if (occlusionManager == null || arCamera == null)
            {
                return false;
            }

            Vector2 screenPoint = detection.ToScreenPoint(
                Screen.width,
                Screen.height,
                normalizedAnchorInBox);
            if (!TrySampleDepthMeters(
                detection,
                normalizedAnchorInBox,
                out depthMeters,
                out confidence))
            {
                MaybeLogDepthUnavailable();
                return false;
            }

            Vector3 viewportPoint = new Vector3(
                Mathf.Clamp01(screenPoint.x / Mathf.Max(1f, Screen.width)),
                Mathf.Clamp01(screenPoint.y / Mathf.Max(1f, Screen.height)),
                depthMeters);
            worldPoint = arCamera.ViewportToWorldPoint(viewportPoint);
            return true;
        }

        public bool TryBuildMaskedPointCloud(
            DetectionResult detection,
            int horizontalSamples,
            int verticalSamples,
            int minimumPointCount,
            out MaskedDepthPointCloud cloud)
        {
            cloud = null;
            if (occlusionManager == null
                || arCamera == null
                || !hasDisplayMatrix
                || !occlusionManager.TryAcquireEnvironmentDepthCpuImage(
                    out XRCpuImage depthImage))
            {
                MaybeLogDepthUnavailable();
                return false;
            }

            bool hasConfidenceImage =
                occlusionManager.TryAcquireEnvironmentDepthConfidenceCpuImage(
                    out XRCpuImage confidenceImage);

            depthCandidates.Clear();
            depthSamples.Clear();
            absoluteDeviations.Clear();

            try
            {
                int samplesX = Mathf.Max(2, horizontalSamples);
                int samplesY = Mathf.Max(2, verticalSamples);
                int acceptedMaskSamples = 0;

                for (int y = 0; y < samplesY; y++)
                {
                    float localV = (y + 0.5f) / samplesY;
                    for (int x = 0; x < samplesX; x++)
                    {
                        float localU = (x + 0.5f) / samplesX;
                        Vector2 topLeftPoint = new Vector2(
                            detection.NormalizedBox.xMin + detection.NormalizedBox.width * localU,
                            detection.NormalizedBox.yMin + detection.NormalizedBox.height * localV);

                        if (detection.Mask != null
                            && !detection.Mask.ContainsTopLeftNormalizedPoint(
                                topLeftPoint,
                                (byte)maskThreshold))
                        {
                            continue;
                        }

                        acceptedMaskSamples++;
                        if (!TryMapScreenPointToImage(
                            topLeftPoint,
                            depthImage.width,
                            depthImage.height,
                            latestDisplayMatrix,
                            out int depthX,
                            out int depthY)
                            || !TryReadDepth(depthImage, depthX, depthY, out float depth)
                            || depth < minDepthMeters
                            || depth > maxDepthMeters)
                        {
                            continue;
                        }

                        float normalizedConfidence = 0.75f;
                        if (hasConfidenceImage)
                        {
                            if (!TryMapScreenPointToImage(
                                topLeftPoint,
                                confidenceImage.width,
                                confidenceImage.height,
                                latestDisplayMatrix,
                                out int confidenceX,
                                out int confidenceY)
                                || !TryReadConfidence(
                                    confidenceImage,
                                    confidenceX,
                                    confidenceY,
                                    out float confidenceValue)
                                || confidenceValue < minConfidence)
                            {
                                continue;
                            }

                            normalizedConfidence = Mathf.Clamp01(confidenceValue / 2f);
                        }

                        depthCandidates.Add(new DepthCandidate(
                            topLeftPoint,
                            depth,
                            normalizedConfidence));
                        depthSamples.Add(depth);
                    }
                }

                if (depthCandidates.Count < minimumPointCount)
                {
                    return false;
                }

                depthSamples.Sort();
                float medianDepth = MedianOfSorted(depthSamples);
                for (int i = 0; i < depthSamples.Count; i++)
                {
                    absoluteDeviations.Add(Mathf.Abs(depthSamples[i] - medianDepth));
                }

                absoluteDeviations.Sort();
                float mad = MedianOfSorted(absoluteDeviations);
                float clusterRadius = Mathf.Clamp(
                    Mathf.Max(minimumDepthClusterMeters, mad * madMultiplier),
                    minimumDepthClusterMeters,
                    maximumDepthClusterMeters);

                List<Vector3> points = new List<Vector3>(depthCandidates.Count);
                Vector3 centroid = Vector3.zero;
                float confidenceSum = 0f;
                for (int i = 0; i < depthCandidates.Count; i++)
                {
                    DepthCandidate candidate = depthCandidates[i];
                    if (Mathf.Abs(candidate.Depth - medianDepth) > clusterRadius)
                    {
                        continue;
                    }

                    Vector3 worldPoint = arCamera.ViewportToWorldPoint(new Vector3(
                        candidate.TopLeftScreenPoint.x,
                        1f - candidate.TopLeftScreenPoint.y,
                        candidate.Depth));
                    points.Add(worldPoint);
                    centroid += worldPoint;
                    confidenceSum += candidate.Confidence;
                }

                if (points.Count < minimumPointCount)
                {
                    return false;
                }

                centroid /= points.Count;
                float sampleCoverage = acceptedMaskSamples > 0
                    ? (float)points.Count / acceptedMaskSamples
                    : 0f;
                float depthConfidence = confidenceSum / points.Count;
                float confidence = detection.Confidence
                    * Mathf.Clamp01(sampleCoverage * 2f)
                    * depthConfidence;
                cloud = new MaskedDepthPointCloud(
                    detection,
                    points,
                    centroid,
                    medianDepth,
                    confidence,
                    acceptedMaskSamples);
                return true;
            }
            finally
            {
                if (hasConfidenceImage)
                {
                    confidenceImage.Dispose();
                }

                depthImage.Dispose();
            }
        }

        private static float MedianOfSorted(List<float> values)
        {
            int count = values.Count;
            if (count == 0)
            {
                return 0f;
            }

            int middle = count / 2;
            return (count & 1) == 0
                ? (values[middle - 1] + values[middle]) * 0.5f
                : values[middle];
        }

        private void ApplyDepthSettings()
        {
            if (occlusionManager == null)
            {
                return;
            }

            if (occlusionManager.requestedEnvironmentDepthMode != requestedDepthMode)
            {
                occlusionManager.requestedEnvironmentDepthMode = requestedDepthMode;
            }

            if (occlusionManager.environmentDepthTemporalSmoothingRequested
                != requestTemporalSmoothing)
            {
                occlusionManager.environmentDepthTemporalSmoothingRequested =
                    requestTemporalSmoothing;
            }

            UnityEngine.XR.ARSubsystems.OcclusionPreferenceMode preference =
                disableEnvironmentOcclusionRendering
                    ? UnityEngine.XR.ARSubsystems.OcclusionPreferenceMode.NoOcclusion
                    : UnityEngine.XR.ARSubsystems.OcclusionPreferenceMode.PreferEnvironmentOcclusion;
            if (occlusionManager.requestedOcclusionPreferenceMode != preference)
            {
                occlusionManager.requestedOcclusionPreferenceMode = preference;
            }
        }

        private void EnsureDepthManager()
        {
            if (keepAssignedOcclusionManagerInPlace && occlusionManager != null)
            {
                return;
            }

            if (occlusionManager != null && occlusionManager.gameObject == gameObject)
            {
                return;
            }

            AROcclusionManager oldManager = occlusionManager;
            if (oldManager == null && arCamera != null)
            {
                oldManager = arCamera.GetComponent<AROcclusionManager>();
            }

            if (oldManager != null && oldManager.gameObject != gameObject)
            {
                oldManager.enabled = false;
            }

            AROcclusionManager localManager = GetComponent<AROcclusionManager>();
            if (localManager == null)
            {
                localManager = gameObject.AddComponent<AROcclusionManager>();
            }

            occlusionManager = localManager;
        }

        private bool TrySampleDepthMeters(
            DetectionResult detection,
            Vector2 normalizedAnchorInBox,
            out float medianDepth,
            out float medianConfidence)
        {
            medianDepth = 0f;
            medianConfidence = 0f;
            depthSamples.Clear();

            if (!hasDisplayMatrix
                || !occlusionManager.TryAcquireEnvironmentDepthCpuImage(
                    out XRCpuImage depthImage))
            {
                return false;
            }

            bool hasConfidenceImage =
                occlusionManager.TryAcquireEnvironmentDepthConfidenceCpuImage(
                    out XRCpuImage confidenceImage);

            try
            {
                int gridRadius = sampleRadiusNormalized > 0f ? 2 : 0;
                float confidenceSum = 0f;
                int confidenceCount = 0;

                for (int y = -gridRadius; y <= gridRadius; y++)
                {
                    for (int x = -gridRadius; x <= gridRadius; x++)
                    {
                        Vector2 normalizedPoint = new Vector2(
                            detection.NormalizedBox.x
                                + detection.NormalizedBox.width * normalizedAnchorInBox.x,
                            detection.NormalizedBox.y
                                + detection.NormalizedBox.height * normalizedAnchorInBox.y);
                        normalizedPoint.x = Mathf.Clamp01(
                            normalizedPoint.x + x * sampleRadiusNormalized);
                        normalizedPoint.y = Mathf.Clamp01(
                            normalizedPoint.y + y * sampleRadiusNormalized);

                        if (!TryMapScreenPointToImage(
                            normalizedPoint,
                            depthImage.width,
                            depthImage.height,
                            latestDisplayMatrix,
                            out int depthX,
                            out int depthY)
                            || !TryReadDepth(depthImage, depthX, depthY, out float depth)
                            || depth < minDepthMeters
                            || depth > maxDepthMeters)
                        {
                            continue;
                        }

                        float normalizedConfidence = 0.75f;
                        if (hasConfidenceImage)
                        {
                            if (!TryMapScreenPointToImage(
                                normalizedPoint,
                                confidenceImage.width,
                                confidenceImage.height,
                                latestDisplayMatrix,
                                out int confidenceX,
                                out int confidenceY)
                                || !TryReadConfidence(
                                    confidenceImage,
                                    confidenceX,
                                    confidenceY,
                                    out float confidenceValue)
                                || confidenceValue < minConfidence)
                            {
                                continue;
                            }

                            normalizedConfidence = Mathf.Clamp01(confidenceValue / 2f);
                        }

                        depthSamples.Add(depth);
                        confidenceSum += normalizedConfidence;
                        confidenceCount++;
                    }
                }

                if (depthSamples.Count == 0)
                {
                    return false;
                }

                depthSamples.Sort();
                medianDepth = depthSamples[depthSamples.Count / 2];
                medianConfidence = confidenceCount > 0
                    ? confidenceSum / confidenceCount
                    : 0f;
                return true;
            }
            finally
            {
                if (hasConfidenceImage)
                {
                    confidenceImage.Dispose();
                }

                depthImage.Dispose();
            }
        }

        private void HandleCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
        {
            if (eventArgs.displayMatrix.HasValue)
            {
                latestDisplayMatrix = eventArgs.displayMatrix.Value;
                hasDisplayMatrix = true;
            }
        }

        private bool TryMapScreenPointToImage(
            Vector2 topLeftScreenPoint,
            int imageWidth,
            int imageHeight,
            Matrix4x4 displayMatrix,
            out int imageX,
            out int imageY)
        {
            imageX = 0;
            imageY = 0;
            Vector4 screenUv = new Vector4(
                Mathf.Clamp01(topLeftScreenPoint.x),
                Mathf.Clamp01(1f - topLeftScreenPoint.y),
                1f,
                0f);

            Vector2 cameraUv;
#if UNITY_IOS && !UNITY_EDITOR
            screenUv.w = 1f;
            cameraUv = new Vector2(
                screenUv.x * displayMatrix.m00
                    + screenUv.y * displayMatrix.m10
                    + screenUv.z * displayMatrix.m20
                    + screenUv.w * displayMatrix.m30,
                screenUv.x * displayMatrix.m01
                    + screenUv.y * displayMatrix.m11
                    + screenUv.z * displayMatrix.m21
                    + screenUv.w * displayMatrix.m31);
#else
            Vector4 transformed = displayMatrix * screenUv;
            cameraUv = new Vector2(transformed.x, transformed.y);
#endif

            Vector2 topLeftImagePoint = new Vector2(cameraUv.x, 1f - cameraUv.y);
            if (flipDepthX)
            {
                topLeftImagePoint.x = 1f - topLeftImagePoint.x;
            }

            if (flipDepthY)
            {
                topLeftImagePoint.y = 1f - topLeftImagePoint.y;
            }

            if (topLeftImagePoint.x < -0.01f
                || topLeftImagePoint.x > 1.01f
                || topLeftImagePoint.y < -0.01f
                || topLeftImagePoint.y > 1.01f)
            {
                return false;
            }

            imageX = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Clamp01(topLeftImagePoint.x) * (imageWidth - 1)),
                0,
                imageWidth - 1);
            imageY = Mathf.Clamp(
                Mathf.RoundToInt(Mathf.Clamp01(topLeftImagePoint.y) * (imageHeight - 1)),
                0,
                imageHeight - 1);
            return true;
        }

        private static bool TryReadDepth(XRCpuImage image, int x, int y, out float depth)
        {
            depth = 0f;
            if (image.planeCount <= 0)
            {
                return false;
            }

            XRCpuImage.Plane plane = image.GetPlane(0);
            int offset = y * plane.rowStride + x * plane.pixelStride;
            NativeArray<byte> data = plane.data;

            switch (image.format)
            {
                case XRCpuImage.Format.DepthFloat32:
                case XRCpuImage.Format.OneComponent32:
                    if (offset + 3 >= data.Length)
                    {
                        return false;
                    }

                    int bits = data[offset]
                        | (data[offset + 1] << 8)
                        | (data[offset + 2] << 16)
                        | (data[offset + 3] << 24);
                    depth = BitConverter.Int32BitsToSingle(bits);
                    return !float.IsNaN(depth) && !float.IsInfinity(depth);

                case XRCpuImage.Format.DepthUint16:
                    if (offset + 1 >= data.Length)
                    {
                        return false;
                    }

                    ushort millimeters = (ushort)(data[offset] | (data[offset + 1] << 8));
                    depth = millimeters * 0.001f;
                    return millimeters > 0;

                default:
                    return false;
            }
        }

        private static bool TryReadConfidence(
            XRCpuImage image,
            int x,
            int y,
            out float confidence)
        {
            confidence = 0f;
            if (image.planeCount <= 0)
            {
                return false;
            }

            XRCpuImage.Plane plane = image.GetPlane(0);
            int offset = y * plane.rowStride + x * plane.pixelStride;
            NativeArray<byte> data = plane.data;
            if (offset < 0 || offset >= data.Length)
            {
                return false;
            }

            confidence = data[offset];
            return true;
        }

        private void MaybeLogDepthUnavailable()
        {
            if (!logDepthAvailability || Time.frameCount - lastAvailabilityLogFrame < 120)
            {
                return;
            }

            lastAvailabilityLogFrame = Time.frameCount;
            Debug.Log(
                $"Depth unavailable. requested={occlusionManager.requestedEnvironmentDepthMode}, "
                + $"current={occlusionManager.currentEnvironmentDepthMode}",
                this);
        }
    }
}
