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
        _ErosionSize         ("Erosion Size",            Float)       = 5.0 
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
            #include "UnityCG.cginc"

            Texture2D    _MainTex;
            Texture2D    _MaskTex;
            SamplerState sampler_point_clamp;

            float        _PixelSize;
            float        _Threshold;
            float        _FlipMaskX;
            float        _UsePerBodyPixelSize;
            float        _ErosionSize;

            float        _PixelSize0;
            float        _PixelSize1;
            float        _PixelSize2;
            float        _PixelSize3;
            float        _PixelSize4;
            float        _PixelSize5;

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

                // 1. 원본 UV에서 마스크를 읽어 현재 픽셀이 어떤 사람인지 식별
                float  maskX_orig  = _FlipMaskX > 0.5 ? 1.0 - i.uv.x : i.uv.x;
                float2 origMaskUV  = float2(maskX_orig, 1.0 - i.uv.y);
                float  origBaseMask = _MaskTex.Sample(sampler_point_clamp, origMaskUV).r;

                // 2. 식별된 사람의 픽셀 크기(ps) 결정. 사람이 아니면 전역 기본값 사용.
                float ps = _PixelSize;
                if (_UsePerBodyPixelSize > 0.5 && origBaseMask >= _Threshold)
                {
                    int bi = clamp((int)round(origBaseMask * 6.0) - 1, 0, 5);
                    if (bi == 0) ps = _PixelSize0;
                    else if (bi == 1) ps = _PixelSize1;
                    else if (bi == 2) ps = _PixelSize2;
                    else if (bi == 3) ps = _PixelSize3;
                    else if (bi == 4) ps = _PixelSize4;
                    else if (bi == 5) ps = _PixelSize5;
                }
                ps = max(ps, 1.0);

                // 3. 현재 픽셀이 속한 "큰 픽셀 블록의 정중앙 UV"를 계산
                float2 texCoord   = i.uv * float2(texW, texH);
                float2 blockTexel = floor(texCoord / ps) * ps + ps * 0.5;
                float2 blockUV    = clamp(blockTexel / float2(texW, texH), 0.0, 1.0);

                // 4. [핵심] 마스크 자르기 판정을 1픽셀 단위가 아닌 "블록의 정중앙"에서 수행
                // 블록 중심이 마스크 안에 있으면 블록 전체를 그리고, 밖이면 전체를 날려버림 (각진 실루엣 형성)
                float  maskX_block = _FlipMaskX > 0.5 ? 1.0 - blockUV.x : blockUV.x;
                float2 blockMaskUV = float2(maskX_block, 1.0 - blockUV.y);
                float  blockMask = _MaskTex.Sample(sampler_point_clamp, blockMaskUV).r;

                // 5. 블록 중심 기준 침식(Erosion) 연산
                if (_ErosionSize > 0.0)
                {
                    float offsetX = _ErosionSize / texW;
                    float offsetY = _ErosionSize / texH;

                    float maskUp = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2(0, offsetY)).r;
                    float maskDown = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2(0, -offsetY)).r;
                    float maskLeft = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2(-offsetX, 0)).r;
                    float maskRight = _MaskTex.Sample(sampler_point_clamp, blockMaskUV + float2(offsetX, 0)).r;

                    if (maskUp < _Threshold || maskDown < _Threshold || maskLeft < _Threshold || maskRight < _Threshold)
                    {
                        return fixed4(0, 0, 0, 1);
                    }
                }

                if (blockMask < _Threshold) return fixed4(0, 0, 0, 1);

                // 6. 색상 출력
                return fixed4(_MainTex.Sample(sampler_point_clamp, blockUV).rgb, 1.0);
            }
            ENDCG
        }
    }
}