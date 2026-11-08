Shader "Hidden/DynamicGI/Composite"
{
    HLSLINCLUDE
    #pragma target 4.5
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
    TEXTURE2D(_GITexture);
    float4 _GITexture_TexelSize;
    float _GIIntensity;

    float4 GIFragment(Varyings input) : SV_Target
    {
        float2 uv = input.texcoord;
        float rawDepth = SampleSceneDepth(uv);
        float depth = LinearEyeDepth(rawDepth, _ZBufferParams);
        // A depth-aware 3x3 tent reduces Monte Carlo noise without mixing across
        // walls. A failed depth correspondence contributes zero instead of leaking.
        float2 position = uv * _GITexture_TexelSize.zw - .5;
        float2 basePixel = floor(position);
        float3 sum = 0;
        float weightSum = 0;
        [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 pixel = basePixel + float2(x,y);
            float2 tapUV = (pixel + .5) * _GITexture_TexelSize.xy;
            float4 tap = SAMPLE_TEXTURE2D_LOD(_GITexture, sampler_PointClamp, tapUV, 0);
            float2 delta = pixel - position;
            float spatial = exp(-dot(delta,delta) * .65);
            float tolerance = max(.025, depth * .008);
            float agreement = exp(-abs(tap.a-depth) / tolerance);
            agreement *= tap.a > 0 && abs(tap.a-depth) < tolerance * 4 ? 1 : 0;
            float weight = spatial * agreement;
            sum += tap.rgb * weight;
            weightSum += weight;
        }
        return float4(sum / max(weightSum, .0001) * _GIIntensity, 0);
    }
    ENDHLSL
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "AddIndirect"
            ZWrite Off ZTest Always Cull Off
            Blend One One
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment GIFragment
            ENDHLSL
        }
        Pass
        {
            Name "IndirectOnly"
            ZWrite Off ZTest Always Cull Off
            Blend Off
            ColorMask RGB
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment GIFragment
            ENDHLSL
        }
    }
}
