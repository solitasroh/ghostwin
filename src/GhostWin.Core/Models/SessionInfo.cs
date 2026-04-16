using CommunityToolkit.Mvvm.ComponentModel;

namespace GhostWin.Core.Models;

public partial class SessionInfo : ObservableObject
{
    public uint Id { get; init; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _cwd = string.Empty;

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private bool _needsAttention;

    [ObservableProperty]
    private string _lastOscMessage = string.Empty;
}
