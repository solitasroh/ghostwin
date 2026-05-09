namespace GhostWin.App.Input;

public sealed class MouseCursorOracleState
{
    public string ShapeText { get; private set; } = string.Empty;
    public string CursorIdText { get; private set; } = string.Empty;
    public string SessionText { get; private set; } = string.Empty;
    public string VersionText { get; private set; } = string.Empty;
    public string UpdatedAtText { get; private set; } = string.Empty;
    public long Version { get; private set; }
    public DateTimeOffset? UpdatedAt { get; private set; }

    public void Update(uint sessionId, int shape, int cursorId)
    {
        Version++;
        UpdatedAt = DateTimeOffset.UtcNow;
        ShapeText = MouseCursorOracleFormatter.FormatShape(shape);
        CursorIdText = MouseCursorOracleFormatter.FormatCursorId(cursorId);
        SessionText = MouseCursorOracleFormatter.FormatSessionId(sessionId);
        VersionText = $"version={Version}";
        UpdatedAtText = $"updatedAt={UpdatedAt:O}";
    }
}
