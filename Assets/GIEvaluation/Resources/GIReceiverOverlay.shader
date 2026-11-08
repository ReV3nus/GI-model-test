Shader "Hidden/GIEvaluation/Surface Receiver"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _BaseColor ("Albedo tint", Color) = (1,1,1,1)
        _Metallic ("Metallic", Range(0, 1)) = 0
        _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent-50" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Blend One One
            ColorMask RGB
            ZWrite Off
            ZTest LEqual
            Offset -0.2, -1
            Cull [_Cull]
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            StructuredBuffer<float4> _SurfaceLighting;
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Metallic;
                float _Cull;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 chart : TEXCOORD1;
                float2 barycentric : TEXCOORD2;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float4 chart : TEXCOORD1;
                float2 barycentric : TEXCOORD2;
            };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS);
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.chart = input.chart;
                o.barycentric = input.barycentric;
                return o;
            }
            float4 Frag(Varyings input) : SV_Target
            {
                uint stride = (uint)input.chart.y;
                uint2 size = (uint2)input.chart.yz;
                float apex = input.chart.w;
                float r = saturate(input.barycentric.x + apex * input.barycentric.y);
                float t = saturate(input.barycentric.y);
                float2 xy = clamp(float2(r, t) * size - 0.5, 0.0, (float2)size - 1.0);
                uint2 lo = (uint2)xy;
                uint2 hi = min(lo + 1, size - 1);
                float2 f = frac(xy);
                uint first = (uint)input.chart.x + lo.y * stride;
                uint second = (uint)input.chart.x + hi.y * stride;
                float3 lower = lerp(_SurfaceLighting[first + lo.x].rgb, _SurfaceLighting[first + hi.x].rgb, f.x);
                float3 upper = lerp(_SurfaceLighting[second + lo.x].rgb, _SurfaceLighting[second + hi.x].rgb, f.x);
                float3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb * _BaseColor.rgb;
                return float4(albedo * (1.0 - saturate(_Metallic)) * lerp(lower, upper, f.y), 0);
            }
            ENDHLSL
        }
    }
}

