# 라운드1 에이전트08 원인 분석

## 결론
**결론 없음 — 탐색 중 종료**. `gw_surface_resize` (line 581) → `session_mgr->resize_session` 호출 경로를 추적하던 중 "왼쪽 pane 만 사라지는 특이성" 원인을 찾지 못하고 종료.

## 직접 확인한 증거 (부분)
1. `src/engine-api/ghostwin_engine.cpp:581-601` — `gw_surface_resize` 가 WPF Grid layout pass 마다 호출되어 `session_mgr->resize_session(surf->session_id, cols, rows)` 를 실행함 확인.
2. ResizeSession 은 C# 정의가 있지만 실제 호출은 C# → gw_surface_resize → native 내부 경로라는 점 확인.
3. "왼쪽 pane = 기존 session, 오른쪽 = 새 session" 가설을 확인하려던 중 분석 중단.

## 확신도
N/A (결론 미도출)

## 대안 가설
N/A

## 약점
분석 미완료

## 읽은 파일
- src/engine-api/ghostwin_engine.cpp (부분)
- src/session/session_manager.cpp (부분)
