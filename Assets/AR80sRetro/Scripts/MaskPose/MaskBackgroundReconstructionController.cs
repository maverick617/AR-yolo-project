using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Publishes the latest pose-validated YOLO instance mask to the URP
    /// reconstruction pass. This removes the need for a physical CAD mask mesh.
    /// </summary>
    [DefaultExecutionOrder(-70)]
    [DisallowMultipleComponent]
    public sealed class MaskBackgroundReconstructionController : MonoBehaviour
    {
        private static readonly List<MaskBackgroundReconstructionController> ActiveControllers =
            new List<MaskBackgroundReconstructionController>();
        private static readonly int MaskTextureId = Shader.PropertyToID("_ARObjectRemovalMask");
        private static readonly int MaskTexelSizeId = Shader.PropertyToID("_ARObjectRemovalMaskTexelSize");
        private static readonly int RadiusId = Shader.PropertyToID("_ARInpaintRadiusPixels");
        private static readonly int PaddingId = Shader.PropertyToID("_ARMaskPaddingPixels");
        private static readonly int StrengthId = Shader.PropertyToID("_ARReconstructionStrength");

        [SerializeField] private Camera arCamera;
        [SerializeField] private YoloObjectDetector detector;
        [SerializeField, Range(64, 512)] private int maskTextureResolution = 256;
        [SerializeField, Range(8f, 420f)] private float inpaintRadiusPixels = 240f;
        [SerializeField, Range(0f, 32f)] private float maskPaddingPixels = 8f;
        [SerializeField, Range(0f, 1f)] private float reconstructionStrength = 1f;
        [SerializeField, Min(0.05f)] private float maskPersistenceSeconds = 0.28f;

        private Texture2D maskTexture;
        private byte[] maskPixels;
        private float lastMaskTime = float.NegativeInfinity;

        public bool HasVisibleMask => maskTexture != null
            && Time.unscaledTime - lastMaskTime <= maskPersistenceSeconds;

        public static bool TryGetActive(
            Camera camera,
            out MaskBackgroundReconstructionController controller)
        {
            for (int i = 0; i < ActiveControllers.Count; i++)
            {
                MaskBackgroundReconstructionController candidate = ActiveControllers[i];
                if (candidate != null
                    && candidate.isActiveAndEnabled
                    && candidate.arCamera == camera)
                {
                    controller = candidate;
                    return true;
                }
            }

            controller = null;
            return false;
        }

        private void Reset()
        {
            arCamera = GetComponent<Camera>();
            detector = FindObjectOfType<YoloObjectDetector>();
        }

        private void OnEnable()
        {
            if (arCamera == null)
            {
                arCamera = GetComponent<Camera>();
            }

            if (!ActiveControllers.Contains(this))
            {
                ActiveControllers.Add(this);
            }

            if (detector == null)
            {
                detector = FindObjectOfType<YoloObjectDetector>();
            }

            if (detector != null)
            {
                detector.DetectionsReady += HandleDetections;
            }

            EnsureTexture();
            ApplyShaderGlobals();
        }

        private void OnDisable()
        {
            ActiveControllers.Remove(this);
            if (detector != null)
            {
                detector.DetectionsReady -= HandleDetections;
            }

            Shader.SetGlobalTexture(MaskTextureId, Texture2D.blackTexture);
        }

        private void OnDestroy()
        {
            if (maskTexture != null)
            {
                Destroy(maskTexture);
            }
        }

        private void Update()
        {
            ApplyShaderGlobals();
        }

        private void HandleDetections(IReadOnlyList<DetectionResult> detections)
        {
            bool found = false;
            if (detections != null)
            {
                for (int i = 0; i < detections.Count; i++)
                {
                    if (!detections[i].HasMask)
                    {
                        continue;
                    }

                    found = true;
                    break;
                }
            }

            if (!found)
            {
                return;
            }

            EnsureTexture();
            int resolution = maskTexture.width;
            System.Array.Clear(maskPixels, 0, maskPixels.Length);
            // Rasterize only each detection's screen-space bounds. The previous
            // implementation tested every screen pixel against every detection.
            // This cuts CPU mask work drastically on an iPhone while preserving
            // the exact YOLO-seg silhouette.
            for (int detectionIndex = 0;
                detectionIndex < detections.Count;
                detectionIndex++)
            {
                DetectionMask source = detections[detectionIndex].Mask;
                if (source == null)
                {
                    continue;
                }

                Rect box = source.NormalizedScreenBox;
                int minX = Mathf.Clamp(
                    Mathf.FloorToInt(box.xMin * resolution),
                    0,
                    resolution - 1);
                int maxX = Mathf.Clamp(
                    Mathf.CeilToInt(box.xMax * resolution),
                    0,
                    resolution - 1);
                int minY = Mathf.Clamp(
                    Mathf.FloorToInt((1f - box.yMax) * resolution),
                    0,
                    resolution - 1);
                int maxY = Mathf.Clamp(
                    Mathf.CeilToInt((1f - box.yMin) * resolution),
                    0,
                    resolution - 1);
                for (int y = minY; y <= maxY; y++)
                {
                    float topLeftY = 1f - (y + 0.5f) / resolution;
                    for (int x = minX; x <= maxX; x++)
                    {
                        Vector2 point = new Vector2(
                            (x + 0.5f) / resolution,
                            topLeftY);
                        int pixelIndex = y * resolution + x;
                        maskPixels[pixelIndex] = System.Math.Max(
                            maskPixels[pixelIndex],
                            source.SampleTopLeftNormalizedPoint(point));
                    }
                }
            }

            maskTexture.SetPixelData(maskPixels, 0);
            maskTexture.Apply(false, false);
            lastMaskTime = Time.unscaledTime;
            ApplyShaderGlobals();
        }

        public void ApplyShaderGlobals()
        {
            Texture activeMask = HasVisibleMask ? maskTexture : Texture2D.blackTexture;
            Shader.SetGlobalTexture(MaskTextureId, activeMask);
            if (maskTexture != null)
            {
                Shader.SetGlobalVector(
                    MaskTexelSizeId,
                    new Vector4(
                        1f / maskTexture.width,
                        1f / maskTexture.height,
                        maskTexture.width,
                        maskTexture.height));
            }

            Shader.SetGlobalFloat(RadiusId, Mathf.Max(1f, inpaintRadiusPixels));
            Shader.SetGlobalFloat(PaddingId, Mathf.Max(0f, maskPaddingPixels));
            Shader.SetGlobalFloat(StrengthId, Mathf.Clamp01(reconstructionStrength));
        }

        private void EnsureTexture()
        {
            int resolution = Mathf.Clamp(maskTextureResolution, 64, 512);
            if (maskTexture != null
                && maskTexture.width == resolution
                && maskTexture.height == resolution)
            {
                return;
            }

            if (maskTexture != null)
            {
                Destroy(maskTexture);
            }

            maskTexture = new Texture2D(
                resolution,
                resolution,
                TextureFormat.R8,
                false,
                true)
            {
                name = "YOLO Instance Removal Mask",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            maskPixels = new byte[resolution * resolution];
            maskTexture.SetPixelData(maskPixels, 0);
            maskTexture.Apply(false, false);
        }
    }
}
