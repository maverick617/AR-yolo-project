using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace AR80sRetro
{
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
        [SerializeField, Min(64)] private int outputHeight = 640;
        [SerializeField] private TextureFormat outputFormat = TextureFormat.RGB24;
        [SerializeField] private FrameRotation frameRotation = FrameRotation.Clockwise90;
        [SerializeField] private bool centerCropToOutputAspect = true;
        [Tooltip("The cup demo uses one calibrated portrait camera transform. Keep this enabled until all display orientations have separate calibration.")]
        [SerializeField] private bool lockToPortrait = true;
        [SerializeField] private Vector2 screenOffsetNormalized;
        [SerializeField] private Vector2 screenScale = Vector2.one;

        private Texture2D cameraTexture;
        private Texture2D unrotatedTexture;
        private byte[] rotatedPixels;
        private NativeArray<byte> conversionBuffer;
        private int lastUpdatedUnityFrame = -1;
        private RectInt lastInputRect;

        public Texture2D CameraTexture => cameraTexture;
        public bool HasFrame { get; private set; }
        public double FrameTimestampSeconds { get; private set; }
        public FrameRotation AppliedFrameRotation => frameRotation;
        public event Action<Texture2D> FrameReady;

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

        public Rect ImageRectToScreenRect(Rect imageRect)
        {
            if (cameraTexture == null || Screen.width <= 0 || Screen.height <= 0)
            {
                return imageRect;
            }

            float scale = Mathf.Max(
                (float)Screen.width / cameraTexture.width,
                (float)Screen.height / cameraTexture.height);
            float renderedWidth = cameraTexture.width * scale;
            float renderedHeight = cameraTexture.height * scale;
            float croppedX = (renderedWidth - Screen.width) * 0.5f;
            float croppedY = (renderedHeight - Screen.height) * 0.5f;

            float xMin = (imageRect.xMin * renderedWidth - croppedX) / Screen.width;
            float xMax = (imageRect.xMax * renderedWidth - croppedX) / Screen.width;
            float yMin = (imageRect.yMin * renderedHeight - croppedY) / Screen.height;
            float yMax = (imageRect.yMax * renderedHeight - croppedY) / Screen.height;

            Vector2 center = new Vector2((xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f);
            Vector2 size = new Vector2(xMax - xMin, yMax - yMin);
            Vector2 effectiveScreenScale = screenScale;
            if (effectiveScreenScale.x <= 0f || effectiveScreenScale.y <= 0f)
            {
                effectiveScreenScale = Vector2.one;
            }

            center = Vector2.Scale(center - new Vector2(0.5f, 0.5f), effectiveScreenScale)
                + new Vector2(0.5f, 0.5f)
                + screenOffsetNormalized;
            size = Vector2.Scale(size, effectiveScreenScale);

            return Rect.MinMaxRect(
                Mathf.Clamp01(center.x - size.x * 0.5f),
                Mathf.Clamp01(center.y - size.y * 0.5f),
                Mathf.Clamp01(center.x + size.x * 0.5f),
                Mathf.Clamp01(center.y + size.y * 0.5f));
        }

        private void Reset()
        {
            cameraManager = FindObjectOfType<ARCameraManager>();
        }

        public bool TryUpdateFrame()
        {
            if (lastUpdatedUnityFrame == Time.frameCount)
            {
                return HasFrame;
            }

            lastUpdatedUnityFrame = Time.frameCount;
            HasFrame = false;

            if (cameraManager == null || !cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                return false;
            }

            using (image)
            {
                lastInputRect = CalculateInputRect(image.width, image.height);
                XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams
                {
                    inputRect = lastInputRect,
                    outputDimensions = new Vector2Int(outputWidth, outputHeight),
                    outputFormat = outputFormat,
                    transformation = XRCpuImage.Transformation.MirrorY
                };

                int dataSize = image.GetConvertedDataSize(conversionParams);
                if (!conversionBuffer.IsCreated || conversionBuffer.Length != dataSize)
                {
                    if (conversionBuffer.IsCreated)
                    {
                        conversionBuffer.Dispose();
                    }

                    conversionBuffer = new NativeArray<byte>(
                        dataSize,
                        Allocator.Persistent,
                        NativeArrayOptions.UninitializedMemory);
                }

                image.Convert(conversionParams, conversionBuffer);
                UploadConvertedFrame(conversionBuffer);
            }

            HasFrame = true;
            FrameTimestampSeconds = Time.realtimeSinceStartupAsDouble;
            FrameReady?.Invoke(cameraTexture);
            return true;
        }

        public bool TryGetDetectorVerticalFovRadians(Camera fallbackCamera, out float fovRadians)
        {
            if (cameraManager != null
                && cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                bool rotated = frameRotation != FrameRotation.None;
                float sourceSpan = rotated
                    ? Mathf.Max(1f, lastInputRect.width)
                    : Mathf.Max(1f, lastInputRect.height);
                float focalLength = rotated
                    ? intrinsics.focalLength.x
                    : intrinsics.focalLength.y;
                if (focalLength > 0.001f)
                {
                    fovRadians = 2f * Mathf.Atan(sourceSpan / (2f * focalLength));
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
            Quaternion imageToCameraRotation;
            switch (frameRotation)
            {
                case FrameRotation.Clockwise90:
                    imageToCameraRotation = Quaternion.AngleAxis(90f, Vector3.forward);
                    break;
                case FrameRotation.CounterClockwise90:
                    imageToCameraRotation = Quaternion.AngleAxis(-90f, Vector3.forward);
                    break;
                default:
                    imageToCameraRotation = Quaternion.identity;
                    break;
            }

            return new Pose(
                imageToCameraRotation * detectorPose.position,
                imageToCameraRotation * detectorPose.rotation);
        }

        private RectInt CalculateInputRect(int sourceWidth, int sourceHeight)
        {
            if (!centerCropToOutputAspect || outputWidth <= 0 || outputHeight <= 0)
            {
                return new RectInt(0, 0, sourceWidth, sourceHeight);
            }

            float targetAspect = outputWidth / (float)outputHeight;
            float sourceAspect = sourceWidth / (float)sourceHeight;
            if (sourceAspect > targetAspect)
            {
                int croppedWidth = Mathf.Max(1, Mathf.RoundToInt(sourceHeight * targetAspect));
                return new RectInt((sourceWidth - croppedWidth) / 2, 0, croppedWidth, sourceHeight);
            }

            int croppedHeight = Mathf.Max(1, Mathf.RoundToInt(sourceWidth / targetAspect));
            return new RectInt(0, (sourceHeight - croppedHeight) / 2, sourceWidth, croppedHeight);
        }

        private void UploadConvertedFrame(NativeArray<byte> buffer)
        {
            if (frameRotation == FrameRotation.None)
            {
                EnsureTexture(ref cameraTexture, outputWidth, outputHeight, "AR Camera CPU Frame");
                cameraTexture.LoadRawTextureData(buffer);
                cameraTexture.Apply(false, false);
                return;
            }

            if (outputFormat != TextureFormat.RGB24)
            {
                Debug.LogError("Frame rotation currently requires RGB24 output.", this);
                return;
            }

            EnsureTexture(ref unrotatedTexture, outputWidth, outputHeight, "AR Camera CPU Frame Raw");
            unrotatedTexture.LoadRawTextureData(buffer);
            unrotatedTexture.Apply(false, false);

            int rotatedWidth = outputHeight;
            int rotatedHeight = outputWidth;
            int byteCount = rotatedWidth * rotatedHeight * 3;
            if (rotatedPixels == null || rotatedPixels.Length != byteCount)
            {
                rotatedPixels = new byte[byteCount];
            }

            NativeArray<byte>.ReadOnly source = buffer.AsReadOnly();
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
                    int destinationIndex = (destinationY * rotatedWidth + destinationX) * 3;
                    rotatedPixels[destinationIndex] = source[sourceIndex];
                    rotatedPixels[destinationIndex + 1] = source[sourceIndex + 1];
                    rotatedPixels[destinationIndex + 2] = source[sourceIndex + 2];
                }
            }

            EnsureTexture(ref cameraTexture, rotatedWidth, rotatedHeight, "AR Camera CPU Frame Rotated");
            cameraTexture.LoadRawTextureData(rotatedPixels);
            cameraTexture.Apply(false, false);
        }

        private void EnsureTexture(
            ref Texture2D texture,
            int width,
            int height,
            string textureName)
        {
            if (texture != null
                && texture.width == width
                && texture.height == height
                && texture.format == outputFormat)
            {
                return;
            }

            if (texture != null)
            {
                Destroy(texture);
            }

            texture = new Texture2D(width, height, outputFormat, false);
            texture.name = textureName;
        }

        private void OnDestroy()
        {
            if (cameraTexture != null)
            {
                Destroy(cameraTexture);
            }

            if (unrotatedTexture != null)
            {
                Destroy(unrotatedTexture);
            }

            if (conversionBuffer.IsCreated)
            {
                conversionBuffer.Dispose();
            }
        }
    }
}
