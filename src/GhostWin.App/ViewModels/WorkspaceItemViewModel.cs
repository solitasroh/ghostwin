using System.Windows;
using System.Windows.Media;
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
        AgentState.Running => GetThemeBrush("Workspace.Agent.Running.Brush"),
        AgentState.WaitingForInput => GetThemeBrush("Workspace.Agent.Waiting.Brush"),
        AgentState.Error => GetThemeBrush("Workspace.Agent.Error.Brush"),
        AgentState.Completed => GetThemeBrush("Workspace.Agent.Completed.Brush"),
        _ => Brushes.Transparent
    };

    public bool ShowAgentBadge => _workspace.AgentState != AgentState.Idle;

    public string GitBranch => _workspace.GitBranch;
    public string GitPrInfo => _workspace.GitPrInfo;
    public bool HasGitBranch => !string.IsNullOrEmpty(_workspace.GitBranch);

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
        if (e.PropertyName == nameof(WorkspaceInfo.GitBranch))
        {
            OnPropertyChanged(nameof(GitBranch));
            OnPropertyChanged(nameof(HasGitBranch));
        }
        if (e.PropertyName == nameof(WorkspaceInfo.GitPrInfo))
            OnPropertyChanged(nameof(GitPrInfo));
    }

    /// <summary>
    /// Force AgentStateColor to re-resolve from the active ResourceDictionary.
    /// Called by MainWindowViewModel after a theme swap so the agent badge
    /// brush picks up the new dictionary's value.
    /// </summary>
    public void OnThemeChanged()
    {
        OnPropertyChanged(nameof(AgentStateColor));
    }

    private static Brush GetThemeBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
    }
}
