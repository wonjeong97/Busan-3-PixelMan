// PersonPixelate.shader
// 마스크와 컬러 모두 Point 필터(nearest-neighbor)로 강제 샘플링.
// 마스크 경계가 픽셀 블록에 정확히 정렬되어 각진 실루엣을 만듭니다.

Shader "PixelMan/PersonPixelate"
{
    Properties
    {
        _MainTex             ("Camera Texture",          2D)          = "white" {}
        _MaskTex             ("Segmentation Mask",       2D)          = "black" {}
        _PixelSize           ("Pixel Size (texels)",     Float)       = 8.0
        _Threshold           ("Mask Threshold",          Range(0, 1)) = 0.5
        _FlipMaskX           ("Flip Mask X",             Float)       = 0.0
        _UsePerBodyPixelSize ("Use Per-Body Pixel Size", Float)       = 0.0
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

            // Texture2D + SamplerState 분리 선언:
            // sampler_point_clamp 은 Unity 빌트인 nearest-neighbor+clamp 샘플러
            Texture2D    _MainTex;
            Texture2D    _MaskTex;
            SamplerState sampler_point_clamp;
            float4       _MainTex_TexelSize;   // (1/w, 1/h, w, h)
            float        _PixelSize;
            float        _Threshold;
            float        _FlipMaskX;
            float        _UsePerBodyPixelSize;
            float        _PixelSizes[6];       // 바디 0-5 별 픽셀 크기

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
                float texW = _MainTex_TexelSize.z;  // 1920
                float texH = _MainTex_TexelSize.w;  // 1080

                float  maskX_orig  = _FlipMaskX > 0.5 ? 1.0 - i.uv.x : i.uv.x;
                float2 origMaskUV  = float2(maskX_orig, 1.0 - i.uv.y);

                // ── 1. 원본 UV 마스크로 바디 인덱스 → 픽셀 크기 ──────────────
                float ps;
                if (_UsePerBodyPixelSize > 0.5)
                {
                    float mv0 = _MaskTex.Sample(sampler_point_clamp, origMaskUV).r;
                    int   bi  = clamp((int)round(mv0 * 6.0) - 1, 0, 5);
                    ps = max(_PixelSizes[bi], 1.0);
                }
                else
                {
                    ps = max(_PixelSize, 1.0);
                }

                // ── 2. 정수 텍셀 공간에서 블록 중심 계산 (정확한 픽셀 정렬) ──
                // i.uv 를 texW/texH 배수 단위로 스냅하면 블록 경계가 텍셀에 정확히 맞음
                float2 texCoord   = i.uv * float2(texW, texH);
                float2 blockTexel = floor(texCoord / ps) * ps + ps * 0.5;
                float2 blockUV    = clamp(blockTexel / float2(texW, texH), 0.0, 1.0);

                // ── 3. 마스크를 블록 중심 UV 로 Point 샘플 → 각진 경계 ────────
                float  maskX_blk  = _FlipMaskX > 0.5 ? 1.0 - blockUV.x : blockUV.x;
                float  maskVal    = _MaskTex.Sample(sampler_point_clamp,
                                        float2(maskX_blk, 1.0 - blockUV.y)).r;

                if (maskVal < _Threshold)
                    return fixed4(0, 0, 0, 1);

                // ── 4. 픽셀화된 색상 (Point 샘플 = 선명한 블록 픽셀) ──────────
                return fixed4(_MainTex.Sample(sampler_point_clamp, blockUV).rgb, 1.0);
            }
            ENDCG
        }
    }
}
