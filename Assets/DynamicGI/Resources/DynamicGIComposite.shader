Shader "Hidden/DynamicGI/Composite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "DynamicGIComposite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            Texture3D<float4> _VolumeAlbedo;    // rgb albedo, a occupancy
            Texture3D<float4> _VolumeIndirect;  // rgb indirect irradiance at surface
            Texture3D<float4> _VolumeSky;       // rgb sky irradiance at surface
            Texture3D<float> _OutsideMask;      // 1 = air connected to outdoors

            float4 _VolumeMin;
            float4 _VolumeVoxelSize;
            float4 _VolumeDimsF;
            float  _IndirectIntensity;
            int    _ViewMode;                   // 0 composite, 1 all GI, 2 bounces only, 3 false colour
            float  _VolumeValid;
            float  _GateEnclosed;
            float4 _CameraPositionWS;

            int3 GIDims() { return (int3)_VolumeDimsF.xyz; }

            bool GIInVolume(int3 v)
            {
                return all(v >= 0) && all(v < GIDims());
            }

            int3 GIVoxelIndex(float3 p)
            {
                return (int3)floor((p - _VolumeMin.xyz) / _VolumeVoxelSize.xyz);
            }

            float3 GIHeat(float x)
            {
                float t = saturate(x);
                float3 c0 = float3(0.02, 0.02, 0.10);
                float3 c1 = float3(0.05, 0.35, 0.9);
                float3 c2 = float3(0.05, 0.9, 0.45);
                float3 c3 = float3(1.0, 0.85, 0.15);
                float3 c4 = float3(1.0, 0.15, 0.05);
                if (t < 0.25) return lerp(c0, c1, t / 0.25);
                if (t < 0.5)  return lerp(c1, c2, (t - 0.25) / 0.25);
                if (t < 0.75) return lerp(c2, c3, (t - 0.5) / 0.25);
                return lerp(c3, c4, (t - 0.75) / 0.25);
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord.xy;
                float4 source = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                if (_VolumeValid < 0.5)
                    return source;

                float rawDepth = SampleSceneDepth(uv);
                bool isSky;
                #if UNITY_REVERSED_Z
                    isSky = rawDepth <= 1e-6;
                #else
                    isSky = rawDepth >= 0.999999;
                #endif
                if (isSky)
                {
                    if (_ViewMode == 0) return source;
                    return float4(0, 0, 0, 1);
                }

                float3 positionWS = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
                int3 dims = GIDims();
                int3 v = GIVoxelIndex(positionWS);
                if (!GIInVolume(v))
                {
                    if (_ViewMode == 0) return source;
                    return float4(0, 0, 0, 1);
                }

                // Locate the surface voxel that contains this pixel. Surfaces are
                // stored as a one-voxel shell; if the exact voxel was thinned out,
                // step slightly back along the view ray.
                float3 viewDir = normalize(positionWS - GetCameraPositionWS());
                float maxVoxel = max(_VolumeVoxelSize.x, max(_VolumeVoxelSize.y, _VolumeVoxelSize.z));
                float stepBack = maxVoxel * 0.6;

                // Enclosure gate. When the interior lamps are off and the camera
                // itself is in air that is not connected to the outdoor volume,
                // no outdoor GI can physically be present at the camera. This
                // removes residual voxel-pinhole leakage in sealed rooms without
                // creating per-pixel gate halos around thin geometry.
                if (_GateEnclosed > 0.5)
                {
                    int3 camV = GIVoxelIndex(_CameraPositionWS.xyz);
                    float outside = 1.0;
                    if (GIInVolume(camV)) outside = _OutsideMask.Load(int4(camV, 0));
                    if (outside < 0.001)
                    {
                        if (_ViewMode == 0) return source;
                        return float4(0, 0, 0, 1);
                    }
                }

                bool found = false;
                float4 albedoTexel = 0;
                [unroll] for (int k = 0; k < 4; k++)
                {
                    int3 vv = GIVoxelIndex(positionWS - viewDir * (stepBack * k));
                    if (GIInVolume(vv))
                    {
                        float4 a = _VolumeAlbedo.Load(int4(vv, 0));
                        if (a.a > 0.5) { albedoTexel = a; v = vv; found = true; break; }
                    }
                }
                float3 albedo = found ? saturate(albedoTexel.rgb) : float3(0.8, 0.8, 0.8);

                // The solver keeps a filtered irradiance halo around surfaces, so a
                // trilinear lookup is smooth and does not cut off at voxel edges.
                float3 volumeSize = _VolumeVoxelSize.xyz * float3(dims);
                float3 uvw = saturate((positionWS - _VolumeMin.xyz) / volumeSize);
                float3 indirectE = SAMPLE_TEXTURE3D_LOD(_VolumeIndirect, sampler_LinearClamp, uvw, 0).rgb;
                float3 skyE = SAMPLE_TEXTURE3D_LOD(_VolumeSky, sampler_LinearClamp, uvw, 0).rgb;

                float3 indirect = albedo * indirectE * _IndirectIntensity;
                float3 sky = albedo * skyE * _IndirectIntensity;

                if (_ViewMode == 1)
                    return float4(sky + indirect, 1);
                if (_ViewMode == 2)
                    return float4(indirect, 1);
                if (_ViewMode == 3)
                {
                    float lum = dot(indirect, float3(0.2126, 0.7152, 0.0722));
                    return float4(GIHeat(lum * 8.0), 1);
                }

                float3 result = source.rgb + sky + indirect;
                if (any(isnan(result)) || any(isinf(result)))
                    result = source.rgb;
                return float4(max(result, 0), source.a);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
