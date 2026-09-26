Shader "Custom/RoadSurface"
{
    Properties
    {
        [MainTexture] _BaseMap("Asphalt", 2D) = "white" {}
        [MainColor] _BaseColor("Asphalt Color", Color) = (.16,.16,.16,1)
        _Smoothness("Smoothness", Range(0,1)) = .2
        _Metallic("Metallic", Range(0,1)) = 0
        _BumpMap("Normal", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1
        _MetallicGlossMap("Metallic Map", 2D) = "white" {}
        _SpecGlossMap("Specular Map", 2D) = "white" {}
        _SpecColor("Specular", Color) = (.2,.2,.2,1)
        _OcclusionMap("Occlusion", 2D) = "white" {}
        _OcclusionStrength("Occlusion Strength", Float) = 1
        _EmissionMap("Emission", 2D) = "white" {}
        _EmissionColor("Emission Color", Color) = (0,0,0,0)
        _Cutoff("Cutoff", Float) = .5
        [HideInInspector] _Cull("Cull", Float) = 2
        [HideInInspector] _ZWrite("ZWrite", Float) = 1
        [HideInInspector] _Surface("Surface", Float) = 0
        _RoadPageTable("Marking Page Table", 2D) = "black" {}
        _RoadPaintEnabled("Paint Enabled", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Back ZWrite On Offset -1, -1
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex RoadVertex
            #pragma fragment RoadFragment
            #pragma multi_compile_instancing
            #pragma shader_feature_local _NORMALMAP
            #pragma shader_feature_local_fragment _METALLICSPECGLOSSMAP
            #pragma shader_feature_local_fragment _OCCLUSIONMAP
            #pragma shader_feature_local_fragment _EMISSION
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile_fog
            #include "Assets/Shaders/RoadSurface.hlsl"
            ENDHLSL
        }
        UsePass "Custom/DepthOffsetLit/ShadowCaster"
        UsePass "Custom/DepthOffsetLit/DepthOnly"
        UsePass "Custom/DepthOffsetLit/DepthNormals"
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
