Shader "Custom/FireParticle"
{
    // 火災・失火の炎/煙/火の粉パーティクル用シェーダー。
    // アルファは常に前乗算するため、通常合成と加算合成の切り替えは _DstBlend だけで済む
    // (煙 = OneMinusSrcAlpha(10)、炎・火の粉 = One(1))。
    Properties
    {
        _BaseMap ("Particle Texture", 2D) = "white" {}
        _DstBlend ("Dst Blend", Float) = 10
        _SoftFadeDistance ("Soft Fade Distance", Float) = 4.0
        _NearFadeStart ("Near Fade Start", Float) = 1.5
        _NearFadeEnd ("Near Fade End", Float) = 7.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
        }

        Pass
        {
            Name "FireParticlePass"

            ZWrite Off
            Cull Off
            Blend One [_DstBlend]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half _SoftFadeDistance;
                half _NearFadeStart;
                half _NearFadeEnd;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
                half4  color      : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float4 screenPos   : TEXCOORD1;
                float  eyeDepth    : TEXCOORD2;
                half4  color       : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positions = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = positions.positionCS;
                output.screenPos = ComputeScreenPos(positions.positionCS);
                output.eyeDepth = -TransformWorldToView(positions.positionWS).z;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * input.color;

                // ソフトパーティクル。板ポリが建物や地形を貫いた断面の直線を隠す。
                float2 screenUV = input.screenPos.xy / max(input.screenPos.w, 1e-5);
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                col.a *= saturate((sceneDepth - input.eyeDepth) / max(_SoftFadeDistance, 1e-3));

                // カメラに寄りすぎた粒子は画面全体を覆ってしまうため薄くする。
                col.a *= smoothstep(_NearFadeStart, _NearFadeEnd, input.eyeDepth);

                col.rgb *= col.a;
                return col;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
