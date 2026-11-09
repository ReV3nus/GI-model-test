// Full-screen composite: adds indirect diffuse (albedo * probe irradiance) to the lit HDR colour.
// Pass 0: additive (final image). Pass 1: replaces the colour with a debug view of the indirect term.
Shader "Hidden/DynamicGI/Composite"
{
    HLSLINCLUDE
    #pragma target 4.5
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "DynamicGICommon.hlsl"

    Texture2D<float4> _GIAlbedoTex;
    Texture2D<float4> _GINormalTex;
    Texture2D<float4> _GIPositionTex;
    float _GIIndirectIntensity;
    int _GIDebugView;

    struct V2F { float4 positionCS : SV_POSITION; };

    V2F Vert(uint id : SV_VertexID)
    {
        V2F o;
        float2 uv = float2((id << 1) & 2, id & 2);
        o.positionCS = float4(uv * 2.0 - 1.0, UNITY_NEAR_CLIP_VALUE, 1.0);
        return o;
    }

    float3 Indirect(float4 positionCS, out float coverage, out float3 albedo)
    {
        int2 px = int2(positionCS.xy);
        float4 a = _GIAlbedoTex.Load(int3(px, 0));
        coverage = a.a;
        albedo = a.rgb;
        if (a.a < 0.5) return 0;
        float3 n = normalize(_GINormalTex.Load(int3(px, 0)).xyz);
        float3 p = _GIPositionTex.Load(int3(px, 0)).xyz;
        float3 toViewer = normalize(_WorldSpaceCameraPos - p);
        return GISampleIrradiance(p, n, toViewer) * _GIIndirectIntensity;
    }

    float4 FragAdd(V2F i) : SV_Target
    {
        float coverage; float3 albedo;
        float3 irr = Indirect(i.positionCS, coverage, albedo);
        return float4(albedo * irr, 0);
    }

    float4 FragDebug(V2F i) : SV_Target
    {
        float coverage; float3 albedo;
        float3 irr = Indirect(i.positionCS, coverage, albedo);
        if (coverage < 0.5) return float4(0, 0, 0, 1);
        return float4(_GIDebugView == 2 ? irr : albedo * irr, 1);
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        Pass
        {
            Name "Add"
            Blend One One
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragAdd
            ENDHLSL
        }
        Pass
        {
            Name "Debug"
            Blend One Zero
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDebug
            ENDHLSL
        }
    }
}
