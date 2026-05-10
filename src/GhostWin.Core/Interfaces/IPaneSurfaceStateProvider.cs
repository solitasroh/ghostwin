using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface IPaneSurfaceStateProvider
{
    TerminalPaneSurfaceState GetPaneSurfaceState(uint paneId);
}
