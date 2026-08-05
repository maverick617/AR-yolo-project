using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace TagModelAR
{
    /// <summary>
    /// Converts the latest AR camera CPU image into the portrait RGB texture
    /// consumed by the AprilTag detector.
    /// </summary>
    public sealed class ARCameraFrameProvider : MonoBehaviour
    {
        public enum FrameRotation
        {
            None,
            Clockwise90,
            CounterClockwise90
        }

        [SerializeField] private ARCameraManager cameraManager;
        [SerializeField, Min(64)] private int outputWidth = 640;
        [SerializeField, Min(64)] private int outputHeight = 480;
        [SerializeField] private FrameRotation frameRotation =
            FrameRotation.Clockwise90;
        [SerializeField] private bool centerCropToOutputAspect = true;
        [SerializeField] private bool lockToPortrait = true;

        private Texture2D cameraTexture;
        private NativeArray<byte> conversionBuffer;
        private byte[] rotatedPixels;
        private int lastUpdatedFrame = -1;
        private RectInt lastInputRect;
        private Vector2Int lastSourceDimensions;

        public Texture2D CameraTexture => cameraTexture;
        public Pose FrameCameraWorldPose { get; private set; }
        public bool HasFrameCameraWorldPose { get; private set; }
        public FrameRotation AppliedFrameRotation => frameRotation;

        private void Awake()
        {
            if (!lockToPortrait)
            {
                return;
            }

            Screen.autorotateToPortrait = true;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = false;
            Screen.autorotateToLandscapeRight = false;
            Screen.orientation = ScreenOrientation.Portrait;
        }

        private void Reset()
        {
            cameraManager = FindObjectOfType<ARCameraManager>();
        }

        private void OnEnable()
        {
            if (cameraManager == null)
            {
                cameraManager = FindObjectOfType<ARCameraManager>();
            }
        }

        public bool TryUpdateFrame()
        {
            if (lastUpdatedFrame == Time.frameCount)
            {
                return cameraTexture != null;
            }

            lastUpdatedFrame = Time.frameCount;
            HasFrameCameraWorldPose = false;
            if (cameraManager == null
                || !cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                return false;
            }

            using (image)
            {
                Camera arCamera = cameraManager.GetComponent<Camera>();
                if (arCamera != null)
                {
                    FrameCameraWorldPose = new Pose(
                        arCamera.transform.position,
                        arCamera.transform.rotation);
                    HasFrameCameraWorldPose = true;
                }

                lastSourceDimensions = new Vector2Int(image.width, image.height);
                lastInputRect = CalculateInputRect(image.width, image.height);
                var conversion = new XRCpuImage.ConversionParams
                {
                    inputRect = lastInputRect,
                    outputDimensions = new Vector2Int(outputWidth, outputHeight),
                    outputFormat = TextureFormat.RGB24,
                    transformation = XRCpuImage.Transformation.MirrorY
                };

                EnsureConversionBuffer(image.GetConvertedDataSize(conversion));
                image.Convert(conversion, conversionBuffer);
                UploadFrame(conversionBuffer);
            }

            return cameraTexture != null;
        }

        /// <summary>
        /// Returns the vertical field of view of the rotated/cropped detector
        /// image. The AprilTag package uses this value for metric pose recovery.
        /// </summary>
        public bool TryGetDetectorVerticalFovRadians(
            Camera fallbackCamera,
            out float fovRadians)
        {
            if (cameraManager != null
                && cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                bool rotated = frameRotation != FrameRotation.None;
                float cropSpan = rotated
                    ? Mathf.Max(1f, lastInputRect.width)
                    : Mathf.Max(1f, lastInputRect.height);
                float sourceSpan = rotated
                    ? Mathf.Max(1f, lastSourceDimensions.x)
                    : Mathf.Max(1f, lastSourceDimensions.y);
                float intrinsicSpan = rotated
                    ? Mathf.Max(1f, intrinsics.resolution.x)
                    : Mathf.Max(1f, intrinsics.resolution.y);
                float focalLength = rotated
                    ? intrinsics.focalLength.x
                    : intrinsics.focalLength.y;

                if (focalLength > 0.001f)
                {
                    float cropInIntrinsicPixels = cropSpan
                        * intrinsicSpan / sourceSpan;
                    fovRadians = 2f * Mathf.Atan(
                        cropInIntrinsicPixels / (2f * focalLength));
                    return true;
                }
            }

            if (fallbackCamera != null)
            {
                fovRadians = fallbackCamera.fieldOfView * Mathf.Deg2Rad;
                return true;
            }

            fovRadians = 0f;
            return false;
        }

        public Pose DetectorLocalPoseToCameraLocalPose(Pose detectorPose)
        {
            Quaternion correction = frameRotation switch
            {
                FrameRotation.Clockwise90 =>
                    Quaternion.AngleAxis(90f, Vector3.forward),
                FrameRotation.CounterClockwise90 =>
                    Quaternion.AngleAxis(-90f, Vector3.forward),
                _ => Quaternion.identity
            };

            return new Pose(
                correction * detectorPose.position,
                correction * detectorPose.rotation);
        }

        private RectInt CalculateInputRect(int sourceWidth, int sourceHeight)
        {
            if (!centerCropToOutputAspect)
            {
                return new RectInt(0, 0, sourceWidth, sourceHeight);
            }

            float targetAspect = outputWidth / (float)outputHeight;
            float sourceAspect = sourceWidth / (float)sourceHeight;
            if (sourceAspect > targetAspect)
            {
                int width = Mathf.Max(
                    1,
                    Mathf.RoundToInt(sourceHeight * targetAspect));
                return new RectInt((sourceWidth - width) / 2, 0, width, sourceHeight);
            }

            int height = Mathf.Max(
                1,
                Mathf.RoundToInt(sourceWidth / targetAspect));
            return new RectInt(0, (sourceHeight - height) / 2, sourceWidth, height);
        }

        private void UploadFrame(NativeArray<byte> source)
        {
            if (frameRotation == FrameRotation.None)
            {
                EnsureTexture(outputWidth, outputHeight);
                cameraTexture.LoadRawTextureData(source);
                cameraTexture.Apply(false, false);
                return;
            }

            int rotatedWidth = outputHeight;
            int rotatedHeight = outputWidth;
            int byteCount = rotatedWidth * rotatedHeight * 3;
            if (rotatedPixels == null || rotatedPixels.Length != byteCount)
            {
                rotatedPixels = new byte[byteCount];
            }

            NativeArray<byte>.ReadOnly readOnlySource = source.AsReadOnly();
            for (int sourceY = 0; sourceY < outputHeight; sourceY++)
            {
                for (int sourceX = 0; sourceX < outputWidth; sourceX++)
                {
                    int destinationX;
                    int destinationY;
                    if (frameRotation == FrameRotation.Clockwise90)
                    {
                        destinationX = outputHeight - 1 - sourceY;
                        destinationY = sourceX;
                    }
                    else
                    {
                        destinationX = sourceY;
                        destinationY = outputWidth - 1 - sourceX;
                    }

                    int sourceIndex = (sourceY * outputWidth + sourceX) * 3;
                    int destinationIndex =
                        (destinationY * rotatedWidth + destinationX) * 3;
                    rotatedPixels[destinationIndex] = readOnlySource[sourceIndex];
                    rotatedPixels[destinationIndex + 1] =
                        readOnlySource[sourceIndex + 1];
                    rotatedPixels[destinationIndex + 2] =
                        readOnlySource[sourceIndex + 2];
                }
            }

            EnsureTexture(rotatedWidth, rotatedHeight);
            cameraTexture.LoadRawTextureData(rotatedPixels);
            cameraTexture.Apply(false, false);
        }

        private void EnsureTexture(int width, int height)
        {
            if (cameraTexture != null
                && cameraTexture.width == width
                && cameraTexture.height == height)
            {
                return;
            }

            if (cameraTexture != null)
            {
                Destroy(cameraTexture);
            }

            cameraTexture = new Texture2D(
                width,
                height,
                TextureFormat.RGB24,
                false)
            {
                name = "AprilTag Camera Frame"
            };
        }

        private void EnsureConversionBuffer(int size)
        {
            if (conversionBuffer.IsCreated && conversionBuffer.Length == size)
            {
                return;
            }

            if (conversionBuffer.IsCreated)
            {
                conversionBuffer.Dispose();
            }

            conversionBuffer = new NativeArray<byte>(
                size,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        private void OnDestroy()
        {
            if (cameraTexture != null)
            {
                Destroy(cameraTexture);
            }

            if (conversionBuffer.IsCreated)
            {
                conversionBuffer.Dispose();
            }
        }
    }
}
