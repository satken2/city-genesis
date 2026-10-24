Shader "Hidden/DioramaBlur"
{
    HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        #define SAMPLE_COUNT 64
        #define GOLDEN_ANGLE 2.39996323

        TEXTURE2D_X(_DioramaBlurTexture);
        float4 _DioramaParams;    // x: focus distance (eye depth, m), y: blur strength, z: maximum blur radius in half-resolution pixels.
        float4 _DioramaHalfTexel; // xy: texel size of the half-resolution textures.

        // Signed blur amount in [-1, 1]: negative in front of the focus, positive behind it.
        // Depending only on the depth-to-focus ratio, as a lens shooting a miniature does, keeps the look independent of camera altitude.
        float CircleOfConfusion(float2 uv)
        {
            float rawDepth = SampleSceneDepth(uv);
            float eyeDepth = unity_OrthoParams.w == 0.0 ? LinearEyeDepth(rawDepth, _ZBufferParams) : LinearDepthToEyeDepth(rawDepth);
            return clamp((1.0 - _DioramaParams.x / max(eyeDepth, 1e-4)) * _DioramaParams.y, -1.0, 1.0);
        }

        float4 FragPrefilter(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float3 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).rgb;
            return float4(color, CircleOfConfusion(uv));
        }

        // Gather over a disk at half resolution. A sample reaches this pixel when its blur radius covers the distance.
        // A nearer sample spreads its own blur, but a farther one cannot spread past this pixel's blur,
        // so sharp foreground edges do not bleed into the blurred background.
        float4 FragGather(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float maxRadius = _DioramaParams.z;
            float4 center = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
            float centerRadius = abs(center.a) * maxRadius;

            float3 sum = center.rgb;
            float weightSum = 1.0;
            [unroll]
            for (int i = 0; i < SAMPLE_COUNT; i++)
            {
                float theta = i * GOLDEN_ANGLE;
                float2 offset = float2(cos(theta), sin(theta)) * sqrt((i + 0.5) / SAMPLE_COUNT) * maxRadius;
                float4 s = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + offset * _DioramaHalfTexel.xy);
                float sampleRadius = abs(s.a) * maxRadius;
                float radius = s.a < center.a ? sampleRadius : min(sampleRadius, centerRadius);
                float weight = saturate(radius - length(offset) + 1.0);
                sum += s.rgb * weight;
                weightSum += weight;
            }
            return float4(sum / weightSum, center.a);
        }

        // Keep the full-resolution image where the blur is under about one pixel, since the half-resolution result softens it.
        float4 FragComposite(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float4 sharp = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_PointClamp, uv);
            float3 blurred = SAMPLE_TEXTURE2D_X(_DioramaBlurTexture, sampler_LinearClamp, uv).rgb;
            float fullResolutionRadius = abs(CircleOfConfusion(uv)) * _DioramaParams.z * 2.0;
            return float4(lerp(sharp.rgb, blurred, smoothstep(1.0, 3.0, fullResolutionRadius)), sharp.a);
        }
    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZTest Always ZWrite Off Cull Off

        Pass
        {
            Name "Diorama Prefilter"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragPrefilter
            ENDHLSL
        }

        Pass
        {
            Name "Diorama Gather"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragGather
            ENDHLSL
        }

        Pass
        {
            Name "Diorama Composite"
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment FragComposite
            ENDHLSL
        }
    }
}
