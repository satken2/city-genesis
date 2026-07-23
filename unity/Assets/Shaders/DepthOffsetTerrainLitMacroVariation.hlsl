#ifndef DEPTH_OFFSET_TERRAIN_LIT_MACRO_VARIATION_INCLUDED
#define DEPTH_OFFSET_TERRAIN_LIT_MACRO_VARIATION_INCLUDED

float _TerrainMacroVariationStrength;
float _TerrainMacroVariationScale;
float4 _TerrainMacroVariationTint;

// --- スタイライズ向けカラーグレード(標高・傾斜ベースの色被せ) ---
// テクスチャの写実精細さではなく色設計で「整った非写実」を作るための層。
// ライティング済みの最終色に、標高で低地色↔高地色を補間したティントを乗算し、
// 急斜面には別のアースカラーを寄せる。強度0で完全に無効(=従来の見た目)。
float _TerrainColorGradeStrength;   // 0=無効, 1=最大
float _TerrainColorGradeLowHeight;  // 低地色になる標高(m)
float _TerrainColorGradeHighHeight; // 高地色になる標高(m)
float4 _TerrainLowColor;            // 低地のティント(≈1.0付近で明度維持)
float4 _TerrainHighColor;           // 高地のティント
float4 _TerrainSlopeColor;          // 急斜面のティント(土・岩肌寄り)

// --- 平地の手続き的ディテール法線 ---
// 法線マップテクスチャを使わず、ワールドノイズの勾配で微小な凹凸を法線に与える。
// 完全マットな平地に柔らかいスペキュラ/陰影の起伏を出す。急斜面(崖)では
// 岩の法線と競合しないよう平坦度で弱める。強度0で無効。
float _TerrainDetailNormalStrength; // 0=無効
float _TerrainDetailNormalScale;    // ノイズ周期の逆数(1/m)。C#側で逆数化して渡す

// --- 地面のベースアルベドの手続き生成 ---
// 拾い物テクスチャを単純タイリングすると格子状の繰り返しが目立つため、ベース色自体を
// 多スケールノイズ(fbm)で作り、繰り返しを消す。_ProceduralGroundBlend で
// テクスチャ由来アルベドと手続き色を混ぜられる(1=完全手続き=反復ゼロ)。
// 標高/傾斜の大局的な色被せは ApplyTerrainColorGrade(ポストライト)が担うため、
// ここはベース色+ノイズのモザイクに集中し役割を二重掛けしない。
float _ProceduralGroundBlend;   // 0=テクスチャ, 1=完全手続き
float _ProceduralGroundScale;   // 大局モザイクの周期(m)
float4 _TerrainGroundColorA;    // ベース(草)
float4 _TerrainGroundColorB;    // 乾いた土のパッチ
float4 _TerrainGroundColorC;    // 明色(細かい斑で薄く混ぜる)

// タイル全体への乗算ティント(白=通常色)。未開放セルの暗色表示に使う。
// TerrainManager がタイルごとの materialTemplate に設定する。
float4 _TerrainTint;

// Global variables for contour lines
float _ShowContour;

float TerrainMacroHash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453123);
}

float TerrainMacroNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);

    float a = TerrainMacroHash(i);
    float b = TerrainMacroHash(i + float2(1.0, 0.0));
    float c = TerrainMacroHash(i + float2(0.0, 1.0));
    float d = TerrainMacroHash(i + float2(1.0, 1.0));

    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// 3オクターブの値ノイズ(fbm)。戻り値はおよそ 0..0.875。
// 単一周期のノイズだと単調な斑になりタイリングの単調さを隠しきれないため、
// 周期を倍々にした3層を重ねて自然な多スケールの濃淡を作る。
float TerrainMacroFbm(float2 p)
{
    float sum = 0.0;
    float amp = 0.5;
    float freq = 1.0;
    [unroll]
    for (int i = 0; i < 3; i++)
    {
        sum += TerrainMacroNoise(p * freq) * amp;
        freq *= 2.03; // 整数倍を避けて格子の重なりによるパターンを崩す
        amp *= 0.5;
    }
    return sum; // 0.5 + 0.25 + 0.125 = 0.875 が上限
}

half3 ApplyTerrainMacroVariation(half3 color, float3 positionWS)
{
    float strength = saturate(_TerrainMacroVariationStrength);
    if (strength <= 0.0001)
        return color;

    float scale = max(_TerrainMacroVariationScale, 1.0);
    float2 macroUV = positionWS.xz / scale;

    float fbm = TerrainMacroFbm(macroUV);
    float variation = saturate(fbm / 0.875) * 2.0 - 1.0; // -1..1

    half brightness = (half)(1.0 + variation * strength);
    half tintWeight = (half)(saturate(variation * 0.5 + 0.5) * strength);
    half3 tinted = lerp(color, color * _TerrainMacroVariationTint.rgb, tintWeight);

    return saturate(tinted * brightness);
}

/// <summary>
/// 標高・傾斜に応じたカラーランプを最終色に乗算し、スタイライズ向けの統一感ある色設計を与える。
/// 標高tで低地色↔高地色を補間したティントに、急斜面ではアースカラーを混ぜる。
/// 傾斜は面法線を画面微分(ddx/ddy)から復元して求める(頂点法線に依存しない)。
/// ティント各色は≈1.0付近を前提とした乗算なので、明度を大きく損なわない。
/// </summary>
half3 ApplyTerrainColorGrade(half3 color, float3 positionWS)
{
    float strength = saturate(_TerrainColorGradeStrength);
    if (strength <= 0.0001)
        return color;

    float span = max(_TerrainColorGradeHighHeight - _TerrainColorGradeLowHeight, 1.0);
    float hT = saturate((positionWS.y - _TerrainColorGradeLowHeight) / span);
    half3 tint = (half3)lerp(_TerrainLowColor.rgb, _TerrainHighColor.rgb, hT);

    // 面法線を画面微分から復元して傾斜を求める(0=水平, 1=垂直)。
    float3 geoN = normalize(cross(ddy(positionWS), ddx(positionWS)));
    float slope = saturate((1.0 - abs(geoN.y)) * 1.5);
    tint = lerp(tint, (half3)_TerrainSlopeColor.rgb, (half)slope);

    return lerp(color, color * tint, (half)strength);
}

/// <summary>
/// 平地の法線に手続きノイズ勾配で微小な凹凸を与える(法線マップテクスチャ不使用)。
/// ライティング前のワールド法線を直接傾けるため、直接光の陰影と柔らかいスペキュラに
/// ディテールが乗る。急斜面(崖)では平坦度で弱め、岩の法線ブレンドと競合させない。
/// </summary>
void ApplyGroundDetailNormal(float3 positionWS, float3 geoNormalWS, inout float3 normalWS)
{
    float strength = _TerrainDetailNormalStrength;
    if (strength <= 0.0001)
        return;

    // 平地ほど強く、急斜面ではほぼ無効化する。
    float flat = saturate(geoNormalWS.y);
    flat = flat * flat;
    if (flat <= 0.01)
        return;

    float2 uv = positionWS.xz * max(_TerrainDetailNormalScale, 1e-4);
    float e = 0.75; // ノイズ空間での有限差分幅
    float hL = TerrainMacroFbm(uv - float2(e, 0.0));
    float hR = TerrainMacroFbm(uv + float2(e, 0.0));
    float hD = TerrainMacroFbm(uv - float2(0.0, e));
    float hU = TerrainMacroFbm(uv + float2(0.0, e));
    float2 grad = float2(hR - hL, hU - hD);

    // 勾配の逆方向へ法線を水平に傾ける(高い所へ向く)。強度と平坦度で減衰。
    float3 bump = float3(-grad.x, 0.0, -grad.y) * (strength * flat);
    normalWS = normalize(normalWS + bump);
}

/// <summary>
/// 地面のベースアルベドを手続き生成する(テクスチャの単純タイリング反復を消すため)。
/// 大局モザイク(低周波fbm)で草↔乾いた土を切り替え、細かい斑(高周波fbm)で明色を薄く混ぜて
/// 単調さを崩す。texAlbedo(TerrainLayer由来のサンプル色)とは _ProceduralGroundBlend で混合し、
/// 1で完全に手続き色へ置き換わる(=拾い物テクスチャの反復が完全に消える)。
/// geoNormalWS は将来の傾斜連動用に受けるが、標高/傾斜の色被せは ApplyTerrainColorGrade が
/// 担うためここでは使わない(役割の二重掛け回避)。
/// </summary>
half3 ComputeProceduralGroundAlbedo(float3 positionWS, float3 geoNormalWS, half3 texAlbedo)
{
    float blend = saturate(_ProceduralGroundBlend);
    if (blend <= 0.0001)
        return texAlbedo;

    float scale = max(_ProceduralGroundScale, 1.0);
    float2 p = positionWS.xz / scale;

    // 大局モザイク: 低周波fbmで A(草) ↔ B(乾いた土) を切り替える
    float macro = saturate(TerrainMacroFbm(p) / 0.875);
    // 中〜細スケールの斑: 高周波fbmで細かい濃淡
    float detail = saturate(TerrainMacroFbm(p * 6.3 + float2(11.5, 5.2)) / 0.875);

    half3 ground = lerp((half3)_TerrainGroundColorA.rgb, (half3)_TerrainGroundColorB.rgb,
                        (half)smoothstep(0.45, 0.75, macro));
    // 細かい斑で C(明色) を薄く混ぜ、ベタ塗り感を消す
    ground = lerp(ground, (half3)_TerrainGroundColorC.rgb, (half)(detail * 0.25));

    return lerp(texAlbedo, ground, (half)blend);
}

/// <summary>
/// 地形の標高に基づいて等高線を描画する。
/// </summary>
half3 ApplyContourLines(half3 color, float3 positionWS)
{
    if (_ShowContour <= 0.0)
        return color;

    float height = positionWS.y;
    float minorInterval = 10.0;
    float majorInterval = 50.0;

    // 画面スペースでの高さの微分を用いて、等高線の太さを一定にする
    float dy = fwidth(height);

    // 副等高線（10m間隔）
    float minorFrac = frac(height / minorInterval);
    float minorDist = min(minorFrac, 1.0 - minorFrac) * minorInterval;
    float minorThreshold = max(dy * 1.5, 0.01);
    float minorIntensity = 1.0 - smoothstep(0.0, minorThreshold, minorDist);

    // 主等高線（50m間隔）
    float majorFrac = frac(height / majorInterval);
    float majorDist = min(majorFrac, 1.0 - majorFrac) * majorInterval;
    float majorThreshold = max(dy * 2.5, 0.015);
    float majorIntensity = 1.0 - smoothstep(0.0, majorThreshold, majorDist);

    // 暗い茶色の線
    half3 contourColor = half3(0.15, 0.1, 0.05);

    if (majorIntensity > 0.0)
    {
        color = lerp(color, contourColor, majorIntensity * 0.60);
    }
    else if (minorIntensity > 0.0)
    {
        color = lerp(color, contourColor, minorIntensity * 0.25);
    }

    return color;
}

// ================================================================
// 崖の岩ブレンド(プレライティングフック)
// ================================================================
// TerrainLitPasses.hlsl(Forward側)のフラグメントは
//   SplatmapMix → InitializeInputData → UniversalFragmentPBR(8引数)
// という構造で、ライティング呼び出しは1箇所のみ。先に Lighting.hlsl を
// includeして UniversalFragmentPBR の実体を確定させたうえで、
// 同名マクロでラッパー(CliffTerrainFragmentPBR)に差し替えることで、
// URPソースをコピーせずにライティング直前のアルベド・法線・スムースネスへ
// 介入する(既存の SplatmapFragment マクロトリックと同じ発想)。
// TerrainLitPasses.hlsl 内の再includeはインクルードガードで無効化されるため、
// マクロは UniversalFragmentPBR の「呼び出し箇所」だけを置換する。
//
// 既知の限界(いずれも標準的なディテール法線と同等のトレードオフ):
// - InitializeBakedGIData(SH/GI)はフックより前に旧法線で評価済みのため、
//   アンビエント項には岩法線が乗らない(直接光・スペキュラには乗る)。
// - DepthNormalsパスはジオメトリ法線のままなので、SSAOにも岩法線は出ない。
// - GBufferパス(本プロジェクトはForward+のため未使用)は
//   UniversalFragmentPBR を呼ばないので崖ブレンドは適用されない。
// - _DBUFFER のデカールはフックより前にアルベドへ適用されるため、
//   崖上のデカールは岩ブレンドで薄まる(本プロジェクトは地形デカール未使用)。
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Assets/Shaders/TerrainCliffRock.hlsl"

// TerrainLitPasses.hlsl:492 の呼び出しシグネチャ
// (InputData, albedo, metallic, specular, smoothness, occlusion, emission, alpha)
// に依存する。URP更新時はここが変わっていないか確認すること。
half4 CliffTerrainFragmentPBR(InputData inputData, half3 albedo, half metallic, half3 specular,
    half smoothness, half occlusion, half3 emission, half alpha)
{
    // 崖マスク(傾斜判定)とトリプラナー投影の基準にはジオメトリ法線を使う。
    // inputData.normalWS は _NORMALMAP 有効時に地面の法線マップで擾乱済みのため、
    // そのまま傾斜判定に使うと法線マップの偏りで特定の向きの斜面だけ岩化する。
    // _NORMALMAP 時は InitializeInputData が組んだ tangentToWorld の第3行が
    // 頂点ジオメトリ法線そのもの(IN.normal.xyz)なのでそこから復元する。
#if defined(_NORMALMAP) && !defined(ENABLE_TERRAIN_PERPIXEL_NORMAL)
    float3 cliffGeoNormalWS = normalize(inputData.tangentToWorld[2]);
#else
    float3 cliffGeoNormalWS = inputData.normalWS;
#endif
    // 地面のベースアルベドを手続き生成で置き換える(テクスチャ反復を消す)。
    // 崖ブレンドはこの手続きベース色の上に岩を lerp するので、崖の非退行が保たれる。
    albedo = ComputeProceduralGroundAlbedo(inputData.positionWS, cliffGeoNormalWS, albedo);
    // 平地の手続きディテール法線を先に乗せてから崖ブレンドする。
    // (崖マスクが高い箇所は ApplyCliffRock の lerp でこのディテール法線から岩法線へ移る)
    ApplyGroundDetailNormal(inputData.positionWS, cliffGeoNormalWS, inputData.normalWS);
    ApplyCliffRock(inputData.positionWS, cliffGeoNormalWS, inputData.normalWS, albedo, smoothness);
    return UniversalFragmentPBR(inputData, albedo, metallic, specular, smoothness, occlusion, emission, alpha);
}

#define SplatmapFragment DepthOffsetTerrainLitOriginalSplatmapFragment
#define UniversalFragmentPBR CliffTerrainFragmentPBR
#include "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitPasses.hlsl"
#undef UniversalFragmentPBR
#undef SplatmapFragment

#ifdef TERRAIN_GBUFFER
// GBufferパスでは崖の岩ブレンド(ApplyCliffRock)は適用されない
// (TERRAIN_GBUFFER 分岐は UniversalFragmentPBR を呼ばないため)。
// 本プロジェクトはForward+レンダリングなのでこのパスは使われない。
GBufferFragOutput SplatmapFragment(Varyings IN)
{
    GBufferFragOutput output = DepthOffsetTerrainLitOriginalSplatmapFragment(IN);
    output.gBuffer0.rgb = ApplyTerrainMacroVariation(output.gBuffer0.rgb, IN.positionWS);
    output.gBuffer0.rgb = ApplyTerrainColorGrade(output.gBuffer0.rgb, IN.positionWS);
    output.gBuffer0.rgb = ApplyContourLines(output.gBuffer0.rgb, IN.positionWS);
    output.gBuffer0.rgb *= (half3)_TerrainTint.rgb;
    output.color.rgb = ApplyTerrainMacroVariation(output.color.rgb, IN.positionWS);
    output.color.rgb = ApplyTerrainColorGrade(output.color.rgb, IN.positionWS);
    output.color.rgb = ApplyContourLines(output.color.rgb, IN.positionWS);
    output.color.rgb *= (half3)_TerrainTint.rgb;
    return output;
}
#else
void SplatmapFragment(
    Varyings IN
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
    )
{
    DepthOffsetTerrainLitOriginalSplatmapFragment(
        IN
        , outColor
#ifdef _WRITE_RENDERING_LAYERS
        , outRenderingLayers
#endif
    );

    outColor.rgb = ApplyTerrainMacroVariation(outColor.rgb, IN.positionWS);
    outColor.rgb = ApplyTerrainColorGrade(outColor.rgb, IN.positionWS);
    outColor.rgb = ApplyContourLines(outColor.rgb, IN.positionWS);
    outColor.rgb *= (half3)_TerrainTint.rgb;
}
#endif

#endif
