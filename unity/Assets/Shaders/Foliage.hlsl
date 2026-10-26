#ifndef CITY_GENESIS_FOLIAGE_INCLUDED
#define CITY_GENESIS_FOLIAGE_INCLUDED

// 樹木・植え込みの葉の陰影。Forest(範囲散布の森)と Foliage(それ以外の木)が共有する。
// モデルは樹冠全体へ寄せた法線と、樹冠の奥・下ほど暗い明るさの倍率(頂点色R)を持つ(genesis-gallery の foliage.py が焼く)。
// 葉群の凹凸・光の回り込み・逆光の透け・1本ごとの色むらは、頂点を増やさず画素の計算で足す。
// 凹凸と色むらの倍率は平均が1になるようにし、遠景の森の色と近景の木の平均の明るさを揃える。

static const float FoliageClumpSize = .7;    // 葉群の大きさ(m)
static const float FoliageClumpDepth = .12;  // 葉群の凹凸の深さ(m)
static const half FoliageClumpShade = .3;    // 葉群の山と谷の明るさの差
static const half FoliageWrap = .5;          // 光が影の側へ回り込む量
static const half FoliageTranslucency = .35; // 逆光で外側の葉が透ける強さ
static const half FoliageTint = .16;         // 1本ごとの明るさと色合いのばらつき
static const float FoliageTextureTile = 2;   // 葉の色画像1枚の大きさ(m)

InputData FoliageInputData(float3 positionWS, float3 normalWS, float4 positionCS)
{
    InputData lighting = (InputData)0;
    lighting.positionWS = positionWS;
    lighting.normalWS = normalize(normalWS);
    lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
    lighting.shadowCoord = TransformWorldToShadowCoord(positionWS);
    lighting.bakedGI = SampleSH(lighting.normalWS);
    lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
    lighting.shadowMask = half4(1,1,1,1);
    return lighting;
}

half3 FoliageLit(InputData lighting, half3 albedo, half smoothness, half metallic)
{
    SurfaceData surface = (SurfaceData)0;
    surface.albedo = albedo;
    surface.metallic = metallic;
    surface.smoothness = smoothness;
    surface.normalTS = half3(0,0,1);
    surface.occlusion = 1;
    surface.alpha = 1;
    return UniversalFragmentPBR(lighting, surface).rgb;
}

// 葉の色画像は位置から3方向に貼る。葉塊の UV は継ぎ目を作らないためだけの平行投影で、そのまま貼ると斜めに伸びる。
// normalWS は正規化済みで、重み(各成分の2乗)の和が1になる。
half3 FoliageAlbedo(TEXTURE2D_PARAM(map, mapSampler), float3 positionWS, float3 normalWS)
{
    float3 weight = normalWS * normalWS;
    float3 p = positionWS / FoliageTextureTile;
    return SAMPLE_TEXTURE2D(map, mapSampler, p.zy).rgb * weight.x + SAMPLE_TEXTURE2D(map, mapSampler, p.xz).rgb * weight.y
        + SAMPLE_TEXTURE2D(map, mapSampler, p.xy).rgb * weight.z;
}

float FoliageHash(float3 p)
{
    p = frac(p * .3183099 + .1) * 17;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float FoliageNoise(float3 p)
{
    float3 i = floor(p), f = frac(p);
    f = f * f * (3 - 2 * f);
    return lerp(lerp(lerp(FoliageHash(i), FoliageHash(i + float3(1,0,0)), f.x),
                     lerp(FoliageHash(i + float3(0,1,0)), FoliageHash(i + float3(1,1,0)), f.x), f.y),
                lerp(lerp(FoliageHash(i + float3(0,0,1)), FoliageHash(i + float3(1,0,1)), f.x),
                     lerp(FoliageHash(i + float3(0,1,1)), FoliageHash(i + float3(1,1,1)), f.x), f.y), f.z);
}

// clumpPosition は木に固定した座標(m。葉群が木と一緒に回る)、origin は木の置き場所で色むらと葉群の配置の種になる。
// shade は頂点色R、amount は葉の陰影を掛ける割合(葉は1、幹は0)。分岐せず割合で混ぜ、画面微分を分岐の外に保つ。
half3 FoliageShade(InputData lighting, half3 albedo, half smoothness, half metallic, half shade, float3 clumpPosition, float3 origin, half amount)
{
    float3 seed = float3(FoliageHash(origin), FoliageHash(origin + 31.7), FoliageHash(origin + 57.3));
    float3 p = clumpPosition / FoliageClumpSize + seed * 64;
    float clump = .65 * FoliageNoise(p) + .35 * FoliageNoise(p * 2.3 + 17);
    float3 dpdx = ddx(lighting.positionWS), dpdy = ddy(lighting.positionWS);
    // 葉群が画面上で数画素まで縮んだら凹凸を消す。微分で作る凹凸はその大きさでちらつく。
    float detail = saturate((FoliageClumpSize / max(max(length(dpdx), length(dpdy)), 1e-5) - 3) / 6);
    float height = FoliageClumpDepth * detail * clump * amount;
    // 画面微分による凹凸(Mikkelsen 2010)。接線を使わないので法線だけのメッシュでよい。
    float3 r1 = cross(dpdy, lighting.normalWS), r2 = cross(lighting.normalWS, dpdx);
    float det = dot(dpdx, r1);
    float3 bumped = abs(det) * lighting.normalWS - sign(det) * (ddx(height) * r1 + ddy(height) * r2);
    if (dot(bumped, bumped) > 1e-20) lighting.normalWS = normalize(bumped);
    lighting.bakedGI = SampleSH(lighting.normalWS);

    albedo *= lerp(1, shade * (1 + 2 * FoliageClumpShade * (lerp(.5, clump, detail) - .5)) * (1 + FoliageTint * (seed.x - .5)), amount);
    albedo.r *= 1 + amount * FoliageTint * (seed.y - .5);
    half3 color = FoliageLit(lighting, albedo, smoothness, metallic);

    Light light = GetMainLight(lighting.shadowCoord, lighting.positionWS, lighting.shadowMask);
    half nl = dot(lighting.normalWS, light.direction);
    half wrap = saturate((nl + FoliageWrap) / (1 + FoliageWrap)) - saturate(nl);
    half back = FoliageTranslucency * shade * pow(saturate(dot(lighting.viewDirectionWS, -light.direction)), 4);
    return color + amount * albedo * light.color * light.distanceAttenuation * (wrap * light.shadowAttenuation + back);
}

#endif
