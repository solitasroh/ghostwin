using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using GhostWin.E2E.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace GhostWin.E2E.Tests;

/// <summary>
/// M-16-F UI 완성도 audit 자동화 진단.
/// 단일 [Fact] 안에 sequential 시나리오 (UIA tear-down 회피 패턴, 이전
/// RootCauseDiagnostics.cs 와 동일).
///
/// 측정 시나리오:
///   A. 초기 UIA tree dump — Button / MenuItem / interactive element
///                            의 Name / HelpText / AutomationId / focusable
///                            (A1 ToolTip / NEW-A 캡션 a11y / NEW-B workspace ✕)
///   B. SettingsPage open + UIA verify — A2 closure
///   C. Tab focus chain — Keyboard.Press(TAB) sequential, focused element list
///                        (F1 / F9 / F10 / F15)
///   D. 워크스페이스 ListItem 우클릭 → ContextMenu popup detection
///   E. NotifPanel 우클릭 → ContextMenu (F12)
///   F. Maximize 후 bottom 픽셀 sample — PrintWindow Win32 (가상 모니터 우회)
///
/// 출력: tests/GhostWin.E2E.Tests/audit-output/{scenario}.tsv|.png|.txt
///
/// memory rule:
///   feedback_ui_visual_audit.md (사용자 시각 + grep 단독 신뢰 금지)
///   feedback_exhaustive_search_before_fix.md (verbatim trace 필수)
///   feedback_external_diagnosis_first.md (외부 진단 우선, 가설 기각 매트릭스)
/// </summary>
[Trait("Tier", "Audit")]
[Trait("Category", "Interactive")]
[Trait("Nightly", "true")]
[Trait("Slow", "true")]
[Collection("GhostWin-App")]
public sealed class UIAuditDiagnostics : IClassFixture<GhostWinAppFixture>
{
    private readonly GhostWinAppFixture _fixture;
    private readonly ITestOutputHelper _output;
    private readonly string _outDir;

    public UIAuditDiagnostics(GhostWinAppFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _outDir = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "audit-output");
        Directory.CreateDirectory(_outDir);
    }

    [InteractiveFact]
    public void Run_AllScenarios()
    {
        if (!InteractiveTestGate.IsEnabled)
            return;

        // 단일 Fact 안에 모든 시나리오 sequential — UIA timeout 0x80131505 회피.
        var summary = new List<string>();

        try { ScenarioA_InitialUiaDump(summary); }
        catch (Exception ex) { summary.Add($"[A] FAIL: {ex.Message}"); }

        try { ScenarioB_SettingsPageVerify(summary); }
        catch (Exception ex) { summary.Add($"[B] FAIL: {ex.Message}"); }

        try { ScenarioC_TabFocusChain(summary); }
        catch (Exception ex) { summary.Add($"[C] FAIL: {ex.Message}"); }

        try { ScenarioD_WorkspaceContextMenu(summary); }
        catch (Exception ex) { summary.Add($"[D] FAIL: {ex.Message}"); }

        try { ScenarioE_NotifPanelContextMenu(summary); }
        catch (Exception ex) { summary.Add($"[E] FAIL: {ex.Message}"); }

        try { ScenarioF_MaximizedBottomPixels(summary); }
        catch (Exception ex) { summary.Add($"[F] FAIL: {ex.Message}"); }

        File.WriteAllLines(Path.Combine(_outDir, "summary.txt"), summary);
        foreach (var line in summary) _output.WriteLine(line);

        // Test 자체는 실패하지 않음 — 진단이 목표. 결과는 audit-output/ 에 fact.
    }

    // ─────────────────────────── Scenario A ───────────────────────────
    private void ScenarioA_InitialUiaDump(List<string> summary)
    {
        var win = _fixture.MainWindow;
        var all = win.FindAllDescendants();

        var lines = new List<string>
        {
            "ControlType\tName\tAutomationId\tIsKeyboardFocusable\tIsEnabled\tHelpText\tBounding"
        };
        var ctCounts = new Dictionary<string, int>();
        var totalButtons = 0;
        var buttonsWithToolTip = 0;
        var buttonsWithName = 0;

        foreach (var el in all)
        {
            try
            {
                var ct = el.ControlType.ToString();
                if (!ctCounts.ContainsKey(ct)) ctCounts[ct] = 0;
                ctCounts[ct]++;

                var name = el.Properties.Name.ValueOrDefault ?? "";
                var aid = el.Properties.AutomationId.ValueOrDefault ?? "";
                var help = el.Properties.HelpText.ValueOrDefault ?? "";
                var focusable = el.Properties.IsKeyboardFocusable.ValueOrDefault;
                var enabled = el.Properties.IsEnabled.ValueOrDefault;
                var rect = el.BoundingRectangle;

                if (el.ControlType == ControlType.Button)
                {
                    totalButtons++;
                    if (!string.IsNullOrEmpty(help)) buttonsWithToolTip++;
                    if (!string.IsNullOrEmpty(name)) buttonsWithName++;
                }

                lines.Add(string.Join("\t",
                    ct,
                    name.Replace("\t", " ").Replace("\n", " ").Replace("\r", " "),
                    aid,
                    focusable,
                    enabled,
                    help.Replace("\t", " ").Replace("\n", " ").Replace("\r", " "),
                    $"{rect.X},{rect.Y},{rect.Width},{rect.Height}"));
            }
            catch (Exception ex)
            {
                lines.Add($"ERR\t{ex.Message}");
            }
        }

        File.WriteAllLines(Path.Combine(_outDir, "A-initial-uia.tsv"), lines);
        summary.Add($"[A] elements={all.Length} buttons={totalButtons} " +
                    $"buttonsWithToolTip={buttonsWithToolTip} ({Pct(buttonsWithToolTip, totalButtons)}%) " +
                    $"buttonsWithName={buttonsWithName} ({Pct(buttonsWithName, totalButtons)}%)");

        var ctSummary = string.Join(", ",
            ctCounts.OrderByDescending(p => p.Value)
                    .Select(p => $"{p.Key}={p.Value}"));
        summary.Add($"[A] ControlType counts: {ctSummary}");
    }

    // ─────────────────────────── Scenario B ───────────────────────────
    private void ScenarioB_SettingsPageVerify(List<string> summary)
    {
        var win = _fixture.MainWindow;
        var settingsBtn = win.FindFirstDescendant(c => c.ByName("Open settings"));
        if (settingsBtn == null)
        {
            summary.Add("[B] SKIP: Open settings button not found");
            return;
        }

        // SettingsPage open via InvokePattern (PowerShell 와 동일 패턴)
        var asButton = settingsBtn.AsButton();
        asButton.Invoke();
        Thread.Sleep(800);

        var all = win.FindAllDescendants();
        var lines = new List<string>
        {
            "ControlType\tName\tAutomationId\tIsKeyboardFocusable\tHelpText"
        };
        var settingsControls = 0;
        var settingsWithName = 0;
        var settingsWithHelp = 0;

        foreach (var el in all)
        {
            try
            {
                var name = el.Properties.Name.ValueOrDefault ?? "";
                var aid = el.Properties.AutomationId.ValueOrDefault ?? "";
                var help = el.Properties.HelpText.ValueOrDefault ?? "";
                var focusable = el.Properties.IsKeyboardFocusable.ValueOrDefault;

                // Settings 영역만 (focusable + name 매치 또는 AutomationId E2E_)
                if (focusable && (
                    name.Contains("Theme") || name.Contains("Mica") ||
                    name.Contains("Font") || name.Contains("Cell") ||
                    name.Contains("Sidebar") || name.Contains("Notif") ||
                    name.Contains("Toast") || name.Contains("Agent") ||
                    name.Contains("Show") || name.Contains("Always") ||
                    name.Contains("Back to terminal") || name.Contains("settings JSON") ||
                    aid.StartsWith("E2E_") && aid.Contains("Settings", StringComparison.OrdinalIgnoreCase)))
                {
                    settingsControls++;
                    if (!string.IsNullOrEmpty(name)) settingsWithName++;
                    if (!string.IsNullOrEmpty(help)) settingsWithHelp++;

                    lines.Add(string.Join("\t",
                        el.ControlType.ToString(),
                        name.Replace("\t", " "),
                        aid, focusable,
                        help.Replace("\t", " ")));
                }
            }
            catch { /* skip */ }
        }

        File.WriteAllLines(Path.Combine(_outDir, "B-settings-page.tsv"), lines);
        summary.Add($"[B] settingsControls={settingsControls} " +
                    $"withName={settingsWithName} ({Pct(settingsWithName, settingsControls)}%) " +
                    $"withHelpText={settingsWithHelp} ({Pct(settingsWithHelp, settingsControls)}%)");

        // Close Settings — Back button 또는 Esc
        var backBtn = win.FindFirstDescendant(c => c.ByName("Back to terminal"));
        if (backBtn != null)
        {
            backBtn.AsButton().Invoke();
            Thread.Sleep(500);
        }
    }

    // ─────────────────────────── Scenario C ───────────────────────────
    private void ScenarioC_TabFocusChain(List<string> summary)
    {
        var win = _fixture.MainWindow;
        win.Focus();
        Thread.Sleep(300);

        var lines = new List<string>
        {
            "Step\tName\tAutomationId\tControlType"
        };

        for (int i = 0; i < 12; i++)
        {
            try
            {
                Keyboard.Press(VirtualKeyShort.TAB);
                Thread.Sleep(180);

                var focused = _fixture.Automation.FocusedElement();
                if (focused == null)
                {
                    lines.Add($"{i + 1}\t<null>\t\t");
                    continue;
                }
                var name = focused.Properties.Name.ValueOrDefault ?? "";
                var aid = focused.Properties.AutomationId.ValueOrDefault ?? "";
                var ct = focused.ControlType.ToString();
                lines.Add($"{i + 1}\t{name.Replace("\t", " ")}\t{aid}\t{ct}");
            }
            catch (Exception ex)
            {
                lines.Add($"{i + 1}\tERR\t{ex.Message}\t");
            }
        }

        File.WriteAllLines(Path.Combine(_outDir, "C-tab-focus-chain.tsv"), lines);
        summary.Add($"[C] Tab focus chain captured ({lines.Count - 1} steps)");
    }

    // ─────────────────────────── Scenario D ───────────────────────────
    private void ScenarioD_WorkspaceContextMenu(List<string> summary)
    {
        var win = _fixture.MainWindow;
        var listItem = win.FindFirstDescendant(c => c.ByControlType(ControlType.ListItem));
        if (listItem == null)
        {
            summary.Add("[D] SKIP: workspace ListItem not found");
            return;
        }

        var rect = listItem.BoundingRectangle;
        var center = new Point(
            (int)(rect.X + rect.Width / 2),
            (int)(rect.Y + rect.Height / 2));

        Mouse.Position = center;
        Mouse.Click(MouseButton.Right);
        Thread.Sleep(500);

        // ContextMenu — global (popup outside main window)
        var allElements = _fixture.Automation
            .GetDesktop()
            .FindAllChildren()
            .Where(e =>
            {
                try { return e.Properties.ProcessId.ValueOrDefault == _fixture.App.ProcessId; }
                catch { return false; }
            })
            .ToArray();

        var menuFound = 0;
        var menuItems = new List<string>();
        foreach (var el in allElements)
        {
            try
            {
                if (el.ControlType == ControlType.Menu || el.ControlType == ControlType.MenuBar)
                {
                    menuFound++;
                    var items = el.FindAllDescendants(c => c.ByControlType(ControlType.MenuItem));
                    foreach (var mi in items)
                    {
                        menuItems.Add(mi.Properties.Name.ValueOrDefault ?? "<unnamed>");
                    }
                }
            }
            catch { /* skip */ }
        }

        File.WriteAllLines(Path.Combine(_outDir, "D-workspace-contextmenu.txt"),
            new[] { $"menuFound={menuFound}", $"menuItems=[{string.Join(", ", menuItems)}]" });
        summary.Add($"[D] Workspace right-click: menuFound={menuFound} items={menuItems.Count}");

        // dismiss menu
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);
    }

    // ─────────────────────────── Scenario E ───────────────────────────
    private void ScenarioE_NotifPanelContextMenu(List<string> summary)
    {
        // NotifPanel area: column 2 in MainWindow.xaml — NotificationPanelColumn
        // 사이드바 너비 (≈ 270) + GridSplitter (8) 다음 column. 초기엔 width=0 (collapsed).
        // 우선 Notif 영역이 visible 인지 검사 — 안 보이면 SKIP (audit fact)
        var win = _fixture.MainWindow;
        var notifControls = win.FindAllDescendants(c => c.ByName("NotificationPanel"));

        if (notifControls.Length == 0)
        {
            summary.Add("[E] SKIP: NotifPanel not visible (collapsed by default)");
            File.WriteAllText(Path.Combine(_outDir, "E-notif-contextmenu.txt"),
                "NotifPanel not visible — F12 confirmation: ContextMenu未定義 + 패널 자체 collapsed.\n");
            return;
        }

        // 보이면 우클릭 시도
        var rect = notifControls[0].BoundingRectangle;
        var center = new Point(
            (int)(rect.X + rect.Width / 2),
            (int)(rect.Y + rect.Height / 2));
        Mouse.Position = center;
        Mouse.Click(MouseButton.Right);
        Thread.Sleep(500);

        var menuFound = _fixture.Automation
            .GetDesktop()
            .FindAllChildren()
            .Count(e =>
            {
                try
                {
                    return e.Properties.ProcessId.ValueOrDefault == _fixture.App.ProcessId
                        && (e.ControlType == ControlType.Menu);
                }
                catch { return false; }
            });

        summary.Add($"[E] NotifPanel right-click: menuFound={menuFound}");
        File.WriteAllText(Path.Combine(_outDir, "E-notif-contextmenu.txt"),
            $"menuFound={menuFound} (0 = F12 결함 confirm)\n");

        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);
    }

    // ─────────────────────────── Scenario F ───────────────────────────
    private void ScenarioF_MaximizedBottomPixels(List<string> summary)
    {
        var win = _fixture.MainWindow;
        var hwnd = win.Properties.NativeWindowHandle.ValueOrDefault;
        if (hwnd == IntPtr.Zero)
        {
            summary.Add("[F] SKIP: NativeWindowHandle empty");
            return;
        }

        // Maximize via Win32 ShowWindow — FlaUI 5.0 의 SetWindowVisualState 는 pattern
        // 기반이라 hidden DispatcherFrame 영향 가능. ShowWindow 가 더 robust.
        try
        {
            ShowWindow(hwnd, SW_MAXIMIZE);
            Thread.Sleep(800);
        }
        catch (Exception ex)
        {
            summary.Add($"[F] Maximize 실패: {ex.Message} — 현재 state 그대로 진행");
        }

        var rect = win.BoundingRectangle;
        var w = (int)Math.Max(1, rect.Width);
        var h = (int)Math.Max(1, rect.Height);

        // PrintWindow — virtual monitor 무시하고 윈도우 직접 캡처
        using var bmp = new Bitmap(w, h);
        using var gfx = Graphics.FromImage(bmp);
        var hdc = gfx.GetHdc();
        var ok = PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
        gfx.ReleaseHdc(hdc);

        if (!ok)
        {
            summary.Add($"[F] PrintWindow 실패 (rect {w}x{h})");
            return;
        }

        var shotPath = Path.Combine(_outDir, "F-maximized.png");
        bmp.Save(shotPath, ImageFormat.Png);

        // Bottom row pixel sample — terminal 영역은 right-half (sidebar 270px 빼고)
        var samples = new List<string>();
        var darkCount = 0;
        var black000Count = 0;
        var theme1E1E2ECount = 0;
        for (int row = h - 10; row >= h - 40; row -= 4)
        {
            for (int col = (int)(w * 0.6); col < w - 10; col += 200)
            {
                if (col < 0 || col >= w || row < 0 || row >= h) continue;
                var px = bmp.GetPixel(col, row);
                samples.Add($"({col},{row}) #{px.R:X2}{px.G:X2}{px.B:X2}");
                if (px.R < 0x30 && px.G < 0x30 && px.B < 0x40) darkCount++;
                if (px.R == 0 && px.G == 0 && px.B == 0) black000Count++;
                if (px.R == 0x1E && px.G == 0x1E && px.B == 0x2E) theme1E1E2ECount++;
            }
        }
        File.WriteAllLines(Path.Combine(_outDir, "F-bottom-pixels.txt"), samples);
        summary.Add($"[F] PrintWindow OK ({w}x{h}). bottom samples={samples.Count} " +
                    $"darkish={darkCount} black#000000={black000Count} theme#1E1E2E={theme1E1E2ECount}");
    }

    private static int Pct(int n, int total) =>
        total == 0 ? 0 : (int)Math.Round(100.0 * n / total);

    // ── Win32 PrintWindow + ShowWindow ──
    private const uint PW_CLIENTONLY = 1;
    private const uint PW_RENDERFULLCONTENT = 2;
    private const int SW_MAXIMIZE = 3;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);
}
