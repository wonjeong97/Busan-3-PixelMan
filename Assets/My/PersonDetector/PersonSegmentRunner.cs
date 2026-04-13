// PersonSegmentRunner.cs
// SelfieSegmenter 모델을 사용해 사람의 per-pixel confidence mask를 추출합니다.
// OnMaskReady(float[] data, int w, int h, float normLeft, normTop, normRight, normBottom) 이벤트를 발생시킵니다.
// maskData: row-major, row 0 = 이미지 상단, 0~1 float (1 = 사람, 0 = 배경)

using System;
using System.Collections;
using Mediapipe;                                     // Image
using Mediapipe.Unity;                               // GpuManager, TryReadChannelNormalized
using Mediapipe.Tasks.Vision.ImageSegmenter;
using Mediapipe.Unity.Sample;
using Mediapipe.Unity.Sample.ImageSegmentation;
using UnityEngine;
using UnityEngine.Rendering;
using TextureFramePool = Mediapipe.Unity.Experimental.TextureFramePool;

namespace PixelMan
{
  public class PersonSegmentRunner : VisionTaskApiRunner<ImageSegmenter>
  {
    private TextureFramePool _textureFramePool;

    public readonly ImageSegmentationConfig config = new ImageSegmentationConfig
    {
      Model    = ModelType.SelfieSegmenterSquare,
      RunningMode = Mediapipe.Tasks.Vision.Core.RunningMode.LIVE_STREAM,
    };

    // float[] : row-major, row0 = 이미지 상단
    // normBounds: 마스크에서 계산한 사람 bounding box (0~1)
    public event Action<float[], int, int, float, float, float, float> OnMaskReady;

    public bool FlipMaskX { get; private set; }

    private float[] _maskArray;
    private int _maskW, _maskH;

    // bounding box 계산용 샘플링 간격 (성능)
    private const int BoundsSampleStride = 4;
    private const float MaskThresholdForBounds = 0.5f;

    public override void Stop()
    {
      base.Stop();
      _textureFramePool?.Dispose();
      _textureFramePool = null;
    }

    protected override IEnumerator Run()
    {
      Debug.Log($"[PersonSegment] Model={config.ModelName}, RunningMode={config.RunningMode}");

      yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

      var options = config.GetImageSegmenterOptions(
        config.RunningMode == Mediapipe.Tasks.Vision.Core.RunningMode.LIVE_STREAM ? OnSegmentOutput : null
      );
      taskApi = ImageSegmenter.CreateFromOptions(options, GpuManager.GpuResources);

      var imageSource = ImageSourceProvider.ImageSource;
      yield return imageSource.Play();

      if (!imageSource.isPrepared)
      {
        Debug.LogError("[PersonSegment] ImageSource 시작 실패");
        yield break;
      }

      _maskW = imageSource.textureWidth;
      _maskH = imageSource.textureHeight;
      _maskArray = new float[_maskW * _maskH];

      _textureFramePool = new TextureFramePool(_maskW, _maskH, TextureFormat.RGBA32, 10);

      screen.Initialize(imageSource);
      yield return null;

      // SelfieSegmenter는 rotation 0 고정
      var imageProcessingOptions = new Mediapipe.Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: 0);

      var transformationOptions = imageSource.GetTransformationOptions();
      var flipHorizontally = transformationOptions.flipHorizontally;
      // flipVertically는 셰이더의 Y-플립(1.0 - uv.y)이 담당하므로 항상 false
      var flipVertically = false;
      FlipMaskX = flipHorizontally;

      AsyncGPUReadbackRequest req = default;
      var waitUntilReqDone = new WaitUntil(() => req.done);
      var waitForEndOfFrame = new WaitForEndOfFrame();
      var result = ImageSegmenterResult.Alloc();

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
          case ImageReadMode.CPU:
            yield return waitForEndOfFrame;
            textureFrame.ReadTextureOnCPU(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
          case ImageReadMode.CPUAsync:
          default:
            req = textureFrame.ReadTextureAsync(imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
            yield return waitUntilReqDone;
            if (req.hasError) continue;
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
        }

        switch (taskApi.runningMode)
        {
          case Mediapipe.Tasks.Vision.Core.RunningMode.IMAGE:
            if (taskApi.TrySegment(image, imageProcessingOptions, ref result))
              FireMask(result);
            DisposeAllMasks(result);
            break;
          case Mediapipe.Tasks.Vision.Core.RunningMode.VIDEO:
            if (taskApi.TrySegmentForVideo(image, GetCurrentTimestampMillisec(), imageProcessingOptions, ref result))
              FireMask(result);
            DisposeAllMasks(result);
            break;
          case Mediapipe.Tasks.Vision.Core.RunningMode.LIVE_STREAM:
            taskApi.SegmentAsync(image, GetCurrentTimestampMillisec(), imageProcessingOptions);
            break;
        }
      }
    }

    private void OnSegmentOutput(ImageSegmenterResult result, Image image, long timestamp)
    {
      FireMask(result);
      DisposeAllMasks(result);
    }

    private void FireMask(ImageSegmenterResult result)
    {
      if (OnMaskReady == null) return;
      if (result.confidenceMasks == null || result.confidenceMasks.Count == 0) return;

      var mask = result.confidenceMasks[0]; // index 0 = Person
      if (!mask.TryReadChannelNormalized(0, _maskArray, false)) return;

      // 마스크에서 bounding box 계산 (서브샘플링으로 성능 절약)
      float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
      bool found = false;
      float invW = 1f / _maskW, invH = 1f / _maskH;

      for (int y = 0; y < _maskH; y += BoundsSampleStride)
      {
        for (int x = 0; x < _maskW; x += BoundsSampleStride)
        {
          if (_maskArray[y * _maskW + x] > MaskThresholdForBounds)
          {
            float nx = x * invW, ny = y * invH;
            if (nx < minX) minX = nx;
            if (nx > maxX) maxX = nx;
            if (ny < minY) minY = ny;
            if (ny > maxY) maxY = ny;
            found = true;
          }
        }
      }

      if (!found) return;

      OnMaskReady.Invoke(_maskArray, _maskW, _maskH, minX, minY, maxX, maxY);
    }

    private void DisposeAllMasks(ImageSegmenterResult result)
    {
      if (result.confidenceMasks != null)
        foreach (var m in result.confidenceMasks)
          m?.Dispose();
      result.categoryMask?.Dispose();
    }
  }
}
