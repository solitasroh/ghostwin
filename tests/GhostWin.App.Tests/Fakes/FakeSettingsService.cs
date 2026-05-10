using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.App.Tests.Fakes;

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();
    public string SettingsFilePath => string.Empty;
    public int SaveCalls { get; private set; }

    public void Load() { }
    public void Save() => SaveCalls++;
    public void StartWatching() { }
    public void StopWatching() { }
}
