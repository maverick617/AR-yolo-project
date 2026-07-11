using System;
using UnityEngine;

namespace AR80sRetro
{
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
        [SerializeField] private float minConfidence = 0.6f;
        [SerializeField] private float trackingMinConfidence = 0.35f;
        [SerializeField] private int confirmationFrames = 2;
        [SerializeField, Range(0.01f, 1f)] private float positionSmoothing = 0.18f;
        [SerializeField] private bool estimateScaleFromBoundingBox = true;
        [SerializeField] private ScaleBoundingBoxAxis scaleBoundingBoxAxis = ScaleBoundingBoxAxis.Height;
        [SerializeField, Min(0.1f)] private float estimatedHeightMultiplier = 0.9f;
        [SerializeField, Min(0.1f)] private float estimatedWidthMultiplier = 0.9f;
        [SerializeField] private Vector2 scaleMultiplierRange = new Vector2(0.25f, 4f);
        [SerializeField] private bool useAprilTagPose;
        [SerializeField] private int aprilTagId = -1;
        [SerializeField] private string aprilTagTrackedImageName;
        [SerializeField] private Vector3 aprilTagToObjectOffsetMeters;
        [SerializeField] private Vector3 aprilTagToObjectRotationEuler;
        [SerializeField, Min(0.05f)] private float aprilTagMaxPoseAgeSeconds = 0.35f;

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
    }
}
