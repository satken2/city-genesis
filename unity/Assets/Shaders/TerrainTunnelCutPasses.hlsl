#ifndef TERRAIN_TUNNEL_CUT_PASSES_INCLUDED
#define TERRAIN_TUNNEL_CUT_PASSES_INCLUDED

// URP 標準の地形の影・深度・深度法線パスに、坑口の内空の切り抜き(TerrainTunnelCut.hlsl)を足す。
// 標準パスの補間値はワールド座標を持たないので、頂点で求めて渡し、断片では切り抜いてから標準の処理を呼ぶ。
// TerrainLitPasses.hlsl(深度法線は TerrainLitDepthNormalsPass.hlsl)の後に include する。

#include "Assets/Shaders/TerrainTunnelCut.hlsl"

// TerrainInstancing は頂点を地形の高さへ動かす。標準の頂点関数と同じ位置を得るため同じ処理を通す。
float3 TunnelCutTerrainPositionWS(float4 positionOS, float3 normalOS, float2 texcoord)
{
    TerrainInstancing(positionOS, normalOS, texcoord);
    return TransformObjectToWorld(positionOS.xyz);
}

struct VaryingsTunnelCutLean
{
    float4 clipPos      : SV_POSITION;
    float2 texcoord     : TEXCOORD0;
    float3 positionWS   : TEXCOORD1;
    UNITY_VERTEX_OUTPUT_STEREO
};

VaryingsTunnelCutLean ToTunnelCutLean(VaryingsLean lean, AttributesLean v)
{
    VaryingsTunnelCutLean o = (VaryingsTunnelCutLean)0;
    o.clipPos = lean.clipPos;
    o.texcoord = lean.texcoord;
    UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(lean, o);
    UNITY_SETUP_INSTANCE_ID(v);
    o.positionWS = TunnelCutTerrainPositionWS(v.position, v.normalOS, v.texcoord);
    return o;
}

VaryingsLean ToLean(VaryingsTunnelCutLean IN)
{
    VaryingsLean lean = (VaryingsLean)0;
    lean.clipPos = IN.clipPos;
    lean.texcoord = IN.texcoord;
    return lean;
}

VaryingsTunnelCutLean TunnelCutShadowPassVertex(AttributesLean v)
{
    return ToTunnelCutLean(ShadowPassVertex(v), v);
}

half4 TunnelCutShadowPassFragment(VaryingsTunnelCutLean IN) : SV_TARGET
{
    ClipTunnelCuts(IN.positionWS);
    return ShadowPassFragment(ToLean(IN));
}

VaryingsTunnelCutLean TunnelCutDepthOnlyVertex(AttributesLean v)
{
    return ToTunnelCutLean(DepthOnlyVertex(v), v);
}

half4 TunnelCutDepthOnlyFragment(VaryingsTunnelCutLean IN) : SV_TARGET
{
    ClipTunnelCuts(IN.positionWS);
    return DepthOnlyFragment(ToLean(IN));
}

#ifdef UNIVERSAL_FORWARD_LIT_DEPTH_NORMALS_PASS_INCLUDED
struct VaryingsTunnelCutDepthNormal
{
    VaryingsDepthNormal base;
    float3 positionWS : TEXCOORD7;
};

VaryingsTunnelCutDepthNormal TunnelCutDepthNormalOnlyVertex(AttributesDepthNormal v)
{
    VaryingsTunnelCutDepthNormal o;
    o.base = DepthNormalOnlyVertex(v);
    UNITY_SETUP_INSTANCE_ID(v);
    o.positionWS = TunnelCutTerrainPositionWS(v.positionOS, v.normalOS, v.texcoord);
    return o;
}

void TunnelCutDepthNormalOnlyFragment(
    VaryingsTunnelCutDepthNormal IN
    , out half4 outNormalWS : SV_Target0
#ifdef _WRITE_RENDERING_LAYERS
    , out uint outRenderingLayers : SV_Target1
#endif
    )
{
    ClipTunnelCuts(IN.positionWS);
    DepthNormalOnlyFragment(
        IN.base
        , outNormalWS
#ifdef _WRITE_RENDERING_LAYERS
        , outRenderingLayers
#endif
    );
}
#endif

#endif
