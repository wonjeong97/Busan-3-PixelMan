// PersonPixelizer.cs
// PixelMode 에 따라 두 가지 방식을 전환합니다.
//   ObjectDetection : bounding box 기반 픽셀화 (PersonDetectorRunner)
//   Segmentation    : per-pixel 실루엣 마스크 픽셀화 (PersonSegmentRunner + 셰이더)

using Mediapipe.Unity.Sample.ObjectDetection;
using UnityEngine;
using UnityEngine.UI;

namespace PixelMan
{
  public enum PixelMode
  {
    ObjectDetection,   // bounding box 픽셀화
    Segmentation,      // per-pixel 실루엣 픽셀화
  }

  /// <summary>
  /// 씬 설정:
  ///   _cameraDisplay  → 웹캠 RawImage 의 RectTransform
  ///   _pixelOverlay   → _cameraDisplay 의 자식 RawImage
  ///   ObjectDetection 모드 → _detectorRunner 연결
  ///   Segmentation 모드   → _segmentRunner + _pixelateShader 연결
  /// </summary>
  public class PersonPixelizer : MonoBehaviour
  {
    [Header("모드 선택")]
    [SerializeField] private PixelMode _mode = PixelMode.ObjectDetection;

    [Header("참조 (공통)")]
    [SerializeField] private RectTransform _cameraDisplay;
    [SerializeField] private RawImage _pixelOverlay;

    [Header("Object Detection 모드 참조")]
    [SerializeField] private PersonDetectorRunner _detectorRunner;

    [Header("Segmentation 모드 참조")]
    [SerializeField] private PersonSegmentRunner _segmentRunner;
    [SerializeField] private Shader _pixelateShader;

    [Header("픽셀화 설정")]
    [SerializeField] private float _pixelSizeAtFar = 32f;
    [SerializeField] private float _pixelSizeAtNear = 4f;
    [SerializeField, Range(0.1f, 1f)] private float _nearThreshold = 0.6f;

    [Header("수동 픽셀 크기 (테스트용)")]
    [SerializeField] private bool _manualPixelSize = false;
    [SerializeField, Range(1f, 128f)] private float _manualPixelSizeValue = 16f;

    [Header("마스크 임계값 (Segmentation 전용)")]
    [SerializeField, Range(0f, 1f)] private float _maskThreshold = 0.5f;

    [Header("박스 스무딩")]
    [SerializeField, Range(0f, 0.99f)] private float _smoothing = 0.9f;

    [Header("디버그 시각화 (Object Detection 전용)")]
    [SerializeField] private bool _showDebugBox = true;
    [SerializeField] private Color _debugColor = new Color(0f, 1f, 0f, 0.4f);
    [SerializeField] private bool _maskOutside = false;
    [SerializeField] private Color _maskColor = new Color(0f, 0f, 0f, 0.75f);

    // ── 공통 스무딩 상태 ──────────────────────────────────────────────────
    private float _sLeft, _sTop, _sRight, _sBottom;
    private bool _smoothingInitialized;

    // ── Object Detection 상태 ─────────────────────────────────────────────
    private PersonDetectionData[] _currentDetections;

    // ── Segmentation 상태 ────────────────────────────────────────────────
    private Material _material;
    private Texture2D _maskTexture;
    private float[] _pendingMask;
    private int _pendingW, _pendingH;
    private bool _hasPending;

    // ── 공통 ─────────────────────────────────────────────────────────────
    private Texture2D _debugTexture;

    // ═════════════════════════════════════════════════════════════════════
    //  라이프사이클
    // ═════════════════════════════════════════════════════════════════════

    private void Awake()
    {
      if (_pixelateShader != null)
        _material = new Material(_pixelateShader);
    }

    private void OnEnable()
    {
      ApplyRunnerActive(_mode);
      Subscribe(_mode);
      SetupOverlayAnchors(_mode);
      _prevMode = _mode;
    }

    private void OnDisable()
    {
      Unsubscribe(_mode);
      if (_pixelOverlay != null) _pixelOverlay.gameObject.SetActive(false);
      _smoothingInitialized = false;
      _hasPending = false;
    }

    private void OnDestroy()
    {
      if (_material    != null) Destroy(_material);
      if (_maskTexture != null) Destroy(_maskTexture);
      if (_debugTexture != null) Destroy(_debugTexture);
    }

    // 런타임 중 모드 변경 시 재구독
    private PixelMode _prevMode;

    private void Update()
    {
      if (_mode != _prevMode)
      {
        Unsubscribe(_prevMode);
        ApplyRunnerActive(_mode);
        Subscribe(_mode);
        SetupOverlayAnchors(_mode);
        ResetSmoothing();
        _prevMode = _mode;

        // 이전 모드 오버레이 초기화
        if (_pixelOverlay != null)
        {
          _pixelOverlay.material = null;
          _pixelOverlay.gameObject.SetActive(false);
        }
      }

      switch (_mode)
      {
        case PixelMode.ObjectDetection: UpdateObjectDetection(); break;
        case PixelMode.Segmentation:    UpdateSegmentation();    break;
      }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  구독 관리
    // ═════════════════════════════════════════════════════════════════════

    private void Subscribe(PixelMode mode)
    {
      if (mode == PixelMode.ObjectDetection && _detectorRunner != null)
        _detectorRunner.OnPersonDetected += HandleDetection;
      if (mode == PixelMode.Segmentation && _segmentRunner != null)
        _segmentRunner.OnMaskReady += HandleMaskReady;
    }

    private void Unsubscribe(PixelMode mode)
    {
      if (_detectorRunner != null)
        _detectorRunner.OnPersonDetected -= HandleDetection;
      if (_segmentRunner != null)
        _segmentRunner.OnMaskReady -= HandleMaskReady;
    }

    private void SetupOverlayAnchors(PixelMode mode)
    {
      if (_pixelOverlay == null) return;
      var rt = _pixelOverlay.rectTransform;

      if (mode == PixelMode.Segmentation)
      {
        // 세그멘테이션: 화면 전체 덮음
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
      }
      // ObjectDetection: 앵커는 Update에서 박스에 맞게 설정
    }

    private void ApplyRunnerActive(PixelMode mode)
    {
      if (_detectorRunner != null)
        _detectorRunner.enabled = (mode == PixelMode.ObjectDetection);
      if (_segmentRunner != null)
        _segmentRunner.enabled = (mode == PixelMode.Segmentation);
    }

    private void ResetSmoothing()
    {
      _smoothingInitialized = false;
      _currentDetections = null;
      _hasPending = false;
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Object Detection 모드
    // ═════════════════════════════════════════════════════════════════════

    private void HandleDetection(PersonDetectionData[] detections)
    {
      _currentDetections = detections;
      if (detections == null || detections.Length == 0) return;

      var d = detections[0];
      UpdateSmoothing(d.normLeft, d.normTop, d.normRight, d.normBottom);
    }

    private void UpdateObjectDetection()
    {
      if (_pixelOverlay == null) return;

      if (_currentDetections == null || _currentDetections.Length == 0 || !_smoothingInitialized)
      {
        _pixelOverlay.gameObject.SetActive(false);
        return;
      }

      var srcImage = _cameraDisplay.GetComponent<RawImage>();
      if (srcImage == null || srcImage.texture == null)
      {
        _pixelOverlay.gameObject.SetActive(false);
        return;
      }

      float pixelSize = CalcPixelSize(_sBottom - _sTop);

      // 오버레이를 스무딩된 박스에 맞게 앵커 설정
      var rt = _pixelOverlay.rectTransform;
      rt.anchorMin = new Vector2(_sLeft,  1f - _sBottom);
      rt.anchorMax = new Vector2(_sRight, 1f - _sTop);
      rt.offsetMin = Vector2.zero;
      rt.offsetMax = Vector2.zero;

      // 박스 크기로 RT 생성 → 픽셀화
      var rect = _cameraDisplay.rect;
      int rtW = Mathf.Max(1, Mathf.RoundToInt((_sRight - _sLeft) * rect.width  / pixelSize));
      int rtH = Mathf.Max(1, Mathf.RoundToInt((_sBottom - _sTop) * rect.height / pixelSize));

      EnsurePixelRT(rtW, rtH);

      Graphics.Blit(
        srcImage.texture, _pixelRT,
        new Vector2(_sRight - _sLeft, _sBottom - _sTop),
        new Vector2(_sLeft, 1f - _sBottom)
      );

      _pixelOverlay.material = null;
      _pixelOverlay.texture  = _pixelRT;
      _pixelOverlay.gameObject.SetActive(true);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  Segmentation 모드
    // ═════════════════════════════════════════════════════════════════════

    private void HandleMaskReady(float[] data, int w, int h,
                                  float normL, float normT, float normR, float normB)
    {
      _pendingMask = data;
      _pendingW    = w;
      _pendingH    = h;
      _hasPending  = true;
      UpdateSmoothing(normL, normT, normR, normB);
    }

    private void UpdateSegmentation()
    {
      if (_pixelOverlay == null || _material == null) return;

      // 한 번도 마스크를 받지 못한 경우에만 숨김
      if (!_smoothingInitialized)
      {
        _pixelOverlay.gameObject.SetActive(false);
        return;
      }

      var srcImage = _cameraDisplay.GetComponent<RawImage>();
      if (srcImage == null || srcImage.texture == null)
      {
        _pixelOverlay.gameObject.SetActive(false);
        return;
      }

      // 새 마스크가 도착했을 때만 텍스처 갱신 (없으면 이전 프레임 유지)
      if (_hasPending)
      {
        EnsureMaskTexture(_pendingW, _pendingH);
        _maskTexture.SetPixelData(_pendingMask, 0);
        _maskTexture.Apply(false);
        _hasPending = false;

        _material.SetTexture("_MaskTex", _maskTexture);
        _pixelOverlay.texture  = srcImage.texture;
        _pixelOverlay.uvRect   = srcImage.uvRect;
        _pixelOverlay.material = _material;
      }

      // 픽셀 크기·임계값은 매 프레임 갱신 (스무딩 반영)
      _material.SetFloat("_PixelSize", CalcPixelSize(_sBottom - _sTop));
      _material.SetFloat("_Threshold", _maskThreshold);

      _pixelOverlay.gameObject.SetActive(true);
    }

    // ═════════════════════════════════════════════════════════════════════
    //  OnGUI 디버그 (Object Detection 모드 전용)
    // ═════════════════════════════════════════════════════════════════════

    private void OnGUI()
    {
      if (_mode != PixelMode.ObjectDetection) return;
      if (!_smoothingInitialized || _cameraDisplay == null) return;

      var corners = new Vector3[4];
      _cameraDisplay.GetWorldCorners(corners);
      float dispX = corners[0].x;
      float dispY = Screen.height - corners[2].y;
      float dispW = corners[2].x - corners[0].x;
      float dispH = corners[2].y - corners[0].y;

      var tex  = GetDebugTex();
      var prev = GUI.color;

      // 마스킹
      if (_maskOutside)
      {
        float ux = dispX + _sLeft  * dispW, uy = dispY + _sTop  * dispH;
        float uw = (_sRight - _sLeft) * dispW, uh = (_sBottom - _sTop) * dispH;
        GUI.color = _maskColor;
        GUI.DrawTexture(new Rect(dispX,   dispY,    dispW,                   uy - dispY),             tex);
        GUI.DrawTexture(new Rect(dispX,   uy + uh,  dispW,                   dispH - (uy-dispY) - uh), tex);
        GUI.DrawTexture(new Rect(dispX,   uy,       ux - dispX,              uh),                     tex);
        GUI.DrawTexture(new Rect(ux + uw, uy,       dispX + dispW - (ux+uw), uh),                     tex);
        GUI.color = prev;
      }

      // 박스 + 레이블
      if (_showDebugBox)
      {
        float bx = dispX + _sLeft  * dispW, by = dispY + _sTop  * dispH;
        float bw = (_sRight - _sLeft) * dispW, bh = (_sBottom - _sTop) * dispH;
        float px = CalcPixelSize(_sBottom - _sTop);

        GUI.color = _debugColor;
        GUI.DrawTexture(new Rect(bx, by, bw, bh), tex);
        GUI.color = prev;

        var style = new GUIStyle(GUI.skin.label)
        {
          normal = { textColor = Color.yellow }, fontSize = 14, fontStyle = FontStyle.Bold
        };
        GUI.Label(new Rect(bx, by - 22f, 280f, 22f),
          $"px:{px:F0}  {DistanceLabel(_sBottom - _sTop)}", style);
      }
    }

    // ═════════════════════════════════════════════════════════════════════
    //  유틸리티
    // ═════════════════════════════════════════════════════════════════════

    private void UpdateSmoothing(float l, float t, float r, float b)
    {
      if (!_smoothingInitialized)
      {
        _sLeft = l; _sTop = t; _sRight = r; _sBottom = b;
        _smoothingInitialized = true;
        return;
      }
      float s = _smoothing;
      _sLeft   = _sLeft   * s + l * (1f - s);
      _sTop    = _sTop    * s + t * (1f - s);
      _sRight  = _sRight  * s + r * (1f - s);
      _sBottom = _sBottom * s + b * (1f - s);
    }

    public float CalcPixelSize(float boxHeightRatio)
    {
      if (_manualPixelSize) return _manualPixelSizeValue;
      float t = Mathf.Clamp01(boxHeightRatio / _nearThreshold);
      return Mathf.Lerp(_pixelSizeAtFar, _pixelSizeAtNear, t);
    }

    private string DistanceLabel(float ratio)
    {
      float t = Mathf.Clamp01(ratio / _nearThreshold);
      if (t > 0.7f) return "가까움";
      if (t > 0.3f) return "중간";
      return "멂";
    }

    // Object Detection 모드용 픽셀 RT
    private RenderTexture _pixelRT;

    private void EnsurePixelRT(int w, int h)
    {
      if (_pixelRT != null && _pixelRT.width == w && _pixelRT.height == h) return;
      if (_pixelRT != null) _pixelRT.Release();
      _pixelRT = new RenderTexture(w, h, 0, RenderTextureFormat.Default)
      {
        filterMode = FilterMode.Point
      };
      _pixelRT.Create();
    }

    private void EnsureMaskTexture(int w, int h)
    {
      if (_maskTexture != null && _maskTexture.width == w && _maskTexture.height == h) return;
      if (_maskTexture != null) Destroy(_maskTexture);
      _maskTexture = new Texture2D(w, h, TextureFormat.RFloat, false)
      {
        filterMode = FilterMode.Bilinear,
        wrapMode   = TextureWrapMode.Clamp
      };
    }

    private Texture2D GetDebugTex()
    {
      if (_debugTexture == null)
      {
        _debugTexture = new Texture2D(1, 1);
        _debugTexture.SetPixel(0, 0, Color.white);
        _debugTexture.Apply();
      }
      return _debugTexture;
    }
  }
}
