using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

public class HookPipeServer : IHookPipeServer
{
    private const string PipeName = "ghostwin-hook";
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly Action<HookMessage> _onMessage;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public bool IsRunning => _listenTask is { IsCompleted: false };

    public HookPipeServer(Action<HookMessage> onMessage)
    {
        _onMessage = onMessage;
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
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                var msg = JsonSerializer.Deserialize<HookMessage>(line, JsonOpts);
                if (msg != null)
                    _onMessage(msg);

                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync("{\"ok\":true}");
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
