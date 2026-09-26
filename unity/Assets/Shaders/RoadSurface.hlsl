#ifndef CITY_ROAD_SURFACE_INCLUDED
#define CITY_ROAD_SURFACE_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/Shaders/LitInput.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

TEXTURE2D(_RoadPageTable); SAMPLER(sampler_RoadPageTable);
TEXTURE2D_ARRAY(_RoadMarkingAtlas0); SAMPLER(sampler_RoadMarkingAtlas0);
TEXTURE2D_ARRAY(_RoadMarkingAtlas1); SAMPLER(sampler_RoadMarkingAtlas1);
TEXTURE2D_ARRAY(_RoadMarkingAtlas2); SAMPLER(sampler_RoadMarkingAtlas2);
float4 _RoadPageGrid;
float4 _RoadBands[32];
float4 _RoadDistances;
float4 _RoadApproaches;
float _RoadBandCount, _RoadPaintEnabled;

struct RoadAttributes
{
    float4 positionOS : POSITION;
    float3 normalOS : NORMAL;
    float4 tangentOS : TANGENT;
    float2 uv : TEXCOORD0;
    float4 paint : TEXCOORD3;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};
struct RoadVaryings
{
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    half3 normalWS : TEXCOORD1;
    half4 tangentWS : TEXCOORD2;
    float2 uv : TEXCOORD3;
    float3 paint : TEXCOORD4;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

RoadVaryings RoadVertex(RoadAttributes input)
{
    RoadVaryings output = (RoadVaryings)0;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
    VertexNormalInputs normal = GetVertexNormalInputs(input.normalOS, input.tangentOS);
    output.positionCS = position.positionCS; output.positionWS = position.positionWS;
    output.normalWS = normal.normalWS;
    output.tangentWS = half4(normal.tangentWS, input.tangentOS.w * GetOddNegativeScale());
    output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
    output.paint = input.paint.xyz;
    return output;
}

float RoadMask(float2 coordinate)
{
    if (_RoadPageGrid.z < .5) return 0;
    float2 page = floor(coordinate / 32.0) - _RoadPageGrid.xy;
    if (any(page < 0) || any(page >= _RoadPageGrid.zw)) return 0;
    float2 address = SAMPLE_TEXTURE2D_LOD(_RoadPageTable, sampler_RoadPageTable, (page + .5) / _RoadPageGrid.zw, 0).rg;
    if (address.x < .5) return 0;
    float size = address.y < .5 ? 256 : address.y < 1.5 ? 512 : 1024;
    float scale = (size - 4) / size;
    float2 uv = frac(coordinate / 32.0) * scale + 2 / size;
    // fracの境界を微分するとミップが過度に粗くなるため、連続座標から勾配を渡す。
    float2 dx = ddx(coordinate / 32.0) * scale, dy = ddy(coordinate / 32.0) * scale;
    if (address.y < .5) return SAMPLE_TEXTURE2D_ARRAY_GRAD(_RoadMarkingAtlas0, sampler_RoadMarkingAtlas0, uv, address.x - 1, dx, dy).r;
    if (address.y < 1.5) return SAMPLE_TEXTURE2D_ARRAY_GRAD(_RoadMarkingAtlas1, sampler_RoadMarkingAtlas1, uv, address.x - 1, dx, dy).r;
    return SAMPLE_TEXTURE2D_ARRAY_GRAD(_RoadMarkingAtlas2, sampler_RoadMarkingAtlas2, uv, address.x - 1, dx, dy).r;
}

float RoadLines(float2 st)
{
    float coverage = 0;
    float across = max(fwidth(st.y), .0001);
    float along = max(fwidth(st.x), .0001);
    bool junction = st.x < _RoadApproaches.x || _RoadDistances.x - st.x < _RoadApproaches.y;
    bool crossing = st.x < _RoadApproaches.z || _RoadDistances.x - st.x < _RoadApproaches.w;
    for (int i = 0; i < min(32, (int)_RoadBandCount); i++)
    {
        float4 band = _RoadBands[i];
        float bandCoverage = saturate((band.y * .5 - abs(st.y - band.x)) / across + .5);
        if (band.w > .5 && crossing) bandCoverage = 0;
        if (band.z > .5 && !junction)
        {
            float phase = frac((st.x + _RoadDistances.y) / 4.0);
            float edge = abs(phase - .25) - .25;
            float dash = saturate(.5 - edge * 4.0 / along);
            // 周期自体が画素より細かくなったら、被覆率へ収束させる。
            bandCoverage *= lerp(dash, .5, saturate(along / 4.0 - .5));
        }
        coverage = max(coverage, bandCoverage);
    }
    return coverage;
}

half4 RoadFragment(RoadVaryings input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    SurfaceData surface;
    InitializeStandardLitSurfaceData(input.uv, surface);
    float distanceToCamera = distance(input.positionWS, GetCameraPositionWS());
    float fade = 1 - smoothstep(400, 540, distanceToCamera);
    float paint = 0;
    if (_RoadPaintEnabled > .5 && input.paint.z > .99)
        paint = max(RoadLines(input.paint.xy), RoadMask(input.paint.xy)) * fade;
    surface.albedo = lerp(surface.albedo, half3(.78,.78,.72), paint);
    surface.smoothness = lerp(surface.smoothness, .28, paint);
    InputData data = (InputData)0;
    data.positionWS = input.positionWS;
    half3 bitangent = input.tangentWS.w * cross(input.normalWS, input.tangentWS.xyz);
    data.normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(surface.normalTS, half3x3(input.tangentWS.xyz, bitangent, input.normalWS)));
    data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
    data.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    data.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
    data.bakedGI = SampleSH(data.normalWS); data.shadowMask = half4(1,1,1,1);
    data.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1), 0);
    half4 color = UniversalFragmentPBR(data, surface);
    color.rgb = MixFog(color.rgb, data.fogCoord); color.a = 1;
    return color;
}
#endif
