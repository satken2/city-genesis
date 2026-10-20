// 区画データ可視化フィル用の地形追従オーバーレイシェーダー。
//
// OverlayLine(平板ポリゴン)との違いは、GPUテッセレーションで三角形を細分化し、
// 生成された全頂点を _TerrainHeightmap(HeightmapTextureManager が供給)の地形高さ
// + _TerrainLift に張り付けること。CPU側は区画ポリゴンの単純な三角形分割を投げるだけで、
// 三角形の内部を含む全域が地形+εの高さになるため、深度テストあり(LEqual)でも
// 地形に埋もれない(=建物には隠れるが地形の起伏で水たまり/穴あきにならない)。
//
// _GpuTerrainConform が 0(テクスチャ未アップロード)の間は頂点の元のY座標を使う
// フォールバックとなり、その間だけ従来同等の平板描画になる。
Shader "Custom/OverlayFillTerrainConform"
{
    Properties
    {
        _ZTest ("ZTest", Float) = 4
        _OffsetFactor ("Offset Factor", Float) = -1.0
        _OffsetUnits ("Offset Units", Float) = -4.0
        _Tint ("Tint", Color) = (1, 1, 1, 1)
        _TerrainLift ("Terrain Lift (m)", Float) = 0.3
        _TessEdgeLength ("Tessellation Edge Length (m)", Float) = 2.0
        _MaxTessFactor ("Max Tessellation Factor", Float) = 48.0
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
            Name "OverlayFillTerrainConformPass"

            ZTest [_ZTest]
            Offset [_OffsetFactor], [_OffsetUnits]
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off

            HLSLPROGRAM
            #pragma target 4.6
            #pragma vertex vert
            #pragma hull hull
            #pragma domain domain
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Shaders/TerrainHeightmapConform.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
                float _TerrainLift;
                float _TessEdgeLength;
                float _MaxTessFactor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 color : COLOR;
            };

            struct ControlPoint
            {
                float3 positionWS : INTERNALTESSPOS;
                float4 color : COLOR;
            };

            struct TessFactors
            {
                float edge[3] : SV_TessFactor;
                float inside : SV_InsideTessFactor;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color : COLOR;
            };

            ControlPoint vert(Attributes input)
            {
                ControlPoint output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.color = input.color;
                return output;
            }

            // 辺の分割数はワールドXZ長のみから決める。同じ辺を共有する隣接三角形は
            // 同じ2点から同じ係数を得るため、細分化後もクラック(隙間)が生じない。
            float EdgeTessFactor(float3 a, float3 b)
            {
                float len = distance(a.xz, b.xz);
                return clamp(len / max(_TessEdgeLength, 0.25), 1.0, _MaxTessFactor);
            }

            TessFactors PatchConstants(InputPatch<ControlPoint, 3> patch)
            {
                TessFactors f;
                // SV_TessFactor[i] は頂点iの対辺に対応する
                f.edge[0] = EdgeTessFactor(patch[1].positionWS, patch[2].positionWS);
                f.edge[1] = EdgeTessFactor(patch[2].positionWS, patch[0].positionWS);
                f.edge[2] = EdgeTessFactor(patch[0].positionWS, patch[1].positionWS);
                f.inside = max(f.edge[0], max(f.edge[1], f.edge[2]));
                return f;
            }

            [domain("tri")]
            [outputcontrolpoints(3)]
            [outputtopology("triangle_cw")]
            [partitioning("fractional_odd")]
            [patchconstantfunc("PatchConstants")]
            ControlPoint hull(InputPatch<ControlPoint, 3> patch, uint id : SV_OutputControlPointID)
            {
                return patch[id];
            }

            [domain("tri")]
            Varyings domain(TessFactors factors, OutputPatch<ControlPoint, 3> patch,
                            float3 bary : SV_DomainLocation)
            {
                float3 positionWS =
                    patch[0].positionWS * bary.x +
                    patch[1].positionWS * bary.y +
                    patch[2].positionWS * bary.z;
                float4 color =
                    patch[0].color * bary.x +
                    patch[1].color * bary.y +
                    patch[2].color * bary.z;

                // 地形追従: 高さマップが有効な間は元のYを捨てて地形高さ+リフトに張り付ける。
                // 無効時(未アップロード)はCPUが設定した頂点Yのまま描く。
                if (_GpuTerrainConform >= 0.5)
                {
                    positionWS.y = SampleTerrainHeight(positionWS) + _TerrainLift;
                }

                Varyings output;
                output.positionHCS = TransformWorldToHClip(positionWS);
                output.color = color;
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
