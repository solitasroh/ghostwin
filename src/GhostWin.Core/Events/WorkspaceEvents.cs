using CommunityToolkit.Mvvm.Messaging.Messages;

namespace GhostWin.Core.Events;

public sealed class WorkspaceCreatedMessage(uint workspaceId)
    : ValueChangedMessage<uint>(workspaceId);

public sealed class WorkspaceClosedMessage(uint workspaceId)
    : ValueChangedMessage<uint>(workspaceId);

public sealed class WorkspaceActivatedMessage(uint workspaceId)
    : ValueChangedMessage<uint>(workspaceId);
