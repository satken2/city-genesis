Shader "CityGenesis/UndergroundGround"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(input.positionOS);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return o;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float elevation = input.positionWS.y / 10;
                float width = max(fwidth(elevation), .008);
                float contour = 1 - smoothstep(width, width*2, abs(frac(elevation+.5)-.5));
                half shade = .7 + .3 * saturate(normalize(input.normalWS).y);
                return half4(lerp(half3(.64,.57,.44)*shade, half3(.85,.79,.65), contour), .06 + contour*.08);
            }
            ENDHLSL
        }
    }
}
