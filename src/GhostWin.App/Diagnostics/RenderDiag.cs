// RenderDiag — first-pane render lifecycle diagnostic logger.
//
// Design reference: docs/02-design/features/first-pane-render-failure.design.md §3.1
//
// Mirrors KeyDiag (src/GhostWin.App/Diagnostics/KeyDiag.cs) pattern:
//   - env-var runtime gate (no allocation when off)
//   - sync Trace.WriteLine + File.AppendAllText (no Dispatcher)
//   - lock-free Interlocked sequence + static lock for IO
//   - fail-silent (Debug.WriteLine fallback on exception)
//
// HEISENBUG AVOIDANCE (CRITICAL):
//   - NEVER use Dispatcher.BeginInvoke / Dispatcher.InvokeAsync inside RenderDiag.
//     Dispatcher.BeginInvoke 는 본 race 의 trigger 이므로 instrumentation 이 쓰면 Heisenbug.
//   - NEVER use async/await (SynchronizationContext post 가능)
//   - NEVER use Task.Run (ThreadPool 이동)
//   - NEVER use ConfigureAwait(false)
//   - NEVER use Lazy<T> (thread-safe variant 도 lock 생성)
//   - Stopwatch.GetTimestamp() (QPC) 만 사용 — DateTime.UtcNow 는 wall-clock jitter
//
// Runtime gate: GHOSTWIN_RENDERDIAG env var
//   unset / "0" → LEVEL_OFF (early-return, zero allocation, zero syscall)
//   "1"          → LEVEL_LIFECYCLE (BuildWindowCore, HostReady, OnHostReady, SurfaceCreate)
//   "2"          → LEVEL_TIMING   (+ enqueue/dequeue ticks)
//   "3"          → LEVEL_STATE    (+ subscriber_count, hwnd transitions)
//
// Output: %LocalAppData%\GhostWin\diagnostics\render_{yyyyMMdd}.log
//   - Append mode, line-per-event, ISO8601 UTC timestamp + Stopwatch ns
//   - Daily rotation (30회 reproduction 분량 + multi-day session 대응)
//   - Thread-safe via static _lock
//
// NFR note: [Conditional("DEBUG")] 미사용 (KeyDiag R4 deviance 동일 이유).
//   GHOSTWIN_RENDERDIAG 미설정(Release default) 에서 process 당 env-var 1회 조회 +
//   이후 cached int compare 1회만 발생 — 사실상 0 perf impact.

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace GhostWin.App.Diagnostics;

internal static class RenderDiag
{
    public const int LEVEL_OFF       = 0;
    public const int LEVEL_LIFECYCLE = 1;
    public const int LEVEL_TIMING    = 2;
    public const int LEVEL_STATE     = 3;

    private static readonly object _lock = new();
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private static int _seq;
    // -1 = 미초기화. GetLevel() 최초 호출 시 env-var 조회 후 캐시.
    private static int _level = -1;
    private static string? _logPath;

    /// <summary>
    /// 주요 logging API. requiredLevel 이상의 GHOSTWIN_RENDERDIAG 가 설정되어야 emit.
    /// 첫 번째 명령이 GetLevel() check 이므로 off 상태에서 allocation 0, syscall 0.
    /// </summary>
    /// <param name="requiredLevel">LEVEL_LIFECYCLE / LEVEL_TIMING / LEVEL_STATE 중 하나</param>
    /// <param name="eventName">evt= 필드 값 (예: "buildwindow-enter")</param>
    /// <param name="fields">가변 (key, value) 튜플 — log 라인에 space-separated 로 추가</param>
    public static void LogEvent(
        int requiredLevel,
        string eventName,
        params (string key, object? value)[] fields)
    {
        if (GetLevel() < requiredLevel) return;
        try
        {
            int seq = Interlocked.Increment(ref _seq);
            long elapsedTicks = _stopwatch.ElapsedTicks;
            long elapsedNs = elapsedTicks * 1_000_000_000L / Stopwatch.Frequency;
            int tid = Environment.CurrentManagedThreadId;
            // UI thread 여부 — Application.Current 가 null 일 수 있으므로 null-safe
            bool onUi = Application.Current?.Dispatcher.CheckAccess() ?? false;

            var sb = new StringBuilder(256);
            sb.Append('[').Append(DateTime.UtcNow.ToString("O")).Append('|');
            sb.Append("ns=").Append(elapsedNs).Append('|');
            sb.Append('#').Append(seq.ToString("D5")).Append('|');
            sb.Append("tid=").Append(tid).Append('|');
            sb.Append("ui=").Append(onUi ? '1' : '0').Append('|');
            sb.Append("evt=").Append(eventName);
            foreach (var (k, v) in fields)
                sb.Append(' ').Append(k).Append('=').Append(v?.ToString() ?? "null");
            sb.Append(']');

            WriteLine(sb.ToString());
        }
        catch
        {
            // Fail-silent — 진단 로그가 production throw 를 일으키면 안 됨
        }
    }

    /// <summary>
    /// Scope-based timing helper (LEVEL_TIMING 이상에서만 동작).
    /// 호출자는 반환된 tick 을 로컬 변수로 보관하고 MarkExit 에 전달.
    /// QPC 기반 — Dispatcher 상호작용 없음.
    /// </summary>
    public static long MarkEnter(string scopeName)
    {
        if (GetLevel() < LEVEL_TIMING) return 0;
        long t = _stopwatch.ElapsedTicks;
        LogEvent(LEVEL_TIMING, scopeName + ":enter", ("tick", t));
        return t;
    }

    /// <summary>MarkEnter 와 쌍을 이루는 종료 측정.</summary>
    public static void MarkExit(string scopeName, long enterTick)
    {
        if (GetLevel() < LEVEL_TIMING) return;
        long exitTick = _stopwatch.ElapsedTicks;
        long elapsedNs = (exitTick - enterTick) * 1_000_000_000L / Stopwatch.Frequency;
        LogEvent(LEVEL_TIMING, scopeName + ":exit",
            ("tick", exitTick), ("elapsed_ns", elapsedNs));
    }

    // ---------------------------------------------------------------------------
    // Internal helpers
    // ---------------------------------------------------------------------------

    private static int GetLevel()
    {
        if (_level >= 0) return _level;
        // Lazy init — 최초 호출 시에만 env-var 조회, 이후 캐시된 _level 반환
        try
        {
            var v = Environment.GetEnvironmentVariable("GHOSTWIN_RENDERDIAG");
            _level = int.TryParse(v, out var n) && n >= LEVEL_OFF && n <= LEVEL_STATE
                ? n
                : LEVEL_OFF;
        }
        catch
        {
            _level = LEVEL_OFF;
        }
        return _level;
    }

    private static void WriteLine(string line)
    {
        // Debug.WriteLine → OutputDebugString (DebugView/VS Output 에서 가시)
        Debug.WriteLine(line);

        // File 출력 — lock 으로 thread-safe 보장, 예외는 swallow
        lock (_lock)
        {
            try
            {
                // _logPath 초기화는 lock 안에서만 수행 (double-checked locking 불필요)
                _logPath ??= ResolveLogPath();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // swallow — file I/O failure 는 무시 (Debug.WriteLine 으로 충분)
            }
        }
    }

    private static string ResolveLogPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostWin", "diagnostics");
        Directory.CreateDirectory(dir);
        // 일자별 rotation — 30회 reproduction 분량 + multi-day session 대응
        return Path.Combine(dir, $"render_{DateTime.UtcNow:yyyyMMdd}.log");
    }
}
