namespace GhostWin.Core.Interfaces;

public interface IHookPipeServer
{
    Task StartAsync();
    Task StopAsync();
    bool IsRunning { get; }
}
