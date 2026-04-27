namespace GhostWin.Core.Models;

public record CommandInfo(
    string ActionId,
    string Name,
    string? KeyCombo,
    Action Execute);
