using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Category-level pose, deliberately avoiding exact-instance CAD. It applies
    /// symmetry/shape priors to PCA axes extracted from the masked depth cloud.
    /// </summary>
    [DefaultExecutionOrder(-80)]
    public sealed class CategoryPoseEstimator : MonoBehaviour
    {
        private enum PoseRule
        {
            Axisymmetric,
            Planar,
            WorldUpright
        }

        [SerializeField] private MaskDepthPointCloudProvider pointCloudProvider;
        [SerializeField] private Camera arCamera;
        [SerializeField, Range(0f, 1f)] private float minimumCloudConfidence = 0.12f;

        [Header("Temporal filter")]
        [SerializeField, Range(0.1f, 12f)] private float minimumPositionCutoff = 4.5f;
        [SerializeField, Range(0f, 40f)] private float positionVelocityResponse = 24f;
        [SerializeField, Range(0.1f, 12f)] private float rotationCutoff = 4.5f;
        [SerializeField, Range(0f, 0.15f)] private float rotationVelocityResponse = 0.05f;
        [SerializeField, Range(0.05f, 4f)] private float sizeCutoff = 0.45f;
        [SerializeField, Range(0.1f, 3f)] private float resetAfterSeconds = 0.6f;

        [Header("Bottle size calibration")]
        [SerializeField, Range(0.1f, 0.4f)] private float nominalBottleHeightMeters = 0.23f;
        [SerializeField, Range(0.03f, 0.15f)] private float nominalBottleDiameterMeters = 0.068f;
        [SerializeField, Range(0.7f, 1.1f)] private float bottleBoxScale = 0.94f;
        [SerializeField, Range(0f, 1f)] private float bottleMeasurementWeight = 0.18f;

        [Header("Category physical-size priors")]
        [SerializeField] private Vector3 nominalPhoneSizeMeters =
            new Vector3(0.075f, 0.16f, 0.009f);
        [SerializeField] private Vector3 nominalCupSizeMeters =
            new Vector3(0.085f, 0.105f, 0.085f);
        [SerializeField] private Vector3 nominalPenSizeMeters =
            new Vector3(0.012f, 0.145f, 0.012f);
        [SerializeField, Range(0f, 1f)] private float planarMeasurementWeight = 0.22f;

        private readonly Dictionary<string, AdaptivePoseFilter> filters =
            new Dictionary<string, AdaptivePoseFilter>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Vector3> previousPrimaryAxes =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        public event Action<CategoryPoseEstimate> PoseReady;

        private void Reset()
        {
            pointCloudProvider = FindObjectOfType<MaskDepthPointCloudProvider>();
            arCamera = Camera.main;
        }

        private void OnEnable()
        {
            if (pointCloudProvider != null)
            {
                pointCloudProvider.PointCloudReady += HandlePointCloud;
            }
        }

        private void OnDisable()
        {
            if (pointCloudProvider != null)
            {
                pointCloudProvider.PointCloudReady -= HandlePointCloud;
            }

            filters.Clear();
            previousPrimaryAxes.Clear();
        }

        private void HandlePointCloud(MaskedDepthPointCloud cloud)
        {
            if (cloud == null
                || cloud.Confidence < minimumCloudConfidence)
            {
                return;
            }

            if (arCamera == null)
            {
                arCamera = Camera.main;
            }

            if (arCamera == null)
            {
                return;
            }

            bool hasReliablePointCloud = cloud.WorldPoints != null
                && cloud.WorldPoints.Count >= 3;
            Vector3 largest = arCamera.transform.up;
            Vector3 middle = arCamera.transform.right;
            Vector3 smallest = arCamera.transform.forward;
            if (hasReliablePointCloud
                && !TryGetPrincipalAxes(
                    cloud.WorldPoints,
                    cloud.Centroid,
                    out largest,
                    out middle,
                    out smallest))
            {
                hasReliablePointCloud = false;
                largest = arCamera.transform.up;
                middle = arCamera.transform.right;
                smallest = arCamera.transform.forward;
            }

            float projectionDepth = ResolveProjectionDepth(cloud);
            PoseRule rule = ResolveRule(cloud.Detection.Label);
            Quaternion rotation = ResolveCategoryRotation(
                cloud,
                rule,
                smallest,
                projectionDepth);
            Vector3 size = hasReliablePointCloud
                ? MeasureSize(cloud.WorldPoints, cloud.Centroid, rotation)
                : Vector3.zero;
            Vector3 position = ProjectDetectionCenter(cloud, projectionDepth);
            size = MeasureCategorySize(cloud, size, projectionDepth);

            size.x = Mathf.Max(size.x, 0.008f);
            size.y = Mathf.Max(size.y, 0.008f);
            size.z = EqualsLabel(cloud.Detection.Label, "phone")
                || EqualsLabel(cloud.Detection.Label, "pen")
                ? Mathf.Max(size.z, 0.003f)
                : Mathf.Max(size.z, Mathf.Min(size.x, size.y) * 0.3f);

            // ProjectDetectionCenter lies on the visible front surface. Move the
            // reported pose to the category's geometric center using the stable
            // camera-facing normal, so replacement depth matches its silhouette.
            float centerOffset = EqualsLabel(cloud.Detection.Label, "phone")
                ? size.z * 0.5f
                : (EqualsLabel(cloud.Detection.Label, "bottle")
                    || EqualsLabel(cloud.Detection.Label, "cup"))
                    ? size.x * 0.5f
                    : size.z * 0.5f;
            position -= rotation * Vector3.forward * centerOffset;

            CategoryPoseEstimate raw = new CategoryPoseEstimate(
                cloud.Detection.Label,
                new Pose(position, rotation),
                size,
                cloud.Confidence,
                cloud.Detection);
            if (!filters.TryGetValue(raw.Label, out AdaptivePoseFilter filter))
            {
                filter = new AdaptivePoseFilter();
                filters.Add(raw.Label, filter);
            }

            PoseReady?.Invoke(filter.Update(
                raw,
                Time.unscaledTime,
                minimumPositionCutoff,
                positionVelocityResponse,
                rotationCutoff,
                rotationVelocityResponse,
                sizeCutoff,
                resetAfterSeconds));
        }

        private Vector3 ProjectDetectionCenter(
            MaskedDepthPointCloud cloud,
            float projectionDepth)
        {
            Rect box = GetSilhouetteBounds(cloud.Detection);
            return arCamera.ViewportToWorldPoint(new Vector3(
                box.center.x,
                1f - box.center.y,
                projectionDepth));
        }

        private Vector3 MeasureBottleSize(
            MaskedDepthPointCloud cloud,
            Vector3 pointCloudSize,
            float projectionDepth)
        {
            Rect box = GetSilhouetteBounds(cloud.Detection);
            float depth = projectionDepth;
            Vector3 top = arCamera.ViewportToWorldPoint(new Vector3(
                box.center.x,
                1f - box.yMin,
                depth));
            Vector3 bottom = arCamera.ViewportToWorldPoint(new Vector3(
                box.center.x,
                1f - box.yMax,
                depth));
            float measuredHeight = Vector3.Distance(top, bottom) * bottleBoxScale;
            if (float.IsNaN(measuredHeight)
                || float.IsInfinity(measuredHeight)
                || measuredHeight < 0.1f
                || measuredHeight > 0.5f)
            {
                measuredHeight = Mathf.Max(pointCloudSize.y, nominalBottleHeightMeters);
            }

            measuredHeight = Mathf.Clamp(
                measuredHeight,
                nominalBottleHeightMeters * 0.82f,
                nominalBottleHeightMeters * 1.18f);
            float height = Mathf.Lerp(
                nominalBottleHeightMeters,
                measuredHeight,
                bottleMeasurementWeight);
            height = Mathf.Clamp(
                height,
                nominalBottleHeightMeters * 0.88f,
                nominalBottleHeightMeters * 1.12f);

            Vector3 left = arCamera.ViewportToWorldPoint(new Vector3(
                box.xMin,
                1f - box.center.y,
                depth));
            Vector3 right = arCamera.ViewportToWorldPoint(new Vector3(
                box.xMax,
                1f - box.center.y,
                depth));
            float measuredDiameter = Vector3.Distance(left, right) * bottleBoxScale;
            float expectedDiameter = nominalBottleDiameterMeters;
            float diameter = float.IsNaN(measuredDiameter)
                || float.IsInfinity(measuredDiameter)
                || measuredDiameter < nominalBottleDiameterMeters * 0.7f
                || measuredDiameter > nominalBottleDiameterMeters * 1.45f
                ? expectedDiameter
                : Mathf.Lerp(expectedDiameter, measuredDiameter, 0.35f);
            diameter = Mathf.Clamp(
                diameter,
                nominalBottleDiameterMeters * 0.86f,
                nominalBottleDiameterMeters * 1.14f);
            return new Vector3(diameter, height, diameter);
        }

        private Vector3 MeasureCategorySize(
            MaskedDepthPointCloud cloud,
            Vector3 pointCloudSize,
            float projectionDepth)
        {
            string label = cloud.Detection.Label;
            if (EqualsLabel(label, "bottle"))
            {
                return MeasureBottleSize(cloud, pointCloudSize, projectionDepth);
            }

            Vector3 nominal = EqualsLabel(label, "phone")
                ? nominalPhoneSizeMeters
                : EqualsLabel(label, "cup")
                    ? nominalCupSizeMeters
                    : EqualsLabel(label, "pen")
                        ? nominalPenSizeMeters
                        : pointCloudSize;
            if (nominal == pointCloudSize)
            {
                return pointCloudSize;
            }

            if (!TryMeasureMaskAxes(
                cloud,
                projectionDepth,
                out float longAxis,
                out float shortAxis))
            {
                return nominal;
            }

            float expectedLong = Mathf.Max(nominal.x, nominal.y);
            float expectedShort = Mathf.Min(nominal.x, nominal.y);
            longAxis = ClampAround(longAxis, expectedLong, 0.78f, 1.22f);
            shortAxis = ClampAround(shortAxis, expectedShort, 0.78f, 1.22f);
            float weight = planarMeasurementWeight;
            if (EqualsLabel(label, "cup"))
            {
                return new Vector3(
                    Mathf.Lerp(nominal.x, shortAxis, weight),
                    Mathf.Lerp(nominal.y, longAxis, weight),
                    Mathf.Lerp(nominal.z, shortAxis, weight));
            }

            return new Vector3(
                Mathf.Lerp(nominal.x, shortAxis, weight),
                Mathf.Lerp(nominal.y, longAxis, weight),
                nominal.z);
        }

        private bool TryMeasureMaskAxes(
            MaskedDepthPointCloud cloud,
            float depth,
            out float longAxis,
            out float shortAxis)
        {
            longAxis = 0f;
            shortAxis = 0f;
            DetectionMask mask = cloud.Detection.Mask;
            if (mask == null
                || !mask.TryGetPrincipalAxesTopLeftNormalized(
                    out Vector2 center,
                    out Vector2 primaryStart,
                    out Vector2 primaryEnd,
                    out Vector2 secondaryStart,
                    out Vector2 secondaryEnd))
            {
                return false;
            }

            longAxis = Vector3.Distance(
                ViewportPoint(primaryStart, depth),
                ViewportPoint(primaryEnd, depth));
            shortAxis = Vector3.Distance(
                ViewportPoint(secondaryStart, depth),
                ViewportPoint(secondaryEnd, depth));
            if (shortAxis > longAxis)
            {
                (longAxis, shortAxis) = (shortAxis, longAxis);
            }

            return longAxis > 0.001f && shortAxis > 0.001f;
        }

        private float ResolveProjectionDepth(MaskedDepthPointCloud cloud)
        {
            string label = cloud.Detection.Label;
            float nominalLongAxis = EqualsLabel(label, "bottle")
                ? nominalBottleHeightMeters
                : EqualsLabel(label, "phone")
                    ? Mathf.Max(nominalPhoneSizeMeters.x, nominalPhoneSizeMeters.y)
                    : EqualsLabel(label, "cup")
                        ? Mathf.Max(nominalCupSizeMeters.x, nominalCupSizeMeters.y)
                        : EqualsLabel(label, "pen")
                            ? Mathf.Max(nominalPenSizeMeters.x, nominalPenSizeMeters.y)
                            : 0f;
            if (nominalLongAxis <= 0f
                || cloud.Detection.Mask == null
                || !cloud.Detection.Mask.TryGetPrincipalAxesTopLeftNormalized(
                    out _,
                    out Vector2 primaryStart,
                    out Vector2 primaryEnd,
                    out _,
                    out _))
            {
                return cloud.MedianDepth;
            }

            float lengthAtOneMeter = Vector3.Distance(
                ViewportPoint(primaryStart, 1f),
                ViewportPoint(primaryEnd, 1f));
            if (lengthAtOneMeter < 0.001f)
            {
                return cloud.MedianDepth;
            }

            float silhouetteDepth = Mathf.Clamp(
                nominalLongAxis / lengthAtOneMeter,
                0.18f,
                3f);
            float lidarRatio = cloud.MedianDepth / Mathf.Max(0.01f, silhouetteDepth);
            if (lidarRatio >= 0.75f && lidarRatio <= 1.3f)
            {
                return Mathf.Lerp(silhouetteDepth, cloud.MedianDepth, 0.15f);
            }

            // Transparent bottles and hand-held glossy phones frequently return
            // the background LiDAR depth. Reject it when it contradicts the
            // class-size/silhouette estimate.
            return silhouetteDepth;
        }

        private Quaternion ResolveCategoryRotation(
            MaskedDepthPointCloud cloud,
            PoseRule rule,
            Vector3 smallestPointCloudAxis,
            float projectionDepth)
        {
            string label = cloud.Detection.Label;
            Rect silhouetteBounds = GetSilhouetteBounds(cloud.Detection);
            Vector3 referencePosition = arCamera.ViewportToWorldPoint(new Vector3(
                silhouetteBounds.center.x,
                1f - silhouetteBounds.center.y,
                Mathf.Max(0.18f, projectionDepth)));
            if (EqualsLabel(label, "bottle"))
            {
                Vector3 facing = Vector3.ProjectOnPlane(
                    arCamera.transform.position - referencePosition,
                    Vector3.up).normalized;
                if (facing.sqrMagnitude < 0.001f)
                {
                    facing = -arCamera.transform.forward;
                }

                return Quaternion.LookRotation(facing, Vector3.up);
            }

            if (cloud.Detection.Mask == null
                || !cloud.Detection.Mask.TryGetPrincipalAxesTopLeftNormalized(
                    out Vector2 center,
                    out Vector2 primaryStart,
                    out Vector2 primaryEnd,
                    out _,
                    out _))
            {
                Vector3 toCamera = (arCamera.transform.position - cloud.Centroid).normalized;
                return ResolveRotation(
                    rule,
                    label,
                    referencePosition,
                    Vector3.up,
                    Vector3.right,
                    smallestPointCloudAxis);
            }

            float depth = Mathf.Max(0.18f, projectionDepth);
            Vector3 worldCenter = ViewportPoint(center, depth);
            Vector3 screenAxis = (ViewportPoint(primaryEnd, depth)
                - ViewportPoint(primaryStart, depth)).normalized;
            screenAxis = StabilizePrimaryAxis(label, screenAxis);
            Vector3 toCameraDirection = (arCamera.transform.position - worldCenter).normalized;

            if (EqualsLabel(label, "phone"))
            {
                // The COCO phone class contains no semantic front/back signal.
                // Force the replacement's designated front toward the camera and
                // use the silhouette only for its in-plane roll; this removes
                // random 180-degree PCA flips.
                Vector3 front = toCameraDirection;
                Vector3 up = Vector3.ProjectOnPlane(screenAxis, front).normalized;
                if (up.sqrMagnitude < 0.001f)
                {
                    up = Vector3.ProjectOnPlane(arCamera.transform.up, front).normalized;
                }

                return Quaternion.LookRotation(front, up);
            }

            Vector3 axis = screenAxis;
            Vector3 forward = Vector3.ProjectOnPlane(toCameraDirection, axis).normalized;
            if (forward.sqrMagnitude < 0.001f)
            {
                forward = Vector3.ProjectOnPlane(-arCamera.transform.forward, axis).normalized;
            }

            return Quaternion.LookRotation(forward, axis);
        }

        private Vector3 ViewportPoint(Vector2 topLeftNormalized, float depth)
        {
            return arCamera.ViewportToWorldPoint(new Vector3(
                topLeftNormalized.x,
                1f - topLeftNormalized.y,
                depth));
        }

        private static float ClampAround(
            float value,
            float nominal,
            float minimumScale,
            float maximumScale)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return nominal;
            }

            return Mathf.Clamp(value, nominal * minimumScale, nominal * maximumScale);
        }

        private static bool EqualsLabel(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }

        private static Rect GetSilhouetteBounds(DetectionResult detection)
        {
            return detection.Mask != null
                && detection.Mask.TryGetActiveTopLeftNormalizedBounds(out Rect bounds)
                ? bounds
                : detection.NormalizedBox;
        }

        private Quaternion ResolveRotation(
            PoseRule rule,
            string label,
            Vector3 centroid,
            Vector3 largest,
            Vector3 middle,
            Vector3 smallest)
        {
            Vector3 toCamera = (arCamera.transform.position - centroid).normalized;
            switch (rule)
            {
                case PoseRule.Planar:
                {
                    Vector3 forward = Vector3.Dot(smallest, toCamera) >= 0f
                        ? smallest
                        : -smallest;
                    Vector3 up = Vector3.ProjectOnPlane(largest, forward).normalized;
                    if (up.sqrMagnitude < 0.01f)
                    {
                        up = Vector3.ProjectOnPlane(middle, forward).normalized;
                    }

                    up = StabilizePrimaryAxis(label, up);

                    return Quaternion.LookRotation(forward, up);
                }

                case PoseRule.WorldUpright:
                {
                    Vector3 forward = Vector3.ProjectOnPlane(toCamera, Vector3.up).normalized;
                    if (forward.sqrMagnitude < 0.01f)
                    {
                        forward = Vector3.ProjectOnPlane(arCamera.transform.forward, Vector3.up).normalized;
                    }

                    return Quaternion.LookRotation(forward, Vector3.up);
                }

                default:
                {
                    Vector3 up = StabilizePrimaryAxis(label, largest.normalized);
                    Vector3 forward = Vector3.ProjectOnPlane(toCamera, up).normalized;
                    if (forward.sqrMagnitude < 0.01f)
                    {
                        forward = Vector3.ProjectOnPlane(arCamera.transform.forward, up).normalized;
                    }

                    return Quaternion.LookRotation(forward, up);
                }
            }
        }

        private Vector3 StabilizePrimaryAxis(string label, Vector3 axis)
        {
            if (axis.sqrMagnitude < 0.001f)
            {
                axis = Vector3.up;
            }

            axis.Normalize();
            if (previousPrimaryAxes.TryGetValue(label, out Vector3 previous))
            {
                if (Vector3.Dot(axis, previous) < 0f)
                {
                    axis = -axis;
                }

                // PCA axes are sign-ambiguous and can also rotate sharply when a
                // hand changes the visible mask contour. Limit one detector
                // update to a plausible angular change before the quaternion
                // filter sees it. Fast intentional turns remain possible across
                // consecutive results, while a one-frame contour glitch cannot
                // make the object kick sideways.
                axis = Vector3.RotateTowards(
                    previous,
                    axis,
                    38f * Mathf.Deg2Rad,
                    0f).normalized;
                axis = Vector3.Slerp(previous, axis, 0.72f).normalized;
            }
            else if (Vector3.Dot(axis, Vector3.up) < 0f)
            {
                axis = -axis;
            }

            previousPrimaryAxes[label] = axis;
            return axis;
        }

        private static PoseRule ResolveRule(string label)
        {
            if (string.Equals(label, "phone", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "tv", StringComparison.OrdinalIgnoreCase))
            {
                return PoseRule.Planar;
            }

            if (string.Equals(label, "bottle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "cup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "pen", StringComparison.OrdinalIgnoreCase)
                || string.Equals(label, "plant", StringComparison.OrdinalIgnoreCase))
            {
                return PoseRule.Axisymmetric;
            }

            return PoseRule.WorldUpright;
        }

        private static Vector3 MeasureSize(
            IReadOnlyList<Vector3> points,
            Vector3 centroid,
            Quaternion rotation)
        {
            Quaternion inverse = Quaternion.Inverse(rotation);
            Vector3 minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 local = inverse * (points[i] - centroid);
                minimum = Vector3.Min(minimum, local);
                maximum = Vector3.Max(maximum, local);
            }

            return maximum - minimum;
        }

        private static bool TryGetPrincipalAxes(
            IReadOnlyList<Vector3> points,
            Vector3 centroid,
            out Vector3 largest,
            out Vector3 middle,
            out Vector3 smallest)
        {
            largest = Vector3.up;
            middle = Vector3.right;
            smallest = Vector3.forward;
            if (points == null || points.Count < 3)
            {
                return false;
            }

            float[,] matrix = new float[3, 3];
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 p = points[i] - centroid;
                matrix[0, 0] += p.x * p.x;
                matrix[0, 1] += p.x * p.y;
                matrix[0, 2] += p.x * p.z;
                matrix[1, 1] += p.y * p.y;
                matrix[1, 2] += p.y * p.z;
                matrix[2, 2] += p.z * p.z;
            }

            matrix[1, 0] = matrix[0, 1];
            matrix[2, 0] = matrix[0, 2];
            matrix[2, 1] = matrix[1, 2];
            float[,] eigenvectors =
            {
                { 1f, 0f, 0f },
                { 0f, 1f, 0f },
                { 0f, 0f, 1f }
            };

            for (int iteration = 0; iteration < 12; iteration++)
            {
                int p = 0;
                int q = 1;
                float maximumOffDiagonal = Mathf.Abs(matrix[0, 1]);
                if (Mathf.Abs(matrix[0, 2]) > maximumOffDiagonal)
                {
                    p = 0;
                    q = 2;
                    maximumOffDiagonal = Mathf.Abs(matrix[0, 2]);
                }

                if (Mathf.Abs(matrix[1, 2]) > maximumOffDiagonal)
                {
                    p = 1;
                    q = 2;
                    maximumOffDiagonal = Mathf.Abs(matrix[1, 2]);
                }

                if (maximumOffDiagonal < 0.0000001f)
                {
                    break;
                }

                float app = matrix[p, p];
                float aqq = matrix[q, q];
                float apq = matrix[p, q];
                float angle = 0.5f * Mathf.Atan2(2f * apq, aqq - app);
                float cosine = Mathf.Cos(angle);
                float sine = Mathf.Sin(angle);

                for (int k = 0; k < 3; k++)
                {
                    if (k == p || k == q)
                    {
                        continue;
                    }

                    float akp = matrix[k, p];
                    float akq = matrix[k, q];
                    matrix[k, p] = matrix[p, k] = cosine * akp - sine * akq;
                    matrix[k, q] = matrix[q, k] = sine * akp + cosine * akq;
                }

                matrix[p, p] = cosine * cosine * app
                    - 2f * sine * cosine * apq
                    + sine * sine * aqq;
                matrix[q, q] = sine * sine * app
                    + 2f * sine * cosine * apq
                    + cosine * cosine * aqq;
                matrix[p, q] = matrix[q, p] = 0f;

                for (int k = 0; k < 3; k++)
                {
                    float vkp = eigenvectors[k, p];
                    float vkq = eigenvectors[k, q];
                    eigenvectors[k, p] = cosine * vkp - sine * vkq;
                    eigenvectors[k, q] = sine * vkp + cosine * vkq;
                }
            }

            int largestIndex = 0;
            int smallestIndex = 0;
            for (int i = 1; i < 3; i++)
            {
                if (matrix[i, i] > matrix[largestIndex, largestIndex])
                {
                    largestIndex = i;
                }

                if (matrix[i, i] < matrix[smallestIndex, smallestIndex])
                {
                    smallestIndex = i;
                }
            }

            int middleIndex = 3 - largestIndex - smallestIndex;
            if (largestIndex == smallestIndex)
            {
                largestIndex = 0;
                middleIndex = 1;
                smallestIndex = 2;
            }

            largest = ReadEigenvector(eigenvectors, largestIndex);
            middle = ReadEigenvector(eigenvectors, middleIndex);
            smallest = ReadEigenvector(eigenvectors, smallestIndex);
            return largest.sqrMagnitude > 0.5f
                && middle.sqrMagnitude > 0.5f
                && smallest.sqrMagnitude > 0.5f;
        }

        private static Vector3 ReadEigenvector(float[,] matrix, int column)
        {
            return new Vector3(
                matrix[0, column],
                matrix[1, column],
                matrix[2, column]).normalized;
        }
    }
}
