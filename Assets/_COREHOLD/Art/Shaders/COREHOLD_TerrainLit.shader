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

        // Weather surface response, driven per-scene by WeatherApplier through a
        // MaterialPropertyBlock. Both default to 0, so a scene that never sets
        // them renders exactly as before.
        //
        // SNOW accumulates by the surface NORMAL: flat ground whitens, slopes
        // stay bare, which is the whole read of snow at a distance. WET simply
        // DARKENS and desaturates — no specular path, because at 130-150 m
        // wetness reads as darker ground, not as shine, and a gloss lane would
        // cost WebGL bandwidth for something nobody can resolve.
        _SnowAmount ("Snow Amount", Range(0, 1)) = 0
        _SnowColor ("Snow Color", Color) = (0.92, 0.94, 0.98, 1)
        _SnowUpBias ("Snow Up Bias", Range(1, 8)) = 3
        _WetAmount ("Wet Amount", Range(0, 1)) = 0
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

        // Enemy trail map — GLOBALS, set by TrailMap at runtime, deliberately
        // outside the per-material cbuffer: one map serves every terrain
        // material in the scene, and a scene without a TrailMap leaves
        // _CoreholdTrailStrength at its default 0, which zeroes the sample
        // whatever an unbound texture returns.
        TEXTURE2D(_CoreholdTrailMap);
        SAMPLER(sampler_CoreholdTrailMap);
        float4 _CoreholdTrailArea;      // xy = world min, zw = 1/size
        half _CoreholdTrailStrength;
        // How dark a fully carved track goes, multiplied over the exposed
        // ground. A global rather than a material property so the one value
        // covers every terrain material a scene happens to use; TrailMap sets
        // it, and the 1.0 default leaves a scene without one unchanged.
        half3 _TrailDarken;

        // Standing water, written by WeatherApplier.
        //   x = the WATER TABLE's world Y — ground below it pools
        //   y = shoreline feather in metres
        //   z = rain ripple 0-1 (rain only; snow and dust must not shimmer)
        //   w = designer scale on the whole specular lane
        // All zero by default, which switches the entire block off.
        float4 _CoreholdWater;

        // What a wet surface mirrors. The applier feeds it the scene's own fog
        // or ambient colour, so a puddle under a storm sky reads storm-coloured
        // rather than reflecting some fixed blue nobody chose.
        half4 _CoreholdSkyColor;

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            float _DetailScale;
            half _DetailStrength;
            float _RockDetailScale;
            half _RockDetailStrength;
            half _RockDetailBlend;
            half _SnowAmount;
            half4 _SnowColor;
            half _SnowUpBias;
            half _WetAmount;
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

                // ---- weather surface response ---------------------------------
                // WET first, then SNOW: snow lies ON a wet surface, not under it.
                // Wet darkens and desaturates — no specular, because at this
                // camera distance wetness reads as darker ground and a gloss
                // lane would cost WebGL bandwidth nobody can resolve.
                half wet = saturate(_WetAmount);
                half pool = 0.0h;
                half gloss = 0.0h;
                if (wet > 0.001h)
                {
                    // Damp ground: water in the pores drops the diffuse albedo
                    // and pulls the colour toward grey. This part was always
                    // right — it is what wet DIRT does.
                    half lum = dot(albedo, half3(0.299h, 0.587h, 0.114h));
                    albedo = lerp(albedo, lerp(albedo, lum.xxx, 0.35h) * 0.55h, wet);

                    // POOLING. Water runs downhill and stands where it cannot
                    // leave, so the mask is a WATER TABLE — a world height the
                    // applier raises as the ground soaks — rather than a
                    // texture somebody painted. Pools therefore land in the
                    // valleys this terrain already has, on any map, with no
                    // authoring; and a flood event is the same lever pushed
                    // further rather than a second system.
                    half below = saturate((_CoreholdWater.x - input.positionWS.y)
                                          / max(_CoreholdWater.y, 0.01h));

                    // Standing water needs somewhere flat to stand. Sharper
                    // than the snow bias: snow clings to a slope, water does
                    // not sit on one at all.
                    half level = pow(saturate(normalWS.y), 6.0h);

                    // A water line that follows a height contour exactly reads
                    // as a line drawn on the hill. Two incommensurate sines
                    // wobble the shoreline for a few ALU and no texture — and
                    // no texture matters here, because the detail maps are
                    // optional and a pool that only appears on themes that
                    // authored one would be a trap.
                    half edge = 0.55h + 0.45h * sin(input.positionWS.x * 0.21h +
                                                    sin(input.positionWS.z * 0.17h) * 2.3h);

                    pool = saturate(below * level * edge) * wet;

                    // Water is darker than the wet ground under it.
                    albedo = lerp(albedo, albedo * 0.55h, pool);

                    // Damp ground has a broad sheen; standing water is a
                    // mirror. One scalar carries both — the exponent below is
                    // what separates them.
                    gloss = (0.30h * wet + 0.70h * pool) * _CoreholdWater.w;
                }

                // Snow accumulates by the surface NORMAL: flat ground whitens,
                // slopes shed. The bias sharpens that falloff so the transition
                // reads as accumulation rather than as a wash over everything.
                half up = saturate(normalWS.y);
                half snow = saturate(_SnowAmount) * pow(up, _SnowUpBias);

                // Enemy trails carve the FILM, not the ground: where units have
                // walked, the snow thins back toward the base albedo. Strength
                // is 0 unless a live TrailMap is feeding the globals.
                float2 tuv = (input.positionWS.xz - _CoreholdTrailArea.xy) * _CoreholdTrailArea.zw;
                half trail = SAMPLE_TEXTURE2D(_CoreholdTrailMap, sampler_CoreholdTrailMap, tuv).r
                             * _CoreholdTrailStrength;
                snow *= saturate(1.0h - trail);

                albedo = lerp(albedo, _SnowColor.rgb, snow);

                // ...and then the track DARKENS what it exposed. Removing the
                // film alone is not enough to see a track: on pale ground —
                // sand, most of this project's themes — the snow and the ground
                // beneath it sit at nearly the same luminance, so carving one
                // away leaves no contrast and the trail is invisible. Real
                // tracks read dark because the surface is compressed and in its
                // own shadow, so the shader says that outright and the effect
                // stops depending on the ground being darker than the snow.
                albedo *= lerp(1.0h, _TrailDarken, trail);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);

                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = SampleSH(normalWS) +
                                 mainLight.color * (ndotl * mainLight.shadowAttenuation);

                half3 color = albedo * lighting;

                // ---- the shine ------------------------------------------------
                // This is the half that makes wetness read as WET rather than
                // as dark. Darkening alone says "different ground"; a highlight
                // says "there is water on it". It costs ALU and nothing else —
                // no extra texture sample, no second pass, no screen texture —
                // and the uniform branch means a dry map pays for none of it.
                if (gloss > 0.001h)
                {
                    float3 viewDir = normalize(_WorldSpaceCameraPos - input.positionWS);
                    half3 n = normalWS;

                    // RIPPLES, only where rain is actually falling. Snow and
                    // dust settle on water without stirring it, and a shimmering
                    // puddle under a dust storm is an instant tell.
                    if (_CoreholdWater.z > 0.001h && pool > 0.01h)
                    {
                        float2 p = input.positionWS.xz * 3.1;
                        float t = _Time.y * 2.7;
                        half2 r = half2(sin(p.x + t) + sin(p.y * 1.31h - t * 1.7h),
                                        cos(p.y + t * 1.1h) + cos(p.x * 1.19h + t * 0.9h));
                        n = normalize(n + half3(r.x, 0.0h, r.y) *
                                          (0.05h * _CoreholdWater.z * pool));
                    }

                    // Blinn-Phong: the cheapest specular that still looks like
                    // a surface. The exponent rides `pool`, so damp ground gets
                    // a wide soft sheen and standing water gets the tight
                    // glint — one term, two materials.
                    half3 hv = normalize(mainLight.direction + viewDir);
                    half spec = pow(saturate(dot(n, hv)), lerp(24.0h, 180.0h, pool));
                    color += mainLight.color * mainLight.shadowAttenuation * spec * gloss;

                    // And water MIRRORS THE SKY, which is most of why a puddle
                    // reads as water rather than as a shiny patch of dirt —
                    // at this camera angle the grazing reflection covers more
                    // pixels than the sun glint ever does.
                    half fres = pow(1.0h - saturate(dot(n, viewDir)), 4.0h);
                    color = lerp(color, _CoreholdSkyColor.rgb, saturate(fres * gloss * 0.9h));
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
