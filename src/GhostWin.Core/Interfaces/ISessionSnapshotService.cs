using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

/// <summary>
/// 앱 세션 상태를 JSON 파일에 주기적으로 저장하고 복원하는 서비스.
/// Singleton 수명 — 앱 전 생애주기에서 타이머와 파일 잠금을 단일 소유.
/// </summary>
public interface ISessionSnapshotService : IAsyncDisposable
{
    /// <summary>
    /// 디스크에서 스냅샷을 읽어 반환. 없음/손상 시 null 반환.
    /// 손상 시 자동으로 .corrupt.{yyyyMMdd-HHmmss} 로 rename (데이터 유실 방지).
    /// App.OnStartup 에서 1회 호출.
    /// </summary>
    Task<SessionSnapshot?> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// 주어진 스냅샷을 session.json 에 원자적 쓰기 (temp → File.Replace).
    /// MainWindow.OnClosing (동기 종료 경로) 에서 1회 await 호출.
    /// 주기 저장은 내부 Start() 경로에서 자동.
    /// </summary>
    Task SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// 주기 저장 타이머 시작 (기본 10초). App.OnStartup 의 복원 직후 1회 호출.
    /// <paramref name="snapshotCollector"/> 는 UI 스레드에서 스냅샷을 수집해 반환하는 대리자.
    /// GhostWin.Services 는 WPF 비의존 — Dispatcher.InvokeAsync 래핑은 호출자(App) 책임.
    /// </summary>
    void Start(Func<Task<SessionSnapshot>> snapshotCollector);

    /// <summary>주기 저장 타이머 중단. OnClosing 의 엔진 정리 직전 호출.</summary>
    Task StopAsync();

    /// <summary>session.json 저장 경로 (진단/테스트 용).</summary>
    string SnapshotPath { get; }
}
