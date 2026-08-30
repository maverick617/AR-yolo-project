using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Turns every YOLO-seg instance mask into a filtered ARKit depth point cloud.
    /// Acquiring one CPU depth image per detector result is acceptable at the
    /// detector's low update rate and keeps this component independent of YOLO.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    public sealed class MaskDepthPointCloudProvider : MonoBehaviour
    {
        [SerializeField] private YoloObjectDetector detector;
        [SerializeField] private ARDepthFrameProvider depthProvider;
        [SerializeField, Range(6, 48)] private int horizontalSamples = 12;
        [SerializeField, Range(6, 64)] private int verticalSamples = 16;
        [SerializeField, Range(8, 512)] private int minimumPointCount = 12;
        [SerializeField] private bool requireInstanceMask = true;
        [Tooltip("Only this YOLO class is converted to a pose. Leave empty to process every configured class.")]
        [SerializeField] private string targetLabel = "bottle";

        private readonly Dictionary<string, DetectionResult> bestDetections =
            new Dictionary<string, DetectionResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> bestSelectionScores =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector2> previousSelectionCenters =
            new Dictionary<string, Vector2>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> previousSelectionTimes =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        public event Action<MaskedDepthPointCloud> PointCloudReady;

        private void Reset()
        {
            detector = FindObjectOfType<YoloObjectDetector>();
            depthProvider = FindObjectOfType<ARDepthFrameProvider>();
        }

        private void OnEnable()
        {
            if (detector != null)
            {
                detector.DetectionsReady += HandleDetections;
            }
        }

        private void OnDisable()
        {
            if (detector != null)
            {
                detector.DetectionsReady -= HandleDetections;
            }
        }

        private void HandleDetections(IReadOnlyList<DetectionResult> detections)
        {
            if (depthProvider == null || detections == null)
            {
                return;
            }

            bestDetections.Clear();
            bestSelectionScores.Clear();
            float now = Time.unscaledTime;
            for (int i = 0; i < detections.Count; i++)
            {
                DetectionResult detection = detections[i];
                if ((!string.IsNullOrWhiteSpace(targetLabel)
                    && !string.Equals(
                        detection.Label,
                        targetLabel,
                        StringComparison.OrdinalIgnoreCase))
                    || (requireInstanceMask && !detection.HasMask))
                {
                    continue;
                }

                float selectionScore = detection.Confidence;
                if (previousSelectionCenters.TryGetValue(
                        detection.Label,
                        out Vector2 previousCenter)
                    && previousSelectionTimes.TryGetValue(
                        detection.Label,
                        out float previousTime)
                    && now - previousTime < 0.75f)
                {
                    // Prefer temporal continuity when two detections share a
                    // category. Confidence alone can alternate between them and
                    // make the only replacement jump back and forth.
                    float distance = Vector2.Distance(
                        detection.NormalizedCenter,
                        previousCenter);
                    selectionScore += Mathf.Max(0f, 0.32f - distance * 1.25f);
                }

                if (!bestSelectionScores.TryGetValue(
                        detection.Label,
                        out float bestScore)
                    || selectionScore > bestScore)
                {
                    bestDetections[detection.Label] = detection;
                    bestSelectionScores[detection.Label] = selectionScore;
                }
            }

            // The replacement controller displays one object per category. Do not
            // acquire a second LiDAR CPU image for lower-confidence duplicates.
            foreach (DetectionResult detection in bestDetections.Values)
            {
                if (!depthProvider.TryBuildMaskedPointCloud(
                    detection,
                    horizontalSamples,
                    verticalSamples,
                    minimumPointCount,
                    out MaskedDepthPointCloud cloud))
                {
                    // Transparent hand-held bottles often provide no usable
                    // environment-depth samples. Category pose can still obtain
                    // metric distance from the known physical size and mask
                    // silhouette, so emit a mask-only cloud instead of freezing
                    // the replacement at its last table-top pose.
                    cloud = new MaskedDepthPointCloud(
                        detection,
                        new List<Vector3>(0),
                        Vector3.zero,
                        0f,
                        detection.Confidence * 0.85f,
                        0);
                }

                previousSelectionCenters[detection.Label] = detection.NormalizedCenter;
                previousSelectionTimes[detection.Label] = now;
                PointCloudReady?.Invoke(cloud);
            }
        }
    }
}
