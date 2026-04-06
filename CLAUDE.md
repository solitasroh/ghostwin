# GhostWin Terminal — Project Rules

## 상세 규칙

빌드/행동 규칙은 `.claude/rules/`에 분리되어 경로별로 자동 로드됨.

| 규칙 파일                            | 적�� 범위                                   |
| ------------------------------------ | ------------------------------------------- |
| `.claude/rules/behavior.md`          | 항상 (의존성 대응, 빌드 실패, 스크립트)     |
| `.claude/rules/commit.md`            | 항상 (커밋 메시지 형식, AI 언급 금지)       |
| `.claude/rules/build-environment.md` | CMakeLists.txt, scripts/, external/ghostty/ |

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

## 핵심 참고 문서

| 문서                       | 경로                                                                           |
| -------------------------- | ------------------------------------------------------------------------------ |
| Upstream 동기화 분석       | `docs/00-research/ghostty-upstream-sync-analysis.md`                           |
| 트러블슈팅 가이드          | `docs/00-research/troubleshooting-windows-build.md`                            |
| Phase 1 완료 보고서        | `docs/archive/2026-03/libghostty-vt-build/libghostty-vt-build.report.md`       |
| Phase 3 완료 보고서        | `docs/archive/2026-03/dx11-rendering/dx11-rendering.report.md`                 |
| Phase 3 Design             | `docs/archive/2026-03/dx11-rendering/dx11-rendering.design.md`                 |
| Phase 4-A 완료 보고서      | `docs/archive/2026-03/winui3-shell/winui3-shell.report.md`                     |
| Phase 4-A Design           | `docs/archive/2026-03/winui3-shell/winui3-shell.design.md`                     |
| Phase 4-B 완료 보고서      | `docs/archive/2026-04/tsf-ime/tsf-ime.report.md`                               |
| Phase 4-B Design           | `docs/archive/2026-04/tsf-ime/tsf-ime.design.md`                               |
| Phase 4-F 완료 보고서      | `docs/archive/2026-04/dpi-aware-rendering/dpi-aware-rendering.report.md`       |
| Phase 4-F Design           | `docs/archive/2026-04/dpi-aware-rendering/dpi-aware-rendering.design.md`       |
| Phase 4 Master Plan        | `docs/01-plan/features/winui3-integration.plan.md`                             |
| DX11 GPU 렌더링 리서치     | `docs/00-research/research-dx11-gpu-rendering.md`                              |
| ClearType 90%+ 리서치      | `docs/00-research/research-cleartype-90-percent.md`                            |
| ClearType 작업일지         | `docs/archive/2026-04/cleartype-sharpness-v2/cleartype-composition-worklog.md` |
| Phase 5 Master Plan        | `docs/01-plan/features/multi-session-ui.plan.md`                               |
| Phase 5-A 완료 보고서      | `docs/archive/2026-04/session-manager/session-manager.report.md`               |
| Phase 5-A Design           | `docs/archive/2026-04/session-manager/session-manager.design.md`               |
| Phase 5-B 완료 보고서      | `docs/archive/2026-04/tab-sidebar/tab-sidebar.report.md`                       |
| Phase 5-B Design           | `docs/archive/2026-04/tab-sidebar/tab-sidebar.design.md`                       |
| Phase 5-C 완료 보고서      | `docs/archive/2026-04/titlebar-customization/titlebar-customization.report.md` |
| Phase 5-C Design           | `docs/archive/2026-04/titlebar-customization/titlebar-customization.design.md` |
| Phase 5-D 완료 보고서      | `docs/archive/2026-04/settings-system/settings-system.report.md`               |
| cmux AI 에이전트 UX 리서치 | `docs/00-research/cmux-ai-agent-ux-research.md`                                |
| WPF Hybrid PoC Plan        | `docs/01-plan/features/wpf-hybrid-poc.plan.md`                                 |
| WPF Hybrid PoC Design      | `docs/02-design/features/wpf-hybrid-poc.design.md`                             |
| WPF Hybrid PoC Report      | `docs/04-report/wpf-hybrid-poc.report.md`                                      |

## 프로젝트 진행 상태 (2026-04-06 기준)

### 완료된 Phase

| Phase | Feature                     | Match Rate | Archive                                                          |
| ----- | --------------------------- | :--------: | ---------------------------------------------------------------- |
| 1     | libghostty-vt-build         |    96%     | `docs/archive/2026-03/libghostty-vt-build/`                     |
| 2     | conpty-integration          |    100%    | `docs/archive/2026-03/conpty-integration/`                       |
| 3     | dx11-rendering              |   96.6%    | `docs/archive/2026-03/dx11-rendering/`                           |
| 4-A   | winui3-shell (FR-01~07)     |    94%     | `docs/archive/2026-03/winui3-shell/`                             |
| 4-B   | tsf-ime (FR-08)             |    99%     | `docs/archive/2026-04/tsf-ime/`                                  |
| 4-C   | cleartype-subpixel (FR-09)  |    95%     | `docs/archive/2026-03/cleartype-subpixel/`                       |
| 4-D   | nerd-font-fallback (FR-10)  |    96%     | `docs/archive/2026-03/nerd-font-fallback/`                       |
| 4-E   | quadinstance-opt (FR-11)    |    100%    | `docs/archive/2026-03/quadinstance-opt/`                         |
| 4-F   | dpi-aware-rendering (FR-05) |   98.6%    | `docs/archive/2026-04/dpi-aware-rendering/`                      |
| 4-G   | mica-backdrop (FR-07)       |     —      | 코드 적용 (MicaBackdrop + try/catch 폴백)                        |
| —     | legacy-cleanup              |     —      | ime_handler 삭제, cleartype-composition 문서 아카이브            |
| —     | cleartype-composition       |  **완료**  | CreateAlphaTexture(gamma AA) + Dual Source Blending. 사용자 확인 |
| —     | glyph-metrics (간격 조정)   |    93%     | `docs/archive/2026-04/glyph-metrics/`                            |

### Phase 4 미완료 잔여 항목

| 항목          | FR     | 상태             | 설명                                                            |
| ------------- | ------ | ---------------- | --------------------------------------------------------------- |
| 유휴 GPU 실측 | NFR-03 | 런타임 검증 대기 | Waitable swapchain + Sleep(1) idle 코드 완료. GPU-Z 실측만 잔여 |

### Phase 5: multi-session-ui (WinUI3 기반 — WPF 전환 전 구현분)

| ID  | Feature                | 의존성 |     상태       | Archive                                              |
| --- | ---------------------- | ------ | :------------: | ---------------------------------------------------- |
| A   | session-manager        | 없음   | **95% 완료**   | `docs/archive/2026-04/session-manager/`              |
| B   | tab-sidebar            | A 이후 | **~98% 완료**  | `docs/archive/2026-04/tab-sidebar/`                  |
| C   | titlebar-customization | B 이후 | **99.3% 완료** | `docs/archive/2026-04/titlebar-customization/`       |
| D   | settings-system        | 없음   | **98% 완료**   | `docs/archive/2026-04/settings-system/`              |
| E   | pane-split             | A 이후 |     대기       | —                                                    |
| F   | session-restore        | A+B+E  |     대기       | —                                                    |

Master Plan: `docs/01-plan/features/multi-session-ui.plan.md`

### WPF Hybrid PoC (아키텍처 전환 검증 — 완료)

| 항목 | 검증 내용                    | 결과          | 수치                    |
| ---- | ---------------------------- | :-----------: | ----------------------- |
| V1   | Engine DLL 빌드 + 예외 방어  | **Pass**      | 18 API, 10/10 테스트    |
| V2   | HwndHost + ClearType         | **Pass**      | 시각적 동등             |
| V3   | P/Invoke 왕복 지연 < 1ms     | **Pass**      | 749ns (0.7μs)           |
| V4   | TSF 한글 조합/확정           | **Pass**      | 확정/종성분리/BS 정상   |
| V5   | wpf-ui Mica + 다크모드       | **Pass**      | FluentWindow 동작       |
| V6   | 대량 출력 스루풋             | **Pass**      | 1.0 MB/s, 프리징 없음   |

**판정: Go — WPF 전환 확정** (2026-04-06)

- Plan: `docs/01-plan/features/wpf-hybrid-poc.plan.md`
- Design: `docs/02-design/features/wpf-hybrid-poc.design.md`
- Report: `docs/04-report/wpf-hybrid-poc.report.md`
- PoC 코드: `wpf-poc/` (GhostWinPoC.csproj, .NET 10 + wpf-ui)
- Engine C API: `src/engine-api/ghostwin_engine.h` (18 exported functions)

### 마이그레이션 잔여 항목 (WPF 전환 후)

| 항목                       | 설명                                                    |
| -------------------------- | ------------------------------------------------------- |
| 조합 미리보기 오버레이     | DoCompositionUpdate → 렌더러 연동 (preedit 표시)        |
| Settings UI (XAML)         | SettingsService.cs + XAML 페이지 (C++ → C# 이전)        |
| TabSidebar WPF 재작성      | WinUI3 StackPanel → WPF MVVM                            |
| TitleBar WPF 재작성        | AppWindowTitleBar → WPF WindowChrome                    |
| Command Palette            | 검색/팔레트 팝업 (Airspace 우회 Popup Window)            |
| Phase 5-E pane-split       | Tree<Pane> 레이아웃 엔진 (WPF 기반으로 구현)            |
| Phase 5-F session-restore  | CWD + 레이아웃 JSON 직렬화/복원                          |

## ghostty 서브모듈 상태

- 현재: `debcffbad` — upstream 동기화 완료 (#11950 C++ 헤더 호환 포함)
- 로컬 패치: 없음 (Phase 2에서 3건 제거, ADR-001 GNU+simd=false에서 불필요)
- 동기화 이력: `docs/00-research/ghostty-upstream-sync-analysis.md`
