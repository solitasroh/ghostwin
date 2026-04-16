# GhostWin Roadmap (2026-04-15 기준)

> **이 프로젝트의 존재 이유**: Windows 용 **AI 에이전트 멀티플렉서**
> (macOS cmux + ghostty 성능 철학을 윈도우 네이티브로).
>
> 자세한 비전: `onboarding.md` (프로젝트 루트) + Obsidian `_index.md` 3대 비전 표.

---

## 🎯 3대 비전 (모든 의사결정의 기준선)

| # | 축 | 진행 |
|:-:|----|:----:|
| 1 | **macOS cmux 기능 탑재** (수직 탭, pane 분할, 알림 링/패널, 워크스페이스 등) | 🟡 부분 |
| 2 | **AI 에이전트 멀티플렉서 기반** (OSC hooks, Named pipe 훅, 에이전트 배지, Toast) | ❌ **미시작 (Phase 6)** |
| 3 | **타 터미널 대비 성능 우수** (ghostty libvt + DX11 인스턴싱 + ClearType + CJK) | ✅ 기반 완성 |

---

## 현재 위치 (2026-04-15)

```
Phase 1~4 ✅ → M-1~M-10.5 ✅ → Codebase Review ✅ → Pre-M11 Cleanup ✅ (15/16)
                                                                              ↓
                                                                       ★ 여기 ★
                                                                              ↓
                                                    M-11 → 🎯 Phase 6-A → 🎯 Phase 6-B → ...
```

**앱 상태**: DX11 렌더링 + ConPTY + WPF Shell + 다중 Workspace/Pane + 마우스 + 복붙 + DPI + 자물쇠 단일화 + 종료 hang 해결.

**부족한 것 (우선순위 순)**:
1. 🎯 **AI 에이전트 멀티플렉서 기능** (Phase 6 — 존재 이유, 한 줄도 미진행)
2. 세션 복원 (M-11)
3. 설정 UI (M-12), 한글 입력 미리보기 (M-13)

---

## 완료된 마일스톤

| 마일스톤 | 내용 | 완료일 |
|----------|------|:------:|
| **Pre-M11 Cleanup** | Follow-up 9 + Tech Debt 7 = 16건 청산 (15/16, 재설계 sub-cycle 3건 완결) | 2026-04-15 |
| **Codebase Review** | 전수 재검토 + graceful shutdown + VS 솔루션 통합 | 2026-04-14 |
| **M-10.5 Clipboard** | Ctrl+C/V + Bracketed Paste | 2026-04-13 |
| **M-10 Mouse** | 클릭/스크롤/선택 + CJK + DX11 하이라이트 | 2026-04-11 |
| **M-8~M-9** | Pane 분할 + Workspace 관리 | 2026-04-08 |
| **M-1~M-7** | WinUI3 → WPF 이행 | 2026-04-06 |
| **Phase 5-A~E** | session/tab/titlebar/settings/pane-split | ~2026-04-08 |

---

## 🎯 확정 실행 순서 (2026-04-16)

```
1. M-11 Session Restore            ✅ 완료 (96%)
2. M-11.5 E2E 자동화 체계화        ✅ 완료 (100%)
3. 🎯 Phase 6-A OSC + 알림 링      ✅ 완료 (93%) — 핵심 가설 실증
4. 🎯 Phase 6-B 알림 인프라        ✅ 완료 (97%) — 운영 인프라 완성
5a. M-12 Settings UI               (기본기) ← 즉시 진입 가능
5b. 🎯 Phase 6-C 외부 통합         (AI 에이전트 ② — 5a 와 병행 가능)
6. M-13 Input UX                   (기본기)
7. M-14 렌더 스레드 안전성          (독립 — Phase 6-B에서 발견한 경쟁 조건 근본 해결)
```

**근거**:
- Phase 6-A/B 완료 → 비전 ② AI 에이전트 멀티플렉서 핵심 기능 실증됨
- M-12 는 3대 비전 축에 직접 기여 없으나, Phase 6-C 와 병행하여 진행 가능
- M-14 는 창 리사이즈 시 렌더 스레드 `_p` RenderFrame 경쟁 조건 (Phase 6-B 테스트 중 발견). 방어 가드 적용 완료, 근본적 double-buffer 해결은 독립 수행

---

## 남은 마일스톤 상세

### M-11: 세션 복원

> 목표: 앱 재시작 시 창 구성/쉘 CWD 복원. 비전 ① cmux 기능 + Phase 6 알림 UX 완성도 보강.

| 순서 | Feature | 의존성 | 규모 | 설명 |
|:----:|---------|--------|:----:|------|
| 1 | **session-restore** | 없음 | 중 | CWD + pane 레이아웃 JSON 직렬화 → 시작 시 복원 |
| 2 | Workspace title mirror | #1 | 소 | Active pane 의 session title/cwd 가 sidebar 에 반영 |

### 🎯 Phase 6-A: OSC Hook + 알림 링 (핵심 가설 검증)

> 목표: **"Claude Code 가 OSC 9 보내면 탭에 알림 링이 뜨는가?"** 가설 검증.
> 비전 ② 의 본질. 이것이 동작해야 나머지 Phase 6 의 의미가 있음.

| 순서 | Feature | 규모 | 설명 |
|:----:|---------|:----:|------|
| 1 | **FR-01 OSC hooks 파싱** | 중 | OSC 9/99/777 + 커스텀 시퀀스. VT 파서에서 이벤트 발행 |
| 2 | **FR-02 에이전트 알림 링** | 중 | 세션 입력 대기 시 탭에 컬러 링/배지 표시 |

**검증 기준**: 실제 Claude Code 세션에서 입력 대기 시 탭에 알림이 뜨는가?

사전 리서치: `docs/00-research/cmux-ai-agent-ux-research.md` (2026-03-28)

### 🎯 Phase 6-B: 알림 인프라

> 목표: Phase 6-A 에서 띄운 알림을 체계적으로 관리.

| 순서 | Feature | 규모 | 설명 |
|:----:|---------|:----:|------|
| 1 | **FR-03 알림 패널** | 중 | 모든 대기 중 에이전트 목록. 클릭으로 해당 탭 점프 |
| 2 | **FR-04 에이전트 상태 배지** | 소 | 탭별 아이콘: 대기 / 실행중 / 오류 / 완료 |
| 3 | **FR-06 Win32 Toast** | 소 | 창 비활성 시 Windows Toast 로 알림 |

### M-12: 사용자 설정 UI

> 목표: JSON 수동 편집 없이 설정 변경. **3대 비전 축에 직접 기여 없음 → Phase 6-B 뒤로 조정**.

| 순서 | Feature | 의존성 | 규모 | 설명 |
|:----:|---------|--------|:----:|------|
| 1 | **Settings UI** | 없음 | 중 | XAML 설정 페이지 (테마, 폰트, 키바인딩 등) |
| 2 | Command Palette | #1 | 중 | Airspace 우회 Popup Window, 검색 기반 명령 실행 |

### 🎯 Phase 6-C: 외부 통합

> 목표: Claude Code / git 과 직접 연결. M-12 와 병행 가능.

| 순서 | Feature | 규모 | 설명 |
|:----:|---------|:----:|------|
| 1 | **FR-05 Named pipe 훅 서버** | 대 | Claude Code Stop/Notification 이벤트 수신 |
| 2 | **FR-07 git branch/PR 표시** | 중 | 사이드바에 각 세션의 git 상태 |

### M-13: 입력 UX 완성

| 순서 | Feature | 의존성 | 규모 | 설명 |
|:----:|---------|--------|:----:|------|
| 1 | **조합 미리보기** | 없음 | 소 | TSF preedit → 렌더러 오버레이 (한글 입력 UX) |
| 2 | **마우스 커서 모양** | 없음 | 소 | ghostty cursor_shape 콜백 → WPF Cursor 변경 |

### M-14: 렌더 스레드 안전성 (Render Thread Safety)

> 목표: 렌더 스레드 ↔ UI 스레드 간 `_p` RenderFrame 동시 접근 경쟁 조건 근본 해결.
> 비전 축 ③ 성능 우수 — 크래시 없는 안정성이 성능의 전제.

| 순서 | Feature | 의존성 | 규모 | 설명 |
|:----:|---------|--------|:----:|------|
| 1 | **RenderFrame double-buffer** | 없음 | 중 | `_p` 단일 인스턴스 → front/back 이중 버퍼. `start_paint`에서 back에 기록, swap 후 `builder.build`는 front만 읽음. 리사이즈 시 back만 reshape → swap 이후 적용 |
| 2 | **resize 원자성 보장** | #1 | 소 | `resize_session` → `_p.reshape()`이 렌더 스레드 관측 시점과 분리됨을 검증. TSAN 또는 수동 스트레스 테스트 |

**현재 상태**: Phase 6-B에서 방어적 가드 추가 (빈 span 반환, min 크기 복사). 크래시는 방지하나 리사이즈 시 1프레임 부분 렌더 가능.

**발견 경위**: 2026-04-16 Phase 6-B 테스트 중 창 리사이즈 시 `span subscript out of range` Debug Assertion 발생. `render_state.h:row()` → `quad_builder.cpp:build()` 경로.

**근본 원인**: `builder.build(frame)` 가 `_p`를 vt_mutex **락 밖**에서 읽는 동안, UI 스레드가 `resize_session()` → `_p.reshape()`으로 `cols`를 변경. span 크기 불일치 → 범위 초과.

---

## 의존성 다이어그램

```
M-11 Session Restore
  ↓
🎯 Phase 6-A (OSC hook + 알림 링, 핵심 가설 검증)
  ↓
🎯 Phase 6-B (알림 패널 + 배지 + Toast)
  ↓
M-12 Settings UI  ───┬─── 병행 가능
  ↓                   │
M-13 Input UX    🎯 Phase 6-C (Named pipe + git)
  ↓
M-14 렌더 스레드 안전성    ← Phase 6-B에서 발견, 독립 수행 가능
```

---

## 진행 규칙

1. **비전 정렬 우선** — 모든 새 작업은 3대 축 중 어디에 기여하는지 명시
2. **마일스톤 단위 PDCA** — PM(선택) → Plan → Design → Do → Check → Archive
3. **Phase 6 는 최우선** — 터미널 기본기보다 "AI 에이전트 멀티플렉서" 본질이 우선
4. **기술 부채는 마일스톤 사이에 삽입** — Pre-M11 Cleanup 패턴 유지
5. **주요 feature 는 리서치 선행** — 참조 코드베이스 조사 후 설계

---

## 관련 문서

- **원본 비전**: `onboarding.md` (프로젝트 루트) — v0.5 (2026-04-15)
- **Obsidian 로드맵** (상세): `C:\Users\Solit\obsidian\note\Projects\GhostWin\Milestones\roadmap.md`
- **Obsidian 프로젝트 진입점**: `C:\Users\Solit\obsidian\note\Projects\GhostWin\_index.md`
- **cmux 리서치**: `docs/00-research/cmux-ai-agent-ux-research.md` (2026-03-28)
- **Pre-M11 상세**: `docs/01-plan/features/pre-m11-backlog-cleanup.plan.md`

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | Initial roadmap |
| 0.2 | 2026-04-11 | M-10 완료 반영. M-10.5 복사/붙여넣기 추가. M-13 입력 UX 분리 |
| **0.3** | **2026-04-15** | **🎯 비전 동기화 — Phase 6 AI 에이전트 멀티플렉서 복원. 확정 실행 순서 (M-11 → Phase 6-A → 6-B → M-12/6-C → M-13). M-10.5 ~ Pre-M11 완료 반영. 기술 부채는 Pre-M11 Cleanup 으로 대부분 청산.** |
