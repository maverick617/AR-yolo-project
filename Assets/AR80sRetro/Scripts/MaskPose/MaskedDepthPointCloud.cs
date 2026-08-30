using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Robust world-space depth samples that survived both an instance-mask test
    /// and median/MAD depth-cluster rejection.
    /// </summary>
    public sealed class MaskedDepthPointCloud
    {
        public MaskedDepthPointCloud(
            DetectionResult detection,
            List<Vector3> worldPoints,
            Vector3 centroid,
            float medianDepth,
            float confidence,
            int maskSampleCount)
        {
            Detection = detection;
            WorldPoints = worldPoints;
            Centroid = centroid;
            MedianDepth = medianDepth;
            Confidence = confidence;
            MaskSampleCount = maskSampleCount;
        }

        public DetectionResult Detection { get; }
        public IReadOnlyList<Vector3> WorldPoints { get; }
        public Vector3 Centroid { get; }
        public float MedianDepth { get; }
        public float Confidence { get; }
        public int MaskSampleCount { get; }
    }
}
