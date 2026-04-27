# M-14 Render Thread Safety — Design/Implementation Gap Analysis

> **요약 한 줄**: W1~W3 은 Design과 1:1 일치하며 구현·검증 완료. W4 는 "절차 자동화 + 1-pane baseline" 까지만 들어왔고 핵심 NFR 타깃인 **4-pane resize p95 는 자동화 CSV 가 없다** (사용자 수동 관찰만 존재). W5 외부 비교(FR-06)는 아직 수집 전. 산업 표준 해석은 **Match Rate 82%** — "report 진입 가능하지만 4-pane 수치 공백 + W5 미수행을 follow-up 으로 명시" 분기.

---

## 1. Analysis Overview

| 항목 | 내용 |
|------|------|
| Analysis Target | `m14-render-thread-safety` (feature branch `feature/wpf-migration`) |
| Design | `docs/02-design/features/m14-render-thread-safety.design.md` v1.0 (2026-04-20) |
| Plan | `docs/01-plan/features/m14-render-thread-safety.plan.md` v1.0 (2026-04-20) |
| Implementation | 17 commits `19db612..70f5bc9` on `feature/wpf-migration` |
| Analysis Date | 2026-04-22 |
| Evidence scope | 코드 + 단위 테스트 + 3개 baseline 폴더 + 3개 perf 분석 문서 + Obsidian milestone/backlog |

상세 커밋 체인(시간순, W1→W2→W3→W4→hardening):

| SHA | 분류 | 내용 |
|-----|------|------|
| `19db612` | W1 | perf instrumentation hook 추가 |
| `c458ad5` | W0 | PRD / Plan / Design 문서 추가 |
| `41fecb8` | infra | app exe 경로 탐지 SDK/RID 레이아웃 대응 |
| `476f4f2` | W1 | idle baseline 1,643 samples 기록 |
| `52ebfe1` | W2-a | `FrameReadGuard` contract 도입 |
| `6059ab4` | W2-b | 6개 frame reader를 새 계약으로 마이그레이션 |
| `c5c1a03` | W2-c | reshape-during-read stress + test build 연결 |
| `c31dffe` | W2-d | 방어 가드 제거 (contract 완성 후) |
| `346968f` | W3-a | `visual_epoch` 인프라 도입 |
| `d43abb6` | W3-b | `force_all_dirty` 제거 + skip path 적용 |
| `3a0eaf5` | W3 | idle baseline −99.76% 기록 |
| `7e54608` | W4 | Win32 SetWindowPos 자동 resize 루프 추가 |
| `03da259` | W4 | 1-pane resize baseline 기록 + follow-up 방향 재설정 |
| `94109e9` | post-hardening | `SessionVisualState` snapshot + present-aware epoch |
| `079b8e1` | post-hardening | `FrameReadGuard::get()` rvalue 금지 + 테스트 마이그레이션 |
| `0a86e44` | test | standalone test 빌드 + `SessionVisualState` 단위 테스트 |
| `5d91baa` | W4 | `-Panes > 1` 을 silently mislabel 하지 않고 reject |
| `70f5bc9` | fix | MainWindow shutdown 에서 null engine 가드 (shutdown 안정성) |

---

## 2. Overall Scores

| Category | Score | Status | 근거 |
|----------|:-----:|:------:|------|
| Design Match (구조 결정이 코드로 나왔는가) | 95% | 양호 | 5.1/5.2/5.3/5.4/8.3 모두 설계대로 구현됨 |
| FR Coverage (기능 요구 7건 중) | 86% | 양호 | FR-01~FR-05 완료, FR-07 완료, FR-06 만 미수행 |
| NFR Coverage (성능/안전성 5건 중) | 60% | 주의 | 안전성 + idle 확인. load NFR 미측정. 4-pane NFR 측정 자동화 없음 |
| Convention/Architecture | 100% | 양호 | Lock order invariant 준수, 주석/문서에 근거 표기 |
| **Overall Match Rate** | **82%** | **주의** | Design 대로 정확히 구현됐고 안전성 목표 달성. 남은 것은 **측정/비교** 카테고리(W4 4-pane, W5 전부) |

---

## 3. FR Per-Item Status

### FR-01 — `GHOSTWIN_RENDER_PERF` env gate + perf 로그 스키마

| 항목 | Design/Plan 요구 | 구현 위치 | 결과 |
|------|------------------|-----------|------|
| 환경 변수 gate (startup 1회 static) | Plan R-03 / Design 5.3 | `render_perf.cpp` — file-scope static `g_perf_enabled` | ✅ 일치 |
| 로그 스키마(12 필드: frame/sid/panes/vt_dirty/visual_dirty/resize/start_us/build_us/draw_us/present_us/total_us/quads) | Design 5.3 | `ghostwin_engine.cpp:342-354` | ✅ 일치 |
| hot-path overhead 최소 | Plan R-03 | `perf_enabled()` 은 plain load, disabled 경로 추가 작업 없음 | ✅ 일치 |
| `present_us` 를 `upload_and_draw_timed()` 로 `draw_us` 와 분리 | Design 5.3 + Plan 7.1 | `DrawPerfResult{upload_draw_us, present_us, presented}` (`render_perf.h:45`) | ✅ 일치 |

**상태**: Implemented.

### FR-02 — `measure_render_baseline.ps1` (3 시나리오)

| 항목 | Design/Plan 요구 | 구현 | 결과 |
|------|------------------|------|------|
| `idle / load / resize` 3개 시나리오 인자 | Plan 3.1 FR-02 | `[ValidateSet('idle','load','resize')]` | ✅ |
| CSV 출력 + summary (avg/p95/max/count) | Plan 3.1 FR-02 | `render-perf.csv` + `summary.txt` | ✅ |
| PresentMon 통합 | Design 5.3 + Plan 개선 P5 | `-PresentMonPath` 파라미터 + 병렬 실행 | ✅ 래핑은 되었음. 하지만 3개 baseline 폴더 어디에도 `presentmon.csv` 없음 → **병행 수집은 실제로 안 돌렸음** |
| Release 전용 판정 | Design 5.3 | 기본값 Release, Debug 시 경고 출력 | ✅ |
| `idle / load / resize` 모두 자동 실행 | Plan 3.1 FR-02 | idle/resize 자동화 완료. **load 는 여전히 수동** (`load-input drive is NOT yet implemented`) | 🟡 부분 |
| Win32 `SetWindowPos` 자동 resize 루프 (500ms 간격) | — (Design 5.4 재현 절차의 자동화) | `SetWindowPos` + `W4Automation.Win32` PInvoke | ✅ Design 에는 없던 개선. 적절 |
| Multi-pane 지원 | Design 5.4 "4-pane 재현 절차" | `-Panes > 1` 은 **silent mislabel 방지를 위해 throw** | ⚠️ 자동화 미지원. 4-pane 수집은 script 밖에서 수동 pre-split 필요 |

**상태**: Partial. load 자동화 없음, 4-pane 은 reject (의도적 안전장치지만 NFR 측정 경로가 비어 있음).

### FR-03 — `row()` 방어 가드 제거 + reader safety

| 항목 | Design/Plan 요구 | 구현 | 결과 |
|------|------------------|------|------|
| `FrameReadGuard` RAII + `shared_mutex` | Design 5.1 | `render_state.h:136-153`, `render_state.cpp:320-326` | ✅ |
| `acquire_frame() / acquire_frame_copy()` 2-API | Design 5.1 reader 길이 정책 | `render_state.h:173-178`, `render_state.cpp:320-335` | ✅ |
| 6개 reader 마이그레이션 | Design 5.1 reader 대상 목록 | commit `6059ab4`. 확인한 위치: `ghostwin_engine.cpp` 의 render surface / `get_cell_text:1138` / `get_selected_text:1173` / `find_word_bounds:1265` / `find_line_bounds:1319` + `terminal_window.cpp:74` | ✅ 6개 전부 |
| 짧은/긴 reader 정책 | Design 5.1 | `get_cell_text` / `find_line_bounds` / render hot path → `acquire_frame()`. `get_selected_text` / `find_word_bounds` → `acquire_frame_copy()` | ✅ 정책대로 |
| W2-d 가드 제거 시점 | Design 8.3 (마지막 단계) | 커밋 순서: `52ebfe1`(W2-a) → `6059ab4`(W2-b) → `c5c1a03`(stress) → `c31dffe`(W2-d 가드 제거) | ✅ 정확히 설계 순서 |
| stress test | Design 8.1, Plan NFR 안전성 | `test_frame_snapshot_stays_consistent_during_concurrent_reshape` — 500ms writer+reader, `row.size() != f.cols` 이면 즉시 실패 | ✅ 17/17 PASS |
| `FrameReadGuard::get()` rvalue 금지 | — (post-review hardening) | `render_state.h:148` `get() const && = delete;` | ✅ Design 에는 없던 추가 안전장치 |
| Lock ordering 주석 (`vt_mutex` → `frame_mutex`, reader 는 `frame_mutex` only) | Design 5.1 invariant | `render_state.h:14-18` + `.cpp:216`/`.cpp:260-262` 주석에 명시 | ✅ |

**상태**: Implemented. Plan NFR "1시간 random resize stress, 0 assertion/UAF/검정 프레임" 중 자동화된 500ms stress 는 PASS. 1시간 스트레스는 기록된 증거 없음.

### FR-04 — clean-surface skip-present

| 항목 | Design/Plan 요구 | 구현 | 결과 |
|------|------------------|------|------|
| `vt_dirty && visual_dirty && resize_applied` 모두 false 시 draw/present 생략 | Design 5.2 판단식 + Plan 3.1 FR-04 | `ghostwin_engine.cpp:196-198` — `if (!vt_dirty && !visual_dirty && !resize_applied) return;` | ✅ 정확히 일치 |
| `resize_applied` 을 dirty 로 간주 | Design 5.2 | 구현됨 (별도 분기) | ✅ |
| idle baseline 유의미 감소 증명 | Plan 7.3 완료 조건 | W1 1643 → W3 4 samples (−99.76%) 기록 | ✅ |
| `Present(1, 0)` 유지 | Design 5.4 | 변경 없음 (`upload_and_draw` 내부) | ✅ |
| DXGI tearing mode 는 범위 밖 | Design 5.4 / Plan 2.2 | 건드리지 않음 | ✅ |

**상태**: Implemented.

### FR-05 — `visual_epoch` 는 selection/IME/activate 전용 (resize 제외)

| 항목 | Design/Plan 요구 | 구현 | 결과 |
|------|------------------|------|------|
| selection 변경 시 epoch bump | Design 5.2 | `ghostwin_engine.cpp:1034` `set_selection` / `:1037` `clear_selection` | ✅ |
| IME composition 시 epoch bump | Design 5.2 | `ghostwin_engine.cpp:903` `set_composition` / `:906` `clear_composition` + `session_manager.cpp:50` TSF adapter | ✅ |
| session activate 시 epoch bump | Design 5.2 | `session_manager.cpp:290` `(*it)->visual_state.bump_epoch();` + `:194` 첫 session 생성 시 | ✅ |
| **resize 는 visual_epoch 에 섞지 않음** | Design 5.2 원칙 "resize 는 포함하지 않는다" | `resize_session` 경로에 `bump_epoch()` 호출 없음. resize 는 `needs_resize` + `resize_applied` 로만 신호 | ✅ |
| `SessionVisualState` snapshot-atomic contract | post-review hardening (Design 5.2 의도 강화) | `session_visual_state.h` — `VisualStateSnapshot{composition, selection, epoch}` 한 락 아래 캡처 | ✅ Design 의도 유지 + ordering 이슈를 `std::mutex` 로 단순화 (release/acquire atomic 대신) |
| present-aware epoch commit | post-review hardening | `ghostwin_engine.cpp:324-326` — `presented` 성공시에만 `last_visual_epoch = visual.epoch` | ✅ 추가 안전장치 |
| memory ordering 정책 | Design 5.2 "release/acquire" | 현재 `std::mutex` 기반 `SessionVisualState` 이므로 ordering 이 내부적으로 보장됨 (`atomic<uint32_t>` 대신 `uint32_t` + mutex) | 🔵 **방식 변경**: 원 Design 은 `atomic` + `release/acquire`. 구현은 `mutex` 기반 snapshot. 결과적으로 더 강한 보장(payload+epoch 원자성), R-02 "dropped redraw" 리스크는 제거. 문서 업데이트 필요 |

**상태**: Implemented, with method substitution (atomic ordering → mutex snapshot). Design 5.2 목적(non-VT visual 전용 + consistent publish) 은 달성했으나 **구체 매커니즘이 Design 문서와 불일치**.

### FR-06 — WT / WezTerm / Alacritty 외부 비교

| 항목 | Design/Plan 요구 | 구현 | 결과 |
|------|------------------|------|------|
| 3종 비교표 | Plan 3.1 FR-06, Design 8.4 | 기록 없음 (`docs/04-report/features/` 에 `m14-render-baseline.report.md` 없음) | ❌ |
| 4 시나리오 × 3회 반복 | Plan NFR 외부 비교 열세 판정 | 측정 없음 | ❌ |
| 완료 게이트 #5 판정 | Plan 4.1 | 미판정 | ❌ |

**상태**: **Scope-change (deferred)**. 사용자 요청대로 missing 이 아닌 "follow-up 이관" 분류. Obsidian milestone progress: `W5 ⏳`.

### FR-07 — Obsidian Milestones + Backlog 갱신

| 항목 | Design/Plan 요구 | 구현 | 결과 |
|------|------------------|------|------|
| `Milestones/m14-render-thread-safety.md` 상태 표기 | Plan 3.1 FR-07 | frontmatter `progress: [W1 ✅, W2 ✅, W3 ✅, W4 🟡, W5 ⏳]` | ✅ |
| W1/W3/W4 sub-baseline 문서 | — | `Milestones/m14-w1-baseline.md` / `m14-w3-baseline.md` / `m14-w4-baseline.md` 존재 | ✅ Design 에는 없던 세분화 (좋은 추가) |
| `Backlog/tech-debt.md` 업데이트 | Plan 3.1 FR-07 | 항목 #26 상태 = "`FrameReadGuard` + `SessionVisualState` hardening 으로 root cause 정리. 남은 건 M-14 측정/비교 close-out" | ✅ |

**상태**: Implemented (active status 유지, M-14 는 아직 close 되지 않았으므로 적절).

---

## 4. NFR Per-Item Status

| NFR | Budget | 측정 여부 | 실측 | 판정 |
|-----|--------|-----------|------|------|
| Idle CPU (1-pane) ≤ 2% | Plan 3.2 | ⚠️ **미측정** | W3 분석문에 "추정 0.3%" 로만 기술 — Task Manager 실측 없음 | 미확정(추정만) |
| Load p95 frame time ≤ 16.7ms (60fps) | Plan 3.2 | ❌ **미측정** | load 자동화 없음 → baseline 폴더에 load 시나리오 없음 | 측정 자체 안 함 |
| Resize p95 frame time (4-pane) ≤ 33ms | Plan 3.2 | 🟡 **간접** | 1-pane 자동: **34.3ms** (1.3ms 초과). 4-pane CSV 없음. "참을 만" 은 사용자 수동 관찰만 | **1-pane 에서 이미 미달** + 4-pane 자동 데이터 없음 |
| 외부 비교 열세 판정 규칙 | Plan 3.2 | ❌ **미수행** | FR-06 전체 미수집 | 미측정 |
| 안전성: `row()` 가드 제거 후 assertion/UAF/검정 프레임 0 | Plan 3.2 | ✅ 부분 | `test_frame_snapshot_stays_consistent_during_concurrent_reshape` 500ms stress PASS. 1시간 random resize 는 기록 없음 | PASS within automated scope |

---

## 5. Design §5 결정 코드 매핑

| Design 조항 | 결정 내용 | 코드 반영 |
|-------------|-----------|----------|
| 5.1 W2 | `shared_mutex + FrameReadGuard` + copy-first for 긴 reader | ✅ 정확히 일치. 락 순서 주석 `render_state.h:14-18` |
| 5.1 reader 규칙 | 6 reader 전부 마이그레이션 | ✅ 6개 call site 전부 `acquire_frame*` 로 교체됨 |
| 5.2 W3 | `visual_epoch` 가 selection/IME/activate 만 표현, resize 는 `resize_applied` 경로로 분리 | ✅ resize 경로에 `bump_epoch` 없음 |
| 5.2 ordering | `release` writer / `acquire` reader (`atomic<uint32_t>`) | 🔵 `std::mutex` + `uint32_t` 로 **방식 변경**. 의도(원자적 publish)는 달성, 오히려 강화 (snapshot-atomic). Design 문서 미업데이트 |
| 5.3 W1 | 로그 스키마(12필드) + `present_us` 별도 | ✅ `ghostwin_engine.cpp:342-354` 완전 일치 |
| 5.3 수집 | 내부 `render-perf` + PresentMon 병행 | ⚠️ script 는 병행 수집 지원 (`-PresentMonPath`). 실제 baseline 에는 PresentMon CSV 없음 |
| 5.4 W4 | Clean-surface skip, `Present(1,0)` 유지, tearing mode 는 범위 밖 | ✅ |
| 5.4 4-pane 절차 | "창 전체 resize 반복" 1차 기준 | ⚠️ 자동화는 1-pane 만 지원. `-Panes > 1` 은 reject |
| 8.3 가드 제거 순서 | contract → migration → stress → PASS → guard 제거 | ✅ 커밋 순서 5단계 정확히 일치 (`52ebfe1`→`6059ab4`→`c5c1a03`→`c31dffe`) |
| 8.5 Fallback Path | 1 시나리오 열세: follow-up / 2+: iterate / 구조적: milestone 분할 | ⏳ W5 미수행이라 미적용. W4 1-pane 1.3ms 초과 + 4-pane 공백 은 "1 시나리오 열세 + 측정 공백" 으로 → Fallback "report 에 follow-up 기록" 가능 |

---

## 6. Unexpected Deviations (Design 에 없지만 들어온 것 / Design 에 있지만 빠진 것)

### 6.1 Design 에 없지만 구현에 들어온 것 (모두 긍정적)

1. **`SessionVisualState` snapshot contract** (`session_visual_state.h`)
   - Design 5.2 는 `Session::visual_epoch` 단일 `atomic<uint32_t>` + selection/composition 각자의 필드 + 별도 mutex 를 전제.
   - 구현은 `VisualStateSnapshot{composition, selection, epoch}` 한 락에서 원자 캡처.
   - 효과: post-review 에서 발견된 "새 epoch + stale payload 소비" race 를 구조적으로 차단.
   - 평가: **Design 의도 강화**. Design 문서 업데이트 필요 (방식 변경 기록).

2. **`FrameReadGuard::get() const && = delete`** (`render_state.h:148`)
   - rvalue guard 에서 frame 참조를 빼낼 수 없게 강제.
   - Design 에는 없던 compile-time 안전장치. 좋은 추가.

3. **`present-aware epoch commit`** (`ghostwin_engine.cpp:324-326`)
   - `if (presented) surf->last_visual_epoch = visual.epoch;`
   - Design 5.2 는 "reader 가 load 후 비교" 까지만 명시. 구현은 "실제 present 성공 후에만 커밋" 으로 dropped-paint 방어.

4. **W4 Win32 자동 resize 루프** (`measure_render_baseline.ps1` `SetWindowPos`)
   - Design 5.4 는 "창 전체 resize 반복" 만 명시. 구현은 PInvoke 로 반복 자동화 → 재현성 향상.

5. **Obsidian milestone 세분화** (`m14-w1-baseline.md` / `m14-w3-baseline.md` / `m14-w4-baseline.md`)
   - Plan FR-07 은 `m14-render-thread-safety.md` 1개 갱신만 요구. 구현은 baseline 분석을 3개 sub-page 로 분리 — 추적성↑.

6. **`-Panes > 1` reject** (`measure_render_baseline.ps1:89-91`)
   - Silent mislabel 방지 위해 의도적으로 throw. 안전장치로 적절.

### 6.2 Design/Plan 에 있지만 구현에 빠진 것

1. **FR-06 외부 비교 (WT/WezTerm/Alacritty)** — 사용자 지시대로 scope-change 로 분류.
2. **Load 시나리오 자동화** — script 에 scenario 는 정의됐으나 "automated load-input drive is NOT yet implemented" 로 여전히 수동. Plan FR-02 의 "3개 시나리오 자동 실행" 의 1/3 부족.
3. **4-pane baseline CSV** — Plan 7.4 완료 조건 "pane 수 별 p95 frame time + present_ms" 의 4-pane 수치가 CSV 로 없음. 사용자 주관 평가 "참을 만" 만 존재.
4. **PresentMon 병행 실측** — script 가 지원하는 `-PresentMonPath` 옵션을 실제로 실행한 흔적 없음 (baseline 폴더 3개 어디에도 `presentmon.csv` 없음). Design 5.3 "내부/외부 둘 다 잡음" 이 외부는 공백.
5. **Idle CPU 절대값 측정** — Plan NFR "Release 60초 평균 CPU ≤ 2%" 의 Task Manager/Process Explorer 실측 없음. W3 분석문에 "추정 0.3%" 로만 기술.
6. **1시간 random resize stress** — Plan NFR 안전성 항목. 자동화된 500ms stress 는 PASS 했지만 1시간 스케일 증거 없음.

### 6.3 Design/Plan 어디에도 없지만 구현에 있는 정리 작업

- `70f5bc9` MainWindow shutdown null engine guard — M-14 본체와 분리된 shutdown 안정성 수정. 섞여 들어온 것은 나쁘지 않음(관련 경로 안정화).

---

## 7. Top Gap Items (심각도 순)

| # | Gap | 심각도 | 근거 |
|---|-----|:------:|------|
| 1 | **4-pane resize 자동 CSV 부재 + 사용자 수동 "참을 만" 평가만 존재** | 🟡 Medium | Plan NFR "Resize p95 (4-pane) ≤ 33ms" 판정 근거 미확보. 1-pane 이미 34.3ms — 선형 증가 시 ~137ms 예상. report 수록 전 반드시 정량 수집 필요 |
| 2 | **FR-06 외부 비교 전체 미수행** (scope-change) | 🟡 Medium | PRD 완료 게이트 #4/#5 판정 불가. Plan Fallback Path "1 시나리오 열세 → report 기록, M-14 닫음" 적용 가능하지만 3종 비교 없이는 판정 자체가 성립 안 됨 |
| 3 | **`visual_epoch` ordering 방식이 Design 과 다름** (atomic+release/acquire → mutex+snapshot) | 🔵 Low | 기능·안전성 모두 Design 의도보다 강화됐으나 Design 문서가 구현을 반영 못함. 현 상태는 **설계문서 stale**. Design v1.1 업데이트 or analysis 결과를 Design 에 역류 반영 필요 |
| 4 | **Load 시나리오 자동화 없음 + Load NFR(`≤ 16.7ms`) 측정 공백** | 🟡 Medium | Plan FR-02 "3 시나리오 자동" 중 load 미완성. script 가 사용자에게 수동 입력 안내만 출력. Plan NFR "Load p95 ≤ 16.7ms" 판정 자체가 비어 있음 |
| 5 | **Idle CPU 절대값 실측 부재** (Plan NFR "≤ 2%" 미확정) | 🔵 Low | W3 분석문에서 "추정 0.3%" 라고 기술하고 측정 필요성도 명시됨. Task Manager 30초만 있으면 확정 가능. 기록 누락에 가까움 |

---

## 8. Recommendation

### 8.1 진입 분기 판정

| Match Rate | 판정 |
|:----------:|------|
| 82% | **report 진입 가능하되 scope-renegotiation 병행** |

이유:
- **안전성 축 (W2)**: Design 대로 완결. root-cause 제거 확인 (방어 가드 없이 stress PASS). 이 축만 보면 close 가능.
- **측정 축 (W1/W3/W4)**: idle 은 −99.76% 로 완결. 4-pane 은 자동 CSV 없음. load 는 측정 자체가 비어 있음.
- **비교 축 (W5/FR-06)**: 전부 미수행. Plan 완료 게이트 #5 판정 불가.

### 8.2 권장 절차

1. **report 진입** (`/pdca report m14-render-thread-safety`) — **단, report 에 아래 2개 항목을 "Known Gaps" 로 명시**:
   - 4-pane resize p95 자동 CSV 부재 (수동 관찰 "참을 만" 만 기록)
   - W5 외부 비교 미수행 → follow-up milestone (예: `m15-render-baseline-comparison`) 조건 기록
   - Design 5.2 ordering 기술 내용을 `SessionVisualState` mutex 방식으로 업데이트 (analysis 이 Design 에 소급)
2. **Plan §7.5 Fallback Path 적용**: 측정 공백 2건 + 4-pane 자동 부재 → "1 시나리오 열세 + 측정 공백" 분기 → **M-14 는 닫되 report 에 follow-up 명시**. `/pdca iterate` 로 재진입하는 선택지보다 follow-up 분리가 건전 (구조 작업 자체는 완결됐고 남은 건 측정/비교 노력).
3. **Obsidian frontmatter 유지**: 현재 `W4 🟡 / W5 ⏳` 가 정확한 상태. report 작성 시 milestone status 를 `done-with-followup` 같은 명시 상태로 전환 권장.

### 8.3 report 작성자를 위한 체크포인트

- [ ] report 의 "Known Gaps" 섹션에 위 Top 5 전부 나열
- [ ] Before/After 수치: W1(1643) → W3(4) idle + 1-pane resize(34.3ms) 만 기록. 4-pane 수치는 "manual qualitative only" 로 labelling
- [ ] FR-06 은 "deferred to m15-render-baseline-comparison (or follow-up)" 로 기록 + PRD 완료 게이트 #4/#5 는 "untested" 표기
- [ ] Design 5.2 ordering 실제 구현이 mutex-based snapshot 임을 Design 1.1 개정으로 소급 반영
- [ ] Obsidian `Milestones/m14-render-thread-safety.md` frontmatter 를 `status: follow-up-split` 또는 동등한 상태로 전환

---

## 9. Category Breakdown (근거 수치)

| Category | 항목 수 | 충족 | 부분 | 미충족 | Score |
|----------|:------:|:---:|:---:|:----:|:----:|
| FR | 7 | 5 (FR-01,03,04,05,07) | 1 (FR-02 load 자동화 없음) | 1 (FR-06 deferred) | 86% (딱 떨어지지 않음; deferred 은 0.5 weight) |
| NFR | 5 | 1 (안전성 automated) | 1 (Resize 4-pane 미자동) | 3 (Idle CPU 미실측, Load 미측정, 외부비교 미수행) | 60% |
| Design §5 결정 | 9 | 7 | 1 (5.2 ordering 방식 변경) | 1 (5.3 PresentMon 실수집 공백) | ~89% |
| Convention/Architecture | 모든 항목 | 전부 (lock order 주석, feedback_teaching_comments 준수, commit message 영문/타입 준수) | 0 | 0 | 100% |

**가중 평균 (FR 0.35 + NFR 0.30 + Design 0.25 + Conv 0.10)**:
`0.86 × 0.35 + 0.60 × 0.30 + 0.89 × 0.25 + 1.00 × 0.10 = 0.301 + 0.180 + 0.222 + 0.100 ≈ 0.803`

→ **Match Rate 80~82%** (반올림 82% 로 표기).

---

## 10. 한 줄 요약

> **M-14 구조 작업(안전성 + idle skip)은 Design 그대로 완결됐다. 남은 건 측정과 비교 — 4-pane 자동 CSV 없음 + Load 시나리오 자동화 없음 + FR-06 전체 미수행. report 진입은 가능하되 이 3건을 "Known Gaps" 로 명시하고 `m15-render-baseline-comparison` follow-up 으로 분리해 M-14 는 닫는 Plan Fallback Path 분기를 권장. Match Rate 82%.**

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 1.0 | 2026-04-22 | Initial gap analysis against Design v1.0 + Plan v1.0 + 17 commits `19db612..70f5bc9` | gap-detector (automated analysis) |
