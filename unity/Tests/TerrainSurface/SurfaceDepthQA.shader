Shader "Tests/SurfaceDepthQA"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Factor ("Factor", Float) = 0
        _Units ("Units", Float) = 0
    }
    SubShader
    {
        Pass
        {
            Offset [_Factor], [_Units]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Color;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            float4 frag() : SV_Target { return _Color; }
            ENDCG
        }
    }
}
