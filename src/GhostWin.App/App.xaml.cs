using System.Windows;
using CommunityToolkit.Mvvm.DependencyInjection;
using GhostWin.Core.Interfaces;
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

        // Services (M-4에서 구현체 추가)
        services.AddSingleton<ISettingsService, SettingsService>();

        // ViewModels
        services.AddSingleton<ViewModels.MainWindowViewModel>();

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }
}
