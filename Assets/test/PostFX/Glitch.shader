Shader "Hidden/Custom/Glitch"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType"     = "Opaque"
        }

        ZTest Always
        ZWrite Off
        Cull Off

        Pass
        {
            Name "GlitchPass"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // ★ 不要在这里声明 _BlitTexture / sampler_LinearClamp / Varyings / Vert
            //   Blit.hlsl 已经全部提供了

            float _Intensity;
            float _Pulse;

            float _BlockAmount;
            float _BlockCount;
            float _BlockShift;
            float _BlockSpeed;

            float _RgbSplit;

            float _ScanlineIntensity;
            float _ScanlineCount;
            float _ScanlineSpeed;

            float _NoiseAmount;
            float _Vignette;
            float _Saturation;
            float4 _Tint;
            float _TintAmount;
            
            float _Burst;
            

            float Hash11(float n)
            {
                return frac(sin(n * 12.9898) * 43758.5453);
            }

            float Hash21(float2 p)
            {
                return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453);
            }
 half4 Frag(Varyings input) : SV_Target
            {
                float2 uv  = input.texcoord;
                float  t   = _Time.y;
                float  amp = _Intensity * _Pulse;

                // ---------- 1. 块位移（burst 时条带更多、位移更大）----------
                float blockIndex = floor(uv.y * _BlockCount);
                float blockRand  = Hash11(blockIndex + floor(t * _BlockSpeed));

                // ★ burst 时临时提高"有多少条带错位"
                float blockAmount = saturate(_BlockAmount + _Burst * 0.55f);
                float active      = step(1.0 - blockAmount, blockRand);

                float dir = Hash11(blockIndex * 7.13 + floor(t * _BlockSpeed) * 3.71) * 2.0 - 1.0;

                // ★ burst 额外放大位移
                uv.x += dir * active * (_BlockShift * amp + _Burst * 0.05);

                // ---------- 2. RGB 分离（burst 时色差急剧增大）----------
                float2 off = float2(_RgbSplit * amp + _Burst * 0.012, 0.0);

                float3 col;
                col.r = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + off).r;
                col.g = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv).g;
                col.b = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv - off).b;

                // ---------- 3. 扫描线 ----------
                float scan = sin((uv.y * _ScanlineCount + t * _ScanlineSpeed) * 3.14159265 * 2.0);
                col *= saturate(1.0 - _ScanlineIntensity * (scan * 0.5 + 0.5) * amp);

                // ---------- 4. 噪点（burst 时更脏）----------
                float noise = Hash21(uv * _ScreenParams.xy + frac(t) * 137.0);
                col += (noise - 0.5) * (_NoiseAmount * amp + _Burst * 0.18);

                // ---------- 5. 暗角 ----------
                float2 d   = uv - 0.5;
                float  vig = 1.0 - saturate(dot(d, d) * _Vignette * 2.0);
                col *= vig;

                // ---------- 6. 饱和度 + 染色 ----------
                float lum = dot(col, float3(0.299, 0.587, 0.114));

                // ★ burst 时颜色失真（降饱和）
                float sat = _Saturation * (1.0 - saturate(_Burst * 0.65));

                col = lerp(float3(lum, lum, lum), col, sat);
                col = lerp(col, col * _Tint.rgb, _TintAmount);

                // ---------- 7. 短暂闪白 ----------
                col += _Burst * 0.10;

                return half4(col, 1.0);
            }
            
            ENDHLSL
        }
    }

    Fallback Off
}