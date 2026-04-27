# Design-Implementation Gap Analysis: Phase 6-C External Integration

> **분석 대상**: Phase 6-C (Named Pipe Hook + git 사이드바)
> **설계 문서**: `docs/02-design/features/phase-6-c-external-integration.design.md`
> **분석일**: 2026-04-16

---

## 1. 전체 점수

| Wave | 범위 | 점수 | 상태 |
|:----:|------|:----:|:----:|
| W1 | Named Pipe 서버 | 97% | OK |
| W2 | ghostwin-hook.exe CLI | 90% | OK |
| W3 | 이벤트 라우팅 | 95% | OK |
| W4 | 세션 매칭 (GHOSTWIN_SESSION_ID) | 100% | OK |
| W5 | git branch 사이드바 | 95% | OK |
| W6 | PR 감지 (gh CLI) | 0% | 미구현 |
| **전체** | | **92%** | **OK** |

> W6은 설계 문서에서 "선택적 (Wave 6)" 로 명시되어 있으므로 가중치를 낮게 적용.
> W6 제외 시 전체 Match Rate = **95%**.

---

## 2. Wave별 상세 비교

### W1: Named Pipe 서버 (97%)

| 항목 | 설계 | 구현 | 일치 |
|------|------|------|:----:|
| HookMessage record | `HookMessage(Event, SessionId, Cwd, Data)` | 동일 | OK |
| HookData record | `HookData(StopHookReason, NotificationType, Message, Status)` | 동일 | OK |
| IHookPipeServer | `StartAsync, StopAsync, IsRunning` | 동일 | OK |
| HookPipeServer 구현 | ListenLoop, JSON 파싱, 응답 전송 | 동일 | OK |
| JsonSerializerOptions | 인라인 `new JsonSerializerOptions` | `static readonly` 필드로 추출 | 개선 |
| DI 등록 | `services.AddSingleton<IHookPipeServer>(hookServer)` | 동일 | OK |
| 시작 위치 | `mainWindow.Show() 직후` | `hookServer.StartAsync()` 후 `mainWindow.Show()` (순서 반대) | 차이 |
| 종료 위치 | `MainWindow.OnClosing 엔진 DetachCallbacks 전` | 동일 | OK |
| StopAsync 예외 처리 | 없음 | `TimeoutException`, `OperationCanceledException` catch 추가 | 개선 |

**차이 1건**:
- 시작 순서: 설계는 `mainWindow.Show()` 직후, 구현은 `Show()` 직전. 기능적 영향 없음 (파이프는 비동기 대기이므로 순서 무관).

### W2: ghostwin-hook.exe CLI (90%)

| 항목 | 설계 | 구현 | 일치 |
|------|------|------|:----:|
| 프로젝트 생성 | `GhostWin.Hook.csproj` | 동일 | OK |
| stdin JSON 읽기 | `Console.In.ReadToEnd()` | 동일 | OK |
| 환경변수 GHOSTWIN_SESSION_ID 읽기 | `Environment.GetEnvironmentVariable` | 동일 | OK |
| JSON 필드 추출 (cwd, session_id 등) | `JsonDocument.Parse` | 동일 | OK |
| 1초 타임아웃 | `pipe.Connect(timeout: 1000)` | 동일 | OK |
| 연결 실패 시 exit 0 | `catch {}` + `return 0` | 동일 | OK |
| 솔루션 등록 | GhostWin.sln에 추가 | 등록 확인됨 | OK |

**차이 2건**:

| # | 항목 | 설계 | 구현 | 영향 |
|:-:|------|------|------|------|
| 1 | `.csproj` Publish 설정 | `PublishSingleFile`, `SelfContained`, `PublishTrimmed`, `RuntimeIdentifier=win-x64` 포함 | 미포함 (`ImplicitUsings`, `Nullable`, `AssemblyName` 만 있음) | 낮음 |
| 2 | JSON 직렬화 옵션 | `new JsonSerializerOptions` 없이 기본 직렬화 | `JsonNamingPolicy.SnakeCaseLower` 옵션 추가 | 개선 |

**차이 1 상세**: `.csproj`에 `PublishSingleFile`/`SelfContained`/`PublishTrimmed`/`RuntimeIdentifier` 가 없음. 설계 문서 Section 12의 S-3 ("개발 중은 `dotnet run`으로 실행, 릴리스 시 self-contained")에 따른 의도적 간소화로 판단. 릴리스 빌드 시 추가 필요.

### W3: 이벤트 라우팅 (95%)

| 항목 | 설계 | 구현 | 일치 |
|------|------|------|:----:|
| stop -> Idle | `session.AgentState = AgentState.Idle` + `DismissAttention` | 동일 | OK |
| notify -> HandleOscEvent | `oscService.HandleOscEvent(session.Id, title, body)` | 동일 | OK |
| prompt -> Running | `session.AgentState = AgentState.Running` | 동일 | OK |
| cwd-changed -> UpdateCwd | `sessionMgr.UpdateCwd(session.Id, msg.Cwd)` | 동일 | OK |
| set-status -> AgentState 파싱 | `Enum.TryParse<AgentState>` | 동일 | OK |
| 워크스페이스 동기화 | `wsSvc.FindWorkspaceBySessionId` 후 AgentState 반영 | 동일 | OK |

**차이 1건**:

| # | 항목 | 설계 | 구현 | 영향 |
|:-:|------|------|------|------|
| 1 | MatchSession 시그니처 | `MatchSession(msg, sessionMgr, wsSvc)` (3인자) | `MatchSession(msg, sessionMgr)` (2인자, wsSvc 미사용) | 없음 |

설계의 MatchSession은 `wsSvc` 인자를 받지만 실제 로직에서 사용하지 않음. 구현에서 불필요한 인자를 제거한 것은 올바른 간소화.

### W4: 세션 매칭 (100%)

| 항목 | 설계 | 구현 | 일치 |
|------|------|------|:----:|
| SessionConfig에 session_id 필드 | `uint32_t session_id = 0` | 동일 (line 41) | OK |
| build_environment_block 시그니처 | `uint32_t session_id` 인자 | `uint32_t session_id = 0` (기본값) | OK |
| GHOSTWIN_SESSION_ID 환경변수 주입 | `remove_env_var` + 문자열 삽입 | 동일 (line 125-134) | OK |
| session_manager.cpp에서 config.session_id 설정 | `config.session_id = sess->id` | 동일 (line 134) | OK |
| C# MatchSession 우선순위 | 1순위 SessionId, 2순위 CWD | 동일 | OK |
| session.h에 env_session_id 필드 | 설계에 없음 | 추가됨 (부가 필드, 기능 영향 없음) | OK |

### W5: git branch 사이드바 (95%)

| 항목 | 설계 | 구현 | 일치 |
|------|------|------|:----:|
| SessionInfo.GitBranch | `[ObservableProperty] string _gitBranch = ""` | 동일 | OK |
| SessionInfo.GitPrInfo | `[ObservableProperty] string _gitPrInfo = ""` | 동일 | OK |
| WorkspaceInfo.GitBranch | 미러링 | 동일 | OK |
| WorkspaceInfo.GitPrInfo | 미러링 | 동일 | OK |
| WorkspaceItemViewModel.GitBranch | `_workspace.GitBranch` | 동일 | OK |
| WorkspaceItemViewModel.HasGitBranch | `!string.IsNullOrEmpty(_workspace.GitBranch)` | 동일 | OK |
| OnWorkspacePropertyChanged | GitBranch/GitPrInfo 감지 | 동일 | OK |
| TickGitStatus | `Process.Start("git", ...)` | 동일 (변경 감지 최적화 추가) | 개선 |
| XAML TextBlock | `<Run Text="{Binding GitBranch}"/>` | `Mode=OneWay` 추가 | 개선 |
| 5초 폴링 카운터 | `gitPollCounter % 5 == 0` | 동일 (인스턴스 필드 `_gitPollCounter`) | OK |

**개선 2건**:

| # | 항목 | 설계 | 구현 | 비고 |
|:-:|------|------|------|------|
| 1 | TickGitStatus 변경 감지 | 무조건 대입 | `s.GitBranch != branch` 비교 후 변경 시에만 대입 + 비-git 디렉토리 시 빈 문자열 복구 | 불필요한 PropertyChanged 이벤트 방지 |
| 2 | XAML Run Binding | `{Binding GitBranch}` | `{Binding GitBranch, Mode=OneWay}` | record 기반 바인딩 경고 방지 |

### W6: PR 감지 (0% -- 의도적 미구현)

| 항목 | 설계 | 구현 | 일치 |
|------|------|------|:----:|
| TickGitPrStatus 메서드 | 30초 폴링, `gh pr view` 호출 | 미구현 | 미구현 |
| 30초 폴링 타이머 | _cwdPollTimer 내 별도 카운터 | 미구현 | 미구현 |

**판정**: 설계 문서 Section 8.2에 "(선택적, Wave 6)" 로 명시. Section 12 의도적 간소화 S-4에 "`gh` CLI 의존 (미설치 시 graceful skip)"로 기록. v1 범위에서 의도적 생략으로 판정.

---

## 3. 차이 요약

### 미구현 (설계 O, 구현 X)

| # | 항목 | 설계 위치 | 영향 | 비고 |
|:-:|------|----------|:----:|------|
| 1 | TickGitPrStatus (gh CLI PR 감지) | Section 8.2 | 낮음 | 설계에서 "선택적" 명시 (S-4) |
| 2 | .csproj Publish 속성 4개 | Section 4.1 | 낮음 | 개발 중은 dotnet run (S-3) |

### 의도적 변경 (설계와 다르지만 개선)

| # | 항목 | 설계 | 구현 | 판정 |
|:-:|------|------|------|:----:|
| 1 | JsonSerializerOptions 재사용 | 매 요청마다 `new` | `static readonly` 필드 | 개선 |
| 2 | StopAsync 예외 처리 | 없음 | `TimeoutException`/`OperationCanceledException` catch | 개선 |
| 3 | MatchSession 시그니처 | 3인자 (wsSvc 포함) | 2인자 (wsSvc 제거) | 개선 |
| 4 | TickGitStatus 변경 감지 | 무조건 대입 | 변경 시에만 대입 | 개선 |
| 5 | XAML Binding Mode | 기본값 | `Mode=OneWay` 명시 | 개선 |
| 6 | hookServer 시작 순서 | Show() 직후 | Show() 직전 | 동등 |
| 7 | CLI JSON 직렬화 | 기본 직렬화 | SnakeCaseLower 정책 추가 | 개선 |

---

## 4. 아키텍처/컨벤션 점수

| 카테고리 | 점수 | 상태 |
|----------|:----:|:----:|
| 설계 일치도 (W6 제외) | 95% | OK |
| 설계 일치도 (W6 포함) | 92% | OK |
| 아키텍처 준수 | 100% | OK |
| 컨벤션 준수 | 98% | OK |
| **종합 (W6 제외)** | **97%** | **OK** |

**아키텍처 준수 세부**:
- DI 패턴: `IHookPipeServer` 인터페이스 분리 + `services.AddSingleton` 등록 -- 기존 패턴 준수
- 스레드 안전: `Dispatcher.BeginInvoke` 로 UI 스레드 전환 -- 설계 명세 준수
- 기존 OSC 경로 재사용: `IOscNotificationService.HandleOscEvent()` 호출 -- 설계 의도 준수
- C++ 환경변수 주입: `build_environment_block` 기존 TERM 패턴 동일 방식 -- 설계 의도 준수

---

## 5. 권장 사항

### 즉시 조치 불필요

Match Rate 95% (W6 제외) -- 설계와 구현이 잘 일치함. 의도적 변경은 모두 개선 방향.

### 향후 작업 (v2 또는 릴리스 시)

1. **TickGitPrStatus 구현 결정** -- PR 감지가 필요하면 W6 설계대로 구현, 불필요하면 설계 문서에서 제거
2. **GhostWin.Hook.csproj Publish 속성 추가** -- 릴리스 빌드 파이프라인 구성 시 `PublishSingleFile`/`SelfContained`/`PublishTrimmed`/`RuntimeIdentifier` 추가

---

*Phase 6-C Design-Implementation Gap Analysis v1.0 (2026-04-16)*
