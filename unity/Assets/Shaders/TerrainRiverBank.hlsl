#ifndef TERRAIN_RIVER_BANK_INCLUDED
#define TERRAIN_RIVER_BANK_INCLUDED

// ================================================================
// 川床・川岸の地表表現(川マスク駆動)
// ================================================================
// RiverSurfaceManager が川(範囲ポリゴン+水面プロファイル線)から CPU で生成する
// 全域マスクテクスチャ _RiverMask(RG8)を、地形シェーダーのプレライティング段で参照する。
//   R = 川床(範囲内=1。範囲外は岸ぎわ1テクセルだけ弱い値で濡れを表現)
//   G = 砂地(カーブ内岸の岸帯。範囲外側に距離で減衰し、曲率が強いほど濃い)
// 川床は崖と同じ岩ブレンドへ合流させ(ApplyCliffRock の extraRockMask)、その上から
// 濡れた暗色と高いスムースネスを乗せる。砂地はベース色を砂色へ寄せる。
// テクスチャ素材は使わず色パラメータだけで成立させる(地表の手続き生成方針と同じ)。
//
// マスクは川群の外接矩形だけを覆う。範囲外の地点は uv が [0,1] を外れるので0扱いにする。
// マスク未生成(_RiverMaskParams.w=0)なら全て0で、既存の見た目に影響しない。
// ================================================================

TEXTURE2D(_RiverMask);
SAMPLER(sampler_RiverMask);
float4 _RiverMaskParams;   // x=原点X(m), y=原点Z(m), z=1/テクセル(1/m), w=1でマスク有効
float4 _RiverMaskSize;     // x=幅テクセル数, y=高さテクセル数
float4 _RiverSandColor;    // 砂地の色
float4 _RiverBedTint;      // rgb=濡れた川床の乗算色, a=川床のスムースネス
float  _RiverSandSmoothness;

/// <summary>川マスクを (bed, sand) としてサンプルする。範囲外・未生成は0。</summary>
void SampleRiverMask(float3 positionWS, out half bed, out half sand)
{
    bed = 0; sand = 0;
    if (_RiverMaskParams.w < 0.5) return;
    // テクセル中心=格子点。+0.5 でテクセル中心に合わせる
    float2 uv = ((positionWS.xz - _RiverMaskParams.xy) * _RiverMaskParams.z + 0.5) / max(_RiverMaskSize.xy, 1.0);
    if (any(uv < 0.0) || any(uv > 1.0)) return;
    // プレライティングのフック内(動的分岐の可能性あり)なので LOD 明示でサンプルする
    half2 m = SAMPLE_TEXTURE2D_LOD(_RiverMask, sampler_RiverMask, uv, 0).rg;
    bed = m.r;
    sand = m.g;
}

/// <summary>
/// 砂地(内岸)をベースアルベドへ乗せ、川床の岩化率を返す。ApplyCliffRock の前に呼び、
/// 戻り値を extraRockMask として渡す。川床の濡れ表現は ApplyRiverBedWet で岩ブレンド後に乗せる。
/// </summary>
half ApplyRiverSand(float3 positionWS, inout half3 albedo, inout half smoothness, out half bed)
{
    half sand;
    SampleRiverMask(positionWS, bed, sand);
    albedo = lerp(albedo, (half3)_RiverSandColor.rgb, sand);
    smoothness = lerp(smoothness, (half)_RiverSandSmoothness, sand);
    return bed;
}

/// <summary>濡れた川床: 岩ブレンド後のアルベドを暗くし、スムースネスを上げる。</summary>
void ApplyRiverBedWet(half bed, inout half3 albedo, inout half smoothness)
{
    albedo *= lerp((half3)1.0, (half3)_RiverBedTint.rgb, bed);
    smoothness = lerp(smoothness, (half)_RiverBedTint.a, bed);
}

#endif // TERRAIN_RIVER_BANK_INCLUDED
