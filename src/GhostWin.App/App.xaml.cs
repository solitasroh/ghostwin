using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using GhostWin.Core.Events;
using GhostWin.Core.Interfaces;
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
        services.AddSingleton<ViewModels.MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        // 설정 로드 + FileWatcher 시작 + Dispatcher 마셜링 콜백 연결
        var settingsService = (SettingsService)Ioc.Default.GetRequiredService<ISettingsService>();
        settingsService.Load();
        settingsService.OnSettingsReloaded = settings =>
        {
            Dispatcher.BeginInvoke(() =>
                WeakReferenceMessenger.Default.Send(new SettingsChangedMessage(settings)));
        };
        settingsService.StartWatching();

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

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

    private static void WriteCrashLog(string source, Exception? ex)
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
        // 설정 감시 중지
        var settingsService = Ioc.Default.GetService<ISettingsService>();
        (settingsService as IDisposable)?.Dispose();

        // 렌더링 중지 (I/O 스레드 블로킹 방지를 위해 destroy 전에)
        var engine = Ioc.Default.GetService<IEngineService>();
        if (engine is Interop.EngineService es)
        {
            engine.RenderStop();
        }

        base.OnExit(e);

        // ConPTY I/O 스레드가 gw_engine_destroy에서 블로킹되므로
        // 강제 종료로 프로세스 정리 (WT도 동일 패턴 사용)
        Environment.Exit(0);
    }
}
