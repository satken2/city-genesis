#ifndef CITY_WATER_INCLUDED
#define CITY_WATER_INCLUDED

// ================================================================
// 半透明の水面シェーディング(Custom/WaterLit の本体)
// ================================================================
// 海面・湖面(Y=一定の水平メッシュ)をリアルに見せるための、
// 「フル装備」の水シェーディングを1つの ForwardLit パスで行う。
//
//  1. さざ波:      プロシージャルノイズ(fbm)の高さ場から法線を生成し、
//                   時間でスクロールさせる。テクスチャ不要・タイリング継ぎ目なし。
//  2. 深度による色: シーン深度(_CameraDepthTexture)と水面の深度差(=水柱の厚み)から、
//                   浅瀬色↔深海色を補間する。
//  3. 岸のフォーム: 水柱が薄い(岸ぎわ・浅瀬)ピクセルに、アニメする泡ノイズを乗せる。
//  4. 屈折:         _CameraOpaqueTexture を波の法線でずらしてサンプリングし、
//                   水中(水底)を歪ませて透かす。前景を吸い込まないよう深度でガードする。
//  5. 反射/太陽:    アルベド=水の体色・低メタリック・高スムースネスで URP の
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
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

// --- マテリアルプロパティ(SRP Batcher 互換のため UnityPerMaterial にまとめる) ---
CBUFFER_START(UnityPerMaterial)
    float4 _DeepColor;          // 深海(水柱が厚い)部分の色
    float4 _ShallowColor;       // 浅瀬(水柱が薄い)部分の色
    float4 _FoamColor;          // 岸の泡の色
    float  _DepthColorDistance; // 深海色に達する水深(m)
    float  _MurkDistance;       // 屈折(水底の透け)が濁って見えなくなる水深(m)
    float  _FoamDistance;       // 岸の泡が発生する水深(m)。これより浅いと泡が出る
    float  _FoamNoiseScale;     // 泡ノイズの空間周期(1/m)
    float  _FoamCutoff;         // 泡マスクの閾値(小さいほど泡が広がる)
    float  _RefractionStrength; // 屈折によるスクリーンUVのずらし量(画面比)
    float  _EdgeFadeDistance;   // 水際のソフトフェード距離(m)。0で不透明に立ち上がる
    float  _Smoothness;         // 水面のスムースネス(高いほど鋭い反射・太陽グレア)
    float  _Metallic;           // 水は誘電体なので基本0付近
    float  _RippleScale;        // さざ波ノイズの空間周期(1/m)
    float  _RippleSpeed;        // さざ波のスクロール速度
    float  _RippleStrength;     // 法線の傾き強度(0で完全に平ら)
    float  _RippleDetail;       // 2枚目(細波)の相対スケール
    float  _FlowEnabled;        // 1 で頂点UV2の流下方向へさざ波・泡を流す(川)。海面・湖面は0
    float  _FlowSpeed;          // 流れの速度(m/s)
CBUFFER_END

// ---------------- プロシージャルノイズ ----------------
// テクスチャを使わずに水面のさざ波・泡を作るための、ハッシュベースの
// 値ノイズと fbm。ワールドXZ座標を入力にするため、メッシュのタイリングや
// 頂点密度に依存せず継ぎ目が出ない。

float2 WaterHash2(float2 p)
{
    p = float2(dot(p, float2(127.1, 311.7)), dot(p, float2(269.5, 183.3)));
    return frac(sin(p) * 43758.5453123) * 2.0 - 1.0;
}

// 勾配(Perlin風)ノイズ。-1..1 程度。
float WaterGradNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f); // smoothstep 補間
    float a = dot(WaterHash2(i + float2(0, 0)), f - float2(0, 0));
    float b = dot(WaterHash2(i + float2(1, 0)), f - float2(1, 0));
    float c = dot(WaterHash2(i + float2(0, 1)), f - float2(0, 1));
    float d = dot(WaterHash2(i + float2(1, 1)), f - float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// 3オクターブの fbm。0..1 程度に正規化して返す。
float WaterFbm(float2 p)
{
    float v = 0.0;
    float amp = 0.5;
    for (int i = 0; i < 3; i++)
    {
        v += amp * WaterGradNoise(p);
        p *= 2.0;
        amp *= 0.5;
    }
    return v * 0.5 + 0.5;
}

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
    float  fogFactor  : TEXCOORD2;
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
    OUT.fogFactor  = ComputeFogFactor(vpi.positionCS.z);
    OUT.flowDir    = IN.uv1 * _FlowEnabled;
    return OUT;
}

half4 WaterFragment(Varyings IN) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(IN);

    float3 positionWS = IN.positionWS;
    float3 viewDirWS  = normalize(GetCameraPositionWS() - positionWS);
    // 川の流れ: ノイズの参照座標を流下方向へ時間比例でずらすと、さざ波・泡が下流へ動いて見える
    float2 flowOffset = IN.flowDir * (_Time.y * _FlowSpeed);
    float2 noiseXZ    = positionWS.xz - flowOffset;
    float3 normalWS   = WaterNormalWS(noiseXZ, _Time.y);

    // --- 水面ピクセルと、その背後にある不透明物(水底・地形)の深度差 ---
    float2 screenUV        = IN.screenPos.xy / IN.screenPos.w;
    float  surfaceEyeDepth = IN.screenPos.w; // 水面フラグメントのビュー空間深度
    float  sceneRaw        = SampleSceneDepth(screenUV);
    float  sceneEyeDepth   = LinearEyeDepth(sceneRaw, _ZBufferParams);
    float  depthColumn     = max(0.0, sceneEyeDepth - surfaceEyeDepth); // 水柱の厚み(m)

    // --- 屈折: 波の法線でスクリーンUVをずらして水中を透かす ---
    // 手前(水面より近い)物体を吸い込まないよう、ずらした先の深度が水面より
    // 奥にある場合のみ屈折UVを採用する(定番のガード)。
    float2 refractUV      = screenUV + normalWS.xz * _RefractionStrength;
    float  refractRaw     = SampleSceneDepth(refractUV);
    float  refractEyeDepth = LinearEyeDepth(refractRaw, _ZBufferParams);
    if (refractEyeDepth < surfaceEyeDepth)
    {
        refractUV = screenUV;
        refractEyeDepth = sceneEyeDepth;
    }
    float  refractColumn = max(0.0, refractEyeDepth - surfaceEyeDepth);
    float3 refracted     = SampleSceneColor(refractUV).rgb;

    // --- 体色: 浅瀬は水底(屈折)が透け、深いほど水自身の色で濁る ---
    float3 waterTint = lerp(_ShallowColor.rgb, _DeepColor.rgb,
                            saturate(depthColumn / max(_DepthColorDistance, 0.01)));
    float  murk      = saturate(refractColumn / max(_MurkDistance, 0.01));
    float3 bodyColor = lerp(refracted * _ShallowColor.rgb, waterTint, murk);

    // --- 岸のフォーム: 水柱が薄いピクセルにアニメする泡ノイズを乗せる ---
    float  foamBand  = 1.0 - saturate(depthColumn / max(_FoamDistance, 0.01));
    float  foamNoise = WaterFbm(noiseXZ * _FoamNoiseScale + _Time.y * 0.25);
    float  foamMask  = smoothstep(_FoamCutoff, 1.0, foamBand * foamNoise);
    bodyColor        = lerp(bodyColor, _FoamColor.rgb, foamMask);

    // --- URP の物理ベースライティングで反射(フレネル)・太陽グレアを付与 ---
    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo     = bodyColor;
    surfaceData.metallic   = _Metallic;
    surfaceData.smoothness = lerp(_Smoothness, 0.1, foamMask); // 泡はざらつく
    surfaceData.occlusion  = 1.0;
    surfaceData.alpha      = 1.0;
    surfaceData.normalTS   = float3(0.0, 0.0, 1.0);

    InputData inputData = (InputData)0;
    inputData.positionWS              = positionWS;
    inputData.normalWS                = normalWS;
    inputData.viewDirectionWS         = viewDirWS;
    inputData.shadowCoord             = TransformWorldToShadowCoord(positionWS);
    inputData.fogCoord                = IN.fogFactor;
    inputData.bakedGI                 = SampleSH(normalWS);
    inputData.normalizedScreenSpaceUV = screenUV;
    inputData.shadowMask              = half4(1, 1, 1, 1);

    half4 color = UniversalFragmentPBR(inputData, surfaceData);
    color.rgb = MixFog(color.rgb, IN.fogFactor);

    // --- 不透明度: 水際だけソフトにフェードさせて地形との境界をなじませる。
    //     体色には既に屈折(背景)を合成済みなので、それ以外は不透明にする。
    //     泡は境界でも残す。
    float edgeFade = saturate(depthColumn / max(_EdgeFadeDistance, 0.01));
    color.a = max(edgeFade, foamMask);
    return color;
}

#endif // CITY_WATER_INCLUDED
