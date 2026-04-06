using System.Windows;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.Core.Interfaces;
using GhostWin.Interop;
using GhostWin.Services;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Appearance;

namespace GhostWin.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        var services = new ServiceCollection();

        services.AddSingleton<IEngineService, EngineService>();
        services.AddSingleton<ISessionManager, SessionManager>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<ViewModels.MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 모든 세션을 먼저 닫아서 ConPTY 자식 프로세스 정리
        var sessionMgr = Ioc.Default.GetService<ISessionManager>();
        if (sessionMgr != null)
        {
            var ids = sessionMgr.Sessions.Select(s => s.Id).ToList();
            foreach (var id in ids)
            {
                try { sessionMgr.CloseSession(id); }
                catch { /* 종료 중 예외 무시 */ }
            }
        }

        var engine = Ioc.Default.GetService<IEngineService>();
        (engine as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
