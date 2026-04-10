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
        base.OnExit(e);
    }

}
