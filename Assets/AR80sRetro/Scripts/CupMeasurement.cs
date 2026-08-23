using UnityEngine;

namespace AR80sRetro
{
    public readonly struct CupMeasurement
    {
        public CupMeasurement(
            Vector3 centerWorld,
            Vector3 sizeMeters,
            float confidence,
            int sampleCount,
            bool usedEnvironmentDepth,
            float bodyDiameterMeters = 0f)
        {
            CenterWorld = centerWorld;
            SizeMeters = sizeMeters;
            Confidence = Mathf.Clamp01(confidence);
            SampleCount = Mathf.Max(1, sampleCount);
            UsedEnvironmentDepth = usedEnvironmentDepth;
            BodyDiameterMeters = bodyDiameterMeters > 0.01f
                ? bodyDiameterMeters
                : Mathf.Min(sizeMeters.x, sizeMeters.z);
        }

        public Vector3 CenterWorld { get; }
        public Vector3 SizeMeters { get; }
        public float Confidence { get; }
        public int SampleCount { get; }
        public bool UsedEnvironmentDepth { get; }
        public float BodyDiameterMeters { get; }

        public bool IsValid => SizeMeters.x > 0.01f
            && SizeMeters.y > 0.01f
            && SizeMeters.z > 0.01f;
    }
}
