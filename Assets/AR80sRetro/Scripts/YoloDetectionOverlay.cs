using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    public sealed class YoloDetectionOverlay : MonoBehaviour
    {
        [SerializeField] private YoloObjectDetector detector;
        [SerializeField] private Color boxColor = Color.green;
        [SerializeField, Min(1f)] private float lineThickness = 4f;
        [SerializeField, Min(0.1f)] private float holdDetectionSeconds = 0.8f;

        private readonly List<DetectionResult> latestDetections = new List<DetectionResult>();
        private GUIStyle labelStyle;
        private GUIStyle statusStyle;
        private float lastPositiveDetectionTime = float.NegativeInfinity;

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
            if (detections == null || detections.Count == 0)
            {
                return;
            }

            latestDetections.Clear();
            for (int i = 0; i < detections.Count; i++)
            {
                latestDetections.Add(detections[i]);
            }

            lastPositiveDetectionTime = Time.unscaledTime;
        }

        private void OnGUI()
        {
            EnsureStyle();

            if (Time.unscaledTime - lastPositiveDetectionTime > holdDetectionSeconds)
            {
                latestDetections.Clear();
            }

            DrawDetectorStatus();

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

        private void DrawDetectorStatus()
        {
            string message;
            if (detector == null)
            {
                message = "YOLO ERROR: detector missing";
            }
            else if (detector.InitializationFailed)
            {
                message = "YOLO ERROR: initialization failed (see Xcode log)";
            }
            else if (!detector.IsInitialized || detector.InferenceCount == 0)
            {
                message = "YOLO STARTING: waiting for camera frame";
            }
            else
            {
                string state = detector.LastDetectionCount > 0
                    ? "CUP FOUND"
                    : "NO CUP";
                message = $"YOLO RUNNING #{detector.InferenceCount} | {state} | "
                    + $"cup-like={detector.LastCupLikeScore:F2} "
                    + $"(need {detector.ConfidenceThreshold:F2}) | "
                    + $"best={detector.LastBestClassLabel} {detector.LastBestScore:F2}";
            }

            float width = Mathf.Min(900f, Mathf.Max(280f, Screen.width - 32f));
            GUI.Box(new Rect(16f, 88f, width, 74f), message, statusStyle);
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

            statusStyle = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.021f), 14, 25),
                wordWrap = true
            };
            statusStyle.normal.textColor = Color.white;
        }
    }
}
