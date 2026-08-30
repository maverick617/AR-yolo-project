using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Compact instance mask sampled inside one screen-space detection box.
    /// It is optional so the existing detection-only ONNX model remains a valid
    /// fallback while a YOLO segmentation model can provide precise silhouettes.
    /// </summary>
    public sealed class DetectionMask
    {
        private readonly Rect normalizedScreenBox;
        private readonly int width;
        private readonly int height;
        private readonly byte[] values;

        public Rect NormalizedScreenBox => normalizedScreenBox;
        public int Width => width;
        public int Height => height;

        public DetectionMask(
            Rect normalizedScreenBox,
            int width,
            int height,
            byte[] values)
        {
            this.normalizedScreenBox = normalizedScreenBox;
            this.width = Mathf.Max(1, width);
            this.height = Mathf.Max(1, height);
            this.values = values;
        }

        public bool ContainsTopLeftNormalizedPoint(Vector2 point, byte threshold = 128)
        {
            return SampleTopLeftNormalizedPoint(point) >= threshold;
        }

        public byte SampleTopLeftNormalizedPoint(Vector2 point)
        {
            if (values == null
                || values.Length < width * height
                || normalizedScreenBox.width <= 0f
                || normalizedScreenBox.height <= 0f
                || !normalizedScreenBox.Contains(point))
            {
                return 0;
            }

            float u = Mathf.InverseLerp(
                normalizedScreenBox.xMin,
                normalizedScreenBox.xMax,
                point.x);
            float v = Mathf.InverseLerp(
                normalizedScreenBox.yMin,
                normalizedScreenBox.yMax,
                point.y);
            int x = Mathf.Clamp(Mathf.RoundToInt(u * (width - 1)), 0, width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(v * (height - 1)), 0, height - 1);
            return values[y * width + x];
        }

        public byte SampleLocal(float u, float v)
        {
            if (values == null || values.Length < width * height)
            {
                return 0;
            }

            int x = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(u) * (width - 1)), 0, width - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(v) * (height - 1)), 0, height - 1);
            return values[y * width + x];
        }

        public bool TryGetActiveTopLeftNormalizedBounds(
            out Rect bounds,
            byte threshold = 128)
        {
            bounds = default;
            if (values == null
                || values.Length < width * height
                || normalizedScreenBox.width <= 0f
                || normalizedScreenBox.height <= 0f)
            {
                return false;
            }

            int minX = width;
            int minY = height;
            int maxX = -1;
            int maxY = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (values[row + x] < threshold)
                    {
                        continue;
                    }

                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return false;
            }

            float localXMin = minX / (float)width;
            float localYMin = minY / (float)height;
            float localXMax = (maxX + 1f) / width;
            float localYMax = (maxY + 1f) / height;
            bounds = Rect.MinMaxRect(
                normalizedScreenBox.xMin + normalizedScreenBox.width * localXMin,
                normalizedScreenBox.yMin + normalizedScreenBox.height * localYMin,
                normalizedScreenBox.xMin + normalizedScreenBox.width * localXMax,
                normalizedScreenBox.yMin + normalizedScreenBox.height * localYMax);
            return bounds.width > 0f && bounds.height > 0f;
        }

        /// <summary>
        /// Returns the two silhouette axes in screen coordinates. PCA is performed
        /// in pixels (not normalized UVs), so portrait aspect ratio does not skew
        /// the result. Axis signs are intentionally left ambiguous for the pose
        /// estimator to stabilize across frames.
        /// </summary>
        public bool TryGetPrincipalAxesTopLeftNormalized(
            out Vector2 center,
            out Vector2 primaryStart,
            out Vector2 primaryEnd,
            out Vector2 secondaryStart,
            out Vector2 secondaryEnd,
            byte threshold = 128)
        {
            center = normalizedScreenBox.center;
            primaryStart = center;
            primaryEnd = center;
            secondaryStart = center;
            secondaryEnd = center;
            if (values == null
                || values.Length < width * height
                || normalizedScreenBox.width <= 0f
                || normalizedScreenBox.height <= 0f)
            {
                return false;
            }

            float screenWidth = Mathf.Max(1f, Screen.width);
            float screenHeight = Mathf.Max(1f, Screen.height);
            float sumX = 0f;
            float sumY = 0f;
            int count = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (values[row + x] < threshold)
                    {
                        continue;
                    }

                    Vector2 point = LocalPixelToScreenPixels(x, y, screenWidth, screenHeight);
                    sumX += point.x;
                    sumY += point.y;
                    count++;
                }
            }

            if (count < 3)
            {
                return false;
            }

            Vector2 mean = new Vector2(sumX / count, sumY / count);
            float xx = 0f;
            float xy = 0f;
            float yy = 0f;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (values[row + x] < threshold)
                    {
                        continue;
                    }

                    Vector2 delta = LocalPixelToScreenPixels(
                        x,
                        y,
                        screenWidth,
                        screenHeight) - mean;
                    xx += delta.x * delta.x;
                    xy += delta.x * delta.y;
                    yy += delta.y * delta.y;
                }
            }

            float angle = 0.5f * Mathf.Atan2(2f * xy, xx - yy);
            Vector2 primary = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 secondary = new Vector2(-primary.y, primary.x);
            float minPrimary = float.PositiveInfinity;
            float maxPrimary = float.NegativeInfinity;
            float minSecondary = float.PositiveInfinity;
            float maxSecondary = float.NegativeInfinity;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (values[row + x] < threshold)
                    {
                        continue;
                    }

                    Vector2 delta = LocalPixelToScreenPixels(
                        x,
                        y,
                        screenWidth,
                        screenHeight) - mean;
                    float primaryProjection = Vector2.Dot(delta, primary);
                    float secondaryProjection = Vector2.Dot(delta, secondary);
                    minPrimary = Mathf.Min(minPrimary, primaryProjection);
                    maxPrimary = Mathf.Max(maxPrimary, primaryProjection);
                    minSecondary = Mathf.Min(minSecondary, secondaryProjection);
                    maxSecondary = Mathf.Max(maxSecondary, secondaryProjection);
                }
            }

            if (float.IsNaN(minPrimary)
                || float.IsInfinity(minPrimary)
                || maxPrimary - minPrimary < 1f
                || maxSecondary - minSecondary < 1f)
            {
                return false;
            }

            center = ScreenPixelsToNormalized(mean, screenWidth, screenHeight);
            primaryStart = ScreenPixelsToNormalized(
                mean + primary * minPrimary,
                screenWidth,
                screenHeight);
            primaryEnd = ScreenPixelsToNormalized(
                mean + primary * maxPrimary,
                screenWidth,
                screenHeight);
            secondaryStart = ScreenPixelsToNormalized(
                mean + secondary * minSecondary,
                screenWidth,
                screenHeight);
            secondaryEnd = ScreenPixelsToNormalized(
                mean + secondary * maxSecondary,
                screenWidth,
                screenHeight);
            return true;
        }

        private Vector2 LocalPixelToScreenPixels(
            int x,
            int y,
            float screenWidth,
            float screenHeight)
        {
            float u = (x + 0.5f) / width;
            float v = (y + 0.5f) / height;
            return new Vector2(
                (normalizedScreenBox.xMin + normalizedScreenBox.width * u) * screenWidth,
                (normalizedScreenBox.yMin + normalizedScreenBox.height * v) * screenHeight);
        }

        private static Vector2 ScreenPixelsToNormalized(
            Vector2 point,
            float screenWidth,
            float screenHeight)
        {
            return new Vector2(
                Mathf.Clamp01(point.x / screenWidth),
                Mathf.Clamp01(point.y / screenHeight));
        }
    }
}
