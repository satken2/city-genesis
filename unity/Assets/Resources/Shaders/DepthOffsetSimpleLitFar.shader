// Custom/DepthOffsetSimpleLit の派生。Offset を -1,-1 ではなく 4,4 にしたバリアント。
// 用途: 超遠景(UltraFar)の陸地シルエットメッシュ。マップ全体を覆うこのメッシュは、
//   カメラ近傍の per-cell 地形タイル(Custom/DepthOffsetTerrainLit, Offset 1,1)と重なるため、
//   より大きい正の Offset(4,4)で最奥へ押しやり、per-cell タイルが常に手前で勝つようにする。
//   海面(Custom/DepthOffsetLit, Offset -1,-1)よりも奥なので、海岸の縁でも海面が正しく手前に出る。
// SimpleLit の標準パスをそのまま使い、per-cell タイルと同じ地面テクスチャをワールドXZ座標UVで貼る。
//   未開放セルはサブメッシュ別マテリアルの _BaseColor に暗めのティントを乗算して表現する。
// 沈み込み: per-cell タイルがロード済みのセルでは頂点を _FarSinkAmount(既定50m)地中に沈め、
//   タイルへの突き抜け(特に near_map 由来タイルとの起伏差)を隠す。判定は TerrainManager が
//   毎フレーム更新するロード済みセルマスク(_LoadedCellMask、1セル=1テクセル)を頂点シェーダーで参照する。
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
            "UniversalMaterialType" = "SimpleLit"
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
            #include "Packages/com.unity.render-pipelines.universal/Shaders/SimpleLitForwardPass.hlsl"

            TEXTURE2D(_LoadedCellMask);
            SAMPLER(sampler_LoadedCellMask);
            float4 _LoadedCellMaskParams; // x: 1/cellSize, y: セルオフセットX, z: セルオフセットY
            float4 _LoadedCellMaskTexel;  // x: 1/セル数X, y: 1/セル数Y
            float _FarSinkAmount;

            // per-cellタイルがロード済みのセルでは頂点を _FarSinkAmount 沈めてタイルへの突き抜けを隠す。
            // メッシュは原点固定なのでオブジェクト座標=ワールド座標として扱える。
            void ApplyFarSink(inout float3 positionOS)
            {
                float2 cellIdx = floor(positionOS.xz * _LoadedCellMaskParams.x) + _LoadedCellMaskParams.yz;
                float2 uv = (cellIdx + 0.5) * _LoadedCellMaskTexel.xy;
                float mask = SAMPLE_TEXTURE2D_LOD(_LoadedCellMask, sampler_LoadedCellMask, uv, 0).r;
                positionOS.y -= step(0.5, mask) * _FarSinkAmount;
            }

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

            TEXTURE2D(_LoadedCellMask);
            SAMPLER(sampler_LoadedCellMask);
            float4 _LoadedCellMaskParams; // x: 1/cellSize, y: セルオフセットX, z: セルオフセットY
            float4 _LoadedCellMaskTexel;  // x: 1/セル数X, y: 1/セル数Y
            float _FarSinkAmount;

            // per-cellタイルがロード済みのセルでは頂点を _FarSinkAmount 沈めてタイルへの突き抜けを隠す。
            // メッシュは原点固定なのでオブジェクト座標=ワールド座標として扱える。
            void ApplyFarSink(inout float3 positionOS)
            {
                float2 cellIdx = floor(positionOS.xz * _LoadedCellMaskParams.x) + _LoadedCellMaskParams.yz;
                float2 uv = (cellIdx + 0.5) * _LoadedCellMaskTexel.xy;
                float mask = SAMPLE_TEXTURE2D_LOD(_LoadedCellMask, sampler_LoadedCellMask, uv, 0).r;
                positionOS.y -= step(0.5, mask) * _FarSinkAmount;
            }

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

            TEXTURE2D(_LoadedCellMask);
            SAMPLER(sampler_LoadedCellMask);
            float4 _LoadedCellMaskParams; // x: 1/cellSize, y: セルオフセットX, z: セルオフセットY
            float4 _LoadedCellMaskTexel;  // x: 1/セル数X, y: 1/セル数Y
            float _FarSinkAmount;

            // per-cellタイルがロード済みのセルでは頂点を _FarSinkAmount 沈めてタイルへの突き抜けを隠す。
            // メッシュは原点固定なのでオブジェクト座標=ワールド座標として扱える。
            void ApplyFarSink(inout float3 positionOS)
            {
                float2 cellIdx = floor(positionOS.xz * _LoadedCellMaskParams.x) + _LoadedCellMaskParams.yz;
                float2 uv = (cellIdx + 0.5) * _LoadedCellMaskTexel.xy;
                float mask = SAMPLE_TEXTURE2D_LOD(_LoadedCellMask, sampler_LoadedCellMask, uv, 0).r;
                positionOS.y -= step(0.5, mask) * _FarSinkAmount;
            }

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

            TEXTURE2D(_LoadedCellMask);
            SAMPLER(sampler_LoadedCellMask);
            float4 _LoadedCellMaskParams; // x: 1/cellSize, y: セルオフセットX, z: セルオフセットY
            float4 _LoadedCellMaskTexel;  // x: 1/セル数X, y: 1/セル数Y
            float _FarSinkAmount;

            // per-cellタイルがロード済みのセルでは頂点を _FarSinkAmount 沈めてタイルへの突き抜けを隠す。
            // メッシュは原点固定なのでオブジェクト座標=ワールド座標として扱える。
            void ApplyFarSink(inout float3 positionOS)
            {
                float2 cellIdx = floor(positionOS.xz * _LoadedCellMaskParams.x) + _LoadedCellMaskParams.yz;
                float2 uv = (cellIdx + 0.5) * _LoadedCellMaskTexel.xy;
                float mask = SAMPLE_TEXTURE2D_LOD(_LoadedCellMask, sampler_LoadedCellMask, uv, 0).r;
                positionOS.y -= step(0.5, mask) * _FarSinkAmount;
            }

            Varyings VertDepthOnlyFar(Attributes input)
            {
                ApplyFarSink(input.positionOS.xyz);
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

            TEXTURE2D(_LoadedCellMask);
            SAMPLER(sampler_LoadedCellMask);
            float4 _LoadedCellMaskParams; // x: 1/cellSize, y: セルオフセットX, z: セルオフセットY
            float4 _LoadedCellMaskTexel;  // x: 1/セル数X, y: 1/セル数Y
            float _FarSinkAmount;

            // per-cellタイルがロード済みのセルでは頂点を _FarSinkAmount 沈めてタイルへの突き抜けを隠す。
            // メッシュは原点固定なのでオブジェクト座標=ワールド座標として扱える。
            void ApplyFarSink(inout float3 positionOS)
            {
                float2 cellIdx = floor(positionOS.xz * _LoadedCellMaskParams.x) + _LoadedCellMaskParams.yz;
                float2 uv = (cellIdx + 0.5) * _LoadedCellMaskTexel.xy;
                float mask = SAMPLE_TEXTURE2D_LOD(_LoadedCellMask, sampler_LoadedCellMask, uv, 0).r;
                positionOS.y -= step(0.5, mask) * _FarSinkAmount;
            }

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
