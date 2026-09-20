// Custom/DepthOffsetSimpleLit の派生。Offset を -1,-1 ではなく 4,4 にしたバリアント。
// 用途: 超遠景(UltraFar)の陸地シルエットメッシュ。マップ全体を覆うこのメッシュは、
//   カメラ近傍の per-cell 地形タイル(Custom/DepthOffsetTerrainLit, Offset 1,1)と重なるため、
//   より大きい正の Offset(4,4)で最奥へ押しやり、per-cell タイルが常に手前で勝つようにする。
//   海面(Custom/DepthOffsetLit, Offset -1,-1)よりも奥なので、海岸の縁でも海面が正しく手前に出る。
// 地表の手続き色・岩肌・PBR照明をTerrainと共用する。SimpleLitの頂点入力と補助パスを利用する。
// 乗算ティント _TerrainTint はTerrainと同じく、照明・霧の適用後に掛ける。
// 沈み込みは接続する三角形全体がロード済み範囲に収まる頂点だけに適用する。
// 境界をまたぐ面を下げると未ロード側まで海面下へ引き込むため、外周に重なりを残す。
Shader "Custom/DepthOffsetSimpleLitFar"
{
    // Keep properties of StandardSpecular shader for upgrade reasons.
    Properties
    {
        [MainTexture] _BaseMap("Base Map (RGB) Smoothness / Alpha (A)", 2D) = "white" {}
        [MainColor]   _BaseColor("Base Color", Color) = (1, 1, 1, 1)

        // ロード済みセルマスク(1セル=1テクセル、TerrainManagerが毎フレーム更新)。
        // 既定の black はマスク無し=どこも沈めない、の安全なフォールバック。
        [NoScaleOffset] _LoadedCellMask("Loaded Cell Mask", 2D) = "black" {}
        _LoadedCellMaskParams("Loaded Cell Mask Params", Vector) = (0, 0, 0, 0) // x:1/cellSize y:offsetX z:offsetY
        _LoadedCellMaskTexel("Loaded Cell Mask Texel", Vector) = (0, 0, 0, 0)   // x:1/cellsX y:1/cellsY
        _FarSinkAmount("Far Sink Amount", Float) = 100.0
        _FarGridStep("Far Grid Step (m)", Float) = 80.0

        _TerrainMacroVariationStrength("Macro Variation Strength", Range(0.0, 0.5)) = 0.12
        _TerrainMacroVariationScale("Macro Variation Scale", Float) = 350.0
        _TerrainMacroVariationTint("Macro Variation Tint", Color) = (0.85, 0.95, 0.75, 1.0)

        // メッシュ全体への乗算ティント。TerrainManager がマテリアルに設定する。
        // 白=素の地形色。
        _TerrainTint("Terrain Tint", Color) = (1.0, 1.0, 1.0, 1.0)

        // 崖(急傾斜)への岩テクスチャブレンド設定。実行時は TerrainManager の
        // ApplyTerrainMaterialSettings がタイルごとの materialTemplate に設定する。
        // 既定値は TerrainManager.cs の SerializeField 既定値と一致させること。
        _CliffAlbedoMap("Cliff Albedo", 2D) = "grey" {}
        _CliffNormalMap("Cliff Normal", 2D) = "bump" {}
        _CliffUvScale("Cliff UV Scale (1/m)", Float) = 0.0833
        _CliffSlopeThreshold("Cliff Slope Threshold", Range(0.0, 1.0)) = 0.45
        _CliffSlopeBlend("Cliff Slope Blend", Range(0.01, 1.0)) = 0.18
        _CliffNoiseScale("Cliff Noise Scale (m)", Float) = 18.0
        _CliffNoiseStrength("Cliff Noise Strength", Range(0.0, 0.3)) = 0.09
        _CliffNormalStrength("Cliff Normal Strength", Range(0.0, 2.0)) = 1.0
        _CliffSmoothness("Cliff Smoothness", Range(0.0, 1.0)) = 0.08
        _CliffTexturesLoaded("Cliff Textures Loaded", Float) = 0.0
        _CliffFallbackColor("Cliff Fallback Color", Color) = (0.42, 0.36, 0.30, 1.0)
        _CliffLowlandStart("Cliff Lowland Start Height (m)", Float) = 2.0
        _CliffLowlandFull("Cliff Lowland Full Height (m)", Float) = 0.0

        _GroundMetallic("Ground Metallic", Range(0, 1)) = 0

        _Cutoff("Alpha Clipping", Range(0.0, 1.0)) = 0.5

        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _SpecColor("Specular Color", Color) = (0.5, 0.5, 0.5, 0.5)
        _SpecGlossMap("Specular Map", 2D) = "white" {}
        _SmoothnessSource("Smoothness Source", Float) = 0.0
        _SpecularHighlights("Specular Highlights", Float) = 1.0

        [HideInInspector] _BumpScale("Scale", Float) = 1.0
        [NoScaleOffset] _BumpMap("Normal Map", 2D) = "bump" {}

        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0)
        [NoScaleOffset]_EmissionMap("Emission Map", 2D) = "white" {}

        // Blending state
        _Surface("__surface", Float) = 0.0
        _Blend("__blend", Float) = 0.0
        _Cull("__cull", Float) = 2.0
        [ToggleUI] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 1.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 1.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 1.0
        [HideInInspector] _BlendModePreserveSpecular("_BlendModePreserveSpecular", Float) = 1.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        [HideInInspector] _AddPrecomputedVelocity("_AddPrecomputedVelocity", Float) = 0.0
        [HideInInspector] _XRMotionVectorsPass("_XRMotionVectorsPass", Float) = 1.0

        [ToggleUI] _ReceiveShadows("Receive Shadows", Float) = 1.0
        // Editmode props
        _QueueOffset("Queue offset", Float) = 0.0

        // ObsoleteProperties
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (1, 1, 1, 1)
        [HideInInspector] _Shininess("Smoothness", Float) = 0.0
        [HideInInspector] _GlossinessSource("GlossinessSource", Float) = 0.0
        [HideInInspector] _SpecSource("SpecularHighlights", Float) = 0.0

        [HideInInspector][NoScaleOffset]unity_Lightmaps("unity_Lightmaps", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_LightmapsInd("unity_LightmapsInd", 2DArray) = "" {}
        [HideInInspector][NoScaleOffset]unity_ShadowMasks("unity_ShadowMasks", 2DArray) = "" {}
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Lit"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            // -------------------------------------
            // Render State Commands
            // Use same blending / depth states as Standard shader
            Blend[_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
            ZWrite[_ZWrite]
            Cull[_Cull]
            AlphaToMask[_AlphaToMask]
            // per-cell 地形(Offset 1,1)・海面(Offset -1,-1)より奥へ押しやる。
            Offset 4, 4

            HLSLPROGRAM
            // 頂点シェーダーでのマスクテクスチャ参照(SAMPLE_TEXTURE2D_LOD)に必要
            #pragma target 3.5

            // -------------------------------------
            // Shader Stages
            #pragma vertex VertForwardFar
            #pragma fragment LitPassFragmentSimple

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF
            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ _ALPHAPREMULTIPLY_ON _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _ _SPECGLOSSMAP _SPECULAR_COLOR
            #pragma shader_feature_local_fragment _GLOSSINESS_FROM_BASE_ALPHA

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile_fragment _ DEBUG_DISPLAY
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            //--------------------------------------
            // Defines
            #define BUMP_SCALE_NOT_SUPPORTED 1

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            // URPの入力・GI・霧を維持し、照明だけTerrainと共通のPBRへ接続する。
            #include "Assets/Shaders/TerrainSurfaceAppearance.hlsl"
            float _GroundMetallic;
            float _Smoothness;
            half4 FarTerrainFragmentPBR(InputData inputData, SurfaceData surfaceData)
            {
                return CliffTerrainFragmentPBR(inputData, surfaceData.albedo, _GroundMetallic,
                    half3(0, 0, 0), _Smoothness, surfaceData.occlusion, surfaceData.emission, surfaceData.alpha);
            }
            #define UniversalFragmentBlinnPhong FarTerrainFragmentPBR
            #define LitPassFragmentSimple OriginalFarFragment
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitForwardPass.hlsl"
            #undef LitPassFragmentSimple
            #undef UniversalFragmentBlinnPhong

            void LitPassFragmentSimple(Varyings input, out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
                , out uint outRenderingLayers : SV_Target1
#endif
            )
            {
                OriginalFarFragment(input, outColor
#ifdef _WRITE_RENDERING_LAYERS
                    , outRenderingLayers
#endif
                );
                outColor.rgb = ApplyTerrainMacroVariation(outColor.rgb, input.positionWS);
                outColor.rgb = ApplyTerrainColorGrade(outColor.rgb, input.positionWS);
                outColor.rgb = ApplyContourLines(outColor.rgb, input.positionWS);
                outColor.rgb *= (half3)_TerrainTint.rgb;
            }

            #include "Assets/Shaders/UltraFarTerrainSink.hlsl"

            Varyings VertForwardFar(Attributes input)
            {
                ApplyFarSink(input.positionOS.xyz);
                return LitPassVertexSimple(input);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags
            {
                "LightMode" = "ShadowCaster"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull[_Cull]

            HLSLPROGRAM
            // 頂点シェーダーでのマスクテクスチャ参照(SAMPLE_TEXTURE2D_LOD)に必要
            #pragma target 3.5

            // -------------------------------------
            // Shader Stages
            #pragma vertex VertShadowFar
            #pragma fragment ShadowPassFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _GLOSSINESS_FROM_BASE_ALPHA

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // This is used during shadow map generation to differentiate between directional and punctual light shadows, as they use different formulas to apply Normal Bias
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"

            #include "Assets/Shaders/UltraFarTerrainSink.hlsl"

            Varyings VertShadowFar(Attributes input)
            {
                ApplyFarSink(input.positionOS.xyz);
                return ShadowPassVertex(input);
            }
            ENDHLSL
        }

        Pass
        {
            Name "GBuffer"
            Tags
            {
                "LightMode" = "UniversalGBuffer"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite[_ZWrite]
            ZTest LEqual
            Cull[_Cull]
            // ForwardLit と同じ理由で最奥へ。
            Offset 4, 4

            HLSLPROGRAM
            #pragma target 4.5

            // Deferred Rendering Path does not support the OpenGL-based graphics API:
            // Desktop OpenGL, OpenGL ES 3.0, WebGL 2.0.
            #pragma exclude_renderers gles3 glcore

            // -------------------------------------
            // Shader Stages
            #pragma vertex VertGBufferFar
            #pragma fragment LitPassFragmentSimple

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            //#pragma shader_feature _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local_fragment _ _SPECGLOSSMAP _SPECULAR_COLOR
            #pragma shader_feature_local_fragment _GLOSSINESS_FROM_BASE_ALPHA
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local _RECEIVE_SHADOWS_OFF

            // -------------------------------------
            // Universal Pipeline keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            //#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            //#pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/ProbeVolumeVariants.hlsl"
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fragment _ LIGHTMAP_BICUBIC_SAMPLING
            #pragma multi_compile_fragment _ REFLECTION_PROBE_ROTATION
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LEGACY_LIGHTMAPS
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #pragma multi_compile_fragment _ _RENDER_PASS_ENABLED
            #pragma multi_compile _ LOD_FADE_CROSSFADE
            #pragma multi_compile_fragment _ _SCREEN_SPACE_IRRADIANCE

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            //--------------------------------------
            // Defines
            #define BUMP_SCALE_NOT_SUPPORTED 1

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitGBufferPass.hlsl"

            #include "Assets/Shaders/UltraFarTerrainSink.hlsl"

            Varyings VertGBufferFar(Attributes input)
            {
                ApplyFarSink(input.positionOS.xyz);
                return LitPassVertexSimple(input);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags
            {
                "LightMode" = "DepthOnly"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            ColorMask R
            Cull[_Cull]

            HLSLPROGRAM
            // 頂点シェーダーでのマスクテクスチャ参照(SAMPLE_TEXTURE2D_LOD)に必要
            #pragma target 3.5

            // -------------------------------------
            // Shader Stages
            #pragma vertex VertDepthOnlyFar
            #pragma fragment DepthOnlyFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _GLOSSINESS_FROM_BASE_ALPHA

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"

            #include "Assets/Shaders/UltraFarTerrainSink.hlsl"

            Varyings VertDepthOnlyFar(Attributes input)
            {
                ApplyFarSink(input.position.xyz);
                return DepthOnlyVertex(input);
            }
            ENDHLSL
        }

        // This pass is used when drawing to a _CameraNormalsTexture texture
        Pass
        {
            Name "DepthNormals"
            Tags
            {
                "LightMode" = "DepthNormals"
            }

            // -------------------------------------
            // Render State Commands
            ZWrite On
            Cull[_Cull]

            HLSLPROGRAM
            // 頂点シェーダーでのマスクテクスチャ参照(SAMPLE_TEXTURE2D_LOD)に必要
            #pragma target 3.5

            // -------------------------------------
            // Shader Stages
            #pragma vertex VertDepthNormalsFar
            #pragma fragment DepthNormalsFragment

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local _ALPHATEST_ON
            #pragma shader_feature_local_fragment _GLOSSINESS_FROM_BASE_ALPHA

            // -------------------------------------
            // Unity defined keywords
            #pragma multi_compile _ LOD_FADE_CROSSFADE

            // Universal Pipeline keywords
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            //--------------------------------------
            // GPU Instancing
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitDepthNormalsPass.hlsl"

            #include "Assets/Shaders/UltraFarTerrainSink.hlsl"

            Varyings VertDepthNormalsFar(Attributes input)
            {
                ApplyFarSink(input.positionOS.xyz);
                return DepthNormalsVertex(input);
            }
            ENDHLSL
        }

        // This pass it not used during regular rendering, only for lightmap baking.
        Pass
        {
            Name "Meta"
            Tags
            {
                "LightMode" = "Meta"
            }

            // -------------------------------------
            // Render State Commands
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0

            // -------------------------------------
            // Shader Stages
            #pragma vertex UniversalVertexMeta
            #pragma fragment UniversalFragmentMetaSimple

            // -------------------------------------
            // Material Keywords
            #pragma shader_feature_local_fragment _EMISSION
            #pragma shader_feature_local_fragment _SPECGLOSSMAP
            #pragma shader_feature EDITOR_VISUALIZATION

            // -------------------------------------
            // Includes
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitMetaPass.hlsl"

            ENDHLSL
        }
    }

    Fallback  "Hidden/Universal Render Pipeline/FallbackError"
    CustomEditor "UnityEditor.Rendering.Universal.ShaderGUI.SimpleLitShader"
}
