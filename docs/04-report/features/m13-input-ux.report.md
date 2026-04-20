# M-13 Input UX 완료 보고서

> **문서 종류**: PDCA Completion Report
> **마일스톤**: M-13
> **작성일**: 2026-04-20
> **마일스톤 소유자**: 노수장
> **기간**: 2026-04-16 ~ 2026-04-20 (5일, PRD 작성 → 사후 정정 2회 → FR-02 upstream 패치 → 최종)
> **설계-구현 일치도**: **100% (엄격 = 사용자 검증, G-2/G-3 RESOLVED 완료)**
> **PDCA 사이클**: 1회 + 사후 정정 2회 (§10 진단 후 FR-01 재설계, §13 ghostty 패치 후 FR-02 완성)

---

## Executive Summary

### 1.1 기능 개요

M-13 Input UX 는 한글 조합 미리보기(FR-01)와 마우스 커서 모양(FR-02) 두 기능을 통해 GhostWin 의 **터미널 기본기**를 완성한 마일스톤이다. 한국어 사용자가 "지금 뭘 치고 있는지" 알 수 있게 됐고, vim/htop 등 TUI 앱의 커서 모양 변경 요청이 마우스 포인터에 즉시 반영된다.

**완료 범위**:
- ✅ FR-01 한글 조합 미리보기 (WPF 단일 IME 입구 재설계, AC-01~06 전수 PASS)
- ✅ FR-02 마우스 커서 모양 (ghostty upstream 로컬 패치 + 5계층 콜백 경로 + 34종 enum 매핑, AC-07~10 PASS)
- ✅ G-2 / G-3 사용자 직접 검증 완료 (DPI 위치/크기 정상, 멀티 pane 잔상 없음) — **잔존 OPEN 없음**

**구현 규모** (working tree 기준):
- C# 신규 8 파일 + 변경 4 파일
- C++ / vt-bridge 변경 6 파일
- ghostty submodule 로컬 패치 4 파일 (+117 라인)
- 테스트 신규 5 파일 (Unit 5종 + E2E 2종)
- 진단 인프라 신규 5 파일 (ImeDiag + MouseCursorOracle 3종 + 진단 스크립트)
- **합계: 약 28 파일 / 35+ 변경 지점**

### 1.2 프로젝트 맥락

| 항목 | 내용 |
|------|------|
| **선행 작업** | M-12 Settings UI (97%), Phase 6 전체 완료 |
| **후행 작업** | M-14 렌더 스레드 안전성 (예정) |
| **프로젝트 비전** | Windows 용 AI 에이전트 멀티플렉서 (cmux + ghostty 성능) |
| **GhostWin 의 위치** | "한국어 입력 가능한 터미널" + "TUI 친화적 마우스 커서" 둘 다 갖춰 Windows Terminal / Warp 동등 수준 도달 |

### 1.3 Value Delivered (4 관점)

| 관점 | 내용 |
|------|------|
| **Problem** | 한글 입력 시 조합 중인 글자가 화면에 안 보여 "지금 뭘 치고 있는지" 모름. vim/htop TUI 앱이 커서 모양 변경을 요청해도 항상 화살표로 고정. WPF 이행 후 Phase 4-B TSF 입력 경험이 단절됨 |
| **Solution** | (1) **WPF 단일 IME 입구 재설계** — native TSF 분리, `TextCompositionManager` + `gw_session_set_composition` P/Invoke + `Key.ImeProcessed → ImeProcessedKey` 파싱 고정. (2) **ghostty upstream 패치** — `GHOSTTY_TERMINAL_OPT_MOUSE_SHAPE` (OPT 16) 신설 + 5계층 콜백 경로 + Win32 `SetCursor` 직접 호출 |
| **Function / UX Effect** | 한글 `ㅎ→하→한` 조합이 커서 위치에 실시간 표시 (배경 + 글리프 + 밑줄), 확정 시 즉시 제거. BS 한글 자모 케이스 (Microsoft IME 무응답) 도 Reconcile 패턴으로 통과. vim insert → IBeam, normal → Arrow, link 위 → Hand, split boundary → SizeWE/NS 자동 전환 |
| **Core Value** | **CJK 입력 UX 에서 Windows Terminal / WezTerm 동등 수준 달성** + **TUI 친화적 마우스 커서로 Alacritty 대비 명확한 우위**. 한국어 사용자 진입 장벽 제거. 두 기능 모두 사용자가 "터미널이 당연히 해야 할 일" 로 기대하는 기본기 — 누락 시 "이거 왜 안 돼?" 가 나오던 영역을 메움 |

---

## 2. PDCA 사이클 요약

### 2.1 Plan (계획)

**문서**: `docs/01-plan/features/m13-input-ux.plan.md`

**목표 및 범위**:
- FR-01 한글 조합 미리보기 (P0 Must-Have, 가중치 70%)
- FR-02 마우스 커서 모양 (P1 Should-Have, 가중치 30%)
- 5 Wave 구현 (W1~W5: TSF 데이터 흐름 점검 → 렌더 오버레이 → ghostty mouse shape 콜백 → WPF Cursor 매핑 → 통합 검증)

**예상 기간**: 2~3일

**주요 기술 가정 (이후 §10/§13 에서 일부 반증됨)**:
- TSF `Session::composition` 이 이미 채워지고 있다 → **반증** (WPF 가 IME 직접 소유, TSF 호출 안 됨)
- `build_composition()` 호출만 추가하면 화면에 보임 → **반증** (데이터가 안 옴)
- ghostty `GHOSTTY_ACTION_MOUSE_SHAPE` API 사용 가능 → **부분 반증** (terminal.h 에는 부재, upstream 패치 필요)

### 2.2 Design (설계)

**문서**: 정식 design 문서는 작성되지 않음 (PRD + Plan 으로 직접 진행). PDCA 흐름상 결손이지만, 사후 정정에서 §10.3 / §13.3 mermaid 시퀀스 다이어그램으로 실제 데이터 흐름이 문서화돼 결손을 보충함.

### 2.3 Do (구현)

**기간**: 2026-04-17 ~ 2026-04-20 (4일)

**M-13 family sub-cycle 분리** (Act/Iterate 단계에서 응집성 + 추적성 확보):

| Sub-cycle | 작성일 | 범위 | 결과 |
|---|:--:|---|:--:|
| `fr-02-mouse-cursor-shape.plan` | 2026-04-18 | FR-02 구현 (ghostty 패치 + 5계층 콜백 + 34종 매핑). M-15 분리 안 하고 M-13 안에서 마무리 | 구현 완료 |
| `fr-02-mouse-cursor-automation.plan/.report` | 2026-04-19~20 | FR-02 자동화 검증 (Tier 3 Oracle UIA + Tier 4 Win32 smoke) | 6/6 테스트 PASS |

**FR-01 한글 조합 미리보기 — 구현 항목**

| 항목 | 구현 상태 | 파일 |
|------|:--:|------|
| C# Policy (FromPreviewEvent / ShouldClearOnBackspace) | ✅ | `src/GhostWin.Core/Input/ImeCompositionPreview.cs` |
| C# Controller (Begin/Reconcile/_suppressedBackspaceReplay) | ✅ | `src/GhostWin.App/Input/TextCompositionPreviewController.cs` |
| WPF 이벤트 라우팅 (TextCompositionManager + ImeProcessedKey 패턴) | ✅ | `src/GhostWin.App/MainWindow.xaml.cs` |
| C++ P/Invoke 입구 (`gw_session_set_composition`) | ✅ | `src/engine-api/ghostwin_engine.cpp`, `ghostwin_engine.h` |
| Native 데이터 모델 (`ImeCompositionState`) | ✅ | `src/session/session.h` |
| 렌더 오버레이 (`build_composition` 배경+글리프+밑줄, surrogate pair, CJK advance-centering) | ✅ | `src/renderer/quad_builder.cpp/.h` |
| 진단 인프라 (ImeDiag + run_with_log + inspect_ime_logs) | ✅ | `src/GhostWin.App/Diagnostics/ImeDiag.cs`, `scripts/run_with_log.ps1`, `scripts/inspect_ime_logs.ps1` |

**FR-02 마우스 커서 모양 — 구현 항목**

| 계층 | 구현 상태 | 파일 |
|------|:--:|------|
| ghostty C 콜백 typedef + OPT 16 | ✅ | `external/ghostty/include/ghostty/vt/terminal.h` (+59 라인 로컬 패치) |
| ghostty Zig stream 핸들러 | ✅ | `external/ghostty/src/terminal/c/terminal.zig` (+40), `src/terminal/stream_terminal.zig` (+22), `src/build/gtk.zig` (+5) |
| vt_bridge C 래퍼 | ✅ | `src/vt-core/vt_bridge.c:410-428`, `vt_bridge.h:197-202` |
| VtCore C++ 어댑터 | ✅ | `src/vt-core/vt_core.cpp:207-210`, `vt_core.h:126-128` |
| engine-api 콜백 등록 + Session 라우팅 | ✅ | `src/engine-api/ghostwin_engine.cpp` (`on_mouse_shape`) |
| C# Interop (Dispatcher 마셜링) | ✅ | `src/GhostWin.Interop/NativeCallbacks.cs:70-75` |
| WPF UI 적용 (Win32 SetCursor 직접 호출) | ✅ | `src/GhostWin.App/Controls/TerminalHostControl.cs:64-73` |
| 34 종 enum 매핑 (DEFAULT~ZOOM_OUT + Arrow fallback) | ✅ | `src/GhostWin.App/Input/MouseCursorShapeMapper.cs` (43 라인) |
| Oracle 진단 (UIA AutomationProperty 노출) | ✅ | `src/GhostWin.App/Input/MouseCursorOracleProbe/State/Formatter.cs` |

### 2.4 Check (검증)

**문서**: `docs/03-analysis/m13-input-ux.analysis.md` (§1~§13, 약 600 라인)

**검증 사이클** (3 단계):

| 단계 | 시점 | FR-01 | FR-02 | 가중 Match | 비고 |
|------|------|:--:|:--:|:--:|------|
| ① 사전 추정 (§9) | 2026-04-17 (Plan/Design 시점) | 70% | 20-30% | 약 45% | 초안 가정 |
| ② 진단 직후 (§10) | 2026-04-18 | 30% | 동일 | 약 15% | TSF 경로 단절 발견, 4 가지 결정적 오해 정리 |
| ③ Gap Analysis (§12) | 2026-04-18 | 100% | 0% | **70.0%** (사용자 검증) | FR-01 사용자 검증 통과, FR-02 미구현 |
| ④ §13 초안 | 2026-04-20 | 83.3% (엄격) | 100% | 88.3% (엄격) / 100% (사용자) | ghostty upstream 패치 후 FR-02 완성 |
| ⑤ **최종 (G-2/G-3 RESOLVED)** | **2026-04-20** | **100%** | **100%** | **100% (엄격 = 사용자 검증)** | 사용자 직접 검증으로 AC-05/06 PASS |

**채택 점수**: **100% (엄격 = 사용자 검증 일치)** — 사용자가 IME 조합 표시 + 멀티 pane 잔상 모두 정상 동작 확인. PARTIAL 0개, OPEN 0개.

### 2.5 Act (사후 정정 — M-13 의 핵심 학습)

이 마일스톤의 가장 큰 가치는 **PDCA 사이클 자체의 사후 정정 패턴**에 있다. Plan/Design 단계의 가정이 두 번 반증됐고, 두 번 모두 정정 섹션을 분석 문서에 보존했다.

**1차 사후 정정 (§10, 2026-04-18)** — FR-01 데이터 입구 단절 발견:

| 초안 가정 | 실제 진실 | 발견 시점 |
|:-:|---|---|
| TSF 가 `Session::composition` 에 글자를 채워준다 | WPF 가 IME 를 직접 소유. native TSF 호출 안 됨 | 빌드/실행 진단 |
| `build_composition()` 호출만 추가하면 화면에 보임 | 데이터가 안 오니 어떤 렌더 코드를 짜도 안 보임 | 진단 LOG |
| PRD "TSF 94 E2E 테스트 통과" 안전 신호 | WinUI3 시절 검증, WPF 이행 후 통합 테스트 부재 | git 추적 |
| BS 분기는 `actualKey == Key.Back` 으로 들어온다 | WPF 가 IME 활성 시 `e.Key = Key.ImeProcessed` 로 wrapping, 진짜 키는 `e.ImeProcessedKey` | 진단 LOG #0007 |

**해결**: `Key.ImeProcessed` switch 패턴 (한 줄 추가) 으로 BS 모든 케이스 통과. WPF 단일 IME 입구 재설계 + Backspace race-safe Reconcile 패턴 도입.

**2차 사후 정정 (§13, 2026-04-20)** — FR-02 ghostty upstream 패치 채택:

| §11/§12 권고 | 실제 채택 | 사유 |
|---|---|---|
| FR-02 를 M-13 에서 분리, 별도 마일스톤(M-15) 으로 이연 (★★★ 권장) | **옵션 B (iterate)** 채택 — ghostty upstream 패치 + 5계층 콜백 경로 구축 | "ghostty 패치 = ADR-001 영향 큼" 추정이 과대평가였음. 실제 4파일 +117라인, ADR-001 무영향 |

---

## 3. 주요 발견 및 교훈

### 3.1 4 가지 결정적 오해 → 사후 정정 패턴

§10.2 표가 그대로 교훈이다. 사전 분석에서 안전하다고 본 모든 가정이 진단 단계에서 흔들렸다. 이로부터 도출된 패턴:

1. **"동작한다" 주장은 LOG 증거 첨부 요구** — "TSF 94 테스트" 신호만 믿지 말고 현재 셸/플랫폼에서 재검증
2. **데이터 입구 검증이 렌더보다 먼저** — render 코드 수정 전에 콜백 호출 여부 LOG 로 확인
3. **WPF + native 하이브리드는 이벤트 가로채기 의심** — TextInput, Drag, Cursor, IME 모두 Wrapping 가능성
4. **이전 해결 이슈 재발 패턴 추적** — git log 로 같은 본질의 이슈 재등장 감지 (커밋 `6812164` BS 이슈가 다른 레이어로 재등장)

### 3.2 사후 정정 섹션 보존 = 학습 압축

분석 문서가 §1~§9 (초안) + §10 (1차 정정) + §12 (Gap Analysis) + §13 (2차 정정) 으로 시간순 보존됐다. 일반적인 PDCA 흐름은 정정 시 초안을 덮어쓰지만, 이 방식은 **"왜 틀렸는가" 가 함께 보존**돼 다음 사이클의 학습 자산이 된다. 다음 마일스톤에서 같은 가정을 반복하지 않을 수 있다.

### 3.3 "권고 옵션 = 정답" 이 아니다

§12.5 가 옵션 A (분리) 를 ★★★ 로 권장했지만 실제 채택은 옵션 B (iterate) 였다. 권고는 Gap Analysis 시점의 비용/리스크 추정이고, 실제 작업 착수 시점에 새 정보 (ghostty 패치 규모 작음) 가 들어오면 옵션이 바뀌는 게 정상. 권고 무효화도 §13 에 명시적으로 기록해 흐름이 끊기지 않게 했다.

### 3.4 진단 인프라는 보존 가치

`ImeDiag` (FR-01) 과 `MouseCursorOracle` (FR-02) 은 모두 **env var 게이팅 + zero-cost early-out** 패턴으로 보존됐다. 평소 F5 디버깅 시 OFF, IME 회귀 발생 시 `GHOSTWIN_IMEDIAG=1` 로 즉시 재진단. 미래 비용 절감 효과가 큰 자산.

### 3.5 Win32 SetCursor 직접 호출 (WPF Cursor 우회)

FR-02 의 WPF 측 적용은 `Cursor` 의존 프로퍼티 대신 `SetCursor(LoadCursor(NULL, IDC_*))` 직접 호출을 선택했다. 이유:

- `TerminalHostControl` 이 `HwndHost` 기반이라 자체 HWND 보유
- WM_SETCURSOR 처리가 깔끔
- 매 프레임 비용 ≈ 0 (의존 프로퍼티 변경 알림 + 비트맵 변환 오버헤드 회피)
- 34 종 enum 전수 매핑 가능 (WPF `Cursors.*` 보다 풍부)

이 패턴은 향후 다른 native control hybrid 작업의 참조점.

---

## 4. 잔존 작업

**모든 Gap RESOLVED — 코드/검증 갭 없음.**

| 항목 | 상태 | 비고 |
|------|:--:|------|
| G-1 ~ G-5 | **RESOLVED** | §13.11 — HIGH 1 + MEDIUM 2 + LOW 2 모두 해소 |
| G-6 진단 인프라 보존 | **유지 (의도)** | ImeDiag + MouseCursorOracle env var 게이팅 |
| ghostty upstream PR 제출 | **OPTIONAL** | OPT 15/16 패치를 upstream 에 보내면 향후 submodule 동기화 시 충돌 회피. 현재 로컬 패치로 안정 동작 중 |

---

## 5. 다음 단계 권고

| 우선순위 | 작업 | 비고 |
|:-:|------|------|
| **1 (즉시)** | `/pdca archive m13-input-ux --summary` | 5 PDCA 문서를 `docs/archive/2026-04/m13-input-ux/` 로 이동 + 메트릭 보존 |
| **2 (단기)** | M-14 렌더 스레드 안전성 진행 | M-13 §10.2 의 "WPF + native 하이브리드 이벤트 가로채기" 교훈을 M-14 의 thread coordination 설계에 반영 |
| **3 (중기)** | ghostty upstream PR 제출 (OPT 15/16) | submodule 동기화 부담 제거 |
| **4 (선택)** | M-15 입력 UX v2 | IME 다국어 (일본어 / 중국어) 검증 + Mouse cursor 추가 enum (CSS 표준 외) |

---

## 6. 핵심 학습 한 문단

M-13 은 "Plan/Design 단계의 가정이 두 번 반증된 마일스톤" 이다. 첫 번째는 TSF 가 동작한다는 가정 (반증 → WPF 단일 IME 입구 재설계), 두 번째는 ghostty 패치가 비싸다는 가정 (반증 → 4파일 117라인 로컬 패치 채택). 두 정정 모두 분석 문서에 시간순 보존됐고, 이로 인해 PDCA 사이클이 **"덮어쓰기" 대신 "층층이 쌓이는 학습 기록"** 으로 작동했다. **100% (엄격 = 사용자 검증)** 의 Match Rate 는 결과 수치이고, 진짜 가치는 **사후 정정 패턴 + sub-cycle 분리 (구현 sub + 자동화 sub)** 에 있다. 이 패턴을 다음 마일스톤 (M-14, M-15) 에서 표준 절차로 채택할 가치가 있다.

---

## 7. 관련 문서

| 문서 | 경로 |
|------|------|
| PRD | `docs/00-pm/m13-input-ux.prd.md` |
| Plan (main) | `docs/01-plan/features/m13-input-ux.plan.md` |
| Plan (FR-02 sub) | `docs/01-plan/features/fr-02-mouse-cursor-shape.plan.md` |
| Plan (FR-02 automation sub) | `docs/01-plan/features/fr-02-mouse-cursor-automation.plan.md` |
| Design | (작성되지 않음 — §10.3 / §13.3 mermaid 시퀀스로 보충) |
| Analysis | `docs/03-analysis/m13-input-ux.analysis.md` (§1~§13, 약 600 라인) |
| 부속 분석 | `docs/03-analysis/m13-ime-backspace-residual-summary.md` |
| FR-02 자동화 보고서 | `docs/04-report/features/fr-02-mouse-cursor-automation.report.md` |
| Report (this) | `docs/04-report/features/m13-input-ux.report.md` |

---

*PDCA Completion Report v1.0 — generated 2026-04-20 (post §13 update).*
