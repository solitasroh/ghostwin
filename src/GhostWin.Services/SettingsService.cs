using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class SettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();
    public string SettingsFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GhostWin", "ghostwin.json");

    public void Load()
    {
        // M-4에서 System.Text.Json 파싱 구현
    }

    public void Save()
    {
        // M-4에서 JSON 직렬화 구현
    }

    public void StartWatching()
    {
        // M-4에서 FileSystemWatcher 구현
    }

    public void StopWatching()
    {
        // M-4에서 FileSystemWatcher 해제 구현
    }
}
