using System.Collections.Generic;
using UnityEngine;

namespace TagModelAR
{
    /// <summary>Stores short-lived, smoothed world poses for visible AprilTags.</summary>
    public sealed class AprilTagPoseSource : MonoBehaviour
    {
        private sealed class Observation
        {
            public Pose Pose;
            public float LastSeenTime;
            public long Sequence;
            public bool HasPose;
        }

        [SerializeField, Min(0.05f)] private float maxPoseAgeSeconds = 0.8f;
        [SerializeField, Min(0.005f)] private float positionSmoothingSeconds = 0.04f;
        [SerializeField, Min(1f)] private float rotationSmoothingSpeed = 22f;
        [SerializeField, Min(0f)] private float positionDeadbandMeters = 0.0015f;
        [SerializeField, Min(0f)] private float rotationDeadbandDegrees = 0.75f;

        private readonly Dictionary<int, Observation> observations =
            new Dictionary<int, Observation>();
        private long sequence;

        public float MaxPoseAgeSeconds => Mathf.Max(0.05f, maxPoseAgeSeconds);

        public void PublishTagPose(int tagId, Pose rawPose)
        {
            if (!observations.TryGetValue(tagId, out Observation observation))
            {
                observation = new Observation();
                observations.Add(tagId, observation);
            }

            float now = Time.time;
            if (!observation.HasPose
                || now - observation.LastSeenTime > MaxPoseAgeSeconds)
            {
                observation.Pose = rawPose;
            }
            else
            {
                float deltaTime = Mathf.Max(0.001f, now - observation.LastSeenTime);
                Vector3 position = observation.Pose.position;
                if (Vector3.Distance(position, rawPose.position)
                    > positionDeadbandMeters)
                {
                    float alpha = 1f - Mathf.Exp(
                        -deltaTime / Mathf.Max(0.005f, positionSmoothingSeconds));
                    position = Vector3.Lerp(position, rawPose.position, alpha);
                }

                Quaternion rotation = observation.Pose.rotation;
                if (Quaternion.Angle(rotation, rawPose.rotation)
                    > rotationDeadbandDegrees)
                {
                    float alpha = 1f - Mathf.Exp(
                        -rotationSmoothingSpeed * deltaTime);
                    rotation = Quaternion.Slerp(rotation, rawPose.rotation, alpha);
                }

                observation.Pose = new Pose(position, rotation);
            }

            observation.HasPose = true;
            observation.LastSeenTime = now;
            observation.Sequence = ++sequence;
        }

        public bool TryGetTagPose(
            int tagId,
            out Pose pose,
            out float ageSeconds,
            out long sampleSequence)
        {
            pose = default;
            ageSeconds = float.PositiveInfinity;
            sampleSequence = 0L;
            if (!observations.TryGetValue(tagId, out Observation observation)
                || !observation.HasPose)
            {
                return false;
            }

            ageSeconds = Mathf.Max(0f, Time.time - observation.LastSeenTime);
            if (ageSeconds > MaxPoseAgeSeconds)
            {
                return false;
            }

            pose = observation.Pose;
            sampleSequence = observation.Sequence;
            return true;
        }

        public bool TryGetNewestTagPose(
            out Pose pose,
            out int tagId,
            out float ageSeconds,
            out long sampleSequence)
        {
            pose = default;
            tagId = -1;
            ageSeconds = float.PositiveInfinity;
            sampleSequence = 0L;
            float newestTime = float.NegativeInfinity;
            foreach (KeyValuePair<int, Observation> pair in observations)
            {
                Observation observation = pair.Value;
                float age = Time.time - observation.LastSeenTime;
                if (!observation.HasPose
                    || age > MaxPoseAgeSeconds
                    || observation.LastSeenTime <= newestTime)
                {
                    continue;
                }

                newestTime = observation.LastSeenTime;
                pose = observation.Pose;
                tagId = pair.Key;
                ageSeconds = Mathf.Max(0f, age);
                sampleSequence = observation.Sequence;
            }

            return tagId >= 0;
        }

        public void Clear()
        {
            observations.Clear();
        }
    }
}
