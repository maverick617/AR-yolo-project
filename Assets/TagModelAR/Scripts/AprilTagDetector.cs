using AprilTag;
using UnityEngine;

namespace TagModelAR
{
    /// <summary>Detects tagStandard41h12 markers and publishes world poses.</summary>
    public sealed class AprilTagDetector : MonoBehaviour
    {
        [SerializeField] private ARCameraFrameProvider frameProvider;
        [SerializeField] private AprilTagPoseSource poseSource;
        [SerializeField] private Camera arCamera;
        [SerializeField, Min(0.005f)] private float tagSizeMeters = 0.03f;
        [SerializeField, Min(1)] private int decimation = 1;
        [SerializeField, Min(0.02f)] private float detectionIntervalSeconds = 0.05f;
        [SerializeField] private bool compensateFrameRotation;
        [SerializeField, Range(0f, 0.5f)] private float minimumFrontAlignment = 0.2f;

        private TagDetector detector;
        private int detectorWidth;
        private int detectorHeight;
        private Color32[] pixels;
        private float nextDetectionTime;

        public float TagSizeMeters => Mathf.Max(0.005f, tagSizeMeters);

        public void SetTagSizeMeters(float value)
        {
            tagSizeMeters = Mathf.Max(0.005f, value);
        }

        private void Reset()
        {
            ResolveReferences();
        }

        private void Awake()
        {
            ResolveReferences();
        }

        private void ResolveReferences()
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
                || frameProvider.CameraTexture == null
                || !frameProvider.TryGetDetectorVerticalFovRadians(
                    arCamera,
                    out float fovRadians))
            {
                return;
            }

            Texture2D texture = frameProvider.CameraTexture;
            EnsureDetector(texture.width, texture.height);
            int pixelCount = texture.width * texture.height;
            if (pixels == null || pixels.Length != pixelCount)
            {
                pixels = new Color32[pixelCount];
            }

            var raw = texture.GetRawTextureData<byte>();
            for (int i = 0; i < pixelCount; i++)
            {
                int source = i * 3;
                pixels[i] = new Color32(
                    raw[source],
                    raw[source + 1],
                    raw[source + 2],
                    255);
            }

            detector.ProcessImage(pixels, fovRadians, TagSizeMeters);
            foreach (TagPose tag in detector.DetectedTags)
            {
                Pose worldPose = ToWorldPose(tag.Position, tag.Rotation);
                Vector3 cameraPosition = frameProvider.HasFrameCameraWorldPose
                    ? frameProvider.FrameCameraWorldPose.position
                    : arCamera.transform.position;
                Vector3 tagToCamera = cameraPosition - worldPose.position;
                if (tagToCamera.sqrMagnitude < 0.000001f)
                {
                    continue;
                }

                // In this detector convention the printed front normal is -Z.
                Vector3 front = -(worldPose.rotation * Vector3.forward);
                if (Vector3.Dot(front, tagToCamera.normalized)
                    < minimumFrontAlignment)
                {
                    continue;
                }

                poseSource.PublishTagPose(tag.ID, worldPose);
            }
        }

        private Pose ToWorldPose(Vector3 localPosition, Quaternion localRotation)
        {
            Pose cameraLocalPose = new Pose(localPosition, localRotation);
            if (compensateFrameRotation)
            {
                cameraLocalPose = frameProvider
                    .DetectorLocalPoseToCameraLocalPose(cameraLocalPose);
            }

            Pose cameraWorldPose = frameProvider.HasFrameCameraWorldPose
                ? frameProvider.FrameCameraWorldPose
                : new Pose(arCamera.transform.position, arCamera.transform.rotation);
            return new Pose(
                cameraWorldPose.position
                    + cameraWorldPose.rotation * cameraLocalPose.position,
                cameraWorldPose.rotation * cameraLocalPose.rotation);
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

        private void OnDisable()
        {
            detector?.Dispose();
            detector = null;
            pixels = null;
        }

        private void OnDestroy()
        {
            detector?.Dispose();
        }
    }
}
