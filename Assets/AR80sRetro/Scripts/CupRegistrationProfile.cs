using System;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Persistent metric registration between one AprilTag and one physical cup.
    /// The profile is created in caregiver setup and reused during normal use.
    /// </summary>
    [Serializable]
    public sealed class CupRegistrationProfile
    {
        // v2 stores the physical cup-body diameter in depthMeters. v3 invalidates
        // profiles captured with the old 5 cm tag calibration; the demo now uses
        // the physical 1 cm black-square edge length.
        public const int CurrentSchemaVersion = 3;

        [SerializeField] private int schemaVersion = CurrentSchemaVersion;
        [SerializeField] private int tagId = -1;
        [SerializeField] private string trackedImageName;
        [SerializeField] private string detectionLabel = "cup";
        [SerializeField] private Vector3 tagToCupPositionMeters;
        [SerializeField] private Quaternion tagToCupRotation = Quaternion.identity;
        [SerializeField] private float heightMeters;
        [SerializeField] private float widthMeters;
        [SerializeField] private float depthMeters;
        [SerializeField, Range(0f, 1f)] private float confidence;
        [SerializeField] private int sampleCount;
        [SerializeField] private bool measuredWithEnvironmentDepth;
        [SerializeField] private long updatedUtcTicks;

        public int SchemaVersion => schemaVersion;
        public int TagId => tagId;
        public string TrackedImageName => trackedImageName;
        public string DetectionLabel => detectionLabel;
        public Vector3 TagToCupPositionMeters => tagToCupPositionMeters;
        public Quaternion TagToCupRotation => tagToCupRotation;
        public float HeightMeters => heightMeters;
        public float WidthMeters => widthMeters;
        public float DepthMeters => depthMeters;
        public float BodyDiameterMeters => depthMeters;
        public float Confidence => confidence;
        public int SampleCount => sampleCount;
        public bool MeasuredWithEnvironmentDepth => measuredWithEnvironmentDepth;
        public long UpdatedUtcTicks => updatedUtcTicks;

        public bool IsValid => schemaVersion == CurrentSchemaVersion
            && (tagId >= 0 || !string.IsNullOrWhiteSpace(trackedImageName))
            && heightMeters > 0.01f
            && widthMeters > 0.01f
            && Quaternion.Dot(tagToCupRotation, tagToCupRotation) > 0.5f;

        public Pose BuildWorldPose(Pose tagWorldPose)
        {
            return new Pose(
                tagWorldPose.position + tagWorldPose.rotation * tagToCupPositionMeters,
                tagWorldPose.rotation * tagToCupRotation);
        }

        public Vector3 MeasuredSizeMeters => new Vector3(
            Mathf.Max(0.01f, widthMeters),
            Mathf.Max(0.01f, heightMeters),
            Mathf.Max(0.01f, depthMeters > 0.01f ? depthMeters : widthMeters));

        public void SetRegistration(
            int newTagId,
            string newTrackedImageName,
            string newDetectionLabel,
            Pose tagToCupPose,
            Vector3 measuredSizeMeters,
            float newConfidence,
            int newSampleCount,
            bool usedEnvironmentDepth)
        {
            schemaVersion = CurrentSchemaVersion;
            tagId = newTagId;
            trackedImageName = newTrackedImageName;
            detectionLabel = string.IsNullOrWhiteSpace(newDetectionLabel)
                ? "cup"
                : newDetectionLabel;
            tagToCupPositionMeters = tagToCupPose.position;
            tagToCupRotation = Normalize(tagToCupPose.rotation);
            widthMeters = Mathf.Max(0.01f, measuredSizeMeters.x);
            heightMeters = Mathf.Max(0.01f, measuredSizeMeters.y);
            depthMeters = Mathf.Max(0.01f, measuredSizeMeters.z);
            confidence = Mathf.Clamp01(newConfidence);
            sampleCount = Mathf.Max(1, newSampleCount);
            measuredWithEnvironmentDepth = usedEnvironmentDepth;
            updatedUtcTicks = DateTime.UtcNow.Ticks;
        }

        private static Quaternion Normalize(Quaternion rotation)
        {
            float magnitude = Mathf.Sqrt(
                rotation.x * rotation.x
                + rotation.y * rotation.y
                + rotation.z * rotation.z
                + rotation.w * rotation.w);
            if (magnitude <= 0.000001f)
            {
                return Quaternion.identity;
            }

            float inverse = 1f / magnitude;
            return new Quaternion(
                rotation.x * inverse,
                rotation.y * inverse,
                rotation.z * inverse,
                rotation.w * inverse);
        }
    }
}
