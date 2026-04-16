# Design — Phase 6-C: 외부 통합 (External Integration)

> **문서 종류**: Design (구현 명세)
> **작성일**: 2026-04-17
> **Plan 참조**: `docs/01-plan/features/phase-6-c-external-integration.plan.md`
> **PRD 참조**: `docs/00-pm/phase-6-c-external-integration.prd.md`

---

## 1. 한 줄 요약

Named Pipe 훅 서버(`\\.\pipe\ghostwin-hook`, C# `NamedPipeServerStream`) + 경량 CLI(`ghostwin-hook.exe`)로 Claude Code Hooks JSON을 직접 수신하여 AgentState를 정밀 전환하고, git branch/PR 상태를 사이드바에 표시.

---

## 2. 구현 순서 (6 Waves)

| Wave | 범위 | 의존 | 검증 |
|:----:|------|:---:|------|
| **W1** | Named Pipe 서버 (HookPipeServer + DI 등록 + 시작/종료) | — | 파이프 연결 echo 테스트 |
| **W2** | ghostwin-hook.exe CLI (stdin→Pipe JSON 전송) | W1 | `echo '...' \| ghostwin-hook stop` |
| **W3** | 이벤트 라우팅 (JSON → AgentState 전환 + 알림 패널) | W1,W2 | Stop/Notify → AgentState 변경 확인 |
| **W4** | 세션 매칭 (GHOSTWIN_SESSION_ID 환경변수 주입 + CWD 폴백) | W3 | 멀티 탭에서 올바른 탭 매칭 |
| **W5** | git branch 감지 + 사이드바 표시 | — | branch 이름 표시 확인 |
| **W6** | Claude Code 실제 연동 + PR + 통합 검증 | W1-W5 | Claude Code 세션 Stop → Idle |

---

## 3. Wave 1 — Named Pipe 서버

### 3.1 HookMessage 모델 (신규)

**파일**: `src/GhostWin.Core/Models/HookMessage.cs`

```csharp
namespace GhostWin.Core.Models;

public record HookMessage(
    string Event,          // "stop", "notify", "prompt", "cwd-changed", "set-status"
    string? SessionId,     // GHOSTWIN_SESSION_ID 또는 null
    string? Cwd,           // Claude Code cwd
    HookData? Data);

public record HookData(
    string? StopHookReason,      // "end_turn" 등
    string? NotificationType,    // "idle_prompt", "permission_prompt" 등
    string? Message,             // 알림 메시지
    string? Status);             // set-status 용: "idle", "running", "error"
```

### 3.2 IHookPipeServer 인터페이스 (신규)

**파일**: `src/GhostWin.Core/Interfaces/IHookPipeServer.cs`

```csharp
namespace GhostWin.Core.Interfaces;

public interface IHookPipeServer
{
    Task StartAsync();
    Task StopAsync();
    bool IsRunning { get; }
}
```

### 3.3 HookPipeServer 구현 (신규)

**파일**: `src/GhostWin.Services/HookPipeServer.cs`

```csharp
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
            await _listenTask.WaitAsync(TimeSpan.FromSeconds(2))
                             .ConfigureAwait(false);
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

                var msg = JsonSerializer.Deserialize<HookMessage>(line,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                    });

                if (msg != null)
                    _onMessage(msg);

                // 응답 (클라이언트가 대기 중이면)
                using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
                await writer.WriteLineAsync("{\"ok\":true}");
            }
            catch (OperationCanceledException) { break; }
            catch (IOException) { /* 클라이언트 연결 끊김 — 무시 */ }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[HookPipeServer] {ex.Message}");
            }
        }
    }
}
```

**스레드 안전**: `_onMessage` 콜백은 Task.Run 스레드에서 호출됨. 콜백 내부에서 `Dispatcher.BeginInvoke`로 UI 스레드 전환 필수.

### 3.4 DI 등록 + 시작/종료 (App.xaml.cs)

```csharp
// OnStartup에서 DI 등록 (IOscNotificationService 등록 뒤)
var hookServer = new HookPipeServer(msg =>
{
    Dispatcher.BeginInvoke(() => HandleHookMessage(msg));
});
services.AddSingleton<IHookPipeServer>(hookServer);

// mainWindow.Show() 직후
_ = hookServer.StartAsync();

// MainWindow.OnClosing에서 (엔진 DetachCallbacks 전)
var hookSrv = Ioc.Default.GetService<IHookPipeServer>() as HookPipeServer;
if (hookSrv != null)
    await hookSrv.StopAsync().WaitAsync(TimeSpan.FromMilliseconds(100));
```

`HandleHookMessage` 메서드 — App.xaml.cs에 추가:

```csharp
private void HandleHookMessage(HookMessage msg)
{
    var sessionMgr = Ioc.Default.GetService<ISessionManager>();
    var oscService = Ioc.Default.GetService<IOscNotificationService>();
    var wsSvc = Ioc.Default.GetService<IWorkspaceService>();
    if (sessionMgr == null || oscService == null || wsSvc == null) return;

    // 세션 매칭
    var session = MatchSession(msg, sessionMgr, wsSvc);
    if (session == null) return;

    switch (msg.Event)
    {
        case "stop":
            session.AgentState = AgentState.Idle;
            oscService.DismissAttention(session.Id);
            var ws1 = wsSvc.FindWorkspaceBySessionId(session.Id);
            if (ws1 != null) ws1.AgentState = AgentState.Idle;
            break;

        case "notify":
            var title = msg.Data?.NotificationType ?? "Notification";
            var body = msg.Data?.Message ?? "";
            oscService.HandleOscEvent(session.Id, title, body);
            break;

        case "prompt":
            session.AgentState = AgentState.Running;
            var ws2 = wsSvc.FindWorkspaceBySessionId(session.Id);
            if (ws2 != null) ws2.AgentState = AgentState.Running;
            break;

        case "cwd-changed":
            if (!string.IsNullOrEmpty(msg.Cwd))
                sessionMgr.UpdateCwd(session.Id, msg.Cwd);
            break;

        case "set-status":
            if (Enum.TryParse<AgentState>(msg.Data?.Status, true, out var state))
            {
                session.AgentState = state;
                var ws3 = wsSvc.FindWorkspaceBySessionId(session.Id);
                if (ws3 != null) ws3.AgentState = state;
            }
            break;
    }
}

private static SessionInfo? MatchSession(
    HookMessage msg, ISessionManager sessionMgr, IWorkspaceService wsSvc)
{
    // 1순위: GHOSTWIN_SESSION_ID (uint)
    if (!string.IsNullOrEmpty(msg.SessionId) &&
        uint.TryParse(msg.SessionId, out var sid))
    {
        return sessionMgr.Sessions.FirstOrDefault(s => s.Id == sid);
    }

    // 2순위: CWD 매칭
    if (!string.IsNullOrEmpty(msg.Cwd))
    {
        return sessionMgr.Sessions.FirstOrDefault(s =>
            string.Equals(s.Cwd, msg.Cwd, StringComparison.OrdinalIgnoreCase));
    }

    return null;
}
```

---

## 4. Wave 2 — ghostwin-hook.exe CLI

### 4.1 프로젝트 구조

**신규 프로젝트**: `src/GhostWin.Hook/GhostWin.Hook.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <PublishTrimmed>true</PublishTrimmed>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
</Project>
```

### 4.2 Program.cs

**파일**: `src/GhostWin.Hook/Program.cs`

```csharp
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

if (args.Length == 0) { Console.Error.WriteLine("Usage: ghostwin-hook <event>"); return 1; }

var eventName = args[0]; // "stop", "notify", "prompt", "set-status"

// Claude Code가 stdin으로 JSON을 전달
string? stdinJson = null;
if (Console.IsInputRedirected)
{
    stdinJson = Console.In.ReadToEnd();
}

// Claude Code Hooks JSON에서 필요한 필드 추출
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
    catch { /* 파싱 실패 — 무시하고 진행 */ }
}

// status 직접 설정 (set-status idle/running/error)
string? status = eventName == "set-status" && args.Length > 1 ? args[1] : null;

// GhostWin Named Pipe에 전송
var payload = JsonSerializer.Serialize(new
{
    @event = eventName == "set-status" ? "set-status" : eventName,
    session_id = sessionId,
    cwd,
    data = new
    {
        stop_hook_reason = stopReason,
        notification_type = notificationType,
        message,
        status
    }
});

try
{
    using var pipe = new NamedPipeClientStream(".", "ghostwin-hook",
        PipeDirection.InOut, PipeOptions.None);
    pipe.Connect(timeout: 1000); // 1초 타임아웃

    using var writer = new StreamWriter(pipe, Encoding.UTF8) { AutoFlush = true };
    writer.WriteLine(payload);

    // 응답 읽기 (선택적)
    using var reader = new StreamReader(pipe, Encoding.UTF8);
    _ = reader.ReadLine();
}
catch
{
    // GhostWin이 실행 중이 아니면 조용히 종료
    // Claude Code 훅이 실패로 처리하지 않도록 exit 0
}

return 0;
```

---

## 5. Wave 3 — 이벤트 라우팅

Wave 1의 `HandleHookMessage`에서 이미 구현. 핵심은 **OSC 기반 기존 경로와 Named Pipe 경로의 공존**:

```
OSC 9/99/777 (기존, Phase 6-A)
  └── IOscNotificationService.HandleOscEvent()
        ├── NeedsAttention = true
        ├── AgentState = WaitingForInput
        └── 알림 패널 + Toast

Named Pipe "notify" (신규, Phase 6-C)
  └── HandleHookMessage() → IOscNotificationService.HandleOscEvent()
        ├── 동일 경로 재사용!
        └── 추가: NotificationType 구분 (idle_prompt vs permission_prompt)

Named Pipe "stop" (신규)
  └── HandleHookMessage() → AgentState = Idle (직접 설정)
        └── DismissAttention (amber dot 소등)
```

**D-5 결정 확정**: OSC와 Named Pipe 동등. 먼저 도착한 이벤트가 상태를 변경. 100ms debounce가 중복 이벤트를 자연스럽게 필터링.

---

## 6. Wave 4 — 세션 매칭 (GHOSTWIN_SESSION_ID)

### 6.1 환경변수 주입 (C++)

**파일**: `src/conpty/conpty_session.cpp` — `build_environment_block()` 수정

기존 `TERM=xterm-256color` 주입 패턴과 동일하게 `GHOSTWIN_SESSION_ID` 추가:

```cpp
// build_environment_block() 끝부분, TERM 주입 직후
// Phase 6-C: GHOSTWIN_SESSION_ID 주입 (Named Pipe 세션 매칭용)
// session_id는 ConPtySession::start()에서 전달받는 값.
// 현재 build_environment_block()은 static 함수이므로, session_id를 인자로 추가.
```

`build_environment_block`에 session_id 인자를 추가해야 합니다:

```cpp
// 시그니처 변경
std::vector<wchar_t> build_environment_block(uint32_t session_id) {
    // ... 기존 코드 (parent env 복사 + TERM 추가) ...

    // GHOSTWIN_SESSION_ID 추가
    remove_env_var(block, L"GHOSTWIN_SESSION_ID=", 21);
    auto sid_var = L"GHOSTWIN_SESSION_ID=" + std::to_wstring(session_id);
    if (!block.empty()) block.pop_back();
    block.insert(block.end(), sid_var.begin(), sid_var.end());
    block.push_back(L'\0');
    block.push_back(L'\0');

    return block;
}
```

**호출 변경**: `ConPtySession::start()` (line ~490)에서 `build_environment_block()`에 session_id 전달.

### 6.2 세션 매칭 로직 (C#)

Wave 1의 `MatchSession()` 메서드에 이미 구현. 우선순위:
1. `GHOSTWIN_SESSION_ID` (uint 정확 매칭)
2. `cwd` (대소문자 무시 경로 비교)

---

## 7. Wave 5 — git branch/PR 표시

### 7.1 SessionInfo 확장

```csharp
// SessionInfo.cs에 추가
[ObservableProperty]
private string _gitBranch = "";

[ObservableProperty]
private string _gitPrInfo = "";
```

### 7.2 WorkspaceInfo 미러링

```csharp
// WorkspaceInfo.cs에 추가
[ObservableProperty]
private string _gitBranch = "";

[ObservableProperty]
private string _gitPrInfo = "";
```

### 7.3 git 감지 로직 (SessionManager 확장)

기존 `_cwdPollTimer` Tick 이벤트에 병합:

```csharp
// SessionManager에 추가
public void TickGitStatus()
{
    foreach (var s in Sessions)
    {
        if (string.IsNullOrEmpty(s.Cwd)) continue;
        try
        {
            var psi = new ProcessStartInfo("git", $"-C \"{s.Cwd}\" branch --show-current")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) continue;
            var branch = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(1000);
            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(branch))
            {
                s.GitBranch = branch;
                var ws = _workspaceService?.FindWorkspaceBySessionId(s.Id);
                if (ws != null) ws.GitBranch = branch;
            }
        }
        catch { /* git 미설치 또는 git 디렉토리 아님 — 무시 */ }
    }
}
```

**폴링 주기**: 기존 `_cwdPollTimer` (1초)에서 5초마다 한 번 실행 (카운터 기반).

```csharp
// MainWindow.xaml.cs _cwdPollTimer.Tick에 추가
static int gitPollCounter = 0;
if (++gitPollCounter % 5 == 0)
{
    try { (_sessionManager as Services.SessionManager)?.TickGitStatus(); }
    catch (Exception ex) { App.WriteCrashLog("GitPollTimer.Tick", ex); }
}
```

### 7.4 사이드바 XAML

기존 CWD TextBlock 아래에 git 정보 추가:

```xaml
<!-- 기존 CWD 표시 아래에 추가 -->
<TextBlock FontSize="10" Margin="0,1,0,0"
           Foreground="#636366"
           FontFamily="Segoe UI Variable"
           Visibility="{Binding HasGitBranch,
               Converter={StaticResource BoolToVisibility}}">
    <Run Text="{Binding GitBranch}"/>
    <Run Text="{Binding GitPrInfo}" Foreground="#8E8E93"/>
</TextBlock>
```

### 7.5 WorkspaceItemViewModel 확장

```csharp
public string GitBranch => _workspace.GitBranch;
public string GitPrInfo => _workspace.GitPrInfo;
public bool HasGitBranch => !string.IsNullOrEmpty(_workspace.GitBranch);
```

`OnWorkspacePropertyChanged`에 추가:

```csharp
if (e.PropertyName == nameof(WorkspaceInfo.GitBranch))
{
    OnPropertyChanged(nameof(GitBranch));
    OnPropertyChanged(nameof(HasGitBranch));
}
if (e.PropertyName == nameof(WorkspaceInfo.GitPrInfo))
    OnPropertyChanged(nameof(GitPrInfo));
```

---

## 8. Wave 6 — Claude Code 실제 연동

### 8.1 Claude Code settings.json 설정 예시

GhostWin 설치 디렉토리 기준 절대 경로:

```json
{
  "hooks": {
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "\"C:\\path\\to\\ghostwin-hook.exe\" stop"
          }
        ]
      }
    ],
    "Notification": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "\"C:\\path\\to\\ghostwin-hook.exe\" notify"
          }
        ]
      }
    ],
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "\"C:\\path\\to\\ghostwin-hook.exe\" prompt"
          }
        ]
      }
    ]
  }
}
```

### 8.2 PR 감지 (선택적, Wave 6)

```csharp
// SessionManager.TickGitPrStatus() — 30초마다 실행
public void TickGitPrStatus()
{
    foreach (var s in Sessions)
    {
        if (string.IsNullOrEmpty(s.Cwd) || string.IsNullOrEmpty(s.GitBranch)) continue;
        try
        {
            var psi = new ProcessStartInfo("gh",
                $"pr view --json number -q .number")
            {
                WorkingDirectory = s.Cwd,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) continue;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            if (proc.ExitCode == 0 && int.TryParse(output, out var prNum))
            {
                s.GitPrInfo = $"PR #{prNum}";
                var ws = _workspaceService?.FindWorkspaceBySessionId(s.Id);
                if (ws != null) ws.GitPrInfo = s.GitPrInfo;
            }
            else
            {
                s.GitPrInfo = "";
            }
        }
        catch { s.GitPrInfo = ""; }
    }
}
```

---

## 9. 전체 스레드 다이어그램

```
Claude Code (ConPTY 자식 프로세스)
  │
  ├── hooks.Stop → ghostwin-hook.exe stop
  │     └── NamedPipeClientStream → \\.\pipe\ghostwin-hook
  │
  ▼
HookPipeServer (Task.Run 스레드)
  │
  └── WaitForConnectionAsync → ReadLine → JSON 파싱
        │
        └── Dispatcher.BeginInvoke (UI 스레드)
              │
              ├── HandleHookMessage()
              │     ├── MatchSession (GHOSTWIN_SESSION_ID or CWD)
              │     ├── AgentState 전환
              │     ├── IOscNotificationService.HandleOscEvent() (notify)
              │     └── 알림 패널 + Toast (기존 경로 재사용)
              │
              └── (동시) OSC 9/99/777 콜백도 별도 경로로 동작
                    └── IOscNotificationService.HandleOscEvent() (기존)
```

---

## 10. 파일 변경 목록 (최종)

### 신규 프로젝트 (1개)

| 프로젝트 | 내용 |
|---------|------|
| `src/GhostWin.Hook/` | ghostwin-hook.exe CLI (C# 콘솔 앱) |

### 신규 파일 (4개)

| 파일 | 프로젝트 | 내용 |
|------|---------|------|
| `HookMessage.cs` | GhostWin.Core/Models | 훅 메시지 record |
| `IHookPipeServer.cs` | GhostWin.Core/Interfaces | 서버 인터페이스 |
| `HookPipeServer.cs` | GhostWin.Services | Named Pipe 서버 구현 |
| `Program.cs` | GhostWin.Hook | CLI 진입점 |

### 변경 파일 (8개)

| 파일 | 변경 내용 |
|------|----------|
| `SessionInfo.cs` | GitBranch, GitPrInfo 프로퍼티 |
| `WorkspaceInfo.cs` | GitBranch, GitPrInfo 미러링 |
| `WorkspaceItemViewModel.cs` | GitBranch, GitPrInfo, HasGitBranch 바인딩 |
| `MainWindow.xaml` | 사이드바에 git 정보 TextBlock |
| `MainWindow.xaml.cs` | git 폴링 카운터 추가 |
| `App.xaml.cs` | HookPipeServer DI + HandleHookMessage + 시작/종료 |
| `SessionManager.cs` | TickGitStatus, TickGitPrStatus |
| `conpty_session.cpp` | build_environment_block에 GHOSTWIN_SESSION_ID 주입 |
| `GhostWin.sln` | GhostWin.Hook 프로젝트 추가 |

**총계**: 신규 프로젝트 1 + 신규 4 + 변경 9 = **14개 파일**

---

## 11. 검증 계획

| # | 시나리오 | 검증 방법 |
|:-:|---------|----------|
| T-1 | Named Pipe 연결 | PowerShell에서 Named Pipe 클라이언트로 JSON 전송 |
| T-2 | ghostwin-hook stop | CLI 실행 → 탭 AgentState = Idle |
| T-3 | ghostwin-hook notify | CLI 실행 → WaitingForInput + 알림 패널 |
| T-4 | 세션 매칭 (환경변수) | 멀티 탭에서 올바른 탭에 이벤트 |
| T-5 | 세션 매칭 (CWD 폴백) | 환경변수 없이 CWD로 매칭 |
| T-6 | git branch 표시 | git 저장소에서 사이드바 확인 |
| T-7 | Claude Code 실제 연동 | settings.json 훅 등록 → Stop 시 Idle |
| T-8 | GhostWin 종료 시 Pipe 정리 | 종료 후 파이프 사라짐 확인 |
| T-9 | GhostWin 미실행 시 CLI | exit 0 (오류 없음) |

---

## 12. 의도적 간소화

| # | 항목 | 간소화 | 근거 |
|:-:|------|-------|------|
| S-1 | JSON-RPC 전체 API | 훅 수신만 (cmux의 workspace.list 등 미구현) | v1 범위 |
| S-2 | DACL 접근 제어 | 기본 파이프 보안 (현재 사용자 접근) | v1에서 충분 |
| S-3 | ghostwin-hook.exe publish | 개발 중은 `dotnet run`으로 실행 | 릴리스 시 self-contained |
| S-4 | PR 감지 | `gh` CLI 의존 (미설치 시 graceful skip) | REST API는 과도 |
| S-5 | SubagentStart/Stop | 미구현 | v2 범위 |

---

## 참조

- **Plan**: `docs/01-plan/features/phase-6-c-external-integration.plan.md`
- **Phase 6-A/B**: `docs/archive/2026-04/phase-6-a-osc-notification-ring/`, `docs/archive/2026-04/phase-6-b-notification-infra/`
- **cmux 리서치**: `docs/00-research/cmux-ai-agent-ux-research.md`
- **Claude Code Hooks**: [code.claude.com/docs/en/hooks-guide](https://code.claude.com/docs/en/hooks-guide)

---

*Phase 6-C Design v1.0 — External Integration (2026-04-17)*
