// Dressing shading: the props' half of the weather surface response, plus the
// wind the props stand in.
//
// WHY THIS EXISTS. Terrain gets snow inside COREHOLD/Terrain Lit, where the
// surface normal decides what accumulates. Dressing could not: props wear
// arbitrary vendor materials on arbitrary shaders, so the first attempt tinted
// _BaseColor through a property block. That cannot work and the reason is
// worth writing down — _BaseColor MULTIPLIES the base map, so pushing it
// toward white leaves a textured brown rock exactly as brown as it started,
// and on a vendor shader that names its colour something else it does nothing
// at all. Snow was landing on the ground and stopping at the rocks.
//
// So props are swapped onto THIS shader while weather asks for it (see
// PropSnow), which buys three things at once:
//   • real normal-based accumulation — snow on the tops, bare undersides —
//     rather than a uniform wash that reads as paint;
//   • one global write per channel instead of a property block per renderer;
//   • a vertex stage, which is where wind lives. Vegetation now leans and
//     sways with the same wind vector that drives the precipitation sheet.
//
// The cost of the swap is the vendor shader's own features (normal maps,
// bespoke stylisation) for as long as weather is up. At 130-150 m that trade
// is heavily in our favour, and PropSnow keeps a skip list for the materials
// where it is not (anything doing its own vertex animation).
//
// Deliberately the same shape as COREHOLD/Terrain Lit: albedo × colour,
// Lambert main light with shadows, SH ambient, standard fog. No specular, no
// normal maps — WebGL first.
Shader "COREHOLD/Prop Lit"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1, 1, 1, 1)
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

        // Metres of lateral travel per metre of height at full wind. 0 is a
        // rock; PropSnow sets it per MATERIAL VARIANT rather than per renderer
        // so the SRP batcher keeps working — a property block on every prop
        // would cost more than the whole weather system.
        _SwayScale ("Sway Scale", Range(0, 0.4)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        // Weather GLOBALS, written once per surface tick by PropSnow — outside
        // the per-material cbuffer because one value serves every prop material
        // in the scene. All default to 0, so a prop wearing this shader in a
        // scene with no weather renders as its plain albedo.
        half  _CoreholdPropSnow;
        half  _CoreholdPropWet;
        half4 _CoreholdPropSnowColor;
        // xyz = normalized horizontal wind direction, w = sway amplitude.
        float4 _CoreholdWind;

        // Shared with the terrain shader so ground and props shine as one
        // surface: .w is the specular scale, .z the rain ripple (unused here —
        // props do not pool), and _CoreholdSkyColor is what wet things mirror.
        float4 _CoreholdWater;
        half4 _CoreholdSkyColor;

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Cutoff;
            half _SwayScale;
        CBUFFER_END

        // Lean and sway about the prop's own pivot.
        //
        // The bend is taken from HEIGHT ABOVE THE PIVOT in world space, read
        // straight off the object-to-world translation, which makes it correct
        // under any scale the placer applied — a prop scaled to 1.6 bends 1.6
        // times as far because it IS 1.6 times as tall, with no per-prop data
        // to author or keep in sync.
        //
        // The phase is seeded from the pivot's own position, so a field of
        // identical prefabs never sways in unison — the thing that instantly
        // reads as "this is a shader", not "this is wind". Two incommensurate
        // sines again, the same trick the gust envelope uses.
        float3 CoreholdSway(float3 positionWS)
        {
            float amp = _CoreholdWind.w * _SwayScale;
            if (amp <= 0.00001)
                return positionWS;

            float3 pivotWS = float3(UNITY_MATRIX_M._m03, UNITY_MATRIX_M._m13, UNITY_MATRIX_M._m23);
            float h = max(0.0, positionWS.y - pivotWS.y);
            float phase = _Time.y * 1.9 + pivotWS.x * 0.37 + pivotWS.z * 0.29;
            float s = sin(phase) * 0.72 + sin(phase * 2.7 + 1.3) * 0.28;
            positionWS.xz += _CoreholdWind.xz * (h * amp * s);
            return positionWS;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _ALPHATEST_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half   fogFactor  : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionWS = CoreholdSway(TransformObjectToWorld(input.positionOS.xyz));
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
            #ifdef _ALPHATEST_ON
                clip(tex.a * _BaseColor.a - _Cutoff);
            #endif
                half3 albedo = tex.rgb * _BaseColor.rgb;

                half3 normalWS = normalize(input.normalWS);

                // WET first, then SNOW — snow lies ON a wet surface, not under
                // it. Identical maths to the terrain shader on purpose: ground
                // and props have to agree, or the field reads as two materials
                // in two different weathers.
                half wet = saturate(_CoreholdPropWet);
                half lum = dot(albedo, half3(0.299h, 0.587h, 0.114h));
                albedo = lerp(albedo, lerp(albedo, lum.xxx, 0.35h) * 0.55h, wet);

                // Accumulation by the surface normal. The bias is softer than
                // the terrain's: a rock is all slope, and a prop that only
                // whitens on its perfectly flat faces gets no snow at all.
                half up = saturate(normalWS.y);
                half snow = saturate(_CoreholdPropSnow) * pow(up, 2.0h);
                albedo = lerp(albedo, _CoreholdPropSnowColor.rgb, snow);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = SampleSH(normalWS) +
                                 mainLight.color * (ndotl * mainLight.shadowAttenuation);

                half3 color = albedo * lighting;

                // Wet props SHINE, on the same terms as the wet ground — this
                // is the half that reads as rain rather than as dusk. No
                // pooling: water does not stand on a rock, it runs off, so
                // props get the broad damp sheen and never the mirror.
                //
                // Snow kills it: a snow-capped rock is matte, and leaving the
                // highlight under the film would make fresh snow look like wet
                // plastic.
                half shine = wet * (1.0h - snow) * _CoreholdWater.w;
                if (shine > 0.001h)
                {
                    float3 viewDir = normalize(_WorldSpaceCameraPos - input.positionWS);
                    half3 hv = normalize(mainLight.direction + viewDir);
                    half spec = pow(saturate(dot(normalWS, hv)), 40.0h);
                    color += mainLight.color * mainLight.shadowAttenuation * spec * shine * 0.6h;

                    half fres = pow(1.0h - saturate(dot(normalWS, viewDir)), 5.0h);
                    color = lerp(color, _CoreholdSkyColor.rgb, saturate(fres * shine * 0.5h));
                }

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
            #pragma multi_compile_fragment _ _ALPHATEST_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                // The shadow must sway with the mesh or it detaches from it —
                // a tree leaning in the wind over a stationary shadow is worse
                // than no wind at all.
                float3 positionWS = CoreholdSway(TransformObjectToWorld(input.positionOS.xyz));
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float4 positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                output.positionCS = positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target
            {
            #ifdef _ALPHATEST_ON
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a - _Cutoff);
            #endif
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
            #pragma multi_compile_fragment _ _ALPHATEST_ON

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
            };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformWorldToHClip(
                    CoreholdSway(TransformObjectToWorld(input.positionOS.xyz)));
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 DepthFrag(Varyings input) : SV_Target
            {
            #ifdef _ALPHATEST_ON
                clip(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv).a * _BaseColor.a - _Cutoff);
            #endif
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
