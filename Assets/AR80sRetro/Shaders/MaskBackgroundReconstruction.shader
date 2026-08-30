Shader "Hidden/AR80sRetro/MaskBackgroundReconstruction"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        Pass
        {
            Name "Reconstruct Masked Camera Background"
            ZTest Always
            ZWrite Off
            Cull Off
            Blend Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            SAMPLER(sampler_BlitTexture);
            TEXTURE2D(_ARObjectRemovalMask);
            SAMPLER(sampler_ARObjectRemovalMask);

            float4 _ARObjectRemovalMaskTexelSize;
            float _ARInpaintRadiusPixels;
            float _ARMaskPaddingPixels;
            float _ARReconstructionStrength;

            float ReadMask(float2 uv)
            {
                return SAMPLE_TEXTURE2D(
                    _ARObjectRemovalMask,
                    sampler_ARObjectRemovalMask,
                    saturate(uv)).r;
            }

            float DilatedMask(float2 uv)
            {
                float2 offset = _ARObjectRemovalMaskTexelSize.xy
                    * max(_ARMaskPaddingPixels, 0.0);
                float mask = ReadMask(uv);
                mask = max(mask, ReadMask(uv + float2(offset.x, 0.0)));
                mask = max(mask, ReadMask(uv - float2(offset.x, 0.0)));
                mask = max(mask, ReadMask(uv + float2(0.0, offset.y)));
                mask = max(mask, ReadMask(uv - float2(0.0, offset.y)));
                return mask;
            }

            float DirectionalDistanceToClear(float2 uv, float2 direction, float maxRadiusPixels)
            {
                float distancePixels = maxRadiusPixels;
                [unroll]
                for (int step = 1; step <= 8; step++)
                {
                    float fraction = (float)step / 8.0;
                    float2 candidate = uv + direction
                        * _BlitTexture_TexelSize.xy
                        * maxRadiusPixels
                        * fraction;
                    // The center pixel is already tested against the dilated mask.
                    // Searching the base mask avoids 9 texture reads per step.
                    if (ReadMask(candidate) < 0.05)
                    {
                        distancePixels = maxRadiusPixels * fraction;
                        break;
                    }
                }

                return distancePixels;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                half4 source = SAMPLE_TEXTURE2D_X(
                    _BlitTexture,
                    sampler_BlitTexture,
                    uv);
                float mask = DilatedMask(uv);
                if (mask < 0.01 || _ARReconstructionStrength <= 0.001)
                {
                    return source;
                }

                float maxRadius = max(_ARInpaintRadiusPixels, 8.0);
                float leftDistance = DirectionalDistanceToClear(uv, float2(-1.0, 0.0), maxRadius);
                float rightDistance = DirectionalDistanceToClear(uv, float2(1.0, 0.0), maxRadius);
                float upDistance = DirectionalDistanceToClear(uv, float2(0.0, 1.0), maxRadius);
                float downDistance = DirectionalDistanceToClear(uv, float2(0.0, -1.0), maxRadius);
                float2 texel = _BlitTexture_TexelSize.xy;
                half3 left = SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_BlitTexture,
                    saturate(uv + float2(-leftDistance * texel.x, 0.0))).rgb;
                half3 right = SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_BlitTexture,
                    saturate(uv + float2(rightDistance * texel.x, 0.0))).rgb;
                half3 up = SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_BlitTexture,
                    saturate(uv + float2(0.0, upDistance * texel.y))).rgb;
                half3 down = SAMPLE_TEXTURE2D_X(
                    _BlitTexture, sampler_BlitTexture,
                    saturate(uv + float2(0.0, -downDistance * texel.y))).rgb;
                float horizontalWeight = 1.0 / max(1.0, leftDistance + rightDistance);
                float verticalWeight = 1.0 / max(1.0, upDistance + downDistance);
                half3 horizontal = lerp(left, right,
                    leftDistance / max(1.0, leftDistance + rightDistance));
                half3 vertical = lerp(down, up,
                    downDistance / max(1.0, downDistance + upDistance));
                half3 reconstructed = (
                    horizontal * horizontalWeight
                    + vertical * verticalWeight)
                    / max(0.0001, horizontalWeight + verticalWeight);
                float blend = smoothstep(0.02, 0.65, mask)
                    * saturate(_ARReconstructionStrength);
                return half4(lerp(source.rgb, reconstructed, blend), source.a);
            }
            ENDHLSL
        }
    }
}
