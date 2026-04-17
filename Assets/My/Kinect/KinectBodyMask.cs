// KinectBodyMask.cs
// MultiSourceFrameReader(Color + Depth + BodyIndex) 단일 리더.
// 마스크에 바디 인덱스를 인코딩하여 사람마다 개별 픽셀 크기를 적용합니다.
// 감지된 사람 머리 위에 거리(m) / 픽셀 크기 레이블을 표시합니다.

using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Windows.Kinect;

namespace PixelMan
{
  public class KinectBodyMask : MonoBehaviour
  {
    [Header("참조")]
    [SerializeField] private RawImage _cameraDisplay;
    [SerializeField] private RawImage _pixelOverlay;
    [SerializeField] private Shader   _pixelateShader;

    [Header("사람 감지")]
    [SerializeField, Range(1, 6)] private int _maxUsers = 3;

    [Header("픽셀 크기 - 수동")]
    [SerializeField] private bool _useManualPixelSize = false;
    [SerializeField, Range(1f, 128f)] private float _manualPixelSize = 8f;

    [Header("픽셀 크기 - 거리 자동")]
    [Tooltip("가까울 때(nearDepth 이하) 픽셀 크기")]
    [SerializeField, Range(1f, 128f)] private float _pixelSizeNear = 48f;
    [Tooltip("멀 때(farDepth 이상) 픽셀 크기")]
    [SerializeField, Range(1f, 128f)] private float _pixelSizeFar  = 4f;
    [Tooltip("가깝다고 판단하는 거리 (mm)")]
    [SerializeField] private float _nearDepthMm = 1000f;
    [Tooltip("멀다고 판단하는 거리 (mm)")]
    [SerializeField] private float _farDepthMm  = 2500f;

    [Header("레이블")]
    [SerializeField] private bool  _showLabels  = true;
    [SerializeField] private int   _labelFontSize = 28;
    [SerializeField] private Color _labelColor  = Color.yellow;

    // ── Kinect ──────────────────────────────────────────────────────────────
    private KinectSensor           _sensor;
    private MultiSourceFrameReader _reader;
    private CoordinateMapper       _mapper;

    private const int ColorW = 1920;
    private const int ColorH = 1080;
    private const int DepthW = 512;
    private const int DepthH = 424;

    private byte[]            _colorData;
    private ushort[]          _depthWork;
    private byte[]            _bodyIndexWork;
    private ColorSpacePoint[] _depthToColor;

    private byte[]        _maskOutput;
    private volatile bool _maskReady;
    private Task          _maskTask;

    // 바디별 결과 (_maskReady = true 이전에 모두 기록)
    private readonly float[] _perBodyDepthMm   = new float[6];
    private readonly int[]   _perBodyHeadCx    = new int[6];  // 원본 컬러 좌표 (Y 미반전)
    private readonly int[]   _perBodyHeadCy    = new int[6];
    private readonly bool[]  _perBodyActive    = new bool[6];

    private readonly float[] _pixelSizesBuffer = new float[6];

    // 마스크 인코딩: body b → (b+1)*42
    private static byte BodyIndexToByte(int b) => (byte)((b + 1) * 42);

    // ── 텍스처 / 머티리얼 ────────────────────────────────────────────────────
    private Texture2D _colorTexture;
    private Texture2D _maskTexture;
    private Material  _material;

    private bool _initialized;
    private bool _shaderApplied;

    // ── 레이블 ───────────────────────────────────────────────────────────────
    private readonly Text[]          _labelTexts = new Text[6];
    private readonly RectTransform[] _labelRts   = new RectTransform[6];

    // ═══════════════════════════════════════════════════════════════════════
    //  라이프사이클
    // ═══════════════════════════════════════════════════════════════════════

    private void Start()
    {
      if (!ValidateReferences()) return;

      _sensor = KinectSensor.GetDefault();
      if (_sensor == null)
      {
        Debug.LogError("[KinectBodyMask] Kinect 센서를 찾을 수 없습니다.");
        enabled = false;
        return;
      }

      _mapper = _sensor.CoordinateMapper;
      _reader = _sensor.OpenMultiSourceFrameReader(
        FrameSourceTypes.Color | FrameSourceTypes.Depth | FrameSourceTypes.BodyIndex);

      _colorData     = new byte[ColorW * ColorH * 4];
      _depthWork     = new ushort[DepthW * DepthH];
      _bodyIndexWork = new byte[DepthW * DepthH];
      _depthToColor  = new ColorSpacePoint[DepthW * DepthH];
      _maskOutput    = new byte[ColorW * ColorH];

      for (int b = 0; b < 6; b++) _perBodyDepthMm[b] = -1f;

      _colorTexture = new Texture2D(ColorW, ColorH, TextureFormat.RGBA32, false);
      _maskTexture  = new Texture2D(ColorW, ColorH, TextureFormat.R8, false)
      {
        filterMode = FilterMode.Point,
        wrapMode   = TextureWrapMode.Clamp
      };

      _material = new Material(_pixelateShader);

      _cameraDisplay.texture = _colorTexture;
      _cameraDisplay.uvRect  = new Rect(0f, 1f, 1f, -1f);

      SetupOverlay();
      CreateLabels();

      if (!_sensor.IsOpen)
        _sensor.Open();

      _initialized = true;
      Debug.Log("[KinectBodyMask] 초기화 완료");
    }

    private void Update()
    {
      if (!_initialized) return;

      // ── 1. 마스크 GPU 업로드 + 픽셀 크기 / 레이블 갱신 ──────────────────
      if (_maskReady)
      {
        _maskTexture.LoadRawTextureData(_maskOutput);
        _maskTexture.Apply(false);
        _maskReady = false;

        UpdatePixelSizes();
        UpdateLabels();

        if (!_shaderApplied) ApplyShader();
      }

      // ── 2. 수동 모드: 슬라이더 즉시 반영 ────────────────────────────────
      if (_shaderApplied && _useManualPixelSize)
      {
        FillPixelSizesBuffer(_manualPixelSize);
        if (_material != null)
          _material.SetFloatArray("_PixelSizes", _pixelSizesBuffer);
      }

      // ── 3. 프레임 수신 ────────────────────────────────────────────────────
      var frame = _reader.AcquireLatestFrame();
      if (frame == null) return;

      bool gotDepth = false;
      using (var colorFrame     = frame.ColorFrameReference.AcquireFrame())
      using (var depthFrame     = frame.DepthFrameReference.AcquireFrame())
      using (var bodyIndexFrame = frame.BodyIndexFrameReference.AcquireFrame())
      {
        if (colorFrame != null)
          colorFrame.CopyConvertedFrameDataToArray(_colorData, ColorImageFormat.Rgba);

        if (depthFrame != null && bodyIndexFrame != null
            && (_maskTask == null || _maskTask.IsCompleted))
        {
          depthFrame.CopyFrameDataToArray(_depthWork);
          bodyIndexFrame.CopyFrameDataToArray(_bodyIndexWork);
          gotDepth = true;
        }
      }

      _colorTexture.LoadRawTextureData(_colorData);
      _colorTexture.Apply(false);

      if (gotDepth)
        _maskTask = Task.Run(BuildMaskBackground);
    }

    /// <summary>
    /// 컴포넌트 파괴 시 할당된 메모리와 하드웨어 자원을 해제합니다.
    /// 하드웨어 센서를 명시적으로 닫지 않으면 다음 실행 시 기기 인식 오류나 멈춤 현상이 발생할 수 있습니다.
    /// </summary>
    private void OnDestroy()
    {
      // 백그라운드 스레드 강제 종료 방지 및 안전한 대기
      _maskTask?.Wait();

      // 네이티브 리더 자원 반환
      if (_reader != null)
      {
        _reader.Dispose();
        _reader = null;
      }

      // 하드웨어 센서 연결 해제. 기기 오작동 방지.
      if (_sensor != null)
      {
        if (_sensor.IsOpen)
        {
          _sensor.Close();
        }
        _sensor = null;
      }

      // 유니티 오브젝트 메모리 누수 방지
      if (_material) Destroy(_material);
      if (_colorTexture) Destroy(_colorTexture);
      if (_maskTexture) Destroy(_maskTexture);

      // 동적 생성된 UI 요소 파괴
      for (int b = 0; b < 6; b++)
      {
        if (_labelTexts[b]) Destroy(_labelTexts[b].gameObject);
      }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  마스크 + 깊이 + 머리 위치 계산 (백그라운드 스레드)
    // ═══════════════════════════════════════════════════════════════════════

    private void BuildMaskBackground()
    {
      // 활성 바디 수집
      bool[] active = new bool[6];
      int    count  = 0;
      for (int i = 0; i < DepthW * DepthH; i++)
      {
        byte idx = _bodyIndexWork[i];
        if (idx < 6 && !active[idx] && count < _maxUsers)
        {
          active[idx] = true;
          count++;
        }
      }

      Array.Clear(_maskOutput, 0, _maskOutput.Length);

      if (count == 0)
      {
        for (int b = 0; b < 6; b++) _perBodyDepthMm[b] = -1f;
        for (int b = 0; b < 6; b++) _perBodyActive[b]  = false;
        _maskReady = true;
        return;
      }

      _mapper.MapDepthFrameToColorSpace(_depthWork, _depthToColor);

      long[] depthSum   = new long[6];
      int[]  depthCount = new int[6];
      int[]  headCy     = new int[6];   // 원본 컬러 Y (0=상단), 최솟값이 머리
      int[]  headCx     = new int[6];
      for (int b = 0; b < 6; b++) headCy[b] = int.MaxValue;

      for (int i = 0; i < DepthW * DepthH; i++)
      {
        byte bodyIdx = _bodyIndexWork[i];
        if (bodyIdx >= 6 || !active[bodyIdx]) continue;

        ushort d = _depthWork[i];
        if (d > 0) { depthSum[bodyIdx] += d; depthCount[bodyIdx]++; }

        var cp = _depthToColor[i];
        int cx = (int)(cp.X + 0.5f);
        int cy = (int)(cp.Y + 0.5f);
        if ((uint)cx >= ColorW || (uint)cy >= ColorH) continue;

        // 머리 위치: 원본 좌표계에서 Y 최솟값 (상단)
        if (cy < headCy[bodyIdx]) { headCy[bodyIdx] = cy; headCx[bodyIdx] = cx; }

        int  flippedCy = ColorH - 1 - cy;
        byte maskByte  = BodyIndexToByte(bodyIdx);

        for (int dy = -1; dy <= 1; dy++)
        {
          int py = flippedCy + dy;
          if ((uint)py >= ColorH) continue;
          for (int dx = -1; dx <= 1; dx++)
          {
            int px = cx + dx;
            if ((uint)px >= ColorW) continue;
            _maskOutput[py * ColorW + px] = maskByte;
          }
        }
      }

      // 결과 저장 (_maskReady 쓰기 전에 완료)
      for (int b = 0; b < 6; b++)
      {
        _perBodyDepthMm[b]  = depthCount[b] > 0 ? (float)depthSum[b] / depthCount[b] : -1f;
        _perBodyHeadCx[b]   = headCx[b];
        _perBodyHeadCy[b]   = headCy[b] != int.MaxValue ? headCy[b] : 0;
        _perBodyActive[b]   = active[b];
      }

      _maskReady = true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  픽셀 크기
    // ═══════════════════════════════════════════════════════════════════════

    private void UpdatePixelSizes()
    {
      if (_material == null) return;

      if (_useManualPixelSize)
        FillPixelSizesBuffer(_manualPixelSize);
      else
        for (int b = 0; b < 6; b++)
          _pixelSizesBuffer[b] = DepthToPixelSize(_perBodyDepthMm[b]);

      _material.SetFloatArray("_PixelSizes", _pixelSizesBuffer);
    }

    private void FillPixelSizesBuffer(float value)
    {
      for (int b = 0; b < 6; b++) _pixelSizesBuffer[b] = value;
    }

    private float DepthToPixelSize(float depthMm)
    {
      if (depthMm <= 0f) return _pixelSizeFar;
      float t = Mathf.InverseLerp(_nearDepthMm, _farDepthMm, depthMm);
      return Mathf.Lerp(_pixelSizeNear, _pixelSizeFar, t);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  레이블
    // ═══════════════════════════════════════════════════════════════════════

    private void CreateLabels()
    {
      var parent = _cameraDisplay.transform.parent as RectTransform;
      if (parent == null) return;

      Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

      for (int b = 0; b < 6; b++)
      {
        var go = new GameObject($"BodyLabel_{b}");
        go.transform.SetParent(parent, false);

        var rt = go.AddComponent<RectTransform>();
        // anchor = 정규화 좌표로 직접 위치 지정, pivot 하단 중심
        rt.pivot     = new Vector2(0.5f, 0f);
        rt.sizeDelta = new Vector2(240f, 50f);

        var txt = go.AddComponent<Text>();
        txt.font          = font;
        txt.fontSize      = _labelFontSize;
        txt.color         = _labelColor;
        txt.alignment     = TextAnchor.MiddleCenter;
        txt.raycastTarget = false;

        var outline = go.AddComponent<Outline>();
        outline.effectColor    = Color.black;
        outline.effectDistance = new Vector2(2f, -2f);

        go.SetActive(false);
        _labelTexts[b] = txt;
        _labelRts[b]   = rt;
      }
    }

    private void UpdateLabels()
    {
      if (!_showLabels) { HideAllLabels(); return; }

      for (int b = 0; b < 6; b++)
      {
        if (_labelTexts[b] == null) continue;

        if (!_perBodyActive[b])
        {
          _labelTexts[b].gameObject.SetActive(false);
          continue;
        }

        float depthM  = _perBodyDepthMm[b] > 0f ? _perBodyDepthMm[b] / 1000f : 0f;
        float pixSize = _pixelSizesBuffer[b];
        _labelTexts[b].text = $"{depthM:F1}m  /  {(int)pixSize}px";

        // 컬러 좌표 → 정규화 UV
        // 컬러 Y=0 이 화면 상단 → Unity UI v=1 이 상단이므로 반전
        float u = (float)_perBodyHeadCx[b] / ColorW;
        float v = 1f - (float)_perBodyHeadCy[b] / ColorH;

        // anchorMin = anchorMax = (u, v) 로 설정하면 부모 크기에 무관하게 정규화 위치에 배치
        // pivot=(0.5, 0) 이므로 레이블 하단이 해당 좌표에 오고, anchoredPosition.y 로 머리 위 오프셋
        _labelRts[b].anchorMin        = new Vector2(u, v);
        _labelRts[b].anchorMax        = new Vector2(u, v);
        _labelRts[b].anchoredPosition = new Vector2(0f, 12f); // 머리 위 12px

        _labelTexts[b].gameObject.SetActive(true);
      }
    }

    private void HideAllLabels()
    {
      for (int b = 0; b < 6; b++)
        if (_labelTexts[b] != null) _labelTexts[b].gameObject.SetActive(false);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  셰이더 / 오버레이
    // ═══════════════════════════════════════════════════════════════════════

    private void ApplyShader()
    {
      _material.SetTexture("_MaskTex",           _maskTexture);
      _material.SetFloat("_Threshold",           0.05f);
      _material.SetFloat("_FlipMaskX",           0f);
      _material.SetFloat("_UsePerBodyPixelSize", 1f);
      UpdatePixelSizes();

      _pixelOverlay.texture  = _colorTexture;
      _pixelOverlay.uvRect   = _cameraDisplay.uvRect;
      _pixelOverlay.material = _material;
      _pixelOverlay.gameObject.SetActive(true);
      _shaderApplied = true;
    }

    private void SetupOverlay()
    {
      var rt = _pixelOverlay.rectTransform;
      rt.anchorMin = Vector2.zero;
      rt.anchorMax = Vector2.one;
      rt.offsetMin = Vector2.zero;
      rt.offsetMax = Vector2.zero;
      _pixelOverlay.gameObject.SetActive(false);
    }

    private bool ValidateReferences()
    {
      if (_cameraDisplay  == null) { Debug.LogError("[KinectBodyMask] Camera Display 미연결");  enabled = false; return false; }
      if (_pixelOverlay   == null) { Debug.LogError("[KinectBodyMask] Pixel Overlay 미연결");   enabled = false; return false; }
      if (_pixelateShader == null) { Debug.LogError("[KinectBodyMask] Pixelate Shader 미연결"); enabled = false; return false; }
      return true;
    }
  }
}
