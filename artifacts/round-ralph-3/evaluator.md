# 라운드3 평가자 집계

## 10명 표

| 에이전트 | 분류 | 확신도 | 한 줄 결론 |
|---|---|:-:|---|
| agent-01 | 결론 없음 | N/A | 파일 저장 실패 (읽기 전용 모드) |
| agent-02 | 결론 없음 | N/A | 파일 저장 실패 |
| agent-03 | ghostty shell_redraws_prompt clearCells | 75 | ghostty `Screen.resize` 의 `shell_redraws_prompt=.true` default 가 resize 마다 prompt 행을 `clearCells` 로 비우고 PowerShell 은 OSC 133 미발송으로 redraw 안 함 |
| agent-04 | for_each_row dirty-only memcpy (ghostty cell wipe) | 55 | v2 capacity-backed fix 는 render_state layer 만 보호, `force_all_dirty + for_each_row` 가 ghostty Terminal 의 빈 dirty row 를 그대로 `_api` 에 memcpy 하여 프롬프트가 zero 로 덮어씀 |
| agent-05 | HwndHost reparent → stale swapchain | 55 | `BuildElement` reparent 시 HwndHost 가 BuildWindowCore 재호출해 새 child HWND 생성, swapchain 은 stale old HWND 에 바인딩된 채 남고 `OnHostReady` 는 `SurfaceId != 0` early-return 으로 재생성 안 함 |
| agent-06 | dual-mutex race (Session::vt_mutex ↔ ConPty::Impl::vt_mutex) | 78 | `resize_session` 이 `Session::vt_mutex`, render thread 는 `ConPtySession::Impl::vt_mutex` 로 상호 배제 실패 → `state->resize` 와 `start_paint` race 로 `_api`/`_p` 손상 |
| agent-07 | stale build artifact (WPF 출력 폴더 DLL 23분 오래됨) | 88 | 사용자 실행 DLL 이 working tree capacity-backed fix 보다 23분 오래된 옛 binary — DLL copy 누락, `4492b5d` commit message 의 진술 그대로 재현 |
| agent-08 | 결론 없음 | N/A | 파일 저장 실패 |
| agent-09 | 결론 없음 | N/A | 파일 저장 실패 |
| agent-10 | dual-mutex race (Session::vt_mutex ↔ ConPty::Impl::vt_mutex) | 85 | `gw_surface_resize` 가 UI 스레드에서 `Session::vt_mutex` 로 `state->resize` 호출, render 스레드는 별개의 `ConPtySession::Impl::vt_mutex` 로 `start_paint` → data race 로 첫 pane cell 손실 |

## 분류별 집계

| 분류 | 인원 | 에이전트 |
|---|:-:|---|
| A. dual-mutex race (Session vs ConPty vt_mutex) | 2 | agent-06, agent-10 |
| B. ghostty shell_redraws_prompt / clearCells (VT 측 prompt clear) | 1 | agent-03 |
| C. for_each_row dirty-only memcpy (ghostty empty row 그대로 memcpy) | 1 | agent-04 |
| D. HwndHost reparent → stale swapchain (surface 재생성 안 됨) | 1 | agent-05 |
| E. stale build artifact (WPF 출력 폴더 DLL 옛 binary) | 1 | agent-07 |
| Z. 결론 없음 (파일 저장 실패) | 4 | agent-01, agent-02, agent-08, agent-09 |

## 가설 상호 관계

- **A (dual-mutex race)** 와 **C (for_each_row memcpy)** 는 부분적으로 겹침: 둘 다 `_api` 의 cell_buffer 손상을 다루지만, A 는 thread race 를 proximate cause 로 지목, C 는 ghostty VT 측이 빈 row 를 dirty 로 넘기는 단일 스레드 메커니즘을 지목.
- **B (ghostty clearCells)** 와 **C** 는 "ghostty VT 가 resize 시점에 cells 를 지운다" 는 공통 전제를 갖지만, B 는 zig `Screen.resize` 의 `shell_redraws_prompt` 분기를 근거로, C 는 `force_all_dirty + for_each_row` callback 의 render-측 흐름을 근거로 함. 같은 root 의 서로 다른 면일 수 있음.
- **D (HwndHost reparent)** 는 독립적 — buffer 는 정상이지만 swapchain 바인딩 loss 로 화면만 안 그려짐 가설.
- **E (stale build artifact)** 는 메타 가설 — 가설 A~D 중 어느 것도 현재 working tree 코드가 아닌 옛 binary 의 동작을 설명할 수 있다고 주장. 다른 가설들과 논리적으로 배타적이지 않고 오히려 "사용자가 실행 중인 binary 는 fix 가 없는 이전 버전" 이라는 환경 가설.
- **결론 없음 (Z)**: 에이전트 01/02/08/09 4명은 동일한 "파일 저장 실패" 사유로 결론 미제출. 의미 있는 의견이 없음.

## 100% 합의 여부

10명 중 **의견을 제출한 에이전트는 6명** (agent-03, 04, 05, 06, 07, 10).

제출한 6명 중:
- dual-mutex race (A): 2명 (agent-06, agent-10)
- 기타 서로 다른 가설 각 1명씩: 4명 (agent-03 B, agent-04 C, agent-05 D, agent-07 E)

최대 분류는 A (2명) 로 과반 미달, 결론 없음 4명까지 포함하면 10명 중 단일 가설 수렴 없음.

## 마지막 줄 (필수)
합의 X
