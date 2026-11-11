using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace DynamicGI
{
    /// <summary>
    /// URP renderer feature that adds the voxel GI contribution to the opaque
    /// camera colour. The heavy work (voxelization / bounces) is owned by
    /// DynamicGIController; this pass only integrates the volume into the frame.
    /// </summary>
    public sealed class DynamicGIRendererFeature : ScriptableRendererFeature
    {
        public static DynamicGIRendererFeature Instance { get; private set; }

        private CompositePass m_Pass;
        private Material m_Material;

        public override void Create()
        {
            Instance = this;
            var shader = Resources.Load<Shader>("DynamicGIComposite");
            if (!shader)
            {
                Debug.LogError("[Dynamic GI] Resources/DynamicGIComposite.shader was not found.");
                return;
            }
            m_Material = CoreUtils.CreateEngineMaterial(shader);
            m_Pass = new CompositePass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques,
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var controller = DynamicGIController.Active;
            if (!m_Material || m_Pass == null || controller == null) return;
            if (!controller.enableGI || !controller.IsReady) return;

            var cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection) return;
            if (UniversalRenderer.IsOffscreenDepthTexture(ref renderingData.cameraData)) return;

            m_Pass.Setup(m_Material, controller);
            m_Pass.ConfigureInput(ScriptableRenderPassInput.Depth);
            m_Pass.requiresIntermediateTexture = true;
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
            m_Pass = null;
        }

        private sealed class PassData
        {
            public Material material;
            public TextureHandle source;
            public TextureHandle destination;
            public TextureHandle depth;
        }

        private sealed class CompositePass : ScriptableRenderPass
        {
            private static readonly ProfilingSampler s_Sampler = new ProfilingSampler("Dynamic GI Composite");
            private static readonly int s_DepthTexture = Shader.PropertyToID("_CameraDepthTexture");

            private Material m_Material;
            private DynamicGIController m_Controller;

            public void Setup(Material material, DynamicGIController controller)
            {
                m_Material = material;
                m_Controller = controller;
                profilingSampler = s_Sampler;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (!resources.activeColorTexture.IsValid()) return;
                if (m_Material == null || m_Controller == null || !m_Controller.IsReady) return;

                var cameraData = frameData.Get<UniversalCameraData>();
                m_Controller.SetCameraPosition(cameraData.camera.transform.position);
                m_Controller.ApplyToMaterial(m_Material);

                var color = resources.activeColorTexture;
                var copyDesc = renderGraph.GetTextureDesc(color);
                copyDesc.name = "_DynamicGIColorCopy";
                copyDesc.clearBuffer = false;
                var copy = renderGraph.CreateTexture(copyDesc);
                renderGraph.AddBlitPass(color, copy, Vector2.one, Vector2.zero, passName: "Dynamic GI Copy Color");

                var depth = resources.cameraDepthTexture;

                using (var builder = renderGraph.AddRasterRenderPass<PassData>("Dynamic GI Composite", out var passData, s_Sampler))
                {
                    passData.material = m_Material;
                    passData.source = copy;
                    passData.destination = color;
                    passData.depth = depth;

                    builder.UseTexture(copy, AccessFlags.Read);
                    if (depth.IsValid()) builder.UseTexture(depth, AccessFlags.Read);
                    builder.SetRenderAttachment(color, 0, AccessFlags.Write);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);

                    builder.SetRenderFunc(static (PassData data, RasterGraphContext ctx) =>
                    {
                        if (data.depth.IsValid())
                            ctx.cmd.SetGlobalTexture(s_DepthTexture, data.depth);

                        bool yflip = ctx.GetTextureUVOrigin(data.source) != ctx.GetTextureUVOrigin(data.destination);
                        Vector4 scaleBias = yflip
                            ? new Vector4(1f, -1f, 0f, 1f)
                            : new Vector4(1f, 1f, 0f, 0f);
                        Blitter.BlitTexture(ctx.cmd, data.source, scaleBias, data.material, 0);
                    });
                }
            }
        }
    }
}
