using CommunityToolkit.Mvvm.ComponentModel;
using GhostWin.Core.Models;

namespace GhostWin.App.ViewModels;

public partial class WorkspaceItemViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceInfo _workspace;
    private bool _disposed;

    public uint WorkspaceId => _workspace.Id;
    public string Name => _workspace.Name;
    public string Title => _workspace.Title;
    public string Cwd => _workspace.Cwd;
    public bool IsActive => _workspace.IsActive;

    public WorkspaceItemViewModel(WorkspaceInfo workspace)
    {
        _workspace = workspace;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    private void OnWorkspacePropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
    }
}
