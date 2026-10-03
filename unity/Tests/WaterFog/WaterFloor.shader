// 常駐Terrainと同じ深度オフセットで、海底の有無だけを比較する検証用シェーダー。
Shader "QA/WaterFloor"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry-100" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        float4 Vertex(float4 positionOS : POSITION) : SV_POSITION
        {
            return TransformObjectToHClip(positionOS.xyz);
        }
        half4 Fragment() : SV_Target
        {
            return half4(0.5, 0.5, 0.5, 1.0);
        }
        half4 DepthNormalsFragment() : SV_Target
        {
            return half4(0.0, 1.0, 0.0, 0.0);
        }
        ENDHLSL
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Offset 1, 1
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode" = "DepthNormals" }
            Offset 1, 1
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment DepthNormalsFragment
            ENDHLSL
        }
        Pass
        {
            Tags { "LightMode" = "DepthOnly" }
            Offset 1, 1
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vertex
            #pragma fragment Fragment
            ENDHLSL
        }
    }
}
