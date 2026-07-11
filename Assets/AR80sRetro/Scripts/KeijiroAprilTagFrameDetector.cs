using UnityEngine;
using AprilTag;

namespace AR80sRetro
{
    public sealed class KeijiroAprilTagFrameDetector : MonoBehaviour
    {
        [SerializeField] private ARCameraFrameProvider frameProvider;
        [SerializeField] private AprilTagPoseSource poseSource;
        [SerializeField] private Camera arCamera;
        [SerializeField, Min(0.01f)] private float tagSizeMeters = 0.05f;
        [SerializeField, Min(1)] private int decimation = 4;
        [SerializeField, Min(0.02f)] private float detectionIntervalSeconds = 0.08f;
        [SerializeField] private bool logDetectedTags;

        private float nextDetectionTime;
        private TagDetector detector;
        private int detectorWidth;
        private int detectorHeight;

        private void Reset()
        {
            frameProvider = FindObjectOfType<ARCameraFrameProvider>();
            poseSource = FindObjectOfType<AprilTagPoseSource>();
            arCamera = Camera.main;
        }

        private void Update()
        {
            if (Time.time < nextDetectionTime)
            {
                return;
            }

            nextDetectionTime = Time.time + detectionIntervalSeconds;

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

            Color32[] pixels = cameraTexture.GetPixels32();
            detector.ProcessImage(
                pixels,
                arCamera.fieldOfView * Mathf.Deg2Rad,
                tagSizeMeters);

            foreach (AprilTag.TagPose tag in detector.DetectedTags)
            {
                Pose worldPose = CameraLocalPoseToWorldPose(tag.Position, tag.Rotation);
                poseSource.PublishTagPose(tag.ID, worldPose);

                if (logDetectedTags)
                {
                    Debug.Log($"Keijiro AprilTag: id={tag.ID}, world={worldPose.position}", this);
                }
            }
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
            Transform cameraTransform = arCamera.transform;
            Vector3 worldPosition = cameraTransform.TransformPoint(cameraLocalPosition);
            Quaternion worldRotation = cameraTransform.rotation * cameraLocalRotation;
            return new Pose(worldPosition, worldRotation);
        }

        private void OnDisable()
        {
            detector?.Dispose();
            detector = null;
        }

        private void OnDestroy()
        {
            detector?.Dispose();
            detector = null;
        }
    }
}
