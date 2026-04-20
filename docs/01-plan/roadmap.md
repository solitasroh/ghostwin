# GhostWin Roadmap (2026-04-20 기준)

> **이 프로젝트의 존재 이유**: Windows 용 **AI 에이전트 멀티플렉서**
> (macOS cmux + ghostty 성능 철학을 윈도우 네이티브로).
>
> 자세한 비전: `onboarding.md` (프로젝트 루트) + Obsidian `_index.md` 3대 비전 표.

---

## 🎯 3대 비전 (모든 의사결정의 기준선)

| # | 축 | 진행 |
|:-:|----|:----:|
| 1 | **macOS cmux 기능 탑재** (수직 탭, pane 분할, 알림 링/패널, 워크스페이스, 세션 복원) | ✅ 완성 |
| 2 | **AI 에이전트 멀티플렉서 기반** (OSC hooks, Named pipe 훅, 에이전트 배지, Toast) | ✅ **Phase 6 완료** (6-A 93% + 6-B 97% + 6-C 95%) |
| 3 | **타 터미널 대비 성능 우수** (ghostty libvt + DX11 인스턴싱 + ClearType + CJK + IME) | ✅ 기반 완성 (M-14 렌더 안전성 예정) |

---

## 현재 위치 (2026-04-20)

```
Phase 1~4 ✅ → M-1~M-13 ✅ → Phase 6-A/B/C ✅ → M-11~M-12 ✅
                                                            ↓
                                                    ★ M-14 여기 ★
                                                            ↓
                                                  M-15 입력 UX v2 (선택)
```

**앱 상태**: DX11 렌더링 + ConPTY + WPF Shell + 다중 Workspace/Pane + 마우스 + 복붙 + DPI + IME 조합 미리보기 + TUI 마우스 커서 + AI 에이전트 알림 링/패널/배지/Toast/Named pipe 훅 + Settings UI + Command Palette + 세션 복원 + ClearType + CJK.

**부족한 것 (우선순위 순)**:
1. **M-14 렌더 스레드 안전성** (다음 실행 순서 — Phase 6-B 에서 발견된 `_p` RenderFrame 경쟁 근본 해결)
2. ghostty upstream PR 정리 (OPT 15/16 — 현재 private fork `solitasroh/ghostty:ghostwin-patches/v1` 로 유지 중, 선택)
3. M-15 입력 UX v2 (다국어 IME 검증 — 일본어/중국어, 선택)

---

## 완료된 마일스톤

| 마일스톤 | 내용 | 완료일 | Match |
|----------|------|:------:|:--:|
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
| **wpf-migration** | WinUI3 Code-only C++ → WPF C# Clean Architecture (4프로젝트). M-1~M-13 모두 WPF 위에서 진행 | 2026-03~04 | 완료 |
| **M-10.5 Clipboard** | Ctrl+C/V (1단계 완료, 보안 필터링/BracketedPaste/OSC 52 는 v2 이연) | 2026-04-13 | 1단계 |
| **M-10 Mouse** | 클릭/스크롤/선택 + CJK + DX11 하이라이트 | 2026-04-11 | 완료 |
| **M-8~M-9** | Pane 분할 + Workspace 관리 | 2026-04-08 | 완료 |
| **M-1~M-7** | WinUI3 → WPF 이행 + 기본 인프라 | 2026-04-06 | 완료 |
| **Phase 5-A~E** | session/tab/titlebar/settings/pane-split | ~2026-04-08 | 완료 |

archive: `docs/archive/2026-04/_INDEX.md` (32 사이클) + `docs/archive/legacy/` (3 폴더)

---

## 🎯 확정 실행 순서 (2026-04-20 기준)

```
1. M-11 Session Restore            ✅ 완료 (96%)
2. M-11.5 E2E 자동화 체계화        ✅ 완료 (100%)
3. 🎯 Phase 6-A OSC + 알림 링      ✅ 완료 (93%) — 핵심 가설 실증
4. 🎯 Phase 6-B 알림 인프라        ✅ 완료 (97%) — 운영 인프라 완성
5a. M-12 Settings UI               ✅ 완료 (97%) — 설정 페이지 + Command Palette + 테마
5b. 🎯 Phase 6-C 외부 통합         ✅ 완료 (95%) — Named pipe + git branch
6. M-13 Input UX                   ✅ 완료 (100%) — FR-01 + FR-02 + Tier3/Tier4 자동화
7. M-14 렌더 스레드 안전성          ★ 다음 순서 ★
8. (선택) M-15 입력 UX v2          IME 다국어 (일본어/중국어) + 추가 mouse cursor enum
```

**근거**:
- Phase 6-A/B/C 완료 → 비전 ② AI 에이전트 멀티플렉서 핵심 + 운영 + 외부 통합 모두 실증됨
- M-12 / M-13 완료 → 일반 사용자 접근성 + 한국어/TUI UX 완성
- **M-14 는 마지막 핵심 안정성 작업** — Phase 6-B 테스트 중 발견한 창 리사이즈 시 렌더 스레드 `_p` RenderFrame 경쟁 조건. 방어 가드 적용 완료, 근본적 double-buffer 해결은 독립 수행

---

## 다음 마일스톤 상세

### M-14: 렌더 스레드 안전성 (Render Thread Safety)

> 목표: 렌더 스레드 ↔ UI 스레드 간 `_p` RenderFrame 동시 접근 경쟁 조건 근본 해결.
> 비전 축 ③ 성능 우수 — 크래시 없는 안정성이 성능의 전제.

| 순서 | Feature | 의존성 | 규모 | 설명 |
|:----:|---------|--------|:----:|------|
| 1 | **RenderFrame double-buffer** | 없음 | 중 | `_p` 단일 인스턴스 → front/back 이중 버퍼. `start_paint` 에서 back 에 기록, swap 후 `builder.build` 는 front 만 읽음. 리사이즈 시 back 만 reshape → swap 이후 적용 |
| 2 | **resize 원자성 보장** | #1 | 소 | `resize_session` → `_p.reshape()` 이 렌더 스레드 관측 시점과 분리됨을 검증. TSAN 또는 수동 스트레스 테스트 |

**현재 상태**: Phase 6-B 에서 방어적 가드 추가 (빈 span 반환, min 크기 복사). 크래시는 방지하나 리사이즈 시 1프레임 부분 렌더 가능.

**발견 경위**: 2026-04-16 Phase 6-B 테스트 중 창 리사이즈 시 `span subscript out of range` Debug Assertion 발생. `render_state.h:row()` → `quad_builder.cpp:build()` 경로.

**근본 원인**: `builder.build(frame)` 가 `_p` 를 vt_mutex **락 밖**에서 읽는 동안, UI 스레드가 `resize_session()` → `_p.reshape()` 으로 `cols` 를 변경. span 크기 불일치 → 범위 초과.

**선행 인프라**:
- `docs/03-analysis/concurrency/` — 동시성 분석 자료 (직접 참조)
- `docs/archive/2026-04/vt-mutex-redesign/` — mutex 재설계 결과
- `docs/archive/2026-04/m13-input-ux/` 의 §10.2 "WPF + native 하이브리드 이벤트 가로채기" 교훈

### (선택) M-15: 입력 UX v2

| 순서 | Feature | 규모 | 설명 |
|:----:|---------|:----:|------|
| 1 | IME 다국어 검증 | 중 | 일본어 (Microsoft IME) + 중국어 (微软拼音/搜狗) 조합 미리보기 회귀 검증 |
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
★ M-14 렌더 스레드 안전성    ← Phase 6-B 에서 발견, 독립 수행
  ↓
(선택) M-15 입력 UX v2
```

---

## 진행 규칙

1. **비전 정렬 우선** — 모든 새 작업은 3대 축 중 어디에 기여하는지 명시
2. **마일스톤 단위 PDCA** — PM(선택) → Plan → Design → Do → Check → Archive
3. **사후 정정 보존** — Plan/Design 가정이 반증되면 § 사후 정정 형태로 시간순 보존 (M-13 §10/§13 패턴)
4. **기술 부채는 마일스톤 사이에 삽입** — Pre-M11 Cleanup 패턴 유지
5. **주요 feature 는 리서치 선행** — 참조 코드베이스 조사 후 설계
6. **외부 패치는 fork 로 pin** — ghostty 처럼 upstream 손대는 변경은 private fork + 빌드 재현성 확보 (M-13 사례)

---

## 관련 문서

- **원본 비전**: `onboarding.md` (프로젝트 루트) — v0.5 (2026-04-15)
- **Obsidian 로드맵** (상세): `C:\Users\Solit\obsidian\note\Projects\GhostWin\Milestones\roadmap.md`
- **Obsidian 프로젝트 진입점**: `C:\Users\Solit\obsidian\note\Projects\GhostWin\_index.md`
- **cmux 리서치**: `docs/00-research/cmux-ai-agent-ux-research.md` (2026-03-28)
- **ghostty 패치 분석**: `docs/00-research/ghostty-upstream-sync-analysis.md`
- **PDCA archive 인덱스**: `docs/archive/2026-04/_INDEX.md` (32 사이클) + `docs/archive/legacy/_INDEX.md`

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | Initial roadmap |
| 0.2 | 2026-04-11 | M-10 완료 반영. M-10.5 복사/붙여넣기 추가. M-13 입력 UX 분리 |
| 0.3 | 2026-04-15 | 비전 동기화 — Phase 6 AI 에이전트 멀티플렉서 복원. 확정 실행 순서 (M-11 → Phase 6-A → 6-B → M-12/6-C → M-13). M-10.5 ~ Pre-M11 완료 반영 |
| **0.4** | **2026-04-20** | **M-11 ~ M-13 + Phase 6-A/B/C + session-restore 모두 완료 반영. ★ 위치를 M-14 로 이동. 완료 마일스톤 표 확장 (Match Rate 컬럼 추가). 남은 상세는 M-14 만 유지 (완료된 마일스톤은 archive 인덱스 참조). Pre-M11 plan 경로를 archive 로 갱신. ghostty fork 정책 항목 추가 (진행 규칙 #6).** |
