using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    public sealed class RetroReplacementManager : MonoBehaviour
    {
        private enum ReplacementState
        {
            Searching,
            Acquiring,
            Locked,
            TrackingMove,
            Lost
        }

        private sealed class TrackedReplacement
        {
            public string Label;
            public RetroReplacementRule Rule;
            public GameObject Instance;
            public Pose LastPose;
            public Pose PendingMovePose;
            public Vector3 LockedScale;
            public Quaternion LockedRotation;
            public Vector3 MoveVelocity;
            public Quaternion TargetRotation;
            public int ConfirmedFrames;
            public int PendingMoveFrames;
            public float LastSeenTime;
            public bool HasMoveTarget;
            public bool LastPoseFromAprilTag;
            public int AprilTagId = -1;
            public string AprilTagImageName;
            public int LastMatchedFrame = -1;
            public ReplacementState State = ReplacementState.Searching;
        }

        [SerializeField] private RetroPrefabLibrary prefabLibrary;
        [SerializeField] private ARRaycastPositionSolver positionSolver;
        [SerializeField] private AprilTagPoseSource aprilTagPoseSource;
        [SerializeField] private Transform contentRoot;
        [SerializeField] private Camera arCamera;
        [SerializeField] private bool preferAprilTagPose = true;
        [SerializeField] private bool destroyWhenLost;
        [SerializeField] private float lostGraceSeconds = 1f;
        [SerializeField, Min(0f)] private float lostStateDelaySeconds = 1f;
        [SerializeField, Min(0.05f)] private float reacquireMatchRadiusMeters = 0.4f;
        [SerializeField] private float duplicateRadiusMeters = 0.25f;
        [SerializeField, Min(0.005f)] private float movementDeadZoneMeters = 0.05f;
        [SerializeField, Min(1)] private int movementConfirmationFrames = 3;
        [SerializeField, Min(0.01f)] private float movementSmoothTime = 0.25f;

        private readonly List<TrackedReplacement> trackedReplacements = new List<TrackedReplacement>();

        private void Awake()
        {
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }
        }

        private void Reset()
        {
            positionSolver = FindObjectOfType<ARRaycastPositionSolver>();
            aprilTagPoseSource = FindObjectOfType<AprilTagPoseSource>();
            arCamera = Camera.main;
            contentRoot = transform;
        }

        private void Update()
        {
            UpdateAprilTagTargets();
            UpdateLostStates();
            SmoothActiveMoves();
            RemoveExpiredReplacements();
        }

        public void ApplyDetections(IReadOnlyList<DetectionResult> detections)
        {
            if (detections == null || prefabLibrary == null)
            {
                return;
            }

            float now = Time.time;
            for (int i = 0; i < detections.Count; i++)
            {
                DetectionResult detection = detections[i];
                if (!prefabLibrary.TryGetRule(detection.Label, out RetroReplacementRule rule))
                {
                    continue;
                }

                string key = rule.DetectionLabel.ToLowerInvariant();
                if (!detection.IsValid(rule.TrackingMinConfidence))
                {
                    continue;
                }

                bool poseFromAprilTag = TryGetAprilTagPose(
                    rule,
                    detection,
                    out Pose pose,
                    out int aprilTagId,
                    out string aprilTagImageName);
                if (!poseFromAprilTag
                    && !TryGetRaycastPose(rule, detection, out pose))
                {
                    continue;
                }

                TrackedReplacement tracked = FindBestTrack(key, pose, Time.frameCount);
                bool hasLockedInstance = tracked != null && tracked.Instance != null;
                float requiredConfidence = hasLockedInstance
                    ? rule.TrackingMinConfidence
                    : rule.MinConfidence;
                if (!detection.IsValid(requiredConfidence))
                {
                    continue;
                }

                if (tracked == null)
                {
                    tracked = new TrackedReplacement
                    {
                        Label = key,
                        Rule = rule
                    };
                    trackedReplacements.Add(tracked);
                }

                tracked.Rule = rule;
                tracked.LastMatchedFrame = Time.frameCount;
                UpdateReplacement(
                    rule,
                    detection,
                    pose,
                    poseFromAprilTag,
                    aprilTagId,
                    aprilTagImageName,
                    now,
                    tracked);
            }
        }

        private bool TryGetAprilTagPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose pose,
            out int tagId,
            out string imageName)
        {
            pose = default;
            tagId = -1;
            imageName = null;
            return preferAprilTagPose
                && aprilTagPoseSource != null
                && aprilTagPoseSource.TryGetObjectPose(
                    rule,
                    detection,
                    out pose,
                    out tagId,
                    out imageName);
        }

        private bool TryGetRaycastPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose pose)
        {
            pose = default;
            if (positionSolver == null
                || !positionSolver.TrySolvePose(detection, rule.RaycastAnchorInBoundingBox, out pose))
            {
                return false;
            }

            pose.position += Vector3.up * rule.VerticalOffsetMeters;
            pose.rotation *= rule.RotationOffset;
            return true;
        }

        private void UpdateReplacement(
            RetroReplacementRule rule,
            DetectionResult detection,
            Pose pose,
            bool poseFromAprilTag,
            int aprilTagId,
            string aprilTagImageName,
            float now,
            TrackedReplacement tracked)
        {
            if (!poseFromAprilTag
                && tracked.Instance != null
                && tracked.State == ReplacementState.Lost
                && !IsWithinReacquireRadius(tracked, pose))
            {
                return;
            }

            tracked.LastSeenTime = now;

            if (tracked.Instance == null)
            {
                tracked.State = ReplacementState.Acquiring;

                if (tracked.ConfirmedFrames > 0
                    && Vector3.Distance(tracked.LastPose.position, pose.position) > duplicateRadiusMeters)
                {
                    tracked.ConfirmedFrames = 0;
                }

                tracked.ConfirmedFrames++;
                tracked.LastPose = pose;
                tracked.TargetRotation = pose.rotation;
                tracked.LastPoseFromAprilTag = poseFromAprilTag;
                StoreAprilTagIdentity(tracked, poseFromAprilTag, aprilTagId, aprilTagImageName);

                if (tracked.ConfirmedFrames < rule.ConfirmationFrames)
                {
                    return;
                }

                tracked.Instance = Instantiate(rule.Prefab, pose.position, pose.rotation, contentRoot);
                tracked.Instance.transform.localScale = GetBaseScale(rule);
                tracked.LockedScale = EstimateTargetScale(tracked.Instance, rule, detection, pose);
                tracked.LockedRotation = pose.rotation;
                tracked.Instance.transform.localScale = tracked.LockedScale;
                ApplyPoseImmediately(tracked, pose, poseFromAprilTag);
                tracked.State = ReplacementState.Locked;
                return;
            }

            tracked.Instance.transform.localScale = tracked.LockedScale;
            if (poseFromAprilTag)
            {
                StoreAprilTagIdentity(tracked, true, aprilTagId, aprilTagImageName);
                TrackAprilTagPose(tracked, pose);
                return;
            }

            tracked.Instance.transform.rotation = tracked.LockedRotation;
            QueueMoveIfStable(tracked, pose);
        }

        private void UpdateAprilTagTargets()
        {
            if (!preferAprilTagPose || aprilTagPoseSource == null)
            {
                return;
            }

            float now = Time.time;
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (tracked.Instance == null
                    || tracked.Rule == null
                    || !TryGetStoredAprilTagPose(tracked, out Pose pose))
                {
                    continue;
                }

                tracked.LastSeenTime = now;
                TrackAprilTagPose(tracked, pose);
            }
        }

        private static void TrackAprilTagPose(
            TrackedReplacement tracked,
            Pose pose)
        {
            tracked.LastPose = pose;
            tracked.PendingMoveFrames = 0;
            tracked.HasMoveTarget = true;
            tracked.LastPoseFromAprilTag = true;
            tracked.TargetRotation = pose.rotation;
            tracked.State = ReplacementState.TrackingMove;
        }

        private bool TryGetStoredAprilTagPose(
            TrackedReplacement tracked,
            out Pose pose)
        {
            pose = default;
            if (tracked.AprilTagId >= 0
                || !string.IsNullOrWhiteSpace(tracked.AprilTagImageName))
            {
                return aprilTagPoseSource.TryGetObjectPose(
                    tracked.Rule,
                    tracked.AprilTagId,
                    tracked.AprilTagImageName,
                    out pose);
            }

            return aprilTagPoseSource.TryGetObjectPose(tracked.Rule, out pose);
        }

        private static void StoreAprilTagIdentity(
            TrackedReplacement tracked,
            bool poseFromAprilTag,
            int aprilTagId,
            string aprilTagImageName)
        {
            if (!poseFromAprilTag)
            {
                return;
            }

            tracked.AprilTagId = aprilTagId;
            tracked.AprilTagImageName = aprilTagImageName;
        }

        private void QueueMoveIfStable(TrackedReplacement tracked, Pose pose)
        {
            float distanceFromLockedPose = Vector3.Distance(tracked.LastPose.position, pose.position);
            if (distanceFromLockedPose < movementDeadZoneMeters)
            {
                tracked.PendingMoveFrames = 0;
                tracked.State = ReplacementState.Locked;
                tracked.LastPoseFromAprilTag = false;
                return;
            }

            if (tracked.PendingMoveFrames == 0
                || Vector3.Distance(tracked.PendingMovePose.position, pose.position) > movementDeadZoneMeters)
            {
                tracked.PendingMovePose = pose;
                tracked.PendingMoveFrames = 1;
                return;
            }

            tracked.PendingMovePose = pose;
            tracked.PendingMoveFrames++;
            if (tracked.PendingMoveFrames < movementConfirmationFrames)
            {
                return;
            }

            tracked.LastPose = pose;
            tracked.HasMoveTarget = true;
            tracked.LastPoseFromAprilTag = false;
            tracked.TargetRotation = tracked.LockedRotation;
            tracked.State = ReplacementState.TrackingMove;
            tracked.PendingMoveFrames = 0;
        }

        private void SmoothActiveMoves()
        {
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (tracked.Instance == null || !tracked.HasMoveTarget)
                {
                    continue;
                }

                Transform instanceTransform = tracked.Instance.transform;
                Vector3 targetPosition = tracked.LastPoseFromAprilTag
                    ? tracked.LastPose.position
                    : new Vector3(
                        tracked.LastPose.position.x,
                        instanceTransform.position.y,
                        tracked.LastPose.position.z);
                instanceTransform.position = Vector3.SmoothDamp(
                    instanceTransform.position,
                    targetPosition,
                    ref tracked.MoveVelocity,
                    movementSmoothTime);
                instanceTransform.localScale = tracked.LockedScale;
                instanceTransform.rotation = SmoothRotation(
                    instanceTransform.rotation,
                    tracked.LastPoseFromAprilTag ? tracked.TargetRotation : tracked.LockedRotation);

                if (!tracked.LastPoseFromAprilTag)
                {
                    AlignBottomToPlane(tracked.Instance, tracked.LastPose.position.y);
                }

                if (Vector3.Distance(instanceTransform.position, targetPosition) <= 0.005f
                    && Quaternion.Angle(
                        instanceTransform.rotation,
                        tracked.LastPoseFromAprilTag ? tracked.TargetRotation : tracked.LockedRotation) <= 0.5f)
                {
                    tracked.HasMoveTarget = false;
                    tracked.MoveVelocity = Vector3.zero;
                    tracked.State = ReplacementState.Locked;
                }
            }
        }

        private static Quaternion SmoothRotation(
            Quaternion current,
            Quaternion target)
        {
            float smoothTime = Mathf.Max(0.01f, Time.deltaTime * 12f);
            return Quaternion.Slerp(current, target, smoothTime);
        }

        private void UpdateLostStates()
        {
            if (lostStateDelaySeconds <= 0f)
            {
                return;
            }

            float now = Time.time;
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (tracked.State == ReplacementState.Searching
                    || tracked.State == ReplacementState.Lost)
                {
                    continue;
                }

                if (now - tracked.LastSeenTime <= lostStateDelaySeconds)
                {
                    continue;
                }

                if (tracked.Instance == null)
                {
                    tracked.State = ReplacementState.Searching;
                    tracked.ConfirmedFrames = 0;
                    tracked.PendingMoveFrames = 0;
                    continue;
                }

                tracked.State = ReplacementState.Lost;
                tracked.HasMoveTarget = false;
                tracked.PendingMoveFrames = 0;
                tracked.MoveVelocity = Vector3.zero;
            }
        }

        private bool IsWithinReacquireRadius(TrackedReplacement tracked, Pose pose)
        {
            if (reacquireMatchRadiusMeters <= 0f)
            {
                return true;
            }

            return Vector3.Distance(tracked.LastPose.position, pose.position) <= reacquireMatchRadiusMeters;
        }

        private TrackedReplacement FindBestTrack(
            string label,
            Pose pose,
            int frame)
        {
            TrackedReplacement best = null;
            float bestDistance = float.PositiveInfinity;

            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (!string.Equals(tracked.Label, label, System.StringComparison.OrdinalIgnoreCase)
                    || tracked.LastMatchedFrame == frame)
                {
                    continue;
                }

                float distance = Vector3.Distance(tracked.LastPose.position, pose.position);
                float matchRadius = tracked.Instance == null
                    ? duplicateRadiusMeters
                    : reacquireMatchRadiusMeters;

                if (matchRadius > 0f && distance > matchRadius)
                {
                    continue;
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = tracked;
                }
            }

            return best;
        }

        private Vector3 EstimateTargetScale(
            GameObject instance,
            RetroReplacementRule rule,
            DetectionResult detection,
            Pose pose)
        {
            if (!rule.EstimateScaleFromBoundingBox || arCamera == null)
            {
                return GetBaseScale(rule);
            }

            if (!TryGetRendererBounds(instance, out Bounds bounds)
                || (bounds.size.x <= 0.0001f && bounds.size.y <= 0.0001f))
            {
                return GetBaseScale(rule);
            }

            float distance = Vector3.Distance(arCamera.transform.position, pose.position);
            float visibleWorldHeight = 2f
                * distance
                * Mathf.Tan(arCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float visibleWorldWidth = visibleWorldHeight * arCamera.aspect;
            float targetHeight = detection.NormalizedBox.height
                * visibleWorldHeight
                * rule.EstimatedHeightMultiplier
                * rule.ScaleCalibrationMultiplier;
            float targetWidth = detection.NormalizedBox.width
                * visibleWorldWidth
                * rule.EstimatedWidthMultiplier
                * rule.ScaleCalibrationMultiplier;
            float scaleMultiplier = CalculateScaleMultiplier(rule, bounds, targetWidth, targetHeight);
            Vector2 range = rule.ScaleMultiplierRange;
            if (range.x <= 0f && range.y <= 0f)
            {
                range = new Vector2(0.25f, 4f);
            }
            scaleMultiplier = Mathf.Clamp(
                scaleMultiplier,
                Mathf.Min(range.x, range.y),
                Mathf.Max(range.x, range.y));
            return Vector3.Scale(instance.transform.localScale, Vector3.one * scaleMultiplier);
        }

        private static float CalculateScaleMultiplier(
            RetroReplacementRule rule,
            Bounds bounds,
            float targetWidth,
            float targetHeight)
        {
            float heightMultiplier = bounds.size.y > 0.0001f
                ? targetHeight / bounds.size.y
                : 0f;
            float widthMultiplier = bounds.size.x > 0.0001f
                ? targetWidth / bounds.size.x
                : 0f;

            switch (rule.BoundingBoxScaleAxis)
            {
                case RetroReplacementRule.ScaleBoundingBoxAxis.Width:
                    return widthMultiplier > 0f ? widthMultiplier : heightMultiplier;
                case RetroReplacementRule.ScaleBoundingBoxAxis.MaxDimension:
                    return Mathf.Max(widthMultiplier, heightMultiplier);
                default:
                    return heightMultiplier > 0f ? heightMultiplier : widthMultiplier;
            }
        }

        private static Vector3 GetBaseScale(RetroReplacementRule rule)
        {
            return Vector3.Scale(
                rule.SpawnScale,
                Vector3.one * rule.ScaleCalibrationMultiplier);
        }

        private static void ApplyPoseImmediately(
            TrackedReplacement tracked,
            Pose pose,
            bool poseFromAprilTag)
        {
            Transform instanceTransform = tracked.Instance.transform;
            instanceTransform.SetPositionAndRotation(pose.position, pose.rotation);
            tracked.TargetRotation = pose.rotation;
            tracked.LastPoseFromAprilTag = poseFromAprilTag;

            if (!poseFromAprilTag)
            {
                AlignBottomToPlane(tracked.Instance, pose.position.y);
            }
        }

        private static void AlignBottomToPlane(GameObject instance, float planeHeight)
        {
            if (!TryGetRendererBounds(instance, out Bounds bounds))
            {
                return;
            }

            Transform instanceTransform = instance.transform;
            Vector3 position = instanceTransform.position;
            position.y += planeHeight - bounds.min.y;
            instanceTransform.position = position;
        }

        private static bool TryGetRendererBounds(GameObject instance, out Bounds bounds)
        {
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                bounds = default;
                return false;
            }

            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return true;
        }

        private void RemoveExpiredReplacements()
        {
            if (lostGraceSeconds <= 0f)
            {
                return;
            }

            float now = Time.time;
            for (int i = trackedReplacements.Count - 1; i >= 0; i--)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (now - tracked.LastSeenTime <= lostGraceSeconds)
                {
                    continue;
                }

                if (tracked.Instance == null)
                {
                    trackedReplacements.RemoveAt(i);
                    continue;
                }

                if (!destroyWhenLost)
                {
                    continue;
                }

                Destroy(tracked.Instance);

                trackedReplacements.RemoveAt(i);
            }
        }
    }
}
