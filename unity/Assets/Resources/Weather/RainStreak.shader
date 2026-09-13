Shader "CityGenesis/Weather/RainStreak"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off Cull Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float4 positionOS : POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float4 color : COLOR; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes i)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(i.positionOS.xyz);
                o.color = i.color; o.uv = i.uv;
                return o;
            }
            half4 Frag(Varyings i) : SV_Target
            {
                float2 d = abs(i.uv * 2 - 1);
                float alpha = pow(saturate(1-d.x*d.x), 2) * saturate(1-d.y*d.y);
                return half4(i.color.rgb, i.color.a * alpha);
            }
            ENDHLSL
        }
    }
}
