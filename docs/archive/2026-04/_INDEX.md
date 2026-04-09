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
