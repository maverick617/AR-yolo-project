using UnityEngine;

namespace AR80sRetro
{
    public readonly struct CategoryPoseEstimate
    {
        public CategoryPoseEstimate(
            string label,
            Pose pose,
            Vector3 size,
            float confidence,
            DetectionResult detection)
        {
            Label = label;
            Pose = pose;
            Size = size;
            Confidence = confidence;
            Detection = detection;
        }

        public string Label { get; }
        public Pose Pose { get; }
        public Vector3 Size { get; }
        public float Confidence { get; }
        public DetectionResult Detection { get; }
    }
}
