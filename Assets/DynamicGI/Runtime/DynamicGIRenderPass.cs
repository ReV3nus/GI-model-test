using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace DynamicGI
{
    // URP integration: after opaques, render a small surface G-buffer (diffuse albedo, normal, position)
    // with an override shader, then add albedo * probe irradiance to the camera colour (indirect diffuse only).
    internal sealed class DynamicGIRenderPass : ScriptableRenderPass
    {
        private readonly DynamicGISystem system;
        private readonly ProfilingSampler albedoSampler = new ProfilingSampler("DynamicGI.Albedo");
        private readonly ProfilingSampler compositeSampler = new ProfilingSampler("DynamicGI.Composite");
        private static readonly ShaderTagId[] Tags = { new ShaderTagId("UniversalForward"), new ShaderTagId("UniversalForwardOnly"), new ShaderTagId("SRPDefaultUnlit") };
        private static readonly int AlbedoId = Shader.PropertyToID("_GIAlbedoTex"), NormalId = Shader.PropertyToID("_GINormalTex"), PositionId = Shader.PropertyToID("_GIPositionTex");
        private readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        private sealed class GBufferData { public RendererListHandle list; }
        private sealed class CompositeData
        {
            public TextureHandle albedo, normal, position;
            public Material material; public int pass; public MaterialPropertyBlock block;
        }

        public DynamicGIRenderPass(DynamicGISystem system)
        {
            this.system = system;
            renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var cameraData = frameData.Get<UniversalCameraData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var lightData = frameData.Get<UniversalLightData>();
            if (resources.isActiveTargetBackBuffer) return;

            var colorDesc = renderGraph.GetTextureDesc(resources.activeColorTexture);
            TextureHandle Make(GraphicsFormat format, string name)
            {
                var d = new TextureDesc(colorDesc.width, colorDesc.height)
                {
                    colorFormat = format, msaaSamples = colorDesc.msaaSamples, clearBuffer = true, clearColor = Color.clear,
                    name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
                };
                return renderGraph.CreateTexture(d);
            }
            var albedo = Make(GraphicsFormat.R8G8B8A8_UNorm, "DynamicGI_Albedo");
            var normal = Make(GraphicsFormat.R16G16B16A16_SFloat, "DynamicGI_Normal");
            var position = Make(GraphicsFormat.R32G32B32A32_SFloat, "DynamicGI_Position");

            using (var builder = renderGraph.AddRasterRenderPass<GBufferData>("DynamicGI Albedo", out var data, albedoSampler))
            {
                var tagList = new System.Collections.Generic.List<ShaderTagId>(Tags);
                var draw = RenderingUtils.CreateDrawingSettings(tagList, renderingData, cameraData, lightData, SortingCriteria.CommonOpaque);
                draw.overrideShader = system.GBufferShader;
                draw.overrideShaderPassIndex = 0;
                var filter = new FilteringSettings(RenderQueueRange.opaque, system.layers);
                data.list = renderGraph.CreateRendererList(new RendererListParams(renderingData.cullResults, draw, filter));
                builder.UseRendererList(data.list);
                builder.SetRenderAttachment(albedo, 0);
                builder.SetRenderAttachment(normal, 1);
                builder.SetRenderAttachment(position, 2);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((GBufferData d, RasterGraphContext ctx) => ctx.cmd.DrawRendererList(d.list));
            }

            using (var builder = renderGraph.AddRasterRenderPass<CompositeData>("DynamicGI Composite", out var data, compositeSampler))
            {
                data.albedo = albedo; data.normal = normal; data.position = position;
                data.material = system.CompositeMaterial;
                data.pass = system.debugView == GIDebugView.Final ? 0 : 1;
                data.block = block;
                builder.UseTexture(albedo); builder.UseTexture(normal); builder.UseTexture(position);
                builder.SetRenderAttachment(resources.activeColorTexture, 0);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((CompositeData d, RasterGraphContext ctx) =>
                {
                    d.block.Clear();
                    d.block.SetTexture(AlbedoId, d.albedo);
                    d.block.SetTexture(NormalId, d.normal);
                    d.block.SetTexture(PositionId, d.position);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.pass, MeshTopology.Triangles, 3, 1, d.block);
                });
            }
        }
    }
}
