using CommunityToolkit.Mvvm.Messaging.Messages;

namespace GhostWin.Core.Events;

public sealed class SessionCreatedMessage(uint sessionId) : ValueChangedMessage<uint>(sessionId);
public sealed class SessionClosedMessage(uint sessionId) : ValueChangedMessage<uint>(sessionId);
public sealed class SessionActivatedMessage(uint sessionId) : ValueChangedMessage<uint>(sessionId);
public sealed class SessionTitleChangedMessage((uint Id, string Title) value) : ValueChangedMessage<(uint Id, string Title)>(value);
public sealed class SessionCwdChangedMessage((uint Id, string Cwd) value) : ValueChangedMessage<(uint Id, string Cwd)>(value);
public sealed class SessionChildExitMessage((uint Id, uint ExitCode) value) : ValueChangedMessage<(uint Id, uint ExitCode)>(value);
