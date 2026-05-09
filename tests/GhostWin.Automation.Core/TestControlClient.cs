using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GhostWin.Core.Models;

namespace GhostWin.Automation.Core;

public sealed class TestControlClient
{
    private const string DefaultPipeName = "ghostwin-hook";

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public TestControlClient(
        string pipeName = DefaultPipeName,
        TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _pipeName = pipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public async Task<TestControlResponse> SendAsync(
        TestControlRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync((int)_connectTimeout.TotalMilliseconds, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Timed out connecting to GhostWin test-control pipe '{_pipeName}'.",
                ex);
        }

        var payload = JsonSerializer.Serialize(request, JsonOpts);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken);

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var responseLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(responseLine))
            throw new InvalidDataException("GhostWin test-control pipe returned an empty response.");

        var response = JsonSerializer.Deserialize<TestControlResponse>(responseLine, JsonOpts);
        return response ?? throw new InvalidDataException("GhostWin test-control pipe returned an invalid response.");
    }

    public async Task<TestControlState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            new TestControlRequest("get-state", RequestId: NewRequestId()),
            cancellationToken);

        EnsureOk(response);
        return ReadData<TestControlState>(response);
    }

    public async Task<TestControlState> ExecuteCommandAsync(
        string commandName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

        var response = await SendAsync(
            new TestControlRequest(
                "execute-command",
                RequestId: NewRequestId(),
                Data: new TestControlPayload(CommandName: commandName)),
            cancellationToken);

        EnsureOk(response);
        return ReadData<TestControlState>(response);
    }

    public async Task<TestControlState> InjectOscAsync(
        string osc,
        string message,
        uint? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(osc);
        ArgumentNullException.ThrowIfNull(message);

        var response = await SendAsync(
            new TestControlRequest(
                "inject-osc",
                RequestId: NewRequestId(),
                SessionId: sessionId,
                Data: new TestControlPayload(Osc: osc, Message: message)),
            cancellationToken);

        EnsureOk(response);
        return ReadData<TestControlState>(response);
    }

    private static void EnsureOk(TestControlResponse response)
    {
        if (!response.Ok)
            throw new InvalidOperationException(
                $"GhostWin test-control command failed: {response.Error ?? "unknown error"}");
    }

    private static T ReadData<T>(TestControlResponse response)
    {
        if (response.Data is JsonElement element)
        {
            return element.Deserialize<T>(JsonOpts)
                ?? throw new InvalidDataException("GhostWin test-control response data was null.");
        }

        if (response.Data is T value)
            return value;

        var json = JsonSerializer.Serialize(response.Data, JsonOpts);
        return JsonSerializer.Deserialize<T>(json, JsonOpts)
            ?? throw new InvalidDataException("GhostWin test-control response data was null.");
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");
}
