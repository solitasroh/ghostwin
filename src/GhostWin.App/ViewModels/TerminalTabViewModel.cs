using CommunityToolkit.Mvvm.ComponentModel;
using GhostWin.Core.Models;

namespace GhostWin.App.ViewModels;

public partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    private readonly SessionInfo _session;
    private bool _disposed;

    public uint SessionId => _session.Id;
    public string Title => _session.Title;
    public string Cwd => _session.Cwd;
    public bool IsActive => _session.IsActive;

    public TerminalTabViewModel(SessionInfo session)
    {
        _session = session;
        _session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void OnSessionPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.PropertyChanged -= OnSessionPropertyChanged;
    }
}
