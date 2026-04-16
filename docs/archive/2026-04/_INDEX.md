# Archive Index — 2026-04

| Feature | Phase | Match Rate | Archived | Documents |
|---------|:-----:|:----------:|----------|-----------|
| tsf-ime | Completed | 99% | 2026-04-01 | plan, design, report |
| dpi-aware-rendering | Completed | 98.6% | 2026-04-01 | plan, design, analysis, report |
| cleartype-sharpness-v2 | Completed | 95% | 2026-04-03 | plan, design, report |
| glyph-metrics | Completed | 93% | 2026-04-03 | plan, design, analysis, report |
| session-manager | Completed | 95% | 2026-04-03 | design, analysis, report, research |
| tab-sidebar | Completed | ~98% | 2026-04-05 | design, analysis, report |
| titlebar-customization | Completed | 99.3% | 2026-04-05 | plan, design, analysis, report |
| tab-sidebar-stackpanel | Completed | 91% | 2026-04-05 | plan, design, analysis, report |
| settings-system | Completed | 98% | 2026-04-05 | plan, design, analysis, report |
| e2e-ctrl-key-injection | Completed | 100% (8/8 e2e, 5/5 hw, 9/9 unit) | 2026-04-08 | plan, design (v0.1+v0.2), report |
| core-tests-bootstrap | Completed | 99.1% (9/9 unit 41-44ms deterministic) | 2026-04-08 | plan, design, report |
| e2e-evaluator-automation | Completed | automation 100% + 2 silent regressions detected (MQ-1 R2 repro, MQ-7) | 2026-04-08 | plan, design (v0.1+v0.2), report |
| first-pane-render-failure | Completed + amended 2026-04-09 | 77.0% weighted (core fix ≈95%, ceiling 89.6% architectural) — user 100% hit-rate blank eliminated, e2e 7/8 visual, 6 follow-up cycles; **post-archive hotfix: split-content-loss fixed via content-preserving TerminalRenderState::resize (commit 4492b5d), unit test 7/7 PASS. See report Appendix A** | 2026-04-08 (+ 2026-04-09 amend) | plan, design (v0.1+v0.1.1+v0.2), analysis (+ Act iter 1-3), report (+ Appendix A) |
| e2e-headless-input | Completed | 95.0% (G-1 Moderate AC-2 reframed + 3 Minor follow-ups) — UIPI 오진 반박 (user insight) → RCA gate → H-RCA4 (child HWND WM_KEYDOWN→DefWindowProc) + H-RCA1 (Keyboard.Modifiers=GetKeyState) 확정, 후보 H (WinAppDriver) drop. MainWindow 단일 파일 defensive 4-scenario fix (post-simplify +56/−9), input.py PostMessage fallback 제거 (+46/−94), FlaUI PoC scaffold. **Hardware smoke 5/5 PASS** (Alt+V/H, Ctrl+T/W/Shift+W), PaneNodeTests 9/9 + VtCore 10/10 + WPF 0W/0E. Commit 1207e5f | 2026-04-09 | plan (v0.1+v0.2), design, analysis, report |
| phase-6-a-osc-notification-ring | Completed | 93% (iterate 1회, 78%→93%) — **비전 ② AI 에이전트 멀티플렉서 가설 실증**. ghostty 로컬 패치(DESKTOP_NOTIFICATION=15) + 4계층 파이프라인(C++ → C# IOscNotificationService → WPF amber dot + Win32 Toast). OSC 9/99/777 캡처 → 비활성 탭 dot 점등 → 전환 시 자동 소등. 버그 3건 발견·해결(use-after-free `&fn`, DI 순환, ViewModel getter 누락). 37파일 +2192/-101. 커밋 826768e | 2026-04-16 | prd, plan-plus, design, analysis, report |
| e2e-test-harness | Completed | 100% (8/8 PASS, 18s) — M-11.5 E2E xUnit 허브 수렴. Wave 1-3 완료 (Tier 1 FileState 2 facts + Tier 2 UiaRead 6 facts). 3-Layer 파편화(Python+FlaUI+PS1) → `tests/GhostWin.E2E.Tests` 단일 허브. Phase 6-A 선행 인프라 확보 (`TestOnlyInjectBytes` stub + `OscInjector` + `NotificationRing` AutomationId + evaluator schema 필드 2개). 구현 중 2개 버그 발견·해결: xUnit `[Collection]` 병렬 실행 충돌, FlaUI `Launch(string)` CWD 오염. Daily CI: `dotnet test --filter "Tier!=Slow"`. 커밋 c24d0d9 | 2026-04-16 | prd, plan (v0.2), design, report |
| phase-6-b-notification-infra | Completed | 97% (iterate 0회) — **비전 ② AI 에이전트 멀티플렉서 운영 인프라 완성**. Phase 6-A 확장: 알림 패널(Ctrl+Shift+I, 100건 FIFO) + AgentState 5-state 배지(●/✕/✓) + Toast 클릭→탭 전환. 버그 2건 발견·해결(ListBox 포커스 탈취→Focusable=False, 렌더 스레드 span 경쟁→방어 가드+M-14 분리). 14파일 신규4+변경10. 빌드 규칙 msbuild 의무화 추가 | 2026-04-16 | prd, plan, design, analysis, report |
| phase-6-c-external-integration | Completed | 95% (iterate 0회) — **비전 ② AI 에이전트 멀티플렉서 정밀 상태 추적 완성 + Phase 6 전체 완결**. Named Pipe 훅 서버(`\\.\pipe\ghostwin-hook`) + ghostwin-hook.exe CLI + HandleHookMessage 이벤트 라우팅(stop/notify/prompt/cwd-changed/set-status). GHOSTWIN_SESSION_ID 환경변수 C++ 주입(ConPTY build_environment_block). git branch 사이드바 5초 폴링. 15파일 신규6+변경9. GhostWin.Hook 프로젝트 추가 | 2026-04-17 | prd, plan, design, analysis, report |
