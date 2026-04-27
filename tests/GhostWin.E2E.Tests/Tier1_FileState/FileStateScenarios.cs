using FluentAssertions;
using GhostWin.E2E.Tests.Infrastructure;
using Xunit;

namespace GhostWin.E2E.Tests.Tier1_FileState;

/// <summary>
/// Tier 1: 파일 상태(session.json)만으로 검증하는 E2E 시나리오.
/// 키보드/마우스/포커스 불필요. disconnected session 에서 실행 가능.
///
/// 원본:
///   scripts/test_m11_cwd_peb.ps1     → PebCwdPolling_Fill
///   scripts/test_m11_e2e_restore.ps1 → CwdRestore_RoundTrip
///
/// M-11 Match Rate 96% 달성에 기여한 패턴을 xUnit Fact 로 이식.
/// </summary>
[Trait("Tier", "1")]
[Trait("Category", "E2E")]
[Collection("GhostWin-App")]  // Tier 1 과 Tier 2 를 같은 컬렉션에 두어 순차 실행 강제.
                               // Collection 이 다르면 xUnit 이 병렬 실행해 StopRunning() 충돌 발생.
public class FileStateScenarios
{
    /// <summary>
    /// PEB(Process Environment Block) 폴링이 올바르게 작동하는지 검증한다.
    ///
    /// 시나리오:
    ///   1. 실행 중인 GhostWin.App 종료 + session.json 삭제 (깨끗한 상태)
    ///   2. 기본 CWD(RepoRoot) 로 앱 실행
    ///   3. 4초 대기 — DispatcherTimer 1s × 3회 PEB 폴링 + snapshot 저장 시간
    ///   4. WM_CLOSE 로 정상 종료 → OnClosing 에서 session.json 저장
    ///   5. session.json 의 leaf.cwd 가 채워졌는지 확인
    ///
    /// 핵심: PowerShell 은 기본적으로 %USERPROFILE% 에서 시작하므로
    /// PEB 폴링이 작동하면 SessionInfo.Cwd 가 비어있지 않아야 한다.
    ///
    /// PS1 원본: scripts/test_m11_cwd_peb.ps1
    /// </summary>
    [Fact]
    public void PebCwdPolling_Fill()
    {
        // ── Arrange ──────────────────────────────────────────────────
        AppRunner.StopRunning();
        SessionJsonHelpers.DeleteSessionJson();

        // ── Act ───────────────────────────────────────────────────────
        // 기본 CWD(RepoRoot) 로 앱 실행
        using var proc = AppRunner.StartDefault();

        // PEB 폴링 대기: DispatcherTimer 1s × 3회 + 여유 (기본 4초)
        Thread.Sleep(AppRunner.DefaultWaitForPeb);

        // WM_CLOSE 로 정상 종료 → OnClosing 에서 session.json 저장
        AppRunner.CloseGracefully(proc);

        // 파일 flush 대기
        Thread.Sleep(500);

        // ── Assert ────────────────────────────────────────────────────
        var leaf = SessionJsonHelpers.FindFirstLeaf(SessionJsonHelpers.ReadSessionJson());

        leaf.Should().NotBeNull(
            "session.json 에 leaf 노드가 있어야 한다. " +
            $"파일: {SessionJsonHelpers.SessionJsonPath}");

        leaf!["cwd"]?.GetValue<string>().Should().NotBeNullOrEmpty(
            "PEB 폴링이 작동하면 PowerShell 시작 CWD 가 leaf.cwd 에 기록되어야 한다.");
    }

    /// <summary>
    /// Run1 에서 저장한 CWD 가 Run2 에서 복원되는지 검증한다 (M-11 핵심 기능).
    ///
    /// 시나리오:
    ///   Run 1: C:\temp 에서 앱 실행 → cwd="C:\temp" 저장 확인
    ///   Run 2: RepoRoot 에서 재실행 (기본 CWD 다름)
    ///           → session.json 에서 cwd 복원 → pwsh 가 C:\temp 에서 시작
    ///           → PEB 폴링이 다시 "C:\temp" 를 캡처 → session.json 재기록
    ///
    /// 핵심: CreateSession(cwd) 오버로드가 복원된 cwd 를 쉘 시작 경로로 전달해야 한다.
    ///
    /// PS1 원본: scripts/test_m11_e2e_restore.ps1
    /// </summary>
    [Fact]
    public void CwdRestore_RoundTrip()
    {
        const string testCwd = @"C:\temp";

        // ── Arrange ──────────────────────────────────────────────────
        AppRunner.StopRunning();
        SessionJsonHelpers.DeleteSessionJson();
        Directory.CreateDirectory(testCwd);

        // ── Act: Run 1 — C:\temp 에서 실행 ───────────────────────────
        using (var proc1 = AppRunner.StartWithCwd(testCwd))
        {
            Thread.Sleep(AppRunner.DefaultWaitForPeb);
            AppRunner.CloseGracefully(proc1);
        }
        Thread.Sleep(500);

        // ── Assert: Run 1 의 cwd 가 C:\temp 인지 확인 ────────────────
        var run1Json = SessionJsonHelpers.ReadSessionJson();
        var run1Leaf = SessionJsonHelpers.FindFirstLeaf(run1Json);

        run1Leaf.Should().NotBeNull("Run 1 실행 후 session.json 에 leaf 노드가 있어야 한다.");
        run1Leaf!["cwd"]?.GetValue<string>().Should().Be(
            testCwd,
            $"Run 1 에서 {testCwd} 로 시작했으므로 PEB 폴링이 {testCwd} 를 기록해야 한다.");

        // ── Act: Run 2 — RepoRoot 에서 재실행 ────────────────────────
        // session.json 이 존재하므로 앱이 cwd 를 복원해야 함
        using (var proc2 = AppRunner.StartWithCwd(AppRunner.RepoRoot))
        {
            Thread.Sleep(AppRunner.DefaultWaitForPeb);
            AppRunner.CloseGracefully(proc2);
        }
        Thread.Sleep(500);

        // ── Assert: Run 2 의 cwd 가 C:\temp 로 복원됐는지 확인 ────────
        var run2Json = SessionJsonHelpers.ReadSessionJson();
        var run2Leaf = SessionJsonHelpers.FindFirstLeaf(run2Json);

        run2Leaf.Should().NotBeNull("Run 2 실행 후 session.json 에 leaf 노드가 있어야 한다.");
        run2Leaf!["cwd"]?.GetValue<string>().Should().Be(
            testCwd,
            $"Run 2 는 session.json 에서 cwd 를 복원해야 하므로 leaf.cwd == {testCwd} 여야 한다. " +
            "복원 실패: CreateSession(cwd) 오버로드 또는 RestoreFromSnapshot 을 확인하라.");
    }
}
