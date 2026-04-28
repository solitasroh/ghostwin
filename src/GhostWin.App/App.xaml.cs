using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.App.Diagnostics;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;
using GhostWin.Interop;
using GhostWin.Services;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;

namespace GhostWin.App;

public partial class App : Application
{
    private static readonly string CrashLogPath =
        Path.Combine(AppContext.BaseDirectory, "ghostwin-crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // M-13 진단: imediag.log에 SESSION START 마커 즉시 기록.
        // GHOSTWIN_IMEDIAG=1 일 때만 작동 — 이전 실행/현재 실행 분리 + 진단 동작 여부 즉시 검증.
        ImeDiag.EnsureSessionMarker();

        // Crash diagnostics: capture exceptions before silent process exit.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Theme is applied later (after settingsService.Load()) so that
        // light-mode users do not see a brief dark flash. See M-16-A FR-04.

        var services = new ServiceCollection();

        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<IEngineService, EngineService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<ISettingsService, SettingsService>();
        // IPaneLayoutService is no longer DI-managed at app scope. Each workspace
        // creates and owns its own instance via WorkspaceService. Consumers should
        // resolve IWorkspaceService and use ActivePaneLayout (cmux model:
        // Window → Workspace → Pane → Surface).
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IOscNotificationService, OscNotificationService>();
        services.AddSingleton<ISessionSnapshotService, SessionSnapshotService>();
        services.AddSingleton<ViewModels.MainWindowViewModel>();

        // Phase 6-C: Named Pipe hook server
        var hookServer = new HookPipeServer(msg =>
        {
            Dispatcher.BeginInvoke(() => HandleHookMessage(msg));
        });
        services.AddSingleton<IHookPipeServer>(hookServer);

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        // Phase 6-A: break circular dependency (SessionManager ↔ OscNotificationService)
        var sm = (SessionManager)Ioc.Default.GetRequiredService<ISessionManager>();
        sm.SetOscService(Ioc.Default.GetRequiredService<IOscNotificationService>());
        sm.SetWorkspaceService(Ioc.Default.GetRequiredService<IWorkspaceService>());

        // 설정 로드 + FileWatcher 시작 + Dispatcher 마셜링 콜백 연결
        var settingsService = (SettingsService)Ioc.Default.GetRequiredService<ISettingsService>();
        settingsService.Load();

        // M-16-A FR-04 (C13 fix): apply settings-based theme as soon as
        // settings are known and before MainWindow is constructed, so that
        // light-mode users do not see a one-frame dark flash. The wpfui
        // Resources block in App.xaml still defaults to Dark, but
        // ApplicationThemeManager.Apply re-swaps the wpfui dictionary
        // synchronously here. The matching Colors.Dark.xaml ↔
        // Colors.Light.xaml swap for GhostWin tokens is wired up in
        // Day 7 (FR-07) via MergedDictionaries.Swap.
        var initialTheme = settingsService.Current.Appearance == "light"
            ? ApplicationTheme.Light
            : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(initialTheme);

        settingsService.OnSettingsReloaded = settings =>
        {
            Dispatcher.BeginInvoke(() =>
                WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(settings)));
        };
        settingsService.StartWatching();

        // ──────────────────────────────────────────────────────────
        // M-11 Session Restore — 시작 시 복원 (MainWindow.Show() 이전)
        //
        // Design §2.4 / §15 Step 7 (C-1 패치):
        //   - 복원/폴백 단일 진입점은 OnStartup (MainWindow.OnLoaded 는 가드만).
        //   - 복원은 동기 await — 빈 화면 시간 최소화 (§2.4 "UI 스레드 단독 실행").
        //   - 주기 저장 타이머는 Start() — PaneLayoutService 는 아직 Surface 없음 (OnLoaded 에서 생성),
        //     그러나 Collect() 는 Surface 에 비의존 (Root 트리 + SessionInfo 만 사용).
        // ──────────────────────────────────────────────────────────
        var snapshotSvc = Ioc.Default.GetRequiredService<ISessionSnapshotService>();
        var wsSvc       = Ioc.Default.GetRequiredService<IWorkspaceService>();
        var sessionMgr  = Ioc.Default.GetRequiredService<ISessionManager>();

        // NOTE: 엔진은 아직 Initialize 되지 않음 (MainWindow.OnLoaded 에서 실행).
        //       따라서 OnStartup 에서는 session.json 을 "읽기만" 하고, 실제 세션 생성이 필요한
        //       RestoreFromSnapshot / CreateWorkspace 는 MainWindow.OnLoaded (엔진 준비 후) 로 이월한다.
        //       App 측은 스냅샷 객체만 보관 후 MainWindow 에서 인스턴스를 소비.
        SessionSnapshot? pendingSnapshot = null;
        try
        {
            pendingSnapshot = snapshotSvc.LoadAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // 로드 실패 — 폴백은 MainWindow 에서 CreateWorkspace 로 (기존 경로 그대로).
            WriteCrashLog("SessionSnapshot.LoadAsync", ex);
        }
        PendingRestoreSnapshot = pendingSnapshot;

        // 주기 저장 대리자 주입 — Dispatcher.InvokeAsync 래핑 (§2.4 "스냅샷 수집은 UI 스레드").
        // GhostWin.Services 는 WPF 비의존이므로 래핑은 반드시 호출자(App) 책임.
        var dispatcher = Dispatcher;
        snapshotSvc.Start(async () =>
            await dispatcher.InvokeAsync(() =>
                SessionSnapshotMapper.Collect(wsSvc, sessionMgr)));

        // M-12: font/settings change → UpdateCellMetrics
        // M-16-A FR-09 (C9 fix): theme change → RenderSetClearColor so the
        // DX11 swap chain ClearColor follows light/dark settings instead of
        // staying at the boot-time 0x1E1E2E set in MainWindow.xaml.cs.
        // M-16-B FR-03 (Day 2): UseMica toggle → FluentWindow.WindowBackdropType
        // swap. wpfui invokes DwmSetWindowAttribute internally and the
        // "(restart required)" label is removed in Day 2 SettingsPageControl.
        WeakReferenceMessenger.Default.Register<SettingsChangedMessage>(this,
            (_, msg) =>
            {
                var engine = Ioc.Default.GetService<IEngineService>();
                if (engine is not { IsInitialized: true }) return;
                var font = msg.Value.Terminal.Font;
                var dpiScale = 1.0f;
                if (MainWindow is Window w)
                    dpiScale = (float)System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX;
                engine.UpdateCellMetrics(
                    (float)font.Size, font.Family, dpiScale,
                    (float)font.CellWidthScale, (float)font.CellHeightScale, 1.0f);

                uint clearRgb = msg.Value.Appearance == "light"
                    ? 0xFBFBFBu  // Light Terminal.Background.Color
                    : 0x1E1E2Eu; // Dark Terminal.Background.Color
                engine.RenderSetClearColor(clearRgb);

                if (MainWindow is Wpf.Ui.Controls.FluentWindow fw)
                {
                    // P0 final: Tabbed = MicaAlt = DWMSBT_TABBEDWINDOW=4.
                    // Stronger system-accent tint than plain Mica, less
                    // wallpaper-dependent. Same family — UI label stays
                    // "Mica backdrop".
                    fw.WindowBackdropType = msg.Value.Titlebar.UseMica
                        ? Wpf.Ui.Controls.WindowBackdropType.Tabbed
                        : Wpf.Ui.Controls.WindowBackdropType.None;
                }
            });

        // Phase 6-A+B: Toast notification on OSC 9/99/777 when window is not active.
        var settingsSvc = Ioc.Default.GetRequiredService<ISettingsService>();
        WeakReferenceMessenger.Default.Register<OscNotificationMessage>(this,
            (_, msg) =>
            {
                if (MainWindow?.IsActive == true) return;
                if (!settingsSvc.Current.Notifications.ToastEnabled) return;
                try
                {
                    new Microsoft.Toolkit.Uwp.Notifications.ToastContentBuilder()
                        .AddArgument("action", "switchTab")
                        .AddArgument("sessionId", msg.SessionId.ToString())
                        .AddText(string.IsNullOrEmpty(msg.Title) ? "GhostWin" : msg.Title)
                        .AddText(string.IsNullOrEmpty(msg.Body) ? msg.Title : msg.Body)
                        .Show();
                }
                catch { }
            });

        // Phase 6-B: Toast click → switch to tab
        Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.OnActivated += args =>
        {
            var parsed = Microsoft.Toolkit.Uwp.Notifications.ToastArguments.Parse(args.Argument);
            if (!parsed.TryGetValue("sessionId", out var idStr) ||
                !uint.TryParse(idStr, out var toastSessionId))
                return;

            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is Window w)
                {
                    if (w.WindowState == WindowState.Minimized)
                        w.WindowState = WindowState.Normal;
                    w.Activate();
                }
                var wsTarget = wsSvc.FindWorkspaceBySessionId(toastSessionId);
                if (wsTarget != null)
                    wsSvc.ActivateWorkspace(wsTarget.Id);
                Ioc.Default.GetService<IOscNotificationService>()
                    ?.DismissAttention(toastSessionId);
            });
        };

        // Phase 6-C: start Named Pipe server
        _ = hookServer.StartAsync();

        var mainWindow = new MainWindow();
        // M-16-B FR-02 (Day 2): apply initial Mica backdrop based on settings
        // before Show() so light-mode users do not see a one-frame None-then-Mica flash.
        // wpfui FluentWindow honors the property at the first chrome layout pass.
        if (settingsService.Current.Titlebar.UseMica)
        {
            mainWindow.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.Mica;
        }
        mainWindow.Show();
    }

    private void HandleHookMessage(HookMessage msg)
    {
        var sessionMgr = Ioc.Default.GetService<ISessionManager>();
        var oscService = Ioc.Default.GetService<IOscNotificationService>();
        var wsSvc = Ioc.Default.GetService<IWorkspaceService>();
        if (sessionMgr == null || oscService == null || wsSvc == null) return;

        // Security gate: the test-inject-osc22 hook lets a same-user process
        // push arbitrary OSC 22 payloads into the active ConPTY's stdin. A
        // crafted message can break out of the OSC envelope (\x1b\\) and run
        // shell commands. Compile this path out of Release builds; E2E
        // harnesses should use Debug binaries (or set
        // GHOSTWIN_E2E_HOOK_INJECT=1 to opt back in for an ad-hoc check).
#if DEBUG
        if (TryHandleTestInjectOsc22(msg, sessionMgr))
            return;
#else
        if (Environment.GetEnvironmentVariable("GHOSTWIN_E2E_HOOK_INJECT") == "1" &&
            TryHandleTestInjectOsc22(msg, sessionMgr))
            return;
#endif

        var session = MatchSession(msg, sessionMgr);
        if (session == null) return;

        switch (msg.Event)
        {
            case "stop":
                session.AgentState = AgentState.Idle;
                oscService.DismissAttention(session.Id);
                var ws1 = wsSvc.FindWorkspaceBySessionId(session.Id);
                if (ws1 != null) ws1.AgentState = AgentState.Idle;
                break;

            case "notify":
                var title = msg.Data?.NotificationType ?? "Notification";
                var body = msg.Data?.Message ?? "";
                oscService.HandleOscEvent(session.Id, title, body);
                break;

            case "prompt":
                session.AgentState = AgentState.Running;
                var ws2 = wsSvc.FindWorkspaceBySessionId(session.Id);
                if (ws2 != null) ws2.AgentState = AgentState.Running;
                break;

            case "cwd-changed":
                if (!string.IsNullOrEmpty(msg.Cwd))
                    sessionMgr.UpdateCwd(session.Id, msg.Cwd);
                break;

            case "set-status":
                if (Enum.TryParse<AgentState>(msg.Data?.Status, true, out var state))
                {
                    session.AgentState = state;
                    var ws3 = wsSvc.FindWorkspaceBySessionId(session.Id);
                    if (ws3 != null) ws3.AgentState = state;
                }
                break;
        }
    }

    private static SessionInfo? MatchSession(HookMessage msg, ISessionManager sessionMgr)
    {
        if (!string.IsNullOrEmpty(msg.SessionId) &&
            uint.TryParse(msg.SessionId, out var sid))
            return sessionMgr.Sessions.FirstOrDefault(s => s.Id == sid);

        if (!string.IsNullOrEmpty(msg.Cwd))
            return sessionMgr.Sessions.FirstOrDefault(s =>
                string.Equals(s.Cwd, msg.Cwd, StringComparison.OrdinalIgnoreCase));

        return null;
    }

    private static bool TryHandleTestInjectOsc22(HookMessage msg, ISessionManager sessionMgr)
    {
        if (!string.Equals(msg.Event, "test-inject-osc22", StringComparison.OrdinalIgnoreCase))
            return false;

        var value = msg.Data?.Message;
        if (string.IsNullOrEmpty(value))
            return true;

        uint? targetSessionId = null;
        if (!string.IsNullOrEmpty(msg.SessionId) && uint.TryParse(msg.SessionId, out var sessionId))
            targetSessionId = sessionId;
        else
            targetSessionId = sessionMgr.ActiveSessionId;

        if (targetSessionId is not { } target)
            return true;

        var sequence = $"\x1b]22;{value}\x1b\\";
#pragma warning disable CS0618
        sessionMgr.TestOnlyInjectBytes(target, Encoding.UTF8.GetBytes(sequence));
#pragma warning restore CS0618
        return true;
    }

    /// <summary>
    /// M-11 Session Restore — OnStartup 에서 로드된 스냅샷을 MainWindow.OnLoaded 로 전달하는 슬롯.
    /// <para>
    /// 엔진 초기화 이후에만 세션 생성이 가능하므로, 스냅샷은 이 프로퍼티에 보관되고
    /// MainWindow.InitializeRenderer 의 CreateWorkspace 위치에서 소비된다 (null 이면 신규 생성 폴백).
    /// </para>
    /// </summary>
    internal static SessionSnapshot? PendingRestoreSnapshot { get; private set; }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);
        try { MessageBox.Show(e.Exception.ToString(), "GhostWin Crash"); } catch { }
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        WriteCrashLog("AppDomainUnhandledException", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    internal static void WriteCrashLog(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:O}] {source}\n{ex}\n\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // All cleanup (engine, settings, TSF) is handled by
        // MainWindow.OnClosing → PerformShutdownAsync before reaching here.
        // See shutdown-path-unification design §2.4.
        //
        // NOTE (M-11 Session Restore): 최종 저장 + StopAsync 는 MainWindow.OnClosing 에서
        //   엔진 DetachCallbacks 직전에 이미 실행됨 (MainWindow.xaml.cs §OnClosing).
        //   OnExit 시점에는 Ioc 컨테이너가 여전히 살아있지만 백업 경로로만 사용.
        base.OnExit(e);
    }

}
