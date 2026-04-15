using System.Text;
using GhostWin.Core.Interfaces;

namespace GhostWin.E2E.Tests.Stubs;

/// <summary>
/// OSC 시퀀스 주입 유틸리티 — Wave 2 stub, Phase 6-A 선행 인프라.
///
/// 목적: 테스트 코드에서 표준 OSC 시퀀스를 쉽게 세션에 주입.
///       ISessionManager.TestOnlyInjectBytes 를 통해 ConPTY stdin 에 직접 기록.
///
/// 현재 상태 (2026-04-16):
///   - ISessionManager.TestOnlyInjectBytes 가 stub (NotImplementedException)
///   - Phase 6-A 에서 ConPTY stdin 쓰기 구현 후 실제 동작
///
/// 사용 예 (Phase 6-A 이후):
///   var mgr = serviceProvider.GetRequiredService&lt;ISessionManager&gt;();
///   OscInjector.InjectOsc9(mgr, sessionId, "알림 테스트 메시지");
///   // → 탭에 알림 링이 표시되는지 FlaUI Tier 3 로 검증
/// </summary>
public static class OscInjector
{
    /// <summary>
    /// OSC 7 — 현재 작업 디렉토리(CWD) 변경 알림.
    /// 시퀀스: ESC ] 7 ; file:///path ST
    ///
    /// Phase 6-A 이전: NotImplementedException 발생.
    /// </summary>
    [Obsolete("Requires Phase 6-A ConPTY stdin injection implementation")]
    public static void InjectOsc7(ISessionManager mgr, uint sessionId, string directoryPath)
    {
        // OSC 7 형식: ESC ] 7 ; file:///URI-encoded-path ST
        // URI encoding: backslash → forward slash
        var uri = $"file:///{directoryPath.Replace('\\', '/')}";
        var sequence = $"\x1b]7;{uri}\x1b\\";
        mgr.TestOnlyInjectBytes(sessionId, Encoding.UTF8.GetBytes(sequence));
    }

    /// <summary>
    /// OSC 9 — 알림(Notification) 메시지.
    /// 시퀀스: ESC ] 9 ; message ST
    ///
    /// Phase 6-A 에서 알림 링(notification ring) 테스트에 사용.
    /// Phase 6-A 이전: NotImplementedException 발생.
    /// </summary>
    [Obsolete("Requires Phase 6-A ConPTY stdin injection implementation")]
    public static void InjectOsc9(ISessionManager mgr, uint sessionId, string message)
    {
        // OSC 9 형식: ESC ] 9 ; message ST
        // GhostWin Phase 6-A 에서 해석하여 탭 알림 링 표시 예정
        var sequence = $"\x1b]9;{message}\x1b\\";
        mgr.TestOnlyInjectBytes(sessionId, Encoding.UTF8.GetBytes(sequence));
    }
}
