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

    public const string OpenCommandPalette = "E2E_OpenCommandPalette";
    public const string MouseCursorShape = "E2E_MouseCursorShape";
    public const string MouseCursorId = "E2E_MouseCursorId";
    public const string MouseCursorSession = "E2E_MouseCursorSession";
    public const string MouseCursorVersion = "E2E_MouseCursorVersion";
    public const string MouseCursorUpdatedAt = "E2E_MouseCursorUpdatedAt";
    public const string CommandPaletteResults = "E2E_CommandPaletteResults";

    public static string TerminalHost(uint paneId)
        => $"E2E_TerminalHost_{paneId}";

    public static string WorkspaceItem(uint workspaceId)
        => $"E2E_WorkspaceItem_{workspaceId}";

    public static string NotificationItem(uint notificationId)
        => $"E2E_NotificationItem_{notificationId}";

    public static string NotificationRing(uint workspaceId)
        => $"E2E_NotificationRing_{workspaceId}";

    public static string CommandPaletteItem(string actionId)
        => $"E2E_CommandPaletteItem_{ToToken(actionId)}";

    private static string ToToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            buffer[i] = char.IsLetterOrDigit(ch) ? ch : '_';
        }

        return new string(buffer);
    }
}
