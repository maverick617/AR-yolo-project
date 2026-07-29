using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Legacy profile collector retained for single-tag rules. The current
    /// multi-tag cup demo uses passive same-view sizing in the replacement manager
    /// and does not require a caregiver scan.
    /// </summary>
    internal sealed class CupRegistrationSession
    {
        private readonly int tagId;
        private readonly string imageName;
        private readonly RetroReplacementRule rule;
        private readonly Quaternion tagToCupRotation;
        private readonly List<Vector3> localCenters = new List<Vector3>(32);
        private readonly List<float> widths = new List<float>(32);
        private readonly List<float> heights = new List<float>(32);
        private readonly List<float> bodyDiameters = new List<float>(32);
        private readonly List<float> confidences = new List<float>(32);
        private readonly List<Vector3> viewDirections = new List<Vector3>(32);
        private int environmentDepthSamples;

        public CupRegistrationSession(
            int tagId,
            string imageName,
            RetroReplacementRule rule)
        {
            this.tagId = tagId;
            this.imageName = imageName;
            this.rule = rule;
            tagToCupRotation = rule.GetAprilTagToObjectRotation(tagId, imageName);
        }

        public int SampleCount => widths.Count;
        public bool IsComplete => SampleCount >= rule.CupRegistrationSamples;

        public bool TryAddSample(
            Pose tagWorldPose,
            CupMeasurement measurement,
            Camera arCamera)
        {
            if (!measurement.IsValid
                || measurement.Confidence < rule.MinimumRegistrationMeasurementConfidence)
            {
                return false;
            }

            Vector3 viewDirection = Vector3.forward;
            if (arCamera != null)
            {
                viewDirection = Quaternion.Inverse(tagWorldPose.rotation)
                    * (arCamera.transform.position - tagWorldPose.position).normalized;
            }

            if (arCamera != null)
            {
                for (int i = 0; i < viewDirections.Count; i++)
                {
                    if (Vector3.Angle(viewDirections[i], viewDirection)
                        < rule.MinimumRegistrationViewAngleDegrees)
                    {
                        return false;
                    }
                }
            }

            float width = measurement.SizeMeters.x;
            float height = measurement.SizeMeters.y;
            Vector3 localCenter;
            if (rule.UseStandardCupTagMount)
            {
                // Legacy standard mounts place a rigid tag at the vertical
                // midpoint. The configured mount rotation supplies the handle
                // direction; current multi-tag cup rules bypass this profile path.
                localCenter = rule.TagToCupCenterDirection
                    * (measurement.BodyDiameterMeters * 0.5f
                        + rule.TagMountStandoffMeters);
            }
            else
            {
                localCenter = Quaternion.Inverse(tagWorldPose.rotation)
                    * (measurement.CenterWorld - tagWorldPose.position);
            }

            localCenters.Add(localCenter);
            widths.Add(width);
            heights.Add(height);
            bodyDiameters.Add(measurement.BodyDiameterMeters);
            confidences.Add(measurement.Confidence);
            viewDirections.Add(viewDirection);
            if (measurement.UsedEnvironmentDepth)
            {
                environmentDepthSamples++;
            }

            return true;
        }

        public CupRegistrationProfile BuildProfile()
        {
            if (SampleCount == 0)
            {
                return null;
            }

            float width = Median(widths);
            float height = Median(heights);
            float bodyDiameter = Median(bodyDiameters);
            Vector3 center = Median(localCenters);
            float confidence = Median(confidences);
            Pose tagToCupPose = new Pose(center, tagToCupRotation);
            CupRegistrationProfile profile = new CupRegistrationProfile();
            profile.SetRegistration(
                tagId,
                imageName,
                rule.DetectionLabel,
                tagToCupPose,
                new Vector3(width, height, bodyDiameter),
                confidence,
                SampleCount,
                environmentDepthSamples >= Mathf.CeilToInt(SampleCount * 0.5f));
            return profile;
        }

        private static float Median(List<float> values)
        {
            List<float> sorted = new List<float>(values);
            sorted.Sort();
            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) * 0.5f
                : sorted[middle];
        }

        private static Vector3 Median(List<Vector3> values)
        {
            List<float> x = new List<float>(values.Count);
            List<float> y = new List<float>(values.Count);
            List<float> z = new List<float>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                x.Add(values[i].x);
                y.Add(values[i].y);
                z.Add(values[i].z);
            }

            return new Vector3(Median(x), Median(y), Median(z));
        }
    }
}
