# RenderDiag 오버헤드 측정 — BC-08 Research

> **결론 (2026-04-14, 정적 분석)**: `GHOSTWIN_RENDERDIAG` 미설정 (Release default) 에서 RenderDiag 는 **process 당 env-var 1회 조회 + 이후 cached int compare 1회** 만 발생. 실질 런타임 overhead ≈ 0. 정확한 on-state 수치 측정은 사용자 hardware profiling 필요.

## 정적 분석 결과

### Off state (GHOSTWIN_RENDERDIAG 미설정)

`src/GhostWin.App/Diagnostics/RenderDiag.cs:63-68`:

```csharp
public static void LogEvent(int requiredLevel, string eventName, params (string, object?)[] fields)
{
    if (GetLevel() < requiredLevel) return;  // ← early-return here
    ...
}
```

`GetLevel()` (L126+):
- 최초 호출: `Environment.GetEnvironmentVariable("GHOSTWIN_RENDERDIAG")` 1회 + `_level` 필드 캐시
- 이후 호출: `_level` 읽기 + int compare만

**Off 상태 cost per LogEvent call**:
- First call per process: ~1 μs (env-var lookup)
- Subsequent calls: ~1 ns (int compare)

**주의**: `params (string, object?)[] fields` 는 호출자 측에서 array 할당이 발생. GC 압박 가능성이 있으나, off 상태에서는 array 는 만들어지지만 내용 enumeration 은 안 됨. 실질 영향은 call site 가 많은 hot path 에서만 유의미.

### On state (level 3 — STATE)

- Interlocked.Increment (seq) — atomic op
- Stopwatch.ElapsedTicks + ns 변환 — low-cost
- StringBuilder 생성 (256 capacity) — heap allocation
- File.AppendAllText 또는 FileStream.Write — I/O 동기 호출
- `static _lock` monitor — contention은 UI thread only 에서 호출되므로 거의 없음

**예상 per-event cost**: ~10~50 μs (I/O 포함, SSD 기준)

### Hot path 분석

RenderDiag 호출 지점 (grep 결과):

```
MainWindow.xaml.cs (lifecycle: BuildWindowCore, HostReady, OnHostReady)
Controls/PaneContainerControl.cs (lifecycle + surface create)
Controls/TerminalHostControl.cs (surface create)
```

모두 **lifecycle / setup 시점** — 프레임 렌더 hot loop (60 Hz+) 가 아님. 따라서:
- Off state overhead: 사실상 무시 가능
- On state overhead: 세션 생성/창 크기 변경 등 저빈도 이벤트에서만 누적, FPS 영향 없음

## 사용자 hardware 측정 가이드

정확한 수치가 필요하면:

### 측정 1: 세션 시작 시간 (setup overhead)

```powershell
# Off
scripts/repro_first_pane.ps1 -Iterations 30 -DelayMs 500 -RenderDiagLevel 0

# On (level 3)
scripts/repro_first_pane.ps1 -Iterations 30 -DelayMs 500 -RenderDiagLevel 3

# summary.json 의 results[*].elapsed_ms 평균 비교
```

`elapsed_ms` 는 kill + launch + DelayMs + screenshot + kill 전체를 포함. `DelayMs=500` 고정으로 noise 감소.

### 측정 2: 프레임 렌더 시간 (FPS)

- Windows Performance Toolkit (WPT) / PresentMon 을 사용해 FPS 측정
- GhostWin 에서 `GHOSTWIN_RENDERDIAG=0` / `=3` 각각 동일 워크로드 (예: `ls -la / | head -1000`) 실행
- PresentMon 으로 프레임 타이밍 비교

### 측정 3: Micro-benchmark

BenchmarkDotNet 으로 `LogEvent` off/on call latency 측정 가능하나, lifecycle 호출이라 체감 영향 무의미.

## 판정

| 항목 | 평가 |
|------|:----:|
| Off state overhead | **무시 가능** (정적 분석 확인) |
| On state overhead | **저빈도 lifecycle 이벤트에만 누적** — FPS 무관 |
| 실측 필요 여부 | **WONTFIX 가능** — on-state 가 성능 문제 일으키는 증거 없음 |
| 후속 조치 | 사용자가 특정 시나리오에서 이상 관찰 시 측정 1 실행 후 재평가 |

## 권고

BC-08 closure 권장 — 정적 분석 결과가 성능 문제 없음을 뒷받침. 실측은 **실제 이상 관찰 시** 진행. 현재 M-11 ~ M-13 기능 진행을 막을 이유 없음.
