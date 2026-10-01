Shader "CityGenesis/UndergroundSoil"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Background" }
        Pass
        {
            ZWrite Off
            ZTest Always
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
                float2 pixels = floor(input.uv * _ScreenParams.xy / 2);
                float grain = frac(sin(dot(pixels,float2(12.9898,78.233))) * 43758.5453);
                float strata = sin(input.uv.y * 65 + sin(input.uv.x * 8) * .5);
                half3 soil = half3(.22,.145,.09) * (.92 + grain*.12 + strata*.04);
                return half4(soil,1);
            }
            ENDHLSL
        }
    }
}
