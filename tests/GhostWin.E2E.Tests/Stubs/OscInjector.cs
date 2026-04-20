using System.IO.Pipes;
using System.Text;
using System.Text.Json;
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

    public static void InjectOsc22(ISessionManager mgr, uint sessionId, string value)
    {
        var sequence = $"\x1b]22;{value}\x1b\\";
#pragma warning disable CS0618
        mgr.TestOnlyInjectBytes(sessionId, Encoding.UTF8.GetBytes(sequence));
#pragma warning restore CS0618
    }

    public static void InjectOsc22ToRunningApp(string value, uint? sessionId = null)
    {
        var payload = JsonSerializer.Serialize(new
        {
            @event = "test-inject-osc22",
            session_id = sessionId?.ToString(),
            data = new
            {
                message = value
            }
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        });

        using var pipe = new NamedPipeClientStream(".", "ghostwin-hook",
            PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(timeout: 1000);

        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };
        writer.WriteLine(payload);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        _ = reader.ReadLine();
    }
}
