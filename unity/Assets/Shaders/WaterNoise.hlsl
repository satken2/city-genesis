#ifndef CITY_WATER_NOISE_INCLUDED
#define CITY_WATER_NOISE_INCLUDED

// ================================================================
// 水面用のプロシージャルノイズ(海面 Water.hlsl と船の航跡 ShipWake.shader で共有)
// ================================================================
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

#endif // CITY_WATER_NOISE_INCLUDED
