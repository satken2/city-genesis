// 石畳の色と凹凸をワールドXZへ投影する。道路タイルの伸縮で石の寸法を変えない。
Shader "Custom/PathPaving"
{
    Properties
    {
        [MainTexture] _BaseMap("Stone Color (tiling per metre)", 2D) = "white" {}
        [MainColor] _BaseColor("Color", Color) = (1,1,1,1)
        [Normal] _BumpMap("Normal", 2D) = "bump" {}
        _BumpScale("Normal Strength", Float) = 0.65
        _Smoothness("Smoothness", Range(0,1)) = 0.15
        _Metallic("Metallic", Range(0,1)) = 0
        [HideInInspector] _Cull("Cull", Float) = 2
        [HideInInspector] _ZWrite("ZWrite", Float) = 1
        [HideInInspector] _Cutoff("Cutoff", Float) = 0.5
        [HideInInspector] _Surface("Surface", Float) = 0
        [HideInInspector] _EmissionColor("Emission", Color) = (0,0,0,0)
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        // 接線も投影方向に揃え、曲線や回転した道路でも法線マップの向きを維持する。
        half4 PavingTangent(half3 n)
        {
            float3 axis = abs(n.x) < .95 ? float3(1,0,0) : float3(0,0,1);
            return half4(normalize(axis - n * dot(axis,n)), -1);
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            Cull Back
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PavingVertex
            #pragma fragment LitPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #define _NORMALMAP 1
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitForwardPass.hlsl"
            Varyings PavingVertex(Attributes input)
            {
                Varyings output = LitPassVertex(input);
                output.uv = TransformObjectToWorld(input.positionOS.xyz).xz * _BaseMap_ST.xy + _BaseMap_ST.zw;
                output.tangentWS = PavingTangent(output.normalWS);
                return output;
            }
            ENDHLSL
        }
        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            Cull Back
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex PavingDepthVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            #define _NORMALMAP 1
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/LitDepthNormalsPass.hlsl"
            Varyings PavingDepthVertex(Attributes input)
            {
                Varyings output = DepthNormalsVertex(input);
                output.uv = TransformObjectToWorld(input.positionOS.xyz).xz * _BaseMap_ST.xy + _BaseMap_ST.zw;
                output.tangentWS = PavingTangent(output.normalWS);
                return output;
            }
            ENDHLSL
        }
    }
    Fallback "Hidden/Universal Render Pipeline/FallbackError"
}
