Shader "Hidden/RoadMarkingBake"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            float4 _BakeRect;
            float4 Vert(float4 vertex : POSITION) : SV_POSITION
            {
                float2 uv = (vertex.xy - _BakeRect.xy) / _BakeRect.zw;
                // 描画先テクスチャのUVと一致させ、APIごとの上下反転を吸収する。
                float2 clip = uv * 2 - 1;
                #if UNITY_UV_STARTS_AT_TOP
                    clip.y = -clip.y;
                #endif
                return float4(clip, 0, 1);
            }
            half4 Frag() : SV_Target { return 1; }
            ENDHLSL
        }
    }
}
