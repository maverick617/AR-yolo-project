using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace AR80sRetro
{
    [DefaultExecutionOrder(-30000)]
    public sealed class ARDepthFrameProvider : MonoBehaviour
    {
        private readonly struct MeasurementDepthSample
        {
            public MeasurementDepthSample(Vector2 topLeftNormalizedPoint, float depth, float confidence)
            {
                TopLeftNormalizedPoint = topLeftNormalizedPoint;
                Depth = depth;
                Confidence = confidence;
            }

            public Vector2 TopLeftNormalizedPoint { get; }
            public float Depth { get; }
            public float Confidence { get; }
        }

        [SerializeField] private AROcclusionManager occlusionManager;
        [SerializeField] private ARCameraManager cameraManager;
        [SerializeField] private Camera arCamera;
        [SerializeField] private EnvironmentDepthMode requestedDepthMode = EnvironmentDepthMode.Fastest;
        [SerializeField] private bool requestTemporalSmoothing = true;
        [SerializeField, Range(0f, 0.2f)] private float sampleRadiusNormalized = 0.025f;
        [SerializeField, Range(0f, 1f)] private float minDepthMeters = 0.15f;
        [SerializeField, Range(1f, 20f)] private float maxDepthMeters = 8f;
        [SerializeField, Range(0, 255)] private int minConfidence = 1;
        [SerializeField] private bool flipDepthX;
        [SerializeField] private bool flipDepthY;
        [SerializeField] private bool logDepthAvailability;

        [Header("Multi-point object measurement")]
        [SerializeField, Range(7, 31)] private int measurementGridSize = 17;
        [SerializeField, Range(0f, 0.2f)] private float measurementBoxInset = 0.035f;
        [SerializeField, Range(0.01f, 0.5f)] private float measurementDepthToleranceMeters = 0.04f;
        [SerializeField, Range(0.01f, 0.5f)] private float measurementRelativeDepthTolerance = 0.06f;
        [SerializeField, Min(8)] private int minimumMeasurementSamples = 24;

        private readonly List<float> depthSamples = new List<float>(25);
        private readonly List<float> measurementSeedDepths = new List<float>(128);
        private readonly List<MeasurementDepthSample> measurementSamples =
            new List<MeasurementDepthSample>(512);
        private readonly List<float> localXSamples = new List<float>(512);
        private readonly List<float> localYSamples = new List<float>(512);
        private readonly List<float> localZSamples = new List<float>(512);
        private readonly List<float> localTangentialSamples = new List<float>(512);
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

            EnsureMeasurementOnlyOcclusionManager();
        }

        private void Reset()
        {
            arCamera = Camera.main;
            cameraManager = arCamera != null
                ? arCamera.GetComponent<ARCameraManager>()
                : FindObjectOfType<ARCameraManager>();
            occlusionManager = arCamera != null
                ? arCamera.GetComponent<AROcclusionManager>()
                : FindObjectOfType<AROcclusionManager>();
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

            Vector2 screenPoint = detection.ToScreenPoint(Screen.width, Screen.height, normalizedAnchorInBox);
            if (!TrySampleDepthMeters(detection, normalizedAnchorInBox, out depthMeters, out confidence))
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

        /// <summary>
        /// Builds a small depth point cloud inside the detection region and rejects
        /// samples that are not in the central object's depth cluster. This is the
        /// detector-box fallback used until a pixel segmentation mask is supplied.
        /// </summary>
        public bool TryMeasureObjectBounds(
            DetectionResult detection,
            Pose referencePose,
            out CupMeasurement measurement)
        {
            measurement = default;
            if (occlusionManager == null || arCamera == null)
            {
                return false;
            }

            if (!TryGetLatestDisplayMatrix(out Matrix4x4 displayMatrix))
            {
                return false;
            }

            if (!occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
            {
                MaybeLogDepthUnavailable();
                return false;
            }

            bool hasConfidenceImage = occlusionManager.TryAcquireEnvironmentDepthConfidenceCpuImage(
                out XRCpuImage confidenceImage);

            try
            {
                measurementSamples.Clear();
                measurementSeedDepths.Clear();
                localXSamples.Clear();
                localYSamples.Clear();
                localZSamples.Clear();
                localTangentialSamples.Clear();

                int gridSize = Mathf.Max(7, measurementGridSize);
                float inset = Mathf.Clamp(measurementBoxInset, 0f, 0.2f);
                Rect box = detection.NormalizedBox;

                for (int y = 0; y < gridSize; y++)
                {
                    float boxY = Mathf.Lerp(inset, 1f - inset, y / (float)(gridSize - 1));
                    for (int x = 0; x < gridSize; x++)
                    {
                        float boxX = Mathf.Lerp(inset, 1f - inset, x / (float)(gridSize - 1));
                        Vector2 point = new Vector2(
                            Mathf.Clamp01(box.x + box.width * boxX),
                            Mathf.Clamp01(box.y + box.height * boxY));

                        if (detection.HasMask
                            && !detection.Mask.ContainsTopLeftNormalizedPoint(point))
                        {
                            continue;
                        }

                        if (!TryMapScreenPointToImage(
                            point,
                            depthImage.width,
                            depthImage.height,
                            displayMatrix,
                            out int depthX,
                            out int depthY))
                        {
                            continue;
                        }

                        if (!TryReadDepth(depthImage, depthX, depthY, out float depth)
                            || depth < minDepthMeters
                            || depth > maxDepthMeters)
                        {
                            continue;
                        }

                        float confidenceValue = 2f;
                        if (hasConfidenceImage)
                        {
                            if (!TryMapScreenPointToImage(
                                point,
                                confidenceImage.width,
                                confidenceImage.height,
                                displayMatrix,
                                out int confidenceX,
                                out int confidenceY))
                            {
                                continue;
                            }

                            if (!TryReadConfidence(
                                confidenceImage,
                                confidenceX,
                                confidenceY,
                                out confidenceValue)
                                || confidenceValue < minConfidence)
                            {
                                continue;
                            }
                        }

                        measurementSamples.Add(new MeasurementDepthSample(
                            point,
                            depth,
                            hasConfidenceImage ? Mathf.Clamp01(confidenceValue / 2f) : 0.75f));

                        if (boxX >= 0.25f && boxX <= 0.75f
                            && boxY >= 0.2f && boxY <= 0.8f)
                        {
                            measurementSeedDepths.Add(depth);
                        }
                    }
                }

                if (measurementSeedDepths.Count == 0)
                {
                    for (int i = 0; i < measurementSamples.Count; i++)
                    {
                        measurementSeedDepths.Add(measurementSamples[i].Depth);
                    }
                }

                if (measurementSeedDepths.Count == 0)
                {
                    return false;
                }

                measurementSeedDepths.Sort();
                float seedDepth = measurementSeedDepths[measurementSeedDepths.Count / 2];
                float tolerance = Mathf.Max(
                    measurementDepthToleranceMeters,
                    seedDepth * measurementRelativeDepthTolerance);
                Quaternion inverseReferenceRotation = Quaternion.Inverse(referencePose.rotation);
                Vector3 localViewDirection = inverseReferenceRotation
                    * (arCamera.transform.position - referencePose.position);
                Vector2 horizontalView = new Vector2(
                    localViewDirection.x,
                    localViewDirection.z);
                if (horizontalView.sqrMagnitude < 0.0001f)
                {
                    horizontalView = Vector2.right;
                }
                else
                {
                    horizontalView.Normalize();
                }

                // For a round cup, the horizontal direction perpendicular to the
                // camera ray spans the body diameter independently of which of the
                // three radial tags is currently visible.
                Vector2 horizontalTangent = new Vector2(
                    -horizontalView.y,
                    horizontalView.x);
                float confidenceSum = 0f;
                int acceptedCount = 0;

                for (int i = 0; i < measurementSamples.Count; i++)
                {
                    MeasurementDepthSample sample = measurementSamples[i];
                    if (Mathf.Abs(sample.Depth - seedDepth) > tolerance)
                    {
                        continue;
                    }

                    Vector3 worldPoint = arCamera.ViewportToWorldPoint(new Vector3(
                        sample.TopLeftNormalizedPoint.x,
                        1f - sample.TopLeftNormalizedPoint.y,
                        sample.Depth));
                    Vector3 localPoint = inverseReferenceRotation
                        * (worldPoint - referencePose.position);
                    localXSamples.Add(localPoint.x);
                    localYSamples.Add(localPoint.y);
                    localZSamples.Add(localPoint.z);
                    localTangentialSamples.Add(
                        localPoint.x * horizontalTangent.x
                        + localPoint.z * horizontalTangent.y);
                    confidenceSum += sample.Confidence;
                    acceptedCount++;
                }

                if (acceptedCount < Mathf.Max(8, minimumMeasurementSamples))
                {
                    return false;
                }

                localXSamples.Sort();
                localYSamples.Sort();
                localZSamples.Sort();
                localTangentialSamples.Sort();
                float xLow = Percentile(localXSamples, 0.04f);
                float xHigh = Percentile(localXSamples, 0.96f);
                float yLow = Percentile(localYSamples, 0.04f);
                float yHigh = Percentile(localYSamples, 0.96f);
                float zLow = Percentile(localZSamples, 0.04f);
                float zHigh = Percentile(localZSamples, 0.96f);
                float tangentLow = Percentile(localTangentialSamples, 0.04f);
                float tangentHigh = Percentile(localTangentialSamples, 0.96f);

                float xExtent = xHigh - xLow;
                float zExtent = zHigh - zLow;
                float width = Mathf.Max(xExtent, zExtent);
                float bodyDiameter = tangentHigh - tangentLow;
                float height = yHigh - yLow;
                if (width <= 0.01f || height <= 0.01f)
                {
                    return false;
                }

                if (bodyDiameter <= 0.01f)
                {
                    bodyDiameter = width;
                }

                Vector3 localCenter = new Vector3(
                    (xLow + xHigh) * 0.5f,
                    (yLow + yHigh) * 0.5f,
                    (zLow + zHigh) * 0.5f);
                Vector3 centerWorld = referencePose.position
                    + referencePose.rotation * localCenter;
                float coverage = acceptedCount / (float)(gridSize * gridSize);
                float sampleConfidence = confidenceSum / acceptedCount;
                float confidence = Mathf.Clamp01(coverage * 0.55f + sampleConfidence * 0.45f);

                measurement = new CupMeasurement(
                    centerWorld,
                    new Vector3(width, height, bodyDiameter),
                    confidence,
                    acceptedCount,
                    true,
                    bodyDiameter > 0.01f ? bodyDiameter : width);
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

            if (occlusionManager.environmentDepthTemporalSmoothingRequested != requestTemporalSmoothing)
            {
                occlusionManager.environmentDepthTemporalSmoothingRequested = requestTemporalSmoothing;
            }
        }

        /// <summary>
        /// Keeps the depth producer away from ARCameraBackground. ARCameraBackground
        /// automatically consumes an AROcclusionManager placed on the camera and
        /// would let the real cup depth-occlude the replacement model. A manager on
        /// this measurement object still exposes the same XR CPU depth images
        /// without enabling camera-background occlusion compositing.
        /// </summary>
        private void EnsureMeasurementOnlyOcclusionManager()
        {
            if (occlusionManager != null
                && occlusionManager.gameObject == gameObject)
            {
                return;
            }

            AROcclusionManager oldOcclusionManager = occlusionManager;
            if (oldOcclusionManager == null && arCamera != null)
            {
                oldOcclusionManager = arCamera.GetComponent<AROcclusionManager>();
            }

            // Only one manager should own the XR occlusion subsystem at a time.
            if (oldOcclusionManager != null
                && oldOcclusionManager.gameObject != gameObject)
            {
                oldOcclusionManager.enabled = false;
            }

            AROcclusionManager measurementManager =
                GetComponent<AROcclusionManager>();
            if (measurementManager == null)
            {
                measurementManager = gameObject.AddComponent<AROcclusionManager>();
            }

            occlusionManager = measurementManager;
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

            if (!TryGetLatestDisplayMatrix(out Matrix4x4 displayMatrix))
            {
                return false;
            }

            if (!occlusionManager.TryAcquireEnvironmentDepthCpuImage(out XRCpuImage depthImage))
            {
                return false;
            }

            bool hasConfidenceImage = occlusionManager.TryAcquireEnvironmentDepthConfidenceCpuImage(
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
                            detection.NormalizedBox.x + detection.NormalizedBox.width * normalizedAnchorInBox.x,
                            detection.NormalizedBox.y + detection.NormalizedBox.height * normalizedAnchorInBox.y);
                        normalizedPoint.x += x * sampleRadiusNormalized;
                        normalizedPoint.y += y * sampleRadiusNormalized;
                        normalizedPoint.x = Mathf.Clamp01(normalizedPoint.x);
                        normalizedPoint.y = Mathf.Clamp01(normalizedPoint.y);

                        if (!TryMapScreenPointToImage(
                            normalizedPoint,
                            depthImage.width,
                            depthImage.height,
                            displayMatrix,
                            out int depthX,
                            out int depthY))
                        {
                            continue;
                        }

                        if (!TryReadDepth(depthImage, depthX, depthY, out float depth)
                            || depth < minDepthMeters
                            || depth > maxDepthMeters)
                        {
                            continue;
                        }

                        float confidenceValue = 255f;
                        if (hasConfidenceImage)
                        {
                            if (!TryMapScreenPointToImage(
                                normalizedPoint,
                                confidenceImage.width,
                                confidenceImage.height,
                                displayMatrix,
                                out int confidenceX,
                                out int confidenceY))
                            {
                                continue;
                            }

                            if (!TryReadConfidence(confidenceImage, confidenceX, confidenceY, out confidenceValue)
                                || confidenceValue < minConfidence)
                            {
                                continue;
                            }
                        }

                        depthSamples.Add(depth);
                        confidenceSum += confidenceValue;
                        confidenceCount++;
                    }
                }

                if (depthSamples.Count == 0)
                {
                    return false;
                }

                depthSamples.Sort();
                medianDepth = depthSamples[depthSamples.Count / 2];
                medianConfidence = confidenceCount > 0 ? confidenceSum / confidenceCount : 0f;
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

        private bool TryGetLatestDisplayMatrix(out Matrix4x4 displayMatrix)
        {
            displayMatrix = latestDisplayMatrix;
            return hasDisplayMatrix;
        }

        private void HandleCameraFrameReceived(ARCameraFrameEventArgs eventArgs)
        {
            if (!eventArgs.displayMatrix.HasValue)
            {
                return;
            }

            latestDisplayMatrix = eventArgs.displayMatrix.Value;
            hasDisplayMatrix = true;
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
            // ARKit's background shader multiplies the UV as a row vector.
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
            // ARCore's background shader uses displayMatrix * screen UV.
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

                    depth = BitConverter.ToSingle(new[]
                    {
                        data[offset],
                        data[offset + 1],
                        data[offset + 2],
                        data[offset + 3]
                    }, 0);
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

        private static bool TryReadConfidence(XRCpuImage image, int x, int y, out float confidence)
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

        private static float Percentile(List<float> sortedValues, float percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0f;
            }

            float index = Mathf.Clamp01(percentile) * (sortedValues.Count - 1);
            int lower = Mathf.FloorToInt(index);
            int upper = Mathf.Min(sortedValues.Count - 1, lower + 1);
            return Mathf.Lerp(sortedValues[lower], sortedValues[upper], index - lower);
        }

        private void MaybeLogDepthUnavailable()
        {
            if (!logDepthAvailability || Time.frameCount - lastAvailabilityLogFrame < 120)
            {
                return;
            }

            lastAvailabilityLogFrame = Time.frameCount;
            Debug.Log(
                $"Depth unavailable. requested={occlusionManager.requestedEnvironmentDepthMode}, current={occlusionManager.currentEnvironmentDepthMode}",
                this);
        }
    }
}
