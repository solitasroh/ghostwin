using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.App.Controls;
using GhostWin.App.Diagnostics;
using GhostWin.App.Input;
using GhostWin.App.ViewModels;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using GhostWin.Interop;

namespace GhostWin.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private IEngineService _engine = null!;
    private ISessionManager _sessionManager = null!;
    private IWorkspaceService _workspaceService = null!;
    private bool _shuttingDown;
    private TextCompositionPreviewController? _compositionPreview;
    private bool _suppressCompositionBackspaceBubble;
    private readonly MouseCursorOracleState _mouseCursorOracle = new();

    // ──────────────────────────────────────────────────────────
    // M-11 followup: PEB CWD polling timer (2026-04-15)
    // WinUI3→WPF 이행 시 winui_app.cpp 의 폴링 타이머가 사라짐 → cmd.exe / 기본
    // PowerShell 처럼 OSC 7 안 보내는 쉘에서 cwd 가 비어 옴. 이 타이머가 1초마다
    // _engine.PollTitles() 호출 → native SessionManager::poll_titles_and_cwd() 가
    // 모든 활성 세션의 PEB 를 읽어 변경 시 OnCwdChanged 콜백 발사.
    // 주기 1초: 사용자 체감 지연 < cd 입력 후 한 호흡, GUI 부담 없음 (PEB 읽기 ~수 μs/세션).
    // ──────────────────────────────────────────────────────────
    private System.Windows.Threading.DispatcherTimer? _cwdPollTimer;
    private int _gitPollCounter;
    // _initialHost removed in first-pane-render-failure Option B.
    // PaneContainerControl is now the single owner of all host lifecycles —
    // first pane is created by BuildElement via the normal
    // WorkspaceActivatedMessage -> SwitchToWorkspace -> BuildGrid path, same
    // code path as split panes. Eliminates the HostReady subscribe race that
    // caused the first pane to render blank.

    public MainWindow()
    {
        InitializeComponent();

        var vm = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        DataContext = vm;

        RestoreWindowBounds();

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;
    }

    /// <summary>
    /// Runtime DPI change handler (monitor move, scale setting change).
    /// Invokes the unified scale pipeline via IEngineService.UpdateCellMetrics —
    /// atlas rebuild + per-surface cols/rows recompute + per-session
    /// resize_pty_only + vt_resize_locked. See dpi-scaling-integration cycle.
    /// </summary>
    // M-16-B P0v3 (2026-04-29): restore CaptionHeight after wpfui overwrites it,
    // but PRESERVE GlassFrameThickness=-1 so Mica still works.
    //
    // Background:
    //  - Wpf.Ui.Controls.FluentWindow.OnExtendsContentIntoTitleBarChanged
    //    (lepoco/wpfui commit 38e888a751, source of the 3.1.1 NuGet package)
    //    runs inside base.OnSourceInitialized and calls
    //      WindowChrome.SetWindowChrome(this, new WindowChrome {
    //          CaptionHeight = 0,                       // drag-able region
    //          GlassFrameThickness = new Thickness(-1), // glass extends to all client area
    //          ResizeBorderThickness = new Thickness(4),
    //          CornerRadius = default,
    //          UseAeroCaptionButtons = false,
    //      })
    //  - CaptionHeight=0 zeros out the title bar drag region — bad for our
    //    custom caption row.
    //  - GlassFrameThickness=-1 is REQUIRED for Mica/Acrylic. The Microsoft
    //    docs for System.Windows.Shell.WindowChrome.GlassFrameThickness
    //    explicitly state: "To make a custom window that does not have a
    //    glass frame, set this thickness to a uniform value of 0. ... To
    //    extend the glass frame to cover the entire window, set the
    //    GlassFrameThickness property to a negative value on any side."
    //    DWM only composites Mica into the glass frame area. Setting
    //    GlassFrameThickness=0 disables the glass frame and Mica is invisible
    //    even though DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE,
    //    DWMSBT_MAINWINDOW) succeeded.
    //  - The previous P0v2 attempt used GlassFrameThickness=new Thickness(0)
    //    which fixed CaptionHeight (Step 1-5/1-6 passed in user PC test) but
    //    disabled the glass frame and therefore disabled Mica (Step 2-2..2-7
    //    still failed).
    //
    // Fix: copy wpfui's WindowChrome but raise CaptionHeight to 32 and
    // ResizeBorderThickness to 8. GlassFrameThickness stays at -1 so Mica
    // composes through the whole client area. WindowCornerPreference="Round"
    // on the FluentWindow handles rounded corners independently.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        System.Windows.Shell.WindowChrome.SetWindowChrome(this,
            new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 32,
                ResizeBorderThickness = new Thickness(8),
                GlassFrameThickness = new Thickness(-1),  // ← P0v3 fix: Mica requires -1
                CornerRadius = default,
                UseAeroCaptionButtons = false,
            });

        // M-16-B P0v4 (2026-04-29): bypass wpfui WindowBackdrop wrapper and call
        // DwmSetWindowAttribute directly + force HwndSource.CompositionTarget
        // .BackgroundColor = Transparent. Diagnostic logging at every step so
        // the next failed run gives a precise root cause instead of guesses.
        ApplyMicaDirectly();
    }

    /// <summary>
    /// P0v4: direct DWM Mica application + diagnostic logging.
    /// All wpfui state-machine steps are bypassed and replicated by hand so
    /// any silent failure points are exposed.
    /// </summary>
    private void ApplyMicaDirectly()
    {
        var settings = Ioc.Default.GetService<ISettingsService>()?.Current;
        bool useMica = settings?.Titlebar.UseMica ?? false;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);

        Debug.WriteLine($"[Mica] settings.UseMica={useMica}");
        Debug.WriteLine($"[Mica] settings.Appearance={settings?.Appearance}");
        Debug.WriteLine($"[Mica] hwnd=0x{hwnd.ToInt64():X}");
        Debug.WriteLine($"[Mica] OS={System.Environment.OSVersion}");
        Debug.WriteLine($"[Mica] IsGlassEnabled={SystemParameters.IsGlassEnabled}");
        Debug.WriteLine($"[Mica] WindowStyle={WindowStyle}");
        Debug.WriteLine($"[Mica] AllowsTransparency={AllowsTransparency}");
        Debug.WriteLine($"[Mica] Background={(Background?.ToString() ?? "<null>")}");
        Debug.WriteLine($"[Mica] HwndSource={(src is null ? "<null>" : "ok")}");

        if (hwnd == IntPtr.Zero)
        {
            Debug.WriteLine("[Mica] ABORT: hwnd is zero");
            return;
        }

        // 1. Mark client area as transparent in WPF compositor — this is the
        //    same call the wpfui RemoveBackground helper makes, replicated so
        //    we know it actually ran. Without this, DWM composites Mica
        //    underneath an opaque DirectComposition surface and the user
        //    sees nothing.
        if (src?.CompositionTarget is { } target)
        {
            Debug.WriteLine($"[Mica] CompositionTarget.BackgroundColor BEFORE={target.BackgroundColor}");
            target.BackgroundColor = System.Windows.Media.Colors.Transparent;
            Debug.WriteLine($"[Mica] CompositionTarget.BackgroundColor AFTER ={target.BackgroundColor}");
        }
        else
        {
            Debug.WriteLine("[Mica] WARN: CompositionTarget is null");
        }

        // 2. Tell DWM about the desired backdrop directly. Win11 22H2+ uses
        //    DWMWA_SYSTEMBACKDROP_TYPE=38 with DWMSBT_MAINWINDOW=2 for Mica.
        //    The PreserveSig=false signature surfaces an HRESULT exception
        //    if DWM rejects the request (e.g. on older builds), so failures
        //    are no longer silent.
        try
        {
            // P0 final: use MicaAlt (DWMSBT_TABBEDWINDOW=4) instead of Mica.
            // MicaAlt blends in more system accent color and is less dependent
            // on the user's wallpaper tone, so the chrome shows visible
            // tinting even when the user uses a dark/single-color wallpaper.
            // The user-facing setting is still labeled "Mica backdrop" because
            // both options are part of the Mica family in Win11 22H2+.
            int backdrop = useMica ? DWMSBT_TABBEDWINDOW : DWMSBT_NONE;
            int hr = DwmSetWindowAttribute(
                hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            Debug.WriteLine($"[Mica] DwmSetWindowAttribute(SYSTEMBACKDROP_TYPE={backdrop}) hr=0x{hr:X8}");

            // Win11 22H2 의 dark-mode caption — Mica + Light theme 시 caption
            // text 가 어두운 OS chrome 위에서 안 보이지 않도록 동기화.
            int useDark = settings?.Appearance == "light" ? 0 : 1;
            int hr2 = DwmSetWindowAttribute(
                hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            Debug.WriteLine($"[Mica] DwmSetWindowAttribute(USE_IMMERSIVE_DARK_MODE={useDark}) hr=0x{hr2:X8}");
        }
        catch (System.Exception ex)
        {
            Debug.WriteLine($"[Mica] DwmSetWindowAttribute threw: {ex.Message}");
        }
    }

    // DWM constants — Microsoft Learn:
    // https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_AUTO = 0;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;     // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    private const int DWMSBT_TABBEDWINDOW = 4;    // MicaAlt

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    // P3 (2026-04-29) — file-backed a11y trace. Debug.WriteLine is invisible
    // outside of an attached debugger, but we need the trace from a normal F5
    // run. Append to %TEMP%\ghostwin-a11y.log so the agent can read it back
    // without launching VS.
    private static readonly string A11yLogPath = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ghostwin-a11y.log");

    private static void LogA11y(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [A11y] {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        try { System.IO.File.AppendAllText(A11yLogPath, line + System.Environment.NewLine); }
        catch { /* logging must never crash the UI thread */ }
    }

    /// <summary>
    /// P3: dump every visible Focusable+IsTabStop element below <paramref name="root"/>.
    /// Visual + logical tree both walked because WPF Tab routing follows the
    /// visual tree for default order. This identifies whether the Sidebar
    /// items, the "+" button, and the Settings gear are present and reachable
    /// at all. If they are listed but Tab still won't reach them, the issue
    /// is Tab-routing (not Focusable wiring).
    /// </summary>
    private static void DumpFocusables(DependencyObject root, int depth)
    {
        if (depth > 32) return;
        var prefix = new string(' ', depth * 2);
        try
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is UIElement ui && ui.Focusable && ui.IsVisible)
                {
                    var name = (child as FrameworkElement)?.Name ?? string.Empty;
                    var ctrl = child as System.Windows.Controls.Control;
                    var tabIndex = System.Windows.Input.KeyboardNavigation.GetTabIndex(child as DependencyObject);
                    var isTabStop = ctrl?.IsTabStop;
                    var automationId = (string?)child.GetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty);
                    LogA11y($"{prefix}{child.GetType().Name} name='{name}' tabIndex={tabIndex} isTabStop={isTabStop} automationId='{automationId}'");
                }
                DumpFocusables(child, depth + 1);
            }
        }
        catch { /* swallow — diagnostic walk must never crash */ }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        // base.OnDpiChanged accepts the proposed window rect from WM_DPICHANGED,
        // which MSDN mandates be honored so Windows can keep the cursor anchor
        // and avoid stale-size flash.
        base.OnDpiChanged(oldDpi, newDpi);

        if (_engine == null || _shuttingDown) return;

        var settings = Ioc.Default.GetService<ISettingsService>();
        var font = settings?.Current.Terminal.Font ?? new FontSettings();

        int rc = _engine.UpdateCellMetrics(
            fontSizePt: (float)font.Size,
            fontFamily: font.Family,
            dpiScale: (float)newDpi.DpiScaleX,
            cellWidthScale: (float)font.CellWidthScale,
            cellHeightScale: (float)font.CellHeightScale,
            zoom: 1.0f);

        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "dpi-changed",
            ("old", oldDpi.DpiScaleX), ("new", newDpi.DpiScaleX), ("rc", rc));
    }

    // M-16-B FR-09/10 (Day 5): animate NotificationPanelColumn.Width via
    // GridLengthAnimationCustom 200ms CubicEase EaseOut. Open uses the VM's
    // current NotificationPanelWidth (default 280, possibly user-customised
    // via GridSplitter drag), close goes back to 0.
    private void AnimateNotificationPanel(bool open)
    {
        if (NotificationPanelColumn == null) return;

        var fromWidth = NotificationPanelColumn.Width;
        var targetPx = open
            ? (DataContext is ViewModels.MainWindowViewModel vm && vm.NotificationPanelWidth > 0
                ? vm.NotificationPanelWidth : 280)
            : 0;
        var targetLength = new System.Windows.GridLength(targetPx);

        var animation = new Animations.GridLengthAnimationCustom
        {
            From = fromWidth,
            To = targetLength,
            Duration = new System.Windows.Duration(System.TimeSpan.FromMilliseconds(200)),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };

        // P2 (2026-04-29) — verification 4-9 fail: NotificationPanel
        // GridSplitter 가 드래그 안 됨. WPF BeginAnimation 의 default
        // FillBehavior=HoldEnd 가 animation 종료 후에도 animated value 를 hold
        // 해서 GridSplitter 의 직접 ColumnDefinition.Width 변경을 차단.
        // 해결: animation Completed 시 BeginAnimation(prop, null) 로
        // animation clear + ColumnDefinition.Width 직접 set → splitter
        // 가 다시 width 변경 가능.
        animation.Completed += (_, _) =>
        {
            NotificationPanelColumn.BeginAnimation(
                System.Windows.Controls.ColumnDefinition.WidthProperty, null);
            NotificationPanelColumn.Width = targetLength;
        };

        NotificationPanelColumn.BeginAnimation(
            System.Windows.Controls.ColumnDefinition.WidthProperty, animation);
    }

    // M-16-B FR-11 (P3 follow-up 2026-04-29): Settings page opacity fade.
    // Visibility binding was removed in MainWindow.xaml so code-behind can
    // sequence Visible↔Collapsed correctly with the opacity animation.
    //   open=true:  set Visibility=Visible, animate Opacity 0→1 (200ms)
    //   open=false: animate Opacity 1→0 (200ms), then Visibility=Collapsed
    // Same easing curve (CubicEase EaseOut) as the NotificationPanel slide
    // for consistency with the cmux pattern.
    private void AnimateSettingsPage(bool open)
    {
        if (SettingsPage == null) return;

        if (open)
        {
            SettingsPage.Visibility = Visibility.Visible;
        }

        var animation = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = open ? 0.0 : SettingsPage.Opacity,
            To = open ? 1.0 : 0.0,
            Duration = new System.Windows.Duration(System.TimeSpan.FromMilliseconds(200)),
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
            }
        };

        animation.Completed += (_, _) =>
        {
            SettingsPage.BeginAnimation(UIElement.OpacityProperty, null);
            SettingsPage.Opacity = open ? 1.0 : 0.0;
            if (!open)
            {
                SettingsPage.Visibility = Visibility.Collapsed;
            }
        };

        SettingsPage.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    // M-16-B FR-06/07 (Day 4): GridSplitter drag → push the new ColumnDefinition
    // width into MainWindowViewModel.SidebarWidth (OneWay binding does not flow
    // back). MainWindowViewModel partial handler then persists to settings via
    // _settingsService.Save() which self-suppresses the file watcher (M-12).
    private void OnSidebarSplitterDragCompleted(
        object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && SidebarColumn?.Width.IsAbsolute == true)
        {
            var clamped = (int)System.Math.Round(SidebarColumn.Width.Value);
            vm.SidebarWidth = clamped;
        }
    }

    private void OnNotificationPanelSplitterDragCompleted(
        object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && NotificationPanelColumn?.Width.IsAbsolute == true)
        {
            var clamped = (int)System.Math.Round(NotificationPanelColumn.Width.Value);
            // Only persist when the panel is open; closing animates back to 0.
            if (vm.IsNotificationPanelOpen)
                vm.NotificationPanelWidth = clamped;
        }
    }

    // M-16-B FR-11/12 (Day 3): BorderThickness=8 manual inset removed.
    // Tech Debt #24 (legacy WindowStyle=None + WindowChrome) was compensating
    // for the maximized window pushing ~8px beyond the working area. With
    // FluentWindow + ClientAreaBorder template (Day 1 FR-01) the chrome layout
    // is handled by the template itself, so the manual BorderThickness toggle
    // is redundant on Win11 22H2+. Verification is deferred to Day 8 user PC
    // visual check across DPI 100/125/150/175/200% (R2 in Plan §5). If a gap
    // reappears, the fallback is to re-introduce BorderThickness=8 here.
    private void OnWindowStateChanged(object? sender, System.EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            if (MaxRestoreIcon != null)
            {
                // Two overlapping rectangles = Restore glyph
                MaxRestoreIcon.Data = System.Windows.Media.Geometry.Parse(
                    "M 2,0 H 10 V 8 H 8 V 10 H 0 V 2 H 2 Z");
            }
        }
        else
        {
            if (MaxRestoreIcon != null)
            {
                // Single rectangle = Maximize glyph
                MaxRestoreIcon.Data = System.Windows.Media.Geometry.Parse(
                    "M 0,0 H 10 V 10 H 0 Z");
            }
        }
    }

    private void RestoreWindowBounds()
    {
        var settings = Ioc.Default.GetRequiredService<ISettingsService>();
        var win = settings.Current.Window;

        Width = win.Width;
        Height = win.Height;

        if (!double.IsNaN(win.Top) && !double.IsNaN(win.Left))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Top = win.Top;
            Left = win.Left;

            // 모니터 경계 벗어남 방지
            var screen = SystemParameters.WorkArea;
            if (Left + Width < 0 || Left > screen.Right ||
                Top + Height < 0 || Top > screen.Bottom)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        if (win.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var settings = Ioc.Default.GetService<ISettingsService>();
        if (settings == null) return;

        var win = settings.Current.Window;
        win.IsMaximized = WindowState == WindowState.Maximized;

        if (WindowState == WindowState.Normal)
        {
            win.Width = Width;
            win.Height = Height;
            win.Top = Top;
            win.Left = Left;
        }

        settings.Save();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // P3 (2026-04-29) — focus diagnostics + production fix.
        //
        // Diagnostic confirmed (focusable scan): Window.Loaded sees
        // FocusedElement=null. WPF Tab routing without an initial focus is
        // unpredictable and the visual-tree-first heuristic was sending
        // keyboard navigation into the HwndHost (PaneContainerControl), where
        // the Win32 child window swallowed WM_KEYDOWN. The same scan also
        // proved Sidebar buttons are wired (TabIndex 100/101) and reachable.
        //
        // Production fix: anchor first focus to SidebarNewWorkspaceButton
        // (TabIndex=100) at Loaded priority. cmux / VS Code follow the same
        // policy — Tab walks the chrome (UI) ring while terminal is entered
        // by mouse click. PaneContainerControl now has IsTabStop=False +
        // KeyboardNavigation.TabNavigation=None (set in MainWindow.xaml) so
        // Tab cannot fall into the airspace.
        LogA11y("Loaded.entry");
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (SidebarNewWorkspaceButton is { IsLoaded: true } anchor)
            {
                System.Windows.Input.Keyboard.Focus(anchor);
                LogA11y($"Loaded.anchorFocus -> SidebarNewWorkspaceButton (TabIndex={System.Windows.Input.KeyboardNavigation.GetTabIndex(anchor)})");
            }

            var f = System.Windows.Input.Keyboard.FocusedElement;
            LogA11y($"Loaded.firstFocus | type={f?.GetType().Name ?? "<null>"} | name={(f as FrameworkElement)?.Name ?? string.Empty}");
            LogA11y("--- logical-tree focusable scan ---");
            DumpFocusables(this, depth: 0);
            LogA11y("--- end scan ---");
        });

        MouseCursorOracleProbe.Updated += OnMouseCursorOracleUpdated;
        _engine = Ioc.Default.GetRequiredService<IEngineService>();
        _sessionManager = Ioc.Default.GetRequiredService<ISessionManager>();
        _workspaceService = Ioc.Default.GetRequiredService<IWorkspaceService>();
        _compositionPreview = new TextCompositionPreviewController(
            getActiveSessionId: () => _sessionManager.ActiveSessionId,
            applyPreview: (sessionId, preview) =>
            {
                if (_engine is { IsInitialized: true })
                    _engine.SetComposition(sessionId, preview.Text, preview.CaretOffset, preview.IsActive);
            });

        var callbackContext = new GwCallbackContext
        {
            OnSessionCreated = id => { },
            OnSessionClosed = id => {
                Debug.WriteLine("${_sessionManager.Sessions.Count()}");
            },
            OnSessionActivated = id => { },
            OnTitleChanged = (id, title) =>
            {
                if (_shuttingDown) return;
                _sessionManager.UpdateTitle(id, title);
                (_sessionManager as Services.SessionManager)?.NotifySessionOutput(id);
            },
            OnCwdChanged = (id, cwd) =>
            {
                if (_shuttingDown) return;
                _sessionManager.UpdateCwd(id, cwd);
                (_sessionManager as Services.SessionManager)?.NotifySessionOutput(id);
            },
            OnMouseShape = (id, shape) =>
            {
                if (_shuttingDown) return;
                _sessionManager.UpdateMouseCursorShape(id, shape);
                PaneContainer.ApplyMouseCursorShape(id, shape);
            },
            OnOscNotify = (id, title, body) =>
            {
                if (_shuttingDown) return;
                Ioc.Default.GetService<IOscNotificationService>()?.HandleOscEvent(id, title, body);
            },
            OnChildExit = (id, code) =>
            {
                if (_shuttingDown) return;
                if (_sessionManager is Services.SessionManager sm)
                    sm.NotifyChildExit(id, code);
                _sessionManager.CloseSession(id);
                if (_sessionManager.Sessions.Count == 0)
                    this.Close();
            },
            OnRenderDone = null,
        };

        _engine.Initialize(callbackContext);

        if (!_engine.IsInitialized) return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            InitializeRenderer);
    }

    private void InitializeRenderer()
    {
        // #1 irenderer-enter — runs on the UI thread via Dispatcher.BeginInvoke(Loaded).
        // ui=1 is normal. In Option B all work is done synchronously inside this
        // single callback — no nested Dispatcher.BeginInvoke, which eliminates
        // the priority-race window where BuildWindowCore's Normal(9) HostReady
        // fire could drain before a nested Loaded(6) AdoptInitialHost callback.
        //
        // ⚠️ DO NOT add nested Dispatcher.BeginInvoke / await Dispatcher.Yield
        // / Task.Run continuations inside this method (between PaneContainer.
        // Initialize and the CreateWorkspace call). first-pane-render-failure
        // Option B (design.md §0.1 C-7/C-8, §4.2 implementation order) requires
        // the entire chain — Initialize → RenderInit → RenderStart → CreateWorkspace
        // — to run synchronously on a single Dispatcher tick so that
        // PaneContainer is registered with the messenger *before* CreateWorkspace
        // publishes WorkspaceActivatedMessage. Any Dispatcher yield in between
        // re-opens the HostReady race window (HC-3) by allowing layout-pass
        // Render(7) callbacks to drain BuildWindowCore's Normal(9) enqueue out
        // of order. If you need async work, defer it to *after* CreateWorkspace
        // returns.
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "irenderer-enter",
            ("dispatcher_thread", Application.Current?.Dispatcher.CheckAccess() ?? false));

        // HC-4: PaneContainer.Initialize subscribes to WeakReferenceMessenger
        // synchronously (no longer deferred to Loaded event). This guarantees
        // that WorkspaceActivatedMessage published by CreateWorkspace below is
        // delivered and Receive()/SwitchToWorkspace/BuildGrid/BuildElement runs,
        // which creates the first TerminalHostControl with HostReady already
        // subscribed — atomically, same code path as split panes.
        PaneContainer.Initialize(_workspaceService);

        // Q-A4: hwnd-less RenderInit. gw_render_init now accepts NULL hwnd via
        // the new RendererConfig.allow_null_hwnd flag and skips the bootstrap
        // swapchain entirely (SurfaceManager creates per-pane swapchains later
        // via bind_surface). Dummy 100x100 size — the atlas recomputes real
        // cols/rows using font-dependent cell size.
        var dpiScale = (float)VisualTreeHelper.GetDpi(this).DpiScaleX;
        var fontSettings = Ioc.Default.GetRequiredService<ISettingsService>().Current.Terminal.Font;
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "renderinit-call",
            ("hwnd", IntPtr.Zero), ("w", 100), ("h", 100), ("dpi", dpiScale),
            ("font", fontSettings.Family), ("size", fontSettings.Size));
        int renderInitRc = _engine.RenderInit(IntPtr.Zero, 100, 100,
            (float)fontSettings.Size, fontSettings.Family, dpiScale);
        RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "renderinit-return",
            ("rc", renderInitRc));
        if (renderInitRc != 0) return;

        _engine.RenderSetClearColor(0x1E1E2E);

        // WPF already owns IME composition/finalized text for this shell.
        // Keeping the native hidden TSF path focused at the same time creates
        // a second composition source, which can resurrect stale preview text
        // after Backspace (for example "한" -> "하" -> "ㅎ" -> empty).
        //
        // For the WPF host we therefore use a single IME pipeline:
        //   WPF TextComposition events -> TextCompositionPreviewController
        //   -> engine composition state
        //
        // Native TSF remains in the engine for non-WPF hosts, but is not
        // attached/focused from the WPF shell.

        _engine.RenderStart();

        // ──────────────────────────────────────────────────────────
        // M-11 Session Restore — 복원 경로 통합 (Design §15 Step 7, C-1 패치)
        //
        // 가드 원칙: App.OnStartup 에서 이미 복원/폴백 시도가 있었을 수 있지만,
        //   엔진 Initialize 는 이 시점에서 완료되므로 "실제 세션 생성" 은 여기서 수행.
        //   Workspaces.Count == 0 가드는 미래에 OnStartup 이 직접 생성하게 될 경우 대비.
        //
        // 이중 생성 차단:
        //   - pending 스냅샷이 있으면 RestoreFromSnapshot (복수 W + 복수 pane + CWD)
        //   - 없으면 기존 CreateWorkspace (첫 실행)
        //   - 복원 실패 시 CreateWorkspace 폴백 (App.WriteCrashLog 기록)
        //
        // 기존 경로 호환:
        //   RestoreFromSnapshot 는 내부에서 ActivateWorkspace 를 호출 →
        //   WorkspaceActivatedMessage 발행 → PaneContainerControl.Receive 가
        //   SwitchToWorkspace -> BuildGrid -> BuildElement 로 TerminalHostControl 생성
        //   (CreateWorkspace 와 동일 경로).
        // ──────────────────────────────────────────────────────────
        var pending = App.PendingRestoreSnapshot;
        if (_workspaceService.Workspaces.Count == 0)
        {
            if (pending is not null && pending.Workspaces.Count > 0)
            {
                try
                {
                    _workspaceService.RestoreFromSnapshot(pending);
                    RenderDiag.LogEvent(RenderDiag.LEVEL_LIFECYCLE, "session-restored",
                        ("workspaces", pending.Workspaces.Count),
                        ("active_idx", pending.ActiveWorkspaceIndex));
                }
                catch (Exception ex)
                {
                    // 복원 실패 — 기존 경로 폴백. 데이터 유실 방지용 crash log.
                    App.WriteCrashLog("RestoreFromSnapshot", ex);
                    _workspaceService.CreateWorkspace();
                }
            }
            else
            {
                // 신규 실행 (session.json 미존재) 또는 빈 스냅샷 — 기존 경로.
                _workspaceService.CreateWorkspace();
            }
        }

        // ──────────────────────────────────────────────────────────
        // M-11 followup: PEB CWD polling 타이머 시작
        // 1초 주기로 native poll_titles_and_cwd() 호출 → cwd 변경 감지 → fire_cwd_event
        // → C# OnCwdChanged → SessionManager.UpdateCwd → SessionInfo.Cwd 갱신
        // → 다음 SessionSnapshot 저장 시 cwd 가 session.json 에 기록됨.
        // OSC 7 미설정 PowerShell, cmd.exe 모두 자동 동작.
        // ──────────────────────────────────────────────────────────
        _cwdPollTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _cwdPollTimer.Tick += (_, _) =>
        {
            if (_shuttingDown || _engine == null) return;
            try { _engine.PollTitles(); }
            catch (Exception ex) { App.WriteCrashLog("CwdPollTimer.Tick", ex); }
            try { (_sessionManager as Services.SessionManager)?.TickAgentStateTimer(); }
            catch (Exception ex) { App.WriteCrashLog("AgentStateTimer.Tick", ex); }
            // Phase 6-C: git branch polling every 5 seconds
            _gitPollCounter++;
            if (_gitPollCounter % 5 == 0)
            {
                try { (_sessionManager as Services.SessionManager)?.TickGitStatus(); }
                catch (Exception ex) { App.WriteCrashLog("GitPollTimer.Tick", ex); }
            }
        };
        _cwdPollTimer.Start();

        PreviewKeyDown += OnTerminalKeyDown;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, OnTerminalTextComposition);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, OnTerminalTextComposition);
        PreviewTextInput += OnTerminalTextInput;

        // M-12: 설정 페이지 닫힐 때 터미널 포커스 복원
        // MainWindowViewModel.IsSettingsOpen 변경을 감시하여 false가 되면 포커스 복원
        // M-16-B FR-09/10 (Day 5): IsNotificationPanelOpen change → animate
        // NotificationPanelColumn.Width via GridLengthAnimationCustom 200ms.
        if (DataContext is ViewModels.MainWindowViewModel mwvm)
        {
            mwvm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewModels.MainWindowViewModel.IsSettingsOpen))
                {
                    AnimateSettingsPage(mwvm.IsSettingsOpen);

                    if (mwvm.IsSettingsOpen == false)
                    {
                        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                            new Action(() => PaneContainer.GetFocusedHost()?.Focus()));
                    }
                }

                if (e.PropertyName == nameof(ViewModels.MainWindowViewModel.IsNotificationPanelOpen))
                {
                    AnimateNotificationPanel(mwvm.IsNotificationPanelOpen);
                }
            };
        }

        // Bubble-phase fallback for scenario A/D — child HwndHost can consume
        // WM_KEYDOWN before WPF tunnelling reaches the Window. See
        // docs/02-design/features/e2e-headless-input.design.md §3.1.2.
        AddHandler(KeyDownEvent,
                   new KeyEventHandler(OnTerminalKeyDownBubbled),
                   handledEventsToo: true);
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shuttingDown) return;
        e.Cancel = true;
        _shuttingDown = true;
        MouseCursorOracleProbe.Updated -= OnMouseCursorOracleUpdated;

        // ★ 사용자 체감 즉시 닫힘 — 윈도우를 먼저 숨긴 후 정리 진행
        this.Visibility = Visibility.Hidden;

        // M-11 followup: CWD 폴링 타이머 즉시 중단 (Snapshot 저장 직전)
        // 마지막 한 번 동기 호출로 최신 cwd 반영 후 정지.
        try
        {
            _cwdPollTimer?.Stop();
            _engine?.PollTitles();  // 종료 직전 마지막 cwd 캡쳐
        }
        catch (Exception ex) { App.WriteCrashLog("CwdPollTimer.Stop", ex); }

        // 1. UI 리소스 정리 (엔진보다 먼저)
        try { SaveWindowBounds(); }
        catch (Exception ex)
        {
            App.WriteCrashLog("SaveWindowBounds", ex);
        }

        // Phase 6-C: stop Named Pipe server before engine teardown
        try
        {
            var hookSrv = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
                .GetService<Core.Interfaces.IHookPipeServer>();
            if (hookSrv != null)
                await hookSrv.StopAsync().WaitAsync(TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex) { App.WriteCrashLog("HookPipeServer.Stop", ex); }

        // ──────────────────────────────────────────────────────────
        // M-11 Session Restore — 최종 저장 + 주기 타이머 중단 (Design §7, §15 Step 9)
        //
        // 실행 순서:
        //   1) UI 스레드에서 Collect (PaneLayoutService.Root / WorkspaceInfo 동기 읽기)
        //   2) SaveAsync (원자 쓰기, 100ms 타임아웃 — NFR-1)
        //   3) StopAsync (주기 타이머 중단 + 워커 task join)
        //   4) 엔진 DetachCallbacks 및 정리는 이후 기존 로직 유지
        //
        // 타임아웃: NFR-1 (<100ms) 근거. 종료 경로 2 초 전체 버짓 안에 여유.
        // 예외 처리: 실패 시 crash log 만 남기고 진행 (종료 경로 블록 금지).
        // ──────────────────────────────────────────────────────────
        try
        {
            var snapshotSvc = Ioc.Default.GetService<ISessionSnapshotService>();
            var wsSvc       = Ioc.Default.GetService<IWorkspaceService>();
            if (snapshotSvc != null && wsSvc != null && _sessionManager != null)
            {
                var finalSnapshot = GhostWin.Services.SessionSnapshotMapper.Collect(wsSvc, _sessionManager);
                await snapshotSvc.SaveAsync(finalSnapshot)
                                  .WaitAsync(TimeSpan.FromMilliseconds(100));
                await snapshotSvc.StopAsync();
            }
        }
        catch (TimeoutException tex)
        {
            App.WriteCrashLog("SessionSnapshot.SaveAsync timeout", tex);
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("SessionSnapshot shutdown", ex);
        }

        var settings = Ioc.Default.GetService<ISettingsService>();
        (settings as IDisposable)?.Dispose();

        // 2. 엔진 정리 + 프로세스 종료 (WT 패턴)
        //
        // 재진입 crash 방지 순서:
        //   (a) DetachCallbacks — C++ 콜백 포인터 NULL + C# context/dispatcher null.
        //       이 시점 이후 네이티브 I/O thread의 on_exit fire는 양쪽 모두에서
        //       early-return. Dispatcher 큐에 CloseSession이 큐잉되지 않음.
        //   (b) Dispatcher 큐 flush — (a) 이전에 이미 큐잉된 콜백이 있을 수
        //       있으므로, 한 번 Yield하여 대기 중인 BeginInvoke 항목들을
        //       _shuttingDown 가드 하에서 안전하게 drain.
        //   (c) Task.Run(engine.Dispose) — 콜백이 완전히 차단된 상태에서
        //       gw_engine_destroy 실행. I/O thread join 중 fire되는 on_exit도
        //       C++ NULL check에서 drop됨.
        //
        // gw_engine_destroy 후 WPF/CLR finalizer가 해제된 네이티브 메모리에
        // 접근하므로 Environment.Exit로 즉시 종료해야 함. WT도 동일 패턴.
        var engineRef = _engine;
        engineRef?.DetachCallbacks();
        // Drain 이미 큐잉된 BeginInvoke 항목 — Background 우선순위로 한 번 양보.
        await this.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);

        _engine = null!;
        _ = Task.Run(() =>
        {
            Application.Current?.Dispatcher.Invoke(                () => Application.Current.Shutdown());
            (engineRef as IDisposable)?.Dispose();
        });

        // 3. 타임아웃 fallback — ConPty I/O 무한 블로킹 시
        await Task.Delay(TimeSpan.FromSeconds(2));
        App.WriteCrashLog("shutdown", new TimeoutException(
            "engine.Dispose blocked >2s (ConPty I/O hang)"));
        Environment.Exit(0);
    }

    // Caption button handlers
    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaxRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e)
        => Close();

    private void ShowCommandPalette()
    {
        var vm = DataContext as ViewModels.MainWindowViewModel;
        if (vm == null) return;

        var commands = new List<Core.Models.CommandInfo>
        {
            new("CreateWorkspace", "New workspace", "Ctrl+T",
                () => _workspaceService.CreateWorkspace()),
            new("CloseWorkspace", "Close workspace", "Ctrl+W",
                () => { if (_workspaceService.ActiveWorkspaceId is {} id) _workspaceService.CloseWorkspace(id); }),
            new("SplitVertical", "Split vertical", "Alt+V",
                () => _workspaceService.ActivePaneLayout?.SplitFocused(Core.Models.SplitOrientation.Vertical)),
            new("SplitHorizontal", "Split horizontal", "Alt+H",
                () => _workspaceService.ActivePaneLayout?.SplitFocused(Core.Models.SplitOrientation.Horizontal)),
            new("ToggleNotificationPanel", "Toggle notification panel", "Ctrl+Shift+I",
                () => vm.ToggleNotificationPanelCommand.Execute(null)),
            new("JumpToUnread", "Jump to unread notification", "Ctrl+Shift+U",
                () => vm.JumpToUnreadCommand.Execute(null)),
            new("OpenSettings", "Open settings", "Ctrl+,",
                () => vm.OpenSettingsCommand.Execute(null)),
            new("ToggleTheme", "Toggle theme (dark/light)", null,
                () => {
                    vm.OpenSettingsCommand.Execute(null);
                    if (vm.SettingsPageVM != null)
                        vm.SettingsPageVM.Appearance = vm.SettingsPageVM.Appearance == "dark" ? "light" : "dark";
                }),
        };

        var palette = new CommandPaletteWindow(commands) { Owner = this };
        palette.ShowDialog();
    }

    // OnTerminalResized removed in Phase 5-E.5 P0-2 (bisect-mode-termination).
    // Pane resizes are handled by PaneContainerControl.OnPaneResized via
    // ActiveLayout.OnPaneResized → SurfaceResize per-pane. The old path called
    // gw_render_resize which was a duplicate with broken uniform-size semantics.

    // BC-03 (keydiag-log-dedupe): suppresses duplicate ENTRY logs when the
    // Bubble handler (OnTerminalKeyDownBubbled, Scenario D fallback) re-invokes
    // OnTerminalKeyDown with the same KeyEventArgs. ThreadStatic because WPF
    // input is single-threaded but this guards against any cross-dispatcher use.
    [ThreadStatic]
    private static bool _keyDiagSuppressEntry;

    private void OnTerminalKeyDown(object sender, KeyEventArgs e)
    {
        // P3 (2026-04-29) — Tab a11y trace. Verification 7-1~7-6 reported
        // Tab navigation does not reach the Sidebar buttons. Capture the
        // logical-focus state so we can confirm whether (a) Tab is already
        // being eaten by the HwndHost child window, (b) the focus starts
        // outside the WPF logical tree, or (c) WPF Tab routing chooses an
        // unexpected next focus. No behavior change yet — pure logging.
        if (e.Key == Key.Tab && !e.Handled)
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            LogA11y($"Tab tunneling | mod={modifiers} | source={e.Source?.GetType().Name} | original={e.OriginalSource?.GetType().Name} | focused={focused?.GetType().Name ?? "<null>"} | focusedName={(focused as FrameworkElement)?.Name ?? string.Empty}");
        }

        // Diagnostic instrumentation — e2e-ctrl-key-injection §4 spec, v0.2 §11.6.
        // Gated at runtime by GHOSTWIN_KEYDIAG env var (cached LEVEL_OFF on first
        // call when unset → method body returns immediately, no allocation/IO).
        // [Conditional("DEBUG")] removed so Release builds can be diagnosed in
        // place — see e2e-ctrl-key-injection.design.md §11.6 NFR-01 deviation.
        if (!_keyDiagSuppressEntry)
            KeyDiag.LogEntry(e, _workspaceService);

        // M-12: Esc closes settings page (before engine check — settings may be open without active session)
        if (e.Key == Key.Escape && DataContext is ViewModels.MainWindowViewModel { IsSettingsOpen: true } settingsVm)
        {
            settingsVm.CloseSettingsCommand.Execute(null);
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                PaneContainer.GetFocusedHost()?.Focus();
            });
            e.Handled = true;
            return;
        }

        if (_engine is not { IsInitialized: true })
        {
            KeyDiag.LogExit("early-return:engine-not-initialized", e);
            return;
        }
        if (_sessionManager.ActiveSessionId is not { } activeId)
        {
            KeyDiag.LogExit("early-return:no-active-session", e);
            return;
        }

        // Real-key resolution across WPF key wrapping:
        //
        //   1. Alt is a system modifier → WPF delivers Key=System, real key in e.SystemKey.
        //   2. While an IME composition is active (hasActiveComp=True) WPF delivers
        //      Key=ImeProcessed and the real key in e.ImeProcessedKey. Without this
        //      branch, Backspace pressed during a Hangul composition resolves as
        //      ImeProcessed and the (actualKey == Key.Back) guard below never fires —
        //      so ScheduleCompositionBackspaceReconcile() is not called and a lone
        //      jamo like 'ㅎ' is left stranded on screen because the Microsoft Hangul
        //      IME does not emit a follow-up empty composition for the last jamo.
        //
        //      Confirmed by ImeDiag #0007 (2026-04-18): KeyDown.ENTRY records
        //      key=ImeProcessed sysKey=None imeProcessed=Back hasActiveComp=True for
        //      the Backspace press that should clear the lone 'ㅎ' jamo preview.
        var actualKey = e.Key switch
        {
            Key.System       => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _                => e.Key
        };

        // M-13 진단: KeyDown 모든 진입 — BS가 어떤 형태로 오는지 확정
        ImeDiag.Log("KeyDown.ENTRY",
            $"routed={e.RoutedEvent?.Name} key={e.Key} sysKey={e.SystemKey} " +
            $"imeProcessed={e.ImeProcessedKey} actualKey={actualKey} " +
            $"hasActiveComp={_compositionPreview?.HasActivePreview} " +
            $"suppress={_suppressCompositionBackspaceBubble}");

        if (actualKey != Key.Back)
            _compositionPreview?.ResetBackspaceSuppression();

        if (actualKey == Key.Back &&
            e.RoutedEvent == Keyboard.KeyDownEvent &&
            _suppressCompositionBackspaceBubble)
        {
            ImeDiag.Log("KeyDown.BS_BUBBLE_SUPPRESSED",
                $"routed={e.RoutedEvent?.Name} suppress=true -> Handled=true (bubble blocked)");
            e.Handled = true;
            return;
        }

        if (actualKey == Key.Back && _compositionPreview?.HasActivePreview == true)
        {
            ImeDiag.Log("KeyDown.BS_WITH_ACTIVE_COMP",
                $"routed={e.RoutedEvent?.Name} hasActive=true");
            if (e.RoutedEvent == Keyboard.PreviewKeyDownEvent)
            {
                ImeDiag.Log("KeyDown.BS_PREVIEW",
                    "schedule reconcile (Handled=false, IME will receive BS)");
                ScheduleCompositionBackspaceReconcile();
                return;
            }

            ImeDiag.Log("KeyDown.BS_BUBBLE",
                "schedule reconcile + Handled=true (block shell from receiving BS)");
            e.Handled = true;
            ScheduleCompositionBackspaceReconcile();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            FocusDirection? dir = actualKey switch
            {
                Key.Left => FocusDirection.Left,
                Key.Right => FocusDirection.Right,
                Key.Up => FocusDirection.Up,
                Key.Down => FocusDirection.Down,
                _ => null,
            };
            if (dir.HasValue)
            {
                _workspaceService.ActivePaneLayout?.MoveFocus(dir.Value);
                e.Handled = true;
                return;
            }
        }

        // App shortcuts — dispatched here because when focus sits inside the
        // TerminalHostControl HwndHost child, Ctrl+... WM_KEYDOWN is consumed
        // by DefWindowProc before WPF InputBindings run. Alt+... still works
        // via HwndSource preprocessing.
        //
        // IsCtrlDown/Shift/Alt helpers (scenario B defence, Design §3.1.2)
        // triangulate Keyboard.IsKeyDown with raw GetKeyState so every
        // injection path (real user, SendInput, FlaUI, Appium) lights up
        // the same branch.
        //
        // ★ Ctrl+Shift block MUST precede Ctrl-only block — more-specific
        //   modifier combo first, otherwise Ctrl+Shift+W could be swallowed
        //   by the Ctrl+W branch on keyboards/injectors where IsShiftDown()
        //   returns false transiently.
        if (IsCtrlDown() && IsShiftDown() && !IsAltDown())
        {
            KeyDiag.LogBranch(BranchCtrlShift, e);
            if (e.Key == Key.C)
            {
                TryCopySelection();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.V)
            {
                PasteFromClipboard(activeId);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.W)
            {
                _workspaceService.ActivePaneLayout?.CloseFocused();
                e.Handled = true;
                return;
            }
            if (actualKey == Key.I)
            {
                if (DataContext is ViewModels.MainWindowViewModel vm)
                    vm.ToggleNotificationPanelCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (actualKey == Key.U)
            {
                if (DataContext is ViewModels.MainWindowViewModel vm2)
                    vm2.JumpToUnreadCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (actualKey == Key.P)
            {
                ShowCommandPalette();
                e.Handled = true;
                return;
            }
        }

        if (IsCtrlDown() && !IsShiftDown() && !IsAltDown())
        {
            KeyDiag.LogBranch(BranchCtrl, e);
            // M-12: Ctrl+, → open settings
            if (e.Key == Key.OemComma)
            {
                if (DataContext is ViewModels.MainWindowViewModel vm3)
                    vm3.OpenSettingsCommand.Execute(null);
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.T:
                    KeyDiag.LogKeyBindCommand(nameof(IWorkspaceService.CreateWorkspace));
                    _workspaceService.CreateWorkspace();
                    e.Handled = true;
                    return;
                case Key.W:
                    KeyDiag.LogKeyBindCommand(nameof(IWorkspaceService.CloseWorkspace));
                    if (_workspaceService.ActiveWorkspaceId is { } wsId)
                        _workspaceService.CloseWorkspace(wsId);
                    e.Handled = true;
                    return;
                case Key.Tab:
                {
                    KeyDiag.LogKeyBindCommand(nameof(IWorkspaceService.ActivateWorkspace));
                    var list = _workspaceService.Workspaces;
                    if (list.Count > 1 && _workspaceService.ActiveWorkspaceId is { } curId)
                    {
                        var idx = -1;
                        for (int i = 0; i < list.Count; i++)
                            if (list[i].Id == curId) { idx = i; break; }
                        if (idx >= 0)
                            _workspaceService.ActivateWorkspace(
                                list[(idx + 1) % list.Count].Id);
                    }
                    e.Handled = true;
                    return;
                }
            }
        }

        // Alt+V / Alt+H — direct dispatch as a belt-and-suspenders fallback
        // (InputBindings also handle these, but focus state may differ).
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key == Key.System)
        {
            if (actualKey == Key.V)
            {
                _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Vertical);
                e.Handled = true;
                return;
            }
            if (actualKey == Key.H)
            {
                _workspaceService.ActivePaneLayout?.SplitFocused(SplitOrientation.Horizontal);
                e.Handled = true;
                return;
            }
        }

        byte[]? data = e.Key switch
        {
            Key.Enter => "\r"u8.ToArray(),
            Key.Back => "\x7f"u8.ToArray(),
            Key.Tab => "\t"u8.ToArray(),
            Key.Escape => "\x1b"u8.ToArray(),
            Key.Up => "\x1b[A"u8.ToArray(),
            Key.Down => "\x1b[B"u8.ToArray(),
            Key.Right => "\x1b[C"u8.ToArray(),
            Key.Left => "\x1b[D"u8.ToArray(),
            Key.Home => "\x1b[H"u8.ToArray(),
            Key.End => "\x1b[F"u8.ToArray(),
            Key.Delete => "\x1b[3~"u8.ToArray(),
            _ => null,
        };

        // Ctrl+C: 선택 있으면 복사, 없으면 SIGINT (WT 패턴)
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (TryCopySelection())
            {
                e.Handled = true;
                return;
            }
            data = "\x03"u8.ToArray();
        }

        // Ctrl+V: 붙여넣기
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            PasteFromClipboard(activeId);
            e.Handled = true;
            return;
        }

        // Shift+Insert: 붙여넣기
        if (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            PasteFromClipboard(activeId);
            e.Handled = true;
            return;
        }

        if (data != null)
        {
            if (actualKey is Key.Escape or Key.Enter)
                ClearTerminalComposition();

            _engine.WriteSession(activeId, data);
            // Auto-scroll to bottom on keyboard input (WT/Alacritty pattern)
            _engine.ScrollViewport(activeId, int.MaxValue);
            e.Handled = true;
        }
    }

    private void OnTerminalTextComposition(object sender, TextCompositionEventArgs e)
    {
        var compositionText = e.TextComposition?.CompositionText ?? string.Empty;
        ImeDiag.Log("OnTerminalTextComposition",
            $"routed={e.RoutedEvent?.Name} comp={ImeDiag.Escape(compositionText)} text={ImeDiag.Escape(e.Text)}");
        if (_engine is not { IsInitialized: true }) return;
        if (_sessionManager.ActiveSessionId is not { }) return;

        _compositionPreview?.UpdateFromPreviewEvent(compositionText, e.Text);
    }

    private void OnTerminalTextInput(object sender, TextCompositionEventArgs e)
    {
        ImeDiag.Log("OnTerminalTextInput",
            $"routed={e.RoutedEvent?.Name} text={ImeDiag.Escape(e.Text)} comp={ImeDiag.Escape(e.TextComposition?.CompositionText)}");
        if (_engine is not { IsInitialized: true }) return;
        if (_sessionManager.ActiveSessionId is not { } activeId) return;
        _compositionPreview?.ResetBackspaceSuppression();
        ClearTerminalComposition();
        if (string.IsNullOrEmpty(e.Text)) return;

        _engine.WriteSession(activeId, Encoding.UTF8.GetBytes(e.Text));
        // Auto-scroll to bottom on keyboard input
        _engine.ScrollViewport(activeId, int.MaxValue);
        e.Handled = true;
    }

    private void ClearTerminalComposition()
    {
        _compositionPreview?.Clear();
    }

    private void ScheduleCompositionBackspaceReconcile()
    {
        ImeDiag.Log("ScheduleReconcile.ENTRY");
        var checkpoint = _compositionPreview?.BeginBackspace();
        if (checkpoint is null)
        {
            ImeDiag.Log("ScheduleReconcile.NO_CHECKPOINT", "exiting (no active preview)");
            return;
        }

        _suppressCompositionBackspaceBubble = true;
        ImeDiag.Log("ScheduleReconcile.SUPPRESS_ON",
            "queueing Background invocation");
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                ImeDiag.Log("ScheduleReconcile.BACKGROUND_FIRE",
                    "Background priority callback running");
                try
                {
                    _compositionPreview?.ReconcileBackspace(checkpoint);
                }
                finally
                {
                    _suppressCompositionBackspaceBubble = false;
                    ImeDiag.Log("ScheduleReconcile.SUPPRESS_OFF");
                }
            }));
    }

    // Scenario D fallback — re-runs OnTerminalKeyDown if tunnelling didn't reach
    // it (child HwndHost consumed the event). Guarded by e.Handled.
    // BC-03: sets _keyDiagSuppressEntry so the re-entry doesn't emit a duplicate
    // ENTRY log line (BRANCH/EXIT still logged normally).
    //
    // P3 (2026-04-29): a Tab pressed inside the HwndHost (TerminalHostControl)
    // arrives here on the bubble pass *only* if the child window did not call
    // SetFocus / handle WM_KEYDOWN itself. The verification 7-1~7-6 failure
    // suggests the child window is consuming Tab. Logging in this handler
    // tells us whether bubble runs at all and what focus is current at that
    // point — together with the tunneling log earlier we can tell exactly
    // which leg of WPF routing is dropped.
    private void OnTerminalKeyDownBubbled(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            var focused = System.Windows.Input.Keyboard.FocusedElement;
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            LogA11y($"Tab BUBBLE   | mod={modifiers} | handled={e.Handled} | source={e.Source?.GetType().Name} | original={e.OriginalSource?.GetType().Name} | focused={focused?.GetType().Name ?? "<null>"} | focusedName={(focused as FrameworkElement)?.Name ?? string.Empty}");
        }

        if (e.Handled) return;
        _keyDiagSuppressEntry = true;
        try { OnTerminalKeyDown(sender, e); }
        finally { _keyDiagSuppressEntry = false; }
    }

    // Scenario B defence — Keyboard.IsKeyDown (WPF cache) OR raw GetKeyState
    // (OS-level, via GhostWin.Interop.VirtualKeys) so SendInput/FlaUI/Appium
    // injection paths all read consistent modifier state regardless of
    // KeyboardDevice cache refresh timing. VK constants + P/Invoke live in
    // GhostWin.Interop.VirtualKeys (BC-09 centralisation).
    private static bool IsCtrlDown()
        => Keyboard.IsKeyDown(Key.LeftCtrl)
           || Keyboard.IsKeyDown(Key.RightCtrl)
           || VirtualKeys.IsCtrlDownRaw();

    private static bool IsShiftDown()
        => Keyboard.IsKeyDown(Key.LeftShift)
           || Keyboard.IsKeyDown(Key.RightShift)
           || VirtualKeys.IsShiftDownRaw();

    private static bool IsAltDown()
        => Keyboard.IsKeyDown(Key.LeftAlt)
           || Keyboard.IsKeyDown(Key.RightAlt)
           || VirtualKeys.IsAltDownRaw();

    private const string BranchCtrl      = "ctrl-branch";
    private const string BranchCtrlShift = "ctrl-shift-branch";

    // ── 클립보드: 복사 (Ctrl+C / Ctrl+Shift+C) ──

    /// <summary>
    /// 활성 pane에 선택 영역이 있으면 클립보드에 복사하고 true 반환.
    /// 선택 영역이 없으면 false 반환 (호출측에서 SIGINT 등 대체 동작 수행).
    /// </summary>
    private bool TryCopySelection()
    {
        var host = PaneContainer.GetFocusedHost();
        if (host == null) return false;

        if (host._selection.CurrentRange is not { IsValid: true } range) return false;

        var text = _engine.GetSelectedText(
            host.SessionId,
            range.Start.Row, range.Start.Col,
            range.End.Row, range.End.Col);

        if (string.IsNullOrEmpty(text)) return false;

        // OLE 재시도 (클립보드 잠금 경합 대비, WT 패턴)
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                Clipboard.SetText(text);
                break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (retry == 2) return false;
                Thread.Sleep(50);
            }
        }

        // 복사 후 선택 해제
        _engine.SetSelection(host.SessionId, 0, 0, 0, 0, false);
        host._selection.Clear();

        return true;
    }

    // ── 클립보드: 붙여넣기 (Ctrl+V / Ctrl+Shift+V / Shift+Insert) ──

    private void PasteFromClipboard(uint sessionId)
    {
        // OLE 재시도
        string? text = null;
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                text = Clipboard.GetText();
                break;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (retry == 2) return;
                Thread.Sleep(50);
            }
        }

        if (string.IsNullOrEmpty(text)) return;

        // C0/C1 제어 문자 필터 + 줄바꿸 정규화
        text = FilterForPaste(text);
        if (string.IsNullOrEmpty(text)) return;

        // Bracketed Paste Mode (mode 2004)
        bool bracketedPaste = _engine.GetMode(sessionId, 2004);

        byte[] payload;
        if (bracketedPaste)
        {
            // \x1b[200~ ... \x1b[201~ 로 감싸기
            var prefix = "\x1b[200~"u8;
            var suffix = "\x1b[201~"u8;
            var textBytes = Encoding.UTF8.GetBytes(text);
            payload = new byte[prefix.Length + textBytes.Length + suffix.Length];
            prefix.CopyTo(payload.AsSpan(0));
            textBytes.CopyTo(payload, prefix.Length);
            suffix.CopyTo(payload.AsSpan(prefix.Length + textBytes.Length));
        }
        else
        {
            // 비-bracket 모드: 줄바꿈을 \r로 통일 (터미널 입력 규약)
            text = text.Replace("\r\n", "\r").Replace("\n", "\r");
            payload = Encoding.UTF8.GetBytes(text);
        }

        _engine.WriteSession(sessionId, payload);
        _engine.ScrollViewport(sessionId, int.MaxValue);
    }

    /// <summary>
    /// 붙여넣기용 텍스트 필터: C0/C1 제어 문자 제거 (HT, LF, CR 제외).
    /// </summary>
    private static string FilterForPaste(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            // C0 제어 문자 (0x00-0x1F): HT(0x09), LF(0x0A), CR(0x0D)만 허용
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                continue;
            // C1 제어 문자 (0x80-0x9F): 모두 제거
            if (c >= 0x80 && c <= 0x9F)
                continue;
            // DEL (0x7F): 제거
            if (c == 0x7F)
                continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private void OnMouseCursorOracleUpdated(uint sessionId, int shape, int cursorId)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMouseCursorOracleUpdated(sessionId, shape, cursorId));
            return;
        }

        _mouseCursorOracle.Update(sessionId, shape, cursorId);
        MouseCursorShapeProbe.Content = _mouseCursorOracle.ShapeText;
        MouseCursorIdProbe.Content = _mouseCursorOracle.CursorIdText;
        MouseCursorSessionProbe.Content = _mouseCursorOracle.SessionText;
        AutomationProperties.SetName(MouseCursorShapeProbe, _mouseCursorOracle.ShapeText);
        AutomationProperties.SetName(MouseCursorIdProbe, _mouseCursorOracle.CursorIdText);
        AutomationProperties.SetName(MouseCursorSessionProbe, _mouseCursorOracle.SessionText);
        AutomationProperties.SetHelpText(MouseCursorShapeProbe, _mouseCursorOracle.ShapeText);
        AutomationProperties.SetHelpText(MouseCursorIdProbe, _mouseCursorOracle.CursorIdText);
        AutomationProperties.SetHelpText(MouseCursorSessionProbe, _mouseCursorOracle.SessionText);
    }
}
