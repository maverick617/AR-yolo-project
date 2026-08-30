using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Uses the YOLO cup box as the size envelope. Environment depth is preferred
    /// only to convert that screen envelope into metres. If depth is unavailable,
    /// the Tag pose supplies camera distance, but never supplies the cup extent.
    /// </summary>
    public sealed class CupDimensionEstimator : MonoBehaviour
    {
        [Tooltip("The AprilTag experiment uses Tag range plus the YOLO box. Enable only for a separate RGB-LiDAR experiment.")]
        [SerializeField] private bool useEnvironmentDepth;
        [SerializeField] private ARDepthFrameProvider depthProvider;
        [SerializeField] private Camera arCamera;
        [SerializeField, Min(0.01f)] private float minimumCupDimensionMeters = 0.03f;
        [SerializeField, Min(0.05f)] private float maximumCupDimensionMeters = 0.6f;
        [SerializeField, Range(0.5f, 1.5f)] private float fallbackHeightMultiplier = 0.94f;
        [SerializeField, Range(0.4f, 1.2f)] private float fallbackWidthMultiplier = 0.82f;

        private void Awake()
        {
            ResolveDependencies();
        }

        private void Reset()
        {
            ResolveDependencies();
        }

        public bool TryMeasure(
            DetectionResult detection,
            Pose tagWorldPose,
            Quaternion expectedCupRotation,
            out CupMeasurement measurement)
        {
            ResolveDependencies();
            CupMeasurement depthMeasurement = default;
            bool hasDepth = useEnvironmentDepth
                && depthProvider != null
                && depthProvider.TryMeasureObjectBounds(
                    detection,
                    new Pose(tagWorldPose.position, expectedCupRotation),
                    out depthMeasurement)
                && depthMeasurement.CenterWorld != Vector3.zero;

            float cameraDepth;
            float confidence;
            int sampleCount;
            bool usedEnvironmentDepth;
            if (hasDepth)
            {
                cameraDepth = arCamera.transform
                    .InverseTransformPoint(depthMeasurement.CenterWorld).z;
                confidence = depthMeasurement.Confidence;
                sampleCount = depthMeasurement.SampleCount;
                usedEnvironmentDepth = true;
            }
            else
            {
                // A 2D YOLO box cannot produce metres without a depth value. The
                // Tag is used only as a fallback range observation; the box still
                // determines width and height, and the final renderer is matched
                // back to the YOLO box in screen space.
                cameraDepth = arCamera.transform
                    .InverseTransformPoint(tagWorldPose.position).z;
                confidence = 0.35f;
                sampleCount = 1;
                usedEnvironmentDepth = false;
            }

            return TryMeasureYoloEnvelopeAtDepth(
                detection,
                cameraDepth,
                confidence,
                sampleCount,
                usedEnvironmentDepth,
                out measurement);
        }

        private bool TryMeasureYoloEnvelopeAtDepth(
            DetectionResult detection,
            float cameraDepth,
            float confidence,
            int sampleCount,
            bool usedEnvironmentDepth,
            out CupMeasurement measurement)
        {
            measurement = default;
            if (arCamera == null || cameraDepth <= 0.05f)
            {
                return false;
            }

            Rect box = detection.NormalizedBox;
            Vector3 left = ViewportToWorld(box.xMin, box.center.y, cameraDepth);
            Vector3 right = ViewportToWorld(box.xMax, box.center.y, cameraDepth);
            Vector3 top = ViewportToWorld(box.center.x, box.yMin, cameraDepth);
            Vector3 bottom = ViewportToWorld(box.center.x, box.yMax, cameraDepth);
            Vector3 center = ViewportToWorld(box.center.x, box.center.y, cameraDepth);

            float detectedWidth = Vector3.Distance(left, right);
            float detectedHeight = Vector3.Distance(top, bottom);
            // YOLO's width may include a real handle or background. Store the
            // complete envelope in X, but use the calibrated body diameter in Z;
            // CupModelFitter deliberately fits its cylindrical body from Z.
            float bodyDiameter = detectedWidth * fallbackWidthMultiplier;
            float height = detectedHeight * fallbackHeightMultiplier;
            Vector3 size = new Vector3(detectedWidth, height, bodyDiameter);
            if (!IsPlausible(size))
            {
                return false;
            }

            measurement = new CupMeasurement(
                center,
                size,
                confidence,
                sampleCount,
                usedEnvironmentDepth,
                bodyDiameter);
            return true;
        }

        private Vector3 ViewportToWorld(float topLeftX, float topLeftY, float depth)
        {
            return arCamera.ViewportToWorldPoint(new Vector3(
                Mathf.Clamp01(topLeftX),
                Mathf.Clamp01(1f - topLeftY),
                depth));
        }

        private bool IsPlausible(Vector3 size)
        {
            float minimum = Mathf.Max(0.01f, minimumCupDimensionMeters);
            float maximum = Mathf.Max(minimum, maximumCupDimensionMeters);
            return size.x >= minimum
                && size.y >= minimum
                && size.z >= minimum
                && size.x <= maximum
                && size.y <= maximum
                && size.z <= maximum;
        }

        private void ResolveDependencies()
        {
            if (depthProvider == null)
            {
                depthProvider = FindObjectOfType<ARDepthFrameProvider>();
            }

            if (arCamera == null)
            {
                arCamera = Camera.main;
            }
        }
    }
}
