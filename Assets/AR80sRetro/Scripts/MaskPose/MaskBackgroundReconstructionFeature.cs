using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace AR80sRetro
{
    public sealed class MaskBackgroundReconstructionFeature : ScriptableRendererFeature
    {
        private sealed class ReconstructionPass : ScriptableRenderPass
        {
            private readonly ProfilingSampler profilingSampler =
                new ProfilingSampler("YOLO Mask Background Reconstruction");
            private RTHandle temporaryColor;
            private Material material;

            public ReconstructionPass()
            {
                // AR Foundation renders the camera image at BeforeRenderingOpaques.
                // This feature is deliberately listed after ARBackgroundRendererFeature,
                // so an equal event runs after the camera background but before all
                // opaque virtual models: camera -> removal -> replacement.
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
                requiresIntermediateTexture = true;
            }

            public void Setup(Material activeMaterial)
            {
                material = activeMaterial;
            }

#pragma warning disable 618, 672
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                RenderingUtils.ReAllocateIfNeeded(
                    ref temporaryColor,
                    descriptor,
                    FilterMode.Bilinear,
                    TextureWrapMode.Clamp,
                    name: "_MaskReconstructedCameraColor");
            }

            public override void Execute(
                ScriptableRenderContext context,
                ref RenderingData renderingData)
            {
                if (material == null || temporaryColor == null)
                {
                    return;
                }

                RTHandle cameraColor = renderingData.cameraData.renderer.cameraColorTargetHandle;
                CommandBuffer cmd = CommandBufferPool.Get("YOLO Mask Background Reconstruction");
                using (new ProfilingScope(cmd, profilingSampler))
                {
                    Blitter.BlitCameraTexture(cmd, cameraColor, temporaryColor, material, 0);
                    Blitter.BlitCameraTexture(cmd, temporaryColor, cameraColor);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore 618, 672

            public void Dispose()
            {
                temporaryColor?.Release();
                temporaryColor = null;
            }
        }

        [SerializeField] private Shader inpaintShader;

        private ReconstructionPass reconstructionPass;
        private Material reconstructionMaterial;

        public Shader InpaintShader
        {
            get => inpaintShader;
            set => inpaintShader = value;
        }

        public override void Create()
        {
            reconstructionPass ??= new ReconstructionPass();
            CoreUtils.Destroy(reconstructionMaterial);
            Shader shader = inpaintShader != null
                ? inpaintShader
                : Shader.Find("Hidden/AR80sRetro/MaskBackgroundReconstruction");
            reconstructionMaterial = shader != null
                ? CoreUtils.CreateEngineMaterial(shader)
                : null;
        }

#pragma warning disable 618, 672
        public override void AddRenderPasses(
            ScriptableRenderer renderer,
            ref RenderingData renderingData)
        {
            Camera camera = renderingData.cameraData.camera;
            if (camera == null
                || camera.cameraType != CameraType.Game
                || reconstructionMaterial == null
                || !MaskBackgroundReconstructionController.TryGetActive(
                    camera,
                    out MaskBackgroundReconstructionController controller)
                || !controller.HasVisibleMask)
            {
                return;
            }

            controller.ApplyShaderGlobals();
            reconstructionPass.Setup(reconstructionMaterial);
            renderer.EnqueuePass(reconstructionPass);
        }
#pragma warning restore 618, 672

        protected override void Dispose(bool disposing)
        {
            reconstructionPass?.Dispose();
            CoreUtils.Destroy(reconstructionMaterial);
            reconstructionMaterial = null;
        }
    }
}
