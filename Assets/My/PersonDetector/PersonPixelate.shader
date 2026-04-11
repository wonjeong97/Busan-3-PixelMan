// PersonPixelate.shader
// 카메라 텍스처를 픽셀화하고, 마스크 기준으로 사람 부분만 표시합니다.
// 마스크 미만인 픽셀은 검정 출력.

Shader "PixelMan/PersonPixelate"
{
    Properties
    {
        _MainTex   ("Camera Texture",       2D)            = "white" {}
        _MaskTex   ("Segmentation Mask",    2D)            = "black" {}
        _PixelSize ("Pixel Size (texels)",  Float)         = 8.0
        _Threshold ("Mask Threshold",       Range(0, 1))   = 0.5
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

            sampler2D _MainTex;
            float4    _MainTex_TexelSize;   // (1/w, 1/h, w, h)
            sampler2D _MaskTex;
            float     _PixelSize;
            float     _Threshold;

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
                // ── 마스크 샘플링 ───────────────────────────────
                // maskData는 row0 = 이미지 상단 (MediaPipe convention).
                // Texture2D는 row0 = 하단이므로 Y 반전.
                float2 maskUV  = float2(i.uv.x, 1.0 - i.uv.y);
                float  maskVal = tex2D(_MaskTex, maskUV).r;

                // 마스크 미만 → 검정 (불투명)
                if (maskVal < _Threshold)
                    return fixed4(0, 0, 0, 1);

                // ── 픽셀화 ─────────────────────────────────────
                // UV를 _PixelSize 텍셀 단위로 스냅 → 블록 중심 샘플
                float2 step   = _MainTex_TexelSize.xy * _PixelSize;
                float2 blockUV = floor(i.uv / step) * step + step * 0.5;
                blockUV = clamp(blockUV, 0.0, 1.0);

                return tex2D(_MainTex, blockUV);
            }
            ENDCG
        }
    }
}
