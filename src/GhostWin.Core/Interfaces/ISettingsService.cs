using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    void Reload();
}
