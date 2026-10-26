Shader "CityGenesis/Forest"
{
    Properties
    {
        _BaseMap("Color", 2D) = "white" {}
        _BaseColor("Tint", Color) = (1,1,1,1)
        _Smoothness("Smoothness", Range(0,1)) = 0.1
        _Metallic("Metallic", Range(0,1)) = 0
        _UndergroundAlpha("Underground opacity", Range(0,1)) = 1
        _ZWrite("Depth write", Float) = 1
        _Foliage("Foliage shading", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        #include "Assets/Shaders/ForestCoverage.hlsl"
        #include "Assets/Shaders/Foliage.hlsl"
        struct ForestPoint { float4 positionScale; float4 rotationVisible; };
        StructuredBuffer<ForestPoint> _ForestPoints;
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        float4 _BaseColor, _BaseMap_ST;
        half _Smoothness, _Metallic, _UndergroundAlpha, _Foliage;
        float4x4 _PartLocal, _PartNormal;
        float3 _ForestCamera;
        float _DetailDistance, _TransitionWidth, _MaxDistance;
        int _ForestLod;
        float3 _LightDirection;
        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; half4 color : COLOR; };
        struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float2 uv : TEXCOORD2;
            float3 clumpPosition : TEXCOORD3; float3 origin : TEXCOORD4; half shade : TEXCOORD5; };
        float3 Rotate(float3 p, float angle)
        {
            float s,c; sincos(angle,s,c);
            return float3(c*p.x+s*p.z,p.y,-s*p.x+c*p.z);
        }
        Varyings ForestVertex(Attributes input, uint id : SV_InstanceID)
        {
            Varyings o = (Varyings)0;
            ForestPoint p = _ForestPoints[id];
            float distance = length(p.positionScale.xyz-_ForestCamera);
            float threshold = _DetailDistance + (p.rotationVisible.z-.5)*_TransitionWidth;
            bool near = distance < threshold;
            float modelWeight = ForestModelWeight(p.positionScale.xyz, _ForestCamera);
            if (p.rotationVisible.y < .5 || distance >= _MaxDistance || near != (_ForestLod == 0)
                || modelWeight <= p.rotationVisible.w)
            {
                o.positionCS = float4(2,2,2,1);
                return o;
            }
            o.clumpPosition = mul(_PartLocal,float4(input.positionOS,1)).xyz;
            o.positionWS = p.positionScale.xyz + Rotate(o.clumpPosition * p.positionScale.w,p.rotationVisible.x);
            o.normalWS = normalize(Rotate(mul((float3x3)_PartNormal,input.normalOS),p.rotationVisible.x));
            o.positionCS = TransformWorldToHClip(o.positionWS);
            o.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
            o.origin = p.positionScale.xyz;
            o.shade = input.color.r;
            return o;
        }
        half4 ForestFragment(Varyings input) : SV_Target
        {
            // 配置だけを独自処理とし、陰影は本編のURP Litと、葉は他の木の Foliage と共通にする。
            InputData lighting = FoliageInputData(input.positionWS,input.normalWS,input.positionCS);
            half3 albedo = lerp(SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,input.uv).rgb,
                FoliageAlbedo(TEXTURE2D_ARGS(_BaseMap,sampler_BaseMap),input.positionWS,lighting.normalWS),_Foliage) * _BaseColor.rgb;
            half3 color = FoliageShade(lighting,albedo,_Smoothness,_Metallic,input.shade,input.clumpPosition,input.origin,_Foliage);
            // Terrain/Litと同じビュー空間距離を使い、正投影でも霧の濃さを揃える。
            float fog = InitializeInputDataFog(float4(input.positionWS,1),0);
            return half4(MixFog(color,fog),_UndergroundAlpha);
        }
        half4 DepthFragment(Varyings input) : SV_Target { return 0; }
        half4 NormalFragment(Varyings input) : SV_Target { return half4(normalize(input.normalWS),0); }
        Varyings ShadowVertex(Attributes input, uint id : SV_InstanceID)
        {
            Varyings o = ForestVertex(input,id);
            if (o.positionCS.w == 1 && all(o.positionCS.xyz == 2)) return o;
            o.positionCS = TransformWorldToHClip(ApplyShadowBias(o.positionWS,o.normalWS,_LightDirection));
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
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_ZWrite]
            HLSLPROGRAM
            #pragma vertex ForestVertex
            #pragma fragment ForestFragment
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
            #pragma vertex ForestVertex
            #pragma fragment DepthFragment
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals" Tags { "LightMode"="DepthNormalsOnly" }
            HLSLPROGRAM
            #pragma vertex ForestVertex
            #pragma fragment NormalFragment
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster" Tags { "LightMode"="ShadowCaster" } ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVertex
            #pragma fragment DepthFragment
            ENDHLSL
        }
    }
}
