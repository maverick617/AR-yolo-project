using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    public sealed class RetroDetectionPipeline : MonoBehaviour
    {
        [SerializeField] private YoloObjectDetector detector;
        [SerializeField] private RetroReplacementManager replacementManager;

        private void Reset()
        {
            detector = FindObjectOfType<YoloObjectDetector>();
            replacementManager = FindObjectOfType<RetroReplacementManager>();
        }

        private void OnEnable()
        {
            if (detector != null)
            {
                detector.DetectionsReady += HandleDetectionsReady;
            }
        }

        private void OnDisable()
        {
            if (detector != null)
            {
                detector.DetectionsReady -= HandleDetectionsReady;
            }
        }

        private void HandleDetectionsReady(IReadOnlyList<DetectionResult> detections)
        {
            if (replacementManager != null)
            {
                replacementManager.ApplyDetections(detections);
            }
        }
    }
}
