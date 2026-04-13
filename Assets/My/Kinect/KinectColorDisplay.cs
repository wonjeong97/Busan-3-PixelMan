// KinectColorDisplay.cs
// Kinect V2 공식 Windows.Kinect API를 사용해 컬러 스트림을 RawImage에 출력합니다.
// 씬 설정:
//   - 이 컴포넌트를 아무 오브젝트에나 추가
//   - Target Raw Image → 출력할 RawImage 연결

using UnityEngine;
using UnityEngine.UI;
using Windows.Kinect;

namespace PixelMan
{
  public class KinectColorDisplay : MonoBehaviour
  {
    [SerializeField] private RawImage _targetRawImage;

    private KinectSensor    _sensor;
    private ColorFrameReader _reader;
    private Texture2D        _texture;
    private byte[]           _data;

    private void Start()
    {
      if (_targetRawImage == null)
      {
        _targetRawImage = GetComponent<RawImage>();
        if (_targetRawImage == null)
        {
          Debug.LogError("[KinectColorDisplay] RawImage가 연결되지 않았습니다.");
          enabled = false;
          return;
        }
      }

      _sensor = KinectSensor.GetDefault();
      if (_sensor == null)
      {
        Debug.LogError("[KinectColorDisplay] Kinect 센서를 찾을 수 없습니다.");
        enabled = false;
        return;
      }

      _reader = _sensor.ColorFrameSource.OpenReader();

      var desc = _sensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Rgba);
      _texture = new Texture2D(desc.Width, desc.Height, TextureFormat.RGBA32, false);
      _data    = new byte[desc.Width * desc.Height * 4];

      _targetRawImage.texture = _texture;
      _targetRawImage.uvRect  = new Rect(0f, 1f, 1f, -1f); // Y 반전

      if (!_sensor.IsOpen)
        _sensor.Open();

      Debug.Log($"[KinectColorDisplay] Kinect 초기화 완료 ({desc.Width}x{desc.Height})");
    }

    private void Update()
    {
      if (_reader == null) return;

      using (var frame = _reader.AcquireLatestFrame())
      {
        if (frame == null) return;

        frame.CopyConvertedFrameDataToArray(_data, ColorImageFormat.Rgba);
        _texture.LoadRawTextureData(_data);
        _texture.Apply();
      }
    }

    private void OnDestroy()
    {
      if (_reader != null)
      {
        _reader.Dispose();
        _reader = null;
      }

      if (_sensor != null && _sensor.IsOpen)
      {
        _sensor.Close();
        _sensor = null;
      }
    }
  }
}
