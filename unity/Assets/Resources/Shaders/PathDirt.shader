// 未舗装路の路面シェーダー。
//
// 本体のシェーディングは Assets/Shaders/PathDirt.hlsl に置く。ここでは
// AlphaTest キューの各パスとして、URP のライティング系キーワードを宣言して
// PathDirt.hlsl を include する。
//
//  - 土の色ムラ・砂利斑・凹凸法線をワールドXZ座標の手続きノイズで作り、
//    頂点カラーのアルファを縁マスクにして路肩をアルファクリップで溶かす。
//    詳細と頂点カラーの規約は PathDirt.hlsl のヘッダ参照。
//  - 4パス構成: ForwardLit / ShadowCaster / DepthOnly / DepthNormals。
//    DepthNormals は PC_Renderer の SSAO(Source=DepthNormals)が前段パスとして
//    要求するため必須で、省くと未舗装路だけ SSAO と深度テクスチャから抜け落ちる。
//    切り抜きの輪郭が食い違わないよう、全パスが同じ PathDirtClip を通る。
//
// 用途: 未舗装路(path_variant 201)の body。マテリアルは Resources/PATH_SAND.mat。
//       **頂点カラーのアルファを縁マスクとして読むため、このシェーダーは
//       規約どおりに頂点カラーを持つメッシュ専用**(他の道路 body へ流用しないこと)。
Shader "Custom/PathDirt"
{
    Properties
    {
        // 実アルベド相当(概ね0.2〜0.6)のリニア値。地形シェーダーの手続き地面色
        // (TerrainManager の terrainGroundColorA/B/C)と同じ土俵の数値で、乾いた砂の道が
        // 周囲の地面よりわずかに明るく見えるように置いている。彩度を上げると湿った土に見える。
        [Header(Dirt Colors)]
        _BaseColor("Base Color", Color) = (0.66, 0.58, 0.43, 1)
        _SecondColor("Second Color", Color) = (0.52, 0.45, 0.32, 1)
        _GravelColor("Gravel Color", Color) = (0.78, 0.72, 0.58, 1)

        [Header(Surface Noise)]
        _MacroNoiseScale("Macro Noise Scale (1/m)", Float) = 0.08
        _DetailNoiseScale("Detail Noise Scale (1/m)", Float) = 1.2
        _GravelAmount("Gravel Amount", Range(0.0, 1.0)) = 0.35

        [Header(Edge Erosion)]
        _EdgeNoiseScale("Edge Noise Scale (1/m)", Float) = 2.5
        _EdgeSoftness("Edge Band Width", Range(0.05, 1.0)) = 1.0
        _EdgeErosion("Edge Erosion", Range(0.0, 1.0)) = 1.0
        _EdgeDetailFadeDistance("Edge Detail Fade Distance (m)", Float) = 150.0

        [Header(Surface)]
        _NormalStrength("Detail Normal Strength", Range(0.0, 2.0)) = 0.5
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.05
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "TransparentCutout"
            "Queue" = "AlphaTest"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite On
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex PathDirtVertex
            #pragma fragment PathDirtFragment

            #pragma multi_compile_instancing

            // URP ライティング(影・追加光・SSAO・フォグ)に必要なキーワード
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile_fog

            #include "Assets/Shaders/PathDirt.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex PathDirtShadowVertex
            #pragma fragment PathDirtShadowFragment

            #pragma multi_compile_instancing
            // 点光源・スポットライトの影は光源方向が頂点ごとに変わるため分岐が必要。
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Assets/Shaders/PathDirt.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex PathDirtDepthVertex
            #pragma fragment PathDirtDepthOnlyFragment

            #pragma multi_compile_instancing

            #include "Assets/Shaders/PathDirt.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex PathDirtDepthVertex
            #pragma fragment PathDirtDepthNormalsFragment

            #pragma multi_compile_instancing

            #include "Assets/Shaders/PathDirt.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
