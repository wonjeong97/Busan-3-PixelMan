using UnityEngine;
using Wonjeong.Reporter;
using Wonjeong.Utils;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }
    
    [SerializeField] private Reporter reporter;
    [SerializeField] private GameObject systemCanvas;

    /// <summary>
    /// 싱글톤 패턴 구성 및 시스템 초기화를 수행합니다.
    /// 외부 제이슨 로직을 제거하고, 핵심 런타임 환경과 로그 핸들러만 부착하기 위함입니다.
    /// </summary>
    private void Awake()
    {
        if (Instance)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        
        if (systemCanvas)
        {
            DontDestroyOnLoad(systemCanvas);
        }
        
        TimestampLogHandler.Attach();
    }

    /// <summary>
    /// 초기 커서 숨김 및 리포터 UI 비활성화.
    /// 런타임 시작 시 불필요한 시스템 UI 노출을 방지하기 위함입니다.
    /// </summary>
    private void Start()
    {
        Cursor.visible = false;
        
        if (reporter && reporter.show) 
        {
            reporter.show = false;
        }
    }

    /// <summary>
    /// 단축키 입력 감지 및 시스템 디버그 UI 토글.
    /// 개발 및 유지보수 과정에서 런타임 환경을 제어하기 위함입니다.
    /// </summary>
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.D) && reporter)
        {
            reporter.showGameManagerControl = !reporter.showGameManagerControl;
            if (reporter.show) 
            {
                reporter.show = false;
            }
        }
        else if (Input.GetKeyDown(KeyCode.M)) 
        {
            Cursor.visible = !Cursor.visible;
        }
    }
}