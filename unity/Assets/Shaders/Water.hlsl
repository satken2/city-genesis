#ifndef CITY_WATER_INCLUDED
#define CITY_WATER_INCLUDED

// ================================================================
// 半透明の水面シェーディング(Custom/WaterLit の本体)
// ================================================================
// 海面・湖面を1つの ForwardLit パスで描く。
//
//  1. さざ波:      プロシージャルノイズ(fbm)の高さ場から法線を生成し、
//                   画素より細かい波は平均化し、反射の粗さへ引き継ぐ。
//  2. 深度による色: シーン深度(_CameraDepthTexture)から復元した鉛直水深で、
//                   浅瀬色↔深海色を補間する。
//  3. 岸のフォーム: 水柱が薄い(岸ぎわ・浅瀬)ピクセルに、アニメする泡ノイズを乗せる。
//  4. 屈折:         _CameraOpaqueTexture を波の法線でずらしてサンプリングし、
//                   水中(水底)を歪ませて透かす。前景を吸い込まないよう深度でガードする。
//  5. 反射/太陽:    水の正面反射率と、場所ごとの弱い波立ちの差を使って URP の
//                   UniversalFragmentPBR を呼び、フレネルによる空の映り込みと
//                   太陽のグレア(さざ波で砕けてキラキラする)を物理ベースで得る。
//
// 半透明(Transparent キュー・ZWrite Off)で描画するため、岸の水際は
// シーン深度差でソフトにフェードし、地形との Z-fighting は視認できない。
// (不透明時代の Offset -1,-1 は互換のため残すが、水際の見た目は深度フェードが担う)
//
// 前提: URP アセットで Depth Texture / Opaque Texture が有効であること
//       (PC_RPAsset は m_RequireDepthTexture=1 / m_RequireOpaqueTexture=1)。
// ================================================================

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
// 水の正面反射率は約2%。汎用の非金属材質(4%)より弱い反射を指定する。
#define _SPECULAR_SETUP 1
// 水面はOpaqueの深度へ書かないため、水底由来のSSAOやスクリーン空間の影を受けない。
#define _SURFACE_TYPE_TRANSPARENT 1
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
#include "Assets/Shaders/WaterNoise.hlsl"

// 天気の減光量。未設定の0では晴天と同じ反射・フォグを使う。
float _WaterOvercast;

// --- マテリアルプロパティ(SRP Batcher 互換のため UnityPerMaterial にまとめる) ---
CBUFFER_START(UnityPerMaterial)
    float4 _DeepColor;          // 深海(水柱が厚い)部分の色
    float4 _ShallowColor;       // 浅瀬(水柱が薄い)部分の色
    float4 _FoamColor;          // 岸の泡の色
    float  _DepthColorDistance; // 深海色へ近づく水深の減衰距離(m)
    float  _MurkDistance;       // 水中を通る光が減衰する距離(m)
    float  _MaxTransmission;    // 浅瀬でも水底の色に占有させないための透過率上限
    float  _FoamDistance;       // 岸の泡が発生する水深(m)。これより浅いと泡が出る
    float  _FoamNoiseScale;     // 泡ノイズの空間周期(1/m)
    float  _FoamCutoff;         // 泡マスクの閾値(小さいほど泡が広がる)
    float  _RefractionStrength; // 屈折によるスクリーンUVのずらし量(画面比)
    float  _EdgeFadeDistance;   // 水際のソフトフェード距離(m)。0で不透明に立ち上がる
    float  _Smoothness;         // 水面のスムースネス(高いほど鋭い反射・太陽グレア)
    float  _EnvironmentIntensity; // 天空の反射・拡散環境光の寄与。直射光の光沢には掛けない
    float  _SurfacePatternScale;   // 波立ちが緩やかに変わる範囲(m)
    float  _SurfacePatternStrength; // 広域の波立ちの差。体色に斑を描かず粗さを変える
    float  _RippleScale;        // さざ波ノイズの空間周期(1/m)
    float  _RippleSpeed;        // さざ波のスクロール速度
    float  _RippleStrength;     // 法線の傾き強度(0で完全に平ら)
    float  _RippleDetail;       // 2枚目(細波)の相対スケール
    float  _FlowEnabled;        // 1 で頂点UV2の流下方向へさざ波・泡を流す(川)。海面・湖面は0
    float  _FlowSpeed;          // 流れの速度(m/s)
CBUFFER_END

// 画素が覆う範囲で解像できるオクターブだけを残す。
// 全体の振幅を再正規化すると、遠景でも残った波が強調されて斑になる。
float WaterFilteredFbm(float2 p, float3 weights)
{
    float value = 0.0;
    float amplitude = 0.5;
    [unroll]
    for (int i = 0; i < 3; i++)
    {
        value += amplitude * WaterGradNoise(p) * weights[i];
        p *= 2.0;
        amplitude *= 0.5;
    }
    return value * 0.5 + 0.5;
}

float WaterHeight(float2 worldXZ, float t, float3 rippleWeights, float3 detailWeights)
{
    float2 p = worldXZ * _RippleScale;
    float2 dir1 = float2(1.0, 0.6);
    float2 dir2 = float2(-0.7, 1.0);
    float h = WaterFilteredFbm(p + dir1 * (t * _RippleSpeed), rippleWeights);
    h += 0.5 * WaterFilteredFbm(p * _RippleDetail + dir2 * (t * _RippleSpeed * 1.7), detailWeights);
    return h;
}

// 高さ場の中央差分からワールド法線を求める(水面は水平なので up=+Y)。
float3 WaterNormalWS(float2 worldXZ, float t, float3 rippleWeights, float3 detailWeights, float waveStrength)
{
    float e = 0.6; // サンプル間隔(m)
    float hL = WaterHeight(worldXZ - float2(e, 0), t, rippleWeights, detailWeights);
    float hR = WaterHeight(worldXZ + float2(e, 0), t, rippleWeights, detailWeights);
    float hD = WaterHeight(worldXZ - float2(0, e), t, rippleWeights, detailWeights);
    float hU = WaterHeight(worldXZ + float2(0, e), t, rippleWeights, detailWeights);
    float3 n = float3((hL - hR) * waveStrength, 1.0, (hD - hU) * waveStrength);
    return normalize(n);
}

// 風・流れによる波立ちの違いを、ゆっくり曲がる弱い帯として近似する。
// 色へ直接乗せず、反射の広がりを変えるため、視線と光の向きに応じて見え方が変わる。
float WaterSurfacePattern(float2 worldXZ, float t, float footprint)
{
    float scale = max(_SurfacePatternScale, 1.0);
    float2 p = worldXZ / scale;
    float2 along = float2(0.86, 0.51);
    float2 across = float2(-along.y, along.x);
    float bend = WaterGradNoise(p * 0.7 + 19.3);
    float2 bands = float2(dot(p, along) * 0.4, dot(p, across) * 1.8 + bend);
    float pattern = WaterGradNoise(bands + float2(t * 0.001, 0.0));
    float resolved = 1.0 - smoothstep(0.15, 0.65, footprint * 1.8 / scale);
    return max(0.1, 1.0 + pattern * _SurfacePatternStrength * resolved);
}

// LinearEyeDepth とクリップWの差は透視投影専用なので、真上の正射影でも
// 同じ水深が得られるよう逆投影する。非Reversed-Z環境ではNDCの深度範囲を合わせる。
float3 WaterScenePositionWS(float2 screenUV, float rawDepth)
{
#if !UNITY_REVERSED_Z
    rawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
#endif
    return ComputeWorldSpacePosition(screenUV, rawDepth, UNITY_MATRIX_I_VP);
}

// 深度のクリア値は水底ではない。全景の海には不透明な地形メッシュが無い。
bool WaterHasSceneDepth(float rawDepth)
{
#if UNITY_REVERSED_Z
    return rawDepth > 0.0;
#else
    return rawDepth < 1.0;
#endif
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
    float3 viewDirWS  = normalize(GetCameraPositionWS() - positionWS);
    // 海底を持たない全景と常駐タイルの境界が水色へ現れないよう、
    // 深度に依存する表現はカメラ近傍の常駐範囲(500m)より手前で滑らかに消す。
    float depthDetailWeight = 1.0 - smoothstep(150.0, 450.0, distance(GetCameraPositionWS(), positionWS));
    // 川の流れ: ノイズの参照座標を流下方向へ時間比例でずらすと、さざ波・泡が下流へ動いて見える
    float2 flowOffset = IN.flowDir * (_Time.y * _FlowSpeed);
    float2 noiseXZ    = positionWS.xz - flowOffset;
    // 距離だけでなく解像度・俯角にも追従し、斜めに見た細波のちらつきも抑える。
    float footprint = max(length(ddx(positionWS.xz)), length(ddy(positionWS.xz)));
    float3 frequencies = max(abs(_RippleScale), 0.0001) * float3(1.0, 2.0, 4.0);
    float3 rippleWeights = 1.0 - smoothstep(0.15, 0.65, footprint * frequencies);
    float3 detailWeights = 1.0 - smoothstep(0.15, 0.65, footprint * frequencies * abs(_RippleDetail));
    float pattern = WaterSurfacePattern(noiseXZ, _Time.y, footprint);
    float waveStrength = _RippleStrength * sqrt(pattern);
    float3 normalWS = WaterNormalWS(noiseXZ, _Time.y, rippleWeights, detailWeights, waveStrength);
    float resolvedWaves = dot((rippleWeights * 2.0 + detailWeights) / 3.0, float3(4.0, 2.0, 1.0) / 7.0);
    // 消えた法線の分散を粗さへ足し、遠景が完全な鏡へ変わるのを避ける。
    float roughness = 1.0 - saturate(_Smoothness);
    float roughnessVariance = roughness * roughness * roughness * roughness * pattern;
    roughnessVariance += (1.0 - resolvedWaves) * waveStrength * waveStrength * 0.0015;
    float filteredSmoothness = 1.0 - pow(saturate(roughnessVariance), 0.25);

    // 水色・泡・水際には鉛直水深を使い、カメラを傾けても境界を移動させない。
    float2 screenUV        = IN.screenPos.xy / IN.screenPos.w;
    float  surfaceEyeDepth = -TransformWorldToView(positionWS).z;
    float  sceneRaw        = SampleSceneDepth(screenUV);
    bool   hasSceneDepth   = WaterHasSceneDepth(sceneRaw);
    float3 scenePositionWS = positionWS;
    if (hasSceneDepth) scenePositionWS = WaterScenePositionWS(screenUV, sceneRaw);
    float  waterDepth      = max(0.0, positionWS.y - scenePositionWS.y);

    // --- 屈折: 波の法線でスクリーンUVをずらして水中を透かす ---
    // 手前(水面より近い)物体を吸い込まないよう、ずらした先の深度が水面より
    // 奥にある場合のみ屈折UVを採用する(定番のガード)。
    float2 refractUV      = screenUV + normalWS.xz * _RefractionStrength;
    float  refractRaw     = SampleSceneDepth(refractUV);
    bool   hasRefractDepth = WaterHasSceneDepth(refractRaw);
    float3 refractPositionWS = positionWS;
    if (hasRefractDepth)
    {
        refractPositionWS = WaterScenePositionWS(refractUV, refractRaw);
        float refractEyeDepth = -TransformWorldToView(refractPositionWS).z;
        if (refractEyeDepth < surfaceEyeDepth || refractPositionWS.y > positionWS.y)
        {
            refractUV = screenUV;
            refractPositionWS = scenePositionWS;
            hasRefractDepth = hasSceneDepth;
        }
    }
    float  opticalPath = distance(refractPositionWS, positionWS);
    float3 refracted = 0.0;
    if (hasRefractDepth) refracted = SampleSceneColor(refractUV).rgb;

    // 深海色と吸収を指数減衰でつなぎ、一定深度で突然黒くなる切り替えを避ける。
    // 水底が描かれていない場所は深海色・透過なしとし、空の深度を水底へ逆投影しない。
    float depthColorBlend = hasSceneDepth ? 1.0 - exp(-waterDepth / max(_DepthColorDistance, 0.01)) : 1.0;
    depthColorBlend = lerp(1.0, depthColorBlend, depthDetailWeight);
    float3 waterTint = lerp(_ShallowColor.rgb, _DeepColor.rgb,
                            depthColorBlend);
    float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(normalWS, viewDirWS)), 5.0);
    float transmission = saturate(_MaxTransmission) * exp(-opticalPath / max(_MurkDistance, 0.01)) * (1.0 - fresnel);
    if (!hasRefractDepth) transmission = 0.0;
    transmission *= depthDetailWeight;

    // --- 岸のフォーム: 水柱が薄いピクセルにアニメする泡ノイズを乗せる ---
    float  foamBand  = hasSceneDepth ? 1.0 - saturate(waterDepth / max(_FoamDistance, 0.01)) : 0.0;
    float  foamNoise = WaterFbm(noiseXZ * _FoamNoiseScale + _Time.y * 0.25);
    float  foamMask  = smoothstep(_FoamCutoff, 1.0, foamBand * foamNoise);
    foamMask *= depthDetailWeight;
    transmission *= 1.0 - foamMask;
    float3 bodyColor = lerp(waterTint * (1.0 - transmission), _FoamColor.rgb, foamMask);

    // 曇天では浅い視線角での天空反射の増加を抑える。体色の環境光と直射光は保つ。
    float weatherLight = 1.0 - saturate(_WaterOvercast);
    float grazing = pow(1.0 - saturate(dot(normalWS, viewDirWS)), 4.0);
    float reflectionScale = lerp(1.0, max(weatherLight * weatherLight, 0.02), grazing);
    reflectionScale = lerp(reflectionScale, 1.0, foamMask);

    // --- URP の物理ベースライティングで反射(フレネル)・太陽グレアを付与 ---
    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo     = bodyColor;
    surfaceData.specular   = lerp(0.02, 0.04, foamMask).xxx;
    surfaceData.smoothness = lerp(filteredSmoothness, 0.1, foamMask); // 泡はざらつく
    surfaceData.occlusion  = lerp(saturate(_EnvironmentIntensity), 1.0, foamMask) * reflectionScale;
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
    // URPのocclusionは反射と拡散環境光の両方に掛かるため、拡散側だけ相殺する。
    inputData.bakedGI                 = SampleSH(normalWS) / reflectionScale;
    inputData.normalizedScreenSpaceUV = screenUV;
    inputData.shadowMask              = half4(1, 1, 1, 1);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);

    // 水底の取得色には照明と大気フォグが含まれるため、PBRで再照明しない。
    // 水底が占める割合の霧色を差し引いて合成し、大気フォグの重ね掛けを避ける。
    // 陸地と同じ霧色・距離で合成し、天気による反射の減光を大気フォグへ掛けない。
    half3 fogColorContribution = MixFog(half3(0, 0, 0), inputData.fogCoord);
    color.rgb = MixFog(color.rgb, inputData.fogCoord)
              + transmission * (refracted - fogColorContribution);

    // --- 不透明度: 水際だけソフトにフェードさせて地形との境界をなじませる。
    //     屈折(背景)はシェーダー内で合成済みなので、それ以外は不透明にする。
    //     泡は境界でも残す。
    float edgeFade = hasSceneDepth ? saturate(waterDepth / max(_EdgeFadeDistance, 0.01)) : 1.0;
    edgeFade = lerp(1.0, edgeFade, depthDetailWeight);
    color.a = max(edgeFade, foamMask);
    return color;
}

#endif // CITY_WATER_INCLUDED
