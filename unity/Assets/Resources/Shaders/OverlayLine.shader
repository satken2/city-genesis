Shader "Custom/OverlayLine"
{
    Properties
    {
        _ZTest ("ZTest", Float) = 4
        _OffsetFactor ("Offset Factor", Float) = -1.0
        _OffsetUnits ("Offset Units", Float) = -20.0
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        // 加算合成に切り替えるための混合係数。既定は通常のアルファブレンド
        // (SrcAlpha=5 / OneMinusSrcAlpha=10)。DstBlend=One(1) にすると光の加算表現になる。
        _SrcBlend ("Src Blend", Float) = 5
        _DstBlend ("Dst Blend", Float) = 10
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+100"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "OverlayLinePass"

            ZTest [_ZTest]
            Offset [_OffsetFactor], [_OffsetUnits]
            ZWrite Off
            Blend [_SrcBlend] [_DstBlend]
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ OVERLAY_GPU_BILLBOARD

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 頂点色に乗算する全体色。メッシュを作り直さずに明滅(点滅アニメ)させるために使う。
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
                #if defined(OVERLAY_GPU_BILLBOARD)
                    float3 otherEndOS : NORMAL;
                    float4 billboard : TEXCOORD0;
                #endif
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR;
                #if defined(OVERLAY_GPU_BILLBOARD)
                    float3 shape : TEXCOORD0;
                #endif
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                #if defined(OVERLAY_GPU_BILLBOARD)
                {
                    output.shape = float3(input.billboard.xy, input.billboard.w);
                    float2 offset = input.billboard.xy;
                    if (input.billboard.w < .5)
                    {
                        float4 other = TransformObjectToHClip(input.otherEndOS);
                        float2 delta = (other.xy / (max(abs(other.w), .0001) * (other.w < 0 ? -1 : 1))
                            - output.positionHCS.xy / (max(abs(output.positionHCS.w), .0001) * (output.positionHCS.w < 0 ? -1 : 1))) * _ScreenParams.xy;
                        offset = float2(-delta.y, delta.x) * rsqrt(max(dot(delta, delta), .0001)) * input.billboard.x;
                    }
                    output.positionHCS.xy += offset * input.billboard.z * 2 / _ScreenParams.xy * output.positionHCS.w;
                }
                #endif
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 color = input.color * _Tint;
                #if defined(OVERLAY_GPU_BILLBOARD)
                if (input.shape.z > .5)
                {
                    float radius = length(input.shape.xy);
                    color.a *= saturate((1 - radius) / max(fwidth(radius), .001));
                }
                #endif
                return color;
            }
            ENDHLSL
        }
    }
}
