using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class SettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public void Reload()
    {
        // M-4에서 JSON 파싱 + FileWatcher 구현
    }
}
