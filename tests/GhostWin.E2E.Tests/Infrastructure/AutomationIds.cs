namespace GhostWin.E2E.Tests.Infrastructure;

/// <summary>
/// GhostWin.App XAML 의 AutomationProperties.AutomationId 값 상수.
///
/// 원천: src/GhostWin.App/MainWindow.xaml:193-209
/// XAML 선언과 반드시 동기화해야 한다.
///
/// Tier 2/3 UIA 시나리오에서 FlaUI 가 이 ID 로 요소를 찾는다.
/// Phase 6-A 예약 슬롯은 TODO 주석으로 표시.
/// </summary>
public static class AutomationIds
{
    // ── 확인된 기존 4개 (MainWindow.xaml:193-209) ────────────────────────────
    // 0x0 크기의 투명 버튼으로 XAML 에 선언됨.
    // FlaUI InvokePattern.Invoke() 로 키보드 없이 명령 실행 가능.
    // disconnected session (Claude Code bash) 에서도 작동 확인됨.

    /// <summary>수직 분할 (Alt+V 대응)</summary>
    public const string SplitVertical   = "E2E_SplitVertical";

    /// <summary>수평 분할 (Alt+H 대응)</summary>
    public const string SplitHorizontal = "E2E_SplitHorizontal";

    /// <summary>현재 포커스된 pane 닫기 (Ctrl+Shift+W 대응)</summary>
    public const string ClosePane       = "E2E_ClosePane";

    /// <summary>새 워크스페이스 (Ctrl+T 대응)</summary>
    public const string NewWorkspace    = "E2E_NewWorkspace";

    // ── Wave 3 실측 후 확정 예정 ───────────────────────────────────────────
    // GhostWin.App 에 TabControl 또는 동등 컨트롤이 있으면 여기서 선언.
    // Wave 3 에서 UIA tree 탐색으로 실제 존재 여부 확인 필요.
    // public const string WorkspaceTabControl = "E2E_WorkspaceTabControl";

    // ── Phase 6-A 예약 슬롯 (Tier 3) ─────────────────────────────────────
    // TODO Phase 6-A: XAML 에 AutomationProperties.AutomationId=E2E_NotificationRing_{index} 추가 필요.
    // 탭 인덱스(0-based)별 알림 링 요소를 가리킨다.
    // 현재는 메서드만 선언하고, 실제 UI 는 Phase 6-A 에서 구현.

    /// <summary>
    /// [Phase 6-A 예약] 지정 탭의 알림 링 AutomationId.
    /// Phase 6-A 에서 XAML 에 AutomationProperties.AutomationId 부여 후 활성화.
    /// </summary>
    public static string NotificationRing(int tabIndex)
        => $"E2E_NotificationRing_{tabIndex}";
}
