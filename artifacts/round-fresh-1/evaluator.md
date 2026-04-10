# Evaluator 합의 판정 (Round 1)

## 0. 분류 정의

- **분류 A**: "두 자물쇠 race" — `Session::vt_mutex` 와 `ConPtySession::vt_mutex` 가 서로 다른 객체라서 resize 경로와 render 경로가 동일 mutex 를 공유하지 못해 `reshape` 중 `cell_buffer = std::move(new_buffer)` 가 render 스레드의 메모리 접근과 경합
- **분류 B**: "reshape slow path 의 copy bound 문제" — `render_state.cpp:110-111` 의 `copy_rows = min(rows_count, cap_rows)` 와 `copy_cols = min(cols, cap_cols)` 가 shrink 된 logical dims 를 사용해서 cap 초과 grow 시 capacity 내 잔존 content 가 drop (single-thread 에서도 재현)
- **분류 C**: 그 외 가설 (VT 측 blank row 재공급, structural null-buffer 등)

## 1. 10명 집계표

| 에이전트 | 분류 | 확신도 | 핵심 한 줄 인용 |
|---|---|---|---|
| F-01 | A | 85% | "서로 다른 두 mutex (Session::vt_mutex ↔ ConPtySession::vt_mutex) 때문에 reshape 의 grow-path 와 start_paint 가 동시 실행되어 cell_buffer 가 move 되는 순간 render 가 dangling 메모리/불일치 cap 을 읽는다" |
| F-02 | A | 92% | "Session::vt_mutex 와 ConPtySession::vt_mutex 가 서로 다른 객체라서 resize 경로와 start_paint 경로가 동일 mutex 를 공유하지 못해 TerminalRenderState 내부 reshape 가 찢어진 상태로 관측되는 dual-mutex race 가 원인" |
| F-03 | A | 72% | "`TerminalRenderState::resize` 와 `start_paint` 가 서로 다른 mutex 를 잡아서 `reshape` slow path 의 buffer 재할당과 render 스레드의 cell memcpy 가 동시에 실행되는 data race" |
| F-04 | A | 92% | "resize는 Session::vt_mutex로 잠그고 render는 ConPty vt_mutex로 잠가서 reshape 중 buffer가 교체되는 순간 render 스레드가 그대로 옛 포인터를 읽는다" |
| F-05 | C | 80% | "`_p` 프레임이 `_api` 와 독립적으로 `reshape` 되어 스트라이드 재매핑 시 dirty row 복사 없이 이전 content 가 사라진다" (reshape 후 VT 가 dirty 로 알리지 않아 `_p` 가 blank 로 전파되는 structural 문제) |
| F-06 | A | 65% | "Resize 경로 (`Session::vt_mutex`) 와 Render 경로 (`ConPtySession::vt_mutex`) 가 서로 다른 mutex 를 잡아 `reshape()` 와 `start_paint()` 의 memcpy 가 경합한다" |
| F-07 | B | 72% | "`RenderFrame::reshape` slow path 가 copy bound 로 shrink 된 logical dims 를 써서 cap 초과 grow 시 capacity 내 잔존 셀이 drop 된다" |
| F-08 | A | 92% | "render 스레드의 `ConPtySession::vt_mutex` 와 resize 경로의 `Session::vt_mutex` 가 서로 다른 뮤텍스라 `_api` 재할당이 race 로 덮인다" |
| F-09 | A | 78% | "resize_session 이 Session::vt_mutex 로 락하지만 렌더 쓰레드는 ConPty::vt_mutex 로 락해 _api/_p 가 비보호 동시 접근된다" |
| F-10 | A | 85% | "Resize 와 render 가 서로 다른 mutex 를 잡아 `RenderFrame` 재할당 중 race 발생" |

## 2. 분류별 count

- **분류 A (dual-mutex race)**: **8명** (F-01, F-02, F-03, F-04, F-06, F-08, F-09, F-10)
- **분류 B (slow path copy bound 버그, single-thread)**: **1명** (F-07)
- **분류 C (기타: `_p` structural / VT blank propagation)**: **1명** (F-05)

평균 확신도:
- A: (85+92+72+92+65+92+78+85)/8 ≈ **82.6%**
- B: 72%
- C: 80%

## 3. 가설 상호 관계

### A vs B 는 상호 배타인가 보완인가

- **결정적 empirical 분기점**: "단일 쓰레드에서 V1 시나리오 (`{40,5}→{1,1}→{200,50}→...`) 가 실패하는가?"
  - 분류 A 예상 답: **아니오** (race 가 있어야 실패. 단일 쓰레드에서 V1 은 PASS 해야 함)
  - 분류 B 예상 답: **예** (single-thread 에서도 `copy_rows=min(1,5)=1`, `copy_cols=min(1,40)=1` 로 1 cell 만 복사되므로 실패해야 함)
- 이 분기점에서 두 가설은 **배타적** — single-thread V1 결과가 하나의 답만 나오기 때문

### 양측이 공통으로 인정하는 사실

- V1 은 slow path (`new_c > cap_cols || new_r > cap_rows`) 를 trigger 하고, V2 는 fast path (metadata-only) 만 탄다
- 단일 쓰레드 `test_resize_shrink_then_grow_preserves_content (40,5→1,1→20,5)` 는 PASS
- 두 mutex 가 서로 다른 객체라는 code fact (F-07 을 제외한 모두가 직독 확인, F-07 은 이 사실을 부인하지 않음)

### A 지지자들이 제시한 "B 로는 설명 못함" 근거

- F-03: "단일 쓰레드 unit test `test_real_app_resize_flow` 가 PASS 하는 것도 mutex mismatch 해석과 일치 — 단일 쓰레드에서는 reshape 가 원자적으로 끝나고 memcpy 와 interleave 할 상대 스레드가 없음"
- F-08: "단일 쓰레드 unit test PASS 와 multi-thread 실패의 갭을 설명"

### B 지지자 F-07 이 제시한 "A 로는 설명 못함" 근거

- F-07 증거 2: "기존 unit test `test_resize_shrink_then_grow_preserves_content (40,5→1,1→20,5)` 가 PASS 하는 이유도 동일 — 20<=40, 5<=5 이라 두 번째 reshape 도 fast path. slow path 의 `copy_rows/cols = min(logical, cap)` 버그는 이 테스트로는 건드려지지 않는다"
- 즉 기존 단일 쓰레드 unit test 가 V1 의 slow path (grow-over-cap) 를 아예 trigger 하지 않기 때문에, 이 test 의 PASS 는 B 가설을 반증하지 못한다

### 핵심 불일치

- A 그룹은 "V1 의 100% FAIL 은 slow path 가 backing storage 재할당을 하는 구간에서 race 가 발생하기 때문" 으로 해석
- B 그룹 (F-07) 은 "V1 의 100% FAIL 은 slow path 의 copy bound 가 shrink 된 logical dims 를 쓰기 때문이고, race 가 전혀 없어도 single-thread 에서 재현된다" 로 해석
- 두 해석은 **같은 V1/V2 관측 데이터에 대한 다른 인과**. 상호 **배타**

### 가능한 3 번째 시나리오 (본 평가자 해석 아님, 에이전트 입에서 나온 경우만 기록)

- F-04 (A 지지) 의 대안 가설 H4: "`reshape` 자체의 copy_cols/copy_rows 로직 버그. 단일 스레드 unit test `test_resize_shrink_then_grow_preserves_content` 가 FAIL 했다는 점을 보면 아주 배제되지는 않는다. 두 원인이 겹쳐 있을 가능성도 있음 (추측)." — **F-04 는 이 단위 테스트가 FAIL 했다고 언급**. 하지만 F-07 은 동일 테스트가 "PASS" 라고 기록 (기존 shrink-then-grow test 의 grow 대상이 cap 내부라서 fast path 만 탔다는 해석). 이 사실 자체에 에이전트 간 불일치 존재

## 4. 결정적 질문

### Q1: 단일 쓰레드에서 V1 시나리오 (`{40,5}→{1,1}→{200,50}→{20,5}→{1,1}→{300,80}→{40,5}`) 가 실패하는가?

- 분류 A 예상 답: **아니오** — 경합할 상대 스레드가 없으므로 content 가 보존되어야 함
- 분류 B 예상 답: **예** — `{200,50}` 단계에서 `copy_rows=min(1,5)=1, copy_cols=min(1,40)=1` 이 되어 이전 content 1 cell 만 신 버퍼로 복사. 나머지 drop. `{300,80}` 단계에서 또 다시 동일 문제. Single-thread 여도 fail
- **empirical 검증 가능**: 예. `tests/render_state_test.cpp` 에 single-thread V1 시퀀스 순차 호출 test 를 추가하고 main() 에서 직접 호출. 10 초 내로 답이 나옴

### Q2: `test_resize_shrink_then_grow_preserves_content (40,5→1,1→20,5)` 는 실제로 PASS 인가 FAIL 인가?

- F-04 는 "FAIL 했다" 고 기재 (대안 가설 H4 문단)
- F-07 은 "PASS 한다" 고 기재 (증거 2 에서 "기존 unit test ... PASS 하는 이유도 동일")
- F-03 도 "단일 쓰레드 unit test (`test_resize_shrink_then_grow_preserves_content`, `test_real_app_resize_flow`) 가 PASS" 로 기재
- 사용자가 입력에 "`test_resize_shrink_then_grow_preserves_content` 가 FAIL empirical 확정" 이라고 썼는데 (CLAUDE.md Follow-up row 8), 에이전트 간 해석이 갈림. **사실 자체 재확인 필요**

### Q3: V1 의 `{200,50}` 단계 직후 `_api.cell_buffer` 의 non-zero cell 개수를 single-thread 로 찍으면 몇인가?

- A 예상: **정상적으로 preserve 됨** (reshape 가 old content 의 `min(1,5) × min(1,40) = 1` cell 만 복사하지만, 직전이 `{1,1}` 이었으므로 live cell 이 원래 1 개뿐이었다면 loss 없음. 하지만 그 1 cell 이 문제 지점)
- B 예상: **모든 이전 content 손실** (원래 `{40,5}` 상태의 200 cell 중 `{1,1}` shrink 에서 logical 만 clamp, capacity buffer 는 유지됐으나 `{200,50}` grow 에서 copy bound 가 logical 기반이라 1 cell 만 살아남음)
- **empirical 검증 가능**: 예. `render_state.cpp:110-111` 전후에 printf 삽입하거나 reshape 호출 후 `cell_buffer` scan

## 5. 100% 합의 여부

**아니오 — 10명 중 8명이 A, 1명이 B, 1명이 C**

- A 지지 8/10 (80%)
- B 지지 1/10 (10%)
- C 지지 1/10 (10%)

단일 분류에 합의 도달 실패. 다수 의견은 A 이지만, B 와 C 가 각각 empirical 으로 배타적인 주장을 제기했고, F-07 의 B 가설은 single-thread 재현성을 구체적인 수식 (`copy_rows=min(1,5)=1`) 까지 제시해서 Q1 을 통해 검증되어야 함. 또한 F-04 와 F-07 사이에 `test_resize_shrink_then_grow_preserves_content` 의 PASS/FAIL 사실 자체에 충돌이 있어 사실 확인도 필요.

## 6. 합의 없는 경우, 2차 라운드 반박 포인트

### 2차 라운드 핵심 질문 (10명 전원에게 물을 것)

1. **Q1 실행 결과**: `tests/render_state_test.cpp` 에 single-thread V1 시퀀스 test 를 추가해서 실행한 결과 PASS 인가 FAIL 인가? (구체적으로 `{200,50}` 호출 직후 `cell_buffer` 내 이전 content 가 몇 cell 살아있는지)
2. **Q2 사실 확인**: `test_resize_shrink_then_grow_preserves_content (40,5→1,1→20,5)` 는 현재 repo 에서 PASS 인가 FAIL 인가? (CLAUDE.md 설명 vs 에이전트 해석 불일치)

### 분류 A 에 대한 반박 포인트 (B, C 입장에서 A 진영에 묻기)

- A1: "V1/V2 asymmetry 를 mutex race 로 설명하려면 V1 이 multi-thread 에서만 실패해야 한다. single-thread V1 도 실패한다면 (Q1 결과가 FAIL 이면) A 가설은 기각되는가?"
- A2: "V2 는 fast path only 라서 PASS 라는 설명은, B 가설로도 동일하게 가능하다. V1 과 V2 의 차이가 slow path trigger 여부라는 점은 A/B 양측 공통. 이 점을 mutex race 의 **결정적** 증거로 삼을 수 있는 근거는 무엇인가?"
- A3: "F-07 의 수식 `copy_rows = min(rows_count, cap_rows) = min(1, 5) = 1` 은 code 상 정확한가? 이것이 single-thread 에서도 content loss 를 만든다면 mutex 와 무관한 2 차 버그가 동시에 존재한다는 뜻인가, 아니면 A 가 틀린 것인가?"

### 분류 B 에 대한 반박 포인트 (A 입장에서 B 진영에 묻기)

- B1: "`render_state.cpp:110-111` 의 copy bound 가 logical dims 를 쓴다는 것은 사실이다. 하지만 그 직전 `{1,1}` shrink 단계에서 fast path (metadata-only) 가 실행되면 logical `cols=1, rows_count=1` 로 줄어들지만 capacity 는 `{40,5}` 유지. 이후 `{200,50}` 에서 slow path 진입 시 `copy_rows=min(1,5)=1, copy_cols=min(1,40)=1` 로 정말 1 cell 만 복사되는가? 아니면 fast path 는 `rows_count/cols` 를 건드리지 않는가?"
- B2: "V1 의 `{1,1}` 는 capacity 보다 작은 shrink 다. fast path 가 'metadata-only' 라는 이름이지만 실제로 `cols`/`rows_count` 를 업데이트하는가 안 하는가? 이 한 줄이 B 가설의 성립/기각을 결정한다. `render_state.cpp:92-96` 에 어떤 코드가 있는지 직접 인용"
- B3: "만약 fast path 가 `cols`/`rows_count` 를 업데이트한다면 B 가 맞다. 이 경우 single-thread V1 은 FAIL 해야 한다. F-07 외 9명은 이를 확인했는가?"
- B4 (C 에 대한 반박): "F-05 의 가설 — `_p` 가 reshape 후 `_api` 의 blank 를 전파받는다 — 은 V2 가 PASS 인 것을 어떻게 설명하는가? V2 도 fast path reshape 은 하지만 `_p` 의 dirty-row 전파 경로는 동일하다. V2 에서도 동일 증상이 발생해야 하지 않나?"

### 2 차 라운드 이전에 먼저 해결할 수 있는 사실 (F-07 이 제시한 정적 증거)

- `render_state.cpp:92-96` 의 fast path 가 `cols`/`rows_count` 를 업데이트하는 코드인지 여부. 이 한 줄이 A 와 B 의 decisive split. 별도의 파일 read 로 한 번 더 직접 확인 필요.
