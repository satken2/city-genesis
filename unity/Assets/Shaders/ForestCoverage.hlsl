#ifndef FOREST_COVERAGE_INCLUDED
#define FOREST_COVERAGE_INCLUDED
TEXTURE2D(_ForestCoverage); SAMPLER(sampler_ForestCoverage);
TEXTURE2D(_ForestDetail); SAMPLER(sampler_ForestDetail);
float4 _ForestCoverageRect, _ForestDetailRect;
float4 _ForestPineColor, _ForestLeafColor;
float _ForestOverviewEnabled;

float ForestDetailAt(float2 position)
{
    float2 uv = (position - _ForestDetailRect.xy) * _ForestDetailRect.zw;
    if (any(uv < 0) || any(uv > 1)) return 0;
    return SAMPLE_TEXTURE2D_LOD(_ForestDetail, sampler_ForestDetail, uv, 0).r;
}

// 生成済み小区画の縁からも退かせ、距離上限より近い常駐セル境界を隠す。
float ForestModelWeight(float3 position, float3 camera)
{
    if (_ForestOverviewEnabled < .5) return 1;
    float support = smoothstep(0,.65,ForestDetailAt(position.xz));
    return support * (1 - smoothstep(1200,1500,distance(position,camera)));
}

float2 ForestCoverageAt(float3 position)
{
    if (_ForestOverviewEnabled < .5) return 0;
    float2 uv = (position.xz - _ForestCoverageRect.xy) * _ForestCoverageRect.zw;
    if (any(uv < 0) || any(uv > 1)) return 0;
    return SAMPLE_TEXTURE2D_LOD(_ForestCoverage, sampler_ForestCoverage, uv, 0).rg;
}

float ForestOverviewBlend(float3 position)
{
    // 下地が薄いまま木を間引くと、両方の被覆が不足して明るい帯になる。
    // 距離・常駐境界ともに、樹冠の下地を完成させてからモデルを減らす。
    float distanceWeight = smoothstep(800, 1100, distance(position, _WorldSpaceCameraPos));
    float boundaryWeight = 1 - smoothstep(.7, 1, ForestDetailAt(position.xz));
    return max(distanceWeight, boundaryWeight);
}

float ForestSurfaceWeight(float3 position, float3 normalWS)
{
    float2 coverage = ForestCoverageAt(position);
    // 視線が樹冠の層を通る長さを地形法線に対する角度から求め、実際に葉で見える割合だけを返す。
    // 水平に近い浅い角度や疎林で地面全体が緑に塗り潰れないよう、倍率に上限を設ける。
    float open = (1 - coverage.r) * (1 - coverage.g);
    float viewCos = max(abs(dot(normalWS, GetWorldSpaceNormalizeViewDir(position))), .35);
    return (1 - pow(open, 1 / viewCos)) * ForestOverviewBlend(position);
}

float ForestSurfaceWeight(float3 position)
{
    return ForestSurfaceWeight(position, float3(0, 1, 0));
}

// 樹冠の隙間から見えている地面に落ちる木の影(明度倍率)。
// 影は葉の色に置き換えず、元の地表色(砂・雪・草など)のまま暗くする。
float ForestGroundShadow(float2 coverage, float3 position, float3 normalWS)
{
    float open = (1 - coverage.r) * (1 - coverage.g);
    float lightCos = max(dot(normalWS, _MainLightPosition.xyz), .25);
    float sunOpen = pow(open, 1 / lightCos);
    return lerp(1, lerp(.55, 1, sunOpen), ForestOverviewBlend(position));
}
#endif
