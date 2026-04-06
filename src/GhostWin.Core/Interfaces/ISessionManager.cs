using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface ISessionManager
{
    IReadOnlyList<SessionInfo> Sessions { get; }
    uint? ActiveSessionId { get; }

    uint CreateSession(ushort cols = 80, ushort rows = 24);
    void CloseSession(uint id);
    void ActivateSession(uint id);
    void UpdateTitle(uint id, string title);
    void UpdateCwd(uint id, string cwd);
}
