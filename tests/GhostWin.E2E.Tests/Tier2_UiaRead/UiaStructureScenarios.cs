using FluentAssertions;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using GhostWin.E2E.Tests.Infrastructure;
using Xunit;

namespace GhostWin.E2E.Tests.Tier2_UiaRead;

/// <summary>
/// Tier 2: UIA Tree 구조 읽기 시나리오.
/// FlaUI AutomationId 기반으로 요소 존재 여부와 초기 레이아웃을 검증한다.
/// 키보드/마우스 입력 없음 — InvokePattern 없음 — 순수 읽기 전용.
///
/// 검증 항목:
///   1. 앱 메인 윈도우 Name/AutomationId 읽기 가능
///   2. E2E 자동화 훅 버튼 4개 존재 (AutomationIds.cs 기준)
///   3. 초기 상태에서 Pane 요소가 1개 이상 존재
///
/// 원본 참조: tests/e2e-flaui-split-content/UiaProbe.cs Step 3~5
///
/// Phase 6-A 예약:
///   NotificationRing AutomationId 가 구현되면 Wave 3 확장 또는 Tier3_UiaProperty 에 추가.
/// </summary>
[Trait("Tier", "2")]
[Trait("Category", "E2E")]
[Collection("GhostWin-App")]
public class UiaStructureScenarios : IClassFixture<GhostWinAppFixture>
{
    private readonly GhostWinAppFixture _fixture;

    public UiaStructureScenarios(GhostWinAppFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// 메인 윈도우의 UIA Name 프로퍼티를 읽을 수 있는지 확인한다.
    /// UIA 연결 자체가 작동하는지 검증하는 기본 Smoke Test.
    /// </summary>
    [Fact]
    public void MainWindow_Name_IsReadable()
    {
        var name = _fixture.MainWindow.Properties.Name.Value;
        // 이름이 null/empty 가 아니면 OK (GhostWin, 또는 title 포함 어떤 값이든)
        name.Should().NotBeNull(
            "UIA 가 메인 윈도우 Name 을 반환해야 한다. " +
            "null 이면 FlaUI/UIA3 연결 자체가 실패한 것.");
    }

    /// <summary>
    /// E2E_SplitVertical 버튼이 UIA Tree 에 존재하는지 확인한다.
    /// MainWindow.xaml 의 AutomationId 선언과 동기화 상태를 검증.
    /// </summary>
    [Fact]
    public void E2E_SplitVertical_Button_Exists()
    {
        AssertAutomationButtonExists(AutomationIds.SplitVertical,
            "E2E_SplitVertical 버튼이 UIA Tree 에 있어야 한다. " +
            "없으면 MainWindow.xaml 에서 AutomationProperties.AutomationId 선언이 제거된 것.");
    }

    /// <summary>E2E_SplitHorizontal 버튼 존재 확인</summary>
    [Fact]
    public void E2E_SplitHorizontal_Button_Exists()
    {
        AssertAutomationButtonExists(AutomationIds.SplitHorizontal,
            "E2E_SplitHorizontal 버튼이 UIA Tree 에 있어야 한다.");
    }

    /// <summary>E2E_ClosePane 버튼 존재 확인</summary>
    [Fact]
    public void E2E_ClosePane_Button_Exists()
    {
        AssertAutomationButtonExists(AutomationIds.ClosePane,
            "E2E_ClosePane 버튼이 UIA Tree 에 있어야 한다.");
    }

    /// <summary>E2E_NewWorkspace 버튼 존재 확인</summary>
    [Fact]
    public void E2E_NewWorkspace_Button_Exists()
    {
        AssertAutomationButtonExists(AutomationIds.NewWorkspace,
            "E2E_NewWorkspace 버튼이 UIA Tree 에 있어야 한다.");
    }

    /// <summary>
    /// 초기 상태에서 Pane ControlType 요소가 1개 이상 존재하는지 확인한다.
    /// GhostWin 은 WPF + HwndHost 구조이므로 최소 1개의 Pane 은 항상 있어야 한다.
    /// </summary>
    [Fact]
    public void InitialState_HasAtLeastOnePaneElement()
    {
        var panes = _fixture.MainWindow.FindAllDescendants(
            cf => cf.ByControlType(ControlType.Pane));

        panes.Should().NotBeEmpty(
            "초기 상태에서 Pane 요소가 1개 이상 있어야 한다. " +
            "0 이면 WPF visual tree 가 UIA 에 노출되지 않은 것.");
    }

    // ── Helper ──────────────────────────────────────────────────────────

    private void AssertAutomationButtonExists(string automationId, string because)
    {
        var button = _fixture.MainWindow.FindFirstDescendant(
            cf => cf.ByAutomationId(automationId));

        button.Should().NotBeNull(because +
            $"\n  AutomationId: {automationId}" +
            $"\n  원천: src/GhostWin.App/MainWindow.xaml:193-209");
    }
}
