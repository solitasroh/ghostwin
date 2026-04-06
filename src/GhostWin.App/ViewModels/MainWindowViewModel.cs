using CommunityToolkit.Mvvm.ComponentModel;
using GhostWin.Core.Interfaces;

namespace GhostWin.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    public MainWindowViewModel(ISettingsService settings)
    {
        _settings = settings;
    }
}
