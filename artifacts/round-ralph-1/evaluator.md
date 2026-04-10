# 라운드1 평가자 집계 보고서

## 10명 결론 표

| 에이전트 | 분류 | 확신도 | 한 줄 결론 |
|---|---|---|---|
| agent-01 | C. render_state.cpp straight memcpy 덮어쓰기 (VT 가 cp_count=0 blank row 반환) + WPF 1px intermediate shrink 체인 | 78 | WPF Grid 1px intermediate layout pass → VT 1x1 축소 → start_paint 가 blank row 를 `_api` 에 memcpy 로 덮어씀 |
| agent-02 | E. sessionId==0 / SurfaceId!=0 가드로 host 재사용·재바인딩 실패 (C# 쪽 구조 버그) | 85 | 첫 session id=0 → PaneContainerControl 가드 스킵 → 새 HWND 에 surface 미바인딩 + 구 HWND destroy |
| agent-03 | B. ghostty Screen.resize 의 prompt_redraw clearCells 경로가 prompt row 를 지움 | 60 | `shell_redraws_prompt=true` 경로가 resize 전 prompt 라인 cell 을 blankCell 로 덮어씀 |
| agent-04 | A. render_state.cpp min() memcpy 가 shrink-then-grow 에서 content 영구 truncate | 72 | HEAD `4492b5d` 의 min(old,new) memcpy 가 intermediate 1x1 shrink 에서 cell buffer 를 잃어버림 |
| agent-05 | D. dual-mutex race (Session::vt_mutex ↔ ConPtySession::Impl::vt_mutex) | 78 | 두 mutex 가 다른 객체 → resize_session 과 start_paint 가 같은 `_api` 를 비동기 동시 접근 |
| agent-06 | F. WPF Grid intermediate 작은 크기 resize → ghostty VT primary.resize reflow 가 prompt 를 삭제 | 55 | Grid 재배치 intermediate (1px/0) → VT 1x1 reflow → wraparound reflow 가 content 를 scrollback 으로 밀거나 truncate |
| agent-07 | 결론 없음 | N/A | 탐색 중 종료 |
| agent-08 | 결론 없음 | N/A | gw_surface_resize 추적 중 종료 |
| agent-09 | B. ghostty Screen.resize 의 prompt_redraw clearCells + start_paint straight memcpy 가 `_api` 를 blank 로 덮어씀 | 55 | `shell_redraws_prompt=true` clearCells → cp_count=0 → start_paint 가 straight memcpy 로 `_api` 덮어쓰기 |
| agent-10 | D. dual-mutex race (Session::vt_mutex ↔ ConPtySession::Impl::vt_mutex) | 72 | Render 는 ConPtySession vt_mutex, resize_session 은 Session::vt_mutex → 완전 독립 interleave |

## 분류별 집계

- **A. render_state min() memcpy truncate (shrink-then-grow content loss)**: 1명 (agent-04)
- **B. ghostty Screen.resize prompt_redraw / clearCells 경로**: 2명 (agent-03, agent-09)
- **C. render_state straight memcpy + WPF 1px intermediate 체인 (VT blank row 반환 → `_api` 덮어쓰기)**: 1명 (agent-01)
- **D. dual-mutex race (Session::vt_mutex vs ConPtySession::Impl::vt_mutex)**: 2명 (agent-05, agent-10)
- **E. C# sessionId==0 / SurfaceId!=0 host 재바인딩 실패**: 1명 (agent-02)
- **F. WPF Grid intermediate → VT primary.resize reflow 로 content loss**: 1명 (agent-06)
- **결론 없음**: 2명 (agent-07, agent-08)

## 가설 상호 관계

- A, C, F 는 모두 "intermediate 작은 크기 (1x1 근처) resize 가 상류에서 흘러들어온다" 는 전제를 공유하지만, **어느 층이 content 를 실제로 잃는가** 에서 갈린다:
  - A = `render_state.cpp` 의 min() memcpy 자체 (C++ side)
  - C = VT 가 blank row 를 돌려주고 `start_paint` straight memcpy 가 그걸 덮어씀
  - F = ghostty `primary.resize` reflow 단계 (Zig VT side)
- B 는 A/C/F 와 다른 trigger 를 주장 — "intermediate 1x1" 이 아니라 "resize 가 발생하는 모든 순간" 에 `prompt_redraw=true` 경로가 명시적으로 `clearCells` 를 호출한다는 deterministic 경로.
- C 와 B 는 "straight memcpy 가 `_api` 를 blank 로 덮어씀" 이라는 종착점을 공유하지만 **cp_count=0 의 upstream 원인** 이 다르다 (C = VT 내부 resize 중 transient blank, B = 명시적 prompt clear).
- D 는 A/B/C/F 와 독립된 층위 — 단일 스레드 content loss 가 아니라 **두 스레드 interleave** 를 주장. 다른 가설이 맞아도 D 가 동시에 성립할 수 있음 (보완 가능).
- E 는 C# 층 구조 버그로, "cell 이 지워지는게 아니라 render 가 stale HWND 에 Present 된다" 는 완전히 다른 증상 모델. A/B/C/D/F 와 상호 배타.

대체로 **층위별 가설** (C# E, C++ render_state A/C, VT Zig B/F, threading D) 이 섞여있고, 일부는 보완 관계, 일부는 상호 배타.

## 에이전트 간 사실 충돌

1. **`test_resize_shrink_then_grow_preserves_content` 상태**:
   - agent-04 "현재 render_state.cpp 로 FAIL 함을 기록했고, 그래서 `main()` 의 TEST 호출이 주석 처리된 채 커밋됐다" (6141005 evidence commit 근거)
   - agent-10 "`render_state_test.cpp:819-822` 에서 ... 활성화되어 있다. 만약 이 테스트가 실제 CI 에서 PASS 하고 있다면 split-content-loss-v2 fix 자체는 올바르게 들어간 것" — memory note 와 실제 테스트 파일 상태가 다르다고 인정
   - 즉 테스트 활성화 여부에 대한 **직접 관찰** 이 서로 다름.

2. **working tree 의 render_state.cpp 수정 상태**:
   - agent-04 "Working tree 의 render_state.cpp 는 modified (uncommitted) 상태로 reshape 기반 Option A 가 이미 들어있다. 사용자가 **어떤 빌드** 를 실행 중인지 확실하지 않다"
   - agent-05 "RenderFrame::reshape 는 fast path ... cell_buffer = std::move(new_buffer); cap_cols = new_cap_c; ..." — Option A 가 이미 들어있는 working tree 전제로 분석
   - agent-01 "Round 2 합의로 defensive merge 가 제거되어 straight memcpy 만 수행함" — 이건 merge 제거 측면만 얘기
   - 즉 **현재 바이너리가 어떤 코드로 빌드되었는가** 에 대한 전제가 에이전트마다 다름.

3. **defensive merge 주석 날짜**:
   - agent-09 "defensive merge 제거 주석의 날짜(2026-04-10)가 오늘(2026-04-09)보다 하루 미래여서 이 수정이 실제로 현재 바이너리에 들어 있는지 직접 확인하지 못했다" — 명시적으로 미래 날짜 의심
   - 다른 에이전트는 이 불일치를 언급하지 않음.

4. **첫 session id 값**:
   - agent-02 "`next_id_ = 0;` — 첫 세션 id 가 0 으로 시작. `sess->id = next_id_++;`"
   - 다른 에이전트는 sessionId 값을 가설의 근거로 삼지 않음 (충돌은 아니지만 agent-02 단독 사실).

5. **`shell_redraws_prompt` 기본값**:
   - agent-03 "`shell_redraws_prompt: osc.semantic_prompt.Redraw = .true` 로 **기본값이 `.true`**"
   - agent-09 "PowerShell (PSReadLine)은 OSC 133 semantic prompt marking을 발행해 `shell_redraws_prompt = true`가 되는 경로가 존재" — PowerShell 의 OSC 133 emit 에 조건부로 기록
   - 두 에이전트 모두 B 가설이지만 **flag 가 항상 true 인지 OSC 수신 후 true 인지** 에 미묘한 차이.

## 100% 합의 여부

**아니오**. 6개의 서로 다른 분류 + 2명의 결론 없음으로, 최다 분류 (B 및 D, 각 2명) 도 10명 중 2명에 불과. 합의에 필요한 수치 (10/10) 와 매우 큰 격차.

## 미합의의 경우 2차 라운드 반박 포인트

### 분류 A (render_state min() memcpy) 에 대한 반박
- agent-05/agent-10: "render_state resize 이후 dirty_rows 를 전부 set 하므로 다음 start_paint 에서 VT cell 로 바로 덮어쓰여짐. min() memcpy 가 살아남아도 곧 VT 가 돌려주는 cell 로 대체되는데 왜 content 가 복원되지 않는지 설명 필요" (암묵적)
- agent-01: "`_api` 의 buffer truncate 이전에 VT 자체가 blank 를 돌려주면 `render_state.cpp` 의 어떤 fix 로도 해결 안 됨"
- agent-06: "Option A 가 있어도 upstream (VT) 가 이미 잃으면 소용없음" 명시

### 분류 B (prompt_redraw clearCells) 에 대한 반박
- agent-03 스스로: "Screen.zig:1709 의 `self.cursor.semantic_content != .output` 조건은 cursor 가 **output 라인이 아닐 때만** clear 를 수행. 즉 output 자체는 보존돼야 함" — 증상 (출력 내용 사라짐) 과 가설이 완전히 일치 안함
- agent-09 스스로: "Screen.resize의 `clearCells` 경로는 보통 'prompt 시작부터 아래로'만 지우므로, cursor 위쪽의 이전 명령 출력까지 사라진다는 사용자 증상을 완전히 설명하려면 추가 메커니즘이 필요하다"
- agent-04: "PowerShell 이 SIGWINCH 수신 후 재도색... 출력 내용 (이전 명령 결과) 까지 사라지는 것은 repaint 로 복구되지 않는다 ... cell buffer 자체가 영구 소실 쪽이 더 정합한다" (우회적으로 B 의 prompt-만 재도색 모델 약화)

### 분류 C (straight memcpy + VT blank row 반환) 에 대한 반박
- agent-03/09: "VT 가 정확히 어느 상황에서 cp_count=0 을 돌려주는지 런타임 empirical 로 미확인"
- agent-05: "정상 clear/cls 경로도 cp_count=0 을 쓰기 때문에 merge 복귀는 타당하다 — straight memcpy 를 탓할 근거가 약함"

### 분류 D (dual-mutex race) 에 대한 반박
- agent-05 스스로: "Race condition 은 일반적으로 비결정적입니다. 100% 재현이라는 빈도는 race 가 아니라 **deterministic 한 VT-level content loss** 를 더 가리킬 수 있습니다"
- agent-10 스스로: "race 는 일반적으로 확률적이다. 100% 재현은 race 보다는 deterministic bug 의 특성"
- agent-05 스스로: "x86-64 에서 aligned uint16_t 단일 store 는 tear 되지 않으므로, race 가 있더라도 `cols` 단일 read 는 old 나 new 둘 중 하나를 보고 중간값은 안 나옵니다"

### 분류 E (sessionId==0 / SurfaceId!=0 host 재바인딩 실패) 에 대한 반박
- agent-01/04/06 등: 증상이 "화면에 아무것도 안 나옴" 이 아니라 "prompt 와 출력 내용이 사라진다" 인데, 이 가설은 render 결과 자체가 화면에 안 뜨는 모델이라 symptom semantics 와 불일치 가능
- agent-02 스스로: "sessionId==0 이 첫 세션에만 해당하므로, 만약 사용자가 '이미 split 되어 있는 상태 + 새 탭' 등 다른 시나리오에서도 재현한다면 이 가설은 부분 부정될 수 있음"

### 분류 F (VT primary.resize reflow) 에 대한 반박
- agent-06 스스로: "WPF Grid reparent 가 실제로 NewSize.Width=0 intermediate 를 유발하는지 본인이 breakpoint 나 log trace 로 확인한 것이 아님"
- agent-06 스스로: "내 가설 (VT reflow 가 root cause) 이 맞다면 Option A 로는 해결 안 됨 (source data 인 VT 자체가 이미 잃었기 때문) ... 반면 CLAUDE.md 는 Option A 를 '유력' fix 로 적었으므로, project 내 다른 분석은 reflow 보다 `_api` memcpy truncate 를 더 유력하게 봄. 이 불일치가 내 가설의 약점"
- agent-03 의 Terminal.zig reflow 경로 분석과 부분 겹치지만 agent-03 은 reflow 가 아니라 prompt_redraw clearCells 를 강조 → 사실 인용 범위 차이

## 마지막 줄 (필수)

합의 X
