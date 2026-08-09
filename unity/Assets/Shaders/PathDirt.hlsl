#ifndef CITY_PATH_DIRT_INCLUDED
#define CITY_PATH_DIRT_INCLUDED

// ================================================================
// 未舗装路の路面シェーディング(Custom/PathDirt の本体)
// ================================================================
// 舗装路のようにベタ塗りの帯にせず、「地面に踏み固められた土の道」に見せるための
// 手続きシェーディング。テクスチャは一切使わない。
//
//  1. 土の色ムラ:  ワールドXZ座標の fbm で主色↔副色を混ぜ、明るい砂利の斑を散らす。
//                   UVを使わないためタイリングの継ぎ目も反復パターンも出ない。
//  2. 細かい凹凸:  同じノイズの勾配で法線を微小に傾け、平面のノッペリ感を消す。
//  3. 縁の侵食:    頂点カラーのアルファを「縁マスク」として、ワールド座標のノイズで
//                   アルファクリップし、路肩をギザギザに溶かして地面へなじませる。
//
// 【頂点カラーの規約(モデル側の契約)】
//   α = 1.0  路面の中核。絶対に抜けない。
//   α = 0.0  なじませスカートの外端。ほぼ完全に抜ける。
//   その間はスカートを横断する線形グラデーション。
//   **頂点カラーを持たないメッシュには Unity が白(α=1)を供給するため、
//     交差点ポリゴンのような外部生成メッシュは侵食されずそのまま描かれる**
//   (RoadIntersectionMeshGenerator は依存を明示するため白を明示設定している)。
//
// アルファブレンドではなくクリップ(cutout)である理由:
//   路面は地形から 0.2m 浮いた板(RoadPartHeights)なので、半透明にすると板の下の
//   隙間が透ける。クリップなら抜けた部分は地形がそのまま見えるだけで済み、
//   不透明キューのままなので RoadPath.GetOrCreateRoadMaterial の renderQueue 調整や
//   ZWrite 前提も壊れない。
// ================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// --- マテリアルプロパティ(SRP Batcher 互換のため UnityPerMaterial にまとめる) ---
CBUFFER_START(UnityPerMaterial)
    float4 _BaseColor;              // 砂の主色(乾いた地面)
    float4 _SecondColor;            // 砂の副色(踏み固められた締まった地面)。主色との差を広げすぎると湿った土に見える
    float4 _GravelColor;            // 明るい砂利の斑の色
    float  _MacroNoiseScale;        // 主色↔副色の大きなムラの空間周期(1/m)
    float  _DetailNoiseScale;       // 砂利斑と凹凸法線の空間周期(1/m)
    float  _GravelAmount;           // 砂利斑の量(0で斑なし)
    float  _EdgeNoiseScale;         // 縁のギザギザの粒度(1/m)。大きいほど細かい
    float  _EdgeSoftness;           // 頂点αのうち侵食対象とする帯の幅(1でα全域)
    float  _EdgeErosion;            // 帯の外端をどこまで削るか(1で外端は消える)
    float  _EdgeDetailFadeDistance; // この距離でギザギザを平均化しきる(m)
    float  _NormalStrength;         // 凹凸法線の傾き強度(0で完全に平ら)
    float  _Smoothness;             // 土なので低め
    float  _Metallic;               // 土は誘電体なので0
CBUFFER_END

// URP がシャドウキャスター描画時に設定する光源パラメータ(ShadowCaster パス専用)。
float3 _LightDirection;
float3 _LightPosition;

// ---------------- プロシージャルノイズ ----------------
// ワールドXZ座標を入力にするため、メッシュのUV・タイリング・頂点密度に依存しない。
// 路面は画面占有が大きいのでオクターブ数は2に抑えている。

float2 PathDirtHash2(float2 p)
{
    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
    return frac(sin(p) * 43758.5453123) * 2.0 - 1.0;
}

// 勾配(Perlin風)ノイズ。-1..1 程度。
float PathDirtGradNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f); // smoothstep 補間
    float a = dot(PathDirtHash2(i + float2(0, 0)), f - float2(0, 0));
    float b = dot(PathDirtHash2(i + float2(1, 0)), f - float2(1, 0));
    float c = dot(PathDirtHash2(i + float2(0, 1)), f - float2(0, 1));
    float d = dot(PathDirtHash2(i + float2(1, 1)), f - float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// 2オクターブの fbm。0..1 に正規化して返す。
float PathDirtFbm(float2 p)
{
    float v = PathDirtGradNoise(p) * 0.6667;
    v += PathDirtGradNoise(p * 2.0) * 0.3333;
    return saturate(v * 0.5 + 0.5);
}

// ---------------- 縁の侵食 ----------------

// 路肩をノイズで抜く。全パス共通の切り抜き基準であり、ForwardLit だけでなく
// 深度・法線・シャドウの各パスからも同じ引数で呼ぶこと(パスごとに輪郭がずれると
// SSAOや影に抜けたはずの路肩が残る)。
void PathDirtClip(float vertexAlpha, float3 positionWS)
{
    // α が _EdgeSoftness 以上の領域は帯の外＝絶対に抜けない中核。
    float band = saturate(vertexAlpha / max(_EdgeSoftness, 0.001));
    float dissolve = (1.0 - band) * _EdgeErosion;

    // 遠景ではノイズの粒がピクセル以下になりチラつくため、距離でノイズを平均値(0.5)へ
    // 寄せて縁を滑らかな線に戻す(このプロジェクトはMSAA無効でクリップ境界が
    // アンチエイリアスされない)。平均へ寄せる方式なら縁の平均位置が変わらないので、
    // 遠近で道路幅が太った/痩せたように見えるポップが起きない。
    float dist = distance(positionWS, GetCameraPositionWS());
    float fadeStart = _EdgeDetailFadeDistance * 0.5;
    float detail = 1.0 - saturate((dist - fadeStart) / max(_EdgeDetailFadeDistance - fadeStart, 0.001));

    float noise = lerp(0.5, PathDirtFbm(positionWS.xz * _EdgeNoiseScale), detail);
    clip(noise - dissolve);
}

// ---------------- 土の色と凹凸 ----------------

// detailHeight は凹凸法線の中心サンプルとして再利用するため出力で返す。
float3 PathDirtAlbedo(float3 positionWS, out float detailHeight)
{
    float2 p = positionWS.xz;
    float macro = PathDirtFbm(p * _MacroNoiseScale);
    detailHeight = PathDirtFbm(p * _DetailNoiseScale);

    float3 albedo = lerp(_BaseColor.rgb, _SecondColor.rgb, macro);
    float gravel = saturate((detailHeight - (1.0 - _GravelAmount)) / max(_GravelAmount, 0.001));
    return lerp(albedo, _GravelColor.rgb, gravel);
}

// 細かい凹凸の陰影。路面はほぼ水平なので、高さ場の勾配を水平成分としてそのまま
// 幾何法線へ足す近似で足りる(スカートの傾き程度では破綻しない)。
float3 PathDirtPerturbNormal(float3 normalWS, float3 positionWS, float centerHeight)
{
    if (_NormalStrength <= 0.0)
    {
        return normalWS;
    }

    float2 p = positionWS.xz * _DetailNoiseScale;
    float e = 0.4 * _DetailNoiseScale; // 前進差分の刻み(ワールドで0.4m相当)
    float hx = PathDirtFbm(p + float2(e, 0.0));
    float hz = PathDirtFbm(p + float2(0.0, e));

    float3 perturb = float3(centerHeight - hx, 0.0, centerHeight - hz) * _NormalStrength;
    return normalize(normalWS + perturb);
}

// ---------------- ForwardLit ----------------

struct Attributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 color      : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

// 補間子で縁マスクを COLOR セマンティクスのまま運ばないのは、環境によって
// 低精度(固定小数)に丸められスカートのグラデーションが段付くのを避けるため。
struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float  fogFactor  : TEXCOORD2;
    float  edgeMask   : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings PathDirtVertex(Attributes IN)
{
    Varyings OUT = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
    OUT.positionCS = vpi.positionCS;
    OUT.positionWS = vpi.positionWS;
    OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
    OUT.fogFactor = ComputeFogFactor(vpi.positionCS.z);
    OUT.edgeMask = IN.color.a;
    return OUT;
}

half4 PathDirtFragment(Varyings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);

    float3 positionWS = IN.positionWS;
    PathDirtClip(IN.edgeMask, positionWS);

    float detailHeight;
    float3 albedo = PathDirtAlbedo(positionWS, detailHeight);
    float3 normalWS = PathDirtPerturbNormal(NormalizeNormalPerPixel(IN.normalWS), positionWS, detailHeight);

    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo = albedo;
    surfaceData.metallic = _Metallic;
    surfaceData.smoothness = _Smoothness;
    surfaceData.occlusion = 1.0;
    surfaceData.alpha = 1.0;
    surfaceData.normalTS = float3(0.0, 0.0, 1.0);

    InputData inputData = (InputData)0;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalWS;
    inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
    inputData.shadowCoord = TransformWorldToShadowCoord(positionWS);
    inputData.fogCoord = IN.fogFactor;
    inputData.bakedGI = SampleSH(normalWS);
    // SSAO は normalizedScreenSpaceUV から引かれる(PC_Renderer で有効)。
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
    inputData.shadowMask = half4(1, 1, 1, 1);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, IN.fogFactor);
    color.a = 1.0;
    return color;
}

// ---------------- ShadowCaster ----------------

struct ShadowAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    float4 color      : COLOR;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct ShadowVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float  edgeMask   : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

ShadowVaryings PathDirtShadowVertex(ShadowAttributes IN)
{
    ShadowVaryings OUT = (ShadowVaryings)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
    float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
#else
    float3 lightDirectionWS = _LightDirection;
#endif

    OUT.positionWS = positionWS;
    OUT.positionCS = ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS)));
    OUT.edgeMask = IN.color.a;
    return OUT;
}

half4 PathDirtShadowFragment(ShadowVaryings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);
    PathDirtClip(IN.edgeMask, IN.positionWS);
    return 0;
}

// ---------------- DepthOnly / DepthNormals ----------------

struct DepthVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    float  edgeMask   : TEXCOORD2;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

DepthVaryings PathDirtDepthVertex(Attributes IN)
{
    DepthVaryings OUT = (DepthVaryings)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
    OUT.positionCS = vpi.positionCS;
    OUT.positionWS = vpi.positionWS;
    OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
    OUT.edgeMask = IN.color.a;
    return OUT;
}

half4 PathDirtDepthOnlyFragment(DepthVaryings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);
    PathDirtClip(IN.edgeMask, IN.positionWS);
    return 0;
}

half4 PathDirtDepthNormalsFragment(DepthVaryings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);
    PathDirtClip(IN.edgeMask, IN.positionWS);

    float detailHeight = PathDirtFbm(IN.positionWS.xz * _DetailNoiseScale);
    float3 normalWS = PathDirtPerturbNormal(NormalizeNormalPerPixel(IN.normalWS), IN.positionWS, detailHeight);
    return half4(normalWS, 0.0);
}

#endif // CITY_PATH_DIRT_INCLUDED
