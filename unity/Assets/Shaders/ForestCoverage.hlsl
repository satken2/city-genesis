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

float ForestSurfaceWeight(float3 position)
{
    float2 coverage = ForestCoverageAt(position);
    // 下地が薄いまま木を間引くと、両方の被覆が不足して明るい帯になる。
    // 距離・常駐境界ともに、樹冠の下地を完成させてからモデルを減らす。
    float distanceWeight = smoothstep(800,1100,distance(position,_WorldSpaceCameraPos));
    float boundaryWeight = 1 - smoothstep(.7,1,ForestDetailAt(position.xz));
    return (1 - (1-coverage.r)*(1-coverage.g)) * max(distanceWeight,boundaryWeight);
}
#endif
