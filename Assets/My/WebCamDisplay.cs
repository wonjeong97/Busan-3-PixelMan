using UnityEngine;
using UnityEngine.UI;

public class WebCamDisplay : MonoBehaviour
{
    [SerializeField] private RawImage displayImage;

    private WebCamTexture webCamTexture;

    /// <summary>
    /// 웹캠 디바이스를 초기화하고 RawImage 텍스처에 할당합니다.
    /// 하드웨어 카메라 피드를 UI에 실시간으로 렌더링하기 위함입니다.
    /// </summary>
    private void Start()
    {
        if (!displayImage)
        {
            displayImage = GetComponent<RawImage>();
            Debug.LogWarning("displayImage 할당 누락. GetComponent로 폴백 적용.");
        }

        if (!displayImage) return;

        // TODO: 다중 카메라 연결 시 WebCamTexture.devices 배열을 순회하여 원하는 장치를 선택하는 로직 추가 필요.
        webCamTexture = new WebCamTexture();
        displayImage.texture = webCamTexture;
        
        webCamTexture.Play();
    }

    /// <summary>
    /// 컴포넌트 파괴 시 하드웨어 리소스 점유를 해제합니다.
    /// 웹캠이 백그라운드에서 계속 켜져 있어 발생하는 메모리 누수 및 기기 부하를 방지하기 위함입니다.
    /// </summary>
    private void OnDestroy()
    {
        if (webCamTexture)
        {
            webCamTexture.Stop();
        }
    }
}