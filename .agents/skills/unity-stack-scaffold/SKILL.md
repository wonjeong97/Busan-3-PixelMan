---
name: unity-stack-scaffold
description: Scaffold or refactor Unity C# classes (managers, systems, services) using this project's established stack — VContainer for DI, UniTask for async, MessagePipe for pub/sub events, R3 for reactive state, ZLogger.Unity for logging, ZString for string building, DOTween for tweening. Use this whenever the user asks to create a new manager/system/service class, wants to "이 스택으로" build or refactor something, mentions VContainer/UniTask/MessagePipe/R3/ZLogger/ZString/DOTween by name, or asks to convert a coroutine/event/singleton pattern to the project's DI+async style — even if they just say "매니저 하나 만들어줘" without naming the libraries. The concrete conventions here come from this project's own Packages/com.wonjeong.template code (RootLifetimeScope, GameManagerBase, SoundManager), not generic library docs, so prefer this skill over general Unity/C# knowledge for this stack.
---

# Unity 스택 스캐폴딩 (VContainer / UniTask / MessagePipe / R3 / ZLogger / ZString)

이 스킬은 라이브러리 사용법을 처음부터 설계하는 게 아니라, 이 프로젝트의 `Packages/com.wonjeong.template` (RootLifetimeScope.cs, GameManagerBase.cs, SoundManager.cs)에 이미 정착된 실제 패턴을 재현하기 위한 것이다. 왜 이 방식인지 궁금하면 해당 파일들을 직접 열어 대조해도 된다.

이 스킬은 "어떤 라이브러리를 어떤 순서/형태로 조합하는가"만 다룬다. `GetComponent` 대신 `TryGetComponent`, null 비교 시 암시적 bool, `var` 금지 같은 규칙은 프로젝트 CLAUDE.md가 이미 항상 적용하고 있으므로 여기서 반복하지 않는다.

## 1. Wonjeong 템플릿 우선 재사용

새 기능을 처음부터 작성하기 전에, `com.wonjeong.template` 패키지(`Wonjeong.App` / `Wonjeong.Core` / `Wonjeong.UI` / `Wonjeong.Utils` / `Wonjeong.Data` 네임스페이스)에 이미 그 역할을 하는 베이스 클래스나 유틸리티가 있는지 먼저 확인한다. 없는 걸 새로 만드는 것보다 있는 걸 최대한 활용하는 게 우선이다.

- 씬/전역 매니저는 `MonoBehaviour`를 직접 상속하지 않고 `Wonjeong.Core.GameManagerBase<T>`를 상속한다. 싱글톤 DontDestroyOnLoad, InputAction 연결, Reporter/Inspector 디버그 토글, Settings 비동기 로드가 이미 구현되어 있으므로 중복 구현하지 않는다.
- 사운드/UI/페이드/비디오 관련 요청이면 새로 만들기 전에 `Wonjeong.UI.SoundManager` / `UIManager` / `FadeManager` / `VideoManager`가 이미 그 역할을 하는지 먼저 확인하고, 있으면 확장하거나 그대로 호출한다.
- 설정/데이터 파일 로딩은 직접 파일 I/O나 JSON 파싱을 짜지 않고 `Wonjeong.Utils.JsonLoader.Load<T>` / `LoadAsync<T>`를 재사용한다.
- 전역 DI/로깅/MessagePipe 브로커 등록이 필요하면 완전히 새로운 LifetimeScope를 만들기보다 `Wonjeong.App.RootLifetimeScope`를 상속해 필요한 `Configure*` 메서드만 override하는 걸 우선 고려한다.
- 템플릿에 대응되는 게 없는 완전히 새로운 기능일 때만 아래 2~8번 패턴을 따라 처음부터 작성한다.

## 2. VContainer — DI 등록

- 전역 등록은 `LifetimeScope.Configure(IContainerBuilder builder)`에서 하되, 관심사별로 `ConfigureLogging(builder)`, `ConfigureMessagePipe(builder)`처럼 **private/protected 메서드로 쪼갠다.** 하나의 Configure가 모든 걸 다 하지 않도록 하는 이유는, 나중에 로깅만 바꾸거나 파생 LifetimeScope에서 특정 부분만 override하기 쉽게 하기 위함이다.
- 개별 컴포넌트는 생성자 주입이 아니라 **메서드 주입**을 쓴다:
  ```csharp
  [Inject]
  public void Construct(IPublisher<SomeEvent> publisher, ILogger<MyManager> logger)
  {
      _publisher = publisher;
      _logger = logger;
  }
  ```
  MonoBehaviour는 생성자를 직접 호출할 수 없어서 이 패턴이 필요하다.

## 3. UniTask — 비동기 처리

- Fire-and-forget 초기화/연출은 `async UniTaskVoid` + 호출부에서 `.Forget()`:
  ```csharp
  protected virtual async UniTaskVoid LoadSettingsAsync()
  {
      settings = await JsonLoader.LoadAsync<Settings>("Settings.json", this.GetCancellationTokenOnDestroy());
  }
  // 호출부: LoadSettingsAsync().Forget();
  ```
- 취소 토큰은 기본적으로 `this.GetCancellationTokenOnDestroy()`를 쓴다. 오브젝트가 파괴되면 자동으로 취소되어 별도 정리 코드가 필요 없다.
- 사용자가 도중에 취소할 수 있는 흐름(페이드, 연출 등)은 별도 `CancellationTokenSource`를 만들어 관리한다:
  ```csharp
  _fadeCts = CancellationTokenSource.CreateLinkedTokenSource(this.GetCancellationTokenOnDestroy());
  FadeOutAsync(duration, _fadeCts).Forget();
  ```
  이렇게 링크하면 오브젝트 파괴 시 자동 취소와 수동 취소(`_fadeCts.Cancel()`) 둘 다 동작한다. `try { ... } catch (OperationCanceledException) { ... } finally { _fadeCts?.Dispose(); _fadeCts = null; }` 형태로 정리한다.
- 같은 리소스를 여러 곳에서 동시에 요청할 수 있는 로더/다운로더는 `Dictionary<string, UniTask<T>>`로 진행 중인 태스크를 캐싱해서 중복 요청을 막는다 (SoundManager의 `_activeDownloads` 패턴 참고).

## 4. MessagePipe — 이벤트

- 전역 브로커는 LifetimeScope에서 `builder.RegisterMessageBroker<TEvent>(options)`로 등록한다.
- 발행은 `IPublisher<TEvent>`, 구독은 `ISubscriber<TEvent>`를 주입받아 사용한다.
- 구독은 반드시 `IDisposable`을 받아 `OnDestroy` 등에서 해제한다. 구독 해제를 빼먹으면 파괴된 오브젝트가 이벤트를 계속 받으려다 예외가 난다.

## 5. R3 — 반응형 상태

- 외부에서 관찰해야 하는 상태(체력, 스코어, UI 바인딩 대상 등)는 일반 필드 대신 `ReactiveProperty<T>`로 노출한다.
- 구독은 `.Subscribe(...)`가 반환하는 `IDisposable`을 모아서(`DisposableBag` 등) 오브젝트 파괴 시 한 번에 해제한다. MessagePipe 구독과 동일하게, 해제를 빼먹지 않는 게 핵심이다.

## 6. ZLogger.Unity — 로깅

- 필드로 `ILogger<T> _logger`를 주입받는다.
- 로그를 남길 때는 항상 이 형태를 따른다:
  ```csharp
  if (_logger != null) _logger.ZLogWarning($"[MyManager] 대상이 null이라 처리를 건너뜀.");
  ```
  - `[클래스명]` 태그를 메시지 맨 앞에 붙인다 — 여러 매니저의 로그가 한 콘솔에 섞여도 어디서 난 건지 바로 구분하기 위함.
  - null이 발생할 수 있는 지점(Fallback이 필요한 지점)마다 `_logger != null` 체크 후 경고/에러 로그를 남긴다. 로그 없이 조용히 return하지 않는다.
- **if로 조건/참조를 검사하는 모든 곳은 실패 분기(else)에 로그를 남긴다.** `if (someRef != null) { ... }`처럼 성공 분기만 작성하고 else를 생략하면, 조건이 실패했을 때 아무 흔적 없이 조용히 아무 일도 안 일어난다 — 원인 파악이 콘솔 로그가 아니라 코드 리딩으로만 가능해진다.
  ```csharp
  if (flowController != null)
  {
      flowController.ShowCompletePanel();
  }
  else if (_logger != null)
  {
      _logger.ZLogWarning($"[ResultVideoPanel] flowController is null. CompletePanel will not fade in.");
  }
  ```
  실제로 `ResultVideoPanel`에서 `flowController`가 씬에 연결되지 않았는데 else 로그가 없어서, 영상 재생이 끝나도 CompletePanel이 페이드인되지 않는 원인을 로그 없이 코드까지 뒤져서 찾아야 했던 사례가 있었다. 조건이 맞지 않는 경우가 "정상적으로 자주 발생하는 no-op"이 아니라면 반드시 로그를 남긴다.
- 로그 레벨: 복구 가능한 이상 상황은 `ZLogWarning`, 기능이 실패한 경우는 `ZLogError`, 정상 흐름 기록은 `ZLogInformation`.

## 7. ZString — 문자열 조합

- 런타임에 반복적으로 실행되는 문자열 연결(로그 메시지의 보간 문자열 자체는 예외)이나 경로/URI 조합은 `+`나 `string.Format` 대신 `ZString.Concat(...)` / `ZString.Format(...)`을 쓴다. GC 할당을 줄이기 위함이다.
  ```csharp
  string uri = ZString.Concat("file://", path);
  ```

## 8. DOTween — 트윈/연출

- 템플릿도 DOTween을 표준으로 쓴다. `Wonjeong.UI.FadeManager`와 `SoundManager`의 페이드가 이미 `DOFade(...).SetUpdate(true)` + UniTask 연동으로 구현되어 있으므로, 새 연출도 같은 패턴으로 작성한다. 화면/볼륨 페이드 자체는 여전히 새로 만들지 않고 해당 매니저를 호출한다(1번 재사용 원칙).
- 새로 만드는 연출에서 `Update()` 안의 수동 `Lerp` 누적이나 `WaitForSeconds` 코루틴으로 위치/색/알파를 직접 보간하지 않는다. 그런 코드는 DOTween 한 줄로 대체된다:
  ```csharp
  await _panel.DOAnchorPosY(0f, 0.3f).SetEase(Ease.OutCubic);
  ```
- **UniTask와 연동해서 await 한다** (3번의 취소 토큰 규칙을 그대로 따른다). `UNITASK_DOTWEEN_SUPPORT` define이 켜져 있어 `DOTweenAsyncExtensions`를 쓸 수 있다. 대기는 `ToUniTask(cancellationToken: ...)`로 통일한다 — 템플릿 표준 패턴이고, `WithCancellation`과 달리 취소 시 동작(`TweenCancelBehaviour`)을 연출별로 지정할 수 있다.
- **`SetUpdate(true)`는 연출 성격에 따라 구분한다.** `Time.timeScale`을 무시하는 옵션이므로, 페이드/일시정지 메뉴/로딩처럼 timeScale이 0이어도 돌아야 하는 UI·시스템 연출에만 붙이고, 일시정지·슬로모션을 따라야 하는 게임플레이 연출에는 붙이지 않는다:
  ```csharp
  // UI/시스템 연출 (timeScale 0에서도 동작해야 함)
  await _canvasGroup.DOFade(1f, 0.3f).SetUpdate(true)
      .ToUniTask(cancellationToken: this.GetCancellationTokenOnDestroy());

  // 게임플레이 연출 (일시정지/슬로모션을 따라야 함) — SetUpdate 없이
  await transform.DOMove(target, 1f)
      .ToUniTask(cancellationToken: this.GetCancellationTokenOnDestroy());
  ```
  `yield return tween.WaitForCompletion()` 같은 코루틴 대기는 쓰지 않는다.
- **생성한 트윈은 반드시 수명을 묶는다.** 4번(MessagePipe 구독 해제), 5번(R3 구독 해제)과 같은 이유로, 해제를 빼먹으면 파괴된 오브젝트를 트윈이 계속 건드리다 예외가 난다.
  ```csharp
  transform.DOMove(target, 1f).SetLink(gameObject);
  ```
  `SetLink`로 오브젝트 파괴 시 자동 Kill 되게 하는 게 기본이다. 무한 반복(`SetLoops(-1)`) 트윈처럼 도중에 직접 멈춰야 하는 건 `Tween` 참조를 필드로 들고 있다가 `_tween?.Kill()`로 정리한다.
- 트윈 대상이 없는 순수 수치 보간이나 지연 실행은 빈 GameObject를 만들지 말고 `DOVirtual.Float(...)` / `DOVirtual.DelayedCall(...)`을 쓴다.
- **무료판이라 TextMeshPro 숏컷이 없다.** `DOText`, TMP `DOColor`/`DOFade`는 Pro 전용이라 이 프로젝트에서 컴파일되지 않는다. TMP 텍스트 연출이 필요하면 `DOVirtual.Float`로 값을 보간해 콜백에서 직접 대입한다.

## 9. 리플렉션 최소화

런타임 코드에서 리플렉션(`System.Reflection`, `Type.GetType`, `GetMethod`/`GetField`/`Invoke`, `Activator.CreateInstance`, `dynamic`)을 쓰지 않는다. 이유는 세 가지다.

- IL2CPP 빌드에서 코드 스트리핑에 걸려 에디터에서는 되는데 빌드에서만 깨진다. 재현이 어려운 종류의 버그다.
- 컴파일 타임 검증이 사라진다. 문자열로 참조한 멤버는 이름을 바꿔도 컴파일러가 잡아주지 않는다.
- 매 호출마다 할당과 조회 비용이 든다. ZString(7번)으로 GC를 줄이는 것과 정반대 방향이다.

이 스택에는 리플렉션이 필요할 만한 자리에 이미 대안이 있다.

- 타입으로 구현체를 찾아 생성 → VContainer 주입(2번)
- 다른 시스템의 메서드를 이름으로 호출 → MessagePipe 이벤트(4번) 또는 인터페이스
- 상태 변화 감지 → R3 `ReactiveProperty`(5번)
- JSON 역직렬화 → `Wonjeong.Utils.JsonLoader` 재사용(1번)
- 인스펙터 노출 → `[SerializeField]`

`SendMessage`, `Invoke("MethodName", ...)`, `StartCoroutine("MethodName")`처럼 문자열로 메서드를 호출하는 Unity API도 같은 이유로 쓰지 않는다.

예외는 `Editor/` 폴더의 에디터 전용 도구와 일회성 디버그 코드다. 빌드에 포함되지 않으므로 스트리핑 문제가 없다. 그래도 공개 API가 있으면 그쪽을 먼저 쓴다.

기존 코드에서 리플렉션을 발견하면 조용히 남겨두지 말고, 위 대안 중 무엇으로 바꿀 수 있는지 사용자에게 알린다. 다만 CLAUDE.md의 "수술적 변경" 원칙에 따라 요청받지 않은 리팩터링을 임의로 수행하지는 않는다.

## 10. 메서드 분리 기준

한 메서드가 여러 책임(캐시 확인 → 다운로드 → 적용 같은)을 한 번에 처리하지 않도록, 단계별로 private 메서드를 쪼갠다. SoundManager의 `LoadAndPlayAsync → DownloadAndCacheClipAsync → ExecuteDownloadAsync` 체인이 예시다. 기준은 "이 메서드 이름만 보고 무슨 일을 하는지 한 문장으로 설명할 수 있는가" — 안 되면 쪼갠다.

## 11. 주석 규칙 (이 스택 코드에 한함)

프로젝트 전역 규칙은 "주석은 최소화"지만, 이 프로젝트의 매니저/서비스 클래스는 이미 모든 메서드에 한국어 summary 주석이 달려 있다 (실측 컨벤션). 이 스킬이 적용되는 코드에서는 그 기존 컨벤션을 따른다:

- public/protected/private 구분 없이 **모든 메서드**에 `/// <summary>...</summary>` 작성. `<param>`, `<returns>` 태그는 쓰지 않는다.
- Summary는 이 메서드가 **어떤 역할을 하는지**를 한 문장으로 작성한다 (예: "프레임 단위 보간을 통해 볼륨을 줄이는 페이드아웃 핵심 로직").
- 이모티콘, 특수문자 없이 평서형으로 끝맺는다.

## 12. 상수 중앙 관리 (씬 이름 등)

여러 파일에서 참조하는 문자열 상수 — 특히 **씬 이름**, StreamingAssets 파일 이름처럼 "식별자" 성격의 값 — 은 각 클래스에 `[SerializeField] private string xxxSceneName = "..."` 기본값이나 리터럴로 흩어놓지 않고, `DGAIZone.App.Constants` 같은 **static 클래스 한 곳에 const로 모아** 참조한다.

- 씬을 리네임하면 `Constants.Scenes` 한 줄만 바꾸면 모든 참조가 따라온다.
- 씬 이름을 SerializeField로 두면 코드 기본값과 씬에 직렬화된 값이 이원화되어, 씬 파일에 낡은 값이 남은 채로 조용히 잘못된 씬을 로드하는 사고가 난다. 실제로 이 프로젝트에서 씬 번호를 다시 매긴 뒤 `resultSceneName = "3_Result"`, `outroSceneName = "4_Outro"`, `gameSceneName = "2_Game"`이 갱신되지 않아 존재하지 않는 씬을 로드하려던 버그가 있었다. 그래서 씬 이름은 SerializeField를 제거하고 코드에서 `Constants.Scenes.Xxx`를 직접 참조한다.
- 관심사별 중첩 static 클래스로 묶는다:
  ```csharp
  public static class Constants
  {
      public static class Scenes
      {
          public const string Title = "0_Title";
          public const string Game = "3_Game";
          // ...
      }

      public static class Files { public const string RfidMappings = "RfidMappings.json"; }
  }
  ```
- 반대로 씬별로 조정 가능한 튜닝 값(페이드 시간, 타이핑 속도 등)은 그대로 SerializeField로 둔다. 중앙화 대상은 "정체성(식별자)"이지 "인스턴스별 조정 파라미터"가 아니다.

## 13. 테스트 작성 기준 (UTF)

이 스킬로 매니저/시스템 클래스를 스캐폴딩하거나 수정할 때, 아래 조건에 해당하면 `Tests/Runtime`에 대응하는 `[UnityTest]`를 같이 작성한다. 조건에 해당하지 않으면 테스트를 만들지 않는다 — 모든 클래스에 테스트를 강제하지 않는다.

**테스트가 필요한 경우:**
- `Time.timeScale`, `SetUpdate(true)`처럼 타이밍/일시정지에 영향을 받는 로직 (예: 페이드, 연출)
- 상태 플래그(`_isTransitioning` 등)로 중복 호출을 막는 로직 — 플래그가 해제되지 않으면 이후 호출이 전부 무시되는 소프트락 위험이 있는 코드
- 여러 곳에서 동시에 호출될 수 있는 캐싱/중복 방지 로직 (`SoundManager`의 `_activeDownloads` 패턴 등)
- 과거에 버그가 발생했던 지점을 수정하는 경우 (회귀 방지)

**테스트가 필요 없는 경우:**
- 단순 getter/setter, DI 등록 코드, 데이터 클래스
- Inspector 값 대입만 하는 초기화 코드

**작성 패턴** (`FadeManagerTests.cs` 표준을 따른다):
```csharp
[UnityTest]
public IEnumerator 설명은_한글_평서형으로() => UniTask.ToCoroutine(async () =>
{
    await AwaitWithRealtimeTimeout(_target.SomeAsync());
    Assert.IsFalse(GetIsTransitioning(), "플래그가 해제되지 않아 이후 호출이 무시됨");
});
```
- 비동기 완료 대기는 반드시 `UniTask.WhenAny(task, UniTask.Delay(..., DelayType.UnscaledDeltaTime))`로 실시간 타임아웃을 건다. 가드가 없으면 결함이 있는 구현에서 테스트가 "실패"가 아니라 "무한 대기"로 멈춰 테스트 러너 전체를 막는다.
- `[TearDown]`에서 `Time.timeScale` 등 건드린 전역 상태를 반드시 원복한다.
- private 필드 검증에 새 리플렉션 코드를 추가하지 않는다(9번 규칙과 동일한 이유). 이미 `NonPublic` 리플렉션이 쓰인 기존 헬퍼가 있으면 재사용하되, 새로 만들 경우 가능하면 공개 상태/이벤트로 노출하는 걸 우선 고려한다.
