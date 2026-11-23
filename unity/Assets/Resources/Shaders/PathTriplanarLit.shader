// コンクリート等の色と法線をワールド空間の三方向(トライプラナー)へ投影する。
// SplineMesh による進行方向の伸縮や、橋脚の高さに応じた Y スケールで模様が引き伸ばされるのを防ぐ。
Shader "Custom/PathTriplanarLit"
{
    Properties
    {
        [MainTexture] _BaseMap("Albedo (tiling per metre)", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal", 2D) = "bump" {}
        _BumpScale("Normal Strength", Float) = 0.75
        _Smoothness("Smoothness", Range(0,1)) = 0.16
        _Metallic("Metallic", Range(0,1)) = 0
        [HDR] _EmissionColor("Emission", Color) = (0,0,0,0)
        [HideInInspector] _Cull("Cull", Float) = 2
        [HideInInspector] _ZWrite("ZWrite", Float) = 1
        [HideInInspector] _Cutoff("Cutoff", Float) = 0.5
        [HideInInspector] _Surface("Surface", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap);
        SAMPLER(sampler_BumpMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half4 _EmissionColor;
            half _BumpScale;
            half _Smoothness;
            half _Metallic;
            half _Cutoff;
            half _Surface;
        CBUFFER_END

        half3 TriplanarWeights(half3 normalWS)
        {
            half3 w = pow(abs(normalWS), 4.0h);
            return w / max(w.x + w.y + w.z, 1e-4h);
        }

        half3 SampleTriplanarAlbedo(float3 posWS, half3 weights)
        {
            float2 uvX = posWS.zy * _BaseMap_ST.xy + _BaseMap_ST.zw;
            float2 uvY = posWS.xz * _BaseMap_ST.xy + _BaseMap_ST.zw;
            float2 uvZ = posWS.xy * _BaseMap_ST.xy + _BaseMap_ST.zw;
            half3 cX = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvX).rgb;
            half3 cY = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvY).rgb;
            half3 cZ = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uvZ).rgb;
            return cX * weights.x + cY * weights.y + cZ * weights.z;
        }

        // OpenGL(Y+)法線マップを各投影軸の勾配としてワールド法線へ合成する(Whiteout合成)。
        half3 SampleTriplanarNormalWS(float3 posWS, half3 normalWS, half3 weights)
        {
            float2 uvX = posWS.zy * _BaseMap_ST.xy + _BaseMap_ST.zw;
            float2 uvY = posWS.xz * _BaseMap_ST.xy + _BaseMap_ST.zw;
            float2 uvZ = posWS.xy * _BaseMap_ST.xy + _BaseMap_ST.zw;

            half3 nX = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvX), _BumpScale);
            half3 nY = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvY), _BumpScale);
            half3 nZ = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvZ), _BumpScale);

            half3 wX = half3(nX.z * normalWS.x, nX.y + normalWS.y, nX.x + normalWS.z);
            half3 wY = half3(nY.x + normalWS.x, nY.z * normalWS.y, nY.y + normalWS.z);
            half3 wZ = half3(nZ.x + normalWS.x, nZ.y + normalWS.y, nZ.z * normalWS.z);
            return normalize(wX * weights.x + wY * weights.y + wZ * weights.z);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite On
            Cull Back
            Offset -1, -1

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex TriplanarVert
            #pragma fragment TriplanarFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_local_fragment _ _EMISSION
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half fogFactor : TEXCOORD2;
                half3 vertexSH : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings TriplanarVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                output.fogFactor = ComputeFogFactor(posInputs.positionCS.z);
                output.vertexSH = SampleSHVertex(normInputs.normalWS);
                return output;
            }

            half4 TriplanarFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 geomNormalWS = normalize(input.normalWS);
                half3 weights = TriplanarWeights(geomNormalWS);
                half3 albedo = SampleTriplanarAlbedo(input.positionWS, weights) * _BaseColor.rgb;
                half3 normalWS = SampleTriplanarNormalWS(input.positionWS, geomNormalWS, weights);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.alpha = 1.0h;
                surfaceData.metallic = _Metallic;
                surfaceData.smoothness = _Smoothness;
                surfaceData.occlusion = 1.0h;
                surfaceData.normalTS = half3(0, 0, 1);
                #ifdef _EMISSION
                surfaceData.emission = _EmissionColor.rgb;
                #endif

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1.0), input.fogFactor);
                inputData.bakedGI = SampleSHPixel(input.vertexSH, normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = half4(1, 1, 1, 1);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                return color;
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex TriplanarDepthNormalsVert
            #pragma fragment TriplanarDepthNormalsFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings TriplanarDepthNormalsVert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(input.normalOS);
                output.positionCS = posInputs.positionCS;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normInputs.normalWS;
                return output;
            }

            half4 TriplanarDepthNormalsFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 geomNormalWS = normalize(input.normalWS);
                half3 weights = TriplanarWeights(geomNormalWS);
                half3 normalWS = SampleTriplanarNormalWS(input.positionWS, geomNormalWS, weights);

                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                    float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                    half3 packedNormalWS = PackFloat2To888(remappedOctNormalWS);
                    return half4(packedNormalWS, 0.0h);
                #else
                    return half4(normalWS, 0.0h);
                #endif
            }
            ENDHLSL
        }
    }

    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
