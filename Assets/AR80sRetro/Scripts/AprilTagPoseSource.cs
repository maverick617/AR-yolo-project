using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace AR80sRetro
{
    /// <summary>
    /// Stores recent poses from a conventional AprilTag detector or an
    /// ARTrackedImageManager. A rule may map a small set of rigid tag IDs to one
    /// common object frame, while each observed tag is still associated with at
    /// most one YOLO detection.
    /// </summary>
    public sealed class AprilTagPoseSource : MonoBehaviour
    {
        [Serializable]
        public sealed class Binding
        {
            [SerializeField] private bool enabled = true;
            [SerializeField] private string detectionLabel = "cup";
            [SerializeField] private int tagId = -1;
            [SerializeField] private string trackedImageName;
            [SerializeField] private Vector3 tagToObjectOffsetMeters;
            [SerializeField] private Vector3 tagToObjectRotationEuler;
            [SerializeField, Min(0.05f)] private float maxPoseAgeSeconds = 0.35f;

            public bool Enabled => enabled;
            public string DetectionLabel => detectionLabel;
            public int TagId => tagId;
            public string TrackedImageName => trackedImageName;
            public Vector3 TagToObjectOffsetMeters => tagToObjectOffsetMeters;
            public Quaternion TagToObjectRotation => Quaternion.Euler(tagToObjectRotationEuler);
            public float MaxPoseAgeSeconds => Mathf.Max(0.05f, maxPoseAgeSeconds);
        }

        private sealed class Observation
        {
            public int TagId = -1;
            public string ImageName;
            public Pose TagPose;
            public bool HasPose;
            public Pose LastRawPose;
            public bool HasRawPose;
            public float LastSeenTime;
            public TrackableId TrackableId;
            public bool HasTrackableId;
        }

        [SerializeField] private Camera arCamera;
        [SerializeField] private ARTrackedImageManager trackedImageManager;
        [SerializeField] private List<Binding> bindings = new List<Binding>();
        [SerializeField, Min(0.05f)] private float defaultMaxPoseAgeSeconds = 0.6f;
        [SerializeField, Range(0f, 0.5f)] private float detectionBoxPadding = 0.18f;
        [SerializeField, Range(0.01f, 1f)] private float maxViewportCenterDistance = 0.55f;
        [Tooltip("If exactly one fresh tag can belong to the detected cup, allow it to bind even when RGB/display transforms make its projected point miss the YOLO box.")]
        [SerializeField] private bool allowSingleVisibleTagFallback = true;

        [Header("Pose stabilization")]
        [Tooltip("Filters each physical Tag independently before multi-Tag handover. This removes the high-frequency pose noise produced by a 1 cm marker.")]
        [SerializeField] private bool filterPublishedTagPoses = true;
        [SerializeField, Range(1f, 30f)] private float positionFilterSpeed = 7f;
        [SerializeField, Range(1f, 30f)] private float rotationFilterSpeed = 6f;
        [SerializeField, Min(0f)] private float positionDeadbandMeters = 0.0015f;
        [SerializeField, Range(0f, 10f)] private float rotationDeadbandDegrees = 0.8f;
        [Tooltip("A one-frame jump larger than this is treated as a bad planar-pose solution.")]
        [SerializeField, Min(0.02f)] private float maximumSingleFramePositionJumpMeters = 0.12f;
        [SerializeField, Range(10f, 180f)] private float maximumSingleFrameRotationJumpDegrees = 55f;
        [SerializeField, Min(0.1f)] private float filterResetGapSeconds = 0.45f;
        [SerializeField] private bool logPoseUpdates;

        [Header("Runtime evaluation")]
        [Tooltip("Logs frame-to-frame raw and filtered pose motion. Interpret these values as jitter only while the physical cup and camera are stationary.")]
        [SerializeField] private bool logRuntimeEvaluation = true;
        [SerializeField, Min(5f)] private float evaluationWindowSeconds = 20f;

        private readonly List<Observation> observations = new List<Observation>();
        private readonly Dictionary<int, Pose> evaluationLastRawPoses =
            new Dictionary<int, Pose>();
        private readonly Dictionary<int, Pose> evaluationLastFilteredPoses =
            new Dictionary<int, Pose>();
        private readonly List<float> evaluationRawPositionStepsMm =
            new List<float>(512);
        private readonly List<float> evaluationFilteredPositionStepsMm =
            new List<float>(512);
        private readonly List<float> evaluationRawRotationStepsDegrees =
            new List<float>(512);
        private readonly List<float> evaluationFilteredRotationStepsDegrees =
            new List<float>(512);
        private double evaluationWindowStartSeconds;
        private int evaluationAcceptedPoseCount;
        private int evaluationRejectedPoseCount;

        public int FreshObservationCount
        {
            get
            {
                int count = 0;
                float now = Time.time;
                for (int i = 0; i < observations.Count; i++)
                {
                    if (now - observations[i].LastSeenTime <= defaultMaxPoseAgeSeconds)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private void Awake()
        {
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }

            if (trackedImageManager == null)
            {
                trackedImageManager = FindObjectOfType<ARTrackedImageManager>();
            }
        }

        private void Reset()
        {
            arCamera = Camera.main;
            trackedImageManager = FindObjectOfType<ARTrackedImageManager>();
        }

        private void OnEnable()
        {
            ResetEvaluationWindow();
            if (trackedImageManager != null)
            {
                trackedImageManager.trackedImagesChanged += HandleTrackedImagesChanged;
            }
        }

        private void OnDisable()
        {
            LogEvaluationWindow(true);
            if (trackedImageManager != null)
            {
                trackedImageManager.trackedImagesChanged -= HandleTrackedImagesChanged;
            }
        }

        public void PublishTagPose(int tagId, Pose tagPose)
        {
            PublishTagPose(tagId, null, tagPose);
        }

        public void PublishTagPose(int tagId, string imageName, Pose tagPose)
        {
            Observation observation = FindOrCreateObservation(tagId, imageName);
            float now = Time.time;
            if (!TryUpdateFilteredPose(observation, tagPose, now))
            {
                RecordRejectedEvaluationPose();
                return;
            }

            observation.TagId = tagId;
            observation.ImageName = imageName;
            observation.LastSeenTime = now;
            observation.HasTrackableId = false;
            RecordAcceptedEvaluationPose(tagId, tagPose, observation.TagPose);

            if (logPoseUpdates)
            {
                Debug.Log($"AprilTag pose: id={tagId}, image={imageName}, position={observation.TagPose.position}", this);
            }
        }

        private bool TryUpdateFilteredPose(
            Observation observation,
            Pose rawPose,
            float now)
        {
            float gapSeconds = now - observation.LastSeenTime;
            if (!filterPublishedTagPoses
                || !observation.HasPose
                || gapSeconds > Mathf.Max(0.1f, filterResetGapSeconds))
            {
                observation.TagPose = rawPose;
                observation.HasPose = true;
                observation.LastRawPose = rawPose;
                observation.HasRawPose = true;
                return true;
            }

            float positionJump = Vector3.Distance(
                observation.HasRawPose
                    ? observation.LastRawPose.position
                    : observation.TagPose.position,
                rawPose.position);
            float rotationJump = Quaternion.Angle(
                observation.HasRawPose
                    ? observation.LastRawPose.rotation
                    : observation.TagPose.rotation,
                rawPose.rotation);
            if (positionJump > Mathf.Max(0.02f, maximumSingleFramePositionJumpMeters)
                || rotationJump > Mathf.Clamp(
                    maximumSingleFrameRotationJumpDegrees,
                    10f,
                    180f))
            {
                // Do not refresh LastSeenTime for an outlier. A persistent new
                // pose will be accepted as a fresh acquisition after resetGap.
                return false;
            }

            observation.LastRawPose = rawPose;
            observation.HasRawPose = true;

            float deltaTime = Mathf.Clamp(gapSeconds, 0.001f, 0.25f);
            float filteredPositionError = Vector3.Distance(
                observation.TagPose.position,
                rawPose.position);
            Vector3 filteredPosition = observation.TagPose.position;
            if (filteredPositionError > Mathf.Max(0f, positionDeadbandMeters))
            {
                float positionBlend = 1f - Mathf.Exp(
                    -Mathf.Max(1f, positionFilterSpeed) * deltaTime);
                filteredPosition = Vector3.Lerp(
                    filteredPosition,
                    rawPose.position,
                    positionBlend);
            }

            Quaternion filteredRotation = observation.TagPose.rotation;
            float filteredRotationError = Quaternion.Angle(
                filteredRotation,
                rawPose.rotation);
            if (filteredRotationError > Mathf.Max(0f, rotationDeadbandDegrees))
            {
                float rotationBlend = 1f - Mathf.Exp(
                    -Mathf.Max(1f, rotationFilterSpeed) * deltaTime);
                filteredRotation = Quaternion.Slerp(
                    filteredRotation,
                    rawPose.rotation,
                    rotationBlend);
            }

            observation.TagPose = new Pose(filteredPosition, filteredRotation);
            return true;
        }

        /// <summary>
        /// Returns the raw tag pose associated with a YOLO detection. Registration
        /// code should use this API so the learned Tag-to-cup transform is applied
        /// exactly once.
        /// </summary>
        public bool TryGetTagPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose tagPose,
            out int tagId,
            out string imageName)
        {
            return TryGetTagPose(
                rule,
                detection,
                out tagPose,
                out tagId,
                out imageName,
                out _);
        }

        public bool TryGetTagPose(
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

            if (rule == null || !rule.UseAprilTagPose)
            {
                return false;
            }

            Rect viewportRect = ToViewportRect(detection.NormalizedBox);
            Rect paddedViewportRect = ExpandRect(viewportRect, detectionBoxPadding);
            Vector2 detectionCenter = viewportRect.center;
            float now = Time.time;

            Observation bestObservation = null;
            float bestScore = float.PositiveInfinity;
            Observation onlyFreshMatch = null;
            int freshMatchCount = 0;

            for (int i = 0; i < observations.Count; i++)
            {
                Observation observation = observations[i];
                if (!TryResolveBinding(rule, observation, out Binding binding))
                {
                    continue;
                }

                float maxAge = GetMaxPoseAge(rule, binding);
                if (now - observation.LastSeenTime > maxAge)
                {
                    continue;
                }

                freshMatchCount++;
                onlyFreshMatch = observation;

                if (TryScoreObservation(
                    observation,
                    paddedViewportRect,
                    detectionCenter,
                    out float score)
                    && score < bestScore)
                {
                    bestScore = score;
                    bestObservation = observation;
                }
            }

            if (bestObservation == null
                && allowSingleVisibleTagFallback
                && freshMatchCount == 1)
            {
                bestObservation = onlyFreshMatch;
            }

            if (bestObservation == null)
            {
                return false;
            }

            tagPose = bestObservation.TagPose;
            tagId = bestObservation.TagId;
            imageName = bestObservation.ImageName;
            poseAgeSeconds = Mathf.Max(0f, now - bestObservation.LastSeenTime);
            return true;
        }

        public bool TryGetTagPose(
            RetroReplacementRule rule,
            int requiredTagId,
            string requiredImageName,
            out Pose tagPose,
            out float poseAgeSeconds)
        {
            tagPose = default;
            poseAgeSeconds = float.PositiveInfinity;

            if (rule == null || !rule.UseAprilTagPose)
            {
                return false;
            }

            float now = Time.time;
            Observation newest = null;
            float newestTime = float.NegativeInfinity;

            for (int i = 0; i < observations.Count; i++)
            {
                Observation observation = observations[i];
                if (!IsTagMatch(requiredTagId, requiredImageName, observation)
                    || !TryResolveBinding(rule, observation, out Binding binding))
                {
                    continue;
                }

                float maxAge = GetMaxPoseAge(rule, binding);
                if (now - observation.LastSeenTime > maxAge
                    || observation.LastSeenTime <= newestTime)
                {
                    continue;
                }

                newest = observation;
                newestTime = observation.LastSeenTime;
            }

            if (newest == null)
            {
                return false;
            }

            tagPose = newest.TagPose;
            poseAgeSeconds = Mathf.Max(0f, now - newest.LastSeenTime);
            return true;
        }

        /// <summary>
        /// Returns the newest fresh observation across every tag mount configured
        /// for the same rule. This lets an existing cup track hand over from one
        /// face tag to another without waiting for the next YOLO inference.
        /// </summary>
        public bool TryGetNewestTagPose(
            RetroReplacementRule rule,
            out Pose tagPose,
            out int tagId,
            out string imageName,
            out float poseAgeSeconds)
        {
            tagPose = default;
            tagId = -1;
            imageName = null;
            poseAgeSeconds = float.PositiveInfinity;
            if (!TryGetNewestMatchingObservation(
                rule,
                out Observation observation,
                out _))
            {
                return false;
            }

            tagPose = observation.TagPose;
            tagId = observation.TagId;
            imageName = observation.ImageName;
            poseAgeSeconds = Mathf.Max(0f, Time.time - observation.LastSeenTime);
            return true;
        }

        // Compatibility APIs for non-cup rules and existing native callers.
        public bool TryGetObjectPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose objectPose)
        {
            return TryGetObjectPose(rule, detection, out objectPose, out _, out _);
        }

        public bool TryGetObjectPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose objectPose,
            out int tagId,
            out string imageName)
        {
            objectPose = default;
            if (!TryGetTagPose(rule, detection, out Pose tagPose, out tagId, out imageName))
            {
                return false;
            }

            TryResolveBinding(rule, FindObservation(tagId, imageName), out Binding binding);
            objectPose = BuildObjectPose(
                rule,
                binding,
                tagPose,
                tagId,
                imageName);
            return true;
        }

        public bool TryGetObjectPose(RetroReplacementRule rule, out Pose objectPose)
        {
            objectPose = default;
            if (!TryGetNewestMatchingObservation(rule, out Observation observation, out Binding binding))
            {
                return false;
            }

            objectPose = BuildObjectPose(
                rule,
                binding,
                observation.TagPose,
                observation.TagId,
                observation.ImageName);
            return true;
        }

        public bool TryGetObjectPose(
            RetroReplacementRule rule,
            int requiredTagId,
            string requiredImageName,
            out Pose objectPose)
        {
            objectPose = default;
            if (!TryGetTagPose(
                rule,
                requiredTagId,
                requiredImageName,
                out Pose tagPose,
                out _))
            {
                return false;
            }

            Observation observation = FindObservation(requiredTagId, requiredImageName);
            TryResolveBinding(rule, observation, out Binding binding);
            objectPose = BuildObjectPose(
                rule,
                binding,
                tagPose,
                requiredTagId,
                requiredImageName);
            return true;
        }

        private bool TryGetNewestMatchingObservation(
            RetroReplacementRule rule,
            out Observation newest,
            out Binding newestBinding)
        {
            newest = null;
            newestBinding = null;
            if (rule == null || !rule.UseAprilTagPose)
            {
                return false;
            }

            float newestTime = float.NegativeInfinity;
            float now = Time.time;
            for (int i = 0; i < observations.Count; i++)
            {
                Observation observation = observations[i];
                if (!TryResolveBinding(rule, observation, out Binding binding)
                    || now - observation.LastSeenTime > GetMaxPoseAge(rule, binding)
                    || observation.LastSeenTime <= newestTime)
                {
                    continue;
                }

                newest = observation;
                newestBinding = binding;
                newestTime = observation.LastSeenTime;
            }

            return newest != null;
        }

        private float GetMaxPoseAge(RetroReplacementRule rule, Binding binding)
        {
            return binding != null
                ? binding.MaxPoseAgeSeconds
                : rule.AprilTagMaxPoseAgeSeconds;
        }

        private Observation FindObservation(int tagId, string imageName)
        {
            for (int i = 0; i < observations.Count; i++)
            {
                if (IsTagMatch(tagId, imageName, observations[i]))
                {
                    return observations[i];
                }
            }

            return null;
        }

        private void HandleTrackedImagesChanged(ARTrackedImagesChangedEventArgs eventArgs)
        {
            for (int i = 0; i < eventArgs.added.Count; i++)
            {
                UpdateTrackedImage(eventArgs.added[i]);
            }

            for (int i = 0; i < eventArgs.updated.Count; i++)
            {
                UpdateTrackedImage(eventArgs.updated[i]);
            }

            for (int i = 0; i < eventArgs.removed.Count; i++)
            {
                RemoveTrackedImage(eventArgs.removed[i]);
            }
        }

        private void UpdateTrackedImage(ARTrackedImage trackedImage)
        {
            if (trackedImage == null || trackedImage.trackingState != TrackingState.Tracking)
            {
                return;
            }

            string imageName = trackedImage.referenceImage.name;
            int tagId = ParseTagId(imageName);
            Observation observation = FindOrCreateObservation(tagId, imageName);
            observation.TagId = tagId;
            observation.ImageName = imageName;
            Pose rawPose = new Pose(
                trackedImage.transform.position,
                trackedImage.transform.rotation);
            float now = Time.time;
            if (!TryUpdateFilteredPose(observation, rawPose, now))
            {
                RecordRejectedEvaluationPose();
                return;
            }

            observation.LastSeenTime = now;
            observation.TrackableId = trackedImage.trackableId;
            observation.HasTrackableId = true;
            RecordAcceptedEvaluationPose(tagId, rawPose, observation.TagPose);
        }

        private void RecordRejectedEvaluationPose()
        {
            if (!logRuntimeEvaluation)
            {
                return;
            }

            evaluationRejectedPoseCount++;
            LogEvaluationWindow(false);
        }

        private void RecordAcceptedEvaluationPose(
            int tagId,
            Pose rawPose,
            Pose filteredPose)
        {
            if (!logRuntimeEvaluation)
            {
                return;
            }

            evaluationAcceptedPoseCount++;
            if (evaluationLastRawPoses.TryGetValue(tagId, out Pose previousRaw))
            {
                evaluationRawPositionStepsMm.Add(
                    Vector3.Distance(previousRaw.position, rawPose.position) * 1000f);
                evaluationRawRotationStepsDegrees.Add(
                    Quaternion.Angle(previousRaw.rotation, rawPose.rotation));
            }

            if (evaluationLastFilteredPoses.TryGetValue(
                tagId,
                out Pose previousFiltered))
            {
                evaluationFilteredPositionStepsMm.Add(
                    Vector3.Distance(
                        previousFiltered.position,
                        filteredPose.position) * 1000f);
                evaluationFilteredRotationStepsDegrees.Add(
                    Quaternion.Angle(
                        previousFiltered.rotation,
                        filteredPose.rotation));
            }

            evaluationLastRawPoses[tagId] = rawPose;
            evaluationLastFilteredPoses[tagId] = filteredPose;
            LogEvaluationWindow(false);
        }

        private void ResetEvaluationWindow()
        {
            evaluationWindowStartSeconds = Time.realtimeSinceStartupAsDouble;
            evaluationAcceptedPoseCount = 0;
            evaluationRejectedPoseCount = 0;
            evaluationLastRawPoses.Clear();
            evaluationLastFilteredPoses.Clear();
            evaluationRawPositionStepsMm.Clear();
            evaluationFilteredPositionStepsMm.Clear();
            evaluationRawRotationStepsDegrees.Clear();
            evaluationFilteredRotationStepsDegrees.Clear();
        }

        private void LogEvaluationWindow(bool force)
        {
            if (!logRuntimeEvaluation
                || evaluationAcceptedPoseCount + evaluationRejectedPoseCount == 0)
            {
                return;
            }

            double durationSeconds = Time.realtimeSinceStartupAsDouble
                - evaluationWindowStartSeconds;
            if (!force
                && durationSeconds < Mathf.Max(5f, evaluationWindowSeconds))
            {
                return;
            }

            float acceptedRateHz = (float)(evaluationAcceptedPoseCount
                / Math.Max(0.001, durationSeconds));
            Debug.Log(
                $"APRILTAG_POSE_EVAL duration={durationSeconds:F2}s, "
                + $"accepted={evaluationAcceptedPoseCount}, "
                + $"rejected={evaluationRejectedPoseCount}, "
                + $"acceptedRate={acceptedRateHz:F2}Hz, "
                + $"rawPositionStepMean={Mean(evaluationRawPositionStepsMm):F2}mm, "
                + $"rawPositionStepP95={Percentile(evaluationRawPositionStepsMm, 0.95f):F2}mm, "
                + $"filteredPositionStepMean={Mean(evaluationFilteredPositionStepsMm):F2}mm, "
                + $"filteredPositionStepP95={Percentile(evaluationFilteredPositionStepsMm, 0.95f):F2}mm, "
                + $"rawRotationStepMean={Mean(evaluationRawRotationStepsDegrees):F2}deg, "
                + $"rawRotationStepP95={Percentile(evaluationRawRotationStepsDegrees, 0.95f):F2}deg, "
                + $"filteredRotationStepMean={Mean(evaluationFilteredRotationStepsDegrees):F2}deg, "
                + $"filteredRotationStepP95={Percentile(evaluationFilteredRotationStepsDegrees, 0.95f):F2}deg",
                this);
            ResetEvaluationWindow();
        }

        private static float Mean(List<float> values)
        {
            if (values == null || values.Count == 0)
            {
                return 0f;
            }

            float sum = 0f;
            for (int i = 0; i < values.Count; i++)
            {
                sum += values[i];
            }

            return sum / values.Count;
        }

        private static float Percentile(List<float> values, float percentile)
        {
            if (values == null || values.Count == 0)
            {
                return 0f;
            }

            List<float> sorted = new List<float>(values);
            sorted.Sort();
            int index = Mathf.Clamp(
                Mathf.CeilToInt(percentile * sorted.Count) - 1,
                0,
                sorted.Count - 1);
            return sorted[index];
        }

        private void RemoveTrackedImage(ARTrackedImage trackedImage)
        {
            if (trackedImage == null)
            {
                return;
            }

            for (int i = observations.Count - 1; i >= 0; i--)
            {
                Observation observation = observations[i];
                if (observation.HasTrackableId
                    && observation.TrackableId.Equals(trackedImage.trackableId))
                {
                    observations.RemoveAt(i);
                }
            }
        }

        private Observation FindOrCreateObservation(int tagId, string imageName)
        {
            Observation existing = FindObservation(tagId, imageName);
            if (existing != null)
            {
                return existing;
            }

            Observation created = new Observation();
            observations.Add(created);
            return created;
        }

        private bool TryResolveBinding(
            RetroReplacementRule rule,
            Observation observation,
            out Binding binding)
        {
            binding = null;
            if (rule == null || observation == null)
            {
                return false;
            }

            // An optional legacy Binding may tune age/offset, but it must never
            // widen a rule that explicitly owns a fixed multi-tag identity set.
            if (!rule.IsAprilTagMatch(observation.TagId, observation.ImageName))
            {
                return false;
            }

            for (int i = 0; bindings != null && i < bindings.Count; i++)
            {
                Binding candidate = bindings[i];
                if (candidate == null
                    || !candidate.Enabled
                    || !IsLabelMatch(candidate.DetectionLabel, rule.DetectionLabel)
                    || !IsTagMatch(candidate.TagId, candidate.TrackedImageName, observation))
                {
                    continue;
                }

                binding = candidate;
                return true;
            }

            return rule.IsAprilTagMatch(observation.TagId, observation.ImageName);
        }

        private bool TryScoreObservation(
            Observation observation,
            Rect paddedViewportRect,
            Vector2 detectionCenter,
            out float score)
        {
            score = 0f;
            if (arCamera == null)
            {
                return true;
            }

            Vector3 viewportPoint = arCamera.WorldToViewportPoint(observation.TagPose.position);
            if (viewportPoint.z <= 0f)
            {
                return false;
            }

            Vector2 viewportPosition = new Vector2(viewportPoint.x, viewportPoint.y);
            float centerDistance = Vector2.Distance(viewportPosition, detectionCenter);
            if (!paddedViewportRect.Contains(viewportPosition)
                && centerDistance > maxViewportCenterDistance)
            {
                return false;
            }

            score = centerDistance;
            return true;
        }

        private static Pose BuildObjectPose(
            RetroReplacementRule rule,
            Binding binding,
            Pose tagPose,
            int tagId,
            string imageName)
        {
            Vector3 localOffset = binding != null
                ? binding.TagToObjectOffsetMeters
                : rule.AprilTagToObjectOffsetMeters;
            Quaternion localRotation = binding != null
                ? binding.TagToObjectRotation * rule.RotationOffset
                : rule.GetAprilTagToObjectRotation(tagId, imageName);

            return new Pose(
                tagPose.position
                    + tagPose.rotation * localOffset
                    + Vector3.up * rule.VerticalOffsetMeters,
                tagPose.rotation * localRotation);
        }

        private static bool IsLabelMatch(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTagMatch(
            int tagId,
            string imageName,
            Observation observation)
        {
            if (observation == null)
            {
                return false;
            }

            bool hasTagId = tagId >= 0;
            bool hasImageName = !string.IsNullOrWhiteSpace(imageName);
            if (!hasTagId && !hasImageName)
            {
                return true;
            }

            return (hasTagId && observation.TagId == tagId)
                || (hasImageName
                    && string.Equals(
                        observation.ImageName,
                        imageName,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static Rect ToViewportRect(Rect topLeftNormalizedBox)
        {
            return Rect.MinMaxRect(
                topLeftNormalizedBox.xMin,
                1f - topLeftNormalizedBox.yMax,
                topLeftNormalizedBox.xMax,
                1f - topLeftNormalizedBox.yMin);
        }

        private static Rect ExpandRect(Rect rect, float padding)
        {
            return Rect.MinMaxRect(
                Mathf.Clamp01(rect.xMin - padding),
                Mathf.Clamp01(rect.yMin - padding),
                Mathf.Clamp01(rect.xMax + padding),
                Mathf.Clamp01(rect.yMax + padding));
        }

        private static int ParseTagId(string imageName)
        {
            if (string.IsNullOrWhiteSpace(imageName))
            {
                return -1;
            }

            int value = 0;
            bool hasDigits = false;
            for (int i = 0; i < imageName.Length; i++)
            {
                char character = imageName[i];
                if (character < '0' || character > '9')
                {
                    if (hasDigits)
                    {
                        break;
                    }

                    continue;
                }

                hasDigits = true;
                value = value * 10 + character - '0';
            }

            return hasDigits ? value : -1;
        }
    }
}
