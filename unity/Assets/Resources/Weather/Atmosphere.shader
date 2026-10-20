Shader "CityGenesis/Weather/Atmosphere"
{
    Properties
    {
        _Zenith ("Zenith", Color) = (0.1,0.3,0.6,1)
        _Horizon ("Horizon", Color) = (0.6,0.75,0.9,1)
        _SunColor ("Sun", Color) = (1,0.9,0.7,1)
        _CloudColor ("Cloud", Color) = (1,1,1,1)
        _SunDirection ("Sun direction", Vector) = (0,1,0,0)
        _Coverage ("Cloud coverage", Range(0,1)) = 0.4
        _Thickness ("Cloud thickness", Float) = 0.6
        _Seed ("Cloud arrangement", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" "RenderPipeline"="UniversalPipeline" }
        Cull Off ZWrite Off
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
            float4 _Zenith, _Horizon, _SunColor, _CloudColor, _SunDirection;
            float _Coverage, _Thickness, _Seed;
            CBUFFER_END
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 direction : TEXCOORD0; };
            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.direction = input.positionOS.xyz;
                return o;
            }
            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1,311.7))) * 43758.5453); }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f*f*(3.0-2.0*f);
                return lerp(lerp(Hash(i), Hash(i+float2(1,0)), f.x),
                    lerp(Hash(i+float2(0,1)), Hash(i+1), f.x), f.y);
            }
            float Fbm(float2 p)
            {
                float n = 0, a = 0.5;
                [unroll] for (int i=0; i<5; i++) { n += Noise(p)*a; p = p*2.03+17.1; a *= 0.5; }
                return n;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float3 d = normalize(input.direction);
                float elevation = saturate(d.y);
                float mu = saturate(dot(d, normalize(_SunDirection.xyz)));
                float3 sky = lerp(_Horizon.rgb, _Zenith.rgb, pow(elevation, 0.45));
                sky += _SunColor.rgb * (pow(mu, 12)*0.16 + pow(mu, 160)*0.22);
                float disk = smoothstep(0.99994, 0.99997, mu);
                sky += _SunColor.rgb * disk * 5;

                // 球面へ投影して地平線での発散を避け、厚みと縁の透過で雲の立体感を作る。
                float2 p = d.xz / (elevation+0.22) * 3.2 + _Seed;
                float n = Fbm(p);
                float threshold = lerp(1.05, 0.12, _Coverage);
                float body = smoothstep(threshold-0.08, threshold+0.20, n);
                float opacity = (1-exp(-body*_Thickness*3.5)) * smoothstep(0, 0.12, d.y);
                float slope = saturate((Fbm(p + _SunDirection.xz*0.14)-n)*5+0.5);
                float3 cloud = _CloudColor.rgb * lerp(0.48, 1.05, slope);
                cloud += _SunColor.rgb * pow(mu, 24) * (1-body)*0.65;
                sky = lerp(sky, cloud, opacity);
                return half4(max(sky,0),1);
            }
            ENDHLSL
        }
    }
}
