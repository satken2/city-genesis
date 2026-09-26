#ifndef ULTRA_FAR_TERRAIN_SINK_INCLUDED
#define ULTRA_FAR_TERRAIN_SINK_INCLUDED

TEXTURE2D(_LoadedCellMask);
SAMPLER(sampler_LoadedCellMask);
float4 _LoadedCellMaskParams;
float4 _LoadedCellMaskTexel;
float _FarSinkAmount;
float _FarGridStep;
float4 _FarCityRect; // xy=都市の南西隅, zw=北東隅(ワールドXZ)。z<=x なら都市範囲の扱いを無効にする

float FarLoadedAt(float2 position)
{
    // 遠景メッシュは都市の範囲にしか面を持たないので、境界の外側の近傍は境界上のセルとして扱う。
    // マスク外を未ロードとみなすと、都市境界から1格子の帯が沈まずに詳細地形の上へ突き出る。
    if (_FarCityRect.z > _FarCityRect.x)
        position = clamp(position, _FarCityRect.xy + 0.01, _FarCityRect.zw - 0.01);
    float2 cell = floor(position * _LoadedCellMaskParams.x) + _LoadedCellMaskParams.yz;
    float2 uv = (cell + 0.5) * _LoadedCellMaskTexel.xy;
    if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
    return step(0.5, SAMPLE_TEXTURE2D_LOD(_LoadedCellMask, sampler_LoadedCellMask, uv, 0).r);
}

void ApplyFarSink(inout float3 positionOS)
{
    // 接続面の全頂点はXZ各軸で最大1グリッド先にある。セルをまたぐ面は
    // 沈めずに残し、下向きの補間が未ロード領域へ漏れないようにする。
    float radius = max(_FarGridStep, 1.0) + 0.01;
    // 9点で範囲内の全セルを調べられない粗さでは、安全側として沈めない。
    if (_LoadedCellMaskParams.x <= 0.0 || radius * _LoadedCellMaskParams.x > 1.0) return;
    UNITY_BRANCH
    if (FarLoadedAt(positionOS.xz) < 0.5) return;
    float loaded = 1.0;
    [unroll] for (int z = -1; z <= 1; z++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
            if (x != 0 || z != 0)
                loaded *= FarLoadedAt(positionOS.xz + float2(x, z) * radius);
    }
    positionOS.y -= loaded * _FarSinkAmount;
}

#endif
