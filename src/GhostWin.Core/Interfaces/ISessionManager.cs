using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface ISessionManager
{
    IReadOnlyList<SessionInfo> Sessions { get; }
    uint? ActiveSessionId { get; }

    SessionInfo CreateSession(ushort cols = 80, ushort rows = 24);
    void CloseSession(uint id);
    void ActivateSession(uint id);
    void WriteToActive(ReadOnlySpan<byte> data);
    void ResizeActive(ushort cols, ushort rows);
}
