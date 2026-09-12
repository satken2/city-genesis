Shader "Custom/EarthworkPreview"
{
    SubShader
    {
        Tags { "Queue"="Transparent+20" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Always
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct Input { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct Output { float4 vertex : SV_POSITION; fixed4 color : COLOR; };
            Output vert(Input v) { Output o; o.vertex=UnityObjectToClipPos(v.vertex); o.color=v.color; return o; }
            fixed4 frag(Output v) : SV_Target { return v.color; }
            ENDCG
        }
    }
}
