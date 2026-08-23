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
            if (values == null
                || values.Length < width * height
                || normalizedScreenBox.width <= 0f
                || normalizedScreenBox.height <= 0f
                || !normalizedScreenBox.Contains(point))
            {
                return false;
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
            return values[y * width + x] >= threshold;
        }
    }
}
