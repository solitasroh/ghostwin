namespace GhostWin.App.Input;

public sealed class MouseCursorOracleState
{
    public string ShapeText { get; private set; } = string.Empty;
    public string CursorIdText { get; private set; } = string.Empty;
    public string SessionText { get; private set; } = string.Empty;

    public void Update(uint sessionId, int shape, int cursorId)
    {
        ShapeText = MouseCursorOracleFormatter.FormatShape(shape);
        CursorIdText = MouseCursorOracleFormatter.FormatCursorId(cursorId);
        SessionText = MouseCursorOracleFormatter.FormatSessionId(sessionId);
    }
}
