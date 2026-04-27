using GhostWin.Core.Models;

namespace GhostWin.Core.Interfaces;

public interface ISettingsService
{
    AppSettings Current { get; }
    string SettingsFilePath { get; }

    void Load();
    void Save();
    void StartWatching();
    void StopWatching();
}
