# GhostWin Terminal — Project Rules

## 상세 규칙

빌드/행동 규칙은 `.claude/rules/`에 분리되어 경로별로 자동 로드됨.

| 규칙 파일                            | 적용 범위                                   |
| ------------------------------------ | ------------------------------------------- |
| `.claude/rules/behavior.md`          | 항상 (의존성 대응, 빌드 실패, 스크립트)     |
| `.claude/rules/commit.md`            | 항상 (커밋 메시지 형식, AI 언급 금지)       |
| `.claude/rules/build-environment.md` | GhostWin.sln, *.vcxproj, *.csproj, scripts/, external/ghostty/ |

## 아키텍처 결정 (ADR)

| ADR                                             | 결정                               | 근거                                                                                      |
| ----------------------------------------------- | ---------------------------------- | ----------------------------------------------------------------------------------------- |
| [001](docs/adr/001-simd-false-gnu-target.md)    | windows-gnu + simd=false           | CRT 독립                                                                                  |
| [002](docs/adr/002-c-bridge-pattern.md)         | C 브릿지 레이어                    | MSVC C++ typedef 충돌 회피                                                                |
| [003](docs/adr/003-dll-dynamic-crt.md)          | DLL 방식 유지                      | GNU static lib MSVC 링커 COMDAT 불호환                                                    |
| [004](docs/adr/004-utf8-source-encoding.md)     | MSVC /utf-8 강제                   | 한국어 Windows CP949 인코딩 충돌                                                          |
| [005](docs/adr/005-sdk-version-pinning.md)      | SDK 22621 버전 고정                | SDK 26100 shared 헤더 누락                                                                |
| [006](docs/adr/006-vt-mutex-thread-safety.md)   | vt_mutex 스레드 안전성             | write/resize 경합 방지 (Alacritty 패턴)                                                   |
| [007](docs/adr/007-r32-quad-instance-format.md) | R32 QuadInstance (68B)             | R16 포맷 CreateInputLayout 타입 불일치                                                    |
| [008](docs/adr/008-two-pass-rendering.md)       | 2-Pass 렌더링 (배경→텍스트)        | CJK 글리프 클리핑 방지 (4개 터미널 표준)                                                  |
| [009](docs/adr/009-winui3-codeonly-cmake.md)    | Code-only WinUI3 + CMake 필수 요소 | IXamlMetadataProvider + RegFree WinRT + GetCurrentTime undef                              |
| [010](docs/adr/010-grayscale-aa-composition.md) | Composition Swapchain AA           | IGNORE PoC 성공 + ClearType 3x1 + sRGB 감마. 블라인드 74→~80. per-channel blend 한계 잔존 |
| [011](docs/adr/011-tsf-hidden-hwnd-ime.md)      | TSF + Hidden Win32 HWND            | IMM32 충돌 → WT 패턴 TSF 전환                                                             |
| [012](docs/adr/012-cjk-advance-centering.md)    | CJK Advance-Centering              | fallback 높이 축소 gap → no-height-scale + advance-centering                              |
| [013](docs/adr/013-embedded-shader-source.md)   | 셰이더 소스 임베드                 | CWD 의존 상대 경로 → C++ raw string literal 임베드. 실행 위치 무관                        |

## 핵심 참고 문서

| 문서                                        | 경로                                                                           |
| ------------------------------------------- | ------------------------------------------------------------------------------ |
| Upstream 동기화 분석                        | `docs/00-research/ghostty-upstream-sync-analysis.md`                           |
| 트러블슈팅 가이드                           | `docs/00-research/troubleshooting-windows-build.md`                            |
| Phase 1 완료 보고서                         | `docs/archive/2026-03/libghostty-vt-build/libghostty-vt-build.report.md`       |
| Phase 3 완료 보고서                         | `docs/archive/2026-03/dx11-rendering/dx11-rendering.report.md`                 |
| Phase 3 Design                              | `docs/archive/2026-03/dx11-rendering/dx11-rendering.design.md`                 |
| Phase 4-A 완료 보고서                       | `docs/archive/2026-03/winui3-shell/winui3-shell.report.md`                     |
| Phase 4-A Design                            | `docs/archive/2026-03/winui3-shell/winui3-shell.design.md`                     |
| Phase 4-B 완료 보고서                       | `docs/archive/2026-04/tsf-ime/tsf-ime.report.md`                               |
| Phase 4-B Design                            | `docs/archive/2026-04/tsf-ime/tsf-ime.design.md`                               |
| Phase 4-F 완료 보고서                       | `docs/archive/2026-04/dpi-aware-rendering/dpi-aware-rendering.report.md`       |
| Phase 4-F Design                            | `docs/archive/2026-04/dpi-aware-rendering/dpi-aware-rendering.design.md`       |
| Phase 4 Master Plan                         | `docs/01-plan/features/winui3-integration.plan.md`                             |
| DX11 GPU 렌더링 리서치                      | `docs/00-research/research-dx11-gpu-rendering.md`                              |
| ClearType 90%+ 리서치                       | `docs/00-research/research-cleartype-90-percent.md`                            |
| ClearType 작업일지                          | `docs/archive/2026-04/cleartype-sharpness-v2/cleartype-composition-worklog.md` |
| Phase 5 Master Plan                         | `docs/01-plan/features/multi-session-ui.plan.md`                               |
| Phase 5-A 완료 보고서                       | `docs/archive/2026-04/session-manager/session-manager.report.md`               |
| Phase 5-A Design                            | `docs/archive/2026-04/session-manager/session-manager.design.md`               |
| Phase 5-B 완료 보고서                       | `docs/archive/2026-04/tab-sidebar/tab-sidebar.report.md`                       |
| Phase 5-B Design                            | `docs/archive/2026-04/tab-sidebar/tab-sidebar.design.md`                       |
| Phase 5-C 완료 보고서                       | `docs/archive/2026-04/titlebar-customization/titlebar-customization.report.md` |
| Phase 5-C Design                            | `docs/archive/2026-04/titlebar-customization/titlebar-customization.design.md` |
| Phase 5-D 완료 보고서                       | `docs/archive/2026-04/settings-system/settings-system.report.md`               |
| cmux AI 에이전트 UX 리서치                  | `docs/00-research/cmux-ai-agent-ux-research.md`                                |
| WPF Hybrid PoC Plan                         | `docs/01-plan/features/wpf-hybrid-poc.plan.md`                                 |
| WPF Hybrid PoC Design                       | `docs/02-design/features/wpf-hybrid-poc.design.md`                             |
| WPF Hybrid PoC Report                       | `docs/04-report/wpf-hybrid-poc.report.md`                                      |
| WPF Migration Plan                          | `docs/01-plan/features/wpf-migration.plan.md`                                  |
| WPF Migration Design                        | `docs/02-design/features/wpf-migration.design.md`                              |
| Pane-Split Design (v0.5)                    | `docs/02-design/features/pane-split.design.md`                                 |
| Pane-Split v0.5 완성도 평가 (10-agent 합의) | `docs/03-analysis/pane-split-workspace-completeness-v0.5.md`                   |
| Core Tests Bootstrap (archived)             | `docs/archive/2026-04/core-tests-bootstrap/`                                   |
| BISECT Termination Plan                     | `docs/01-plan/features/bisect-mode-termination.plan.md`                        |
| BISECT Termination Design                   | `docs/02-design/features/bisect-mode-termination.design.md`                    |
| E2E Ctrl-Key Injection (archived)           | `docs/archive/2026-04/e2e-ctrl-key-injection/`                                 |
| E2E Evaluator Automation (archived)         | `docs/archive/2026-04/e2e-evaluator-automation/`                               |
| First-Pane Render Failure (archived)        | `docs/archive/2026-04/first-pane-render-failure/`                              |
| E2E Headless Input (archived)               | `docs/archive/2026-04/e2e-headless-input/`                                     |
| split-content-loss-v2 (archived)            | `docs/archive/2026-04/split-content-loss-v2/`                                  |

## 프로젝트 진행 상태 (2026-04-10 기준)

### 완료된 Phase

| Phase | Feature                     | Match Rate | Archive                                     |
| ----- | --------------------------- | :--------: | ------------------------------------------- |
| 1     | libghostty-vt-build         |    96%     | `docs/archive/2026-03/libghostty-vt-build/` |
| 2     | conpty-integration          |    100%    | `docs/archive/2026-03/conpty-integration/`  |
| 3     | dx11-rendering              |   96.6%    | `docs/archive/2026-03/dx11-rendering/`      |
| 4-A   | winui3-shell (FR-01~07)     |    94%     | `docs/archive/2026-03/winui3-shell/`        |
| 4-B   | tsf-ime (FR-08)             |    99%     | `docs/archive/2026-04/tsf-ime/`             |
| 4-C   | cleartype-subpixel (FR-09)  |    95%     | `docs/archive/2026-03/cleartype-subpixel/`  |
| 4-D   | nerd-font-fallback (FR-10)  |    96%     | `docs/archive/2026-03/nerd-font-fallback/`  |
| 4-E   | quadinstance-opt (FR-11)    |    100%    | `docs/archive/2026-03/quadinstance-opt/`    |
| 4-F   | dpi-aware-rendering (FR-05) |   98.6%    | `docs/archive/2026-04/dpi-aware-rendering/` |
| 4-G   | mica-backdrop (FR-07)       |     —      | MicaBackdrop + try/catch 폴백               |
| —     | cleartype-composition       |  **완료**  | CreateAlphaTexture + Dual Source Blending   |
| —     | glyph-metrics               |    93%     | `docs/archive/2026-04/glyph-metrics/`       |

### WPF 마이그레이션 (M-1 ~ M-7 완료)

| 마일스톤 | 내용                               |   상태   |
| -------- | ---------------------------------- | :------: |
| M-1      | Clean Architecture 4-프로젝트 + DI | **완료** |
| M-2      | Engine Interop (19 API + 7 콜백)   | **완료** |
| M-3      | Session/Tab MVVM sidebar           | **완료** |
| M-4      | Settings JSON + hot reload         | **완료** |
| M-5      | TitleBar + Mica + WindowChrome     | **완료** |
| M-6      | WinUI3 코드/의존성 완전 제거       | **완료** |
| M-7      | cmux 스타일 UI 폴리시              | **완료** |

- Plan: `docs/01-plan/features/wpf-migration.plan.md`
- Design: `docs/02-design/features/wpf-migration.design.md`
- 아키텍처: `GhostWin.Core` → `GhostWin.Interop` → `GhostWin.Services` → `GhostWin.App`

### Phase 5-E: pane-split + Workspace Layer (**구현 완료 — 2026-04-07**)

설계: `docs/02-design/features/pane-split.design.md` (v0.5 — cmux 5-level 계층 반영)

- [x] M-8a: SurfaceManager + bind_surface (C++) — `8e4e6c2`
- [x] M-8b: PaneLayoutService + PaneNode 리팩토링 + Core 인터페이스 (C#) — `ab0770a`
- [x] M-8c: PaneContainerControl 슬림화 + WPF Shell 통합 (C#) — `7565d70`
- [x] 단일 pane 렌더링 검증 (PowerShell 프롬프트 표시)
- [x] **M-8d: Crash fixes 10건** — session sync, RemoveLeaf redesign, Alt SystemKey, Border reparent, session-based host migration, deferred dispose, ConcurrentDictionary, crash diagnostics
- [x] **M-9: Workspace Layer (cmux 정식 모델)** — Window → Workspace → Pane → Surface. `IWorkspaceService` + per-workspace `PaneLayoutService` instance. Sidebar entries = workspaces.
- [x] Alt+V/H split 검증
- [x] Pane focus (마우스/키보드) + 키 라우팅
- [x] Ctrl+Shift+W pane close, Ctrl+W workspace close, Ctrl+T new workspace
- [x] Sidebar 클릭 workspace 전환

### Phase 5-E.5: 부채 청산 (10-agent v0.5 평가 §4 P0)

- [x] **P0-1 테스트 인프라** — PaneNode 9개 단위 테스트. 아카이브: `docs/archive/2026-04/core-tests-bootstrap/`
- [x] **P0-2 BISECT mode 종료** — 커밋 `e8d7e58`. 아카이브: `docs/archive/2026-04/` (plan/design 문서)
- [x] **P0-* e2e-ctrl-key-injection** — H9 확정 + 2줄 fix. 아카이브: `docs/archive/2026-04/e2e-ctrl-key-injection/`
- [x] **P0-* e2e-evaluator-automation** — 아카이브: `docs/archive/2026-04/e2e-evaluator-automation/`
- [x] **P0-* first-pane-render-failure** — Option B 구조 fix + content-preserving resize (`4492b5d`). 아카이브: `docs/archive/2026-04/first-pane-render-failure/`
- [x] **P0-* e2e-headless-input** — H-RCA4 + H-RCA1 확정, Match Rate 95%. 아카이브: `docs/archive/2026-04/e2e-headless-input/`
- [x] **P0-* split-content-loss-v2** (2026-04-10) — `sessionId != 0` 가드가 session 0 host 재사용 차단 → Surface가 파괴된 HWND에 렌더링. Fix: 가드 제거 1줄. Hardware smoke 검증 완료
- [x] **P0-3 종료 경로 단일화** (2026-04-10) — OnClosing 단일 진입점 + WT 패턴 (Dispose→Exit) + 2s 타임아웃 fallback. Smoke 10/10 PASS, hang 0건. 아카이브: `docs/archive/2026-04/shutdown-path-unification/`
- [x] **P0-4 PropertyChanged detach** (2026-04-10) — 익명 람다 → named handler + `WorkspaceEntry`에 저장 + `CloseWorkspace`에서 `-=` 해제. 아카이브: `docs/archive/2026-04/propertychanged-detach/`

### TODO — Follow-up Cycles (Next Up)

first-pane-render-failure 사이클에서 분리된 6 개 + e2e-evaluator-automation 1 개 + e2e-headless-input 5 개:

| # | Cycle | 우선순위 | Scope | Trigger |
|:-:|---|:-:|---|---|
| 1 | ~~`e2e-mq7-workspace-click`~~ | **완료** | E2E operator 좌표 오차 (`y=150→104`). XAML 정적 분석으로 H1 확정, Match Rate 97%. 아카이브: `docs/archive/2026-04/e2e-mq7-workspace-click/` (2026-04-10) | e2e-evaluator-automation + first-pane-render-failure 둘 다 독립 확정 |
| 2 | ~~`first-pane-manual-verification`~~ | **완료** | Alt+V split content 시각 검증 — split-content-loss-v2 fix (2026-04-10)로 해소. G5b IME + G5c Mica 잔여 | split-content-loss-v2 fix 에서 동시 해소 |
| 3 | `repro-script-fix` | MEDIUM | `repro_first_pane.ps1` 의 AMSI window-capture 차단 우회 (PrintWindow 등 non-P/Invoke 방식) | first-pane-render-failure Iter 2 G8 불가 |
| 4 | `runner-py-feature-field-cleanup` | micro | `runner.py:344` `feature` field hardcoded 정리 | e2e-evaluator-automation §8.5 |
| 5 | `first-pane-regression-tests` | LOW | WPF WinExe 의 library-level 참조 제약 조사 → `PaneContainerControl`/`TsfBridge` unit test infra | first-pane-render-failure 아키텍처 제약 |
| 6 | `adr-011-timer-review` | LOW | `TsfBridge.OnFocusTick` dead-code 정식 제거 또는 valid use case 발굴 | R10 (same-cycle mitigated) |
| 7 | `render-overhead-measurement` | LOW | G8/G9 `RenderDiag` off/on latency 비교 | #3 선행 권장 |
| 8 | ~~`split-content-loss-v2`~~ | **완료** | `PaneContainerControl.cs:201` `sessionId != 0` 가드가 session 0 host 재사용 차단 → split 시 새 HWND 생성되지만 Surface swapchain은 옛 HWND 바인딩 유지 → 화면 빈칸. Fix: `&& sessionId != 0` 제거 (1줄). Hardware smoke 검증 완료 (2026-04-10) | e2e-headless-input smoke 중 사용자 hardware 관찰 |
| 9 | `keydiag-log-dedupe` | LOW | `handledEventsToo:true` bubble handler 가 duplicate ENTRY 2~4회 기록 — 기능 영향 0, log clarity 만 | e2e-headless-input Report §11 |
| 10 | `keydiag-keybind-instrumentation` | LOW | `evt=KEYBIND command=...` log line 누락 — `LogKeyBindCommand` 호출 경로가 defensive fix path 에서 skip, 진단 완전성만 | e2e-headless-input Report §11 |
| 11 | `main-window-vk-centralize` | LOW | `VK_CONTROL/SHIFT/MENU` + `GetKeyState` P/Invoke 가 `MainWindow.xaml.cs` + `KeyDiag.cs` 에 중복 — `GhostWin.Interop.NativeConstants` 로 centralize (simplify Reuse findings) | e2e-headless-input simplify |
| 12 | `e2e-flaui-cross-validation-run` | LOW | `tests/e2e-flaui-cross-validation/` 의 PoC 를 사용자 hardware 에서 실행해서 FlaUI UIA 경로가 H-RCA4 fix 없이도 Ctrl chord 를 디스패치하는지 확인 | e2e-headless-input T-5 optional |

### Roadmap (2026-04-11~)

> 상세: `docs/01-plan/roadmap.md`

| 마일스톤 | 목표 | 핵심 항목 | 상태 |
|----------|------|-----------|:----:|
| ~~**M-10**~~ | ~~터미널 기본 조작~~ | ~~마우스 클릭/스크롤/선택~~ | **완료** (2026-04-11) |
| **M-10.5** | 복사/붙여넣기 | Ctrl+C/V 클립보드 | 대기 |
| **M-11** | 세션 지속성 | session-restore (Phase 5-F) | 대기 |
| **M-12** | 사용자 설정 UI | Settings XAML + Command Palette | 대기 |
| **M-13** | 입력 UX 완성 | 조합 미리보기 + 마우스 커서 모양 | 대기 |

### TODO — Phase 5-E 잔여 품질 항목

- [ ] Workspace title/cwd가 active pane의 session을 따라가도록 mirror 확장
- [ ] MoveFocus DFS → spatial navigation (실제 좌표 기반)
- [ ] `Pane` 내 multi-surface tab (cmux Surface layer) — Phase 5-G 후보
- [ ] CrashLog 파일 회전 + `%LocalAppData%` 이동 + Microsoft.Extensions.Logging 도입

### TODO — Phase 5-F: session-restore

- [ ] 설계 문서 작성
- [ ] CWD + 레이아웃 JSON 직렬화
- [ ] 시작 시 복원

### TODO — 마이그레이션 잔여 항목

- [x] **마우스 입력** (2026-04-11) — 클릭/스크롤/텍스트 선택/DX11 하이라이트/CJK. 아카이브: `docs/archive/2026-04/mouse-input/`
- [ ] 복사/붙여넣기 (클립보드) — M-10.5
- [ ] 조합 미리보기 오버레이 (TSF preedit → 렌더러 연동) — M-13
- [ ] Settings UI (XAML 페이지) — M-12
- [ ] Command Palette (Airspace 우회 Popup Window) — M-12

### TODO — 기술 부채

- [x] vt_mutex 통합 — Session::vt_mutex 필드 제거, conpty->vt_mutex() 위임 메서드로 단일화
- [ ] 유휴 GPU 실측 (NFR-03, GPU-Z 실측만 잔여)
- [ ] SessionManager 리팩토링 (17 public → SRP)

### Phase 5: multi-session-ui 현황

| ID  | Feature                | 의존성 |               상태               | Archive                                               |
| --- | ---------------------- | ------ | :------------------------------: | ----------------------------------------------------- |
| A   | session-manager        | 없음   |      **완료** (WPF M-2/M-3)      | `docs/archive/2026-04/session-manager/`               |
| B   | tab-sidebar            | A 이후 |        **완료** (WPF M-3)        | `docs/archive/2026-04/tab-sidebar/`                   |
| C   | titlebar-customization | B 이후 |        **완료** (WPF M-5)        | `docs/archive/2026-04/titlebar-customization/`        |
| D   | settings-system        | 없음   |        **완료** (WPF M-4)        | `docs/archive/2026-04/settings-system/`               |
| E   | pane-split + workspace | A 이후 | **구현 완료** (M-8a/b/c/d + M-9) | `docs/02-design/features/pane-split.design.md` (v0.5) |
| F   | session-restore        | A+B+E  |               대기               | —                                                     |

Master Plan: `docs/01-plan/features/multi-session-ui.plan.md`

## ghostty 서브모듈 상태

- 현재: `debcffbad` — upstream 동기화 완료 (#11950 C++ 헤더 호환 포함)
- 로컬 패치: 없음 (Phase 2에서 3건 제거, ADR-001 GNU+simd=false에서 불필요)
- 동기화 이력: `docs/00-research/ghostty-upstream-sync-analysis.md`
