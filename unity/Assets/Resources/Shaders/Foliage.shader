Shader "CityGenesis/Foliage"
{
    // 森以外の木・植え込みの葉。葉の陰影は Forest と同じ Foliage.hlsl で、頂点色R(樹冠の陰り)を持つメッシュを前提にする。
    Properties
    {
        [MainTexture] _BaseMap("Color", 2D) = "white" {}
        [MainColor] _BaseColor("Tint", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0,1)) = 0.05
        _Metallic("Metallic", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #pragma target 3.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Assets/Shaders/Foliage.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
        float4 _BaseMap_ST;
        half4 _BaseColor;
        half _Smoothness, _Metallic;
        CBUFFER_END
        float3 _LightDirection, _LightPosition;
        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; half4 color : COLOR; UNITY_VERTEX_INPUT_INSTANCE_ID };
        struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1;
            float3 positionOS : TEXCOORD2; float3 origin : TEXCOORD3; half shade : TEXCOORD4; };
        Varyings FoliageVertex(Attributes input)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            Varyings o = (Varyings)0;
            o.positionWS = TransformObjectToWorld(input.positionOS);
            o.normalWS = TransformObjectToWorldNormal(input.normalOS);
            o.positionCS = TransformWorldToHClip(o.positionWS);
            o.positionOS = input.positionOS;
            o.origin = TransformObjectToWorld(float3(0,0,0));
            o.shade = input.color.r;
            return o;
        }
        half4 FoliageFragment(Varyings input) : SV_Target
        {
            InputData lighting = FoliageInputData(input.positionWS,input.normalWS,input.positionCS);
            half3 albedo = FoliageAlbedo(TEXTURE2D_ARGS(_BaseMap,sampler_BaseMap),input.positionWS,lighting.normalWS) * _BaseColor.rgb;
            half3 color = FoliageShade(lighting,albedo,_Smoothness,_Metallic,input.shade,input.positionOS,input.origin,1);
            // Terrain/Litと同じビュー空間距離を使い、正投影でも霧の濃さを揃える。
            float fog = InitializeInputDataFog(float4(input.positionWS,1),0);
            return half4(MixFog(color,fog),1);
        }
        half4 DepthFragment(Varyings input) : SV_Target { return 0; }
        half4 NormalFragment(Varyings input) : SV_Target { return half4(normalize(input.normalWS),0); }
        Varyings ShadowVertex(Attributes input)
        {
            Varyings o = FoliageVertex(input);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 lightDirection = normalize(_LightPosition - o.positionWS);
            #else
            float3 lightDirection = _LightDirection;
            #endif
            o.positionCS = TransformWorldToHClip(ApplyShadowBias(o.positionWS,o.normalWS,lightDirection));
            #if UNITY_REVERSED_Z
            o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE * o.positionCS.w);
            #else
            o.positionCS.z = max(o.positionCS.z, UNITY_NEAR_CLIP_VALUE * o.positionCS.w);
            #endif
            return o;
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit" Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex FoliageVertex
            #pragma fragment FoliageFragment
            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_ATLAS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly" Tags { "LightMode"="DepthOnly" } ColorMask R
            HLSLPROGRAM
            #pragma vertex FoliageVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals" Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex FoliageVertex
            #pragma fragment NormalFragment
            #pragma multi_compile_instancing
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster" Tags { "LightMode"="ShadowCaster" } ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
    }
}
