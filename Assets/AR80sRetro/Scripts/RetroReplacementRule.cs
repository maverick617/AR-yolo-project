using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    [Serializable]
    public sealed class AdditionalAprilTagMount
    {
        [SerializeField] private int tagId = -1;
        [SerializeField] private string trackedImageName;
        [Tooltip("Rotation from this tag frame to the common cup frame, before the shared model rotation offset. Follow the documented image-top direction for this mount.")]
        [SerializeField] private Vector3 tagToObjectRotationEuler;
        [Tooltip("Enable for a non-standard mount such as a tag attached to the cup bottom.")]
        [SerializeField] private bool overrideCupCenterOffset;
        [Tooltip("Direction from this tag toward the cup center, expressed in tag-local axes. Keijiro's canonical front-facing Tag uses (0,0,+1), into the printed Tag.")]
        [SerializeField] private Vector3 tagToCupCenterDirection = Vector3.forward;
        [Tooltip("Use half the measured cup height instead of half the measured body diameter. Enable this for a bottom tag.")]
        [SerializeField] private bool useCupHeightForCenterDistance;
        [SerializeField, Min(0f)] private float tagMountStandoffMeters;

        public int TagId => tagId;
        public string TrackedImageName => trackedImageName;
        public Quaternion TagToObjectRotation =>
            Quaternion.Euler(tagToObjectRotationEuler);
        public bool OverrideCupCenterOffset => overrideCupCenterOffset;
        public Vector3 TagToCupCenterDirection =>
            tagToCupCenterDirection.sqrMagnitude > 0.0001f
                ? tagToCupCenterDirection.normalized
                : Vector3.forward;
        public bool UseCupHeightForCenterDistance => useCupHeightForCenterDistance;
        public float TagMountStandoffMeters => Mathf.Max(0f, tagMountStandoffMeters);

        public bool Matches(int observedTagId, string observedImageName)
        {
            bool hasTagId = tagId >= 0;
            bool hasImageName = !string.IsNullOrWhiteSpace(trackedImageName);
            if (!hasTagId && !hasImageName)
            {
                return false;
            }

            return (hasTagId && observedTagId == tagId)
                || (hasImageName
                    && string.Equals(
                        trackedImageName,
                        observedImageName,
                        StringComparison.OrdinalIgnoreCase));
        }
    }

    [Serializable]
    public sealed class RetroReplacementRule
    {
        public enum ScaleBoundingBoxAxis
        {
            Height,
            Width,
            MaxDimension
        }

        [SerializeField] private string detectionLabel = "cup";
        [SerializeField] private GameObject prefab;
        [SerializeField] private Vector3 spawnScale = Vector3.one;
        [SerializeField] private float verticalOffsetMeters;
        [SerializeField] private Vector2 raycastAnchorInBoundingBox = new Vector2(0.5f, 0.9f);
        [SerializeField] private Vector3 rotationOffsetEuler;
        [SerializeField, Min(0.01f)] private float scaleCalibrationMultiplier = 1f;
        [SerializeField] private float minConfidence = 0.45f;
        [SerializeField] private float trackingMinConfidence = 0.3f;
        [SerializeField] private int confirmationFrames = 1;
        [SerializeField, Range(0.01f, 1f)] private float positionSmoothing = 0.18f;
        [SerializeField] private bool estimateScaleFromBoundingBox = true;
        [SerializeField] private ScaleBoundingBoxAxis scaleBoundingBoxAxis = ScaleBoundingBoxAxis.Height;
        [SerializeField, Min(0.1f)] private float estimatedHeightMultiplier = 0.9f;
        [SerializeField, Min(0.1f)] private float estimatedWidthMultiplier = 0.9f;
        [SerializeField] private Vector2 scaleMultiplierRange = new Vector2(0.25f, 4f);
        [SerializeField] private bool useAprilTagPose = true;
        [SerializeField] private int aprilTagId = -1;
        [SerializeField] private string aprilTagTrackedImageName;
        [SerializeField] private Vector3 aprilTagToObjectOffsetMeters;
        [SerializeField] private Vector3 aprilTagToObjectRotationEuler;
        [SerializeField, Min(0.05f)] private float aprilTagMaxPoseAgeSeconds = 0.35f;
        [Tooltip("Extra rigid tags on the same physical cup. Each ID has a known rotation to the common cup/handle frame.")]
        [SerializeField] private List<AdditionalAprilTagMount> additionalAprilTagMounts =
            new List<AdditionalAprilTagMount>();

        [Header("Cup sizing and rigid tag mount")]
        [Tooltip("Legacy single-tag profile collection. Multi-tag cup rules use passive same-view sizing instead.")]
        [SerializeField] private bool enableAutomaticCupRegistration = true;
        [SerializeField, Range(3, 30)] private int cupRegistrationSamples = 8;
        [SerializeField, Range(0f, 45f)] private float minimumRegistrationViewAngleDegrees = 4f;
        [SerializeField, Range(0f, 1f)] private float minimumRegistrationMeasurementConfidence = 0.25f;
        [Tooltip("Use configured rigid mounts to convert every side/bottom Tag into one common cup/handle frame.")]
        [SerializeField] private bool useStandardCupTagMount = true;
        [Tooltip("Direction from the visible front of the Tag toward the cup center in Tag-local axes. Keijiro's canonical Tag frame points +Z into the printed Tag, so an outward-facing mount uses (0,0,+1).")]
        [SerializeField] private Vector3 tagToCupCenterDirection = Vector3.forward;
        [SerializeField, Min(0f)] private float tagMountStandoffMeters;
        [SerializeField] private bool fitMeasuredWidthAndHeight = true;
        [SerializeField, Range(0.1f, 3f)] private float maximumNonUniformScaleRatio = 1.8f;
        [SerializeField, Range(0f, 3f)] private float tagLostVisualFallbackSeconds = 0.8f;

        public string DetectionLabel => detectionLabel;
        public GameObject Prefab => prefab;
        public Vector3 SpawnScale => spawnScale;
        public float VerticalOffsetMeters => verticalOffsetMeters;
        public Vector2 RaycastAnchorInBoundingBox => raycastAnchorInBoundingBox == Vector2.zero
            ? new Vector2(0.5f, 0.9f)
            : raycastAnchorInBoundingBox;
        public Quaternion RotationOffset => Quaternion.Euler(rotationOffsetEuler);
        public float ScaleCalibrationMultiplier => Mathf.Max(0.01f, scaleCalibrationMultiplier);
        public float MinConfidence => minConfidence;
        public float TrackingMinConfidence => Mathf.Clamp01(trackingMinConfidence);
        public int ConfirmationFrames => Mathf.Max(1, confirmationFrames);
        public float PositionSmoothing => positionSmoothing;
        public bool EstimateScaleFromBoundingBox => estimateScaleFromBoundingBox;
        public ScaleBoundingBoxAxis BoundingBoxScaleAxis => scaleBoundingBoxAxis;
        public float EstimatedHeightMultiplier => estimatedHeightMultiplier;
        public float EstimatedWidthMultiplier => estimatedWidthMultiplier;
        public Vector2 ScaleMultiplierRange => scaleMultiplierRange;
        public bool UseAprilTagPose => useAprilTagPose;
        public int AprilTagId => aprilTagId;
        public string AprilTagTrackedImageName => aprilTagTrackedImageName;
        public Vector3 AprilTagToObjectOffsetMeters => aprilTagToObjectOffsetMeters;
        public Quaternion AprilTagToObjectRotation => Quaternion.Euler(aprilTagToObjectRotationEuler);
        public float AprilTagMaxPoseAgeSeconds => Mathf.Max(0.05f, aprilTagMaxPoseAgeSeconds);
        public IReadOnlyList<AdditionalAprilTagMount> AdditionalAprilTagMounts =>
            additionalAprilTagMounts;
        public bool HasMultipleAprilTags => additionalAprilTagMounts != null
            && additionalAprilTagMounts.Count > 0;
        public string AprilTagIdSummary
        {
            get
            {
                string summary = aprilTagId >= 0
                    ? aprilTagId.ToString()
                    : aprilTagTrackedImageName;
                if (additionalAprilTagMounts == null)
                {
                    return summary;
                }

                for (int i = 0; i < additionalAprilTagMounts.Count; i++)
                {
                    AdditionalAprilTagMount mount = additionalAprilTagMounts[i];
                    if (mount == null)
                    {
                        continue;
                    }

                    string value = mount.TagId >= 0
                        ? mount.TagId.ToString()
                        : mount.TrackedImageName;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    summary = string.IsNullOrWhiteSpace(summary)
                        ? value
                        : summary + "/" + value;
                }

                return summary;
            }
        }
        public bool EnableAutomaticCupRegistration => enableAutomaticCupRegistration;
        public int CupRegistrationSamples => Mathf.Max(3, cupRegistrationSamples);
        public float MinimumRegistrationViewAngleDegrees => Mathf.Max(0f, minimumRegistrationViewAngleDegrees);
        public float MinimumRegistrationMeasurementConfidence =>
            Mathf.Clamp01(minimumRegistrationMeasurementConfidence);
        public bool UseStandardCupTagMount => useStandardCupTagMount;
        public Vector3 TagToCupCenterDirection => tagToCupCenterDirection.sqrMagnitude > 0.0001f
            ? tagToCupCenterDirection.normalized
            : Vector3.forward;
        public float TagMountStandoffMeters => Mathf.Max(0f, tagMountStandoffMeters);
        public bool FitMeasuredWidthAndHeight => fitMeasuredWidthAndHeight;
        public float MaximumNonUniformScaleRatio => Mathf.Max(1f, maximumNonUniformScaleRatio);
        public float TagLostVisualFallbackSeconds => Mathf.Max(0f, tagLostVisualFallbackSeconds);

        public bool IsAprilTagMatch(int observedTagId, string observedImageName)
        {
            if (additionalAprilTagMounts != null)
            {
                for (int i = 0; i < additionalAprilTagMounts.Count; i++)
                {
                    AdditionalAprilTagMount mount = additionalAprilTagMounts[i];
                    if (mount != null && mount.Matches(observedTagId, observedImageName))
                    {
                        return true;
                    }
                }
            }

            bool hasTagId = aprilTagId >= 0;
            bool hasImageName = !string.IsNullOrWhiteSpace(aprilTagTrackedImageName);
            if (!hasTagId && !hasImageName)
            {
                return true;
            }

            return (hasTagId && observedTagId == aprilTagId)
                || (hasImageName
                    && string.Equals(
                        aprilTagTrackedImageName,
                        observedImageName,
                        StringComparison.OrdinalIgnoreCase));
        }

        public Quaternion GetAprilTagToObjectRotation(
            int observedTagId,
            string observedImageName)
        {
            if (additionalAprilTagMounts != null)
            {
                for (int i = 0; i < additionalAprilTagMounts.Count; i++)
                {
                    AdditionalAprilTagMount mount = additionalAprilTagMounts[i];
                    if (mount != null && mount.Matches(observedTagId, observedImageName))
                    {
                        return mount.TagToObjectRotation * RotationOffset;
                    }
                }
            }

            return AprilTagToObjectRotation * RotationOffset;
        }

        public Vector3 GetAprilTagToCupCenterOffset(
            int observedTagId,
            string observedImageName,
            Vector3 measuredSizeMeters)
        {
            if (additionalAprilTagMounts != null)
            {
                for (int i = 0; i < additionalAprilTagMounts.Count; i++)
                {
                    AdditionalAprilTagMount mount = additionalAprilTagMounts[i];
                    if (mount == null
                        || !mount.Matches(observedTagId, observedImageName)
                        || !mount.OverrideCupCenterOffset)
                    {
                        continue;
                    }

                    float fullDimension = mount.UseCupHeightForCenterDistance
                        ? measuredSizeMeters.y
                        : measuredSizeMeters.z;
                    return mount.TagToCupCenterDirection
                        * (Mathf.Max(0f, fullDimension) * 0.5f
                            + mount.TagMountStandoffMeters);
                }
            }

            return TagToCupCenterDirection
                * (Mathf.Max(0f, measuredSizeMeters.z) * 0.5f
                    + TagMountStandoffMeters);
        }

        public bool IsAxialAprilTagMount(
            int observedTagId,
            string observedImageName)
        {
            if (additionalAprilTagMounts == null)
            {
                return false;
            }

            for (int i = 0; i < additionalAprilTagMounts.Count; i++)
            {
                AdditionalAprilTagMount mount = additionalAprilTagMounts[i];
                if (mount != null
                    && mount.Matches(observedTagId, observedImageName)
                    && mount.OverrideCupCenterOffset
                    && mount.UseCupHeightForCenterDistance)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
