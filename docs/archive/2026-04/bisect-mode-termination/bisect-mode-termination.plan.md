# BISECT Mode Termination — Planning Document

> **Summary**: pane-split v0.5의 legacy fallback 경로(render_loop else branch + renderer 내부 swapchain + surfaceId=0 placeholder)를 제거하여 design↔runtime divergence 해소. Surface 경로 단일화.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 — 부채 청산 P0-2
> **Author**: 노수장
> **Date**: 2026-04-07
> **Status**: Draft
> **Previous**: `docs/03-analysis/pane-split-workspace-completeness-v0.5.md` §1 C1 (BISECT 미해결 + 미문서화, 5 agents 동의)
> **Previous completed**: `docs/04-report/core-tests-bootstrap.report.md` — PaneNode 회귀 방어망 확보 (9/9 PASS)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | pane-split v0.5 10-agent 평가 §1 C1: "BISECT mode 미해결 + 미문서화" (5 agents 동의). design 문서의 "Surface 전용 경로" 주장 ↔ runtime의 이중 경로 공존 (`render_loop:184-216`의 if/else). `release_swapchain()` 주석 처리 + `Initialize(sessionId, 0)` placeholder + legacy else branch 3개가 서로 얽혀 **design ↔ runtime divergence**가 구조적으로 존재. 이 상태에서는 P0-3 (종료 경로 단일화), P0-4 (PropertyChanged detach) 등 후속 작업 시 어떤 경로를 수정해야 할지 불명확. |
| **Solution** | 실측 기반 scope 축소: **Surface 경로는 이미 정상 작동 중** (`PaneLayoutService.OnHostReady:184`이 `SurfaceCreate` 호출, `render_loop:184-190`이 `active_surfaces()` 순회). BISECT 실체는 warm-up 폴백 + safety net. 4가지 변경으로 종료: (1) `render_loop` legacy else branch 삭제, (2) `release_swapchain()` 활성화, (3) `Initialize(sessionId, initialSurfaceId)` 시그니처에서 `initialSurfaceId` 파라미터 제거, (4) design 문서 §1.4 "BISECT 상태 종료" 섹션 + §4.4 함수명 정합화 (v0.5.1). |
| **Function/UX Effect** | 사용자 가시 기능 변경 0 (이미 Surface 경로로 동작 중). 개발자 관점: design ↔ runtime 정합성 회복 → 후속 부채 청산 작업의 수정 지점이 명확해짐. 리스크 영역 축소: legacy else branch 제거로 dual-path maintenance 비용 0. warm-up 구간(workspace 생성 ~ HostReady fire 사이)은 빈 화면 유지 (렌더 skip) — 체감 시간 매우 짧음 (~수십 ms 예상). |
| **Core Value** | "BISECT"가 실제로 **아키텍처적 미완성이 아니라 단순 폴백**이었음을 코드 리딩으로 확인. 10-agent 평가의 Critical 이슈가 실측으로 "Moderate refactoring" 수준임이 드러났고, 이는 **평가 보고서의 한계 조사**로 작용 (§7 "정직한 불확실성"이 중요했던 이유). P0-1 (테스트 인프라) 다음 순서로 P0-2를 빠르게 처리하여 Phase 5-E.5 부채 청산 진도 확보. |

---

## 1. Overview

### 1.1 Purpose

**BISECT 종료**는 pane-split v0.5의 runtime 상에 남아 있는 4가지 legacy 흔적을 제거하여, design 문서가 주장하는 "Surface 전용 경로"를 **실제로 유일 경로**로 만드는 작업.

**BISECT의 진짜 의미 (실측 기반)**:

| # | BISECT 흔적 | 위치 | 실체 |
|---|---|---|---|
| 1 | `render_loop`의 legacy else 분기 | `ghostwin_engine.cpp:191-216` | warm-up 기간 (workspace 생성 ~ HostReady 발사 사이) 또는 SurfaceCreate 실패 시의 폴백. **정상 동작 중에는 실행되지 않음** (active_surfaces가 비어있지 않으므로) |
| 2 | `release_swapchain()` 주석 처리 | `ghostwin_engine.cpp:321-322` | renderer 내부 swapchain을 살려두어 legacy 경로가 사용할 수 있게 함. **legacy 경로가 유일한 사용처** |
| 3 | `Initialize(sessionId, 0)` surfaceId=0 | `WorkspaceService.cs:49` + `IPaneLayoutService.Initialize` | placeholder. 실제 surfaceId는 `OnHostReady`가 `SurfaceCreate` 호출 후 `PaneLeafState` 업데이트로 설정됨. **파라미터 자체가 dead** |
| 4 | design 문서 §1.4 부재 + §4.4 함수명 drift | `pane-split.design.md` | design-validator Critical 7건 |

**실측 증거**:
- `PaneLayoutService.cs:184` — `var surfaceId = _engine.SurfaceCreate(hwnd, leaf.SessionId.Value, widthPx, heightPx);`
- `PaneLayoutService.cs:179` — `if (state.SurfaceId != 0) return; // Already created` (BISECT 초기값 0은 "미생성"의 flag로만 기능)
- `TerminalHostControl.cs:71` — `HostReady?.Invoke(this, new(PaneId, _childHwnd, pw, ph));`
- `PaneContainerControl.cs:311-313` — HostReady → ActiveLayout.OnHostReady 포워딩
- `ghostwin_engine.cpp:164-170` — `render_surface`가 `bind_surface/upload_and_draw/unbind_surface`로 per-surface 렌더
- `ghostwin_engine.cpp:184-190` — `render_loop`이 `active_surfaces()` 순회

결론: **Surface 경로는 이미 완전히 작동하고 있다**. BISECT는 "legacy 안전망을 아직 해체하지 못한 상태"를 지칭.

### 1.2 Background

**10-agent v0.5 평가 §1 C1** (5 agents 동의 — wpf-architect, design-validator, code-analyzer, gap-detector, cto-lead):

> "design이 주장하는 per-pane 다중 SwapChain이 실제로는 동작 안 함"  
> "분할 시 화면이 active pane만 렌더, 비활성 pane은 빈 화면 가능성"

이 평가는 **코드를 정확히 읽지 못한 부분이 있음**. 실제로는 per-pane SwapChain이 동작하고 있으며 (SurfaceManager가 HWND당 별도 SwapChain 생성), 비활성 pane도 같은 `active_surfaces()` 집합에 포함되어 매 프레임 렌더된다. 평가의 "확실하지 않음"이 바로 이 영역을 가리켰던 것.

그럼에도 10-agent 평가는 **올바른 방향**을 지적했다: **design 문서와 runtime 코드가 일치해야 한다**는 원칙. 문서에 BISECT라는 개념이 존재하지 않는데 코드에는 주석으로 존재 → 이 불일치는 종료되어야 한다.

### 1.3 Related Documents

- `docs/03-analysis/pane-split-workspace-completeness-v0.5.md` §1 C1 (BISECT Critical)
- `docs/02-design/features/pane-split.design.md` v0.5 — v0.5.1로 갱신 대상
- `docs/04-report/core-tests-bootstrap.report.md` — PaneNode 회귀 방어망 (전제)
- `src/engine-api/ghostwin_engine.cpp` (render_loop, gw_render_init, gw_surface_create)
- `src/GhostWin.Services/WorkspaceService.cs` (Initialize 호출)
- `src/GhostWin.Services/PaneLayoutService.cs` (Initialize 구현, OnHostReady 실구현)
- `src/GhostWin.Core/Interfaces/IPaneLayoutService.cs` (시그니처)

---

## 2. Scope

### 2.1 In Scope

**C++ 엔진 변경** (`src/engine-api/ghostwin_engine.cpp`)
- [ ] `render_loop` 함수에서 legacy else branch 전체 삭제 (line 191-216, ~25 lines). `active_surfaces()`가 비어있을 때는 `Sleep(1); continue;`로 단순화
- [ ] `gw_render_init`의 `release_swapchain()` 활성화 (line 321-322 주석 해제). BISECT 주석 삭제
- [ ] `gw_render_resize`의 `renderer->resize_swapchain()` 호출 — **Design에서 재검토 필요**. main window 렌더러의 내부 swapchain이 다른 용도로 사용되는지 확인 후 결정
- [ ] BISECT 마커 주석 (`// BISECT: ...`) 전부 제거

**C# Services 변경**
- [ ] `src/GhostWin.Core/Interfaces/IPaneLayoutService.cs:13` — `Initialize(uint initialSessionId, uint initialSurfaceId)` → `Initialize(uint initialSessionId)`로 시그니처 단순화
- [ ] `src/GhostWin.Services/PaneLayoutService.cs:37-43` — 구현 업데이트, `PaneLeafState`의 `SurfaceId` 초기값 0 유지 (OnHostReady가 설정)
- [ ] `src/GhostWin.Services/WorkspaceService.cs:49` — `paneLayout.Initialize(sessionId, 0)` → `paneLayout.Initialize(sessionId)`, BISECT 주석 제거

**Design 문서 갱신** (`docs/02-design/features/pane-split.design.md` → v0.5.1)
- [ ] §1.4 신설: "BISECT 상태 종료" — 역사적 맥락 + 종료 일자 + 관련 커밋
- [ ] §4.4 함수명 drift 수정: `render_to_target` → `bind_surface`/`upload_and_draw`/`unbind_surface` (실제 구현명 반영)
- [ ] §8 NFR — v0.5.1 신규 항목 추가 여부 검토 (warm-up latency 관련)
- [ ] §11 Test Plan — PaneNode T-1~T-5 실제 구현됨을 반영 (core-tests-bootstrap report 인용)
- [ ] §12 Migration Checklist — v0.5 → v0.5.1 최신화
- [ ] Version History v0.5.1 entry 추가

**검증**
- [ ] `scripts/test_ghostwin.ps1` 실행 — PaneNode 9/9 PASS 유지 (PaneNode 변경 없으므로 회귀 가능성 0에 가까움)
- [ ] `scripts/build_ghostwin.ps1 -Config Release` 전체 빌드 성공
- [ ] 수동 실행: 단일 pane 렌더링, Alt+V/H split, 마우스 focus, Ctrl+Shift+W close 정상 동작 확인
- [ ] warm-up 기간 crash 없음 (workspace 생성 직후 몇 프레임 동안 blank screen은 허용)

**CLAUDE.md 갱신**
- [ ] Phase 5-E.5 섹션에서 P0-2 상태 `[ ]` → `[x]` 표시
- [ ] Key references 테이블에 `bisect-mode-termination` Plan/Design/Report 링크 추가

### 2.2 Out of Scope

- **DX11Renderer 내부 리팩토링**: `release_swapchain`이 resource를 정리하는 방식 자체는 변경하지 않음. 기존 메서드를 호출만 할 뿐
- **`gw_render_resize`의 main window 역할**: 현재 WPF 메인 윈도우 리사이즈 시 여전히 호출되는 것으로 보이는데, 이것이 renderer 내부 swapchain에 의존하는지는 Design 단계에서 확인. **변경 범위 최소화**: 만약 제거 가능하면 제거, 아니면 유지하되 BISECT 아님
- **P0-3 종료 경로 단일화**: 별도 feature. 본 feature의 결과가 그 작업의 입력이 됨
- **P0-4 PropertyChanged detach**: 별도 feature
- **warm-up 기간 단축**: "blank screen 기간 < Xms" 같은 성능 목표는 NFR로 설정하지 않음 (체감상 충분히 짧고, 이를 줄이는 것은 별도 최적화 feature)
- **C++ 테스트 추가**: `tests/surface_manager_race_test.cpp` 등은 qa-strategist 권고에 있지만 본 feature 외
- **Fallback 재도입 가드**: 만약 legacy 경로가 미래에 필요해지면 그때 다시 추가. 지금은 YAGNI

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | `render_loop`에 legacy else branch 제거. `active_surfaces()` 비어있으면 렌더 skip (`Sleep(1); continue;`) | High | Pending |
| FR-02 | `gw_render_init`에서 SurfaceManager 초기화 후 `release_swapchain()` 호출 — renderer 내부 swapchain 해제 | High | Pending |
| FR-03 | `IPaneLayoutService.Initialize` 시그니처에서 `initialSurfaceId` 파라미터 제거. 호출처 1곳 (`WorkspaceService.cs:49`) 갱신 | High | Pending |
| FR-04 | 모든 BISECT 주석/마커 제거 (grep `BISECT` 결과 0건) | Medium | Pending |
| FR-05 | `pane-split.design.md` v0.5.1 개정 — §1.4, §4.4, §8 (검토), §11 (검토), §12, Version History | High | Pending |
| FR-06 | `gw_render_resize`의 `resize_swapchain` 호출 유지 여부 결정 (Design 단계) | Medium | Pending |
| FR-07 | PaneNode 9/9 테스트 PASS 유지 (core-tests-bootstrap 스위트) | High | Pending |
| FR-08 | 수동 테스트 시나리오 8건 PASS (단일 pane, split V, split H, focus, close, workspace create, workspace switch, window resize) | High | Pending |
| FR-09 | CLAUDE.md Phase 5-E.5 P0-2 `[x]` 표시, reference 링크 추가 | Low | Pending |

### 3.2 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| warm-up 허용성 | workspace 생성 직후 blank screen 체감 < 1초 (경험적 기준, 정량 벤치마크 불요) | 수동 확인 |
| 빌드 | `scripts/build_ghostwin.ps1 -Config Release` 성공, 경고 증가 0 | 빌드 출력 |
| 단위 테스트 | `scripts/test_ghostwin.ps1` 9/9 PASS | CI 없음, 로컬 실행 |
| 커밋 입도 | BISECT 종료 단일 커밋 권장. 실패 시 C++/C#/docs 3커밋 최대 | git log |
| BISECT 흔적 | `grep -rn "BISECT" src/ docs/` 결과 0건 (design history 예외 가능) | grep |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] `grep -n BISECT src/engine-api/ghostwin_engine.cpp src/GhostWin.Services/WorkspaceService.cs`가 0건
- [ ] `render_loop`의 else branch 제거됨 (line 범위 축소 확인)
- [ ] `release_swapchain()` 호출 활성화됨 (주석 해제)
- [ ] `IPaneLayoutService.Initialize` 시그니처 단순화
- [ ] `scripts/test_ghostwin.ps1` 9/9 PASS (5회 연속 결정론)
- [ ] `scripts/build_ghostwin.ps1 -Config Release` exit 0, 경고 증가 0
- [ ] 수동 QA 8건 PASS
- [ ] `pane-split.design.md` v0.5.1 발행 (§1.4 신설, §4.4 함수명 수정)
- [ ] CLAUDE.md P0-2 완료 표시

### 4.2 Quality Criteria

- [ ] gap-detector Match Rate ≥ 90%
- [ ] `PaneNode.cs` 미변경 (git diff empty)
- [ ] `scripts/build_*.ps1`, `scripts/test_ghostwin.ps1` 미변경
- [ ] 커밋 메시지가 `.claude/rules/commit.md` 준수 (영문, 50자 이내, AI 언급 없음)

---

## 5. Risks and Mitigation

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| `gw_render_resize`의 `resize_swapchain` 호출이 main window 렌더링에 필수로 남아있는 경우 — `release_swapchain` 활성화 후 NPE/AccessViolation | **High** | Medium | Design 단계에서 DX11Renderer의 `release_swapchain`/`resize_swapchain` 구현 전수 조사 (code-analyzer). 필요 시 `resize_swapchain`만 no-op로 만들거나 메서드 분리 |
| warm-up 기간 blank screen이 기존보다 눈에 띄게 길어짐 (legacy 경로가 있을 때는 최소 뭔가 표시되었을 가능성) | Medium | Low | 현재도 legacy 경로가 session이 없으면 `Sleep(1); continue;`로 떨어지고 있어 체감 차이 없을 것 (확실하지 않음). 수동 QA에서 확인 |
| `release_swapchain()` 호출이 내부 back buffer/RTV 참조를 해제하여 이후 `bind_surface`에 영향 | Medium | Low | DX11Renderer 내부의 swapchain과 per-surface swapchain은 독립된 객체. Design 단계에서 code-analyzer가 확인 |
| `Initialize` 시그니처 변경이 `IPaneLayoutService` mock/테스트 구현에 영향 | Low | Low | 현재 테스트 프로젝트는 PaneNode.Tests만 존재 — PaneLayoutService 직접 테스트 없음. 영향 없음 |
| design 문서 §4.4 수정 시 pane-split.design.md v0.5에 이미 작성된 다른 섹션과 일관성 깨짐 | Low | Medium | design-validator council agent가 v0.5.1 갱신 시 전수 리뷰 |
| 수동 QA 8건 중 1건 실패 — 회귀 발견 | Medium | Low | `core-tests-bootstrap` PaneNode 스위트가 1차 방어선. PaneNode 문제면 여기서 잡힘. 로직 회귀는 수동 QA가 잡음. 실패 시 원인 확정 후 수정 |
| legacy else branch 제거 후 `callbacks.on_render_done`이 호출 안 되는 경로 등장 | Medium | Low | 현재 코드 line 221: `on_render_done`은 if/else 밖에 있음 — 어느 경로든 호출됨. 제거 후에도 `continue;` 경로로 갈 때는 skip되는 것이 정확. Design에서 재확인 |
| **확실하지 않음**: 본 feature가 10-agent 평가의 "Critical"이었으나 실측은 "Moderate" — 평가의 다른 Critical 항목도 같은 패턴일 가능성 | N/A | N/A | 별도 feature의 Plan 단계에서 동일한 실측 검증 수행 (이미 core-tests-bootstrap에서 FluentAssertions 라이선스 이슈가 같은 패턴으로 발견됨) |

---

## 6. Architecture Considerations

### 6.1 현재 상태 (실측 기반 데이터 플로우)

```
User clicks "New Workspace"
    │
    ▼
WorkspaceService.CreateWorkspace()
    │
    ├─ _sessions.CreateSession() → sessionId
    ├─ new PaneLayoutService(...)
    └─ paneLayout.Initialize(sessionId, 0)  ← BISECT placeholder
         │
         └─ PaneLeafState { SurfaceId = 0 } 등록

⏬ (WPF visual tree 빌드, 프레임 진행)

TerminalHostControl.BuildWindowCore() → _childHwnd 생성
TerminalHostControl.OnRendered(측정 완료 시점)
    │
    └─ HostReady.Invoke(paneId, childHwnd, w, h)
         │
         ▼
PaneContainerControl.OnHostReady()
    │
    └─ ActiveLayout.OnHostReady(paneId, hwnd, w, h)
         │
         ▼
PaneLayoutService.OnHostReady()
    │
    ├─ state.SurfaceId != 0 가드 (이미 생성된 경우)
    ├─ _engine.SurfaceCreate(hwnd, sessionId, w, h)  ← 실제 surface 생성
    │    │
    │    ▼
    │  gw_surface_create (ghostwin_engine.cpp:541)
    │    │
    │    ├─ surface_mgr->create(hwnd, sessionId, w, h) → GwSurfaceId
    │    └─ session_mgr->resize_session(sessionId, cols, rows)
    │
    └─ PaneLeafState.SurfaceId = 실제 surfaceId

⏬ (render thread)

render_loop (ghostwin_engine.cpp:173)
    │
    ├─ active = surface_mgr->active_surfaces()
    ├─ if (!active.empty()):
    │      for (surf : active) render_surface(surf, builder)  ← Surface path (정상)
    └─ else:
           [BISECT: legacy fallback branch]                    ← 제거 대상
```

### 6.2 목표 상태

```
render_loop
    │
    ├─ active = surface_mgr->active_surfaces()
    ├─ if (active.empty()): Sleep(1); continue;               ← warm-up 허용
    └─ for (surf : active): render_surface(surf, builder)     ← 유일 경로

gw_render_init
    │
    ├─ SurfaceManager 초기화
    └─ renderer->release_swapchain()                          ← 활성화

IPaneLayoutService.Initialize(uint initialSessionId)          ← 시그니처 단순화
    │
    └─ PaneLeafState { SurfaceId = 0 }                        ← 내부 placeholder
                                                                 (OnHostReady가 설정)
```

### 6.3 Key Architectural Decisions

| Decision | Options | Selected | Rationale |
|----------|---------|----------|-----------|
| legacy else branch 처리 | (a) 삭제 / (b) debug build에서만 활성 / (c) no-op | **(a) 삭제** | YAGNI. fallback 재도입 필요 시 git history에서 복원 가능 |
| `initialSurfaceId` 파라미터 | (a) 유지 / (b) 제거 / (c) default value | **(b) 제거** | dead parameter. API 단순화로 호출자 혼란 방지 |
| `gw_render_resize`의 `resize_swapchain` | (a) 유지 / (b) no-op / (c) 제거 | **Design에서 결정** | DX11Renderer 내부 조사 필요 |
| design 문서 버전 bump | v0.5.1 (patch) / v0.6 (minor) | **v0.5.1** | BISECT 종료는 구조 변경 아님 + 문서 정합성 회복이 주 목적 |

### 6.4 Clean Architecture 정합

변경 없음. 의존성 방향 그대로 유지.

---

## 7. Convention Prerequisites

### 7.1 Existing Conventions

- [x] `.claude/rules/behavior.md` — 우회 금지, 근거 기반 문제 해결. 본 feature가 특히 엄격 준수 대상 (10-agent "Critical" 평가를 근거 기반 실측으로 재평가한 것이 바로 이 원칙)
- [x] `.claude/rules/commit.md` — 영문 커밋, AI 언급 없음
- [x] `.claude/rules/build-environment.md` — `scripts/build_ghostwin.ps1` 사용
- [x] `core-tests-bootstrap`의 D2 (FluentAssertions 7.0.0) 라이선스 룰

### 7.2 Conventions to Define/Verify

- [ ] design 문서 버전 bump 룰: BISECT 종료 같은 정합성 복구 작업은 **patch (v0.5.1)**, 아키텍처 변경은 **minor (v0.6)**. 이 구분을 본 feature로 정립

### 7.3 Environment Variables Needed

없음.

---

## 8. Next Steps

1. [ ] `/pdca team design bisect-mode-termination` — Design 단계, Expanded Slim 4-agent council 호출
   - wpf-architect: HostReady 흐름 + HwndHost 라이프사이클 정합성
   - dotnet-expert: `IPaneLayoutService.Initialize` 시그니처 변경 영향 전수 분석 + WorkspaceService 갱신
   - code-analyzer: `ghostwin_engine.cpp` + `DX11Renderer` release_swapchain 전수 조사 + legacy branch 참조 검색
   - design-validator: pane-split.design.md v0.5.1 갱신 사항 검증
2. [ ] Design 문서 작성 후 Do 진입
3. [ ] Check: gap-detector + `test_ghostwin.ps1` (PaneNode 9/9) + 수동 QA 8건
4. [ ] Report + CLAUDE.md 갱신
5. [ ] 다음: P0-3 종료 경로 단일화

---

## 9. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-07 | Initial draft. 실측 기반 scope 축소 (Critical → Moderate 재평가). core-tests-bootstrap 완료 후 Phase 5-E.5 P0-2 착수 | 노수장 |
