using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhostWin.Core.Interfaces;
using GhostWin.Core.Models;

namespace GhostWin.Services;

/// <summary>
/// 앱 세션 상태를 %AppData%/GhostWin/session.json 에 저장/복원하는 Singleton 서비스.
/// <para>
/// 쓰기 경로: temp 파일 → File.Replace (원자적) / SemaphoreSlim(1,1) 으로 I/O 직렬화.
/// 주기 저장: PeriodicTimer(10s) / 해시 비교로 변경 없으면 쓰기 건너뜀.
/// </para>
/// </summary>
public sealed class SessionSnapshotService : ISessionSnapshotService
{
    // ──────────────────────────────────────────────────────────
    // 직렬화 옵션 (스레드-세이프 불변 정적 인스턴스)
    // ──────────────────────────────────────────────────────────

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // ──────────────────────────────────────────────────────────
    // 상태
    // ──────────────────────────────────────────────────────────

    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private SessionSnapshot? _lastSaved;
    private Task? _timerTask;
    private Func<Task<SessionSnapshot>>? _snapshotCollector;

    /// <summary>주기 저장 간격 (기본 10초). 테스트에서 단축 가능.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public string SnapshotPath { get; }

    // ──────────────────────────────────────────────────────────
    // 생성자
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// DI 생성자. SettingsService 와 동일하게 %AppData%/GhostWin/ 폴더를 사용.
    /// </summary>
    public SessionSnapshotService()
    {
        SnapshotPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GhostWin", "session.json");
    }

    // ──────────────────────────────────────────────────────────
    // 공개 API
    // ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<SessionSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 1차: session.json 시도
            var snap = await TryReadAndParseAsync(SnapshotPath, ct).ConfigureAwait(false);
            if (snap is not null)
            {
                _lastSaved = snap;
                return snap;
            }

            // session.json 실패 시 .bak (File.Replace 가 자동 유지하는 직전 성공본) 시도
            var bakPath = SnapshotPath + ".bak";
            if (File.Exists(bakPath))
            {
                Debug.WriteLine("[SessionSnapshot] Primary failed — attempting .bak fallback");
                snap = await TryReadAndParseAsync(bakPath, ct).ConfigureAwait(false);
                if (snap is not null)
                {
                    _lastSaved = snap;
                    return snap;
                }
                // .bak 도 파손 — 다음 저장 시 덮어쓰기 예정이므로 별도 조치 없음
            }

            return null;
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(SessionSnapshot snapshot, CancellationToken ct = default)
    {
        await _ioLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOpts);
            await WriteAtomicAsync(bytes, ct).ConfigureAwait(false);
            _lastSaved = snapshot;
        }
        catch (IOException ex)
        {
            // 종료 경로 블록 금지 — 로그만 남기고 계속
            Debug.WriteLine($"[SessionSnapshot] SaveAsync IO error: {ex.Message}");
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <inheritdoc/>
    public void Start(Func<Task<SessionSnapshot>> snapshotCollector)
    {
        if (_timerTask is not null) return;
        _snapshotCollector = snapshotCollector;
        _timerTask = RunTimerAsync(_cts.Token);
    }

    /// <inheritdoc/>
    public async Task StopAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_timerTask is not null)
        {
            try
            {
                await _timerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 정상 취소 경로 — 무시
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _ioLock.Dispose();
    }

    // ──────────────────────────────────────────────────────────
    // 내부 구현
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// PeriodicTimer 루프. _snapshotCollector 대리자 (호출자가 UI 스레드 래핑)로
    /// 스냅샷을 수집한 후 변경 여부 판단 → 쓰기.
    /// </summary>
    private async Task RunTimerAsync(CancellationToken ct)
    {
        if (_snapshotCollector is null) return;

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                // 호출자(App)가 Dispatcher.InvokeAsync 래핑 후 주입한 대리자
                var snap = await _snapshotCollector().ConfigureAwait(false);

                // record 동등성 비교 — 변경 없으면 쓰기 건너뜀
                if (_lastSaved is not null && snap == _lastSaved) continue;

                await SaveAsync(snap, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SessionSnapshot] Periodic save failed: {ex.Message}");
                // 다음 주기에 자동 재시도
            }
        }
    }

    /// <summary>
    /// 단일 파일 읽기 + 파싱 + schema 검증.
    /// 실패 시 해당 파일을 .corrupt.{ts} 로 격리하고 null 반환.
    /// 파일 미존재 시 격리 없이 null.
    /// </summary>
    private async Task<SessionSnapshot?> TryReadAndParseAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Debug.WriteLine($"[SessionSnapshot] IO error reading {path}: {ex.Message}");
            return null;
        }

        SessionSnapshot? snap;
        try
        {
            snap = JsonSerializer.Deserialize<SessionSnapshot>(bytes, JsonOpts);
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SessionSnapshot] Parse failed ({path}): {ex.Message} — quarantining");
            QuarantineCorrupt(path);
            return null;
        }

        if (snap is null || snap.SchemaVersion != 1)
        {
            Debug.WriteLine(
                $"[SessionSnapshot] Alien schema v={snap?.SchemaVersion} ({path}) — quarantining");
            QuarantineCorrupt(path);
            return null;
        }

        return snap;
    }

    /// <summary>
    /// temp 파일에 쓴 후 File.Replace 로 원자 교체.
    /// 대상 파일이 없으면 File.Move 로 이동.
    /// </summary>
    private async Task WriteAtomicAsync(byte[] bytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(SnapshotPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var tempPath   = SnapshotPath + ".tmp";
        var backupPath = SnapshotPath + ".bak";

        await File.WriteAllBytesAsync(tempPath, bytes, ct).ConfigureAwait(false);

        if (File.Exists(SnapshotPath))
        {
            // Win32 ReplaceFile — 저널링 FS 에서 원자적.
            // ignoreMetadataErrors=true 로 네트워크 드라이브/권한 문제 완화.
            File.Replace(tempPath, SnapshotPath, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, SnapshotPath);
        }
    }

    /// <summary>
    /// 손상 파일을 .corrupt.{yyyyMMdd-HHmmss} 로 rename (데이터 유실 방지).
    /// </summary>
    private static void QuarantineCorrupt(string path)
    {
        try
        {
            var stamp       = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var corruptPath = $"{path}.corrupt.{stamp}";
            File.Move(path, corruptPath, overwrite: false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SessionSnapshot] Quarantine failed ({path}): {ex.Message}");
        }
    }
}
