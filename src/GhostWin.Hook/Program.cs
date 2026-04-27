using System.IO.Pipes;
using System.Text;
using System.Text.Json;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: ghostwin-hook <stop|notify|prompt|set-status [state]>");
    return 1;
}

var eventName = args[0];

// Claude Code passes JSON via stdin
string? stdinJson = null;
if (Console.IsInputRedirected)
{
    stdinJson = Console.In.ReadToEnd();
}

// Extract fields from Claude Code Hooks JSON
string? cwd = null;
string? sessionId = Environment.GetEnvironmentVariable("GHOSTWIN_SESSION_ID");
string? notificationType = null;
string? message = null;
string? stopReason = null;

if (!string.IsNullOrEmpty(stdinJson))
{
    try
    {
        using var doc = JsonDocument.Parse(stdinJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("cwd", out var cwdProp))
            cwd = cwdProp.GetString();
        if (root.TryGetProperty("session_id", out var sidProp))
            sessionId ??= sidProp.GetString();
        if (root.TryGetProperty("notification_type", out var ntProp))
            notificationType = ntProp.GetString();
        if (root.TryGetProperty("stop_hook_reason", out var srProp))
            stopReason = srProp.GetString();
    }
    catch { /* parse failure — proceed with what we have */ }
}

// set-status <state> direct state control
string? status = eventName == "set-status" && args.Length > 1 ? args[1] : null;

var payload = JsonSerializer.Serialize(new
{
    @event = eventName,
    session_id = sessionId,
    cwd,
    data = new
    {
        stop_hook_reason = stopReason,
        notification_type = notificationType,
        message,
        status
    }
}, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

try
{
    using var pipe = new NamedPipeClientStream(".", "ghostwin-hook",
        PipeDirection.InOut, PipeOptions.None);
    pipe.Connect(timeout: 1000);

    using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
    writer.WriteLine(payload);

    using var reader = new StreamReader(pipe, Encoding.UTF8);
    _ = reader.ReadLine();
}
catch
{
    // GhostWin not running — exit silently so Claude Code hooks don't fail
}

return 0;
