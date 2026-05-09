using System.Text.Json;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public sealed class HookPipeProtocol
{
    private readonly Action<HookMessage> _onMessage;
    private readonly Func<TestControlRequest, TestControlResponse>? _onTestControlRequest;

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public HookPipeProtocol(
        Action<HookMessage> onMessage,
        Func<TestControlRequest, TestControlResponse>? onTestControlRequest = null)
    {
        _onMessage = onMessage;
        _onTestControlRequest = onTestControlRequest;
    }

    public string HandleLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!HasProperty(doc.RootElement, "event") &&
                HasProperty(doc.RootElement, "command"))
                return HandleTestControlRequest(line);

            var msg = JsonSerializer.Deserialize<HookMessage>(line, JsonOpts);
            if (msg != null)
                _onMessage(msg);

            return "{\"ok\":true}";
        }
        catch (JsonException ex)
        {
            return Serialize(TestControlResponse.Failure(0, $"invalid hook pipe payload: {ex.Message}"));
        }
        catch (Exception ex)
        {
            return Serialize(TestControlResponse.Failure(0, ex.Message));
        }
    }

    private string HandleTestControlRequest(string line)
    {
        var request = JsonSerializer.Deserialize<TestControlRequest>(line, JsonOpts);
        if (request == null)
            return Serialize(TestControlResponse.Failure(0, "test-control request is required"));

        var response = _onTestControlRequest?.Invoke(request)
            ?? TestControlResponse.Failure(0, "test-control handler is not configured", request.RequestId);

        return Serialize(response);
    }

    private static string Serialize(TestControlResponse response)
        => JsonSerializer.Serialize(response, JsonOpts);

    private static bool HasProperty(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.EnumerateObject()
               .Any(property => string.Equals(
                   property.Name,
                   propertyName,
                   StringComparison.OrdinalIgnoreCase));
}
