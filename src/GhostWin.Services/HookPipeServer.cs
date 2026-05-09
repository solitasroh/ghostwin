using System.IO.Pipes;
using System.Text;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class HookPipeServer : IHookPipeServer
{
    public const string DefaultPipeName = "ghostwin-hook";
    public const string PipeNameEnvironmentVariable = "GHOSTWIN_HOOK_PIPE_NAME";

    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly HookPipeProtocol _protocol;

    public bool IsRunning => _listenTask is { IsCompleted: false };

    public HookPipeServer(
        Action<HookMessage> onMessage,
        Func<TestControlRequest, TestControlResponse>? onTestControlRequest = null,
        string pipeName = DefaultPipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        _pipeName = pipeName;
        _protocol = new HookPipeProtocol(onMessage, onTestControlRequest);
    }

    public Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_listenTask != null)
        {
            try { await _listenTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
    }

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                var response = _protocol.HandleLine(line);

                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync(response);
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* client disconnect */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HookPipeServer] {ex.Message}");
            }
        }
    }
}
