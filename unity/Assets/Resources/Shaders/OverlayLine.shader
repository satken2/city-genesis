Shader "Custom/OverlayLine"
{
    Properties
    {
        _ZTest ("ZTest", Float) = 4
        _OffsetFactor ("Offset Factor", Float) = -1.0
        _OffsetUnits ("Offset Units", Float) = -20.0
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _DistanceFadeRange ("Distance Fade Range (Full, Hidden)", Vector) = (600, 1000, 0, 0)
        _WorldGrid ("World Grid (Spacing, Line Pixels)", Vector) = (20, 1.5, 0, 0)
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
            #pragma multi_compile_local _ OVERLAY_DISTANCE_FADE
            #pragma multi_compile_local _ OVERLAY_WORLD_GRID

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // 頂点色に乗算する全体色。メッシュを作り直さずに明滅(点滅アニメ)させるために使う。
            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                float4 _DistanceFadeRange;
                float4 _WorldGrid;
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
                #if defined(OVERLAY_DISTANCE_FADE) || defined(OVERLAY_WORLD_GRID)
                    float3 positionWS : TEXCOORD1;
                #endif
                #if defined(OVERLAY_GPU_BILLBOARD)
                    float3 shape : TEXCOORD0;
                #endif
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.color = input.color;
                #if defined(OVERLAY_DISTANCE_FADE) || defined(OVERLAY_WORLD_GRID)
                    output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                #endif
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
                #if defined(OVERLAY_WORLD_GRID)
                    // 矩形境界の各辺ではXかZの一方が一定なので、X+Zで四辺共通の固定格子を作れる。
                    float2 grid = float2(input.positionWS.x + input.positionWS.z, input.positionWS.y) / _WorldGrid.x;
                    float2 gridDistance = abs(frac(grid + .5) - .5) / max(fwidth(grid), .00001);
                    float halfLinePixels = _WorldGrid.y * .5;
                    color.a *= 1 - smoothstep(max(halfLinePixels - .5, 0), halfLinePixels + .5, min(gridDistance.x, gridDistance.y));
                #endif
                #if defined(OVERLAY_DISTANCE_FADE)
                    // 画素ごとの実距離で減衰させ、長い線でも場所に応じて連続した透明度にする。
                    float cameraDistance = distance(input.positionWS, _WorldSpaceCameraPos);
                    color.a *= 1 - smoothstep(_DistanceFadeRange.x, _DistanceFadeRange.y, cameraDistance);
                #endif
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
