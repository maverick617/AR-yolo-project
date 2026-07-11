using System;
using UnityEngine;

namespace AR80sRetro
{
    [Serializable]
    public readonly struct DetectionResult
    {
        public DetectionResult(string label, float confidence, Rect normalizedBox)
        {
            Label = label;
            Confidence = confidence;
            NormalizedBox = normalizedBox;
        }

        public string Label { get; }
        public float Confidence { get; }

        // Normalized screen-space box with origin at the top-left.
        public Rect NormalizedBox { get; }

        public Vector2 NormalizedCenter
        {
            get
            {
                return new Vector2(
                    NormalizedBox.x + NormalizedBox.width * 0.5f,
                    NormalizedBox.y + NormalizedBox.height * 0.5f);
            }
        }

        public Vector2 ToScreenPoint(int screenWidth, int screenHeight)
        {
            return ToScreenPoint(screenWidth, screenHeight, new Vector2(0.5f, 0.9f));
        }

        public Vector2 ToScreenPoint(
            int screenWidth,
            int screenHeight,
            Vector2 normalizedAnchorInBox)
        {
            float normalizedX = NormalizedBox.x + NormalizedBox.width * normalizedAnchorInBox.x;
            float normalizedY = NormalizedBox.y + NormalizedBox.height * normalizedAnchorInBox.y;
            return new Vector2(normalizedX * screenWidth, (1f - normalizedY) * screenHeight);
        }

        public bool IsValid(float minConfidence)
        {
            return !string.IsNullOrWhiteSpace(Label)
                && Confidence >= minConfidence
                && NormalizedBox.width > 0f
                && NormalizedBox.height > 0f;
        }
    }
}
