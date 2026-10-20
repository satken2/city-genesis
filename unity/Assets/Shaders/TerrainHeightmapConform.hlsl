// 地形高さマップによるGPU地形追従の共通ヘッダー。
// DepthOffsetSimpleLit.shader 等の頂点シェーダー前にインクルードして使用する。
//
// 原理:
//   HeightmapTextureManager が生成した _TerrainHeightmap (RFloat または RHalf) から
//   ワールドXZ座標に対応する地形高さをサンプリングし、頂点のY座標に加算する。
//   これによりCPU側で Terrain.SampleHeight を呼び出さずに
//   建物や道路が地形に追従するようになる。
//
// 使い方:
//   1. シェーダーのプロパティに _TerrainHeightmap, _TerrainHeightmapSize,
//      _MapOriginXZ, _TerrainHeightScale, _TerrainHeightOffset, _GpuTerrainConform を追加。
//   2. 頂点シェーダーの出力位置に対して ApplyGpuTerrainConform を呼ぶ。
//   3. HeightmapTextureManager が Shader.SetGlobalXxx でこれらのプロパティを更新する。

#ifndef TERRAIN_HEIGHTMAP_CONFORM_INCLUDED
#define TERRAIN_HEIGHTMAP_CONFORM_INCLUDED

// HeightmapTextureManager から Shader.SetGlobalXxx で設定されるグローバルプロパティ
TEXTURE2D(_TerrainHeightmap);
SAMPLER(sampler_TerrainHeightmap);

// 地形高さマップのパラメータ (Shader.SetGlobalVector 経由で設定)
// x: マップのワールドサイズ幅, y: マップのワールドサイズ奥行き
// z: テクスチャ解像度幅, w: テクスチャ解像度奥行き
float4 _TerrainHeightmapSize;

// 地形高さマップの原点 (Shader.SetGlobalVector 経由で設定)
// x: 原点のワールドX座標, y: 原点のワールドZ座標
float4 _MapOriginXZ;

// 高さマップのエンコードパラメータ
float _TerrainHeightScale;
float _TerrainHeightOffset;

// GPU地形追従が有効かどうか (1.0 = 有効, 0.0 = 無効)
float _GpuTerrainConform;

/// <summary>
/// ワールド座標から地形高さマップをサンプリングしてterrainHeightを返す。
/// _GpuTerrainConform が 0 の場合は 0 を返す（CPU追従にフォールバック）。
/// </summary>
float SampleTerrainHeight(float3 worldPos)
{
    if (_GpuTerrainConform < 0.5)
        return 0.0;

    // ワールドXZ → テクスチャUV。
    // テクセル(i,j)の値は「ワールド格子点 原点+(i,j)×解像度」の標高なので、
    // 格子点がテクセル中心に一致するよう半テクセルずらす。
    // これによりGPUのバイリニア補間が HeightmapTextureManager.SampleHeight の
    // CPUバイリニア補間と一致する。
    float2 uv;
    uv.x = (worldPos.x - _MapOriginXZ.x) / _TerrainHeightmapSize.x + 0.5 / _TerrainHeightmapSize.z;
    uv.y = (worldPos.z - _MapOriginXZ.y) / _TerrainHeightmapSize.y + 0.5 / _TerrainHeightmapSize.w;

    // テクスチャ座標をクランプして境界外アクセスを防ぐ
    uv = saturate(uv);

    // 高さマップをサンプリング
    float encodedHeight = SAMPLE_TEXTURE2D_LOD(_TerrainHeightmap, sampler_TerrainHeightmap, uv, 0).r;

    // デコード: worldHeight = encoded * scale + offset
    return encodedHeight * _TerrainHeightScale + _TerrainHeightOffset;
}

/// <summary>
/// 頂点のワールド座標に地形高さを適用する。
/// オブジェクトのY座標が相対高さ（relY）として設定されている場合、
/// vertex出力の positionWS.y に地形高さを加算するだけでよい。
/// この関数は頂点シェーダーの出力に対して呼ぶ。
/// </summary>
float3 ApplyGpuTerrainConform(float3 positionWS)
{
    float terrainHeight = SampleTerrainHeight(positionWS);
    positionWS.y += terrainHeight;
    return positionWS;
}

#endif // TERRAIN_HEIGHTMAP_CONFORM_INCLUDED