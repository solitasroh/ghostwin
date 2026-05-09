using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using GhostWin.Core.Models;

namespace GhostWin.Automation.Core.Tests;

public sealed class TestControlClientTests
{
    [Fact]
    public async Task SendAsync_WritesTypedRequestAndReadsStructuredResponse()
    {
        var pipeName = $"ghostwin-test-{Guid.NewGuid():N}";
        var serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var requestJson = await reader.ReadLineAsync();
            requestJson.Should().NotBeNull();

            using var requestDoc = JsonDocument.Parse(requestJson!);
            requestDoc.RootElement.GetProperty("command").GetString().Should().Be("get-state");
            requestDoc.RootElement.GetProperty("request_id").GetString().Should().Be("req-1");

            using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(
                """{"ok":true,"state_version":3,"request_id":"req-1","data":{"active_session_id":9}}""");
        });

        var client = new TestControlClient(pipeName: pipeName, connectTimeout: TimeSpan.FromSeconds(3));

        var response = await client.SendAsync(new TestControlRequest("get-state", RequestId: "req-1"));

        response.Ok.Should().BeTrue();
        response.StateVersion.Should().Be(3);
        response.RequestId.Should().Be("req-1");
        await serverTask;
    }

    [Fact]
    public async Task GetStateAsync_ReturnsTypedState()
    {
        var pipeName = $"ghostwin-test-{Guid.NewGuid():N}";
        var serverTask = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            _ = await reader.ReadLineAsync();

            using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(
                """{"ok":true,"state_version":4,"data":{"active_session_id":11,"active_workspace_id":2,"focused_pane_id":3,"focused_session_id":11,"session_count":1,"workspace_count":1,"pane_count":1}}""");
        });

        var client = new TestControlClient(pipeName: pipeName, connectTimeout: TimeSpan.FromSeconds(3));

        var state = await client.GetStateAsync();

        state.ActiveSessionId.Should().Be(11);
        state.ActiveWorkspaceId.Should().Be(2);
        state.PaneCount.Should().Be(1);
        await serverTask;
    }

    [Fact]
    public async Task SendAsync_WhenPipeConnectionFails_ThrowsUsefulException()
    {
        var client = new TestControlClient(
            pipeName: $"ghostwin-missing-{Guid.NewGuid():N}",
            connectTimeout: TimeSpan.FromMilliseconds(50));

        var act = async () => await client.SendAsync(new TestControlRequest("get-state"));

        await act.Should().ThrowAsync<TimeoutException>()
            .WithMessage("*test-control pipe*");
    }
}
