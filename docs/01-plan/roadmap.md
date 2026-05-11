# GhostWin Roadmap (2026-05-12 기준)

> **이 프로젝트의 존재 이유**: Windows 용 **AI 에이전트 멀티플렉서**
> (macOS cmux + ghostty 성능 철학을 윈도우 네이티브로).
>
> 자세한 비전: `onboarding.md` (프로젝트 루트) + Obsidian `_index.md` 3대 비전 표.

---

## 🎯 3대 비전 (모든 의사결정의 기준선)

| # | 축 | 진행 |
|:-:|----|:----:|
| 1 | **macOS cmux 기능 탑재** (수직 탭, pane 분할, 알림 링/패널, 워크스페이스, 세션 복원, ContextMenu, drag-and-drop) | ✅ **완성** (M-16-D 까지 도달) |
| 2 | **AI 에이전트 멀티플렉서 기반** (OSC hooks, Named pipe 훅, 에이전트 배지, Toast) | ✅ **Phase 6 완결** (6-A 93% + 6-B 97% + 6-C 95%) |
| 3 | **타 터미널 대비 성능 우수** (ghostty libvt + DX11 인스턴싱 + ClearType + CJK + IME) | ✅ **기반 완성** (M-14 render 안전 ✅ + M-15 Stage A baseline ✅, Stage B 는 설치/하네스 probe 완료, p95 최종 판정만 권한 조건 잔여) |

---

## 운영 정책

- **UI 영어 단일 운영** (2026-05-09 결정) — i18n / 다국어 / RTL 미지원. 사용자 base 다양화 시점에 별도 사이클로 재논의 (memory: `project_english_only_ui.md`).

---

## 현재 위치 (2026-05-12)

```
Phase 1~4 ✅ → M-1~M-13 ✅ → Phase 6-A/B/C ✅ → M-11/12 ✅ → M-14 ✅ → M-15 Stage A ✅
                                                                                       ↓
                                                                        M-16-A/B/C/D ✅
                                                                                       ↓
                                                                              hotfix 05-06 ✅
                                                                                       ↓
                                                                          ★ M-16-F 여기 ★ (PDCA Plan Active)
                                                                                       ↓
                                                                          (선택) M-16-E / mini 4 / M-15 Stage B p95 / M-17
```

**앱 상태**: DX11 렌더링 + ConPTY + WPF Shell (FluentWindow + Mica/MicaAlt) + 다중 Workspace/Pane + 마우스 + 복붙 + DPI + IME 조합 미리보기 + TUI 마우스 커서 + AI 에이전트 알림 링/패널/배지/Toast/Named pipe 훅 + Settings UI + Command Palette + 세션 복원 + ClearType + CJK + 디자인 시스템 (테마 토큰 4 ResourceDictionary) + ContextMenu 4영역 + 워크스페이스 drag-and-drop + per-pane ScrollBar + cell-snap padding 사방 균등 분배.

**부족한 것 (우선순위 순)**:
1. **M-16-F UI 체감 마감** (★ 다음 실행 — 2026-05-08 자동화 audit 24결함 중 P1 1 + P2 14 단일 사이클 1.5-2주, PDCA Plan phase active)
2. (선택) **mini 4건** — `m16-a-spacing-extra`, `m16-a-cursor-hover`, `m16-a-mainwindow-a11y`, `m16-b-mica-visibility` (OS wallpaper architectural limit)
3. (선택) **M-16-E** — 1결함만 잔여 (분리 발생 사유 archive)
4. (선택) **M-15 Stage B 외부 p95 최종 판정** — WT/WezTerm/Alacritty 설치와 보조 하네스는 확인됨. PresentMon CSV 생성은 elevated 권한 세션에서 재실행 필요.
5. (선택) **M-17 입력 UX v2** — 다국어 IME 검증 (영어 단일 정책 하 보류)

---

## 완료된 마일스톤

| 마일스톤 | 내용 | 완료일 | Match |
|----------|------|:------:|:--:|
| **🎯 hotfix 2026-05-06** | ghostty palette bootstrap (terminal_new 후 OPT_COLOR_* 4종 push 누락 → 모든 cell bg=#000000) + Tab edge case + API 수정 3건 | 2026-05-06 | hotfix |
| **🎯 M-16-D cmux UX 패리티** | ContextMenu 4영역 (Sidebar 7 + Terminal 7 + Pane 4 + Notification 3 = 21/21 a11y Name) + 워크스페이스 drag-and-drop 재정렬 (1px Adorner + 4px threshold + 3중 Risk-2 mitigation) + ZoomPane Visibility=Collapsed + External Launcher (VS Code/Cursor/Explorer where.exe PATH probe) | 2026-04-30 | **94%** |
| **🎯 M-16-C 터미널 렌더 정밀화** | focus border 토글 제거 + DX11 dim overlay 0.4 + per-pane WPF ScrollBar + cell-snap residual padding 사방 균등 분배 + 마우스/selection 좌표 padding 정합 | 2026-04-29 | **92%** |
| **🎯 M-16-B 윈도우 셸** | Wpf.Ui.Controls.FluentWindow 교체 + Mica/MicaAlt + 자체 GridSplitter ControlTemplate + GridLengthAnimationCustom + 5 핵심 결함 closure (Mica/Splitter sync/NotifPanel fade/BorderThickness/DPI) + architectural sub-issue 4건 진단·closure | 2026-04-29 | **92%** |
| **🎯 M-16-A 디자인 시스템** | `src/GhostWin.App/Themes/` 4 ResourceDictionary (Colors.{Dark,Light}.xaml + Spacing.xaml + FocusVisuals.xaml) + `MergedDictionaries.Swap` 패턴 (22줄 → 5줄) + N1 splitter transparent 진짜 root cause (RelativeSource binding fail) + audit 39 결함 중 21 처리 | 2026-04-29 | **96%** |
| **M-15 Stage A baseline 자동화** | M-14 follow-up — 4-pane resize 자동 CSV + load 자동화 + idle CPU 절대값 close. tests/GhostWin.MeasurementDriver C# + scripts/measure_render_baseline.ps1 (511줄, PS5/7 호환). Release 검증 3 시나리오 (idle 22,969μs / resize-4pane 21,470μs / load 514,952μs). Stage B 는 2026-05-12 설치/하네스 probe 완료, 외부 p95 최종 판정은 elevated PresentMon CSV 필요 | 2026-04-27 | **97%** |
| **M-14 렌더 스레드 안전성** | W2 `shared_mutex + FrameReadGuard` reader 안전 계약 + W3 `SessionVisualState` snapshot-atomic + `force_all_dirty()` 제거 → **idle 렌더 1,643→4 frame (−99.76%)**. 1-pane resize p95 33ms NFR +1.3ms. render_state_test 17/17 + session_visual_state_test 3/3 PASS. Known Gap 4건은 M-15 이관 | 2026-04-23 | **82%** |
| **M-13 Input UX** | FR-01 한글 조합 미리보기 (WPF 단일 IME 입구 + Backspace reconcile + Key.ImeProcessed fix) + FR-02 마우스 커서 모양 (ghostty OPT 16 + 5계층 콜백 + Win32 SetCursor + 34종 enum) + Tier 3/4 자동화 | 2026-04-20 | **100%** |
| **session-restore** | 워크스페이스 스냅샷 영속화 (`%APPDATA%/GhostWin/session.json`) | 2026-04-19 | 100% |
| **M-12 Settings UI** | 설정 페이지 (4 카테고리, Ctrl+,) + Command Palette (Ctrl+Shift+P) + JSON↔GUI 양방향 동기화 | 2026-04-17 | 97% |
| **🎯 Phase 6-C 외부 통합** | Named Pipe 훅 서버 + ghostwin-hook.exe CLI + git branch 사이드바 | 2026-04-17 | 95% |
| **🎯 Phase 6-B 알림 인프라** | 알림 패널 (Ctrl+Shift+I, 100건 FIFO) + AgentState 5-state 배지 + Toast 클릭 → 탭 전환 | 2026-04-16 | 97% |
| **🎯 Phase 6-A OSC + 알림 링** | OSC 9/99/777 캡처 → 비활성 탭 dot + Win32 Toast | 2026-04-16 | 93% |
| **e2e-test-harness** | M-11.5 E2E xUnit 허브 (Tier 1/2 8 facts, 18s) | 2026-04-16 | 100% |
| **dpi-scaling-integration** | 런타임 DPI awareness + cell metrics 파이프라인 | 2026-04-12 | 100% |
| **vt-mutex-redesign** | vt_core mutex 구조 재설계 (M-14 선행 인프라) | 2026-04-14 | 100% |
| **io-thread-timeout-v2** | ConPTY io thread 종료 race fix (CancelSynchronousIo) | 2026-04-13 | 100% |
| **Pre-M11 Cleanup** | Follow-up 9 + Tech Debt 7 = 16건 청산 (15/16) | 2026-04-15 | 94% |
| **wpf-migration** | WinUI3 Code-only C++ → WPF C# Clean Architecture (4프로젝트). M-1~M-16 모두 WPF 위에서 진행 | 2026-03~04 | 완료 |
| **M-10.5 Clipboard** | Ctrl+C/V (1단계 완료, 보안 필터링/BracketedPaste/OSC 52 는 v2 이연) | 2026-04-13 | 1단계 |
| **M-10 Mouse** | 클릭/스크롤/선택 + CJK + DX11 하이라이트 | 2026-04-11 | 완료 |
| **M-8~M-9** | Pane 분할 + Workspace 관리 | 2026-04-08 | 완료 |
| **M-1~M-7** | WinUI3 → WPF 이행 + 기본 인프라 | 2026-04-06 | 완료 |
| **Phase 5-A~E** | session/tab/titlebar/settings/pane-split | ~2026-04-08 | 완료 |

archive: `docs/archive/2026-04/_INDEX.md` (42 사이클, M-14 ~ M-16-D 포함) + `docs/archive/2026-03/_INDEX.md` (8 사이클) + `docs/archive/legacy/_INDEX.md` (5 폴더)

---

## 🎯 확정 실행 순서 (2026-05-09 기준)

```
1.  M-11 Session Restore             ✅ 완료 (96%)
2.  M-11.5 E2E 자동화 체계화         ✅ 완료 (100%)
3.  🎯 Phase 6-A OSC + 알림 링       ✅ 완료 (93%) — 핵심 가설 실증
4.  🎯 Phase 6-B 알림 인프라         ✅ 완료 (97%) — 운영 인프라 완성
5a. M-12 Settings UI                 ✅ 완료 (97%) — 설정 페이지 + Command Palette + 테마
5b. 🎯 Phase 6-C 외부 통합           ✅ 완료 (95%) — Named pipe + git branch
6.  M-13 Input UX                    ✅ 완료 (100%) — FR-01 + FR-02 + Tier3/Tier4 자동화
7.  M-14 렌더 스레드 안전성          ✅ 완료 (82%) — reader 안전 계약 + idle −99.76%
8.  M-15 Stage A baseline 자동화     ✅ 완료 (97%) — idle/resize/load 자동 CSV
9.  🎯 M-16-A 디자인 시스템          ✅ 완료 (96%) — 4 ResourceDict + 테마 swap
10. 🎯 M-16-B 윈도우 셸              ✅ 완료 (92%) — FluentWindow + Mica
11. 🎯 M-16-C 터미널 렌더 정밀화     ✅ 완료 (92%) — padding + per-pane scrollbar
12. 🎯 M-16-D cmux UX 패리티         ✅ 완료 (94%) — ContextMenu + drag-drop
13. hotfix 2026-05-06                ✅ 완료 — palette + Tab + API
14. ★ M-16-F UI 체감 마감            ★ 다음 순서 ★ (PDCA Plan active)
─────────────────────────────────────────────────────────────────────
선택 트랙:
  • mini 4건: m16-a-spacing-extra / cursor-hover / mainwindow-a11y / m16-b-mica-visibility
  • M-16-E (1결함만 분리 잔여)
  • M-15 Stage B p95 finalization (WT/WezTerm/Alacritty, elevated PresentMon 필요)
  • M-17 입력 UX v2 (영어 단일 정책 하 보류)
```

**근거**:
- 비전 ① cmux 기능 탑재 = M-16-D 까지로 핵심 도달, **M-16-F 가 UI 완성도 임계 통과의 마지막 한 걸음**
- 비전 ② AI 에이전트 멀티플렉서 = Phase 6 전체 완결
- 비전 ③ 성능 우수 = M-14 reader 안전 + M-15 Stage A baseline = 기반 완성. Stage B 는 설치/하네스 probe 완료, p95 최종 판정만 선택 트랙
- M-16-F 는 2026-05-08 자동화 audit (4 Explore agent 병렬 + UIA dump) 발굴 24결함 중 **P1 + P2 핵심 묶음** — 사용자 PC 의존 제거하는 자동화 검증 도구 (xunit + FlaUI) 도입 포함

---

## 다음 마일스톤 상세

### M-16-F: UI 체감 마감 (★ 다음 실행)

> 목표: M-16-A/B/C/D + Hotfix 2026-05-06 closure 후 잔여 UI 결함 24건 중 **P1 + P2 핵심 묶음** 단일 사이클 마감. cmux 감성 도달 (UI 완성도 임계 통과).
> 비전 축 ① cmux 기능 탑재 — UI 완성도 마감 사이클.

**상태**: PM Done → **PLAN Active** (2026-05-08 PRD: `docs/00-pm/m16-f-ui-completion.prd.md`)

#### P1 (사용자 시각 critical, 1건)

| # | 결함 | 검증 방식 |
|:-:|---|---|
| **L6** | cell-snap residual padding (최대화 하단 잘림) | xunit + FlaUI maximize → bottom 픽셀 sample |

#### P2 (UX 체감 / a11y, 14건)

| # | 결함 | 검증 |
|:-:|---|---|
| **A1** | ToolTip 6% — main 13 + settings 17 = 30 visible 중 2건만 명시 | UIA dump |
| **NEW-A** | 캡션 버튼 (Min/Max/Close) UIA Name 누락 | UIA dump |
| **NEW-B** | 워크스페이스 close ✕ Name + HelpText 누락 | UIA dump |
| **F1** | TabIndex 명시 잔여 (다른 영역) | grep |
| **F6** | Focusable=False 21건 — E2E hook + 사용자 차단 혼재 | grep |
| **F9** | Tab passthrough Settings 열린 edge case 미검사 | grep + xunit/FlaUI |
| **F10** | SettingsPageControl 컨테이너 TabIndex 누락 | grep |
| **F12** | NotifPanel ContextMenu 미정의 | UIA Menu type 0 |
| **F13** | Mouse wheel 줌 (Ctrl+Wheel) / 스크롤백 (Shift+Wheel) 미구현 | grep |
| **F15** | KeyboardNavigation.TabNavigation=None 부수효과 미검증 | grep |
| **L1** | Spacing 토큰 정의 후 inline `Margin="12,0"` 등 비일관 | grep |
| **L3** | Sidebar item `Margin="4,1"` / `Padding="8,6"` magic 잔존 | grep |
| **L4** | NotificationPanelWidth GridLengthAnimationCustom 적용 검증 | xunit + FlaUI |
| **C-NEW-1** | PaneContainerControl `Color.FromRgb` 하드코드 3건 — Light theme 미반영 | grep line 379, 404, 456 |

#### Out of Scope

- **P3 11건** — A5 (i18n) / L2 / L5 / NEW-C / C-NEW-2 / F11 / F14 / A3 / A4 / A7 / A8 → M-16-G 또는 후속 사이클
- **A5 (i18n / 다국어)** — 영어 단일 운영 결정 (2026-05-09)
- **A6 (FlowDirection RTL)** — 영어 단일 운영 우선순위 후순위

#### 자동화 도구 도입 (NFR)

- xunit + FlaUI 조합으로 시각 결함 직접 측정 — **사용자 PC 의존 제거**
- M-16-B 까지는 사용자 PC 8 step 검증 의존 → M-16-F 부터 agent 가 직접 검증

#### 출처

- `docs/00-research/2026-05-08-ui-completeness-audit.md` — 2차 자동화 audit (4 Explore agent + PowerShell + UIA `AutomationElement.FindAll(Subtree)`)

---

### (선택) mini 4건

| 마일스톤 | 출처 | 규모 |
|----------|------|:----:|
| **m16-a-spacing-extra** | M-16-A 분리 — W/H/FontSize 토큰화 | 소 |
| **m16-a-cursor-hover** | M-16-A 분리 — Cursor + hover 잔여 | 소 |
| **m16-a-mainwindow-a11y** | M-16-A 분리 — MainWindow TabIndex | 소 |
| **m16-b-mica-visibility** | M-16-B 분리 — OS wallpaper architectural limit (DwmGetWindowAttribute=2 외부 진단 확증, 사용자 시각은 환경 의존) | 분석만 |

### (선택) M-16-E

> M-16-D 또는 직전 사이클에서 1결함만 분리 잔여. 본 사이클에 흡수 가능 시 M-16-F 와 통합 검토.

### (선택) M-15 Stage B 외부 p95 최종 판정

> Windows Terminal / WezTerm / Alacritty 설치 확인과 보조 하네스 정비는 2026-05-12 완료. 엄격한 idle p95 / resize p95 / load p95 비교는 PresentMon CSV 가 필요한데, 현재 비관리자 세션 probe 에서는 CSV 가 생성되지 않았다. 다음 실행은 elevated PowerShell 또는 ETW 캡처 권한이 있는 세션에서 진행.

### (선택) M-17 입력 UX v2

| 순서 | Feature | 규모 | 설명 |
|:----:|---------|:----:|------|
| 1 | IME 다국어 검증 | 중 | 일본어 (Microsoft IME) + 중국어 (微软拼音/搜狗) 조합 미리보기 회귀 — **영어 단일 정책 하 보류** |
| 2 | Mouse cursor 추가 enum | 소 | ghostty CSS 표준 외 신규 enum 매핑 추가 (필요 시) |

---

## 의존성 다이어그램

```
✅ M-11 Session Restore
  ↓
✅ 🎯 Phase 6-A (OSC hook + 알림 링, 핵심 가설 검증)
  ↓
✅ 🎯 Phase 6-B (알림 패널 + 배지 + Toast)
  ↓
✅ M-12 Settings UI  ───┬─── 병행 완료
  ↓                       │
✅ M-13 Input UX     ✅ 🎯 Phase 6-C (Named pipe + git)
  ↓
✅ M-14 렌더 스레드 안전성 (reader 안전 + idle −99.76%)
  ↓
✅ M-15 Stage A baseline 자동화 (idle/resize/load CSV)
  ↓
✅ 🎯 M-16-A 디자인 시스템 (4 ResourceDict + 테마 swap)
  ↓
✅ 🎯 M-16-B 윈도우 셸 (FluentWindow + Mica)
  ↓
✅ 🎯 M-16-C 터미널 렌더 정밀화
  ↓
✅ 🎯 M-16-D cmux UX 패리티 (ContextMenu + drag-drop)
  ↓
✅ hotfix 2026-05-06 (palette + Tab + API)
  ↓
★ M-16-F UI 체감 마감          ← 2026-05-08 audit 24결함 중 P1+P2
  ↓
(선택) mini 4 / M-16-E / M-15 Stage B p95 / M-17
```

---

## 진행 규칙

1. **비전 정렬 우선** — 모든 새 작업은 3대 축 중 어디에 기여하는지 명시
2. **마일스톤 단위 PDCA** — PM(선택) → Plan → Design → Do → Check → Archive
3. **사후 정정 보존** — Plan/Design 가정이 반증되면 § 사후 정정 형태로 시간순 보존 (M-13 §10/§13 패턴, M-14 design v1.1 소급, M-16-A N1 발견)
4. **기술 부채는 마일스톤 사이에 삽입** — Pre-M11 Cleanup 패턴 유지
5. **주요 feature 는 리서치 선행** — 참조 코드베이스 조사 후 설계 (M-16 시리즈는 audit 선행 — 2026-04-28 + 2026-05-08)
6. **외부 패치는 fork 로 pin** — ghostty 처럼 upstream 손대는 변경은 private fork + 빌드 재현성 확보 (M-13 사례, M-16-D NFR-02 git diff submodule 빈 결과로 0 patch 검증)
7. **자동화 검증 도입** — M-16-F 부터 사용자 PC 의존 제거 (xunit + FlaUI 직접 측정)
8. **UI 영어 단일 운영** — i18n / 다국어 / RTL 미지원, 사용자 base 다양화 시점 별도 사이클로 재논의 (2026-05-09 결정)

---

## 관련 문서

- **원본 비전**: `onboarding.md` (프로젝트 루트) — v0.5 (2026-04-15)
- **Obsidian 로드맵** (상세): `C:\Users\Solit\obsidian\note\Projects\GhostWin\Milestones\roadmap.md`
- **Obsidian 프로젝트 진입점**: `C:\Users\Solit\obsidian\note\Projects\GhostWin\_index.md`
- **cmux 리서치**: `docs/00-research/cmux-ai-agent-ux-research.md` (2026-03-28)
- **ghostty 패치 분석**: `docs/00-research/ghostty-upstream-sync-analysis.md`
- **UI audit 1차** (M-16-A/B/C/D 출처): `docs/00-research/2026-04-28-ui-completeness-audit.md` (39 결함)
- **UI audit 2차** (M-16-F 출처): `docs/00-research/2026-05-08-ui-completeness-audit.md` (24 결함, 자동화)
- **M-16-F PRD**: `docs/00-pm/m16-f-ui-completion.prd.md`
- **PDCA archive 인덱스**: `docs/archive/2026-04/_INDEX.md` (42 사이클) + `docs/archive/2026-03/_INDEX.md` (8 사이클) + `docs/archive/legacy/_INDEX.md`

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | Initial roadmap |
| 0.2 | 2026-04-11 | M-10 완료 반영. M-10.5 복사/붙여넣기 추가. M-13 입력 UX 분리 |
| 0.3 | 2026-04-15 | 비전 동기화 — Phase 6 AI 에이전트 멀티플렉서 복원. 확정 실행 순서 (M-11 → Phase 6-A → 6-B → M-12/6-C → M-13). M-10.5 ~ Pre-M11 완료 반영 |
| 0.4 | 2026-04-20 | M-11 ~ M-13 + Phase 6-A/B/C + session-restore 모두 완료 반영. ★ 위치를 M-14 로 이동. 완료 마일스톤 표 확장 (Match Rate 컬럼 추가). 남은 상세는 M-14 만 유지 (완료된 마일스톤은 archive 인덱스 참조). Pre-M11 plan 경로를 archive 로 갱신. ghostty fork 정책 항목 추가 (진행 규칙 #6) |
| **0.5** | **2026-05-09** | **M-14 + M-15 Stage A + M-16-A/B/C/D + hotfix 2026-05-06 모두 완료 반영. ★ 위치를 M-16-F 로 이동 (PDCA Plan active, 2026-05-08 PRD). 비전 ① cmux 기능 탑재 = M-16-D 까지 도달, ③ 성능 우수 = M-14 + M-15 Stage A 로 기반 완성 갱신. 운영 정책 § 신설 — 영어 단일 UI 결정 (2026-05-09). 진행 규칙 #7 (자동화 검증 도입), #8 (영어 단일 운영) 추가. 의존성 다이어그램 M-14~M-16-F 구간 확장. 선택 트랙 4건 (mini / M-16-E / M-15 Stage B / M-17) 명시. 관련 문서 § 에 audit 1차/2차 + M-16-F PRD 추가. archive 인덱스 카운트 32 → 42 사이클 갱신.** |
