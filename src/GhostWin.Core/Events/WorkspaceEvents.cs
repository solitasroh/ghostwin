using CommunityToolkit.Mvvm.Messaging.Messages;

namespace GhostWin.Core.Events;

public sealed class WorkspaceCreatedMessage(uint workspaceId)
    : ValueChangedMessage<uint>(workspaceId);

public sealed class WorkspaceClosedMessage(uint workspaceId)
    : ValueChangedMessage<uint>(workspaceId);

public sealed class WorkspaceActivatedMessage(uint workspaceId)
    : ValueChangedMessage<uint>(workspaceId);

/// <summary>
/// M-16-D D-08: published when WorkspaceService.MoveWorkspace changes the
/// sidebar order. Recipients refresh their bound list (CollectionChanged
/// is not sufficient because the underlying collection mutates in-place).
/// </summary>
public sealed class WorkspaceReorderedMessage(uint workspaceId, int newIndex)
{
    public uint WorkspaceId { get; } = workspaceId;
    public int NewIndex { get; } = newIndex;
}
