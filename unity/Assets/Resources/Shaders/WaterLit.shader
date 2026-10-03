// 半透明の水面シェーダー(海面・湖面用)。
//
// 本体のシェーディングは Assets/Shaders/Water.hlsl に置く。ここでは
// Transparent キューの ForwardLit パスとして、URP のライティング系
// キーワードを宣言して Water.hlsl を include する。
//
//  - さざ波(プロシージャル法線) + 水中の減衰 + 岸のフォーム + 屈折 +
//    フレネル反射/太陽グレアを1パスで行う。詳細は Water.hlsl のヘッダ参照。
//  - 屈折・水中の減衰は URP の Depth/Opaque テクスチャに依存する
//    (PC_RPAsset で両方 Require 済み)。
//  - Z-fighting 回避: 半透明かつ ZWrite Off なので水際は深度フェードで
//    なじませる。旧不透明版と同様に Offset -1,-1 を残し、水面 Y=0 と
//    標高0の地形が同深度でも水側が優先されるようにする。
//
// 用途: シーンの海面板(SeaObject)と、WaterSurfaceManager が生成する
//       湖面メッシュ(海面 material を借用)、RiverSurfaceManager が生成する
//       川の水面メッシュ(海面 material の複製に _FlowEnabled=1 を設定)。
Shader "Custom/WaterLit"
{
    Properties
    {
        [Header(Water Body)]
        _DeepColor("Deep Color", Color) = (0.02, 0.09, 0.16, 1)
        _Attenuation("Attenuation RGB (1/m)", Vector) = (0.8, 0.45, 0.4, 0)
        _FoamColor("Foam Color", Color) = (0.95, 0.98, 1.0, 1)

        [Header(Depth Response)]
        _FoamDistance("Foam Distance (m)", Float) = 3.0
        _EdgeFadeDistance("Edge Fade Distance (m)", Float) = 0.6

        [Header(Foam)]
        _FoamNoiseScale("Foam Noise Scale (1/m)", Float) = 0.15
        _FoamCutoff("Foam Cutoff", Range(0.0, 1.0)) = 0.55

        [Header(Refraction)]
        _RefractionStrength("Refraction Strength", Range(0.0, 0.2)) = 0.04

        [Header(Surface)]
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.6

        [Header(Ripples)]
        _RippleScale("Ripple Scale (1/m)", Float) = 0.08
        _RippleSpeed("Ripple Speed", Float) = 0.05
        _RippleStrength("Ripple Strength", Range(0.0, 4.0)) = 1.2
        _RippleDetail("Ripple Detail Scale", Float) = 3.0

        [Header(Flow (rivers))]
        // 1 で頂点 UV2 の流下方向(XZ単位ベクトル)へさざ波・泡を流す。海面・湖面は 0 のまま。
        _FlowEnabled("Flow Enabled", Float) = 0.0
        _FlowSpeed("Flow Speed (m/s)", Float) = 1.5
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
        LOD 300

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            // 半透明合成。水際のフェードと泡は自前の alpha で制御する。
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            // 水面 Y=0 と標高0の地形が同深度でも水側を手前に扱う(旧不透明版と同趣旨)。
            Offset -1, -1

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex WaterVertex
            #pragma fragment WaterFragment

            #pragma multi_compile_instancing

            // URP ライティング(反射・太陽グレア・影・フォグ)に必要なキーワード
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

            #include "Assets/Shaders/Water.hlsl"
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
