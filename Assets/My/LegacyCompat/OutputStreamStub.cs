// OutputStream<T> 호환성 스텁
// 구버전 MediaPipe Unity Plugin에서 사용하던 OutputStream<T> 클래스가
// 신버전 Tasks API에서 제거됨. Legacy 씬(Holistic, MediaPipe Video) 컴파일용 스텁.

using System;
using System.Threading.Tasks;

namespace Mediapipe
{
  public class OutputStream<T> : IDisposable
  {
    public class OutputEventArgs : EventArgs
    {
      public Packet<T> packet;
    }

    public struct NextResult
    {
      public bool ok;
      public Packet<T> packet;
    }

    public OutputStream(CalculatorGraph graph, string streamName, bool observeTimestampBound)
    {
      // Legacy API stub - 실제 동작하지 않음
    }

    public void StartPolling() { }

    public void AddListener(EventHandler<OutputEventArgs> listener, long timeoutMicrosec) { }

    public void RemoveListener(EventHandler<OutputEventArgs> listener) { }

    public Task<NextResult> WaitNextAsync()
    {
      return Task.FromResult(new NextResult { ok = false, packet = null });
    }

    public void Dispose() { }
  }
}
