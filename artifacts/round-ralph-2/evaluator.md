# 라운드2 평가자 집계

## 10명 표
| 에이전트 | 분류 | 확신도 | 한 줄 결론 |
|---|---|---|---|
| agent-01 | 결론 없음 | N/A | 파일 저장 실패 — 결론 미제출 |
| agent-02 | 결론 없음 | N/A | 파일 저장 실패 — 결론 미제출 |
| agent-03 | C. VT→_api memcpy 덮어쓰기 (resize 직후) | 72 | resize chain 후 start_paint dirty-row memcpy 가 VT 빈 cell 로 _api 덮어씀 |
| agent-04 | A. dual-mutex race | 55 | Session::vt_mutex ↔ ConPty::Impl::vt_mutex 가 다른 객체라 render/resize 가 RenderFrame race |
| agent-05 | A. dual-mutex race | 72 | dual mutex 토폴로지로 render thread 와 resize thread 가 TerminalRenderState 동시 조작 |
| agent-06 | B. HwndHost reparent + OnHostReady early return | 70 | HwndHost reparent 후 새 hwnd 가 engine 에 재등록 안 되어 swapchain 이 죽은 hwnd 에 묶임 |
| agent-07 | D. Grid shrink → VT resize 체인 (root 미확정) | 55 | Alt+V → 왼쪽 pane shrink → ghostty VT resize 가 viewport 내용을 scrollback 으로 밀어냄 |
| agent-08 | E. render_state min() shrink-then-grow (HEAD 버전) | 82 | HEAD 의 resize() min() memcpy 가 1x1 intermediate 로 cell 버퍼 영구 truncate |
| agent-09 | 결론 없음 | N/A | 파일 저장 실패 — 결론 미제출 |
| agent-10 | C. VT→_api memcpy 덮어쓰기 (split 직후 빈 cells) | 72 | split 직후 VT 가 빈 dirty cells 반환 → start_paint straight memcpy 가 _api row blank 로 덮음 |

## 분류별 집계
- A. dual-mutex race (Session::vt_mutex ↔ ConPty::Impl::vt_mutex): 2명 (agent-04, 05)
- B. HwndHost reparent + OnHostReady early return: 1명 (agent-06)
- C. VT→_api start_paint memcpy 덮어쓰기: 2명 (agent-03, 10)
- D. Grid shrink → ghostty VT resize 체인 (root 미확정): 1명 (agent-07)
- E. render_state min() shrink-then-grow (HEAD 커밋 버전): 1명 (agent-08)
- 결론 없음: 3명 (agent-01, 02, 09)

## 가설 상호 관계
- A (dual-mutex race) 와 C (VT→_api memcpy 덮어쓰기) 는 **보완적**: race 가 발생하면 render thread 가 resize 도중 stale/blank cell 을 memcpy 할 수 있어 동일 증상으로 수렴.
- C 와 D 는 **부분 보완**: D 가 trigger (Grid shrink 가 VT resize 호출), C 가 mechanism (resize 후 VT 가 빈 cell 을 dirty 로 돌려주면 memcpy 가 덮음). agent-07 은 (a)/(b)/(c) 세 sub-경로 중 root 미확정으로 보고.
- E 는 D 와 **상호 배타에 가까움**: agent-08 은 trigger 가 동일하지만 mechanism 을 render_state 내부 (min() shrink) 로 한정. 단 agent-08 은 HEAD 커밋만 본 반면 다른 에이전트들 (03, 07 등) 은 작업트리의 Option A backing capacity hotfix 가 적용됐다고 보고 → 어느 빌드를 분석했는지가 결론을 가른다.
- B 는 다른 가설들과 **상호 배타**: 원인을 C++ 엔진 layer 가 아닌 WPF/HWND 재바인딩 layer 로 한정. agent-06 본인도 증거 2 (engine text count 드롭) 와 충돌함을 언급.
- 결론 없음 3건은 평가 대상에서 제외하더라도 7명 사이에 5개 분류가 분산되어 있어 단일 합의가 형성되지 않음.

## 100% 합의 여부
아니오

## 마지막 줄 (필수)
합의 X
