using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
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

        // Crash diagnostics: capture exceptions before silent process exit.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

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

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        // Phase 6-A: break circular dependency (SessionManager ↔ OscNotificationService)
        var sm = (SessionManager)Ioc.Default.GetRequiredService<ISessionManager>();
        sm.SetOscService(Ioc.Default.GetRequiredService<IOscNotificationService>());
        sm.SetWorkspaceService(Ioc.Default.GetRequiredService<IWorkspaceService>());

        // 설정 로드 + FileWatcher 시작 + Dispatcher 마셜링 콜백 연결
        var settingsService = (SettingsService)Ioc.Default.GetRequiredService<ISettingsService>();
        settingsService.Load();
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

        var mainWindow = new MainWindow();
        mainWindow.Show();
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
