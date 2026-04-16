using System.Text;
using GhostWin.Core.Interfaces;

namespace GhostWin.E2E.Tests.Stubs;

public static class OscInjector
{
    public static void InjectOsc7(ISessionManager mgr, uint sessionId, string directoryPath)
    {
        var uri = $"file:///{directoryPath.Replace('\\', '/')}";
        var sequence = $"\x1b]7;{uri}\x1b\\";
#pragma warning disable CS0618
        mgr.TestOnlyInjectBytes(sessionId, Encoding.UTF8.GetBytes(sequence));
#pragma warning restore CS0618
    }

    public static void InjectOsc9(ISessionManager mgr, uint sessionId, string message)
    {
        var sequence = $"\x1b]9;{message}\x1b\\";
#pragma warning disable CS0618
        mgr.TestOnlyInjectBytes(sessionId, Encoding.UTF8.GetBytes(sequence));
#pragma warning restore CS0618
    }
}
