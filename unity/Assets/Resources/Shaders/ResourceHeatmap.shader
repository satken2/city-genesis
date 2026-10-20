Shader "Custom/ResourceHeatmap"
{
    Properties
    {
        // ノイズの空間周波数（1ワールドメートルあたりのUV量）。
        // 大きいほどノイズ柄が細かくなる。
        _NoiseScale ("Noise Scale", Float) = 0.025
        // ノイズがアルファに与える影響の強さ(0..1)。0=ノイズ無し、1=ノイズ全効き。
        _NoiseStrength ("Noise Strength", Range(0, 1)) = 0.85
        // ノイズ柄の「最も薄い部分」でもこれ以上は薄くならないようにする下限(0..1)。
        // 雲が途切れて完全に消えないようにするためのクランプ。
        _NoiseMin ("Noise Min", Range(0, 1)) = 0.25
    }
    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+50"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ResourceHeatmapPass"

            // メッシュは地形よりわずかに上(HEIGHT_OFFSET)に浮かんでいるため、
            // 通常の深度テストで地形と正しく前後関係が解決する。
            ZTest LEqual
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                // 頂点カラー: RGB=資源タイプ別のベース色、A=エッジフェード(0..1)
                float4 color : COLOR;
                // UV=ワールドXZ座標をそのまま格納(シェーダー側で_NoiseScaleを乗算)
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            float _NoiseScale;
            float _NoiseStrength;
            float _NoiseMin;

            // 2次元ハッシュ関数。プロシージャルノイズのシードとして使用。
            float hash22(float2 p)
            {
                p = float2(dot(p, float2(127.1, 311.7)),
                           dot(p, float2(269.5, 183.3)));
                return frac(sin(p) * 43758.5453);
            }

            // 値ノイズ。格子点4つで補間した滑らかなノイズ。
            float valueNoise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                // smoothstepで滑らかな補間にする(エルミート補間)
                f = f * f * (3.0 - 2.0 * f);
                float a = hash22(i);
                float b = hash22(i + float2(1.0, 0.0));
                float c = hash22(i + float2(0.0, 1.0));
                float d = hash22(i + float2(1.0, 1.0));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // fBm(fractal Brownian motion): 複数周波数のノイズを重ねて雲のような模様を作る。
            // 入力pは既に_NoiseScale済みであること。
            float fbm(float2 p)
            {
                float v = 0.0;
                float a = 0.5;
                [unroll] for (int i = 0; i < 4; i++)
                {
                    v += a * valueNoise(p);
                    p *= 2.0;
                    a *= 0.5;
                }
                return v;
            }

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                // fBmで0..1のノイズ値を生成
                float n = fbm(input.uv * _NoiseScale);
                // smoothstepでコントラストを強調し、雲の「塊感」を出す
                float clumped = smoothstep(0.15, 0.85, n);
                // ノイズ強度でブレンド。_NoiseMinで下限を設けて完全消滅を防ぐ。
                float noiseAlpha = lerp(_NoiseMin, 1.0, clumped);
                float modulated = lerp(1.0, noiseAlpha, _NoiseStrength);

                // 頂点alpha(エッジフェード)とノイズの乗算で最終アルファ
                float finalAlpha = input.color.a * modulated;
                return half4(input.color.rgb, saturate(finalAlpha));
            }
            ENDHLSL
        }
    }
}
