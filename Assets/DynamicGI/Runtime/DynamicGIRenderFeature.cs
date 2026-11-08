using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace DynamicGI
{
    // The feature only composites reflected radiance. It does not alter lights,
    // material parameters, exposure or the fixture's geometry.
    public sealed class DynamicGIRenderFeature : ScriptableRendererFeature
    {
        [SerializeField] private Shader compositeShader;
        private Material material;
        private GIPass pass;

        public override void Create()
        {
            if (!compositeShader) compositeShader = Shader.Find("Hidden/DynamicGI/Composite");
            if (compositeShader && !material) material = CoreUtils.CreateEngineMaterial(compositeShader);
            pass = new GIPass { renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing };
            pass.ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var gi = DynamicGIController.Instance;
            if (!gi || !gi.GIEnabled || !gi.Output || !material ||
                renderingData.cameraData.cameraType != CameraType.Game ||
                (gi.TargetCamera && renderingData.cameraData.camera != gi.TargetCamera)) return;
            pass.Setup(material, gi.Output, gi.Intensity, gi.IndirectOnly);
            renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            pass?.Release();
            CoreUtils.Destroy(material);
            material = null;
        }

        private sealed class GIPass : ScriptableRenderPass
        {
            private Material material;
            private RenderTexture texture;
            private RTHandle handle;
            private float intensity;
            private int shaderPass;
            private readonly ProfilingSampler sampler = new ProfilingSampler("Dynamic GI composite");

            public void Setup(Material mat, RenderTexture output, float value, bool only)
            {
                material = mat; intensity = value; shaderPass = only ? 1 : 0;
                if (texture != output)
                {
                    handle?.Release();
                    texture = output;
                    handle = RTHandles.Alloc(output);
                }
            }

            public void Release() { handle?.Release(); handle = null; texture = null; }

            private sealed class PassData
            {
                public Material material;
                public Texture texture;
                public float intensity;
                public int shaderPass;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (!resources.cameraDepthTexture.IsValid() || handle == null) return;
                var imported = graph.ImportTexture(handle);
                using (var builder = graph.AddRasterRenderPass<PassData>("Dynamic GI composite", out var data, sampler))
                {
                    data.material = material; data.texture = texture;
                    data.intensity = intensity; data.shaderPass = shaderPass;
                    builder.UseTexture(imported, AccessFlags.Read);
                    builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                    // ReadWrite preserves the existing color for hardware addition.
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderFunc(static (PassData d, RasterGraphContext context) =>
                    {
                        var properties = new MaterialPropertyBlock();
                        properties.SetTexture("_GITexture", d.texture);
                        properties.SetVector("_GITexture_TexelSize", new Vector4(1f/d.texture.width, 1f/d.texture.height, d.texture.width, d.texture.height));
                        properties.SetFloat("_GIIntensity", d.intensity);
                        properties.SetVector("_BlitScaleBias", new Vector4(1,1,0,0));
                        context.cmd.DrawProcedural(Matrix4x4.identity, d.material, d.shaderPass, MeshTopology.Triangles, 3, 1, properties);
                    });
                }
            }
        }
    }
}

