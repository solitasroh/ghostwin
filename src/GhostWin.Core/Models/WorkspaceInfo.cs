using CommunityToolkit.Mvvm.ComponentModel;

namespace GhostWin.Core.Models;

/// <summary>
/// A workspace is a sidebar entry that owns its own pane tree (one or more
/// terminal sessions arranged via splits). cmux model: Window → Workspace → Pane → Surface.
/// Each workspace holds an independent <see cref="Interfaces.IPaneLayoutService"/> instance,
/// managed by <see cref="Interfaces.IWorkspaceService"/>.
/// </summary>
public partial class WorkspaceInfo : ObservableObject
{
    public uint Id { get; init; }

    [ObservableProperty]
    private string _name = "Workspace";

    [ObservableProperty]
    private bool _isActive;

    /// <summary>
    /// Display title — typically derived from the active pane's session.
    /// </summary>
    [ObservableProperty]
    private string _title = "Workspace";

    /// <summary>
    /// Display cwd — typically derived from the active pane's session.
    /// </summary>
    [ObservableProperty]
    private string _cwd = "";
}
