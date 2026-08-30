using System;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Visual configuration for one YOLO class. Runtime size comes from the
    /// mask-filtered depth point cloud, so no exact physical CAD is required.
    /// </summary>
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
        [SerializeField] private Vector3 rotationOffsetEuler;
        [SerializeField, Min(0.01f)] private float scaleCalibrationMultiplier = 1f;
        [SerializeField] private ScaleBoundingBoxAxis scaleBoundingBoxAxis =
            ScaleBoundingBoxAxis.Height;

        public string DetectionLabel => detectionLabel;
        public GameObject Prefab => prefab;
        public Vector3 SpawnScale => spawnScale;
        public float VerticalOffsetMeters => verticalOffsetMeters;
        public Quaternion RotationOffset => Quaternion.Euler(rotationOffsetEuler);
        public float ScaleCalibrationMultiplier => Mathf.Max(0.01f, scaleCalibrationMultiplier);
        public ScaleBoundingBoxAxis BoundingBoxScaleAxis => scaleBoundingBoxAxis;
    }
}
