using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface ISessionManager
{
    IReadOnlyList<SessionInfo> Sessions { get; }
    uint? ActiveSessionId { get; }

    uint CreateSession(ushort cols = 80, ushort rows = 24);

    /// <summary>
    /// 지정한 CWD(initial working directory) 로 세션 생성.
    /// M-11 Session Restore 복원 경로 전용 오버로드 — <paramref name="cwd"/> 가 null/empty 면
    /// 엔진 기본 CWD (쉘 프로필) 로 폴백된다.
    /// </summary>
    /// <param name="cwd">저장된 CWD 또는 null (엔진 기본값).</param>
    /// <param name="cols">초기 열 수.</param>
    /// <param name="rows">초기 행 수.</param>
    uint CreateSession(string? cwd, ushort cols = 80, ushort rows = 24);

    void CloseSession(uint id);
    void ActivateSession(uint id);
    void UpdateTitle(uint id, string title);
    void UpdateCwd(uint id, string cwd);
    void UpdateMouseCursorShape(uint id, int mouseCursorShape);

    // ─────────────────────────────────────────────────────────────────────
    // [TEST-ONLY] Phase 6-A 선행 슬롯 — ConPTY stdin 직접 쓰기
    //
    // 목적: 테스트 코드에서 임의 OSC 시퀀스(예: "\e]9;msg\e\\")를
    //       지정 세션의 ConPTY 입력 파이프에 직접 주입하여
    //       shell 출력 없이도 알림 링(Phase 6-A) 등 UI 반응을 검증.
    //
    // 현재 상태: stub — NotImplementedException 반환.
    //             Phase 6-A 구현 시 ConPTY 핸들 통해 실제 쓰기.
    //
    // 격리: [assembly: InternalsVisibleTo("GhostWin.E2E.Tests")] 추가 필요.
    //       프로덕션 코드에서는 절대 호출 금지.
    // ─────────────────────────────────────────────────────────────────────
#pragma warning disable CA1707  // Test-only identifier naming intentional
    [Obsolete("TEST-ONLY: Phase 6-A ConPTY stdin injection — production code must not call this")]
    void TestOnlyInjectBytes(uint sessionId, byte[] data);
#pragma warning restore CA1707
}
