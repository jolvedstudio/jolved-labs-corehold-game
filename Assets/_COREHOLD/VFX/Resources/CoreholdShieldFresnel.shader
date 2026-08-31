Shader "COREHOLD/ShieldFresnel"
{
    // A cheap, WebGL-safe rim/fresnel shell for the visible-shield read (VFX plan
    // Tier 1). One transparent, additive-ish draw per shielded unit — no particles,
    // no real-time lights, no depth prepass — so a swarm of shielded units stays
    // fill-rate-cheap at the fixed camera. Hand-written URP HLSL (no Shader Graph)
    // so it compiles to a small, predictable variant set for the WebGL build.
    Properties
    {
        [HDR] _RimColor ("Rim Color", Color) = (0.35, 0.7, 1.0, 1.0)
        _RimPower ("Rim Power", Range(0.5, 8)) = 2.5
        _Opacity ("Opacity", Range(0, 1)) = 0.6
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Transparent" "Queue" = "Transparent" }
        LOD 100

        Pass
        {
            Name "ShieldRim"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One          // additive over the scene so it always glows, never darkens
            ZWrite Off
            Cull Back                   // draw the front hull only — no doubled overdraw
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float3 viewDirWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _RimColor;
                half  _RimPower;
                half  _Opacity;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionHCS = TransformWorldToHClip(positionWS);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                OUT.viewDirWS = GetWorldSpaceViewDir(positionWS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                float3 n = normalize(IN.normalWS);
                float3 v = normalize(IN.viewDirWS);
                // Fresnel: bright at grazing angles (the rim), faint face-on.
                half fresnel = pow(saturate(1.0 - saturate(dot(n, v))), _RimPower);
                half alpha = saturate(fresnel * _Opacity);
                return half4(_RimColor.rgb * fresnel, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
