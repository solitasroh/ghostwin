using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using GhostWin.Core.Interfaces;
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

    // M-16-D D-03: inline rename UI state.
    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameDraft = string.Empty;

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

    // ── M-16-D ContextMenu commands ──

    private IWorkspaceService? Workspaces => Ioc.Default.GetService<IWorkspaceService>();

    [RelayCommand]
    private void StartRename()
    {
        RenameDraft = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void ConfirmRename()
    {
        if (!IsRenaming) return;
        IsRenaming = false;
        var draft = (RenameDraft ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(draft) && draft != Name)
            Workspaces?.RenameWorkspace(WorkspaceId, draft);
    }

    [RelayCommand]
    private void CancelRename()
    {
        IsRenaming = false;
        RenameDraft = string.Empty;
    }

    [RelayCommand]
    private void MoveUp()
    {
        var ws = Workspaces;
        if (ws == null) return;
        int idx = IndexOfSelf(ws);
        if (idx > 0) ws.MoveWorkspace(WorkspaceId, idx - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        var ws = Workspaces;
        if (ws == null) return;
        int idx = IndexOfSelf(ws);
        if (idx >= 0 && idx < ws.Workspaces.Count - 1)
            ws.MoveWorkspace(WorkspaceId, idx + 1);
    }

    [RelayCommand]
    private void CloseWorkspace()
    {
        Workspaces?.CloseWorkspace(WorkspaceId);
    }

    private int IndexOfSelf(IWorkspaceService ws)
    {
        for (int i = 0; i < ws.Workspaces.Count; i++)
            if (ws.Workspaces[i].Id == WorkspaceId) return i;
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
    }
}
