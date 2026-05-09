# E2E Bash Session Capabilities — MQ Scenario 실행 가능 여부

> **Last updated**: 2026-04-14 (BC-12)
> **Context**: Claude Code bash 세션에서 `scripts/e2e/` 테스트 harness 와 `scripts/repro_first_pane.ps1` 실행 가능 여부를 최종 정리. e2e-headless-input T-Main (2026-04-09) + Pre-M11 BC-01/02/03/04 (2026-04-14) 이후 상태.

## 결론 요약

| Scenario | bash 세션 | Interactive 세션 | 주요 의존성 |
|----------|:--------:|:---------------:|------------|
| MQ-1 initial render | ✅ | ✅ | 없음 (launch + screenshot) |
| MQ-2 split vertical (Alt+V) | ⚠️ | ✅ | **SendInput** + WPF Alt-chord |
| MQ-3 split horizontal (Alt+H) | ⚠️ | ✅ | SendInput + WPF Alt-chord |
| MQ-4 mouse focus | ❌ | ✅ | 실제 마우스 입력 |
| MQ-5 pane close (Ctrl+Shift+W) | ⚠️ | ✅ | SendInput + WPF Ctrl+Shift-chord |
| MQ-6 new workspace (Ctrl+T) | ⚠️ | ✅ | SendInput + WPF Ctrl-chord (T-Main 수혜) |
| MQ-7 workspace switch (sidebar click) | ❌ | ✅ | 실제 마우스 입력 |
| MQ-8 window resize | ✅ | ✅ | WM_SIZE (SendInput 불필요) |

범례:
- ✅: bash 세션에서 정상 실행
- ⚠️: SendInput 이 H-RCA1/H-RCA4 영향 받음 — 실측 미완
- ❌: 실제 마우스 입력 필요 (bash 에서 injection 불가)

## 인프라 상태 (2026-04-14)

### 캡쳐

| Capturer | 상태 | 용도 |
|----------|------|------|
| **PrintWindow** (`printwindow.py`) | ✅ **기본값 권장** | PoC 검증 luma 30.56, venv pywin32, AMSI 미해당 |
| WGC (`wgc.py`) | ⚠️ | DWM composition 요구, bash 세션 부적합 |
| dxcam (`dxcam_impl.py`) | ⚠️ | 3rd party, 일부 GPU 호환성 이슈 |
| PowerShell `CopyFromScreen` (`repro_first_pane.ps1`) | ✅ **fallback only** | 전체 화면 캡쳐. window-only 아님. BC-01 에서 PrintWindow helper 로 교체됨 |

### 입력 (SendInput)

- **bash 세션**: `scripts/e2e/e2e_operator/input.py` 의 `send_keys` (ctypes SendInput batch) 사용. H-RCA1/H-RCA4 로 인해 modifier-chord 실패 가능성 있음.
- **T-Main 완화 (2026-04-09)**: `MainWindow.xaml.cs` bubble-phase handler + `IsCtrlDown/IsShiftDown/IsAltDown` helper → SendInput-injected Ctrl+T 구조 가능성 증가. 사용자 hardware smoke 5/5 PASS 확인됨.
- **T-6 (2026-04-09)**: PostMessage fallback 제거 — `OSError` loud 발생 (조용한 거짓 pass 방지)

### 진단 로그

- `KeyDiag` — `GHOSTWIN_KEYDIAG=3` 활성 시 `%LocalAppData%\GhostWin\diagnostics\keyinput.log`
- `RenderDiag` — `GHOSTWIN_RENDERDIAG=3` 활성 시 `%LocalAppData%\GhostWin\diagnostics\render_{yyyyMMdd}.log`
- **BC-03 dedupe** (2026-04-14): bubble 재진입 시 중복 ENTRY 제거 (ThreadStatic sentinel)
- **BC-04 keybind instrument** (2026-04-14): Ctrl-branch T/W/Tab 에서 `evt=KEYBIND` 라인 emit

## 권장 실행 방법

### bash 세션 (CI / Claude Code)

```powershell
# 기본 수행 가능 scenario (MQ-1, MQ-8)
GHOSTWIN_E2E_CAPTURER=printwindow scripts/test_e2e.ps1 -Scenario MQ-1
scripts/test_e2e.ps1 -Scenario MQ-8

# repro script (window-only capture, BC-01 이후 자동 사용)
scripts/repro_first_pane.ps1 -Iterations 30 -DelayMs 2000

# SendInput 의존 scenario — 실측 필요 (T-Main 이후 재평가)
GHOSTWIN_E2E_CAPTURER=printwindow scripts/test_e2e.ps1 -All
# → 결과가 의심스러우면 interactive 세션에서 재실행
```

### Interactive 세션 (사용자 hardware, 최종 검증)

```powershell
# 전체 scenario
scripts/test_e2e.ps1 -All

# FlaUI cross-validation (BC-05)
dotnet run --project tests/e2e-flaui-cross-validation -c Release
```

## 관련 이력

- `docs/archive/2026-04/e2e-headless-input/` — T-Main / T-6 RCA 와 수정
- `docs/archive/2026-04/e2e-evaluator-automation/` — Claude Code 평가 자동화
- `docs/archive/2026-04/first-pane-render-failure/` — repro script 원본 (G1/G2/G3 false-negative)
- `~/.claude/projects/.../memory/feedback_e2e_bash_session_limits.md` — 메모리 요약 (2026-04-09 v2)

## 다음 재평가 시점

- Sub-cycle `vt-mutex-redesign` / `io-thread-timeout-v2` / `dpi-scaling-integration` 완료 후
- M-11 Session Restore 착수 전 (본 마일스톤 종료 시점)
