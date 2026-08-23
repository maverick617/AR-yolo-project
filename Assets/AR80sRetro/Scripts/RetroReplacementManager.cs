using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Cup-first replacement manager. YOLO confirms the class, while the newest
    /// visible tag from a small rigid mount set supplies identity and 6DoF. The
    /// cup size is initialized from a few ordinary same-view detections, so no
    /// guided camera sweep is required.
    /// </summary>
    public sealed class RetroReplacementManager : MonoBehaviour
    {
        private enum ReplacementState
        {
            Acquiring,
            Registering,
            Tracking,
            VisualFallback,
            Lost
        }

        private sealed class TagAssociation
        {
            public Pose TagPose;
            public int TagId;
            public string ImageName;
            public float PoseAgeSeconds;
            public float ViewportDistance;
            public bool IsSelected;
        }

        private sealed class TrackedReplacement
        {
            public string Label;
            public RetroReplacementRule Rule;
            public GameObject Instance;
            public GameObject Visual;
            public CupModelFitter ModelFitter;
            public CupRegistrationProfile Profile;
            public CupRegistrationSession RegistrationSession;
            public readonly List<Vector3> InitialSizeSamples = new List<Vector3>(8);
            public readonly List<Vector2> InitialYoloBoxSamples = new List<Vector2>(8);
            public Vector3 MeasuredSizeMeters;
            public Vector2 MeasuredYoloBoxSize;
            public bool HasMeasuredSize;
            public Pose LastTagPose;
            public Pose TargetPose;
            public Vector3 MoveVelocity;
            public Vector3 DetectionToPoseOffsetWorld;
            public int ConfirmedFrames;
            public int AprilTagId = -1;
            public string AprilTagImageName;
            public float LastTagSeenTime = float.NegativeInfinity;
            public float LastDetectionSeenTime = float.NegativeInfinity;
            public int LastMatchedFrame = -1;
            public int LastMeasurementFrame = -1;
            public bool HasScreenSizeValidation;
            public bool HasTargetPose;
            public bool HasDetectionOffset;
            public ReplacementState State = ReplacementState.Acquiring;
        }

        [Header("Dependencies")]
        [SerializeField] private RetroPrefabLibrary prefabLibrary;
        [SerializeField] private ARRaycastPositionSolver positionSolver;
        [SerializeField] private AprilTagPoseSource aprilTagPoseSource;
        [SerializeField] private CupRegistrationProfileStore cupProfileStore;
        [SerializeField] private CupDimensionEstimator cupDimensionEstimator;
        [SerializeField] private Transform contentRoot;
        [SerializeField] private Camera arCamera;

        [Header("Tracking policy")]
        [SerializeField] private bool preferAprilTagPose = true;
        [SerializeField] private bool autoConfigureAprilTagTracking = true;
        [Tooltip("A cup is never spawned from a plane pose because a plane anchor cannot follow a lifted cup.")]
        [SerializeField] private bool requireAprilTagPoseForTaggedRules = true;
        [SerializeField] private bool cupDemoOnly = true;
        [SerializeField, Min(0.01f)] private float rotationFollowSpeed = 14f;
        [Tooltip("How long the active tag may stop updating before a fresher configured tag takes over.")]
        [SerializeField, Range(0.05f, 0.25f)] private float tagHandoverFreshnessSeconds = 0.12f;
        [SerializeField, Range(0.05f, 0.75f)] private float visualFallbackMaxViewportDistance = 0.24f;
        [SerializeField, Min(0f)] private float lostStateDelaySeconds = 1.2f;
        [SerializeField] private bool destroyWhenLost;
        [SerializeField, Min(0.1f)] private float destroyAfterLostSeconds = 5f;

        [Header("YOLO visual sizing")]
        [Tooltip("Fraction of the YOLO cup-box height occupied by the rendered retro cup. Width is not authoritative because the virtual handle may extend beyond a handle-less real cup.")]
        [SerializeField, Range(0.75f, 1.05f)] private float yoloScreenHeightFill = 0.96f;
        [Tooltip("Safety limit for each screen-space correction attempt. This is a ratio, not a fixed cup size.")]
        [SerializeField, Range(1f, 20f)] private float maximumYoloScreenScaleCorrection = 10f;

        [Header("Diagnostics")]
        [SerializeField] private bool logReplacementDiagnostics = true;
        [SerializeField, Min(0.25f)] private float diagnosticLogIntervalSeconds = 1f;
        [SerializeField] private bool showCupDemoStatus = true;

        private readonly List<TrackedReplacement> trackedReplacements =
            new List<TrackedReplacement>();
        private readonly Dictionary<string, float> nextDiagnosticLogTimes =
            new Dictionary<string, float>();
        private readonly Dictionary<string, int> bestDetectionByTag =
            new Dictionary<string, int>();
        private TagAssociation[] tagAssociations = Array.Empty<TagAssociation>();
        private string currentStatusMessage = "WAITING: show cup + AprilTag 0/1/2";

        public string CurrentStatusMessage => currentStatusMessage;

        private void Awake()
        {
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }

            if (contentRoot == null)
            {
                contentRoot = transform;
            }

            if (preferAprilTagPose && autoConfigureAprilTagTracking)
            {
                EnsureAprilTagTrackingComponents();
            }

            EnsureCupRegistrationComponents();
            ValidateConfiguration();
            SetStatus("WAITING: show cup + AprilTag 0/1/2");
        }

        private void Reset()
        {
            positionSolver = FindObjectOfType<ARRaycastPositionSolver>();
            aprilTagPoseSource = FindObjectOfType<AprilTagPoseSource>();
            cupProfileStore = FindObjectOfType<CupRegistrationProfileStore>();
            cupDimensionEstimator = FindObjectOfType<CupDimensionEstimator>();
            arCamera = Camera.main;
            contentRoot = transform;
        }

        private void Update()
        {
            UpdateTargetsFromStoredTags();
            SmoothTrackedObjects();
            UpdateLostStates();
        }

        public void ApplyDetections(IReadOnlyList<DetectionResult> detections)
        {
            if (detections == null || prefabLibrary == null)
            {
                return;
            }

            PrepareTagAssociations(detections);
            if (detections.Count == 0 && !HasVisibleReplacement())
            {
                SetStatus("WAITING: show cup + AprilTag 0/1/2");
            }

            float now = Time.time;
            for (int i = 0; i < detections.Count; i++)
            {
                DetectionResult detection = detections[i];
                if (cupDemoOnly
                    && !string.Equals(detection.Label, "cup", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!prefabLibrary.TryGetRule(detection.Label, out RetroReplacementRule rule))
                {
                    LogDiagnostic(
                        $"rule:{detection.Label}",
                        $"Replacement skipped: no prefab rule for YOLO label '{detection.Label}'.");
                    continue;
                }

                if (!detection.IsValid(rule.TrackingMinConfidence))
                {
                    continue;
                }

                TagAssociation association = i < tagAssociations.Length
                    ? tagAssociations[i]
                    : null;
                if (association != null && !association.IsSelected)
                {
                    // The same physical tag can be returned for several overlapping
                    // cup boxes. Only the closest box is allowed to consume it.
                    continue;
                }

                bool hasTag = association != null;
                Pose tagPose = hasTag ? association.TagPose : default;
                int tagId = hasTag ? association.TagId : -1;
                string imageName = hasTag ? association.ImageName : null;
                float poseAgeSeconds = hasTag
                    ? association.PoseAgeSeconds
                    : float.PositiveInfinity;

                if (!hasTag)
                {
                    if (TryApplyShortVisualFallback(rule, detection, now))
                    {
                        continue;
                    }

                    if (rule.UseAprilTagPose
                        && preferAprilTagPose
                        && requireAprilTagPoseForTaggedRules)
                    {
                        SetStatus($"CUP FOUND - WAITING FOR APRILTAG {rule.AprilTagIdSummary}");
                        LogDiagnostic(
                            $"tag:{rule.DetectionLabel}",
                            $"'{rule.DetectionLabel}' is visible, but no fresh configured AprilTag ({rule.AprilTagIdSummary}) can be bound. Keep at least one tag clear and verify the 1 cm black-square size.");
                        continue;
                    }

                    if (!TryGetRaycastPose(rule, detection, out Pose raycastPose))
                    {
                        continue;
                    }

                    ApplyUntaggedDetection(rule, detection, raycastPose, now);
                    continue;
                }

                bool directMultiTagCup = UsesDirectMultiTagCup(rule);
                TrackedReplacement tracked = directMultiTagCup
                    ? FindReusableCupTrack(rule)
                    : FindTrackByTag(tagId, imageName);
                if (directMultiTagCup
                    && tracked != null
                    && !IsSameTag(tracked, tagId, imageName)
                    && aprilTagPoseSource.TryGetTagPose(
                        rule,
                        tracked.AprilTagId,
                        tracked.AprilTagImageName,
                        out Pose activeTagPose,
                        out float activeTagAgeSeconds)
                    && activeTagAgeSeconds
                        <= Mathf.Max(0.05f, tagHandoverFreshnessSeconds))
                {
                    tagPose = activeTagPose;
                    tagId = tracked.AprilTagId;
                    imageName = tracked.AprilTagImageName;
                    poseAgeSeconds = activeTagAgeSeconds;
                }

                if (tracked != null && !IsSameTag(tracked, tagId, imageName))
                {
                    SwitchTrackedTag(tracked, rule, tagId, imageName);
                }

                if (tracked == null)
                {
                    tracked = new TrackedReplacement
                    {
                        Label = rule.DetectionLabel.ToLowerInvariant(),
                        Rule = rule,
                        AprilTagId = tagId,
                        AprilTagImageName = imageName
                    };
                    if (!directMultiTagCup)
                    {
                        cupProfileStore.TryGetProfile(tagId, imageName, out tracked.Profile);
                    }
                    trackedReplacements.Add(tracked);
                }

                tracked.Rule = rule;
                tracked.LastMatchedFrame = Time.frameCount;
                tracked.LastDetectionSeenTime = now;
                tracked.LastTagSeenTime = now - Mathf.Max(0f, poseAgeSeconds);
                tracked.LastTagPose = tagPose;

                Quaternion expectedRotation = tagPose.rotation
                    * GetTagToCupRotation(
                        rule,
                        tracked.Profile,
                        tagId,
                        imageName);
                CupMeasurement measurement = default;
                bool hasMeasurement = cupDimensionEstimator != null
                    && cupDimensionEstimator.TryMeasure(
                        detection,
                        tagPose,
                        expectedRotation,
                        out measurement);

                bool axialMount = rule.IsAxialAprilTagMount(tagId, imageName);
                if (hasMeasurement
                    && UsesDirectMultiTagCup(rule)
                    && (!axialMount || tracked.HasMeasuredSize))
                {
                    UpdateTrackedCupMeasurement(tracked, measurement, detection);
                }
                else if (rule.EnableAutomaticCupRegistration
                    && !HasCompletedProfile(tracked)
                    && hasMeasurement)
                {
                    UpdateAutomaticRegistration(
                        tracked,
                        tagPose,
                        measurement,
                        tagId,
                        imageName);
                }

                if (UsesDirectMultiTagCup(rule) && !tracked.HasMeasuredSize)
                {
                    if (axialMount)
                    {
                        SetStatus("SHOW A SIDE APRILTAG TO SIZE CUP");
                        LogDiagnostic(
                            "size:cup-axial-view",
                            "An axial AprilTag view cannot initialize YOLO cup height. Show a configured side Tag for five frames.");
                        continue;
                    }

                    if (!hasMeasurement)
                    {
                        LogDiagnostic(
                            "size:cup-unavailable",
                            "Cup and AprilTag are visible, but metric sizing is unavailable. Keep the full cup box clear, use 15-30 cm distance, and verify camera focus/depth support.");
                    }
                    else if (measurement.Confidence
                        < rule.MinimumRegistrationMeasurementConfidence)
                    {
                        LogDiagnostic(
                            "size:cup-confidence",
                            $"Cup size sample rejected: confidence={measurement.Confidence:F2}, required={rule.MinimumRegistrationMeasurementConfidence:F2}.");
                    }

                    SetStatus(
                        $"AUTO SIZING CUP: {tracked.InitialSizeSamples.Count}/"
                        + $"{rule.CupRegistrationSamples} - HOLD STILL");
                    continue;
                }

                if (!UsesDirectMultiTagCup(rule)
                    && rule.EnableAutomaticCupRegistration
                    && rule.UseStandardCupTagMount
                    && tracked.Profile == null)
                {
                    SetStatus("AUTO CALIBRATION: HOLD CUP AND ONE TAG CLEAR");
                    continue;
                }

                Pose objectPose = BuildTrackedObjectPose(
                    rule,
                    tracked,
                    tagPose,
                    tagId,
                    imageName);
                tracked.TargetPose = objectPose;
                tracked.HasTargetPose = true;
                SetTrackedVisualActive(tracked, true);
                tracked.State = IsTrackingReady(tracked)
                        ? ReplacementState.Tracking
                        : ReplacementState.Registering;

                if (hasMeasurement)
                {
                    tracked.DetectionToPoseOffsetWorld = objectPose.position
                        - measurement.CenterWorld;
                    tracked.HasDetectionOffset = true;
                }

                bool hasInstance = tracked.Instance != null;
                float requiredConfidence = hasInstance
                    ? rule.TrackingMinConfidence
                    : rule.MinConfidence;
                if (!detection.IsValid(requiredConfidence))
                {
                    continue;
                }

                if (!hasInstance)
                {
                    tracked.ConfirmedFrames++;
                    if (tracked.ConfirmedFrames < rule.ConfirmationFrames)
                    {
                        continue;
                    }

                    CreateReplacement(
                        tracked,
                        measurement,
                        hasMeasurement,
                        detection);
                }
                else if (tracked.ModelFitter != null
                    && TryGetTrackedMeasuredSize(tracked, out Vector3 fittedSize))
                {
                    tracked.ModelFitter.Fit(
                        fittedSize,
                        rule.FitMeasuredWidthAndHeight,
                        rule.MaximumNonUniformScaleRatio);
                    if (!tracked.HasScreenSizeValidation)
                    {
                        TryApplyScreenSizeGuard(tracked, detection);
                    }
                }
            }
        }

        [ContextMenu("Clear Saved Cup Registrations")]
        public void ClearSavedCupRegistrations()
        {
            EnsureCupRegistrationComponents();
            cupProfileStore?.ClearAllProfiles();
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (tracked.Instance != null)
                {
                    Destroy(tracked.Instance);
                }

                tracked.Instance = null;
                tracked.Visual = null;
                tracked.ModelFitter = null;
                tracked.Profile = null;
                tracked.RegistrationSession = null;
                tracked.InitialSizeSamples.Clear();
                tracked.InitialYoloBoxSamples.Clear();
                tracked.MeasuredSizeMeters = Vector3.zero;
                tracked.MeasuredYoloBoxSize = Vector2.zero;
                tracked.HasMeasuredSize = false;
                tracked.HasTargetPose = false;
                tracked.HasDetectionOffset = false;
                tracked.ConfirmedFrames = 0;
                tracked.LastMeasurementFrame = -1;
                tracked.HasScreenSizeValidation = false;
                tracked.MoveVelocity = Vector3.zero;
                tracked.State = ReplacementState.Acquiring;
            }

            SetStatus("WAITING: show cup + AprilTag 0/1/2");
        }

        private void UpdateAutomaticRegistration(
            TrackedReplacement tracked,
            Pose tagPose,
            CupMeasurement measurement,
            int tagId,
            string imageName)
        {
            if (arCamera == null)
            {
                LogDiagnostic(
                    "register:no-camera",
                    "Cup registration is waiting for a valid AR Camera.");
                return;
            }

            if (tracked.RegistrationSession == null)
            {
                tracked.RegistrationSession = new CupRegistrationSession(
                    tagId,
                    imageName,
                    tracked.Rule);
            }

            if (!tracked.RegistrationSession.TryAddSample(tagPose, measurement, arCamera))
            {
                return;
            }

            CupRegistrationProfile candidate = tracked.RegistrationSession.BuildProfile();
            if (candidate == null)
            {
                return;
            }

            tracked.Profile = candidate;
            int samples = tracked.RegistrationSession.SampleCount;
            SetStatus($"AUTO CALIBRATING CUP: {samples}/{tracked.Rule.CupRegistrationSamples} - HOLD STILL");
            LogDiagnostic(
                $"register:{tagId}:{samples}",
                $"Cup auto calibration: tag={tagId}, frames={samples}/{tracked.Rule.CupRegistrationSamples}, size={candidate.MeasuredSizeMeters}. No camera sweep is required.");

            if (tracked.RegistrationSession.IsComplete)
            {
                cupProfileStore.SaveProfile(candidate);
                tracked.RegistrationSession = null;
                SetStatus(GetTrackingStatus(tracked));
            }
        }

        private void CreateReplacement(
            TrackedReplacement tracked,
            CupMeasurement measurement,
            bool hasMeasurement,
            DetectionResult detection)
        {
            RetroReplacementRule rule = tracked.Rule;
            GameObject wrapper = new GameObject(
                $"Retro {rule.DetectionLabel} (AprilTag {tracked.AprilTagId})");
            wrapper.transform.SetParent(contentRoot, false);
            wrapper.transform.SetPositionAndRotation(
                tracked.TargetPose.position,
                tracked.TargetPose.rotation);

            GameObject visual = Instantiate(rule.Prefab, wrapper.transform, false);
            visual.name = $"{rule.Prefab.name} Visual";
            CupModelFitter fitter = wrapper.AddComponent<CupModelFitter>();

            Vector3 measuredSize;
            if (!TryGetTrackedMeasuredSize(tracked, out measuredSize))
            {
                measuredSize = hasMeasurement
                    ? measurement.SizeMeters
                    : Vector3.zero;
            }
            bool fitted = measuredSize.x > 0.01f
                && fitter.InitializeAndFit(
                    visual,
                    measuredSize,
                    rule.FitMeasuredWidthAndHeight,
                    rule.MaximumNonUniformScaleRatio);
            if (!fitted)
            {
                // Never multiply an imported prefab's existing scale by the old
                // authoring scale here. That fallback could turn a 100x FBX root
                // into a 1000x visual when fitting failed.
                visual.transform.localScale = GetBaseScale(rule);
            }

            tracked.Instance = wrapper;
            tracked.Visual = visual;
            tracked.ModelFitter = fitter;
            if (fitted)
            {
                TryApplyScreenSizeGuard(tracked, detection);
                measuredSize = tracked.MeasuredSizeMeters;
            }
            tracked.MoveVelocity = Vector3.zero;
            tracked.State = IsTrackingReady(tracked)
                ? ReplacementState.Tracking
                : ReplacementState.Registering;
            SetStatus(IsTrackingReady(tracked)
                ? GetTrackingStatus(tracked)
                : $"AUTO SIZING CUP: {tracked.Profile?.SampleCount ?? 0}/{rule.CupRegistrationSamples}");

            Debug.Log(
                $"Retro replacement created: label={rule.DetectionLabel}, "
                + $"AprilTag={tracked.AprilTagId}, directMultiTag={UsesDirectMultiTagCup(rule)}, "
                + $"size={(measuredSize.x > 0f ? measuredSize.ToString() : "fallback")}",
                this);
        }

        private bool TryApplyShortVisualFallback(
            RetroReplacementRule rule,
            DetectionResult detection,
            float now)
        {
            TrackedReplacement tracked = FindBestTrackForScreenDetection(rule, detection);
            if (tracked == null
                || tracked.Instance == null
                || now - tracked.LastTagSeenTime > rule.TagLostVisualFallbackSeconds
                || cupDimensionEstimator == null)
            {
                return false;
            }

            if (!cupDimensionEstimator.TryMeasure(
                detection,
                tracked.LastTagPose,
                tracked.TargetPose.rotation,
                out CupMeasurement measurement))
            {
                return false;
            }

            if (UsesDirectMultiTagCup(rule))
            {
                UpdateTrackedCupMeasurement(tracked, measurement, detection);
                if (tracked.ModelFitter != null
                    && TryGetTrackedMeasuredSize(tracked, out Vector3 fittedSize))
                {
                    tracked.ModelFitter.Fit(
                        fittedSize,
                        rule.FitMeasuredWidthAndHeight,
                        rule.MaximumNonUniformScaleRatio);
                }
            }

            Vector3 targetPosition = measurement.CenterWorld;
            if (tracked.HasDetectionOffset)
            {
                targetPosition += tracked.DetectionToPoseOffsetWorld;
            }

            tracked.TargetPose = new Pose(targetPosition, tracked.TargetPose.rotation);
            tracked.HasTargetPose = true;
            tracked.LastDetectionSeenTime = now;
            tracked.State = ReplacementState.VisualFallback;
            SetTrackedVisualActive(tracked, true);
            SetStatus("APRILTAG HIDDEN - SHORT POSITION FALLBACK");
            return true;
        }

        private void ApplyUntaggedDetection(
            RetroReplacementRule rule,
            DetectionResult detection,
            Pose pose,
            float now)
        {
            TrackedReplacement tracked = FindBestTrackForScreenDetection(rule, detection);
            if (tracked == null)
            {
                tracked = new TrackedReplacement
                {
                    Label = rule.DetectionLabel.ToLowerInvariant(),
                    Rule = rule,
                    TargetPose = pose,
                    HasTargetPose = true,
                    LastDetectionSeenTime = now
                };
                trackedReplacements.Add(tracked);
            }

            tracked.TargetPose = pose;
            tracked.HasTargetPose = true;
            tracked.LastDetectionSeenTime = now;
            if (tracked.Instance == null && detection.IsValid(rule.MinConfidence))
            {
                CreateReplacement(tracked, default, false, detection);
            }
        }

        private bool TryApplyScreenSizeGuard(
            TrackedReplacement tracked,
            DetectionResult detection)
        {
            if (tracked == null
                || tracked.Instance == null
                || tracked.Visual == null
                || tracked.ModelFitter == null
                || tracked.Rule == null
                || arCamera == null
                || !tracked.HasMeasuredSize
                || !detection.IsValid(tracked.Rule.TrackingMinConfidence))
            {
                return false;
            }

            Rect target = Rect.MinMaxRect(
                detection.NormalizedBox.xMin,
                1f - detection.NormalizedBox.yMax,
                detection.NormalizedBox.xMax,
                1f - detection.NormalizedBox.yMin);
            // A clipped real cup box cannot tell us the full projected size.
            if (target.xMin <= 0.005f
                || target.yMin <= 0.005f
                || target.xMax >= 0.995f
                || target.yMax >= 0.995f)
            {
                return false;
            }

            float totalCorrection = 1f;
            float detectedHeight = tracked.MeasuredYoloBoxSize.y > 0.001f
                ? tracked.MeasuredYoloBoxSize.y
                : target.height;
            float targetHeight = detectedHeight * Mathf.Clamp(
                yoloScreenHeightFill,
                0.75f,
                1.05f);
            float maximumCorrection = Mathf.Max(
                1f,
                maximumYoloScreenScaleCorrection);
            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (!TryGetProjectedVisualRect(tracked.Visual, out Rect projected)
                    || projected.width <= 0.001f
                    || projected.height <= 0.001f)
                {
                    return false;
                }

                // YOLO height is the stable cup-size signal. Do not use the
                // smaller of width/height: the retro handle legitimately makes
                // the virtual renderer wider than a handle-less real cup.
                float heightCorrection = targetHeight / projected.height;
                float correction = Mathf.Clamp(
                    heightCorrection,
                    1f / maximumCorrection,
                    maximumCorrection);
                if (Mathf.Abs(1f - correction) < 0.025f)
                {
                    break;
                }

                totalCorrection *= correction;
                Vector3 correctedSize = tracked.MeasuredSizeMeters * correction;
                if (correctedSize.x <= 0.01f
                    || correctedSize.y <= 0.01f
                    || correctedSize.z <= 0.01f
                    || correctedSize.x > 1f
                    || correctedSize.y > 1f
                    || correctedSize.z > 1f)
                {
                    return false;
                }

                tracked.MeasuredSizeMeters = correctedSize;
                if (!tracked.ModelFitter.Fit(
                    correctedSize,
                    tracked.Rule.FitMeasuredWidthAndHeight,
                    tracked.Rule.MaximumNonUniformScaleRatio))
                {
                    return false;
                }

                tracked.TargetPose = BuildTrackedObjectPose(
                    tracked.Rule,
                    tracked,
                    tracked.LastTagPose,
                    tracked.AprilTagId,
                    tracked.AprilTagImageName);
                tracked.Instance.transform.SetPositionAndRotation(
                    tracked.TargetPose.position,
                    tracked.TargetPose.rotation);
            }

            if (!TryGetProjectedVisualRect(tracked.Visual, out Rect finalProjection)
                || finalProjection.height <= 0.001f)
            {
                return false;
            }

            float finalRelativeError = Mathf.Abs(
                finalProjection.height - targetHeight) / targetHeight;
            tracked.HasScreenSizeValidation = finalRelativeError <= 0.04f;
            LogDiagnostic(
                "size:screen-guard",
                $"YOLO-authoritative cup height applied: correction={totalCorrection:F3}, "
                + $"targetHeight={targetHeight:F3}, projectedHeight={finalProjection.height:F3}, "
                + $"error={finalRelativeError:P1}, locked={tracked.HasScreenSizeValidation}, "
                + $"finalSize={tracked.MeasuredSizeMeters}.");
            return tracked.HasScreenSizeValidation;
        }

        private bool TryGetProjectedVisualRect(
            GameObject visual,
            out Rect projectedRect)
        {
            projectedRect = default;
            bool hasPoint = false;
            Vector2 minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 maximum = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

            MeshFilter[] filters = visual.GetComponentsInChildren<MeshFilter>();
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                MeshRenderer renderer = filter.GetComponent<MeshRenderer>();
                if (filter.sharedMesh == null
                    || renderer == null
                    || !renderer.enabled)
                {
                    continue;
                }

                EncapsulateProjectedLocalBounds(
                    filter.transform,
                    filter.sharedMesh.bounds,
                    ref minimum,
                    ref maximum,
                    ref hasPoint);
            }

            SkinnedMeshRenderer[] skinned =
                visual.GetComponentsInChildren<SkinnedMeshRenderer>();
            for (int i = 0; i < skinned.Length; i++)
            {
                if (!skinned[i].enabled)
                {
                    continue;
                }

                EncapsulateProjectedLocalBounds(
                    skinned[i].transform,
                    skinned[i].localBounds,
                    ref minimum,
                    ref maximum,
                    ref hasPoint);
            }

            if (!hasPoint)
            {
                return false;
            }

            projectedRect = Rect.MinMaxRect(
                minimum.x,
                minimum.y,
                maximum.x,
                maximum.y);
            return true;
        }

        private void EncapsulateProjectedLocalBounds(
            Transform source,
            Bounds localBounds,
            ref Vector2 minimum,
            ref Vector2 maximum,
            ref bool hasPoint)
        {
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 localPoint = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                Vector3 viewport = arCamera.WorldToViewportPoint(
                    source.TransformPoint(localPoint));
                if (viewport.z <= 0f)
                {
                    continue;
                }

                Vector2 point = new Vector2(viewport.x, viewport.y);
                minimum = Vector2.Min(minimum, point);
                maximum = Vector2.Max(maximum, point);
                hasPoint = true;
            }
        }

        private void UpdateTargetsFromStoredTags()
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
                    || tracked.AprilTagId < 0
                        && string.IsNullOrWhiteSpace(tracked.AprilTagImageName))
                {
                    continue;
                }

                Pose tagPose;
                int tagId = tracked.AprilTagId;
                string imageName = tracked.AprilTagImageName;
                float poseAgeSeconds;
                bool hasCurrentTag = aprilTagPoseSource.TryGetTagPose(
                    tracked.Rule,
                    tracked.AprilTagId,
                    tracked.AprilTagImageName,
                    out tagPose,
                    out poseAgeSeconds);
                bool currentTagIsFresh = hasCurrentTag
                    && poseAgeSeconds
                        <= Mathf.Max(0.05f, tagHandoverFreshnessSeconds);
                if (!currentTagIsFresh
                    && !aprilTagPoseSource.TryGetNewestTagPose(
                        tracked.Rule,
                        out tagPose,
                        out tagId,
                        out imageName,
                        out poseAgeSeconds))
                {
                    continue;
                }

                if (!IsSameTag(tracked, tagId, imageName))
                {
                    SwitchTrackedTag(
                        tracked,
                        tracked.Rule,
                        tagId,
                        imageName);
                }

                tracked.LastTagPose = tagPose;
                tracked.LastTagSeenTime = now - Mathf.Max(0f, poseAgeSeconds);
                if (UsesDirectMultiTagCup(tracked.Rule)
                    && !tracked.HasMeasuredSize)
                {
                    tracked.HasTargetPose = false;
                    tracked.State = ReplacementState.Registering;
                    SetTrackedVisualActive(tracked, false);
                    continue;
                }

                tracked.TargetPose = BuildTrackedObjectPose(
                    tracked.Rule,
                    tracked,
                    tagPose,
                    tracked.AprilTagId,
                    tracked.AprilTagImageName);
                tracked.HasTargetPose = true;
                SetTrackedVisualActive(tracked, true);
                tracked.State = IsTrackingReady(tracked)
                    ? ReplacementState.Tracking
                    : ReplacementState.Registering;
                if (tracked.State == ReplacementState.Tracking)
                {
                    SetStatus(GetTrackingStatus(tracked));
                }
            }
        }

        private void SmoothTrackedObjects()
        {
            float rotationBlend = 1f - Mathf.Exp(-rotationFollowSpeed * Time.deltaTime);
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (tracked.Instance == null || !tracked.HasTargetPose)
                {
                    continue;
                }

                Transform instanceTransform = tracked.Instance.transform;
                float smoothTime = tracked.Rule != null
                    ? Mathf.Max(0.01f, tracked.Rule.PositionSmoothing)
                    : 0.08f;
                instanceTransform.position = Vector3.SmoothDamp(
                    instanceTransform.position,
                    tracked.TargetPose.position,
                    ref tracked.MoveVelocity,
                    smoothTime);
                instanceTransform.rotation = Quaternion.Slerp(
                    instanceTransform.rotation,
                    tracked.TargetPose.rotation,
                    rotationBlend);
            }
        }

        private void UpdateLostStates()
        {
            float now = Time.time;
            for (int i = trackedReplacements.Count - 1; i >= 0; i--)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                float lastSeen = Mathf.Max(
                    tracked.LastTagSeenTime,
                    tracked.LastDetectionSeenTime);
                float age = now - lastSeen;
                if (age <= lostStateDelaySeconds)
                {
                    continue;
                }

                tracked.State = ReplacementState.Lost;
                tracked.HasTargetPose = false;
                tracked.MoveVelocity = Vector3.zero;
                SetTrackedVisualActive(tracked, false);
                SetStatus("CUP LOST - SHOW CUP + APRILTAG 0/1/2");
                if (!destroyWhenLost || age <= destroyAfterLostSeconds)
                {
                    continue;
                }

                if (tracked.Instance != null)
                {
                    Destroy(tracked.Instance);
                }

                trackedReplacements.RemoveAt(i);
            }
        }

        private bool TryGetRawAprilTagPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose tagPose,
            out int tagId,
            out string imageName,
            out float poseAgeSeconds)
        {
            tagPose = default;
            tagId = -1;
            imageName = null;
            poseAgeSeconds = float.PositiveInfinity;
            return preferAprilTagPose
                && aprilTagPoseSource != null
                && aprilTagPoseSource.TryGetTagPose(
                    rule,
                    detection,
                    out tagPose,
                    out tagId,
                    out imageName,
                    out poseAgeSeconds);
        }

        private void PrepareTagAssociations(IReadOnlyList<DetectionResult> detections)
        {
            if (tagAssociations.Length != detections.Count)
            {
                tagAssociations = new TagAssociation[detections.Count];
            }
            else
            {
                Array.Clear(tagAssociations, 0, tagAssociations.Length);
            }

            bestDetectionByTag.Clear();
            for (int i = 0; i < detections.Count; i++)
            {
                DetectionResult detection = detections[i];
                if (cupDemoOnly
                    && !string.Equals(detection.Label, "cup", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!prefabLibrary.TryGetRule(detection.Label, out RetroReplacementRule rule)
                    || !detection.IsValid(rule.TrackingMinConfidence)
                    || !TryGetRawAprilTagPose(
                        rule,
                        detection,
                        out Pose tagPose,
                        out int tagId,
                        out string imageName,
                        out float poseAgeSeconds))
                {
                    continue;
                }

                TagAssociation association = new TagAssociation
                {
                    TagPose = tagPose,
                    TagId = tagId,
                    ImageName = imageName,
                    PoseAgeSeconds = poseAgeSeconds,
                    ViewportDistance = GetTagToDetectionViewportDistance(tagPose, detection)
                };
                tagAssociations[i] = association;

                string key = GetTagKey(tagId, imageName);
                if (!bestDetectionByTag.TryGetValue(key, out int existingIndex)
                    || IsBetterTagAssociation(
                        association,
                        detection,
                        tagAssociations[existingIndex],
                        detections[existingIndex]))
                {
                    bestDetectionByTag[key] = i;
                }
            }

            foreach (KeyValuePair<string, int> pair in bestDetectionByTag)
            {
                TagAssociation association = tagAssociations[pair.Value];
                if (association != null)
                {
                    association.IsSelected = true;
                }
            }
        }

        private float GetTagToDetectionViewportDistance(
            Pose tagPose,
            DetectionResult detection)
        {
            if (arCamera == null)
            {
                return 0f;
            }

            Vector3 viewportPoint = arCamera.WorldToViewportPoint(tagPose.position);
            if (viewportPoint.z <= 0f)
            {
                return float.PositiveInfinity;
            }

            Vector2 detectionCenter = new Vector2(
                detection.NormalizedBox.center.x,
                1f - detection.NormalizedBox.center.y);
            return Vector2.Distance(
                new Vector2(viewportPoint.x, viewportPoint.y),
                detectionCenter);
        }

        private static bool IsBetterTagAssociation(
            TagAssociation candidate,
            DetectionResult candidateDetection,
            TagAssociation existing,
            DetectionResult existingDetection)
        {
            if (existing == null)
            {
                return true;
            }

            if (!Mathf.Approximately(candidate.ViewportDistance, existing.ViewportDistance))
            {
                return candidate.ViewportDistance < existing.ViewportDistance;
            }

            return candidateDetection.Confidence > existingDetection.Confidence;
        }

        private static string GetTagKey(int tagId, string imageName)
        {
            return tagId >= 0
                ? $"id:{tagId}"
                : $"image:{imageName?.Trim().ToLowerInvariant()}";
        }

        private bool TryGetRaycastPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose pose)
        {
            pose = default;
            if (positionSolver == null
                || !positionSolver.TrySolvePose(
                    detection,
                    rule.RaycastAnchorInBoundingBox,
                    out pose))
            {
                return false;
            }

            pose.position += Vector3.up * rule.VerticalOffsetMeters;
            pose.rotation *= rule.RotationOffset;
            return true;
        }

        private static Pose BuildTrackedObjectPose(
            RetroReplacementRule rule,
            TrackedReplacement tracked,
            Pose tagPose,
            int tagId,
            string imageName)
        {
            if (UsesDirectMultiTagCup(rule) && tracked.HasMeasuredSize)
            {
                Vector3 tagToCenter = rule.GetAprilTagToCupCenterOffset(
                    tagId,
                    imageName,
                    tracked.MeasuredSizeMeters);
                return new Pose(
                    tagPose.position
                        + tagPose.rotation * tagToCenter
                        + Vector3.up * rule.VerticalOffsetMeters,
                    tagPose.rotation
                        * rule.GetAprilTagToObjectRotation(tagId, imageName));
            }

            CupRegistrationProfile profile = tracked.Profile;
            if (profile != null && profile.IsValid)
            {
                Pose registeredPose = profile.BuildWorldPose(tagPose);
                registeredPose.position += Vector3.up * rule.VerticalOffsetMeters;
                return registeredPose;
            }

            return new Pose(
                tagPose.position
                    + tagPose.rotation * rule.AprilTagToObjectOffsetMeters
                    + Vector3.up * rule.VerticalOffsetMeters,
                tagPose.rotation
                    * rule.GetAprilTagToObjectRotation(tagId, imageName));
        }

        private static Quaternion GetTagToCupRotation(
            RetroReplacementRule rule,
            CupRegistrationProfile profile,
            int tagId,
            string imageName)
        {
            return !UsesDirectMultiTagCup(rule)
                && profile != null
                && profile.IsValid
                ? profile.TagToCupRotation
                : rule.GetAprilTagToObjectRotation(tagId, imageName);
        }

        private TrackedReplacement FindTrackByTag(int tagId, string imageName)
        {
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if ((tagId >= 0 && tracked.AprilTagId == tagId)
                    || (!string.IsNullOrWhiteSpace(imageName)
                        && string.Equals(
                            tracked.AprilTagImageName,
                            imageName,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return tracked;
                }
            }

            return null;
        }

        private TrackedReplacement FindReusableCupTrack(RetroReplacementRule rule)
        {
            TrackedReplacement best = null;
            float bestSeenTime = float.NegativeInfinity;
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                // A configured multi-tag set is one physical identity group. It
                // must reuse the same track even if two overlapping YOLO boxes
                // consume two different visible tags in the same inference.
                if (!string.Equals(
                        tracked.Label,
                        rule.DetectionLabel,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                float seenTime = Mathf.Max(
                    tracked.LastTagSeenTime,
                    tracked.LastDetectionSeenTime);
                if (best == null || seenTime > bestSeenTime)
                {
                    best = tracked;
                    bestSeenTime = seenTime;
                }
            }

            return best;
        }

        private void SwitchTrackedTag(
            TrackedReplacement tracked,
            RetroReplacementRule rule,
            int tagId,
            string imageName)
        {
            if (tracked == null || IsSameTag(tracked, tagId, imageName))
            {
                return;
            }

            tracked.Rule = rule;
            tracked.AprilTagId = tagId;
            tracked.AprilTagImageName = imageName;
            tracked.RegistrationSession = null;
            tracked.HasDetectionOffset = false;
            if (UsesDirectMultiTagCup(rule))
            {
                tracked.Profile = null;
            }
            else
            {
                cupProfileStore.TryGetProfile(tagId, imageName, out tracked.Profile);
            }

            if (tracked.Instance != null)
            {
                tracked.Instance.name =
                    $"Retro {rule.DetectionLabel} (AprilTag {tagId})";
            }

            LogDiagnostic(
                $"tag-switch:{tagId}",
                $"Cup tracking handed over to AprilTag {tagId} without creating a second model.");
        }

        private static bool IsSameTag(
            TrackedReplacement tracked,
            int tagId,
            string imageName)
        {
            return tracked != null
                && ((tagId >= 0 && tracked.AprilTagId == tagId)
                    || (!string.IsNullOrWhiteSpace(imageName)
                        && string.Equals(
                            tracked.AprilTagImageName,
                            imageName,
                            StringComparison.OrdinalIgnoreCase)));
        }

        private static bool UsesDirectMultiTagCup(RetroReplacementRule rule)
        {
            return rule != null
                && rule.HasMultipleAprilTags
                && rule.UseStandardCupTagMount;
        }

        private static void UpdateTrackedCupMeasurement(
            TrackedReplacement tracked,
            CupMeasurement measurement,
            DetectionResult detection)
        {
            if (tracked == null
                || tracked.Rule == null
                || !measurement.IsValid)
            {
                return;
            }

            // The first five same-view measurements define this physical cup for
            // the lifetime of the track. Locking the result prevents detection-box
            // changes around the handle from making the model breathe while the
            // cup rotates. Clear/reacquire to size a different cup.
            if (tracked.HasMeasuredSize)
            {
                return;
            }

            if (measurement.Confidence
                < tracked.Rule.MinimumRegistrationMeasurementConfidence)
            {
                return;
            }

            Rect box = detection.NormalizedBox;
            if (box.xMin <= 0.005f
                || box.yMin <= 0.005f
                || box.xMax >= 0.995f
                || box.yMax >= 0.995f)
            {
                return;
            }

            if (tracked.LastMeasurementFrame == Time.frameCount)
            {
                return;
            }

            tracked.LastMeasurementFrame = Time.frameCount;
            Vector3 sample = new Vector3(
                measurement.SizeMeters.x,
                measurement.SizeMeters.y,
                measurement.BodyDiameterMeters);

            tracked.InitialSizeSamples.Add(sample);
            tracked.InitialYoloBoxSamples.Add(new Vector2(box.width, box.height));
            tracked.MeasuredSizeMeters = MedianSize(
                tracked.InitialSizeSamples);
            tracked.MeasuredYoloBoxSize = MedianSize(
                tracked.InitialYoloBoxSamples);
            tracked.HasMeasuredSize = tracked.InitialSizeSamples.Count
                >= tracked.Rule.CupRegistrationSamples;
        }

        private static Vector2 MedianSize(List<Vector2> samples)
        {
            List<float> x = new List<float>(samples.Count);
            List<float> y = new List<float>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                x.Add(samples[i].x);
                y.Add(samples[i].y);
            }

            x.Sort();
            y.Sort();
            int middle = samples.Count / 2;
            if (samples.Count % 2 != 0)
            {
                return new Vector2(x[middle], y[middle]);
            }

            return new Vector2(
                (x[middle - 1] + x[middle]) * 0.5f,
                (y[middle - 1] + y[middle]) * 0.5f);
        }

        private static Vector3 MedianSize(List<Vector3> samples)
        {
            List<float> x = new List<float>(samples.Count);
            List<float> y = new List<float>(samples.Count);
            List<float> z = new List<float>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                x.Add(samples[i].x);
                y.Add(samples[i].y);
                z.Add(samples[i].z);
            }

            x.Sort();
            y.Sort();
            z.Sort();
            int middle = samples.Count / 2;
            if (samples.Count % 2 != 0)
            {
                return new Vector3(x[middle], y[middle], z[middle]);
            }

            return new Vector3(
                (x[middle - 1] + x[middle]) * 0.5f,
                (y[middle - 1] + y[middle]) * 0.5f,
                (z[middle - 1] + z[middle]) * 0.5f);
        }

        private static bool TryGetTrackedMeasuredSize(
            TrackedReplacement tracked,
            out Vector3 measuredSize)
        {
            if (tracked != null && tracked.HasMeasuredSize)
            {
                measuredSize = tracked.MeasuredSizeMeters;
                return measuredSize.x > 0.01f
                    && measuredSize.y > 0.01f
                    && measuredSize.z > 0.01f;
            }

            if (tracked?.Profile != null && tracked.Profile.IsValid)
            {
                measuredSize = tracked.Profile.MeasuredSizeMeters;
                return true;
            }

            measuredSize = Vector3.zero;
            return false;
        }

        private TrackedReplacement FindBestTrackForScreenDetection(
            RetroReplacementRule rule,
            DetectionResult detection)
        {
            TrackedReplacement best = null;
            float bestDistance = float.PositiveInfinity;
            Rect viewportRect = Rect.MinMaxRect(
                detection.NormalizedBox.xMin,
                1f - detection.NormalizedBox.yMax,
                detection.NormalizedBox.xMax,
                1f - detection.NormalizedBox.yMin);

            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                TrackedReplacement tracked = trackedReplacements[i];
                if (tracked.LastMatchedFrame == Time.frameCount
                    || !string.Equals(
                        tracked.Label,
                        rule.DetectionLabel,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                float distance = 0f;
                if (arCamera != null && tracked.HasTargetPose)
                {
                    Vector3 viewportPoint = arCamera.WorldToViewportPoint(
                        tracked.TargetPose.position);
                    if (viewportPoint.z <= 0f)
                    {
                        continue;
                    }

                    distance = Vector2.Distance(
                        new Vector2(viewportPoint.x, viewportPoint.y),
                        viewportRect.center);
                }

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = tracked;
                }
            }

            if (best != null)
            {
                if (arCamera != null && bestDistance > visualFallbackMaxViewportDistance)
                {
                    return null;
                }

                best.LastMatchedFrame = Time.frameCount;
            }

            return best;
        }

        private static bool HasCompletedProfile(TrackedReplacement tracked)
        {
            return tracked != null
                && tracked.Rule != null
                && tracked.Profile != null
                && tracked.Profile.IsValid
                && tracked.RegistrationSession == null
                && tracked.Profile.SampleCount >= tracked.Rule.CupRegistrationSamples;
        }

        private static bool IsTrackingReady(TrackedReplacement tracked)
        {
            return tracked != null
                && tracked.Rule != null
                && (UsesDirectMultiTagCup(tracked.Rule)
                    ? tracked.HasMeasuredSize
                    : !tracked.Rule.EnableAutomaticCupRegistration
                        || HasCompletedProfile(tracked));
        }

        private static string GetTrackingStatus(TrackedReplacement tracked)
        {
            if (TryGetTrackedMeasuredSize(tracked, out Vector3 size))
            {
                return $"TRACKING CUP (APRILTAG {tracked.AprilTagId})  "
                    + $"H={size.y * 100f:F1}cm D={size.z * 100f:F1}cm";
            }

            return $"TRACKING CUP (APRILTAG {tracked?.AprilTagId ?? -1})";
        }

        private static void SetTrackedVisualActive(
            TrackedReplacement tracked,
            bool active)
        {
            if (tracked?.Visual != null
                && tracked.Visual.activeSelf != active)
            {
                tracked.Visual.SetActive(active);
            }
        }

        private bool HasVisibleReplacement()
        {
            for (int i = 0; i < trackedReplacements.Count; i++)
            {
                if (trackedReplacements[i].Instance != null
                    && trackedReplacements[i].State != ReplacementState.Lost)
                {
                    return true;
                }
            }

            return false;
        }

        private void SetStatus(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                currentStatusMessage = message;
            }
        }

        private void OnGUI()
        {
            if (!showCupDemoStatus || string.IsNullOrWhiteSpace(currentStatusMessage))
            {
                return;
            }

            GUIStyle style = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.026f), 16, 30),
                wordWrap = true
            };
            style.normal.textColor = Color.white;
            float width = Mathf.Min(720f, Mathf.Max(240f, Screen.width - 32f));
            GUI.Box(new Rect(16f, 16f, width, 64f), currentStatusMessage, style);
        }

        private static Vector3 GetBaseScale(RetroReplacementRule rule)
        {
            return Vector3.Scale(
                rule.SpawnScale,
                Vector3.one * rule.ScaleCalibrationMultiplier);
        }

        private void EnsureCupRegistrationComponents()
        {
            if (cupProfileStore == null)
            {
                cupProfileStore = FindObjectOfType<CupRegistrationProfileStore>();
            }

            if (cupProfileStore == null)
            {
                cupProfileStore = gameObject.AddComponent<CupRegistrationProfileStore>();
            }

            if (cupDimensionEstimator == null)
            {
                cupDimensionEstimator = FindObjectOfType<CupDimensionEstimator>();
            }

            if (cupDimensionEstimator == null)
            {
                cupDimensionEstimator = gameObject.AddComponent<CupDimensionEstimator>();
            }
        }

        private void EnsureAprilTagTrackingComponents()
        {
            if (aprilTagPoseSource == null)
            {
                aprilTagPoseSource = FindObjectOfType<AprilTagPoseSource>();
            }

            if (aprilTagPoseSource == null)
            {
                aprilTagPoseSource = gameObject.AddComponent<AprilTagPoseSource>();
            }

            if (FindObjectOfType<KeijiroAprilTagFrameDetector>() == null)
            {
                gameObject.AddComponent<KeijiroAprilTagFrameDetector>();
            }
        }

        private void ValidateConfiguration()
        {
            if (prefabLibrary == null)
            {
                Debug.LogError("Retro replacement is disabled: Prefab Library is not assigned.", this);
            }

            if (preferAprilTagPose && aprilTagPoseSource == null)
            {
                Debug.LogError("AprilTag Pose Source is not assigned.", this);
            }
        }

        private void LogDiagnostic(string key, string message)
        {
            if (!logReplacementDiagnostics)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (nextDiagnosticLogTimes.TryGetValue(key, out float nextTime)
                && now < nextTime)
            {
                return;
            }

            nextDiagnosticLogTimes[key] = now + Mathf.Max(
                0.25f,
                diagnosticLogIntervalSeconds);
            Debug.LogWarning(message, this);
        }
    }
}
