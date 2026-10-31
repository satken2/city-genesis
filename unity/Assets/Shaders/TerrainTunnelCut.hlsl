#ifndef TERRAIN_TUNNEL_CUT_INCLUDED
#define TERRAIN_TUNNEL_CUT_INCLUDED

// 坑口の内空(トンネルの内側の空間)にある地形を描かない。
// 地形は高さ場なので、掘削でできた坑口の段差が坑口をふさいでしまう。坑門の厚みの範囲で内空の箱に入る地形だけを
// 全パス(色・影・深度)から捨て、外から坑内が見えるようにする。箱の外の地形は残るので、地山に穴は開かない。
// 箱は TunnelCutVolume が坑口ごとに渡す。行列はワールド座標を箱の正規化座標([-1,1]^3)へ写す。

#define TUNNEL_CUT_MAX 32 // TunnelCutVolume.MaxCount と一致させる

float4x4 _TunnelCutWorldToBox[TUNNEL_CUT_MAX];
float4 _TunnelCutSpheres[TUNNEL_CUT_MAX]; // xyz=箱の中心、w=外接球の半径の2乗。行列計算の前に遠い箱を除く
int _TunnelCutCount;

void ClipTunnelCuts(float3 positionWS)
{
    for (int i = 0; i < _TunnelCutCount; i++)
    {
        float3 offset = positionWS - _TunnelCutSpheres[i].xyz;
        if (dot(offset, offset) > _TunnelCutSpheres[i].w) continue;
        float3 p = mul(_TunnelCutWorldToBox[i], float4(positionWS, 1.0)).xyz;
        if (all(abs(p) < 1.0)) clip(-1.0);
    }
}

#endif
