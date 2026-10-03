#ifndef CITY_WATER_INCLUDED
#define CITY_WATER_INCLUDED

// ================================================================
// 半透明の水面シェーディング(Custom/WaterLit の本体)
// ================================================================
// 海面・湖面(Y=一定の水平メッシュ)と川を、1つの ForwardLit パスで描く。
//
//  1. さざ波:      プロシージャルノイズ(fbm)の高さ場から法線を生成し、
//                   時間でスクロールさせる。テクスチャ不要・タイリング継ぎ目なし。
//  2. 水中の減衰:  深度テクスチャから水底のワールド座標を復元して鉛直の水深を求め、
//                   光が水底まで下りて視線方向へ上ってくる距離で、波長ごとに減衰させる。
//                   赤が先に吸収されるので、浅瀬は水底が青緑に色づいて見え、深くなると
//                   水底は見えなくなって水自身の体色(_DeepColor)だけが残る。
//  3. 岸のフォーム: 水深が浅い(岸ぎわ)ピクセルに、アニメする泡ノイズを乗せる。
//  4. 屈折:         _CameraOpaqueTexture を波の法線でずらしてサンプリングし、水底を歪ませる。
//                   ずらした先が水面より上(手前の物体・空)なら水中ではないので、ずらさない。
//  5. 反射/太陽:    水の正面反射率(約2%)で URP の UniversalFragmentPBR を呼び、
//                   フレネルによる空の映り込みと太陽のグレアを物理ベースで得る。
//
// 水底の取得色は照明・大気フォグを含む描画済みの色なので、アルベドとして再照明せず、
// PBR の結果へ「水面で反射されずに水中へ入り、戻ってきた割合」だけ足す。
//
// 半透明(Transparent キュー・ZWrite Off)で描画するため、岸の水際は
// 水深でソフトにフェードし、地形との Z-fighting は視認できない。
// (不透明時代の Offset -1,-1 は互換のため残すが、水際の見た目は深度フェードが担う)
//
// 前提: URP アセットで Depth Texture / Opaque Texture が有効であること
//       (PC_RPAsset は m_RequireDepthTexture=1 / m_RequireOpaqueTexture=1)。
// ================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
// 水の正面反射率(屈折率1.33で約2%)を直接指定するため、Specular ワークフローを使う。
#define _SPECULAR_SETUP 1
// 不透明物の深度から作る SSAO・スクリーン空間の影は水底のものなので、水面には適用しない。
#define _SURFACE_TYPE_TRANSPARENT 1
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
#include "Assets/Shaders/WaterNoise.hlsl"

#define WATER_REFLECTANCE 0.02
#define WATER_IOR 1.33

// --- マテリアルプロパティ(SRP Batcher 互換のため UnityPerMaterial にまとめる) ---
CBUFFER_START(UnityPerMaterial)
    float4 _DeepColor;          // 水底が見えない深さの水の体色(拡散反射率)
    float4 _Attenuation;        // 水中の光の減衰係数(1/m)。xyz=RGB
    float4 _FoamColor;          // 岸の泡の色
    float  _FoamDistance;       // 岸の泡が発生する水深(m)。これより浅いと泡が出る
    float  _FoamNoiseScale;     // 泡ノイズの空間周期(1/m)
    float  _FoamCutoff;         // 泡マスクの閾値(小さいほど泡が広がる)
    float  _RefractionStrength; // 屈折によるスクリーンUVのずらし量(画面比)
    float  _EdgeFadeDistance;   // 水際のソフトフェード距離(m)。0で不透明に立ち上がる
    float  _Smoothness;         // 水面のスムースネス(高いほど鋭い反射・太陽グレア)
    float  _RippleScale;        // さざ波ノイズの空間周期(1/m)
    float  _RippleSpeed;        // さざ波のスクロール速度
    float  _RippleStrength;     // 法線の傾き強度(0で完全に平ら)
    float  _RippleDetail;       // 2枚目(細波)の相対スケール
    float  _FlowEnabled;        // 1 で頂点UV2の流下方向へさざ波・泡を流す(川)。海面・湖面は0
    float  _FlowSpeed;          // 流れの速度(m/s)
CBUFFER_END

// 波の高さ場(ワールドXZ・時間 t)。2枚のスクロールする fbm を重ねる。
// 川では呼び出し側が worldXZ を流下方向へ時間比例でずらして渡す(流れの表現)。
float WaterHeight(float2 worldXZ, float t)
{
    float2 p = worldXZ * _RippleScale;
    float2 dir1 = float2(1.0, 0.6);
    float2 dir2 = float2(-0.7, 1.0);
    float h = WaterFbm(p + dir1 * (t * _RippleSpeed));
    h += 0.5 * WaterFbm(p * _RippleDetail + dir2 * (t * _RippleSpeed * 1.7));
    return h;
}

// 高さ場の中央差分からワールド法線を求める(水面は水平なので up=+Y)。
float3 WaterNormalWS(float2 worldXZ, float t)
{
    float e = 0.6; // サンプル間隔(m)
    float hL = WaterHeight(worldXZ - float2(e, 0), t);
    float hR = WaterHeight(worldXZ + float2(e, 0), t);
    float hD = WaterHeight(worldXZ - float2(0, e), t);
    float hU = WaterHeight(worldXZ + float2(0, e), t);
    float3 n = float3((hL - hR) * _RippleStrength, 1.0, (hD - hU) * _RippleStrength);
    return normalize(n);
}

// 深度テクスチャの画素を逆ビュー投影でワールド座標へ戻す。透視投影・正射影(真上モード)の
// どちらでも同じ水深になる。不透明物の無い画素(クリア値)はファークリップ面上の点になり、
// 水面よりはるか下に来るので、特別扱いせずに「水底が見えない深さ」として扱える。
float3 WaterScenePositionWS(float2 screenUV)
{
#if UNITY_REVERSED_Z
    float rawDepth = SampleSceneDepth(screenUV);
#else
    float rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, SampleSceneDepth(screenUV));
#endif
    return ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);
}

// ---------------- 頂点/フラグメント ----------------

struct Attributes
{
    float4 positionOS : POSITION;
    float2 uv1        : TEXCOORD1; // 流下方向(XZ単位ベクトル)。川の水面メッシュだけが持つ(他は0)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float4 screenPos  : TEXCOORD1; // positionNDC(ComputeScreenPos 済み)
    float2 flowDir    : TEXCOORD3; // 流下方向(_FlowEnabled=0 なら零ベクトル)
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

Varyings WaterVertex(Attributes IN)
{
    Varyings OUT = (Varyings)0;
    UNITY_SETUP_INSTANCE_ID(IN);
    UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

    VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
    OUT.positionCS = vpi.positionCS;
    OUT.positionWS = vpi.positionWS;
    OUT.screenPos  = vpi.positionNDC;
    OUT.flowDir    = IN.uv1 * _FlowEnabled;
    return OUT;
}

half4 WaterFragment(Varyings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);

    float3 positionWS = IN.positionWS;
    float3 viewDirWS  = GetWorldSpaceNormalizeViewDir(positionWS);
    // 川の流れ: ノイズの参照座標を流下方向へ時間比例でずらすと、さざ波・泡が下流へ動いて見える
    float2 flowOffset = IN.flowDir * (_Time.y * _FlowSpeed);
    float2 noiseXZ    = positionWS.xz - flowOffset;
    float3 normalWS   = WaterNormalWS(noiseXZ, _Time.y);

    // --- 水底と鉛直の水深 ---
    float2 screenUV   = IN.screenPos.xy / IN.screenPos.w;
    float3 bottomWS   = WaterScenePositionWS(screenUV);
    float  waterDepth = max(0.0, positionWS.y - bottomWS.y);

    // --- 屈折: 波の法線でスクリーンUVをずらして水底を歪ませる ---
    float2 refractUV       = screenUV + normalWS.xz * _RefractionStrength;
    float3 refractBottomWS = WaterScenePositionWS(refractUV);
    if (refractBottomWS.y > positionWS.y)
    {
        refractUV = screenUV;
        refractBottomWS = bottomWS;
    }
    float  refractDepth = max(0.0, positionWS.y - refractBottomWS.y);
    float3 refracted    = SampleSceneColor(refractUV).rgb;

    // --- 水中の減衰: 光は水深ぶん下りて水底で反射し、屈折した視線に沿って水面へ戻る ---
    float cosAir   = saturate(viewDirWS.y);
    float cosWater = sqrt(1.0 - (1.0 - cosAir * cosAir) / (WATER_IOR * WATER_IOR));
    float3 transmittance = exp(-_Attenuation.rgb * refractDepth * (1.0 + 1.0 / cosWater));

    // --- 岸のフォーム: 水深が浅いピクセルにアニメする泡ノイズを乗せる ---
    float foamBand  = 1.0 - saturate(waterDepth / max(_FoamDistance, 0.01));
    float foamNoise = WaterFbm(noiseXZ * _FoamNoiseScale + _Time.y * 0.25);
    float foamMask  = smoothstep(_FoamCutoff, 1.0, foamBand * foamNoise);

    // 水自身が散乱して返す光は、水底まで届いて水底の色に置き換わった割合だけ弱まる。
    float3 bodyColor = lerp(_DeepColor.rgb * (1.0 - transmittance), _FoamColor.rgb, foamMask);

    // --- URP の物理ベースライティングで反射(フレネル)・太陽グレアを付与 ---
    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo     = bodyColor;
    surfaceData.specular   = WATER_REFLECTANCE.xxx;
    surfaceData.smoothness = lerp(_Smoothness, 0.1, foamMask); // 泡はざらつく
    surfaceData.occlusion  = 1.0;
    surfaceData.alpha      = 1.0;
    surfaceData.normalTS   = float3(0.0, 0.0, 1.0);

    InputData inputData = (InputData)0;
    inputData.positionWS              = positionWS;
    inputData.normalWS                = normalWS;
    inputData.viewDirectionWS         = viewDirWS;
    inputData.shadowCoord             = TransformWorldToShadowCoord(positionWS);
    // 大きな海面では頂点ごとの霧を補間すると、遠い頂点の霧が近景にも広がる。
    // ピクセルのワールド位置から求め、カメラの向きやメッシュ分割に依存させない。
    inputData.fogCoord                = InitializeInputDataFog(float4(positionWS, 1.0), 0.0);
    inputData.bakedGI                 = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = screenUV;
    inputData.shadowMask              = half4(1, 1, 1, 1);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);

    // --- 水底: 水面で反射されずに入り、減衰して戻ってきた割合だけ足す ---
    // 取得色は水底までの大気フォグを含むので、合成後に水面のフォグを重ねる分の霧色を差し引く。
    float  fresnel     = WATER_REFLECTANCE + (1.0 - WATER_REFLECTANCE) * pow(1.0 - saturate(dot(normalWS, viewDirWS)), 5.0);
    float3 bottomShare = transmittance * (1.0 - fresnel) * (1.0 - foamMask);
    half3  fogShare    = MixFog(half3(0, 0, 0), inputData.fogCoord);
    color.rgb = MixFog(color.rgb, inputData.fogCoord) + bottomShare * (refracted - fogShare);

    // --- 不透明度: 水際だけソフトにフェードさせて地形との境界をなじませる。
    //     水底は上で合成済みなので、それ以外は不透明にする。泡は境界でも残す。
    float edgeFade = saturate(waterDepth / max(_EdgeFadeDistance, 0.01));
    color.a = max(edgeFade, foamMask);
    return color;
}

#endif // CITY_WATER_INCLUDED
