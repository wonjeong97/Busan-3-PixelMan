Shader "PixelMan/PersonPixelate"
{
    Properties
    {
        _MainTex             ("Camera Texture",      2D)          = "white" {}
        _MaskTex             ("Body Index Mask",     2D)          = "black" {}
        _PixelSize           ("Default Pixel Size",  Float)       = 8.0
        _Threshold           ("Mask Threshold",      Range(0, 1)) = 0.05
        _FlipMaskX           ("Flip Mask X",         Float)       = 0.0
        _UsePerBodyPixelSize ("Per-Body Pixel Size", Float)       = 0.0
        _ErosionSize         ("Erosion Size",         Float)       = 0.0
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.0
            #include "UnityCG.cginc"

            Texture2D    _MainTex;
            Texture2D    _MaskTex;
            SamplerState sampler_point_clamp;

            float _PixelSize;
            float _Threshold;
            float _FlipMaskX;
            float _UsePerBodyPixelSize;
            float _ErosionSize;

            float _PixelSize0, _PixelSize1, _PixelSize2;
            float _PixelSize3, _PixelSize4, _PixelSize5;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f     { float4 pos    : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv  = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float texW = 1920.0;
                float texH = 1080.0;
                float2 texCoord = i.uv * float2(texW, texH);

                if (_UsePerBodyPixelSize > 0.5)
                {
                    // ── 바디별 독립 블록 격자 처리 ─────────────────────────────
                    // 각 바디는 자신의 픽셀 크기(ps)로 블록을 만들고
                    // 해당 블록 중심이 자신의 마스크 안에 있으면 색상 출력.
                    // 1m 사람=48px 격자, 2.5m 사람=4px 격자 → 각자 경계가 각자 격자로 잘림.
                    float bodyPs[6];
                    bodyPs[0] = max(_PixelSize0, 1.0);
                    bodyPs[1] = max(_PixelSize1, 1.0);
                    bodyPs[2] = max(_PixelSize2, 1.0);
                    bodyPs[3] = max(_PixelSize3, 1.0);
                    bodyPs[4] = max(_PixelSize4, 1.0);
                    bodyPs[5] = max(_PixelSize5, 1.0);

                    [loop]
                    for (int b = 0; b < 6; b++)
                    {
                        float ps = bodyPs[b];

                        // 이 바디의 블록 격자로 블록 중심 UV 계산
                        float2 blockTexel = floor(texCoord / ps) * ps + ps * 0.5;
                        float2 blockUV    = clamp(blockTexel / float2(texW, texH), 0.0, 1.0);

                        float  maskX       = _FlipMaskX > 0.5 ? 1.0 - blockUV.x : blockUV.x;
                        float2 blockMaskUV = float2(maskX, 1.0 - blockUV.y);
                        float  maskVal     = _MaskTex.Sample(sampler_point_clamp, blockMaskUV).r;

                        // 블록 중심이 이 바디(b)인지 확인
                        int maskBodyIdx = (int)round(maskVal * 6.0) - 1;
                        if (maskBodyIdx != b) continue;

                        // 침식 (ErosionSize > 0 일 때 경계 블록 제거)
                        if (_ErosionSize > 0.0)
                        {
                            float ox = _ErosionSize / texW;
                            float oy = _ErosionSize / texH;
                            float mu = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2( 0,  oy)).r;
                            float md = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2( 0, -oy)).r;
                            float ml = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2(-ox,  0)).r;
                            float mr = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2( ox,  0)).r;
                            if ((int)round(mu*6.0)-1 != b || (int)round(md*6.0)-1 != b ||
                                (int)round(ml*6.0)-1 != b || (int)round(mr*6.0)-1 != b)
                                continue;
                        }

                        return fixed4(_MainTex.Sample(sampler_point_clamp, blockUV).rgb, 1.0);
                    }

                    return fixed4(0, 0, 0, 1); // 배경
                }
                else
                {
                    // ── 단일 픽셀 크기 모드 (MediaPipe 등 기존 방식) ───────────
                    float ps = max(_PixelSize, 1.0);
                    float2 blockTexel = floor(texCoord / ps) * ps + ps * 0.5;
                    float2 blockUV    = clamp(blockTexel / float2(texW, texH), 0.0, 1.0);

                    float  maskX  = _FlipMaskX > 0.5 ? 1.0 - blockUV.x : blockUV.x;
                    float  maskVal = _MaskTex.Sample(sampler_point_clamp,
                                        float2(maskX, 1.0 - blockUV.y)).r;

                    if (maskVal < _Threshold) return fixed4(0, 0, 0, 1);
                    return fixed4(_MainTex.Sample(sampler_point_clamp, blockUV).rgb, 1.0);
                }
            }
            ENDCG
        }
    }
}
