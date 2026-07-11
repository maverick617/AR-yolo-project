using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    public sealed class YoloDetectionOverlay : MonoBehaviour
    {
        [SerializeField] private YoloObjectDetector detector;
        [SerializeField] private Color boxColor = Color.green;
        [SerializeField, Min(1f)] private float lineThickness = 4f;

        private readonly List<DetectionResult> latestDetections = new List<DetectionResult>();
        private GUIStyle labelStyle;

        private void Reset()
        {
            detector = FindObjectOfType<YoloObjectDetector>();
        }

        private void OnEnable()
        {
            if (detector != null)
            {
                detector.DetectionsReady += HandleDetectionsReady;
            }
        }

        private void OnDisable()
        {
            if (detector != null)
            {
                detector.DetectionsReady -= HandleDetectionsReady;
            }
        }

        private void HandleDetectionsReady(IReadOnlyList<DetectionResult> detections)
        {
            latestDetections.Clear();
            if (detections == null)
            {
                return;
            }

            for (int i = 0; i < detections.Count; i++)
            {
                latestDetections.Add(detections[i]);
            }
        }

        private void OnGUI()
        {
            EnsureStyle();

            Color previousColor = GUI.color;
            GUI.color = boxColor;

            for (int i = 0; i < latestDetections.Count; i++)
            {
                DetectionResult detection = latestDetections[i];
                Rect normalizedBox = detection.NormalizedBox;
                Rect screenBox = new Rect(
                    normalizedBox.x * Screen.width,
                    normalizedBox.y * Screen.height,
                    normalizedBox.width * Screen.width,
                    normalizedBox.height * Screen.height);

                DrawOutline(screenBox);
                GUI.Label(
                    new Rect(screenBox.x, Mathf.Max(0f, screenBox.y - 32f), 240f, 32f),
                    $"{detection.Label} {detection.Confidence:F2}",
                    labelStyle);
            }

            GUI.color = previousColor;
        }

        private void DrawOutline(Rect rect)
        {
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, lineThickness), Texture2D.whiteTexture);
            GUI.DrawTexture(
                new Rect(rect.x, rect.yMax - lineThickness, rect.width, lineThickness),
                Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(rect.x, rect.y, lineThickness, rect.height), Texture2D.whiteTexture);
            GUI.DrawTexture(
                new Rect(rect.xMax - lineThickness, rect.y, lineThickness, rect.height),
                Texture2D.whiteTexture);
        }

        private void EnsureStyle()
        {
            if (labelStyle != null)
            {
                return;
            }

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 24,
                fontStyle = FontStyle.Bold
            };
            labelStyle.normal.textColor = boxColor;
        }
    }
}
