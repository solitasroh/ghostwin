using CommunityToolkit.Mvvm.Messaging.Messages;
using GhostWin.Core.Models;

namespace GhostWin.Core.Events;

public sealed class SettingsChangedMessage(AppSettings settings) : ValueChangedMessage<AppSettings>(settings);

