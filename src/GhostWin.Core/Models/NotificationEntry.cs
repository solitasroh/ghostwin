using CommunityToolkit.Mvvm.ComponentModel;

namespace GhostWin.Core.Models;

public partial class NotificationEntry : ObservableObject
{
    public uint SessionId { get; init; }
    public string SessionTitle { get; init; } = "";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset ReceivedAt { get; init; }

    [ObservableProperty]
    private bool _isRead;
}
