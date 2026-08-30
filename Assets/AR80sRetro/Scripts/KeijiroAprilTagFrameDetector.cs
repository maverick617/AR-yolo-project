using System;
using System.Collections.Generic;
using UnityEngine;
using AprilTag;

namespace AR80sRetro
{
    public sealed class KeijiroAprilTagFrameDetector : MonoBehaviour
    {
        [SerializeField] private ARCameraFrameProvider frameProvider;
        [SerializeField] private AprilTagPoseSource poseSource;
        [SerializeField] private Camera arCamera;
        [SerializeField, Min(0.005f)] private float tagSizeMeters = 0.01f;
        [Tooltip("Use full detector resolution for the 1 cm demo tag.")]
        [SerializeField, Min(1)] private int decimation = 1;
        [SerializeField, Min(0.02f)] private float detectionIntervalSeconds = 0.05f;
        [Tooltip("The shared camera texture is already rotated into portrait detector coordinates. Enabling this again applies a duplicate 90-degree roll to the published Tag pose.")]
        [SerializeField] private bool compensateFrameRotation;
        [SerializeField] private Vector3 detectorToCameraRotationCorrectionEuler;
        [SerializeField] private bool logDetectedTags;

        [Header("Runtime evaluation")]
        [Tooltip("Logs measured AprilTag processing time, achieved detector rate and tag-visible frame rate without changing tracking.")]
        [SerializeField] private bool logRuntimeEvaluation = true;
        [SerializeField, Min(5f)] private float evaluationWindowSeconds = 20f;

        private float nextDetectionTime;
        private TagDetector detector;
        private int detectorWidth;
        private int detectorHeight;
        private Color32[] pixelBuffer;
        private readonly List<float> evaluationLatenciesMs = new List<float>(256);
        private double evaluationWindowStartSeconds;
        private int evaluationProcessedFrameCount;
        private int evaluationTagVisibleFrameCount;
        private int evaluationPublishedPoseCount;

        private void Awake()
        {
            if (frameProvider == null)
            {
                frameProvider = FindObjectOfType<ARCameraFrameProvider>();
            }

            if (poseSource == null)
            {
                poseSource = FindObjectOfType<AprilTagPoseSource>();
            }

            if (arCamera == null)
            {
                arCamera = Camera.main;
            }
        }

        private void Reset()
        {
            frameProvider = FindObjectOfType<ARCameraFrameProvider>();
            poseSource = FindObjectOfType<AprilTagPoseSource>();
            arCamera = Camera.main;
        }

        private void OnEnable()
        {
            ResetEvaluationWindow();
        }

        private void Update()
        {
            if (Time.time < nextDetectionTime)
            {
                return;
            }

            nextDetectionTime = Time.time + detectionIntervalSeconds;
            double processingStartSeconds = Time.realtimeSinceStartupAsDouble;

            if (frameProvider == null
                || poseSource == null
                || arCamera == null
                || !frameProvider.TryUpdateFrame()
                || frameProvider.CameraTexture == null)
            {
                return;
            }

            Texture2D cameraTexture = frameProvider.CameraTexture;
            EnsureDetector(cameraTexture.width, cameraTexture.height);

            int pixelCount = cameraTexture.width * cameraTexture.height;
            if (pixelBuffer == null || pixelBuffer.Length != pixelCount)
            {
                pixelBuffer = new Color32[pixelCount];
            }

            var rawPixels = cameraTexture.GetRawTextureData<byte>();
            if (cameraTexture.format == TextureFormat.RGB24
                && rawPixels.Length >= pixelCount * 3)
            {
                for (int i = 0; i < pixelCount; i++)
                {
                    int source = i * 3;
                    pixelBuffer[i] = new Color32(
                        rawPixels[source],
                        rawPixels[source + 1],
                        rawPixels[source + 2],
                        255);
                }
            }
            else
            {
                pixelBuffer = cameraTexture.GetPixels32();
            }
            if (!frameProvider.TryGetDetectorVerticalFovRadians(arCamera, out float fovRadians))
            {
                return;
            }

            detector.ProcessImage(
                pixelBuffer,
                fovRadians,
                tagSizeMeters);

            int detectedTagCount = 0;
            foreach (AprilTag.TagPose tag in detector.DetectedTags)
            {
                detectedTagCount++;
                Pose worldPose = CameraLocalPoseToWorldPose(tag.Position, tag.Rotation);
                poseSource.PublishTagPose(tag.ID, worldPose);

                if (logDetectedTags)
                {
                    Debug.Log($"Keijiro AprilTag: id={tag.ID}, world={worldPose.position}", this);
                }
            }

            RecordEvaluationSample(processingStartSeconds, detectedTagCount);
        }

        private void RecordEvaluationSample(
            double processingStartSeconds,
            int detectedTagCount)
        {
            if (!logRuntimeEvaluation)
            {
                return;
            }

            evaluationProcessedFrameCount++;
            if (detectedTagCount > 0)
            {
                evaluationTagVisibleFrameCount++;
            }

            evaluationPublishedPoseCount += Mathf.Max(0, detectedTagCount);
            evaluationLatenciesMs.Add((float)(
                (Time.realtimeSinceStartupAsDouble - processingStartSeconds) * 1000.0));
            LogEvaluationWindow(false);
        }

        private void ResetEvaluationWindow()
        {
            evaluationWindowStartSeconds = Time.realtimeSinceStartupAsDouble;
            evaluationProcessedFrameCount = 0;
            evaluationTagVisibleFrameCount = 0;
            evaluationPublishedPoseCount = 0;
            evaluationLatenciesMs.Clear();
        }

        private void LogEvaluationWindow(bool force)
        {
            if (!logRuntimeEvaluation || evaluationProcessedFrameCount == 0)
            {
                return;
            }

            double durationSeconds = Time.realtimeSinceStartupAsDouble
                - evaluationWindowStartSeconds;
            if (!force
                && durationSeconds < Mathf.Max(5f, evaluationWindowSeconds))
            {
                return;
            }

            evaluationLatenciesMs.Sort();
            float latencyMeanMs = 0f;
            for (int i = 0; i < evaluationLatenciesMs.Count; i++)
            {
                latencyMeanMs += evaluationLatenciesMs[i];
            }

            latencyMeanMs /= Mathf.Max(1, evaluationLatenciesMs.Count);
            float processingRateHz = (float)(evaluationProcessedFrameCount
                / Math.Max(0.001, durationSeconds));
            float poseUpdateRateHz = (float)(evaluationPublishedPoseCount
                / Math.Max(0.001, durationSeconds));
            float tagVisibleFrameRate = 100f * evaluationTagVisibleFrameCount
                / Mathf.Max(1, evaluationProcessedFrameCount);
            Debug.Log(
                $"APRILTAG_DETECTOR_EVAL duration={durationSeconds:F2}s, "
                + $"processedFrames={evaluationProcessedFrameCount}, "
                + $"rate={processingRateHz:F2}Hz, "
                + $"processingMean={latencyMeanMs:F1}ms, "
                + $"processingMedian={Percentile(evaluationLatenciesMs, 0.5f):F1}ms, "
                + $"processingP95={Percentile(evaluationLatenciesMs, 0.95f):F1}ms, "
                + $"tagVisibleFrames={evaluationTagVisibleFrameCount}, "
                + $"tagVisibleRate={tagVisibleFrameRate:F1}%, "
                + $"publishedPoses={evaluationPublishedPoseCount}, "
                + $"poseUpdateRate={poseUpdateRateHz:F2}Hz",
                this);
            ResetEvaluationWindow();
        }

        private static float Percentile(List<float> sortedValues, float percentile)
        {
            if (sortedValues == null || sortedValues.Count == 0)
            {
                return 0f;
            }

            int index = Mathf.Clamp(
                Mathf.CeilToInt(percentile * sortedValues.Count) - 1,
                0,
                sortedValues.Count - 1);
            return sortedValues[index];
        }

        private void EnsureDetector(int width, int height)
        {
            if (detector != null
                && detectorWidth == width
                && detectorHeight == height)
            {
                return;
            }

            detector?.Dispose();
            detector = new TagDetector(width, height, decimation);
            detectorWidth = width;
            detectorHeight = height;
        }

        private Pose CameraLocalPoseToWorldPose(
            Vector3 cameraLocalPosition,
            Quaternion cameraLocalRotation)
        {
            Pose detectorPose = new Pose(cameraLocalPosition, cameraLocalRotation);
            Pose cameraPose = compensateFrameRotation && frameProvider != null
                ? frameProvider.DetectorLocalPoseToCameraLocalPose(detectorPose)
                : detectorPose;
            Quaternion manualCorrection = Quaternion.Euler(
                detectorToCameraRotationCorrectionEuler);
            cameraPose = new Pose(
                manualCorrection * cameraPose.position,
                manualCorrection * cameraPose.rotation);

            Transform cameraTransform = arCamera.transform;
            Vector3 worldPosition = cameraTransform.TransformPoint(cameraPose.position);
            Quaternion worldRotation = cameraTransform.rotation * cameraPose.rotation;
            return new Pose(worldPosition, worldRotation);
        }

        private void OnDisable()
        {
            LogEvaluationWindow(true);
            detector?.Dispose();
            detector = null;
            pixelBuffer = null;
        }

        private void OnDestroy()
        {
            detector?.Dispose();
            detector = null;
        }
    }
}
