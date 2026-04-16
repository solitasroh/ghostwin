using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostWin.Core.Models;

namespace GhostWin.App.ViewModels;

public partial class WorkspaceItemViewModel : ObservableObject, IDisposable
{
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly SolidColorBrush WaitingBrush = new(Color.FromRgb(0x00, 0x7A, 0xFF));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly SolidColorBrush CompletedBrush = new(Color.FromRgb(0x8E, 0x8E, 0x93));

    private readonly WorkspaceInfo _workspace;
    private bool _disposed;

    public uint WorkspaceId => _workspace.Id;
    public string Name => _workspace.Name;
    public string Title => _workspace.Title;
    public string Cwd => _workspace.Cwd;
    public bool IsActive => _workspace.IsActive;
    public bool NeedsAttention => _workspace.NeedsAttention;
    public string LastOscMessage => _workspace.LastOscMessage;

    public AgentState AgentState => _workspace.AgentState;

    public string AgentStateBadge => _workspace.AgentState switch
    {
        AgentState.Running => "●",
        AgentState.WaitingForInput => "●",
        AgentState.Error => "✕",
        AgentState.Completed => "✓",
        _ => ""
    };

    public Brush AgentStateColor => _workspace.AgentState switch
    {
        AgentState.Running => RunningBrush,
        AgentState.WaitingForInput => WaitingBrush,
        AgentState.Error => ErrorBrush,
        AgentState.Completed => CompletedBrush,
        _ => Brushes.Transparent
    };

    public bool ShowAgentBadge => _workspace.AgentState != AgentState.Idle;

    public WorkspaceItemViewModel(WorkspaceInfo workspace)
    {
        _workspace = workspace;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
    }

    private void OnWorkspacePropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        if (e.PropertyName == nameof(WorkspaceInfo.AgentState))
        {
            OnPropertyChanged(nameof(AgentStateBadge));
            OnPropertyChanged(nameof(AgentStateColor));
            OnPropertyChanged(nameof(ShowAgentBadge));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
    }
}
