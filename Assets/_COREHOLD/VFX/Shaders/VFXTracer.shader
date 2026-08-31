Shader "Corehold/VFXTracer"
{
    // Purpose-built unlit line shader for hitscan tracers (GDD §11).
    //
    // Why a custom shader instead of URP/Particles/Unlit configured at runtime:
    //   URP's stock particle/lit shaders drive their blend state and surface
    //   keywords through the material inspector's ShaderGUI (MaterialChanged),
    //   NOT through the _Surface/_Blend float properties alone. Setting those
    //   floats from script leaves the ACTUAL SrcBlend/DstBlend render state and
    //   the _SURFACE_TYPE_TRANSPARENT keyword untouched, so "make it additive at
    //   runtime" silently does nothing and the authored HDR colour never reads
    //   correctly. This shader exposes SrcBlend/DstBlend as real material
    //   properties (Blend [_SrcBlend] [_DstBlend]) so a plain material asset —
    //   authored once in the editor — is guaranteed to blend the way we want.
    //
    // It multiplies the LineRenderer's per-vertex gradient colour by an HDR tint,
    // is fully unlit, writes no depth and casts no shadows, so Bloom picks up the
    // brightness and the hue survives (see the core+halo pairing in VfxTracer).
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        [HDR] _Color ("Tint", Color) = (1,1,1,1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5   // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 1   // One (additive)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Blend [_SrcBlend] [_DstBlend]
        ZWrite Off
        ZTest LEqual
        Cull Off

        Pass
        {
            Name "TracerUnlit"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                half4 _Color;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                OUT.color = IN.color;
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);
                // Vertex (gradient) colour drives per-shot hue/alpha; _Color is a
                // material-level HDR tint. Alpha rides through so both additive
                // (SrcAlpha One) and alpha (SrcAlpha OneMinusSrcAlpha) materials fade.
                return tex * IN.color * _Color;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
