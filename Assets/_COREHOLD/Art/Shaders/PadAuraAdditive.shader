Shader "COREHOLD/PadAuraAdditive"
{
    // A minimal unlit ADDITIVE glow shader for hardpoint auras. The glow texture is
    // premultiplied (RGB fades to black at the edges) and blending is One/One, so
    // transparent corners add nothing to the framebuffer and no square edge ever
    // shows. Tinted per-pad via _BaseColor (set through a MaterialPropertyBlock).
    Properties
    {
        _BaseMap ("Glow Texture", 2D) = "white" {}
        [HDR] _BaseColor ("Tint", Color) = (0, 0.8, 1, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            Blend One One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                // Premultiplied texture: tex.rgb carries the soft radial falloff.
                // _BaseColor carries the pad tint premultiplied by the pulse intensity
                // (set via MaterialPropertyBlock). Output alpha is irrelevant under
                // One/One additive blending.
                half3 col = tex.rgb * _BaseColor.rgb;
                return half4(col, tex.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
