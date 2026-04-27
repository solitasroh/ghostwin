using CommunityToolkit.Mvvm.Messaging.Messages;
using GhostWin.Core.Models;

namespace GhostWin.Core.Events;

public sealed class PaneLayoutChangedMessage(IReadOnlyPaneNode root)
    : ValueChangedMessage<IReadOnlyPaneNode>(root);

public sealed class PaneFocusChangedMessage(uint paneId, uint sessionId)
    : ValueChangedMessage<(uint PaneId, uint SessionId)>((paneId, sessionId));
