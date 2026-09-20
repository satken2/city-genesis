#ifndef DEPTH_OFFSET_TERRAIN_LIT_MACRO_VARIATION_INCLUDED
#define DEPTH_OFFSET_TERRAIN_LIT_MACRO_VARIATION_INCLUDED

#include "Assets/Shaders/TerrainSurfaceAppearance.hlsl"

#define SplatmapFragment DepthOffsetTerrainLitOriginalSplatmapFragment
#define UniversalFragmentPBR CliffTerrainFragmentPBR
#include "Packages/com.unity.render-pipelines.universal/Shaders/Terrain/TerrainLitPasses.hlsl"
#undef UniversalFragmentPBR
#undef SplatmapFragment

#ifdef TERRAIN_GBUFFER
// GBufferパスでは崖の岩ブレンド(ApplyCliffRock)は適用されない
// (TERRAIN_GBUFFER 分岐は UniversalFragmentPBR を呼ばないため)。
// 本プロジェクトはForward+レンダリングなのでこのパスは使われない。
GBufferFragOutput SplatmapFragment(Varyings IN)
{
    GBufferFragOutput output = DepthOffsetTerrainLitOriginalSplatmapFragment(IN);
    output.gBuffer0.rgb = ApplyTerrainMacroVariation(output.gBuffer0.rgb, IN.positionWS);
    output.gBuffer0.rgb = ApplyTerrainColorGrade(output.gBuffer0.rgb, IN.positionWS);
    output.gBuffer0.rgb = ApplyContourLines(output.gBuffer0.rgb, IN.positionWS);
    output.color.rgb = ApplyTerrainMacroVariation(output.color.rgb, IN.positionWS);
    output.color.rgb = ApplyTerrainColorGrade(output.color.rgb, IN.positionWS);
    output.color.rgb = ApplyContourLines(output.color.rgb, IN.positionWS);
    return output;
}
#else
void SplatmapFragment(
    Varyings IN
    , out half4 outColor : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
    )
{
    DepthOffsetTerrainLitOriginalSplatmapFragment(
        IN
        , outColor
#ifdef _WRITE_RENDERING_LAYERS
        , outRenderingLayers
#endif
    );

    outColor.rgb = ApplyTerrainMacroVariation(outColor.rgb, IN.positionWS);
    outColor.rgb = ApplyTerrainColorGrade(outColor.rgb, IN.positionWS);
    outColor.rgb = ApplyContourLines(outColor.rgb, IN.positionWS);
}
#endif

#endif
