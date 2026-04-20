# vt-mutex-redesign — Gap Analysis

> **Feature**: vt-mutex-redesign
> **Phase**: Check (PDCA)
> **Date**: 2026-04-15
> **Analyzer**: rkit:gap-detector (opus)
> **Design**: [vt-mutex-redesign.design.md](../02-design/features/vt-mutex-redesign.design.md)
> **Plan**: [vt-mutex-redesign.plan.md](../01-plan/features/vt-mutex-redesign.plan.md)

---

## Executive Summary

| 항목 | 값 |
|------|-----|
| 구현 범위 | Design §4 (7 개 항목) + §9.1 스코프 확장 (2 개) + 검증 (4 개) = **13 개 지표** |
| **Match Rate** | **13/13 = 100%** |
| Gap 개수 | **0** |
| 리스크 실현 | 0 (설계된 5 건 전부 미실현, 1 건은 측정 기반이라 "확실하지 않음") |
| 결론 | Design 과 구현 **완전 일치**. Report 단계 진입 권장 |

---

## 1. 요구사항 대조표

| # | Design 요구 | 실제 구현 위치 | 일치 |
|:-:|------------|----------------|:----:|
| 4.1 | `resize_pty_only` + `vt_resize_locked` public API 신설, `resize()` 는 래퍼 | `conpty_session.h` 3 메서드 선언, `.cpp:488-515` 분리 구현 + 래퍼 | ✅ |
| 4.2a | `resize_session` After 패턴 | `session_manager.cpp:374-392` | ✅ |
| 4.2b | `apply_pending_resize` After 패턴 | `session_manager.cpp:405-421` | ✅ |
| 4.3 | `Session::vt_mutex` 필드 제거 + 주석 M1 참조로 갱신 | `session.h` 필드 부재, 모든 주석 `ConPtySession::vt_mutex()` 로 갱신 | ✅ |
| 4.4 | Contract 주석 갱신 (render_state, vt_core) | `render_state.h:103,115`, `render_state.cpp:246-247`, `vt_core.h:92` 모두 "single VT lock (ADR-006)" 명시 | ✅ |
| 4.5 | `ghostwin_engine.cpp:139-141` 함정 주석 단순화 | "NOT Session::vt_mutex" 제거, "Session::vt_mutex no longer exists" 로 교체 | ✅ |
| 4.6 | `SessionManager::resize_all` dead code 제거 | `session_manager.h`/`.cpp` 에 선언/정의 0 건 | ✅ |
| 9.1a | C# `IEngineService.ResizeSession` 제거 | `IEngineService.cs` 멤버 부재 확인 | ✅ |
| 9.1b | C# `EngineService.ResizeSession` 구현 제거 | `EngineService.cs` 구현 부재 확인 | ✅ |

---

## 2. 코드 품질 검증

| 지표 | Before | After | 근거 |
|------|:------:|:-----:|------|
| `sess->vt_mutex` 활성 참조 | 3 건 | **0** | grep (src/): 주석 내 "Session::vt_mutex no longer exists" 1 건 (의도된 설계 기록) 외 무결과 |
| `Session::vt_mutex` 필드 접근 | 가능 | **불가능** | `session.h` 에서 필드 자체 제거 — 컴파일러 자동 검출 |
| `ResizeSession(` C# 참조 | 2 건 | **0** | grep `src/` 전체 무결과 |
| `resize_all(` 활성 사용 | 1 건 | **0** | `ghostwin_engine.cpp:400` 과거 경로 설명 주석 외 호출 없음 |
| 빌드 상태 | 성공 | **성공** | MSBuild `GhostWin.sln -p:Configuration=Debug -p:Platform=x64` 통과 |
| 빌드 경고 | 7 건 (C4834 5 + C4133 2) | **0** | 사용자 규칙 "경고 제로 유지" 준수 전수 해결 |
| `vt_core_test` | 10/10 | **10/10** | resize, lifecycle_cycle, render_state 등 핵심 포함 |

---

## 3. 리스크 실현 여부 (Design §8)

| 리스크 | 예측 영향 | 실현 | 근거 |
|--------|:--------:|:----:|------|
| `ResizePseudoConsole` M1 밖 호출 시 cols/rows 불일치 window | 저 | ❌ **미실현** | 구현이 PTY 실패 시 VT 업데이트 skip 으로 불변식 강화 |
| `vt_resize_locked` precondition 위반 (M1 안 잡고 호출) | 중 (크래시) | ❌ **미실현** | 호출 2 곳 모두 `std::lock_guard lock(sess->conpty->vt_mutex())` 아래 |
| `EngineService.ResizeSession` dead code 가정 오류 | 저 | ❌ **미실현** | 제거 후 컴파일 성공 + 경고 없음 |
| M2 제거 후 미발견 사용처 | 저 (컴파일 에러) | ❌ **미실현** | grep 활성 소스 0 건 |
| slow path 에서 I/O starvation | 저 (입력 지연) | ⚠️ **확실하지 않음** | 측정 없이 판단 불가. F5 수동 검증 시 체감 확인 권장 |

---

## 4. 추가 설계 개선 (Design 명세 초과)

구현 과정에서 Design 에 없던 **불변식 강화 2 건**이 추가되었다.

### A. PTY syscall 실패 시 VT/RenderState 업데이트 skip

**위치**: `session_manager.cpp:381, 412`

```cpp
// resize_session
if (!sess->conpty->resize_pty_only(cols, rows)) return;

// apply_pending_resize
if (!sess->conpty->resize_pty_only(cols, rows)) return;
```

**근거**: 기존 `ConPtySession::resize()` 래퍼가 `ResizePseudoConsole` 실패 시 VT 갱신을 중단하던 invariant 를 유지. 경고 제거 (C4834) 과정에서 `[[nodiscard]]` 반환값 의미를 정확히 처리.

### B. `apply_pending_resize` 의 `resize_pending` flag 유지

**위치**: `session_manager.cpp:410` 근처

PTY 실패 시 `resize_pending = false` 로 클리어하지 않고 유지 → 다음 `activate()` 때 재시도. 원래 Design 은 이 경우를 명시 안 했으나 구현이 명확한 재시도 패턴을 도입.

---

## 5. 문서 동기화 확인

| 문서 | 상태 |
|------|:----:|
| Plan (`docs/01-plan/features/vt-mutex-redesign.plan.md`) | ✅ 갱신 완료 (2026-04-15, 원 Placeholder 부정확 진단 교정) |
| Design (`docs/02-design/features/vt-mutex-redesign.design.md`) | ✅ 작성 완료 + §9 A 단계 재검증 결과 반영 |
| ADR-006 코드 (`docs/adr/006-vt-mutex-thread-safety.md`) | ✅ 개정 완료 (3-mutex 구조 기술 + M1 통합 방향) |
| ADR-006 Obsidian (`ADR/adr-006-vt-mutex.md`) | ✅ 동기화 완료 |
| Backlog (`Backlog/tech-debt.md` #1) | ✅ "삼중화 정리" 로 교체, 작업 규모 재평가 반영 |
| 본 Analysis | ✅ 본 문서 |

---

## 6. 관련 발견 (별도 cycle 후보)

본 cycle 스코프 밖이지만 관찰된 사항:

| 항목 | 위치 | 처리 제안 |
|------|------|-----------|
| `docs/02-design/features/bisect-mode-termination.design.md:236` 에 `resize_all` 예시 코드 잔존 | 과거 설계 문서 | 이력 보존이면 유지, 아니면 "deprecated" 주석 추가 |
| `docs/02-design/features/pane-split.design.md:39` 에 `resize_all` 비교표 잔존 | 과거 설계 문서 | 동일 |
| `docs/04-report/m1-verification-report.md:125` 의 `ResizeSession` 미구현 기록 | 본 cycle 결과와 일치하는 이력 | 그대로 유지 |
| M3 (`TerminalWindow::Impl::vt_mutex`) | `terminal_window.cpp:41` | 별도 cycle (main.cpp stand-alone PoC 활용도 확인 후) |
| `force_all_dirty()` race (시나리오 D) | `ghostwin_engine.cpp:142` | 별도 부채, M2 와 무관 |
| `gw_render_resize` no-op stub | `ghostwin_engine.cpp:397-408` | ABI 호환성 확인 후 별도 cycle |

---

## 7. 결론

**Match Rate 100%**. Design 전 항목 충족, Gap 0, 리스크 실현 없음, 빌드/테스트/경고 모두 청정.

**다음 단계**: `/pdca report vt-mutex-redesign` 진입.

### 선택적 수동 검증 (F5, 사용자 hardware)

- 창 드래그 리사이즈 + Alt+V pane 분할 → garbage column 잔상 없음 확인
- 빠른 반복 리사이즈 스트레스 → 크래시 없음 확인
- I/O starvation 체감 여부 (Design §8 리스크 5)

위 3 건이 통과하면 리스크 실현 여부 평가가 5/5 미실현으로 확정되나, 본 Analysis 는 정적 검증 기반이므로 이를 "확실하지 않음" 으로 남긴다.

---

## 8. 확실하지 않은 부분

- I/O starvation 성능 영향 — 측정 없이 판단 불가
- 수동 F5 검증 — 사용자 hardware 필요
- `bisect-mode-termination.design.md` / `pane-split.design.md` 의 `resize_all` 잔존 주석 처리 방침 — 이력 보존/정리 중 사용자 선택

---

## 참조 파일 (절대 경로)

- `src/conpty/conpty_session.h`, `src/conpty/conpty_session.cpp`
- `src/session/session.h`, `src/session/session_manager.h`, `src/session/session_manager.cpp`
- `src/renderer/render_state.h`, `src/renderer/render_state.cpp`
- `src/vt-core/vt_core.h`, `src/vt-core/vt_bridge.c`
- `src/engine-api/ghostwin_engine.cpp`
- `src/GhostWin.Core/Interfaces/IEngineService.cs`, `src/GhostWin.Interop/EngineService.cs`
