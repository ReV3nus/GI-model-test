// Override shader used by DynamicGIRenderPass: writes diffuse reflectance, world normal and world position
// for every opaque URP/Lit-style surface. Uses LitInput.hlsl so UnityPerMaterial matches the original material.
Shader "Hidden/DynamicGI/GBuffer"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        Pass
        {
            Name "DynamicGIGBuffer"
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float4 tangentOS : TANGENT; float2 uv : TEXCOORD0; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 positionWS : TEXCOORD1;
                float3 normalWS : TEXCOORD2; float4 tangentWS : TEXCOORD3;
            };
            struct Output { float4 albedo : SV_Target0; float4 normal : SV_Target1; float4 position : SV_Target2; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                VertexNormalInputs n = GetVertexNormalInputs(v.normalOS, v.tangentOS);
                o.positionCS = p.positionCS; o.positionWS = p.positionWS;
                o.normalWS = n.normalWS; o.tangentWS = float4(n.tangentWS, v.tangentOS.w * GetOddNegativeScale());
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            Output Frag(Varyings i)
            {
                Output o;
                float3 base = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv).rgb * _BaseColor.rgb;
                float diffuse = 0.96 * (1.0 - _Metallic);   // URP oneMinusReflectivity
                // Normal map (the default "bump" texture gives the geometric normal).
                float3 nTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, i.uv), _BumpScale);
                float3 nWS = normalize(i.normalWS);
                float3 tWS = normalize(i.tangentWS.xyz - nWS * dot(nWS, i.tangentWS.xyz));
                float3 bWS = cross(nWS, tWS) * i.tangentWS.w;
                float3 nMapped = normalize(nTS.x * tWS + nTS.y * bWS + nTS.z * nWS);
                if (any(isnan(nMapped))) nMapped = nWS;
                o.albedo = float4(base * diffuse, 1);
                o.normal = float4(nMapped, 1);
                o.position = float4(i.positionWS, 1);
                return o;
            }
            ENDHLSL
        }
    }
}
