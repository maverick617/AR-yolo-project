using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace AR80sRetro
{
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
            public float LastSeenTime;
            public TrackableId TrackableId;
            public bool HasTrackableId;
        }

        [SerializeField] private Camera arCamera;
        [SerializeField] private ARTrackedImageManager trackedImageManager;
        [SerializeField] private List<Binding> bindings = new List<Binding>();
        [SerializeField, Min(0.05f)] private float defaultMaxPoseAgeSeconds = 0.35f;
        [SerializeField, Range(0f, 0.5f)] private float detectionBoxPadding = 0.08f;
        [SerializeField, Range(0.01f, 1f)] private float maxViewportCenterDistance = 0.35f;
        [SerializeField] private bool logPoseUpdates;

        private readonly List<Observation> observations = new List<Observation>();

        private void Reset()
        {
            arCamera = Camera.main;
            trackedImageManager = FindObjectOfType<ARTrackedImageManager>();
        }

        private void OnEnable()
        {
            if (trackedImageManager != null)
            {
                trackedImageManager.trackedImagesChanged += HandleTrackedImagesChanged;
            }
        }

        private void OnDisable()
        {
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
            observation.TagId = tagId;
            observation.ImageName = imageName;
            observation.TagPose = tagPose;
            observation.LastSeenTime = Time.time;
            observation.HasTrackableId = false;

            if (logPoseUpdates)
            {
                Debug.Log($"AprilTag pose: id={tagId}, image={imageName}, pose={tagPose.position}", this);
            }
        }

        public bool TryGetObjectPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose objectPose)
        {
            return TryGetObjectPose(
                rule,
                detection,
                out objectPose,
                out _,
                out _);
        }

        public bool TryGetObjectPose(
            RetroReplacementRule rule,
            DetectionResult detection,
            out Pose objectPose,
            out int tagId,
            out string imageName)
        {
            objectPose = default;
            tagId = -1;
            imageName = null;

            if (rule == null || !rule.UseAprilTagPose)
            {
                return false;
            }

            float now = Time.time;
            Rect viewportRect = ToViewportRect(detection.NormalizedBox);
            Rect paddedViewportRect = ExpandRect(viewportRect, detectionBoxPadding);
            Vector2 detectionCenter = viewportRect.center;

            Observation bestObservation = null;
            Binding bestBinding = null;
            float bestScore = float.PositiveInfinity;

            for (int i = 0; i < observations.Count; i++)
            {
                Observation observation = observations[i];
                if (!TryResolveBinding(rule, observation, out Binding binding))
                {
                    continue;
                }

                float maxAge = binding != null
                    ? binding.MaxPoseAgeSeconds
                    : Mathf.Max(defaultMaxPoseAgeSeconds, rule.AprilTagMaxPoseAgeSeconds);
                if (now - observation.LastSeenTime > maxAge)
                {
                    continue;
                }

                if (!TryScoreObservation(
                    observation,
                    paddedViewportRect,
                    detectionCenter,
                    out float score))
                {
                    continue;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestObservation = observation;
                    bestBinding = binding;
                }
            }

            if (bestObservation == null)
            {
                return false;
            }

            objectPose = BuildObjectPose(rule, bestBinding, bestObservation.TagPose);
            tagId = bestObservation.TagId;
            imageName = bestObservation.ImageName;
            return true;
        }

        public bool TryGetObjectPose(
            RetroReplacementRule rule,
            out Pose objectPose)
        {
            objectPose = default;

            if (rule == null || !rule.UseAprilTagPose)
            {
                return false;
            }

            float now = Time.time;
            Observation bestObservation = null;
            Binding bestBinding = null;
            float newestSeenTime = float.NegativeInfinity;

            for (int i = 0; i < observations.Count; i++)
            {
                Observation observation = observations[i];
                if (!TryResolveBinding(rule, observation, out Binding binding))
                {
                    continue;
                }

                float maxAge = binding != null
                    ? binding.MaxPoseAgeSeconds
                    : Mathf.Max(defaultMaxPoseAgeSeconds, rule.AprilTagMaxPoseAgeSeconds);
                if (now - observation.LastSeenTime > maxAge
                    || observation.LastSeenTime <= newestSeenTime)
                {
                    continue;
                }

                bestObservation = observation;
                bestBinding = binding;
                newestSeenTime = observation.LastSeenTime;
            }

            if (bestObservation == null)
            {
                return false;
            }

            objectPose = BuildObjectPose(rule, bestBinding, bestObservation.TagPose);
            return true;
        }

        public bool TryGetObjectPose(
            RetroReplacementRule rule,
            int requiredTagId,
            string requiredImageName,
            out Pose objectPose)
        {
            objectPose = default;

            if (rule == null || !rule.UseAprilTagPose)
            {
                return false;
            }

            float now = Time.time;
            Observation bestObservation = null;
            Binding bestBinding = null;
            float newestSeenTime = float.NegativeInfinity;

            for (int i = 0; i < observations.Count; i++)
            {
                Observation observation = observations[i];
                if (!IsTagMatch(requiredTagId, requiredImageName, observation)
                    || !TryResolveBinding(rule, observation, out Binding binding))
                {
                    continue;
                }

                float maxAge = binding != null
                    ? binding.MaxPoseAgeSeconds
                    : Mathf.Max(defaultMaxPoseAgeSeconds, rule.AprilTagMaxPoseAgeSeconds);
                if (now - observation.LastSeenTime > maxAge
                    || observation.LastSeenTime <= newestSeenTime)
                {
                    continue;
                }

                bestObservation = observation;
                bestBinding = binding;
                newestSeenTime = observation.LastSeenTime;
            }

            if (bestObservation == null)
            {
                return false;
            }

            objectPose = BuildObjectPose(rule, bestBinding, bestObservation.TagPose);
            return true;
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
            observation.TagPose = new Pose(
                trackedImage.transform.position,
                trackedImage.transform.rotation);
            observation.LastSeenTime = Time.time;
            observation.TrackableId = trackedImage.trackableId;
            observation.HasTrackableId = true;

            if (logPoseUpdates)
            {
                Debug.Log($"Tracked tag image: id={tagId}, image={imageName}", this);
            }
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
            for (int i = 0; i < observations.Count; i++)
            {
                Observation observation = observations[i];
                if (tagId >= 0 && observation.TagId == tagId)
                {
                    return observation;
                }

                if (!string.IsNullOrWhiteSpace(imageName)
                    && string.Equals(observation.ImageName, imageName, StringComparison.OrdinalIgnoreCase))
                {
                    return observation;
                }
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
            for (int i = 0; i < bindings.Count; i++)
            {
                Binding candidate = bindings[i];
                if (candidate == null
                    || !candidate.Enabled
                    || !IsLabelMatch(candidate.DetectionLabel, rule.DetectionLabel))
                {
                    continue;
                }

                if (!IsTagMatch(candidate.TagId, candidate.TrackedImageName, observation))
                {
                    continue;
                }

                binding = candidate;
                return true;
            }

            return IsTagMatch(rule.AprilTagId, rule.AprilTagTrackedImageName, observation);
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
            Pose tagPose)
        {
            Vector3 localOffset = binding != null
                ? binding.TagToObjectOffsetMeters
                : rule.AprilTagToObjectOffsetMeters;
            Quaternion localRotation = binding != null
                ? binding.TagToObjectRotation
                : rule.AprilTagToObjectRotation;

            Vector3 position = tagPose.position
                + tagPose.rotation * localOffset
                + Vector3.up * rule.VerticalOffsetMeters;
            Quaternion rotation = tagPose.rotation * localRotation * rule.RotationOffset;
            return new Pose(position, rotation);
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
            bool hasTagId = tagId >= 0;
            bool hasImageName = !string.IsNullOrWhiteSpace(imageName);

            if (!hasTagId && !hasImageName)
            {
                return true;
            }

            bool idMatches = hasTagId && observation.TagId == tagId;
            bool imageMatches = hasImageName
                && string.Equals(observation.ImageName, imageName, StringComparison.OrdinalIgnoreCase);
            return idMatches || imageMatches;
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
