// Terrain shading (M-d): the one thing URP Lit cannot do for the generated
// relief mesh is read the VERTEX COLORS the terrain stage bakes (valley
// darkening, slope rock desaturation) — so this minimal hand-rolled URP
// shader exists for exactly that. Deliberately simple: albedo × vertex
// colour, Lambert main light with shadows, spherical-harmonics ambient,
// standard fog. No normal maps, no specular — WebGL-friendly, and the
// post volume does the rest of the look.
//
// Passes: ForwardLit, ShadowCaster (hills cast onto the corridor — that is
// most of the depth read), DepthOnly. No DepthNormals pass: if the SSAO
// renderer feature is enabled, set its Source to "Depth" so the terrain
// participates.
Shader "COREHOLD/Terrain Lit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        // Near-field detail (M-d): a small grayscale map tiled _DetailScale×
        // denser than the base, overlay-multiplied. This is what keeps the
        // ground readable when a POV camera stands on it despite the 512/1024
        // texture budget — high-frequency detail costs a 16 KB texture, not a
        // 2048 base map. "grey" default (0.5 → ×1.0) is a perfect no-op.
        _DetailMap ("Detail (grayscale)", 2D) = "grey" {}
        _DetailScale ("Detail Scale", Float) = 9
        _DetailStrength ("Detail Strength", Range(0, 1)) = 0.35

        // E2 second detail lane. The mesh bakes the substrate's ROCK WEIGHT
        // into vertex ALPHA, and rocky ground wears this coarser, stronger
        // breakup instead of the fine one — gravel reads differently from sand
        // mostly by the size of its grain, not by its colour.
        //
        // _RockDetailBlend defaults to 0 on purpose: a scene baked before E2
        // has alpha 1 across the whole mesh, so without this gate it would come
        // back wearing gravel everywhere. Only a freshly generated material
        // turns the lane on.
        _RockDetailMap ("Rock Detail (grayscale)", 2D) = "grey" {}
        _RockDetailScale ("Rock Detail Scale", Float) = 4
        _RockDetailStrength ("Rock Detail Strength", Range(0, 1)) = 0.5
        _RockDetailBlend ("Rock Detail Blend", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_DetailMap);
        SAMPLER(sampler_DetailMap);
        TEXTURE2D(_RockDetailMap);
        SAMPLER(sampler_RockDetailMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            float _DetailScale;
            half _DetailStrength;
            float _RockDetailScale;
            half _RockDetailStrength;
            half _RockDetailBlend;
        CBUFFER_END
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half4  color      : TEXCOORD3;
                half   fogFactor  : TEXCOORD4;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).rgb
                             * _BaseColor.rgb * input.color.rgb;

                // E2: how stony this vertex's ground is, baked into vertex
                // alpha by the terrain stage. The blend scalar is 0 on any
                // material built before E2, which pins rock to 0 and leaves
                // the fine-detail path below exactly as it was.
                half rock = saturate(input.color.a) * _RockDetailBlend;

                // Overlay-multiply the high-frequency detail: 0.5 is neutral,
                // so strength 0 or the default grey texture changes nothing.
                // Rocky ground crossfades to a coarser, stronger grain — the
                // size of the grain is most of what separates gravel from sand
                // at this camera distance.
                half fine = SAMPLE_TEXTURE2D(_DetailMap, sampler_DetailMap,
                                             input.uv * _DetailScale).r;
                half coarse = SAMPLE_TEXTURE2D(_RockDetailMap, sampler_RockDetailMap,
                                               input.uv * _RockDetailScale).r;
                half detail = lerp(fine, coarse, rock);
                half strength = lerp(_DetailStrength, _RockDetailStrength, rock);
                albedo *= lerp(1.0h, detail * 2.0h, strength);

                half3 normalWS = normalize(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = SampleSH(normalWS) +
                                 mainLight.color * (ndotl * mainLight.shadowAttenuation);

                half3 color = albedo * lighting;
                color = MixFog(color, input.fogFactor);
                return half4(color, 1.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                output.positionCS = positionCS;
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
}
