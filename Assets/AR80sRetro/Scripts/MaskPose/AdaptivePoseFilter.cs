using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Velocity-adaptive exponential smoothing. Slow jitter is strongly filtered;
    /// deliberate movement raises the cutoff so the replacement does not lag.
    /// </summary>
    public sealed class AdaptivePoseFilter
    {
        private const float PositionDeadbandMeters = 0.0025f;
        private const float RotationDeadbandDegrees = 1.25f;
        private const float SizeDeadbandFraction = 0.018f;
        private const float MaximumPositionSpeedMetersPerSecond = 1.8f;
        private const float MaximumRotationSpeedDegreesPerSecond = 300f;
        private const float MaximumSizeChangeFractionPerSecond = 0.45f;
        private const float PositionJumpMeters = 0.12f;
        private const float PositionJumpSpeedAllowance = 0.8f;
        private const float OutlierConfirmationRadiusMeters = 0.065f;
        private const float OutlierConfirmationSeconds = 0.45f;

        private bool initialized;
        private float lastTime;
        private Vector3 position;
        private Quaternion rotation;
        private Vector3 size;
        private bool hasPendingPositionOutlier;
        private float pendingPositionOutlierTime;
        private Vector3 pendingPositionOutlier;

        public CategoryPoseEstimate Update(
            CategoryPoseEstimate raw,
            float now,
            float minimumCutoff,
            float velocityResponse,
            float rotationCutoff,
            float rotationVelocityResponse,
            float sizeCutoff,
            float resetAfterSeconds)
        {
            if (!initialized || now - lastTime > resetAfterSeconds)
            {
                initialized = true;
                lastTime = now;
                position = raw.Pose.position;
                rotation = raw.Pose.rotation;
                size = raw.Size;
                hasPendingPositionOutlier = false;
                return raw;
            }

            float deltaTime = Mathf.Max(0.001f, now - lastTime);
            Vector3 targetPosition = raw.Pose.position;
            Quaternion targetRotation = raw.Pose.rotation;
            Vector3 targetSize = raw.Size;

            // A depth sample can occasionally land on the hand or the background.
            // Do not let one such frame teleport the replacement. A large move is
            // accepted on the next detector result when it persists in the same
            // neighbourhood, so deliberate hand motion only incurs one sample of
            // confirmation instead of becoming permanently sluggish.
            float positionDelta = Vector3.Distance(position, targetPosition);
            float jumpThreshold = PositionJumpMeters
                + PositionJumpSpeedAllowance * Mathf.Min(deltaTime, 0.25f);
            if (positionDelta > jumpThreshold)
            {
                bool confirmsPreviousOutlier = hasPendingPositionOutlier
                    && now - pendingPositionOutlierTime <= OutlierConfirmationSeconds
                    && Vector3.Distance(targetPosition, pendingPositionOutlier)
                        <= OutlierConfirmationRadiusMeters;
                if (!confirmsPreviousOutlier)
                {
                    pendingPositionOutlier = targetPosition;
                    pendingPositionOutlierTime = now;
                    hasPendingPositionOutlier = true;
                    lastTime = now;
                    return BuildFilteredEstimate(raw);
                }
            }

            hasPendingPositionOutlier = false;
            if (positionDelta < PositionDeadbandMeters)
            {
                targetPosition = position;
            }

            float rotationDelta = Quaternion.Angle(rotation, targetRotation);
            if (rotationDelta < RotationDeadbandDegrees)
            {
                targetRotation = rotation;
            }

            targetSize = ApplySizeDeadband(targetSize);
            targetPosition = Vector3.MoveTowards(
                position,
                targetPosition,
                MaximumPositionSpeedMetersPerSecond * deltaTime);
            targetRotation = Quaternion.RotateTowards(
                rotation,
                targetRotation,
                MaximumRotationSpeedDegreesPerSecond * deltaTime);
            targetSize = LimitSizeRate(targetSize, deltaTime);

            float speed = Vector3.Distance(position, targetPosition) / deltaTime;
            float cutoff = Mathf.Max(0.01f, minimumCutoff + speed * velocityResponse);
            cutoff = Mathf.Min(cutoff, 12f);
            float positionAlpha = LowPassAlpha(deltaTime, cutoff);
            float rotationSpeed = Quaternion.Angle(rotation, targetRotation) / deltaTime;
            float adaptiveRotationCutoff = rotationCutoff
                + rotationSpeed * Mathf.Max(0f, rotationVelocityResponse);
            adaptiveRotationCutoff = Mathf.Min(adaptiveRotationCutoff, 12f);
            float rotationAlpha = LowPassAlpha(deltaTime, adaptiveRotationCutoff);
            float sizeAlpha = LowPassAlpha(deltaTime, Mathf.Max(0.05f, sizeCutoff));

            position = Vector3.Lerp(position, targetPosition, positionAlpha);
            rotation = Quaternion.Slerp(rotation, targetRotation, rotationAlpha);
            size = Vector3.Lerp(size, targetSize, sizeAlpha);
            lastTime = now;
            return BuildFilteredEstimate(raw);
        }

        private CategoryPoseEstimate BuildFilteredEstimate(CategoryPoseEstimate raw)
        {
            return new CategoryPoseEstimate(
                raw.Label,
                new Pose(position, rotation),
                size,
                raw.Confidence,
                raw.Detection);
        }

        private Vector3 ApplySizeDeadband(Vector3 target)
        {
            target.x = ApplyRelativeDeadband(size.x, target.x);
            target.y = ApplyRelativeDeadband(size.y, target.y);
            target.z = ApplyRelativeDeadband(size.z, target.z);
            return target;
        }

        private static float ApplyRelativeDeadband(float current, float target)
        {
            float denominator = Mathf.Max(Mathf.Abs(current), 0.001f);
            return Mathf.Abs(target - current) / denominator < SizeDeadbandFraction
                ? current
                : target;
        }

        private Vector3 LimitSizeRate(Vector3 target, float deltaTime)
        {
            return new Vector3(
                LimitDimensionRate(size.x, target.x, deltaTime),
                LimitDimensionRate(size.y, target.y, deltaTime),
                LimitDimensionRate(size.z, target.z, deltaTime));
        }

        private static float LimitDimensionRate(float current, float target, float deltaTime)
        {
            float maximumDelta = Mathf.Max(0.001f, Mathf.Abs(current))
                * MaximumSizeChangeFractionPerSecond
                * deltaTime;
            return Mathf.MoveTowards(current, target, maximumDelta);
        }

        private static float LowPassAlpha(float deltaTime, float cutoff)
        {
            float timeConstant = 1f / (2f * Mathf.PI * Mathf.Max(0.01f, cutoff));
            return Mathf.Clamp01(deltaTime / (timeConstant + deltaTime));
        }
    }
}
