Shader "CityGenesis/UndergroundShore"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent+20" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes input) { Varyings o; o.positionCS = TransformObjectToHClip(input.positionOS); o.uv = input.uv; return o; }
            half4 Frag(Varyings input) : SV_Target
            {
                float edge = abs(input.uv.y-.5)*2;
                float core = 1-smoothstep(.08,.3,edge);
                float wave = pow(saturate(.5+.5*cos(edge*15-_Time.y*1.6+sin(input.uv.x*.1))),4) * (1-edge);
                return half4(.57,.79,.85,saturate(core*.7+wave*.25));
            }
            ENDHLSL
        }
    }
}
