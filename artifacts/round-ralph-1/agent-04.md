# 라운드1 에이전트04 원인 분석

## 결론 (한 문장, 20 단어 이내)
HEAD 의 `TerminalRenderState::resize` 가 min(old,new) memcpy 라, Alt+V split 중 intermediate 1x1 shrink 에서 첫 session cell buffer 가 소실된다.

## 직접 확인한 증거 3 가지

1. `src/renderer/render_state.cpp` (HEAD 커밋 `4492b5d` 내용, git show 로 확인) 의 `TerminalRenderState::resize` 는 `old_api = std::move(_api)` → `_api.allocate(cols, rows)` → `copy_rows = min(old.rows_count, rows)`, `copy_cols = min(old.cols, cols)` 로 row 별 memcpy 만 수행한다. shrink 하면 min() 바깥 cell 은 즉시 폐기되고, 이후 grow 하면 이미 폐기된 cell 을 복구할 수 없다. (git show 4492b5d:src/renderer/render_state.cpp 꼬리 60 줄 확인)

2. `tests/render_state_test.cpp:199-249` 의 `test_resize_shrink_then_grow_preserves_content` 가 정확히 이 시나리오를 empirical 로 재현한다. 40x5 에 "ShrinkGrow" 를 쓰고 → `state.resize(1,1)` → `state.resize(20,5)` 후 row[0] 의 10 글자 확인. 6141005 commit 메시지 ("post-regrow row[0][1] lost: cp_count=0 cp[0]=0 expected 'h'") 가 이 테스트가 현재 render_state.cpp 로 FAIL 함을 기록했고, 그래서 `main()` 의 TEST 호출이 주석 처리된 채 커밋됐다. (6141005 commit + working-tree 의 test 파일 재활성화 상태)

3. 호출 체인이 실제로 `state->resize` 에 intermediate dimension 을 전달할 수 있다: `src/GhostWin.App/Controls/PaneContainerControl.cs:218-231` 에서 BuildElement 가 split 직후 기존 host 를 `previousBorder.Child = null` 로 detach 한 뒤 새 Border/Grid 에 재부착한다. 이 unparent→reparent 사이 WPF layout pass 가 발생하면 `TerminalHostControl.OnRenderSizeChanged` (`Controls/TerminalHostControl.cs:126-141`) 가 `Math.Max(1, ...)` 로 clamp 된 작은 NewSize (최소 1x1 px) 를 받아 `PaneResizeRequested` 를 fire 하고, 이는 `PaneLayoutService.OnPaneResized` → `_engine.SurfaceResize` → `gw_surface_resize` (`src/engine-api/ghostwin_engine.cpp:581-606`) → `session_mgr->resize_session` (`src/session/session_manager.cpp:369-376`) → `sess->state->resize(cols, rows)` 로 연결된다. gw_surface_resize 에서 `cols = w / atlas->cell_width()` 로 계산하므로 작은 w/h 는 cols=1,rows=1 을 만든다.

## 확신도 (0 부터 100 사이 숫자)
72

확신도를 100 까지 올리지 못하는 이유:
- intermediate 1x1 resize 가 **실제 WPF layout 이벤트로 발생하는지** 는 로그나 디버거로 직접 확인하지 못했다 (코드상 가능하다는 것만 확인). empirical 로는 unit test 가 FAIL 한다는 것만 증거.
- 6141005 의 커밋 메시지와 CLAUDE.md Follow-up 8 번이 이 가설을 HIGH priority 로 기록했으므로 작성자 (프로젝트 오너) 도 같은 결론에 도달했다 (직접 인용은 아니고 메모만 참조). 하지만 다른 에이전트 분석 인용 금지 원칙상 이를 내 판단의 근거로 삼지 않는다.

## 두 번째로 가능한 원인 (대안 가설)
**Dual-mutex race**: `src/session/session_manager.cpp:369-376` 의 `resize_session` 은 `sess->vt_mutex` (Session 구조체의 mutex) 만 잡고 `state->resize` 를 호출한다. 반면 render thread 는 `src/engine-api/ghostwin_engine.cpp:146` 에서 `session->conpty->vt_mutex()` (ConPtySession::Impl 의 다른 mutex) 아래 `state.start_paint` 를 호출한다. 두 mutex 가 다른 객체라서 render thread 의 `start_paint` 가 resize thread 의 `state->resize` 와 완전히 병행 실행될 수 있다. resize 가 `_api`/`_p` 의 rows_count/cols/cell_buffer 를 mutate 하는 도중 render 가 `frame().row(r)` 를 읽으면 torn read 로 cell data 가 0 으로 보일 수 있다. `tests/render_state_test.cpp:546` 의 `test_dual_mutex_race_reproduces_content_loss` 가 이 가설을 직접 테스트한다 (working tree only).

이 가설 단독으로는 "매번 100% 재현" 이라는 증상에 비해 약하다. race 는 보통 확률적이다. 다만 split 직후 짧은 시간 resize 와 render 가 겹칠 확률이 높아서 빈도가 높을 수는 있다.

## 내 결론의 약점 (empirical 반박 가능 지점)

1. **intermediate 1x1 resize 가 실제로 발생하는지 증거 없음.** Grid layout 의 final pass 가 `~600x800` (반쪽 pane) 만 보낼 수도 있고, 그 경우 내 시나리오는 틀리다. OnPaneResized 에 전달된 w/h 를 로깅해서 1x1 가 나타나는지를 확인해야 empirical 확정.

2. **`gw_surface_resize` 의 w=0,h=0 가드.** 593-602 에서 `w > 0 ? w : 1` 로 clamp 하지만, atlas cell_width 가 8 이상이면 1/8 = 0 → 1 clamp 까지 가긴 한다. `OnRenderSizeChanged` 도 widthPx=1 floor 를 건다. 즉 cells 가 정확히 1x1 로 resize 가 가도록 하는 경로는 있는데, 정말 그런 intermediate pass 가 **발생한다는** empirical proof 없음.

3. **Working tree 의 Option A 코드.** render_state.cpp 는 modified (uncommitted) 상태로 reshape 기반 Option A 가 이미 들어있다. 사용자가 **어떤 빌드** 를 실행 중인지 확실하지 않다. HEAD (4492b5d) 로 빌드된 실행파일이면 내 결론이 맞고, working tree 로 재빌드된 것이면 원인은 다르다 (예: Option A 의 `min(rows_count, cap_rows) × min(cols, cap_cols)` grow-path 도 shrink 가 먼저 이뤄지면 cap 내 content 만 복사하는데, pre-reshape logical cols/rows_count 가 shrunk 상태면 여전히 loss 발생 — 이것이 `test_reshape_capacity_retention` 의 NOTE 에 적혀있는 design trade-off).

4. **PowerShell 이 SIGWINCH 수신 후 재도색하는 가능성.** Windows ConPty 는 resize 시 console 을 repaint 하도록 shell 에 신호를 보낸다. PowerShell 이 prompt 재도색을 한다면 설령 cell buffer 가 일시적으로 clear 돼도 곧 새 prompt 가 나타나야 한다. 이를 "프롬프트와 출력 내용이 사라진다" 라고 표현했을 수 있는데, 출력 내용 (이전 명령 결과) 까지 사라지는 것은 repaint 로 복구되지 않는다 (scrollback 의 윗줄). 증상 원문은 "프롬프트와 출력 내용" 이 둘 다 사라진다고 했으므로 repaint 로 회복되는 prompt-only 가 아니라 cell buffer 자체가 영구 소실 쪽이 더 정합한다. 이 부분은 내 결론을 오히려 지지한다.

## 내가 직접 읽은 파일 목록
- `src/GhostWin.App/Controls/PaneContainerControl.cs` : 1-334 (전체)
- `src/GhostWin.App/Controls/TerminalHostControl.cs` : 1-210 (전체)
- `src/GhostWin.Services/PaneLayoutService.cs` : 1-245 (전체)
- `src/GhostWin.Core/Models/PaneNode.cs` : 1-93 (전체)
- `src/renderer/render_state.h` : 1-120 (전체, working tree 수정본)
- `src/renderer/render_state.cpp` : 1-301 (전체, working tree 수정본)
- `src/renderer/render_state.cpp` : 4492b5d HEAD 버전 꼬리 60 줄 (git show 로)
- `src/engine-api/ghostwin_engine.cpp` : 1-340, 530-640
- `src/session/session_manager.cpp` : 200-395
- `src/session/session.h` : 1-148 (전체)
- `src/conpty/conpty_session.cpp` : 420-460 (resize + mutex)
- `src/vt-core/vt_core.cpp` : 1-207 (전체)
- `tests/render_state_test.cpp` : 1-839 (전체)
- `git log --oneline -20`, `git log -- src/renderer/render_state.cpp`, `git show 4492b5d --stat`, `git show 6141005 --stat`, `git status`, `git diff HEAD -- src/renderer/render_state.cpp`
