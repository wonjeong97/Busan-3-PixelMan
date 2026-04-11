// PersonDetectorRunner.cs
// ObjectDetector(EfficientDet) 기반 사람 감지.
// 감지된 사람의 bounding box 높이 비율로 거리를 추정하여
// OnPersonDetected 이벤트로 결과를 전달합니다.

using System;
using System.Collections;
using Mediapipe.Tasks.Vision.ObjectDetector;
using UnityEngine;
using UnityEngine.Rendering;
using ObjectDetectionResult = Mediapipe.Tasks.Components.Containers.DetectionResult;

namespace Mediapipe.Unity.Sample.ObjectDetection
{
  /// <summary>
  /// 사람 감지 결과 데이터.
  /// norm* 필드는 이미지 크기로 정규화된 0~1 범위 값.
  /// boxHeightRatio = normBottom - normTop (거리 추정용: 클수록 가까움).
  /// </summary>
  public struct PersonDetectionData
  {
    public float normLeft;           // 0~1
    public float normTop;            // 0~1
    public float normRight;          // 0~1
    public float normBottom;         // 0~1
    public float boxHeightRatio;     // normBottom - normTop (거리 추정용)
    public Vector2 normalizedCenter; // (centerX, centerY) 0~1
    public float score;
  }

  public class PersonDetectorRunner : VisionTaskApiRunner<ObjectDetector>
  {
    [SerializeField] private DetectionResultAnnotationController _annotationController;

    private Experimental.TextureFramePool _textureFramePool;

    // 정규화에 사용할 이미지 크기 (Run() 진입 시 설정)
    private int _imageWidth;
    private int _imageHeight;

    public readonly ObjectDetectionConfig config = new ObjectDetectionConfig();

    /// <summary>사람 감지 시 호출. PersonDetectionData[] = 이번 프레임의 모든 감지 결과</summary>
    public event Action<PersonDetectionData[]> OnPersonDetected;

    public override void Stop()
    {
      base.Stop();
      _textureFramePool?.Dispose();
      _textureFramePool = null;
    }

    protected override IEnumerator Run()
    {
      Debug.Log($"[PersonDetector] Delegate={config.Delegate}, Model={config.ModelName}, RunningMode={config.RunningMode}");

      yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

      var options = config.GetObjectDetectorOptions(
        config.RunningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM ? OnDetectionsOutput : null
      );
      taskApi = ObjectDetector.CreateFromOptions(options, GpuManager.GpuResources);

      var imageSource = ImageSourceProvider.ImageSource;
      yield return imageSource.Play();

      if (!imageSource.isPrepared)
      {
        Debug.LogError("[PersonDetector] ImageSource 시작 실패");
        yield break;
      }

      _imageWidth  = imageSource.textureWidth;
      _imageHeight = imageSource.textureHeight;

      _textureFramePool = new Experimental.TextureFramePool(
        _imageWidth, _imageHeight, TextureFormat.RGBA32, 10);

      screen.Initialize(imageSource);

      if (_annotationController)
        SetupAnnotationController(_annotationController, imageSource);

      var transformationOptions = imageSource.GetTransformationOptions();
      var flipH = transformationOptions.flipHorizontally;
      var flipV = transformationOptions.flipVertically;
      var imageProcessingOptions = new Tasks.Vision.Core.ImageProcessingOptions(
        rotationDegrees: (int)transformationOptions.rotationAngle);

      AsyncGPUReadbackRequest req = default;
      var waitUntilReqDone = new WaitUntil(() => req.done);
      var waitForEndOfFrame = new WaitForEndOfFrame();
      var result = ObjectDetectionResult.Alloc(System.Math.Max(options.maxResults ?? 0, 0));

      var canUseGpuImage = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3
                           && GpuManager.GpuResources != null;
      using var glContext = canUseGpuImage ? GpuManager.GetGlContext() : null;

      while (true)
      {
        if (isPaused)
          yield return new WaitWhile(() => isPaused);

        if (!_textureFramePool.TryGetTextureFrame(out var textureFrame))
        {
          yield return new WaitForEndOfFrame();
          continue;
        }

        Image image;
        switch (config.ImageReadMode)
        {
          case ImageReadMode.GPU:
            textureFrame.ReadTextureOnGPU(imageSource.GetCurrentTexture(), flipH, flipV);
            image = textureFrame.BuildGPUImage(glContext);
            yield return waitForEndOfFrame;
            break;
          case ImageReadMode.CPU:
            yield return waitForEndOfFrame;
            textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipH, flipV);
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
          case ImageReadMode.CPUAsync:
          default:
            req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipH, flipV);
            yield return waitUntilReqDone;
            if (req.hasError)
            {
              Debug.LogWarning("[PersonDetector] 텍스처 읽기 실패");
              continue;
            }
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
        }

        switch (taskApi.runningMode)
        {
          case Tasks.Vision.Core.RunningMode.IMAGE:
            if (taskApi.TryDetect(image, imageProcessingOptions, ref result))
            {
              if (_annotationController != null) _annotationController.DrawNow(result);
              RaisePersonEvent(result);
            }
            else
            {
              if (_annotationController != null) _annotationController.DrawNow(default);
            }
            break;
          case Tasks.Vision.Core.RunningMode.VIDEO:
            if (taskApi.TryDetectForVideo(image, GetCurrentTimestampMillisec(), imageProcessingOptions, ref result))
            {
              if (_annotationController != null) _annotationController.DrawNow(result);
              RaisePersonEvent(result);
            }
            else
            {
              if (_annotationController != null) _annotationController.DrawNow(default);
            }
            break;
          case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
            taskApi.DetectAsync(image, GetCurrentTimestampMillisec(), imageProcessingOptions);
            break;
        }
      }
    }

    // LIVE_STREAM 콜백
    private void OnDetectionsOutput(ObjectDetectionResult result, Image image, long timestamp)
    {
      if (_annotationController != null) _annotationController.DrawLater(result);
      RaisePersonEvent(result);
    }

    /// <summary>
    /// ObjectDetectionResult에서 'person' 레이블만 추려 PersonDetectionData 배열을 만들고
    /// OnPersonDetected 이벤트를 발생시킵니다.
    /// boundingBox는 픽셀 좌표(left/top/right/bottom)이므로 이미지 크기로 나눠 정규화합니다.
    /// </summary>
    private void RaisePersonEvent(ObjectDetectionResult result)
    {
      if (OnPersonDetected == null) return;

      var detections = result.detections;
      if (detections == null || detections.Count == 0) return;

      float invW = _imageWidth  > 0 ? 1f / _imageWidth  : 1f;
      float invH = _imageHeight > 0 ? 1f / _imageHeight : 1f;

      // 가장 큰 박스(넓이 기준)의 person 하나만 선택
      bool found = false;
      float bestArea = -1f;
      PersonDetectionData best = default;

      foreach (var detection in detections)
      {
        if (detection.categories == null || detection.categories.Count == 0) continue;

        float score = 0f;
        bool isPerson = false;
        foreach (var cat in detection.categories)
        {
          if (string.Equals(cat.categoryName, "person", StringComparison.OrdinalIgnoreCase))
          {
            isPerson = true;
            score = cat.score;
            break;
          }
        }
        if (!isPerson) continue;

        var bb = detection.boundingBox;
        float normLeft   = bb.left   * invW;
        float normTop    = bb.top    * invH;
        float normRight  = bb.right  * invW;
        float normBottom = bb.bottom * invH;
        float area = (normRight - normLeft) * (normBottom - normTop);

        if (area > bestArea)
        {
          bestArea = area;
          best = new PersonDetectionData
          {
            normLeft         = normLeft,
            normTop          = normTop,
            normRight        = normRight,
            normBottom       = normBottom,
            boxHeightRatio   = normBottom - normTop,
            normalizedCenter = new Vector2(
              (normLeft + normRight)  * 0.5f,
              (normTop  + normBottom) * 0.5f
            ),
            score = score
          };
          found = true;
        }
      }

      if (found)
        OnPersonDetected.Invoke(new[] { best });
    }
  }
}
