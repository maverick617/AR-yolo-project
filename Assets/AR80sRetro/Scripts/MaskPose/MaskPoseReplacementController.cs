using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Places existing retro prefabs at category-level mask poses. The measured
    /// cloud size replaces the exact physical CAD bounds used by instance trackers.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class MaskPoseReplacementController : MonoBehaviour
    {
        private sealed class ReplacementState
        {
            public RetroReplacementRule Rule;
            public GameObject Instance;
            public float LastPoseTime;
            public float LastConfidence;
            public bool HasTarget;
            public Vector3 TargetPosition;
            public Quaternion TargetRotation;
            public Vector3 TargetScale;
        }

        [SerializeField] private CategoryPoseEstimator poseEstimator;
        [SerializeField] private RetroPrefabLibrary prefabLibrary;
        [SerializeField] private Transform replacementRoot;
        [SerializeField, Min(0.05f)] private float hideAfterSeconds = 0.45f;
        [SerializeField, Range(0f, 1f)] private float minimumPoseConfidence = 0.12f;
        [Tooltip("Bottle rotation is intentionally ignored; the virtual model stays world-upright.")]
        [SerializeField] private bool lockBottleRotation = true;

        [Header("Per-frame visual smoothing")]
        [Tooltip("How quickly the visible model follows filtered detector positions.")]
        [SerializeField, Range(1f, 40f)] private float positionFollowSharpness = 18f;
        [Tooltip("How quickly phone/cup/pen rotations follow the category pose.")]
        [SerializeField, Range(1f, 40f)] private float rotationFollowSharpness = 14f;
        [SerializeField, Range(1f, 30f)] private float scaleFollowSharpness = 8f;
        [SerializeField, Range(0.1f, 4f)] private float maximumVisualSpeed = 1.6f;
        [SerializeField, Range(30f, 720f)] private float maximumVisualRotationSpeed = 300f;

        [Header("Replacement physical sizes (meters)")]
        [Tooltip("A typical 550 ml water bottle: diameter, height, diameter.")]
        [SerializeField] private Vector3 bottlePhysicalSizeMeters =
            new Vector3(0.068f, 0.23f, 0.068f);
        [Tooltip("A representative smartphone: width, height, thickness.")]
        [SerializeField] private Vector3 phonePhysicalSizeMeters =
            new Vector3(0.072f, 0.15f, 0.008f);
        [SerializeField] private Vector3 cupPhysicalSizeMeters =
            new Vector3(0.12f, 0.105f, 0.085f);
        [SerializeField] private Vector3 penPhysicalSizeMeters =
            new Vector3(0.012f, 0.145f, 0.012f);
        [SerializeField] private bool showStatusOverlay = true;

        private readonly Dictionary<string, ReplacementState> states =
            new Dictionary<string, ReplacementState>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> failedLabels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string status = "MASK POSE: waiting for YOLO-seg + LiDAR";

        private void Reset()
        {
            poseEstimator = FindObjectOfType<CategoryPoseEstimator>();
        }

        private void OnEnable()
        {
            if (poseEstimator != null)
            {
                poseEstimator.PoseReady += HandlePose;
            }
        }

        private void OnDisable()
        {
            if (poseEstimator != null)
            {
                poseEstimator.PoseReady -= HandlePose;
            }
        }

        private void Update()
        {
            float now = Time.unscaledTime;
            foreach (KeyValuePair<string, ReplacementState> pair in states)
            {
                ReplacementState state = pair.Value;
                if (state.Instance != null
                    && state.Instance.activeSelf
                    && now - state.LastPoseTime > hideAfterSeconds)
                {
                    state.Instance.SetActive(false);
                    continue;
                }

                if (state.Instance != null && state.Instance.activeSelf)
                {
                    SmoothVisualTowardsTarget(state, Time.unscaledDeltaTime);
                }
            }
        }

        private void HandlePose(CategoryPoseEstimate estimate)
        {
            if (estimate.Confidence < minimumPoseConfidence
                || prefabLibrary == null
                || !prefabLibrary.TryGetRule(estimate.Label, out RetroReplacementRule rule))
            {
                return;
            }

            if (!states.TryGetValue(estimate.Label, out ReplacementState state))
            {
                if (failedLabels.Contains(estimate.Label))
                {
                    return;
                }

                try
                {
                    state = CreateState(estimate.Label, rule);
                }
                catch (Exception exception)
                {
                    failedLabels.Add(estimate.Label);
                    status = $"MASK POSE: failed to create {estimate.Label} replacement";
                    Debug.LogException(exception, this);
                    return;
                }

                if (state == null)
                {
                    failedLabels.Add(estimate.Label);
                    status = $"MASK POSE: no replacement available for {estimate.Label}";
                    return;
                }

                states.Add(estimate.Label, state);
            }

            Transform visual = state.Instance.transform;
            bool snapToTarget = !state.HasTarget || !state.Instance.activeSelf;
            Vector3 previousPosition = visual.position;
            Quaternion previousRotation = visual.rotation;
            Vector3 previousScale = visual.localScale;
            Vector3 correctedCenter = estimate.Pose.position
                + Vector3.up * rule.VerticalOffsetMeters;
            Quaternion visualRotation = lockBottleRotation
                && string.Equals(estimate.Label, "bottle", StringComparison.OrdinalIgnoreCase)
                ? rule.RotationOffset
                : estimate.Pose.rotation * rule.RotationOffset;
            visual.SetPositionAndRotation(correctedCenter, visualRotation);
            visual.localScale = Vector3.Scale(
                rule.SpawnScale,
                Vector3.one * rule.ScaleCalibrationMultiplier);
            Vector3 replacementPhysicalSize = ResolveReplacementPhysicalSize(
                estimate.Label,
                estimate.Size);
            if (string.Equals(estimate.Label, "bottle", StringComparison.OrdinalIgnoreCase))
            {
                FitToMeasuredDimensions(visual, replacementPhysicalSize);
            }
            else if (string.Equals(estimate.Label, "phone", StringComparison.OrdinalIgnoreCase)
                || string.Equals(estimate.Label, "cup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(estimate.Label, "pen", StringComparison.OrdinalIgnoreCase))
            {
                FitToMeasuredDimensions(visual, replacementPhysicalSize);
            }
            else
            {
                FitToMeasuredSize(visual, estimate.Size, rule.BoundingBoxScaleAxis);
            }
            CenterVisualOnPose(visual, correctedCenter);
            state.TargetPosition = visual.position;
            state.TargetRotation = visual.rotation;
            state.TargetScale = visual.localScale;
            state.HasTarget = true;

            // Pose events arrive at detector frequency. Preserve the current
            // rendered transform and let Update interpolate every display frame;
            // otherwise the model visibly steps whenever a new YOLO result lands.
            if (!snapToTarget)
            {
                visual.SetPositionAndRotation(previousPosition, previousRotation);
                visual.localScale = previousScale;
            }

            state.LastPoseTime = Time.unscaledTime;
            state.LastConfidence = estimate.Confidence;
            if (!state.Instance.activeSelf)
            {
                state.Instance.SetActive(true);
            }

            status = $"MASK POSE: {estimate.Label} confidence={estimate.Confidence:F2} "
                + $"model={replacementPhysicalSize.x:F3}x{replacementPhysicalSize.y:F3}"
                + $"x{replacementPhysicalSize.z:F3}m";
        }

        private Vector3 ResolveReplacementPhysicalSize(string label, Vector3 fallback)
        {
            if (string.Equals(label, "bottle", StringComparison.OrdinalIgnoreCase))
            {
                return SanitizePhysicalSize(bottlePhysicalSizeMeters, fallback);
            }

            if (string.Equals(label, "phone", StringComparison.OrdinalIgnoreCase))
            {
                return SanitizePhysicalSize(phonePhysicalSizeMeters, fallback);
            }

            if (string.Equals(label, "cup", StringComparison.OrdinalIgnoreCase))
            {
                return SanitizePhysicalSize(cupPhysicalSizeMeters, fallback);
            }

            if (string.Equals(label, "pen", StringComparison.OrdinalIgnoreCase))
            {
                return SanitizePhysicalSize(penPhysicalSizeMeters, fallback);
            }

            return fallback;
        }

        private static Vector3 SanitizePhysicalSize(Vector3 configured, Vector3 fallback)
        {
            return new Vector3(
                configured.x > 0.001f ? configured.x : fallback.x,
                configured.y > 0.001f ? configured.y : fallback.y,
                configured.z > 0.001f ? configured.z : fallback.z);
        }

        private ReplacementState CreateState(string label, RetroReplacementRule rule)
        {
            Transform parent = replacementRoot != null ? replacementRoot : transform;
            for (int childIndex = parent.childCount - 1; childIndex >= 0; childIndex--)
            {
                Transform child = parent.GetChild(childIndex);
                if (child != null
                    && string.Equals(
                        child.name,
                        $"Retro {label} (Mask Pose)",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Destroy(child.gameObject);
                }
            }

            // Never silently create a second procedural visual. Every supported
            // category must have exactly one explicit prefab in the library.
            GameObject instance = rule.Prefab != null
                ? Instantiate(rule.Prefab, parent, false)
                : null;
            if (instance == null)
            {
                return null;
            }

            instance.name = $"Retro {label} (Mask Pose)";
            instance.SetActive(false);
            return new ReplacementState
            {
                Rule = rule,
                Instance = instance,
                LastPoseTime = float.NegativeInfinity
            };
        }

        private void SmoothVisualTowardsTarget(ReplacementState state, float deltaTime)
        {
            if (!state.HasTarget || deltaTime <= 0f)
            {
                return;
            }

            Transform visual = state.Instance.transform;
            float positionAlpha = ExponentialAlpha(positionFollowSharpness, deltaTime);
            Vector3 interpolatedPosition = Vector3.Lerp(
                visual.position,
                state.TargetPosition,
                positionAlpha);
            visual.position = Vector3.MoveTowards(
                visual.position,
                interpolatedPosition,
                maximumVisualSpeed * deltaTime);

            float rotationAlpha = ExponentialAlpha(rotationFollowSharpness, deltaTime);
            Quaternion interpolatedRotation = Quaternion.Slerp(
                visual.rotation,
                state.TargetRotation,
                rotationAlpha);
            visual.rotation = Quaternion.RotateTowards(
                visual.rotation,
                interpolatedRotation,
                maximumVisualRotationSpeed * deltaTime);

            float scaleAlpha = ExponentialAlpha(scaleFollowSharpness, deltaTime);
            visual.localScale = Vector3.Lerp(
                visual.localScale,
                state.TargetScale,
                scaleAlpha);
        }

        private static float ExponentialAlpha(float sharpness, float deltaTime)
        {
            return 1f - Mathf.Exp(-Mathf.Max(0.01f, sharpness) * deltaTime);
        }

        private static void FitToMeasuredSize(
            Transform visual,
            Vector3 measuredSize,
            RetroReplacementRule.ScaleBoundingBoxAxis axis)
        {
            if (!TryComputeRendererBounds(visual, out Bounds bounds))
            {
                return;
            }

            float target = SelectDimension(measuredSize, axis);
            float current = SelectDimension(bounds.size, axis);
            if (target > 0.001f && current > 0.0001f)
            {
                visual.localScale *= Mathf.Clamp(target / current, 0.0001f, 100f);
            }
        }

        private static void FitToMeasuredDimensions(Transform visual, Vector3 measuredSize)
        {
            if (!TryComputeLocalRendererBounds(visual, out Bounds bounds)
                || bounds.size.x <= 0.0001f
                || bounds.size.y <= 0.0001f
                || bounds.size.z <= 0.0001f)
            {
                return;
            }

            visual.localScale = Vector3.Scale(
                visual.localScale,
                new Vector3(
                    Mathf.Clamp(measuredSize.x / bounds.size.x, 0.0001f, 100f),
                    Mathf.Clamp(measuredSize.y / bounds.size.y, 0.0001f, 100f),
                    Mathf.Clamp(measuredSize.z / bounds.size.z, 0.0001f, 100f)));
        }

        private static bool TryComputeLocalRendererBounds(
            Transform root,
            out Bounds bounds)
        {
            bounds = default;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool found = false;
            Matrix4x4 worldToRoot = root.worldToLocalMatrix;
            for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                Renderer renderer = renderers[rendererIndex];
                if (renderer == null)
                {
                    continue;
                }

                Bounds local = renderer.localBounds;
                Vector3 center = local.center;
                Vector3 extents = local.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 rendererLocalPoint = center + Vector3.Scale(
                        extents,
                        new Vector3(
                            (corner & 1) == 0 ? -1f : 1f,
                            (corner & 2) == 0 ? -1f : 1f,
                            (corner & 4) == 0 ? -1f : 1f));
                    Vector3 rootLocalPoint = worldToRoot.MultiplyPoint3x4(
                        renderer.transform.TransformPoint(rendererLocalPoint));
                    if (!found)
                    {
                        bounds = new Bounds(rootLocalPoint, Vector3.zero);
                        found = true;
                    }
                    else
                    {
                        bounds.Encapsulate(rootLocalPoint);
                    }
                }
            }

            return found;
        }

        private static void CenterVisualOnPose(Transform visual, Vector3 center)
        {
            if (TryComputeRendererBounds(visual, out Bounds bounds))
            {
                visual.position += center - bounds.center;
            }
        }

        private static float SelectDimension(
            Vector3 size,
            RetroReplacementRule.ScaleBoundingBoxAxis axis)
        {
            switch (axis)
            {
                case RetroReplacementRule.ScaleBoundingBoxAxis.Width:
                    return size.x;
                case RetroReplacementRule.ScaleBoundingBoxAxis.MaxDimension:
                    return Mathf.Max(size.x, Mathf.Max(size.y, size.z));
                default:
                    return size.y;
            }
        }

        private static bool TryComputeRendererBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            bool found = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            return found;
        }

        private void OnGUI()
        {
            if (!showStatusOverlay)
            {
                return;
            }

            GUIStyle style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.Clamp(Screen.width / 38, 18, 34),
                normal = { textColor = Color.white }
            };
            GUI.Box(
                new Rect(16f, Screen.height - 88f, Screen.width - 32f, 60f),
                status,
                style);
        }
    }
}
