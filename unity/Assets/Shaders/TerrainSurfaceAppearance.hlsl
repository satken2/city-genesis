#ifndef TERRAIN_SURFACE_APPEARANCE_INCLUDED
#define TERRAIN_SURFACE_APPEARANCE_INCLUDED
#include "Assets/Shaders/ForestCoverage.hlsl"

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

// 地形全体への乗算ティント(通常は白=素の地形色)。
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
    float strength = saturate(_TerrainMacroVariationStrength) * (1 - ForestSurfaceWeight(positionWS));
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
    float strength = saturate(_TerrainColorGradeStrength) * (1 - ForestSurfaceWeight(positionWS));
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

// ピクセル幅の芯とアンチエイリアスを持つ線。水平面の全面着色と密集時の塗り潰しを防ぐ。
float ContourCoverage(float height, float dy, float interval, float halfWidth)
{
    float phase = frac(height / interval + 0.5) - 0.5;
    float pixels = abs(phase) * interval / max(dy, 0.00001);
    float coverage = 1.0 - smoothstep(halfWidth - 0.5, halfWidth + 0.5, pixels);
    return coverage * smoothstep(0.00001, 0.0001, dy)
        * (1.0 - smoothstep(0.12, 0.25, dy / interval));
}

// 標高は固定したまま近距離の補助線をフェードさせ、ズームで線が地面を滑らないようにする。
half3 ApplyContourLines(half3 color, float3 positionWS)
{
    if (_ShowContour <= 0.0)
        return color;

    half3 groundColor = color;
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

    float closeWeight = 1.0 - smoothstep(150.0, 600.0, distance(_WorldSpaceCameraPos, positionWS));
    float detail = ContourCoverage(height, dy, 2.0, 0.8);
    float index = ContourCoverage(height, dy, 10.0, 1.25);
    float major = ContourCoverage(height, dy, 50.0, 1.6);
    float ink = max(detail * 0.78, max(index * 0.92, major * 0.96));
    float halo = max(ContourCoverage(height, dy, 2.0, 1.8),
        max(ContourCoverage(height, dy, 10.0, 2.25), ContourCoverage(height, dy, 50.0, 2.6)));

    // 明るい縁と濃い青灰色の芯で、草地・岩肌・日陰でも線を見分けられるようにする。
    half3 closeColor = lerp(groundColor, half3(0.8, 0.86, 0.9), halo * 0.32);
    closeColor = lerp(closeColor, half3(0.018, 0.028, 0.045), ink);
    return lerp(color, closeColor, closeWeight);
}

// Terrainと遠景メッシュが照明直前に共用する地表処理。
// GIは呼出し前の法線で計算済み。ディテール法線は直接光へ反映され、
// DepthNormalsと未使用のGBufferには反映されない。
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Assets/Shaders/TerrainCliffRock.hlsl"
#include "Assets/Shaders/TerrainRiverBank.hlsl"

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
    // 川岸の砂地はベース色へ、川床は岩ブレンドへ合流させ、岩化後に濡れの暗色を乗せる
    // (TerrainRiverBank.hlsl。川マスク未生成なら何も変えない)
    half riverBed;
    half riverRock = ApplyRiverSand(inputData.positionWS, albedo, smoothness, riverBed);
    ApplyCliffRock(inputData.positionWS, cliffGeoNormalWS, inputData.normalWS, albedo, smoothness, riverRock);
    ApplyRiverBedWet(riverBed, albedo, smoothness);
    float forest = ForestSurfaceWeight(inputData.positionWS) * (1 - riverBed);
    if (forest > .001)
    {
        float2 coverage = ForestCoverageAt(inputData.positionWS);
        half3 leaf = lerp(_ForestPineColor.rgb, _ForestLeafColor.rgb, coverage.g / max(.001, coverage.r + coverage.g));
        float2 uv = inputData.positionWS.xz / 5;
        float2 cell = floor(uv), offset = 0;
        float closest = 10;
        // 樹冠の丸みを法線に与え、画面上で小さくなった凹凸だけを平均化する。
        [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 key = cell + float2(x,y);
            float2 delta = uv - key - float2(TerrainMacroHash(key), TerrainMacroHash(key + 37));
            float d = dot(delta,delta);
            if (d < closest) { closest = d; offset = delta; }
        }
        float detail = 1 - smoothstep(.35,1.5,max(length(ddx(uv)),length(ddy(uv))));
        float crown = saturate(1 - closest);
        float macro = TerrainMacroNoise(inputData.positionWS.xz / 55);
        leaf *= lerp(.78, lerp(.62,1,crown), detail) * lerp(.93,1.07,macro);
        float3 crownNormal = normalize(float3(offset.x * 1.5 * detail,1,offset.y * 1.5 * detail));
        inputData.normalWS = normalize(lerp(inputData.normalWS,crownNormal,forest));
        inputData.bakedGI = lerp(inputData.bakedGI,SampleSH(inputData.normalWS),forest);
        albedo = lerp(albedo,leaf,forest);
        smoothness = lerp(smoothness,.1,forest);
    }
    return UniversalFragmentPBR(inputData, albedo, metallic, specular, smoothness, occlusion, emission, alpha);
}


#endif
