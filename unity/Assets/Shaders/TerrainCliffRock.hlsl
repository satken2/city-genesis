#ifndef TERRAIN_CLIFF_ROCK_INCLUDED
#define TERRAIN_CLIFF_ROCK_INCLUDED

// ================================================================
// 崖(急傾斜)への岩テクスチャブレンド
// ================================================================
// 地形の傾斜が閾値を超えた部分に、岩肌のアルベド+法線マップを
// トリプラナーマッピング(ワールド座標の3軸投影)でブレンドする。
//
// - ライティング「前」のアルベド・法線・スムースネスに介入するため、
//   崖にも通常のPBRライティング(太陽光の陰影・スペキュラ)が乗る。
//   (旧実装はライティング済みの最終色へ単色をlerpしていたため、
//    急傾斜ほど陰影が消えてベタ塗りになっていた)
// - トリプラナーなのでUnity Terrain標準のXZ平面投影UVと違い、
//   垂直に近い崖面でもテクスチャが引き伸ばされない。
// - 崖テクスチャが未設定(_CliffTexturesLoaded=0)の場合は単色
//   フォールバックにブレンドする。これもプレライティングなので
//   陰影は保たれる。
//
// このファイルは TerrainMacroNoise を使用するため、
// TerrainSurfaceAppearance.hlsl のノイズ関数定義より
// 後で include すること。プロパティ値は TerrainManager.cs の
// ApplyTerrainMaterialSettings がTerrain・遠景の各マテリアルに設定する。
// ================================================================

TEXTURE2D(_CliffAlbedoMap);
SAMPLER(sampler_CliffAlbedoMap);
// 法線マップはアルベドとサンプラーを共用し、サンプラースロットを節約する
// (Terrain標準の _Splat/_Normal 群で既に多数のサンプラーを消費しているため)
TEXTURE2D(_CliffNormalMap);

float _CliffUvScale;          // 1 / タイリング周期(m)。C#側で逆数化して渡される
float _CliffSlopeThreshold;   // 岩ブレンド開始の傾斜(0=水平, 1=垂直)
float _CliffSlopeBlend;       // 草→岩の遷移幅(傾斜値)
float _CliffNoiseScale;       // 閾値を乱すワールドノイズの周期(m)
float _CliffNoiseStrength;    // 閾値ノイズの振幅(傾斜値)
float _CliffNormalStrength;   // 岩法線マップの強度(0=無効)
float _CliffSmoothness;       // 岩部分のスムースネス(乾いた岩なので低め)
float _CliffTexturesLoaded;   // 1=テクスチャ設定済み。0なら単色フォールバック
float4 _CliffFallbackColor;   // テクスチャ未設定時の岩色
float _CliffLowlandStart;     // 低地岩化の開始標高(m)。これ以上は傾斜のみで判定
float _CliffLowlandFull;      // 低地岩化が最大になる標高(m)。これ以下(海面下含む)は一律で岩
float4 _CliffRockTint;        // 岩の色に掛ける色(地表種別ごと。白=岩テクスチャのまま)
float4 _ShoreSandColor;       // rgb=波打ち際の砂の色, a=低地の岩を砂へ置き換える割合(0=岩のまま)

/// <summary>
/// 岩ブレンド率を「傾斜による分」と「低地による分」に分けて求める(0=下地のまま, 1=完全に岩)。
/// 閾値にワールドノイズを加算することで、等高線状の均一な帯にならず
/// 下地と岩の境界が自然に入り乱れる。
/// </summary>
void ComputeCliffMasks(float3 normalWS, float3 positionWS, out half slopeMask, out half lowlandMask)
{
    // --- 傾斜ベース: 急斜面ほど岩 ---
    float slope = 1.0 - saturate(normalWS.y); // 0=水平, 1=垂直
    float jitter = (TerrainMacroNoise(positionWS.xz / max(_CliffNoiseScale, 1.0)) - 0.5) * _CliffNoiseStrength;
    float threshold = _CliffSlopeThreshold + jitter;
    slopeMask = (half)saturate((slope - threshold) / max(_CliffSlopeBlend, 1e-3));

    // --- 標高ベース: 低地(海面下・浅瀬)は傾斜に関係なく岩 ---
    // positionWS.y は標高(m)。海面がワールドY=0。_CliffLowlandStart(例:2m)から
    // _CliffLowlandFull(例:0m)へ徐々に岩へ寄せ、それ以下(海面下)は一律で岩。
    // 汀線が真横一直線の帯にならないよう傾斜と同じノイズで開始標高を揺らす。
    float heightNoise = (TerrainMacroNoise(positionWS.xz / max(_CliffNoiseScale, 1.0) + 31.7) - 0.5) * 2.0;
    float lowlandStart = _CliffLowlandStart + heightNoise; // ノイズ振幅は±1m相当
    float lowlandSpan = max(lowlandStart - _CliffLowlandFull, 1e-3);
    lowlandMask = (half)saturate((lowlandStart - positionWS.y) / lowlandSpan);
}

/// <summary>傾斜・標高いずれかの条件で岩になる率。</summary>
half ComputeCliffMask(float3 normalWS, float3 positionWS)
{
    half slopeMask, lowlandMask;
    ComputeCliffMasks(normalWS, positionWS, slopeMask, lowlandMask);
    return max(slopeMask, lowlandMask);
}

/// <summary>
/// アルベド・ワールド法線・スムースネスを傾斜に応じて岩へブレンドする。
/// TerrainLitPasses.hlsl の UniversalFragmentPBR 呼び出し直前
/// (=InitializeInputData 後)にフックされるため、_NORMALMAP の有無などの
/// 法線解決分岐を気にせず normalWS を直接書き換えられる。
///
/// geoNormalWS には「地面の法線マップで擾乱される前のジオメトリ法線」を渡すこと。
/// 崖マスク(傾斜判定)とトリプラナー投影の基準を擾乱後の normalWS にすると、
/// 地面側の法線マップの偏りがそのまま傾斜判定に乗り、同じ傾斜でも特定の向きの
/// 斜面だけ岩化する(山の片側だけ岩になる)方向依存バグの原因になるため。
/// normalWS(シェーディング法線)は岩の法線をブレンドする出力先としてのみ使う。
/// extraRockMask は傾斜・標高以外の理由で岩にしたい率(川床マスク。TerrainRiverBank.hlsl)。
/// 低地の岩は _ShoreSandColor.a の割合だけ砂へ置き換える(砂漠・乾燥地の砂浜と、浅い海の砂の海底)。
/// 急斜面と川床は置き換えず岩のまま残す。
/// </summary>
void ApplyCliffRock(float3 positionWS, float3 geoNormalWS, inout float3 normalWS, inout half3 albedo, inout half smoothness, half extraRockMask)
{
    half slopeMask, lowlandMask;
    ComputeCliffMasks(geoNormalWS, positionWS, slopeMask, lowlandMask);
    half shoreSand = (half)saturate(_ShoreSandColor.a);
    half mask = max(max(slopeMask, lowlandMask * (1 - shoreSand)), extraRockMask);
    albedo = lerp(albedo, (half3)_ShoreSandColor.rgb, lowlandMask * shoreSand * (1 - mask));

    // 平地ピクセルはテクスチャタップ0で早期脱出する。
    // 動的分岐内の暗黙微分(ミップ選択)は理論上未定義だが、分岐境界では
    // mask≈0 でブレンド寄与もゼロに収束するため、仮にミップが乱れても視認できない。
    UNITY_BRANCH
    if (mask <= 0.001)
        return;

    if (_CliffTexturesLoaded < 0.5)
    {
        // テクスチャ未設定でも破綻しないフォールバック。
        // プレライティングの単色ブレンドなので、旧実装と違い陰影は残る。
        albedo = lerp(albedo, (half3)(_CliffFallbackColor.rgb * _CliffRockTint.rgb), mask);
        smoothness = lerp(smoothness, (half)_CliffSmoothness, mask);
        return;
    }

    // --- トリプラナー重み(pow4でシャープ化し、投影境界のぼやけを抑える) ---
    half3 w = pow(abs((half3)geoNormalWS), 4.0);
    w /= (w.x + w.y + w.z);

    float2 uvX = positionWS.zy * _CliffUvScale; // X軸投影(東西向きの崖面)
    float2 uvY = positionWS.xz * _CliffUvScale; // Y軸投影(上面)
    float2 uvZ = positionWS.xy * _CliffUvScale; // Z軸投影(南北向きの崖面)

    // アルベド 3タップ
    half3 rock = (SAMPLE_TEXTURE2D(_CliffAlbedoMap, sampler_CliffAlbedoMap, uvX).rgb * w.x
               + SAMPLE_TEXTURE2D(_CliffAlbedoMap, sampler_CliffAlbedoMap, uvY).rgb * w.y
               + SAMPLE_TEXTURE2D(_CliffAlbedoMap, sampler_CliffAlbedoMap, uvZ).rgb * w.z)
               * (half3)_CliffRockTint.rgb;

    // 法線 3タップ + Whiteoutブレンドでワールド空間へ再投影
    // (Ben Golus "Normal Mapping for a Triplanar Shader" の方式。
    //  各軸のタンジェント法線XYにジオメトリ法線の対応成分を加算し、
    //  Z成分はabsを取ってジオメトリ法線の軸成分を乗算することで、
    //  負方向を向いた崖面でも正しい向きの法線になる)
    half3 geoN = (half3)geoNormalWS;
    half3 tnX = UnpackNormalScale(SAMPLE_TEXTURE2D(_CliffNormalMap, sampler_CliffAlbedoMap, uvX), _CliffNormalStrength);
    half3 tnY = UnpackNormalScale(SAMPLE_TEXTURE2D(_CliffNormalMap, sampler_CliffAlbedoMap, uvY), _CliffNormalStrength);
    half3 tnZ = UnpackNormalScale(SAMPLE_TEXTURE2D(_CliffNormalMap, sampler_CliffAlbedoMap, uvZ), _CliffNormalStrength);
    tnX = half3(tnX.xy + geoN.zy, abs(tnX.z) * geoN.x);
    tnY = half3(tnY.xy + geoN.xz, abs(tnY.z) * geoN.y);
    tnZ = half3(tnZ.xy + geoN.xy, abs(tnZ.z) * geoN.z);
    half3 rockNormalWS = normalize(tnX.zyx * w.x + tnY.xzy * w.y + tnZ.xyz * w.z);

    albedo = lerp(albedo, rock, mask);
    normalWS = normalize(lerp(normalWS, (float3)rockNormalWS, mask));
    smoothness = lerp(smoothness, (half)_CliffSmoothness, mask);
}

#endif // TERRAIN_CLIFF_ROCK_INCLUDED
