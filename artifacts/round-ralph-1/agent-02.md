# 라운드1 에이전트02 원인 분석

## 결론 (한 문장, 20 단어 이내)
첫 session ID가 0이라 PaneContainerControl 의 sessionId!=0 가드로 host 재사용 실패, 새 HWND 에 surface 미바인딩.

## 직접 확인한 증거 3 가지
1. `C:\Users\Solit\Rootech\works\ghostwin\src\session\session_manager.h:118` 에서 `SessionId next_id_ = 0;` — 첫 세션 id 가 0 으로 시작. `session_manager.cpp:92` 의 `sess->id = next_id_++;` 로 확인.

2. `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.App\Controls\PaneContainerControl.cs:201` `else if (node.SessionId is { } sessionId && sessionId != 0)` — sessionId 가 0 이면 host 재사용 매치 loop 자체가 스킵되어, paneId 매치도 실패한 뒤 line 223 의 새 TerminalHostControl 생성 분기로 떨어짐.

3. `C:\Users\Solit\Rootech\works\ghostwin\src\GhostWin.Services\PaneLayoutService.cs:198` `if (state.SurfaceId != 0) return;` — split 때 _leaves 에 oldState 를 마이그레이션(line 63)했으므로 새 host 의 OnHostReady 호출 시 SurfaceId!=0 으로 silent return, 새 HWND 에 surface 가 바인딩되지 않음. 동시에 BuildGrid line 157-170 에서 기존 host 가 Dispose 대기열에 들어가 DestroyWindowCore → DestroyWindow 로 구 HWND 가 파괴됨. 구 surface 의 swapchain 은 파괴된 HWND 에 Present 를 시도해 화면에 아무것도 나오지 않음.

## 확신도
85

## 두 번째로 가능한 원인 (대안 가설)
WPF HwndHost 의 visual reparenting 이 일어날 때 logical parent 변경으로 DestroyWindowCore/BuildWindowCore 가 자동 호출되어 HWND 가 재생성되고, 같은 방식으로 OnHostReady 의 SurfaceId!=0 guard 때문에 새 HWND 에 surface 가 재바인딩되지 않는 시나리오.

## 내 결론의 약점
- 런타임에 실제로 "기존 host 가 dispose" + "새 host 가 생성" 되는지 확인한 trace 로그는 직접 보지 못했음
- sessionId==0 이 첫 세션에만 해당하므로, 만약 사용자가 "이미 split 되어 있는 상태 + 새 탭" 등 다른 시나리오에서도 재현한다면 이 가설은 부분 부정될 수 있음
- DXGI swapchain 이 DestroyWindow 후에도 어떤 DWM 합성 경로로 일부 프레임을 내보내는지 여부에 따라 "완전히 검은 화면" 인지 "마지막 프레임 고정" 인지가 달라질 수 있음

## 읽은 파일
- src/GhostWin.App/MainWindow.xaml.cs : 300-393
- src/GhostWin.App/Controls/PaneContainerControl.cs : 1-334
- src/GhostWin.App/Controls/TerminalHostControl.cs : 1-210
- src/GhostWin.Services/PaneLayoutService.cs : 1-245
- src/GhostWin.Services/WorkspaceService.cs : 30-89
- src/GhostWin.Core/Models/PaneNode.cs : 1-93
- src/engine-api/surface_manager.cpp : 60-136
- src/engine-api/ghostwin_engine.cpp : 100-305
- src/session/session_manager.cpp : 91-93
- src/session/session_manager.h : 118
- ghostwin_debug.log : 1-279
