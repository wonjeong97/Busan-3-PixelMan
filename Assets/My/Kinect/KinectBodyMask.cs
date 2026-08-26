using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using Windows.Kinect;
using Wonjeong.Utils;

namespace PixelMan
{
    public class KinectBodyMask : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RawImage _cameraDisplay;
        [SerializeField] private RawImage _pixelOverlay;
        [SerializeField] private Shader _pixelateShader;

        [Header("Detection")]
        [SerializeField, Range(1, 3)] private int _maxUsers = 3;

        [Header("Distance Pixel Settings")]
        [SerializeField] private float _pixelSizeNear;
        [SerializeField] private float _pixelSizeFar;
        [SerializeField] private float _nearDepthMm;
        [SerializeField] private float _farDepthMm;

        [Header("Erosion Settings")]
        [SerializeField] private float _erosionSize;

        [Header("Manual Pixel Size")]
        [SerializeField] private bool _useManualPixelSize;
        [SerializeField, Range(1f, 128f)] private float _manualPixelSize;

        [Header("Cover Image")]
        [Tooltip("커버 이미지의 CanvasGroup 컴포넌트")]
        [SerializeField] private CanvasGroup _coverCanvasGroup;
        [Tooltip("사람이 사라진 후 커버 이미지를 다시 표시할 때까지 대기 시간 (초)")]
        [SerializeField] private float _coverHideDelay = 5f;
        [Tooltip("페이드 인/아웃 시간 (초)")]
        [SerializeField] private float _coverFadeDuration = 0.5f;

        [Header("Label Styling")]
        [SerializeField] private bool  _showLabels;
        [SerializeField] private int   _labelFontSize;
        [SerializeField] private Color _labelColor;
        [Tooltip("라벨 위치/숫자 스무딩 (0=고정, 1=즉시반응)")]
        [SerializeField, Range(0.01f, 1f)] private float _labelSmoothing = 0.12f;
        
        [Header("Watchdog Settings")]
        [Tooltip("프레임 수신 타임아웃(초). 이 시간 동안 데이터가 없으면 센서 강제 재시작 (좀비 현상 방지)")]
        [SerializeField] private float _sensorTimeout = 8f;

        private float _lastFrameTime;
        private bool  _isRestarting;

        private KinectSensor _sensor;
        private MultiSourceFrameReader _reader;
        private CoordinateMapper _mapper;

        private const int ColorW = 1920;
        private const int ColorH = 1080;
        private const int DepthW = 512;
        private const int DepthH = 424;

        // 세로형 디스플레이(1080x1920) 출력 비율. 키넥트 컬러 스트림(16:9, 가로)에서
        // 이 비율에 맞는 중앙 영역만 잘라서 보여주기 위해 사용합니다.
        private const float TargetAspect = 9f / 16f;

        private byte[] _colorData;
        private ushort[] _depthWork;
        private byte[] _bodyIndexWork;
        private ColorSpacePoint[] _depthToColor;

        private byte[] _maskOutput;
        private volatile bool _maskReady;
        private Task _maskTask;

        private readonly float[] _perBodyDepthMm = new float[6];
        private readonly int[] _perBodyHeadCx = new int[6];
        private readonly int[] _perBodyHeadCy = new int[6];
        private readonly bool[] _perBodyActive = new bool[6];

        private readonly float[] _pixelSizesBuffer  = new float[6];
        private readonly int[]   _perBodyBlockCount = new int[6];

        private Texture2D _colorTexture;
        private Texture2D _maskTexture;
        private Material  _material;

        private bool  _initialized;

        // 커버 이미지 제어
        private Coroutine _coverFadeCoroutine;
        private float       _lastDetectedTime;
        private bool        _coverVisible = true;
        private bool _shaderApplied;

        private readonly Text[]          _labelTexts   = new Text[6];
        private readonly RectTransform[] _labelRts     = new RectTransform[6];

        // 라벨 EMA 스무딩
        private readonly float[] _smoothHeadCx    = new float[6];
        private readonly float[] _smoothBlockCount = new float[6];
        private readonly bool[]  _smoothInit       = new bool[6];

        /// <summary>
        /// 바디 인덱스를 쉐이더 마스크용 바이트 값으로 인코딩합니다.
        /// 사람마다 고유한 픽셀 크기를 적용하기 위한 식별값 생성 목적입니다.
        /// 예시: b=0 -> 42, b=1 -> 84
        /// </summary>
        private static byte BodyIndexToByte(int b)
        {
            return (byte)((b + 1) * 42);
        }

        /// <summary>
        /// 컴포넌트 초기화 및 하드웨어 센서를 연결합니다.
        /// 런타임 시작 시 메모리를 사전 할당하여 프레임 드랍을 방지합니다.
        /// </summary>
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
            _reader = _sensor.OpenMultiSourceFrameReader(FrameSourceTypes.Color | FrameSourceTypes.Depth | FrameSourceTypes.BodyIndex);

            _colorData = new byte[ColorW * ColorH * 4];
            _depthWork = new ushort[DepthW * DepthH];
            _bodyIndexWork = new byte[DepthW * DepthH];
            _depthToColor = new ColorSpacePoint[DepthW * DepthH];
            _maskOutput = new byte[ColorW * ColorH];

            for (int b = 0; b < 6; b++)
            {
                _perBodyDepthMm[b] = -1f;
            }

            _colorTexture = new Texture2D(ColorW, ColorH, TextureFormat.RGBA32, false);
            _maskTexture = new Texture2D(ColorW, ColorH, TextureFormat.R8, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };

            _material = new Material(_pixelateShader);

            _cameraDisplay.texture = _colorTexture;
            _cameraDisplay.uvRect = ComputeCroppedUvRect();

            SetupOverlay();
            CreateLabels();

            if (_coverCanvasGroup != null)
                _coverCanvasGroup.alpha = 1f; // 시작 시 완전히 표시

            if (!_sensor.IsOpen)
            {
                _sensor.Open();
            }

            _initialized = true;
            _lastFrameTime = Time.time;
            Debug.Log("[KinectBodyMask] 초기화 완료");
        }

        /// <summary>
        /// 매 프레임 센서 데이터를 수신하고 UI를 갱신합니다.
        /// 시각적 피드백 동기화 유지 및 백그라운드 스레드 작업을 트리거합니다.
        /// </summary>
        private void Update()
        {
            if (!_initialized) return;

            if (_maskReady)
            {
                _maskTexture.LoadRawTextureData(_maskOutput);
                _maskTexture.Apply(false);
                _maskReady = false;

                UpdatePixelSizes();
                UpdateLabels();
                UpdateCover();

                if (!_shaderApplied)
                {
                    ApplyShader();
                }
            }

            if (_shaderApplied && _useManualPixelSize)
            {
                FillPixelSizesBuffer(_manualPixelSize);
                if (_material)
                {
                    _material.SetFloatArray("_PixelSizes", _pixelSizesBuffer);
                }
            }
            
            if (_isRestarting) return;
            
            MultiSourceFrame frame = _reader.AcquireLatestFrame();
            if (frame == null) 
            {
                if (Time.time - _lastFrameTime > _sensorTimeout)
                {
                    StartCoroutine(RestartSensorRoutine());
                }
                return;
            }
            
            _lastFrameTime = Time.time;
            bool gotDepth = false;
            
            using (ColorFrame colorFrame = frame.ColorFrameReference.AcquireFrame())
            using (DepthFrame depthFrame = frame.DepthFrameReference.AcquireFrame())
            using (BodyIndexFrame bodyIndexFrame = frame.BodyIndexFrameReference.AcquireFrame())
            {
                if (colorFrame != null)
                {
                    colorFrame.CopyConvertedFrameDataToArray(_colorData, ColorImageFormat.Rgba);
                }

                if (depthFrame != null && bodyIndexFrame != null && (_maskTask == null || _maskTask.IsCompleted))
                {
                    depthFrame.CopyFrameDataToArray(_depthWork);
                    bodyIndexFrame.CopyFrameDataToArray(_bodyIndexWork);
                    gotDepth = true;
                }
            }

            _colorTexture.LoadRawTextureData(_colorData);
            _colorTexture.Apply(false);

            if (gotDepth)
            {
                _maskTask = Task.Run(BuildMaskBackground);
            }
        }

        /// <summary>
        /// 컴포넌트 비활성화 시 네이티브 센서를 우선적으로 닫습니다.
        /// 에디터에서 플레이 모드를 정지할 때 센서가 닫히지 않아 다음 실행 시 두 번 플레이해야 켜지는 고질적인 버그를 완화합니다.
        /// </summary>
        private void OnDisable()
        {
            CloseKinectSensor();
        }

        /// <summary>
        /// 프로그램 강제 종료 시 네이티브 센서의 락(Lock)을 해제합니다.
        /// </summary>
        private void OnApplicationQuit()
        {
            CloseKinectSensor();
        }

        /// <summary>
        /// 유니티 오브젝트 파괴 시 메모리를 해제합니다.
        /// </summary>
        private void OnDestroy()
        {
            CloseKinectSensor();

            if (_material) Destroy(_material);
            if (_colorTexture) Destroy(_colorTexture);
            if (_maskTexture) Destroy(_maskTexture);

            for (int b = 0; b < 6; b++)
            {
                if (_labelTexts[b]) Destroy(_labelTexts[b].gameObject);
            }
        }
        
        /// <summary>
        /// 키넥트 응답이 없을 때 하드웨어를 강제로 재연결합니다.
        /// PC 부팅 직후 서비스와 유니티 간의 초기화 타이밍이 어긋나 발생하는 좀비 상태를 복구하기 위함입니다.
        /// </summary>
        private IEnumerator RestartSensorRoutine()
        {
            Debug.LogWarning("[KinectBodyMask] 센서 응답 없음. 하드웨어 재연결을 시도합니다...");
            _isRestarting = true;

            // 기존 하드웨어 락 해제
            CloseKinectSensor();

            // 윈도우 키넥트 서비스가 포트를 완전히 놓아줄 때까지 대기
            yield return CoroutineData.GetWaitForSeconds(3f);

            _sensor = KinectSensor.GetDefault();
            if (_sensor != null)
            {
                _mapper = _sensor.CoordinateMapper;
                _reader = _sensor.OpenMultiSourceFrameReader(FrameSourceTypes.Color | FrameSourceTypes.Depth | FrameSourceTypes.BodyIndex);
                
                if (!_sensor.IsOpen)
                {
                    _sensor.Open();
                }

                Debug.Log("[KinectBodyMask] 센서 자동 재연결 완료");
            }
            else
            {
                Debug.LogError("[KinectBodyMask] 재연결 실패: 센서를 찾을 수 없습니다.");
            }

            _lastFrameTime = Time.time;
            _isRestarting = false;
        }

        /// <summary>
        /// 키넥트 백그라운드 스레드 및 네이티브 하드웨어 연결을 안전하게 해제합니다.
        /// 다중 호출 시 중복 해제를 방지하기 위해 Null 참조를 명시적으로 검사합니다.
        /// </summary>
        private void CloseKinectSensor()
        {
            _maskTask?.Wait();

            if (_reader != null)
            {
                _reader.Dispose();
                _reader = null;
            }

            if (_sensor != null)
            {
                if (_sensor.IsOpen)
                {
                    _sensor.Close();
                }
                _sensor = null;
            }
        }

        /// <summary>
        /// 백그라운드 스레드에서 깊이 데이터 및 마스크 텍스처를 계산합니다.
        /// 메인 스레드 부하를 줄여 프레임 드랍을 방지합니다.
        /// </summary>
        private void BuildMaskBackground()
        {
            _mapper.MapDepthFrameToColorSpace(_depthWork, _depthToColor);

            // 1차 스캔: 활성 바디를 결정하기 전에 프레임에 나타난 모든 바디의 평균 깊이/머리 위치를 먼저 계산합니다.
            // (거리 기준으로 _maxUsers 명을 고르려면 마스크를 그리기 전에 깊이 값이 필요합니다.)
            bool[] present = new bool[6];
            long[] depthSum = new long[6];
            int[] depthCount = new int[6];
            int[] headCy = new int[6];
            int[] headCx = new int[6];

            for (int b = 0; b < 6; b++)
            {
                headCy[b] = int.MaxValue;
            }

            for (int i = 0; i < DepthW * DepthH; i++)
            {
                byte bodyIdx = _bodyIndexWork[i];
                if (bodyIdx >= 6) continue;

                present[bodyIdx] = true;

                ushort d = _depthWork[i];
                if (d > 0)
                {
                    depthSum[bodyIdx] += d;
                    depthCount[bodyIdx]++;
                }

                ColorSpacePoint cp = _depthToColor[i];
                int cx = (int)(cp.X + 0.5f);
                int cy = (int)(cp.Y + 0.5f);

                if ((uint)cx >= ColorW || (uint)cy >= ColorH) continue;

                if (cy < headCy[bodyIdx])
                {
                    headCy[bodyIdx] = cy;
                    headCx[bodyIdx] = cx;
                }
            }

            // 감지된 사람 중 카메라와 가장 가까운 _maxUsers 명만 활성화 (여러 명이 잡혀도 항상 가장 가까운 사람을 추적)
            bool[] active = SelectClosestBodies(present, depthSum, depthCount);

            Array.Clear(_maskOutput, 0, _maskOutput.Length);

            bool anyActive = false;
            for (int b = 0; b < 6; b++)
            {
                if (active[b]) { anyActive = true; break; }
            }

            if (!anyActive)
            {
                for (int b = 0; b < 6; b++) _perBodyDepthMm[b] = -1f;
                for (int b = 0; b < 6; b++) _perBodyActive[b] = false;
                _maskReady = true;
                return;
            }

            // 2차 스캔: 활성 바디만 마스크에 페인팅
            for (int i = 0; i < DepthW * DepthH; i++)
            {
                byte bodyIdx = _bodyIndexWork[i];
                if (bodyIdx >= 6 || !active[bodyIdx]) continue;

                ColorSpacePoint cp = _depthToColor[i];
                int cx = (int)(cp.X + 0.5f);
                int cy = (int)(cp.Y + 0.5f);

                if ((uint)cx >= ColorW || (uint)cy >= ColorH) continue;

                int flippedCy = ColorH - 1 - cy;
                byte maskByte = BodyIndexToByte(bodyIdx);

                // TODO: 쉐이더의 Erosion 필터 추가 시 외곽 확장(Dilation) 루프 최적화 검토 필요
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

            for (int b = 0; b < 6; b++)
            {
                _perBodyDepthMm[b] = active[b] && depthCount[b] > 0 ? (float)depthSum[b] / depthCount[b] : -1f;
                _perBodyHeadCx[b]  = headCx[b];
                _perBodyHeadCy[b]  = headCy[b] != int.MaxValue ? headCy[b] : 0;
                _perBodyActive[b]  = active[b];
            }

            // 블록 해상도로 마스크를 스캔해 바디별 픽셀 블록 개수 카운트
            for (int b = 0; b < 6; b++)
            {
                if (!active[b]) { _perBodyBlockCount[b] = 0; continue; }

                float ps   = DepthToPixelSize(_perBodyDepthMm[b]);
                int   step = Mathf.Max(1, (int)ps);
                byte  target = BodyIndexToByte(b);
                int   cnt  = 0;

                for (int y = step / 2; y < ColorH; y += step)
                    for (int x = step / 2; x < ColorW; x += step)
                        if (_maskOutput[y * ColorW + x] == target) cnt++;

                _perBodyBlockCount[b] = cnt;
            }

            _maskReady = true;
        }

        /// <summary>
        /// 감지된 바디 중 평균 깊이가 가까운 순으로 최대 _maxUsers 명을 선택합니다.
        /// 여러 명이 동시에 프레임에 잡혀도 항상 카메라에 가장 가까운 사람(들)만 추적하기 위함입니다.
        /// </summary>
        private bool[] SelectClosestBodies(bool[] present, long[] depthSum, int[] depthCount)
        {
            bool[] active = new bool[6];
            int remaining = _maxUsers;

            while (remaining > 0)
            {
                int best = -1;
                float bestDepth = float.MaxValue;

                for (int b = 0; b < 6; b++)
                {
                    if (!present[b] || active[b]) continue;

                    float avg = depthCount[b] > 0 ? (float)depthSum[b] / depthCount[b] : float.MaxValue;
                    if (avg < bestDepth)
                    {
                        bestDepth = avg;
                        best = b;
                    }
                }

                if (best < 0) break;
                active[best] = true;
                remaining--;
            }

            return active;
        }

        /// <summary>
        /// 인스펙터의 설정값을 바탕으로 쉐이더 파라미터를 갱신합니다.
        /// 거리 비례 모자이크 효과와 마스크 외곽선 보정 수치를 동기화합니다.
        /// </summary>
        private void UpdatePixelSizes()
        {
            if (!_material) return;

            if (_useManualPixelSize)
            {
                FillPixelSizesBuffer(_manualPixelSize);
            }
            else
            {
                for (int b = 0; b < 6; b++)
                {
                    _pixelSizesBuffer[b] = DepthToPixelSize(_perBodyDepthMm[b]);
                }
            }

            for (int b = 0; b < 6; b++)
                _material.SetFloat($"_PixelSize{b}", _pixelSizesBuffer[b]);

            _material.SetFloat("_ErosionSize", _erosionSize);
        }

        /// <summary>
        /// 버퍼의 모든 항목을 동일한 픽셀 크기 값으로 채웁니다.
        /// 수동 조절 모드 시 일괄 적용을 위함입니다.
        /// </summary>
        private void FillPixelSizesBuffer(float value)
        {
            for (int b = 0; b < 6; b++)
            {
                _pixelSizesBuffer[b] = value;
            }
        }

        /// <summary>
        /// 인스펙터에 설정된 거리에 따른 픽셀 크기를 보간합니다.
        /// 외부 설정 대신 에디터에서 직접 제어하기 위함입니다.
        /// 입력 예: depthMm이 _nearDepthMm(1000) 이하 -> _pixelSizeNear 값 산출
        /// </summary>
        private float DepthToPixelSize(float depthMm)
        {
            if (depthMm <= 0f) return _pixelSizeFar;

            float t = Mathf.InverseLerp(_nearDepthMm, _farDepthMm, depthMm);
            return Mathf.Lerp(_pixelSizeNear, _pixelSizeFar, t);
        }

        /// <summary>
        /// 사람 머리 위에 표시할 레이블 UI를 동적으로 생성합니다.
        /// 정보 시각화를 위한 캔버스 요소 사전 세팅입니다.
        /// </summary>
        private void CreateLabels()
        {
            RectTransform parent = _cameraDisplay.transform.parent as RectTransform;
            if (!parent) return;

            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Kinect 는 바디 인덱스 0-5 를 자유롭게 할당하므로 항상 6개 생성
            for (int b = 0; b < 6; b++)
            {
                GameObject go = new GameObject($"BodyLabel_{b}");
                go.transform.SetParent(parent, false);

                RectTransform rt = go.AddComponent<RectTransform>();
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(240f, 50f);

                Text txt = go.AddComponent<Text>();
                txt.font = font;
                txt.fontSize = _labelFontSize;
                txt.color = _labelColor;
                txt.alignment = TextAnchor.MiddleCenter;
                txt.raycastTarget = false;

                Outline outline = go.AddComponent<Outline>();
                outline.effectColor = Color.black;
                outline.effectDistance = new Vector2(2f, -2f);

                go.SetActive(false);
                _labelTexts[b] = txt;
                _labelRts[b] = rt;
            }
        }

        /// <summary>
        /// 인스펙터의 레이블 표시 설정에 따라 텍스트 및 위치를 갱신합니다.
        /// 동적 거리 데이터 및 좌표 추적 동기화 목적입니다.
        /// </summary>
        private void UpdateLabels()
        {
            if (!_showLabels)
            {
                HideAllLabels();
                return;
            }

            for (int b = 0; b < 6; b++)
            {
                if (!_labelTexts[b]) continue;

                if (!_perBodyActive[b])
                {
                    // 비활성 시 스무딩 초기화 → 다음에 나타날 때 튀지 않음
                    _smoothInit[b] = false;
                    _labelTexts[b].gameObject.SetActive(false);
                    continue;
                }

                float rawCx    = (float)_perBodyHeadCx[b];
                float rawCount = (float)_perBodyBlockCount[b];

                // EMA 스무딩: 처음 감지 시 즉시 초기화, 이후 lerp
                if (!_smoothInit[b])
                {
                    _smoothHeadCx[b]    = rawCx;
                    _smoothBlockCount[b] = rawCount;
                    _smoothInit[b]      = true;
                }
                else
                {
                    float a = _labelSmoothing;
                    _smoothHeadCx[b]    = Mathf.Lerp(_smoothHeadCx[b],    rawCx,    a);
                    _smoothBlockCount[b] = Mathf.Lerp(_smoothBlockCount[b], rawCount, a);
                }

                _labelTexts[b].text = $"{Mathf.RoundToInt(_smoothBlockCount[b])}px";

                // headCx는 원본(1920폭) 텍스처 기준 좌표이므로, 화면에 잘려서 보이는 UV 구간 기준으로 재매핑합니다.
                Rect displayUv = _cameraDisplay.uvRect;
                float rawU = _smoothHeadCx[b] / ColorW;
                float u = displayUv.width > 0f ? Mathf.Clamp01((rawU - displayUv.x) / displayUv.width) : rawU;
                _labelRts[b].anchorMin        = new Vector2(u, 1f);
                _labelRts[b].anchorMax        = new Vector2(u, 1f);
                _labelRts[b].anchoredPosition = new Vector2(0f, -60f);

                _labelTexts[b].gameObject.SetActive(true);
            }
        }

        /// <summary>
        /// 모든 레이블을 비활성화합니다.
        /// 화면에 표시할 사람이 없거나 설정에서 레이블이 꺼졌을 때 처리합니다.
        /// </summary>
        private void UpdateCover()
        {
            if (_coverCanvasGroup == null) return;

            bool anyone = false;
            for (int b = 0; b < 6; b++)
                if (_perBodyActive[b]) { anyone = true; break; }

            if (anyone)
            {
                _lastDetectedTime = Time.time;

                if (_coverVisible)
                {
                    _coverVisible = false;
                    StartCoverFade(0f); // 페이드 아웃
                }
            }
            else
            {
                if (!_coverVisible && Time.time - _lastDetectedTime >= _coverHideDelay)
                {
                    _coverVisible = true;
                    StartCoverFade(1f); // 페이드 인
                }
            }
        }

        private void StartCoverFade(float targetAlpha)
        {
            if (_coverFadeCoroutine != null)
                StopCoroutine(_coverFadeCoroutine);
            _coverFadeCoroutine = StartCoroutine(FadeCover(targetAlpha));
        }

        private IEnumerator FadeCover(float targetAlpha)
        {
            float startAlpha = _coverCanvasGroup.alpha;
            float elapsed    = 0f;
            float duration   = Mathf.Max(_coverFadeDuration, 0.01f);

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _coverCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
                yield return null;
            }

            _coverCanvasGroup.alpha = targetAlpha;
            _coverFadeCoroutine = null;
        }

        private void HideAllLabels()
        {
            for (int b = 0; b < 6; b++)
            {
                if (_labelTexts[b])
                {
                    _labelTexts[b].gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// 생성된 마스크 텍스처와 관련 수치를 쉐이더 매터리얼에 초기 전달합니다.
        /// GPU 렌더링 파이프라인으로 연산 결과를 넘기기 위함입니다.
        /// </summary>
        private void ApplyShader()
        {
            _material.SetTexture("_MaskTex", _maskTexture);
            _material.SetFloat("_Threshold", 0.05f);
            _material.SetFloat("_FlipMaskX", 0f);
            _material.SetFloat("_UsePerBodyPixelSize", 1f);
            
            UpdatePixelSizes();

            _pixelOverlay.texture = _colorTexture;
            _pixelOverlay.uvRect = _cameraDisplay.uvRect;
            _pixelOverlay.material = _material;
            _pixelOverlay.gameObject.SetActive(true);
            _shaderApplied = true;
        }

        /// <summary>
        /// 가로(16:9) 컬러 스트림에서 세로형(TargetAspect) 출력에 맞는 중앙 영역만 잘라내는 UV 사각형을 계산합니다.
        /// Y축은 기존과 동일하게 반전합니다.
        /// </summary>
        private static Rect ComputeCroppedUvRect()
        {
            float sourceAspect = (float)ColorW / ColorH;
            float widthFraction = Mathf.Clamp01(TargetAspect / sourceAspect);
            float xMin = (1f - widthFraction) * 0.5f;
            return new Rect(xMin, 1f, widthFraction, -1f);
        }

        /// <summary>
        /// 픽셀 오버레이 UI의 앵커 및 위치를 초기화합니다.
        /// 전체 화면 비율에 맞추어 마스크 화면을 정렬합니다.
        /// </summary>
        private void SetupOverlay()
        {
            RectTransform rt = _pixelOverlay.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            
            _pixelOverlay.gameObject.SetActive(false);
        }

        /// <summary>
        /// 핵심 UI 컴포넌트들의 연결 여부를 검증합니다.
        /// 필수 참조 누락으로 인한 런타임 크래시를 방지합니다.
        /// </summary>
        private bool ValidateReferences()
        {
            if (!_cameraDisplay)
            {
                Debug.LogError("[KinectBodyMask] Camera Display 미연결");
                enabled = false;
                return false;
            }
            if (!_pixelOverlay)
            {
                Debug.LogError("[KinectBodyMask] Pixel Overlay 미연결");
                enabled = false;
                return false;
            }
            if (!_pixelateShader)
            {
                Debug.LogError("[KinectBodyMask] Pixelate Shader 미연결");
                enabled = false;
                return false;
            }
            return true;
        }
    }
}