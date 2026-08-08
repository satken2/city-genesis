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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 頂点色に乗算する全体色。メッシュを作り直さずに明滅(点滅アニメ)させるために使う。
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                return input.color * _Tint;
            }
            ENDHLSL
        }
    }
}
