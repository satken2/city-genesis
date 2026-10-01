Shader "Custom/ShipWake"
{
    // 船の航跡(船首波と船体沿いの泡・ケルビン波の腕・船尾の乱流)。帯メッシュは Vehicles/ShipWake.cs が組み、
    // 頂点属性(航跡に沿った座標・経過・各成分の幅と強さ)からピクセルごとに泡の形を決める。
    // 泡は水に固定した座標のノイズで描くので、船が進んでも泡はその場に残り、時間とともに網目状に崩れて消える。
    // 海面(Custom/WaterLit, Transparent)の上に重ねるため、キューを1つ後ろにする。
    Properties
    {
        _FoamColor ("Foam Color", Color) = (0.93, 0.96, 0.97, 1)
        _WashColor ("Wash Color", Color) = (0.50, 0.70, 0.72, 1)
        _EdgeFadeDistance ("Edge Fade Distance (m)", Float) = 0.2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+1"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "ShipWakeForward"
            Tags { "LightMode" = "UniversalForward" }

            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Assets/Shaders/WaterNoise.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _FoamColor;           // 泡の色。a は泡の最大の不透明度
                half4 _WashColor;           // 泡を含んで白く濁った水の色。a はその最大の不透明度
                float _EdgeFadeDistance;    // 船体・岸と交わる線をなじませる深度差 [m]
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 wake       : TEXCOORD0; // x: 横位置 [m](右舷が正) y: 水に固定した前後位置 [m] z: 寿命に対する経過の割合 w: ノイズの粒の倍率
                float4 widths     : TEXCOORD1; // x: 乱流の半幅 y: 腕の横位置 z: 船体の半幅 w: 船首波の幅 [m]
                float4 foam       : TEXCOORD2; // x: 乱流の泡 y: 腕の泡 z: 船首波 の濃さ(0〜1)
                float2 tint       : TEXCOORD3; // x: 乱流の白濁 y: 腕の波頭の明るさ(0〜1)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos  : TEXCOORD1;
                float4 wake       : TEXCOORD2;
                float4 widths     : TEXCOORD3;
                float4 foam       : TEXCOORD4;
                float2 tint       : TEXCOORD5;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs vpi = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vpi.positionCS;
                output.positionWS = vpi.positionWS;
                output.screenPos = vpi.positionNDC;
                output.wake = input.wake;
                output.widths = input.widths;
                output.foam = input.foam;
                output.tint = input.tint;
                return output;
            }

            // ノイズ n(0〜1)のうち density の割合を泡にする。濃さが下がると高い値の所だけが残り、泡が途切れていく。
            // far は画素がノイズの粒より粗い度合いで、遠景ではノイズを平均の濃さに置き換えてちらつきを防ぐ。
            float Coverage(float n, float density, float far)
            {
                float spread = saturate((n - 0.5) * 2.2 + 0.5);
                float threshold = 1.0 - density;
                float foam = smoothstep(threshold - 0.07, threshold + 0.07, spread) * saturate(density * 6.0);
                return lerp(foam, density * 0.85, far);
            }

            // 細い稜線が不規則な網目につながるノイズ(0〜1)。崩れかけた泡の筋に使う。
            float Ridge(float2 p)
            {
                return 1.0 - saturate(abs(WaterFbm(p) * 2.0 - 1.0) * 2.4);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float lateral = input.wake.x;
                float along = input.wake.y;
                float age = input.wake.z;
                float grain = input.wake.w;
                float side = abs(lateral);
                float t = _Time.y;
                float2 q = float2(lateral, along) / grain;
                float footprint = max(fwidth(lateral), fwidth(along)) / grain;   // 1画素あたりのメートル(粒の倍率で正規化)
                float far = saturate(footprint / 3.0 - 0.5);

                // 船尾の乱流: 船尾の直後はプロペラがかき回す丸い渦の塊、古くなるほど前後に長い筋と網目に変わる。
                // 帯の縁も前後で揺らがせ、まっすぐな板に見せない。泡の濃さの減衰は ShipWake.cs が頂点に載せる。
                float turbHalf = input.widths.x;
                float warp = WaterFbm(float2(q.x * 0.03, q.y * 0.008)) * 3.0;
                float ragged = 0.35 * WaterFbm(float2(q.y * 0.02 + (lateral > 0.0 ? 0.0 : 17.3), t * 0.01))
                             + 0.25 * WaterFbm(float2(q.y * 0.09 + (lateral > 0.0 ? 5.1 : 0.0), q.x * 0.05 + t * 0.03));
                float edge = turbHalf * (0.7 + ragged);
                float core = turbHalf > 0.01 ? 1.0 - smoothstep(0.3 * edge, edge, side) : 0.0;
                float boil = WaterFbm(float2(q.x * 0.09, q.y * 0.05) + float2(t * 0.05, t * 0.03));
                float streak = WaterFbm(float2(q.x * 0.12 + warp, q.y * 0.006 + t * 0.01));
                float lace = Ridge(float2(q.x * 0.25 + warp, q.y * 0.02 + t * 0.01));
                float turbNoise = lerp(boil, lerp(streak, lace, 0.5), saturate(age * 4.0));
                float turbFoam = Coverage(turbNoise, input.foam.x * core * 0.85, far);
                float washBand = turbHalf > 0.01 ? 1.0 - smoothstep(0.4 * edge, 1.2 * edge, side) : 0.0;
                float wash = input.tint.x * washBand * pow(1.0 - age, 1.3) * lerp(0.25, 0.5, streak);

                // ケルビン波の腕: 航跡の両側の線。泡は斜めに途切れた短い波頭として載せ、その周りの水をわずかに明るくする。
                float armWidth = 0.8 + 0.035 * input.widths.y;
                float armDistance = (side - input.widths.y) / armWidth;
                float armLine = exp(-armDistance * armDistance);
                float crest = WaterFbm(float2((side - input.widths.y) / grain * 0.3 + q.y * 0.12 + (lateral > 0.0 ? 0.0 : 41.7), q.y * 0.05 + t * 0.02));
                float armFoam = Coverage(crest, input.foam.y * armLine * 0.5, far);
                float armSheen = input.tint.y * exp(-armDistance * armDistance * 0.16) * 0.16;

                // 船首波と船体沿いの泡: 船体の外側へ船首波の幅だけ。船体の内側は船体に隠れるので塗ったままでよい。
                float bowWidth = input.widths.w;
                float bowBand = bowWidth > 0.01 ? 1.0 - smoothstep(0.0, 1.0, (side - input.widths.z) / bowWidth) : 0.0;
                float churn = WaterFbm(q * float2(0.4, 0.2) + float2(t * 0.35, -t * 0.2));
                float bowFoam = Coverage(churn, saturate(input.foam.z * 1.3) * bowBand, far);
                float bowWash = input.foam.z * bowBand * 0.3;

                float foam = max(turbFoam, max(armFoam, bowFoam)) * _FoamColor.a;
                float murk = saturate(max(wash, max(armSheen, bowWash))) * _WashColor.a;
                float cover = foam + murk * (1.0 - foam);
                float3 albedo = (_FoamColor.rgb * foam + _WashColor.rgb * murk * (1.0 - foam)) / max(cover, 1e-4);

                float2 screenUV = input.screenPos.xy / input.screenPos.w;
                float sceneEyeDepth = LinearEyeDepth(SampleSceneDepth(screenUV), _ZBufferParams);
                float alpha = cover * saturate((sceneEyeDepth - input.screenPos.w) / max(_EdgeFadeDistance, 0.01));
                clip(alpha - 0.002);

                // 泡は粗い面なので拡散光だけで照らす。船の影に入った泡は暗くなる。白飛びでブルームが出ないよう1で止める。
                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 lighting = light.color * (saturate(light.direction.y) * light.shadowAttenuation) + SampleSH(float3(0.0, 1.0, 0.0));
                float3 color = min(albedo * lighting, 1.0);
                color = MixFog(color, InitializeInputDataFog(float4(input.positionWS, 1.0), 0.0));
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
