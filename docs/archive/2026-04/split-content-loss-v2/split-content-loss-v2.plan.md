# split-content-loss-v2 Planning Document

> **Summary**: Alt+V split 시 첫 session 의 cell buffer 가 사라지는 regression 을 `TerminalRenderState` 의 backing-capacity 기반 재설계로 해소. `4492b5d` hotfix 가 cover 하지 못한 Grid layout **shrink-then-grow** 연쇄에서 content 손실이 발생함을 unit test (`test_resize_shrink_then_grow_preserves_content`) 로 empirical 확정.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 — Follow-up Cycle #8 (HIGH)
> **Author**: 노수장
> **Date**: 2026-04-09
> **Status**: Draft (v0.1)
> **Previous fix (incomplete)**: `4492b5d fix(render): preserve cell buffer across resize`
> **Evidence commit**: `6141005 test(render): add split-content-loss-v2 regression evidence`
> **Related cycle**: `first-pane-render-failure` (archived, amended 2026-04-09 with `4492b5d` hotfix — 이 cycle 은 그 amend 의 후속작)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | Alt+V split 직후 좌측 (원래) pane 의 buffer 가 사라진 채로 분할됨. `4492b5d` hotfix 는 `min(old, new)` 기반 row-by-row memcpy 를 도입해 **single-resize** shrink 또는 grow 에 대해서는 content 를 보존하지만, WPF Grid layout 의 **중간 pass** 가 기존 pane 을 transient 하게 `~1x1` 로 축소한 후 final pass 가 목표 크기로 grow 하는 **shrink-then-grow 연쇄** 에서는 shrink step 이 `min(30, 1) × min(120, 1) = 1 cell` 로 truncate 하고 grow step 이 이미 잃어버린 cell 을 복구할 수 없음. Empirical: `tests/render_state_test.cpp::test_resize_shrink_then_grow_preserves_content` 가 `ShrinkGrow` (40x5) → `resize(1,1)` → `resize(20,5)` 시퀀스에서 row 0 의 모든 cell 을 lose → `(post-regrow row[0][1] lost: cp_count=0 cp[0]=0 expected 'h')` FAIL. |
| **Solution** | `RenderFrame` 을 **logical size (cols, rows_count) + backing capacity (cap_cols, cap_rows)** 의 2-tier 구조로 재설계 (Option A). `cell_buffer` 는 `max(historical cap)` 를 유지하고 `row(r)` 은 **physical stride `cap_cols`** 로 offset 을 계산하되 span 길이는 **logical `cols`** 로 반환. `resize(new_cols, new_rows)` 는 capacity 이하면 **metadata-only update** (memcpy 0), capacity 초과 시에만 reallocate + row-by-row remap. Shrink 가 데이터를 버리지 않으므로 후속 grow 가 원본 복구 가능. Consumer 인 `quad_builder.cpp` (for loop `r < rows_count, c < cols`), `terminal_window.cpp`, `ghostwin_engine.cpp` 는 `frame.cols/rows_count` 만 참조하므로 API 투명. |
| **Function/UX Effect** | 사용자 가시 동작: Alt+V split 후 좌측 pane 에 원본 shell prompt/출력이 그대로 유지됨 → **첫 session 이 사라지는 현상 해소**. Drag-resize grow (이전 최대 size 까지) 후에도 hidden content 복구. 사이드 이펙트: shrink 후 VT 가 해당 영역에 overwrite 하기 전에 grow 가 일어나면 pre-shrink content 가 재노출 — 이는 본 regression 해결을 위한 trade-off 로, Grid layout transient case 에서는 *원하는 동작* 이며 일반 drag resize case 에서도 시각적 artifact 수준. Memory overhead: cell 당 `sizeof(CellData)` (≈16 bytes) × `max(cap_cols × cap_rows)` × 2 buffer × N sessions. 100×30 기준 ≈ 96KB/session, 200×60 기준 ≈ 384KB/session. |
| **Core Value** | `first-pane-render-failure` amend 에서 발견된 "Grid layout transient intermediate pass" 가 실제로는 **state preservation 의 structural invariant 를 깨는 first-class 현상** 임을 확정. `4492b5d` 의 single-call content-preserving memcpy 는 **idempotent 하지 않은 resize sequence** 에 대응 불가. `std::vector` 의 `capacity ≠ size` 패턴을 그대로 `RenderFrame` 에 적용해 **logical view / physical storage 의 명확한 분리** 달성 → 향후 어떤 sequence 로도 content 가 안전하게 보존됨을 empirical 보장. |

---

## 1. Overview

### 1.1 Purpose

Alt+V / Alt+H split 시 **오른쪽의 새 pane** 이 아니라 **왼쪽의 기존 pane** 의 cell buffer 가 empty 로 초기화되는 regression 을 해소한다. `TerminalRenderState::resize(cols, rows)` 가 shrink-then-grow sequence 에서 content 를 잃는 근본 원인을 수정한다.

### 1.2 Background

#### 1.2.1 Regression 발견 경로

`first-pane-render-failure` cycle (2026-04-08 archived) 은 bisect R2 (HostReady race) 의 실제 최초 reproduction 을 통해 Option B 구조 fix 를 도입했다. 그 cycle 의 closeout 후 사용자 hardware smoke 중 다른 증상이 관찰됨:

> "처음 실행 시 session 렌더링 성공. alt+v 로 화면 분할 시 첫 session 버퍼 삭제되어 또 사라짐. rendering 안됨" — 2026-04-09

1차 대응으로 `4492b5d fix(render): preserve cell buffer across resize` (2026-04-09) 커밋이 `TerminalRenderState::resize` 를 `_api.allocate() + _p.allocate()` 기반 reset 에서 `min()` 기반 row-by-row memcpy 로 전환했다. 단일 shrink 또는 단일 grow 에서는 기존 content 가 보존되는 것을 unit test (`test_resize_preserves_content`, `test_resize_grow_preserves_content`) 로 확인.

그러나 hotfix 만으로는 `/first-pane-render-failure amendment (Appendix A)` 의 e2e visual 이 여전히 fail. 추가 조사 결과 **Grid layout 이 split 중에 중간 pass 로 pane 을 transient 하게 `~1x1` 로 축소** 한다는 가설이 대두됨.

#### 1.2.2 Empirical 확정

`6141005 test(render): add split-content-loss-v2 regression evidence` (2026-04-09) 커밋이 추가한 unit test 가 이 가설을 empirical 로 확정:

```cpp
// tests/render_state_test.cpp:196-246
static bool test_resize_shrink_then_grow_preserves_content() {
    // Write "ShrinkGrow" to 40x5
    state.start_paint(mtx, *vt);
    // Pre-check row 0[0] == 'S' ✓

    state.resize(1, 1);      // intermediate shrink (Grid layout)
    state.resize(20, 5);     // final grow (Grid layout)

    // Verify row 0 still contains "ShrinkGrow"
    for (size_t i = 0; i < 10; i++) {
        if (row0[i].codepoints[0] != expected[i]) return false;
    }
}
```

결과: **FAIL** with `(post-regrow row[0][1] lost: cp_count=0 cp[0]=0 expected 'h')`.

Root cause 분석:
1. `state->resize(1, 1)` 실행 시 `copy_cols = min(40, 1) = 1`, `copy_rows = min(5, 1) = 1` → row 0 의 cell 0 만 새 buffer 에 복사. Old buffer 는 `std::move` 로 drop.
2. `state->resize(20, 5)` 실행 시 `copy_cols = min(1, 20) = 1`, `copy_rows = min(1, 5) = 1` → 여전히 row 0 의 cell 0 만 남음. 나머지 99 cell 은 zero-init.
3. 화면에 거의 아무것도 안 보임 → "첫 session 버퍼 삭제" 증상.

Test 는 `main()` 에서 `TEST()` 호출을 주석 처리했으므로 현재 build 는 여전히 7/7 PASS. Fix 후 `main()` 에서 주석 해제 예정.

#### 1.2.3 왜 `min()` memcpy 는 원리적으로 부족한가

`TerminalRenderState::resize` 의 `min()` 기반 접근은 **각 single call 에 대해** content-preserving 이지만, **N 개의 resize call 이 연속** 된 sequence 에 대해서는 **idempotent 하지 않다**:

```
initial: [A B C D E F G H I J]  (10 cells)
resize(1): [A]                   (1 cell, B..J lost)
resize(10): [A _ _ _ _ _ _ _ _ _]  (10 cells, 9 blank)
```

복구 불가능한 이유: `resize(1)` 실행 시점에 old buffer `[A B ... J]` 는 `std::move` 되어 scope 종료 시 소멸. 다음 `resize(10)` 은 이전 state 의 information 이 없음.

WPF Grid layout 이 intermediate pass 로 pane 을 얼마나 작게 축소하는지는 WPF 내부 구현에 의존. 관찰된 증상 (row/col 이 1~2 로 truncate) 은 Grid 가 pane 을 일시적으로 **measured minimum** 으로 축소 후 final arrange 에서 복원하는 패턴과 일치.

### 1.3 Related Documents

- `docs/01-plan/features/split-content-loss-v2.plan.md` — 본 문서
- `docs/02-design/features/split-content-loss-v2.design.md` — Plan 승인 후 작성
- `docs/archive/2026-04/first-pane-render-failure/` — 선행 cycle, Appendix A 에 `4492b5d` hotfix 기록
- `tests/render_state_test.cpp` — empirical evidence (line 186-246, line 279 `main()` 주석)
- `src/renderer/render_state.h` — `RenderFrame`, `TerminalRenderState` 구조 (수정 대상)
- `src/renderer/render_state.cpp` — `resize()` 구현 (수정 대상)
- `src/renderer/quad_builder.cpp:55-91` — `frame.row(r)` 소비자 (API 투명성 검증 대상)
- `src/engine-api/ghostwin_engine.cpp:149` — `state.frame()` 소비자 1
- `src/renderer/terminal_window.cpp:69` — `state->frame()` 소비자 2
- Commit `4492b5d` — parent hotfix (single-call content-preserving memcpy)
- Commit `6141005` — regression test + CLAUDE.md row 8 update
- Memory: `project_split_content_loss_v2_pending.md`, `feedback_hypothesis_verification_required.md`

---

## 2. Scope

### 2.1 In Scope

**C++ renderer 변경** (`src/renderer/render_state.{h,cpp}`)
- [ ] `RenderFrame` 에 `cap_cols`, `cap_rows` (physical storage 차원) 추가. `cols`, `rows_count` 는 logical view 유지
- [ ] `RenderFrame::row(r)` 을 `{ cell_buffer.data() + r * cap_cols, cols }` 로 변경 (physical stride, logical span length)
- [ ] `RenderFrame::allocate(c, r)` 을 capacity-aware 로 재설계: 첫 호출 시 `cap_cols = c`, `cap_rows = r`
- [ ] `RenderFrame::reshape(new_cols, new_rows)` 신규 메서드:
  - `new_cols <= cap_cols && new_rows <= cap_rows` → metadata-only update (memcpy 0)
  - `new_cols > cap_cols || new_rows > cap_rows` → reallocate to `new_cap = max(cap, new)` + row-by-row remap 존재 content 유지
- [ ] `TerminalRenderState::resize(cols, rows)` 를 `_api.reshape()` + `_p.reshape()` 호출로 단순화
- [ ] `TerminalRenderState::resize` 가 여전히 `force_all_dirty` 유사한 동작으로 "grow 된 영역 + restore 된 영역" 을 다음 `start_paint` 에 복사하도록 `_api.dirty_rows` 설정 유지

**Test 변경** (`tests/render_state_test.cpp`)
- [ ] `main()` 에서 `TEST(resize_shrink_then_grow_preserves_content)` 주석 해제
- [ ] 추가 regression test:
  - `test_resize_capacity_retention` — resize(40x5) → write → resize(1x1) → resize(80x10) (초과 grow) 시 reallocate 동작 검증
  - `test_resize_metadata_only` — capacity 이하 resize 에서 `cell_buffer.size()` 변동 없는지 직접 검증
  - `test_resize_row_stride` — capacity 120x30 에서 cols=60 일 때 `row(1)` 이 `cell_buffer.data() + 120` (physical stride) 인지 검증
- [ ] 전체 suite PASS 유지: 기존 7 test + 신규 3 test + 기존에 disabled 되어 있던 1 test 총 **10/10 PASS**

**문서**
- [ ] Plan 문서 작성 (본 문서)
- [ ] Design 문서 작성 (`docs/02-design/features/split-content-loss-v2.design.md`)
- [ ] Check (Gap analysis) 문서: `docs/03-analysis/split-content-loss-v2.analysis.md`
- [ ] Report 문서: `docs/04-report/split-content-loss-v2.report.md`
- [ ] CLAUDE.md Follow-up Cycles row 8 상태 업데이트 (pending → in-progress → completed)

### 2.2 Out of Scope

- **WPF Grid layout 수정** (Option C): `PaneLayoutService.OnPaneResized` 의 debounce/coalesce 는 근본 원인 해결이 아니며, WPF Grid semantics 의존도가 높아 다른 layout edge case 에 깨질 가능성. 본 cycle 은 C++ 계층에서 해결
- **VT 계층 refill** (Option B): `ghostty_vt_for_each_row` 가 scrollback snapshot read 를 지원하는지 미확인 + force-dirty 가 ghostty 내부 상태와 conflict 우려. 본 cycle 은 renderer 계층 단독 수정
- **Non-terminal resize path**: SwapChain / D3D 리소스 resize 는 별개 경로 (`render_surface`, `DeviceResources`). 본 cycle 은 `RenderFrame` 의 cell buffer layout 만 변경
- **first-pane-render-failure** 의 기타 R-issue (R10 TsfBridge dead code 등): 해당 cycle 에서 same-cycle hotfix 로 처리됨
- **Cursor position** 의 shrink 시 clipping: 현재 `_api.cursor = old_api.cursor;` 로 단순 복사 중. Cursor 가 new cols/rows 범위 밖인 edge case 처리는 **Design 단계에서 결정**. 초기 plan 에서는 기존 동작 유지
- **Scrollback buffer**: ghostty VT 가 관리하는 scrollback history 는 `RenderFrame` 이 아닌 `VtCore` 의 영역. 본 cycle 은 viewport 만 다룸

### 2.3 Risk-bounded: 초기 범위에서 제외했지만 Design 에서 재검토

- **Memory footprint growth**: user 가 window 를 한 번이라도 극단적으로 키우면 (예: 300x120) `cap` 이 그 크기로 고정되어 이후 작은 window 에서도 메모리 보유. Bound: cell 당 ~16 bytes × 300 × 120 × 2 buffer ≈ 1.15 MB/session. 다수 session 시 누적 영향 Design 에서 평가 후 `shrink_to_fit` 임계 도입 여부 결정
- **Cursor 가 capacity 보다 작은 logical bounds 밖**: `_api.cursor.x/y` 가 이전 grow 이후 저장된 값일 때 current shrink 된 logical 범위를 벗어날 수 있음. 이를 `std::min` 으로 clamp 하는지 아니면 그대로 두는지 Design 결정

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | `RenderFrame` 의 `row(r)` 이 logical `cols` 길이의 span 을 반환하되 physical stride `cap_cols` 로 offset 계산 | High | Pending |
| FR-02 | `TerminalRenderState::resize(c, r)` 가 `c <= cap_cols && r <= cap_rows` 일 때 cell buffer 재할당 없이 metadata (cols, rows_count, dirty_rows) 만 업데이트 | High | Pending |
| FR-03 | `TerminalRenderState::resize(c, r)` 가 capacity 초과 시 new capacity = `max(current_cap, new)` 로 reallocate 하고 기존 cell content 를 row-by-row remap 하여 보존 | High | Pending |
| FR-04 | `TerminalRenderState::resize` 후 `_api.dirty_rows` 가 `0..rows_count` 전체 set 되어 다음 `start_paint` 가 `_p` 로 full propagate | High | Pending |
| FR-05 | `tests/render_state_test.cpp::test_resize_shrink_then_grow_preserves_content` 가 PASS (현재 commented out) | High | Pending |
| FR-06 | 기존 regression test 들 (`test_resize`, `test_resize_preserves_content`, `test_resize_grow_preserves_content`) 가 계속 PASS | High | Pending |
| FR-07 | 신규 test `test_resize_capacity_retention`, `test_resize_metadata_only`, `test_resize_row_stride` 가 PASS | Medium | Pending |
| FR-08 | 사용자 hardware smoke: Alt+V split → 좌측 pane 에 pre-split shell 출력이 보임. Alt+H split 도 동일. 3회 이상 연속 split 에서 content 유지 | High | Pending |
| FR-09 | `quad_builder.cpp`, `terminal_window.cpp`, `ghostwin_engine.cpp` 의 `frame.row(r)` / `frame.cols/rows_count` 소비 코드는 **수정 없이** 동작 | High | Pending |

### 3.2 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| **Performance** | `resize()` 의 metadata-only path 가 O(1) + dirty_rows set (O(rows_count)) 으로 single-call 수 μs 이내 | Timer log 또는 unit test 벤치 (out of scope for this cycle, deferred to render-overhead-measurement cycle) |
| **Memory** | 1 session 의 worst-case backing buffer = `cap_cols × cap_rows × sizeof(CellData) × 2 (_api + _p)`. 일반 사용 (120x30) ≈ 115 KB, extreme (300x120) ≈ 1.15 MB | `TerminalRenderState` 생성/resize 시 `cell_buffer.capacity() × sizeof(CellData)` 로그 또는 단순 계산 |
| **Correctness** | `resize` 의 idempotency: 임의의 resize sequence `r1, r2, ..., rN` 후 content 가 "어떤 resize 에서도 shrink 된 적 없는 max 영역 내" 의 초기 content 와 일치 | `test_resize_shrink_then_grow_preserves_content` + 추가 property test 또는 sequence test |
| **API compatibility** | `RenderFrame::row(r)` signature 및 semantics (span length = logical cols) 유지 | Compile check (consumer 3 파일) + unit test |
| **Thread safety** | Caller 가 `vt_mutex` 를 holding 중 `resize` 를 호출 — 기존 invariant 유지 (`render_state.h:62` 주석) | Code review |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] FR-01 ~ FR-09 모두 구현 완료
- [ ] `tests/render_state_test.cpp` suite 10/10 PASS (`scripts/test_ghostwin.ps1` 또는 unit test 실행 스크립트)
- [ ] `scripts/build_wpf.ps1 -Config Release` 가 warning 0 / error 0 으로 완료 (MSVC /W4 기준)
- [ ] 사용자 hardware smoke 통과:
  - [ ] S1: Alt+V split 후 좌측 pane 에 pre-split PowerShell prompt (`PS C:\> ...`) 그대로 유지
  - [ ] S2: Alt+H split 후 상단 pane 에 동일 content 유지
  - [ ] S3: Alt+V → Alt+V → Alt+V 3연속 후 최초 session 의 content 가 가장 왼쪽 pane 에 남아있음
  - [ ] S4: Alt+V → Alt+H 혼합 split 후 모든 기존 pane 의 content 유지
  - [ ] S5: 한 pane 에서 shell 명령 실행 → 출력 확인 → Alt+V split → 출력이 좌측에 유지
- [ ] 기존 PaneNodeTests 9/9 + VtCore 10/10 + WPF 0W/0E 유지
- [ ] Design 문서, Analysis 문서, Report 문서 작성

### 4.2 Quality Criteria

- [ ] **Match Rate >= 90%** (gap-detector 기준)
- [ ] Consumer 3 파일 (`quad_builder.cpp`, `terminal_window.cpp`, `ghostwin_engine.cpp`) **무수정**
- [ ] Regression 재발 방지: `test_resize_shrink_then_grow_preserves_content` enabled + PASS 유지
- [ ] C++ 코드 내 주석: `RenderFrame` 의 capacity 구조 설명 + `resize()` 의 metadata-only vs reallocate 분기 설명 (`4492b5d` 와 `6141005` 커밋 메시지를 `render_state.cpp` 에 간단히 레퍼런스)
- [ ] `feedback_hypothesis_verification_required.md` 준수: 추측 금지, empirical evidence 우선

---

## 5. Risks and Mitigation

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| **R1**: `row(r)` 의 physical stride 변경이 `quad_builder.cpp` 의 2-pass loop 에서 off-by-one 또는 out-of-bounds 유발 | High | Low | Consumer 는 `frame.cols` 만 참조하므로 span 길이가 logical cols 인 이상 안전. Unit test `test_resize_row_stride` 로 검증. Compile 후 `/W4 /Wall` warning 점검 |
| **R2**: Capacity grow path 의 reallocate + remap 가 기존 grow path (single `resize(80, 10)` 등) 의 content preservation 을 깨뜨림 | High | Medium | 기존 test `test_resize_grow_preserves_content` 가 그대로 PASS 해야 함. 추가 test `test_resize_capacity_retention` 으로 grow path 를 독립 검증 |
| **R3**: Grid layout 의 transient pass 가 shrink-then-grow 가 아닌 다른 sequence (예: grow-then-shrink, 또는 3-step shrink→grow→shrink) 로 동작해 여전히 content 손실 | Medium | Low | 본 cycle 의 Option A 는 **모든** resize sequence 에 대해 content 를 preserve (capacity 만 유지되면 어떤 resize 후에도 `0..cap_cols/rows` 영역의 data 는 살아있음). 추가 test 로 3-step sequence 검증 |
| **R4**: Memory footprint 증가가 multi-session 환경에서 문제 | Medium | Low | 일반 사용 (120x30) 기준 per-session 115 KB → 10 session 1.15 MB 허용 범위. Extreme size 의 footprint 폭증 시 Design 에서 `shrink_to_fit` 임계 도입. 본 cycle 에서는 측정만, 임계 결정은 follow-up |
| **R5**: Unit test 가 real Grid layout 의 `1x1` transient size 를 정확히 reproduction 하지 못함 (hypothetical discrepancy) | Medium | Low | `6141005` 의 test 가 사용자 증상과 일치하는 FAIL 을 empirically 냈으므로 reproduction 은 이미 확정. Fix 후 사용자 hardware smoke 로 end-to-end 검증 |
| **R6**: `_api.cursor` 의 x/y 가 현재 logical bounds 밖일 때 consumer 측 clipping 부재로 garbage 출력 | Low | Low | 기존 동작 유지 (Design 에서 결정). 현 시점에서 cursor OOB 가 관찰된 적 없음 |
| **R7**: `first-pane-render-failure` Appendix A 에서 `4492b5d` 를 "완전한 fix" 로 오기재한 것에 대한 문서 정합성 | Low | High | 본 Plan 문서 §1.2.1 에서 상태를 명시. Report 단계에서 Appendix A 를 "incomplete, superseded by split-content-loss-v2" 로 update |

---

## 6. Architecture Considerations

### 6.1 Project Level Selection

| Level | Characteristics | Recommended For | Selected |
|-------|-----------------|-----------------|:--------:|
| **Starter** | Simple structure | Static sites | ☐ |
| **Dynamic** | Feature-based | Web apps | ☐ |
| **Enterprise** | Strict layer separation | Complex architectures | ☐ |

**선택**: N/A. GhostWin 은 WPF 데스크톱 앱 + C++ engine + Zig libghostty-vt 혼합 stack 으로 rkit 의 "Level" 분류에 직접 매핑되지 않음. Clean Architecture 4-프로젝트 (GhostWin.Core / Interop / Services / App) 는 이미 WPF migration M-1 에서 확립됨.

### 6.2 Key Architectural Decisions

| Decision | Options | Selected | Rationale |
|----------|---------|----------|-----------|
| **Content preservation strategy** | A (backing capacity), B (VT refill on grow), C (WPF debounce), D (shrink backup) | **A (backing capacity)** | (1) 본 이슈의 root cause 인 "old buffer drop on shrink" 를 structural 하게 해결. (2) C++ renderer 독립 수정, VT / WPF 계층 무영향. (3) `std::vector` capacity/size 패턴의 well-known 관행. (4) Consumer API 투명. (5) Memory overhead 는 확정적이고 bounded |
| **Capacity shrink policy** | Never shrink / Shrink on explicit request / Auto-shrink after N frames small | **Never shrink (initial)** | 본 cycle scope 단순화. Auto-shrink 는 `shrink_to_fit` threshold 결정 등 추가 작업 필요 → follow-up cycle 후보. 일반 사용 footprint 가 bound 내 |
| **Dirty rows after resize** | `set 0..rows_count` / `reset` / 조건부 | **`set 0..rows_count` (유지)** | `4492b5d` 와 동일. Metadata-only path 에서도 next `start_paint` 가 `_p` 로 propagate 해야 하므로 dirty flag 필수 |
| **row() stride 선택** | `cols` (logical) / `cap_cols` (physical) | **`cap_cols` (physical)** | Metadata-only resize 가 memcpy 없이 가능하려면 physical stride 고정 필수. Span length 는 logical `cols` 로 clip 하여 consumer 투명 |
| **New method name** | `resize` / `reshape` + `resize` 래핑 / `set_logical_size` | **`TerminalRenderState::resize` 유지 + `RenderFrame::reshape` 신규** | Public API 변경 최소화. `TerminalRenderState::resize` 는 caller 가 이미 많으므로 signature 유지. 내부 `RenderFrame` 에 `reshape` 도입 |

### 6.3 Option 비교 상세

#### Option A: Backing buffer with max capacity (**선택**)

```cpp
struct RenderFrame {
    std::vector<CellData> cell_buffer;
    uint16_t cols = 0;         // logical
    uint16_t rows_count = 0;   // logical
    uint16_t cap_cols = 0;     // physical (new)
    uint16_t cap_rows = 0;     // physical (new)

    std::span<CellData> row(uint16_t r) {
        return { cell_buffer.data() + r * cap_cols, cols };  // physical stride, logical length
    }

    void reshape(uint16_t new_cols, uint16_t new_rows) {
        if (new_cols <= cap_cols && new_rows <= cap_rows) {
            // metadata-only
            cols = new_cols;
            rows_count = new_rows;
            return;
        }
        // reallocate to max(cap, new)
        uint16_t new_cap_cols = std::max(cap_cols, new_cols);
        uint16_t new_cap_rows = std::max(cap_rows, new_rows);
        std::vector<CellData> new_buffer(
            static_cast<size_t>(new_cap_cols) * new_cap_rows);
        // row-by-row remap: old physical stride → new physical stride
        uint16_t copy_rows = std::min(rows_count, cap_rows);
        uint16_t copy_cols = std::min(cols, cap_cols);
        for (uint16_t r = 0; r < copy_rows; r++) {
            std::memcpy(
                new_buffer.data() + r * new_cap_cols,
                cell_buffer.data() + r * cap_cols,
                copy_cols * sizeof(CellData));
        }
        cell_buffer = std::move(new_buffer);
        cap_cols = new_cap_cols;
        cap_rows = new_cap_rows;
        cols = new_cols;
        rows_count = new_rows;
    }
};
```

**장점**:
- Shrink 후 grow 가 **idempotent** — 어떤 sequence 에서도 `0..cap` 영역 content 생존
- Consumer 무수정 (`row(r)` 이 logical cols span 유지)
- `std::vector` capacity/size 패턴 — 잘 알려진 관행
- Single-threaded caller 의 복잡도 O(1) metadata-only path

**단점**:
- Memory overhead: 한 번 커진 capacity 는 유지 (추가 shrink policy 필요 시)
- `row()` 의 stride 가 consumer 의 "naive row offset = r × cols" 추측과 다름 — 현재는 consumer 가 `frame.row(r)` 만 쓰므로 투명, 향후 raw pointer access 도입 시 주의

#### Option B: VT-level refill on grow (**기각**)

`ghostty_vt_for_each_row` 를 resize 직후 강제로 재호출하여 VT 의 scrollback snapshot 을 read.

**장점**: VT 가 root-of-truth 라 정확

**단점**:
- ghostty VT API 가 scrollback snapshot read 를 on-demand 로 지원하는지 미확인
- Force dirty 가 ghostty 내부 state machine 과 conflict 가능
- `vt_bridge_update_render_state_no_reset` 이 내부 dirty flag 를 어떻게 관리하는지 추가 조사 필요
- Cycle scope 와 복잡도 대비 benefit 낮음

#### Option C: WPF Grid debounce (**기각**)

`PaneLayoutService.OnPaneResized` 에서 intermediate resize 를 coalesce.

**장점**: C++ 변경 0

**단점**:
- 근본 원인 해결 아님. Renderer 가 여전히 shrink 를 destructive 로 수행
- WPF Grid semantics 변경 시 다시 깨질 risk
- Dispatcher timing 에 의존 — 테스트 어려움
- ConpPty 가 작은 size 로 write 한 후 resize 되면 window 가 실제로 작은 상태에서 content 를 잃는 edge case 재발 가능

#### Option D: Shrink 시 buffer backup (**Option A 의 subset, 기각**)

`_backup` buffer 를 별도 유지하여 shrink 시 full copy, grow 시 restore.

**장점**: Option A 보다 구현 간단 (처음엔)

**단점**:
- 3-buffer 구조 (`_api` + `_p` + `_backup`) → 메모리 3 배
- `_backup` 의 life cycle 관리 (언제 drop? 여러 shrink 연속 시 max 추적?) 결국 Option A 의 capacity 개념 재발명
- Option A 가 더 simple 하고 general

### 6.4 Clean Architecture Approach

```
GhostWin 의 구조:
┌────────────────────────────────────────────────────┐
│ GhostWin.App (WPF)                                 │
│   ↓                                                │
│ GhostWin.Services (PaneLayoutService, etc.)        │
│   ↓                                                │
│ GhostWin.Interop (NativeEngine P/Invoke)           │
│   ↓                                                │
│ GhostWin.Core (interfaces)                         │
│                                                    │
│   ↓ (P/Invoke boundary)                            │
│                                                    │
│ ghostwin_engine.cpp (C++)                          │
│   ↓                                                │
│ TerminalRenderState (← 본 cycle 수정)              │
│   ↓                                                │
│ libghostty-vt (Zig, 무수정)                        │
└────────────────────────────────────────────────────┘
```

본 cycle 은 **C++ 계층의 `TerminalRenderState::resize` 경로** 만 수정. WPF / Services / Interop 계층은 무변경.

---

## 7. Convention Prerequisites

### 7.1 Existing Project Conventions

- [x] `CLAUDE.md` 프로젝트 rules
- [x] `.claude/rules/behavior.md` — 우회 금지 + 근거 기반 문제 해결
- [x] `.claude/rules/commit.md` — English commit, `feat/fix/refactor/docs/chore/test`, No AI attribution
- [x] `.claude/rules/build-environment.md` — `scripts/build_*.ps1` 필수
- [x] C++ style: 기존 `src/renderer/*.{h,cpp}` 의 pattern 준수 (namespace `ghostwin`, 소문자 snake_case, `/// @file` header)

### 7.2 Conventions to Define/Verify

| Category | Current State | To Define | Priority |
|----------|---------------|-----------|:--------:|
| **RenderFrame 의 capacity vs logical 개념 주석화** | 없음 | Header + cpp 에 `cap_cols/cap_rows` 와 `cols/rows_count` 의 의미 설명 | High |
| **Regression test 관리** | `test_resize_shrink_then_grow_preserves_content` 가 `main()` 에서 주석 처리 | Fix 후 uncomment, 동반 test 3개 추가 | High |

### 7.3 Environment Variables Needed

본 cycle 은 env var 신설 없음.

### 7.4 Pipeline Integration

N/A. 본 cycle 은 기존 Phase 5-E.5 부채 청산 follow-up.

---

## 8. Next Steps

1. [ ] **Plan 검토 + 승인** (사용자 확인)
2. [ ] `/pdca design split-content-loss-v2` — Design 문서 작성 (Option A 의 struct layout 확정, consumer 영향 체크리스트, test matrix, rollback plan)
3. [ ] `/pdca do split-content-loss-v2` — 구현:
   1. `render_state.h` 의 `RenderFrame` 에 `cap_cols/rows` + `reshape()` 추가
   2. `render_state.cpp` 의 `TerminalRenderState::resize` 를 `reshape()` 위임으로 재작성
   3. `tests/render_state_test.cpp` 에 신규 test 3개 + `main()` 에서 disabled test uncomment
   4. `scripts/test_ghostwin.ps1` 로 10/10 PASS 확인
   5. `scripts/build_wpf.ps1 -Config Release` 로 빌드 확인
   6. 사용자 hardware smoke (FR-08, 5 scenarios)
4. [ ] `/pdca analyze split-content-loss-v2` — gap-detector 로 Design ↔ implementation match rate 측정
5. [ ] Match Rate >= 90% → `/pdca report split-content-loss-v2`
6. [ ] (선택) `/simplify` 로 coding review
7. [ ] `/pdca archive split-content-loss-v2` + CLAUDE.md Follow-up Cycles row 8 → completed 로 업데이트
8. [ ] Memory update: `project_split_content_loss_v2_pending.md` → closeout memory 로 전환
9. [ ] `first-pane-render-failure` Appendix A 에 superseded 표시

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-09 | Initial draft. `4492b5d` 의 incomplete 원인 RCA + Option A/B/C/D 비교 + Option A 선택 근거 + consumer API 투명성 검증 + `6141005` empirical evidence reference | 노수장 |
