# Phase 5-E pane-split + Workspace Layer (v0.5) — cmux 대비 완성도 평가

> **평가일**: 2026-04-07
> **평가 방식**: 10-agent 병렬 리뷰 (rkit, opus model)
> **대상 커밋**: `3abd213` fix, `954cce4` feat, `d03a19a` docs
> **설계 문서**: `docs/02-design/features/pane-split.design.md` v0.5
> **cmux reference**: `docs/00-research/cmux-ai-agent-ux-research.md`, [cmux.com/docs](https://cmux.com/docs)

---

## Executive Summary — 다차원 완성도

단일 점수로 집계하면 관점별 편차가 손실됩니다. **각 관점이 서로 다른 기준선**을 적용했기 때문에, 아래 6개 차원으로 분리합니다.

| 차원 | 점수 | 범위 | 평가자 |
|---|:---:|:---:|---|
| **설계 문서 claims 대비 구현** | **85%** | 72-88% | code-analyzer, dotnet-expert, design-validator |
| **cmux 3 top levels (Window/Workspace/Pane)** | **75%** | 55-88% | wpf-architect, cto-lead |
| **cmux 전체 feature (5 levels + 부가)** | **37%** | 35-55% | gap-detector (47 features 전수 매핑), frontend-architect |
| **UI/UX 퀄리티** | **35%** | 30-60% | frontend-architect |
| **프로덕션 준비도** | **35%** | 30-50% | enterprise-expert, security-architect |
| **자동화 테스트 커버리지** | **7%** | 0-15% | qa-strategist |

### 관점별 상세 점수

| # | Agent | 기능 완성도 | 품질 완성도 | Grade |
|---|---|:---:|:---:|:---:|
| 1 | wpf-architect | 55% | 70% | B/B+ |
| 2 | dotnet-expert | 85% | 70% | — |
| 3 | frontend-architect | 35% | 60% | — |
| 4 | code-analyzer | 88% | 78% | — |
| 5 | gap-detector | 37% (가중평균) | 68% | — |
| 6 | design-validator | 72% | 68% | C (6.2/10) |
| 7 | security-architect | 35% | 55% | — |
| 8 | qa-strategist | 7% | 30% | F |
| 9 | enterprise-expert | 35% | 50% | — |
| 10 | cto-lead | 55% | 70% | B+ |
| **평균** | — | **50%** | **62%** | — |
| **중앙값** | — | **46%** | **68%** | — |

### 종합 판단 (cto-lead 인용)

> "올바른 방향(cmux 모델)으로 한 단계 도약했고 fix 커밋이 v0.4의 누적 부채를 정리한 것은 긍정적. 다만 동일 세션에 5건의 critical bug가 동시 발견되었다는 사실 자체가 테스트 부재의 비용을 정량으로 보여줌. 다음 Phase는 새 기능이 아니라 테스트 + 부채 청산 9일 → dogfood 임계 도달 2주 → Surface 계층 → session-restore 순서를 권장."

---

## 1. 합의된 Critical Issues (3+ agents 동의)

### C1. BISECT mode 미해결 + 미문서화

**동의**: wpf-architect (M-2), design-validator (C-1/C-2/C-3), code-analyzer (추정), gap-detector (품질 평가), cto-lead (strategic)

**사실**:
- `WorkspaceService.cs:49` — `paneLayout.Initialize(sessionId, 0); // BISECT: surfaceId=0`
- `src/engine-api/ghostwin_engine.cpp:321` — `// BISECT: keep renderer's SwapChain for legacy path`
- `render_loop`의 `if (!active.empty())... else { legacy }` 분기 공존 (line 187-194)
- design §4 "Surface 전용 경로" 주장과 runtime이 불일치
- design 문서 전체에서 "BISECT" 단어 **0회**

**영향**:
- 설계가 주장하는 per-pane 다중 SwapChain이 실제로는 동작 안 함
- 분할 시 화면이 active pane만 렌더, 비활성 pane은 빈 화면 가능성 (확실하지 않음 — 사용자 보고 기반 수동 검증에서는 8/8 통과했으나 per-pane 렌더링 확인은 부족)
- design ↔ runtime 간 가장 큰 구조적 divergence

**필요 조치**:
- design 문서에 "1.4 현재 BISECT 상태" 섹션 신설, 종료 조건 명시
- `gw_surface_create` 경로를 workspace 생성 시점에 연결
- `render_loop`의 legacy else 분기 삭제 (Surface 검증 완료 후)
- design §4.4 `render_to_target` → `bind_surface`/`upload_and_draw`/`unbind_surface` 실제 구현 이름으로 수정

---

### C2. 종료 경로 이중화 (Environment.Exit 중복)

**동의**: wpf-architect (C-1), dotnet-expert (C2), code-analyzer (Critical), cto-lead, enterprise-expert

**사실**:
- `MainWindow.xaml.cs:184-189` — `OnClosing`에서 `Task.Run(() => { Dispose(); Environment.Exit(0); })`
- `App.xaml.cs:106` — `OnExit`에서 추가 `Environment.Exit(0)`
- 두 경로 모두 ConPty I/O thread 블로킹 회피 의도로 강제 종료 사용

**영향**:
- double Environment.Exit race 가능
- `OnClosing` Task.Run의 dispose 도중 UnobservedTaskException 발생 시 로그 유실
- `OnExit`의 `engine.RenderStop()` 호출이 이미 disposed된 native handle 접근 → AccessViolation 가능
- 사용자 규칙 ".claude/rules/behavior.md 우회 금지"와 충돌

**필요 조치**:
- 단일 종료 경로로 통합 (OnClosing 또는 OnExit 하나)
- ConPty I/O thread를 cancellable하게 재설계 (`CancelIoEx` 또는 `SetEvent` wait-object 깨우기)
- `Task.Wait(TimeSpan.FromSeconds(2))` 타임아웃 + fallback `Environment.Exit`

---

### C3. 자동화 테스트 부재

**동의**: qa-strategist (F), code-analyzer (indirect), cto-lead (C1), enterprise-expert

**사실**:
- `*.Tests.csproj` **0건** (qa-strategist 확인)
- design §11 Test Plan T-1~T-14 전부 **계획만**, 구현 없음
- C++ `tests/` 10건은 VtCore/ConPty/DX11/TSF만, pane-split 관련 0건
- `PaneNode.RemoveLeaf` grandparent splice 재설계가 순수 함수임에도 단위 테스트 없음
- 이번 세션의 10건 crash fix도 regression test 없음

**영향**:
- 리팩토링 비용이 매번 hand-regression 1일씩 추가
- Phase 5-F session-restore 작업 시 pane-split 회귀 감지 불가
- Match Rate 90% 게이트와 무관하게 **회귀 위험이 누적**

**필요 조치**:
- `tests/GhostWin.Core.Tests` 신설 (xUnit + FluentAssertions)
- 최우선: `PaneNode` T-1~T-5 (순수 로직, 외부 의존 없음)
- `PaneLayoutService` / `WorkspaceService` T-6~T-11 (Moq 기반)
- C++ `tests/surface_manager_race_test.cpp` (T-12~14 stress)
- Zero Script QA 방법론 적용 (`ghostwin-crash.log` JSON 전환 + PowerShell harness)

---

### C4. PropertyChanged 구독 라이프사이클 누락 (메모리 누수)

**동의**: wpf-architect (M-5), dotnet-expert (C4), code-analyzer (Critical)

**사실**:
- `WorkspaceService.cs:62-71` — `sessionInfo.PropertyChanged += (_, e) => ...` 람다가 `info`/`sessionInfo` 캡처
- `CloseWorkspace`에서 handler detach 없음
- 람다가 `info.Title = ...` mutation 호출 (dispatcher affinity 미검증)

**영향**:
- Workspace 닫은 후에도 SessionInfo → WorkspaceInfo reference 체인으로 GC 실패
- Long-running 세션에서 메모리 누수 누적 가능
- "확실하지 않음" — sessionInfo가 함께 사라지는 경로에서는 무해할 수 있으나, sidebar binding이 살아남는 경로에서 위험

**필요 조치**:
- `WorkspaceEntry`에 `PropertyChangedEventHandler` 필드 보관
- `CloseWorkspace`에서 `sessionInfo.PropertyChanged -= handler` 명시 detach
- 또는 WeakEventManager로 교체

---

### C5. Notification Ring 전무

**동의**: frontend-architect (Critical), gap-detector (Critical, E 카테고리 0%), cto-lead (indirect)

**사실**:
- cmux의 차별화 핵심 feature가 0% 구현
- OSC 9 / OSC 777 / OSC 99 파서 없음 (gap-detector E1)
- Pane 테두리 파란 링은 focus 표시 용도 (`PaneContainerControl.cs:333-338`)이지 알림용 아님
- Sidebar 미읽음 배지, 알림 패널, 데스크톱 토스트 전부 없음
- `⌘⇧I` 패널, `⌘⇧U` 미읽음 점프 단축키 없음

**영향**:
- 에이전트 터미널의 핵심 UX (백그라운드 작업 알림) 동작 불가
- cmux 대비 제품 차별화 상실
- 사용자가 다른 workspace에서 일어난 일을 알 방법 없음
- Claude Code OSC Hooks 연동(category I) 전체가 선결 요건 부재

**필요 조치 (점진적)**:
- Phase 1: OSC 9 파서만 (단순 알림, libghostty upstream에 이미 있을 가능성 — 확실하지 않음)
- Phase 2: Pane border blue ring + sidebar unread dot
- Phase 3: 알림 패널 + `⌘⇧I`/`⌘⇧U` 단축키
- Phase 4: WinRT ToastNotification (데스크톱 토스트)

---

## 2. 합의된 Major Issues (2+ agents 동의)

| # | 이슈 | 동의 agents | 핵심 증거 |
|---|---|---|---|
| M1 | **MoveFocus DFS-only (spatial 아님)** | wpf-architect, gap-detector, cto-lead | `PaneLayoutService.cs:144-174` leaves 인덱스 ±1. H-split{V{A,B}, C} 트리에서 Up/Down과 Left/Right가 동일 |
| M2 | **Sidebar 정보 밀도 부족** | frontend-architect, gap-detector | cmux sidebar 6종 정보 (git branch/PR/CWD 축약/포트/알림/배지) 중 Title/CWD만 구현 |
| M3 | **Surface layer 부재 (pane 내 multi-tab)** | frontend-architect, gap-detector, cto-lead (strategic 2순위) | Phase C 미구현. cmux는 1 pane = N surfaces 지원 |
| M4 | **CrashLog lock 부재 + UAC 경계 + PII** | dotnet-expert (C1), security-architect (High), enterprise-expert | `App.xaml.cs:79-87` `File.AppendAllText` lock 없음, `AppContext.BaseDirectory` (Program Files 하위일 수 있음), `ex.ToString()`에 PII 노출 |
| M5 | **Workspace Jump 1-8 (Ctrl+1~8) 부재** | frontend-architect, gap-detector (B5) | `MainWindow.xaml` KeyBinding 없음. 마우스 의존 강제 |
| M6 | **MVVM 위반**: PaneContainerControl이 IWorkspaceService 직접 주입 | wpf-architect (M-7/B-), code-analyzer (SRP 약점), enterprise-expert | `PaneContainerControl.Initialize(IWorkspaceService)` — View가 Service 직접 알게 됨 |
| M7 | **Workspace 전환 시 detached HwndHost child HWND 관리 불명확** | wpf-architect (M-3) | `BuildGrid`가 Content 통째 교체 → 이전 tree의 모든 HwndHost가 동시 detach. `SetParent(NULL)`/`ShowWindow(SW_HIDE)` 명시 없음 |
| M8 | **Ctrl+... direct dispatch vs IKeyboardInputSink 정석도** | wpf-architect (B-), cto-lead (C3) | `MainWindow.xaml.cs:211-328` 117행. 향후 키바인딩 확장 시 분기 폭발 위험 |
| M9 | **BuildElement H/V 35행 중복 (DRY 위반)** | code-analyzer (Major), gap-detector (품질) | Strategy 패턴으로 통합 가능. 현재 cyclomatic complexity ≈12 |
| M10 | **License/legal 부재** | enterprise-expert (Critical), cto-lead (C5, strategic 3순위) | `LICENSE`/`NOTICE`/`THIRD-PARTY-NOTICES` 미확인. ghostty(MIT)/alacritty(Apache)/wt-ref(MIT) 소스 사용 중 attribution 누락 |
| M11 | **Design NFR/Test Plan/Migration Checklist v0.5 갱신 누락** | design-validator (C-4/C-5/C-6/C-7) | §8 NFR, §11 Test Plan, §12 Migration Checklist 전부 v0.4 상태 유지 |

---

## 3. 개별 관점 핵심 요약

### 3.1 wpf-architect (55%/70%, Grade B/B+)
- **강점**: `RemoveLeaf` grandparent splice, session-based migration, deferred Dispose 주석이 "왜"를 정확히 설명
- **약점**: `_hostsByWorkspace` swap 패턴이 WPF 관용 패턴 아님 (정석은 workspace당 별도 ContentControl + Visibility 토글)
- **Critical**: `_initialHost` ghost reference 관련 — 이번 세션에 일부 해결됐으나 host lifecycle invariant가 여전히 복잡
- **recommendation**: Phase 6에서 `IKeyboardInputSink.TranslateAcceleratorCore` 정식 구현으로 key 라우팅 정리

### 3.2 dotnet-expert (85%/70%)
- **강점**: ConcurrentDictionary + WeakReference 패턴, `ObservableObject` + `[ObservableProperty]` 적절, nullable annotation 일관성
- **약점**: `IPaneLayoutServiceFactory` 같은 factory 패턴 부재, `File.AppendAllText` lock 없음, `SessionInfo` subscription 누수
- **recommendation**: async/await는 "종료 시 dispose" 영역에만 도입. 나머지는 sync 유지 정당

### 3.3 frontend-architect (35%/60%)
- **핵심 인사이트**: "완성된 shell이지만 비어 있는 느낌"
- **Sidebar 정보 밀도**: cmux 대비 25% (Title/CWD/Active만)
- **Critical**: Notification Ring 부재, Workspace Jump 단축키 부재
- **Top 3**:
  1. Notification Ring MVP (pane border ring + sidebar unread dot)
  2. Sidebar 정보 밀도 확장 (GitBranch, Cwd 축약, PortList, UnreadCount)
  3. Workspace Jump 1-8 + Command Palette 힌트

### 3.4 code-analyzer (88%/78%)
- **최고 점수 이유**: design 문서가 정의한 핵심 동작은 모두 구현됨 (좁은 범위 평가)
- **Critical**: `_hostControls`와 `_hostsByWorkspace[id]`가 동일 데이터 4중 미러본 — 단일 source-of-truth 필요
- **Complexity**: `BuildElement` ≈12, `OnTerminalKeyDown` ≈15 (임계 10 초과)

### 3.5 gap-detector (37% 가중평균/68%)
- **카테고리별 점수**:
  - A 계층 구조 80% / B Workspace 관리 29% / C Pane 관리 86% / D Surface 0% / E Notification 7% / F Session 복원 0% / G 인앱 브라우저 0% / H JSON-RPC 0% / I OSC Hooks 25%
- **Pane mouse focus 결함** 보고: CLAUDE.md에 이전 TODO로 등록돼 있으나 이번 세션에 해결됨 (3abd213 fix) — gap-detector는 CLAUDE.md 기반이라 최신 상태 미반영

### 3.6 design-validator (72%/68%, Grade C 6.2/10)
- **가장 정확한 발견**: design ↔ runtime divergence
- **Critical 7건**:
  - BISECT 미문서화
  - `render_to_target` naming drift
  - "Legacy path 삭제" claim 불일치
  - NFR v0.5 반영 0%
  - Test Plan v0.5 반영 0%
  - Migration Checklist v0.5 반영 ~30%
  - v0.5 version history에 BISECT/divergence 미기록
- **권고**: Design 문서를 v0.5.1로 개정하여 구현 정합성 복구 **먼저**, 그 다음 런타임 BISECT 해제

### 3.7 security-architect (35%/55%)
- **아이러니 긍정**: GhostWin은 IPC 자체가 없어 원격 공격 표면 0 (cmux의 JSON-RPC DACL 고민 불요)
- **High**: CrashLogPath UAC 경계, PII 누출
- **Medium**: `_hostsByHwnd` 스푸핑 (PostMessage WM_LBUTTONDOWN 주입 가능 — 영향은 UX 수준 focus 전환만)
- **Low**: ConPty child injection — 현재 shellPath=null 고정이라 안전, Settings UI 도입 시 즉시 High 격상

### 3.8 qa-strategist (7%/30%, Grade F)
- **가장 혹독한 평가**: 자동화 테스트 거의 0, design §11 계획만
- **Top 3**:
  1. C# 테스트 프로젝트 부트스트랩 (PaneNode T-1~T-5)
  2. Zero Script QA 적용 (JSON 로그 + PowerShell harness)
  3. C++ `surface_manager_race_test.cpp` (ASAN/TSAN)
- **추가 권고**: FlaUI (WPF HwndHost airspace 친화적) 기반 UI 자동화

### 3.9 enterprise-expert (35%/50%)
- **가장 가혹한 배포 현실**: Installer 0%, Telemetry 0%, Auto-update 0%, MSIX/Velopack 흔적 없음
- **Accessibility (UIA)**: 0%, 미국/EU 조달 ADA/EN 301 549 자동 실격
- **Localization**: 0%, `.resx`/`x:Uid` 없음
- **License Critical**: LICENSE/NOTICE/THIRD-PARTY-NOTICES 부재 → ghostty MIT attribution 의무 미이행

### 3.10 cto-lead (55%/70%, Grade B+)
- **전략적 핵심**: "session-restore 먼저 만들면 Surface 계층 도입 후 재작성 비용 발생"
- **Roadmap 권장**:
  1. 부채 청산 9일 (테스트 + 종료 경로 + MoveFocus + BISECT)
  2. Dogfood 임계 2주 (마우스/클립보드/Settings UI)
  3. Surface 계층
  4. session-restore
  5. Notification Ring
- **PDCA 방법론 가치**: 실제 효과 있음. 본 세션 `3abd213`이 5개 독립 버그 동시 발견한 것은 multi-agent 분석의 직접 효과
- **License 권장**: **MIT** (ghostty MIT 호환, Windows 네이티브 차별화로 충분)

---

## 4. 합의된 Recommendations — Top 10 우선순위

### P0 (즉시 — 1주일 이내)

1. **테스트 인프라 부트스트랩**
   - `tests/GhostWin.Core.Tests` 신설 (xUnit + FluentAssertions)
   - `PaneNode` T-1~T-5 (순수 로직, ROI 최고)
   - **동의**: qa-strategist, code-analyzer, cto-lead, enterprise-expert

2. **BISECT mode 종료 + design 문서 수정**
   - `docs/02-design/features/pane-split.design.md`에 "1.4 현재 BISECT 상태" 섹션 신설
   - §4.4 `render_to_target` → `bind_surface` 실제 이름으로 수정
   - `MainWindow.InitializeRenderer`에서 `SurfaceCreate` 호출 → surfaceId를 `WorkspaceService` 경로로 전달
   - `render_loop`의 legacy else 분기 삭제 (Surface 검증 후)
   - **동의**: wpf-architect, design-validator, cto-lead, gap-detector

3. **종료 경로 단일화**
   - `MainWindow.OnClosing` Task.Run 폐기 → `App.OnExit`만 유지
   - ConPty I/O thread cancellable 설계 (`CancelIoEx` / `SetEvent`)
   - `Task.Wait(TimeSpan.FromSeconds(2))` 타임아웃 + fallback
   - **동의**: wpf-architect, dotnet-expert, enterprise-expert, cto-lead

4. **PropertyChanged handler unsubscribe**
   - `WorkspaceEntry`에 handler field + `CloseWorkspace`에서 detach
   - 또는 WeakEventManager 도입
   - **동의**: wpf-architect, dotnet-expert, code-analyzer

### P1 (Phase 6 — 2-3주)

5. **CrashLog 강화**
   - `Path.Combine(Environment.GetFolderPath(LocalApplicationData), "GhostWin", "logs", ...)` 이동
   - `private static readonly object _crashLogLock = new();` 도입
   - PII redaction 필터 (환경변수, 경로 치환)
   - 파일 회전 (max 1MB, 7일 보존)
   - **동의**: dotnet-expert, security-architect, enterprise-expert

6. **MVVM 정리**
   - PaneContainerControl을 `RoutedEvent` 기반으로 재작성
   - `IWorkspaceService` 직접 주입 제거 → MainWindowViewModel 라우팅
   - **동의**: wpf-architect, code-analyzer

7. **Design 문서 v0.5.1 개정**
   - §8 NFR에 NFR-07~09 추가 (workspace 전환 지연 < 50ms, per-workspace 메모리, 최대 workspace 수)
   - §11 Test Plan T-15~T-20 workspace 테스트 추가
   - §12 Migration Checklist "신규/수정 파일" v0.5 최신화
   - **동의**: design-validator

### P2 (Phase 6 말 ~ Phase 7)

8. **Sidebar 정보 밀도 확장**
   - `WorkspaceInfo`에 `GitBranch`, `PortList`, `UnreadCount` 필드
   - Cwd 축약 헬퍼 (`~`, `.../parent/dir`)
   - DataTemplate 3행 레이아웃 + 배지 컬럼
   - **동의**: frontend-architect, gap-detector

9. **Notification Ring MVP**
   - OSC 9 파서부터 (ghostty upstream 가능성)
   - Pane border blue ring + sidebar unread dot
   - `INotificationService` 발행 → ViewModel 수신
   - **동의**: frontend-architect, gap-detector, cto-lead (indirect)

10. **Workspace Jump 1-8 + Spatial MoveFocus**
    - `Ctrl+1`~`Ctrl+8` KeyBinding 추가
    - `Ctrl+Shift+Tab` (Prev)
    - `MoveFocus`를 PaneNode `Bounds` 기반 nearest neighbor로 재구현
    - **동의**: frontend-architect, gap-detector, wpf-architect, cto-lead

---

## 5. Roadmap 권장 (cto-lead 기반 + 합의 조정)

| Phase | 기간 | 내용 | 근거 |
|---|:---:|---|---|
| **Phase 5-E.5** | 1주 | 부채 청산 (P0 1-4) | cto-lead 9일 추정, qa-strategist/wpf-architect 우선순위 |
| **Phase 5-E.6** | 1주 | BISECT 종료 + design v0.5.1 | design-validator + runtime 검증 |
| **Phase 6-A** | 2주 | Dogfood 임계 (마우스/클립보드/Settings UI) | cto-lead 권장, CLAUDE.md 잔여 항목 |
| **Phase 6-B** | 1주 | CrashLog + Telemetry 인프라 (P1 5) | enterprise-expert Critical |
| **Phase 6-C** | 1주 | Production Foundation (LICENSE, NOTICE, installer 결정) | enterprise-expert, cto-lead |
| **Phase 5-G** | 2주 | Surface 계층 (pane 내 multi-tab) | cto-lead strategic 우선순위 |
| **Phase 5-F** | 2주 | Session restore (Surface 완료 후) | cmux 호환 직렬화 |
| **Phase 7** | 3주 | Notification Ring (OSC 파서 → Ring → 패널 → Toast) | cmux 차별화 |
| **Phase 8** | 2주 | UI 퀄리티 (sidebar 정보 밀도, Workspace Jump, Spatial MoveFocus) | frontend-architect |
| **Phase 9** | ? | Localization + Accessibility (UIA) | enterprise-expert Critical |

총 ~15주. 1인 개발 가정 ("추측").

---

## 6. 합의된 강점 (positive findings)

공통으로 칭찬받은 항목:

1. **`PaneNode.RemoveLeaf` grandparent splice** — leaf paneId 보존으로 _leaves/_hostControls 정합성 유지. 정석 자료구조 패턴.
2. **Session-based host migration** — `PaneNode.Split`의 sessionId 보존 invariant를 활용한 깔끔한 host reuse 전략.
3. **Deferred Dispose via `Dispatcher.BeginInvoke(Background)`** — WPF visual tree update와 DestroyWindow 동기 호출 race를 정확히 분석한 주석 + 해결.
4. **`ConcurrentDictionary _hostsByHwnd` + WndProc 람다 재검증** — UAF 방지 패턴이 교과서적.
5. **Clean Architecture 4-project 유지** — Core/Interop/Services/App 레이어 분리 일관.
6. **ADR 13건의 깊이** — race, rendering, IME 등 각 도메인 분석이 풍부 (ADR-006 vt_mutex, ADR-010 composition, ADR-011 TSF, ADR-012 CJK 등).
7. **Unified focus sync** — `SetFocused`/`MoveFocus`/`CloseFocused`에 `_sessions.ActivateSession` 일관 동기화.
8. **Clear "왜" 주석** — cleanup loop, detach logic, Alt SystemKey fix, BISECT marker 모두 근거 명시.

---

## 7. 본 평가의 한계 (정직한 불확실성)

- **10개 agent 점수 편차가 큼** (기능 7-88%, 품질 30-78%) — 관점별 기준선이 달라서이며, 단일 숫자로 억지로 통합하지 않음.
- **cmux 기능 전수 카탈로그 미보유** — gap-detector의 47 feature mapping도 평가자 추정 가중치 ±10%p 변동 가능.
- **BISECT mode의 실제 런타임 영향 미검증** — 사용자 수동 테스트 8/8 통과 보고는 "기능 동작"만, 성능/multi-pane 동시 렌더 화질 검증 부족.
- **Runtime stress test 미수행** — 8 pane @ 60fps NFR, memory leak, thread race 전부 정적 분석만.
- **`LICENSE`/`NOTICE` 파일 존재 여부** — enterprise-expert Glob 확인이 repo 루트만 대상. 서브디렉토리 포함 최종 확인 미실시.
- **일부 agent 파일 직접 읽기 누락** — design-validator M-9 claim #5, #6 검증은 간접 증거 기반 (MainWindowViewModel.cs / MainWindow.xaml 직접 미열람).
- **cmux 자체 테스트/CI 커버리지** 비교 불가 — cmux 공식 repo의 test 디렉토리 미검사.

---

## 8. 합의 도출 방식 (메타)

- **공통 발견 임계**: 3+ agents 동의 → Critical, 2+ agents 동의 → Major
- **점수 합의**: 단일 숫자 대신 **6차원 다차원** 평가로 편차 보존
- **Recommendations**: 3+ agents가 겹치는 항목만 P0, 2 agents면 P1, 1 agent면 P2
- **Grade**: cto-lead B+, design-validator C(6.2/10), qa-strategist F — 관점별 기준 유지

**"완전합의"의 의미**: 모든 agent가 같은 점수에 동의하는 것이 아니라, **근거 있는 합의점**에 대해 서로의 관점을 존중하는 형태. 예) wpf-architect는 전체 55%, code-analyzer는 설계 대비 88% — 둘 다 옳고, 표가 자신의 기준을 밝힘.

---

## 참고 파일

### 평가 대상
- `docs/02-design/features/pane-split.design.md` v0.5
- `src/GhostWin.Core/Models/PaneNode.cs`, `WorkspaceInfo.cs`
- `src/GhostWin.Core/Interfaces/IPaneLayoutService.cs`, `IWorkspaceService.cs`
- `src/GhostWin.Services/PaneLayoutService.cs`, `WorkspaceService.cs`
- `src/GhostWin.App/Controls/PaneContainerControl.cs`, `TerminalHostControl.cs`
- `src/GhostWin.App/ViewModels/MainWindowViewModel.cs`, `WorkspaceItemViewModel.cs`
- `src/GhostWin.App/MainWindow.xaml`, `MainWindow.xaml.cs`
- `src/GhostWin.App/App.xaml.cs`
- `src/engine-api/ghostwin_engine.cpp` (BISECT marker line 321)

### 참조
- `docs/00-research/cmux-ai-agent-ux-research.md`
- [cmux.com/docs](https://cmux.com/docs) (WebFetch 확인)
- [github.com/manaflow-ai/cmux](https://github.com/manaflow-ai/cmux)
- `CLAUDE.md` (project status + TODO)

### 평가자
- rkit:wpf-architect (opus)
- rkit:dotnet-expert (opus)
- rkit:frontend-architect (opus)
- rkit:code-analyzer (opus)
- rkit:gap-detector (opus)
- rkit:design-validator (opus)
- rkit:security-architect (opus)
- rkit:qa-strategist (opus)
- rkit:enterprise-expert (opus)
- rkit:cto-lead (opus)
