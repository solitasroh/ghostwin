namespace GhostWin.App.Input;

internal static class MouseCursorOracleProbe
{
    internal static event Action<uint, int, int>? Updated;

    internal static void Publish(uint sessionId, int shape, int cursorId)
        => Updated?.Invoke(sessionId, shape, cursorId);
}
