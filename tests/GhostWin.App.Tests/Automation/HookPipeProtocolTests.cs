using System.Text.Json;
using FluentAssertions;
using GhostWin.Core.Models;
using GhostWin.Services;
using Xunit;

namespace GhostWin.App.Tests.Automation;

public sealed class HookPipeProtocolTests
{
    [Fact]
    public void HandleLine_LegacyHookMessage_InvokesLegacyHandlerAndReturnsSuccess()
    {
        HookMessage? received = null;
        var protocol = new HookPipeProtocol(
            msg => received = msg,
            _ => TestControlResponse.Failure(0, "unexpected"));

        var responseJson = protocol.HandleLine(
            """{"event":"set-status","session_id":"42","data":{"status":"Running"}}""");

        received.Should().NotBeNull();
        received!.Event.Should().Be("set-status");
        received.SessionId.Should().Be("42");
        received.Data!.Status.Should().Be("Running");

        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void HandleLine_LegacyHookMessage_WithCommandField_StillInvokesLegacyHandler()
    {
        HookMessage? received = null;
        var protocol = new HookPipeProtocol(
            msg => received = msg,
            _ => throw new InvalidOperationException("test-control handler should not run"));

        var responseJson = protocol.HandleLine(
            """{"event":"notify","command":"ignored-root-field","session_id":"42","data":{"message":"done"}}""");

        received.Should().NotBeNull();
        received!.Event.Should().Be("notify");
        received.SessionId.Should().Be("42");
        received.Data!.Message.Should().Be("done");

        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void HandleLine_TestControlRequest_DispatchesTypedRequestAndReturnsStructuredResponse()
    {
        TestControlRequest? received = null;
        var protocol = new HookPipeProtocol(
            _ => throw new InvalidOperationException("legacy handler should not run"),
            request =>
            {
                received = request;
                return TestControlResponse.Success(
                    stateVersion: 7,
                    data: new TestControlState(1, 2, 3, 4, 5, 6, 7),
                    requestId: request.RequestId);
            });

        var responseJson = protocol.HandleLine(
            """{"command":"get-state","request_id":"req-1"}""");

        received.Should().NotBeNull();
        received!.Command.Should().Be("get-state");
        received.RequestId.Should().Be("req-1");

        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("state_version").GetInt64().Should().Be(7);
        doc.RootElement.GetProperty("request_id").GetString().Should().Be("req-1");
        doc.RootElement.GetProperty("data").GetProperty("active_session_id").GetUInt32().Should().Be(1);
    }

    [Fact]
    public void HandleLine_TestControlRequest_DetectsCommandCaseInsensitively()
    {
        TestControlRequest? received = null;
        var protocol = new HookPipeProtocol(
            _ => throw new InvalidOperationException("legacy handler should not run"),
            request =>
            {
                received = request;
                return TestControlResponse.Success(1, requestId: request.RequestId);
            });

        var responseJson = protocol.HandleLine(
            """{"Command":"get-state","request_id":"req-2"}""");

        received.Should().NotBeNull();
        received!.Command.Should().Be("get-state");

        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("request_id").GetString().Should().Be("req-2");
    }

    [Fact]
    public void HandleLine_InvalidJson_ReturnsStructuredFailure()
    {
        var protocol = new HookPipeProtocol(
            _ => throw new InvalidOperationException("legacy handler should not run"),
            _ => TestControlResponse.Success(0));

        var responseJson = protocol.HandleLine("{not-json");

        using var doc = JsonDocument.Parse(responseJson);
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("error").GetString().Should().Contain("invalid hook pipe payload");
    }
}
