# E2E Test Harness — Design Document

> **Summary**: Python-based dual-agent E2E test framework. Operator (pywinauto + windows-capture WGC) + Evaluator (Claude Code Task subagent) + Orchestrator (PS1). 첫 소비자: bisect-mode-termination MQ-1~8. 20 design decisions, ~860 LOC + 60 LOC PS1.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 추가 부채 청산
> **Author**: 노수장 (Council: code-analyzer + wpf-architect + qa-strategist, CTO Lead synthesis by Opus)
> **Date**: 2026-04-07
> **Status**: Council-reviewed
> **Plan**: `docs/01-plan/features/e2e-test-harness.plan.md`

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | Plan에서 제기한 "표준 mss/pyautogui 캡처 부적합" + "WPF+HwndHost DX11 구조의 vision 평가 필요"에 더해, council 실측으로 **3가지 숨은 사실** 발견: (1) `windows-capture 1.5.0`이 **`window_name: Optional[str]`** 매개변수로 window-specific 캡처를 공식 지원 (Step 2 PoC에서 `window_hwnd` 가설은 sig 정정됨, 2026-04-08), (2) GhostWin은 **PerMonitorV2 DPI-aware** (`app.manifest` 실측, wpf-architect) → Operator 프로세스도 같은 DPI context 필수, (3) **현재 WPF 빌드에 Mica 활성화 코드 없음** (grep 확인) — CLAUDE.md의 "Mica backdrop" 언급은 WinUI3 시절 잔재. 또한 `MainWindow.OnTerminalKeyDown`이 Window 레벨에서 Alt/Ctrl 처리 → HwndHost child focus 불필요. |
| **Solution** | **`windows-capture 1.5.0` primary + dxcam fallback + PrintWindow last resort** factory pattern. `SetProcessDpiAwarenessContext(-4)` 부트스트랩 **첫 줄**. `pywinauto.send_keys('%v')` Alt+V 등 WPF 정확 시퀀스. PID-based window discovery. 3-tier 준비 시그널 (HWND → non-black pixel → 옵션 OCR prompt). Scenario chain 모드 (isolation 아님) — MQ-1→8 누적 상태 + 의존 skip. 16개 failure class taxonomy. JSON result schema + summary aggregation. |
| **Function/UX Effect** | `scripts/test_e2e.ps1 -All` 단일 명령으로 venv 자동 초기화 + 8 시나리오 순차 실행 + `artifacts/{run_id}/` 스크린샷 수집. 사용자가 Claude Code에서 Evaluator Task 호출 → JSON 결과 집계. bisect MQ-1~8 retroactive 검증 동시 수행. 향후 P0-3/P0-4도 동일 프레임워크 재사용. |
| **Core Value** | Council의 **근거 기반 실측**이 Plan의 가정을 3건 교정: (1) `windows-capture`가 window-specific 캡처를 공식 지원함을 PyPI 소스에서 확인, (2) DPI PerMonitorV2 필수성을 `app.manifest` 실측으로 증명, (3) Mica 활성화 부재를 grep으로 확증. rkit council 방법론이 **Plan 단계의 추측을 Design 단계에서 사실로 수렴**시키는 패턴을 또 입증. |

---

## 1. Council Synthesis

### 1.1 Council 분담

| Agent | 담당 범위 | 핵심 기여 | Output 규모 |
|---|---|---|---|
| `rkit:code-analyzer` | 캡처 라이브러리 deep research + 아키텍처 + Python 모듈 API + 디렉토리 구조 + venv bootstrap | windows-capture 1.5.0 window-specific 지원 확증 + WebSearch 기반 라이브러리 비교 + factory pattern 설계 + 21 files/860 LOC 추정 | ~4500 words, 20+ URL sources |
| `rkit:wpf-architect` | Win32/WPF 자동화 제약 + DPI + Mica 검증 + 키 주입 정확 시퀀스 + 창 정규화 + 준비 시그널 | PerMonitorV2 실측 + Mica 부재 확증 + `send_keys('%v')` 확정 + PID-based discovery + 3-tier readiness | ~4000 words, 8 files inspected |
| `rkit:qa-strategist` | MQ-1~8 정의 + Evaluator 프롬프트 + JSON schema + 의존 skip 정책 + failure taxonomy | 시나리오당 Pre/Actions/Expected/Pass criteria/Failure modes 완성 + 16 failure classes + scenario chain 권고 | ~5000 words, 시나리오 8개 |
| CTO Lead (Opus) | Council 통합 + Mica 불일치 검증 + D1-D20 lock-in + Risk 매트릭스 + Implementation Order | Mica 직접 grep 검증 + 20 decisions + 11 risks | 이 문서 |

### 1.2 Plan에 없던 발견 (Council Value-Add)

| # | 발견 | 출처 | Plan 영향 |
|---|---|---|---|
| C1 | `windows-capture 1.5.0`이 window-specific 캡처를 공식 지원 — **API 정정 (2026-04-08 Step 2 PoC 실측)**: 실제 시그니처는 `WindowsCapture(window_name: Optional[str], cursor_capture, draw_border)` 이며 HWND 직접 전달 불가, title 기반 lookup만 지원. PoC에서 GhostWin title `'GhostWin'`로 PASS (mean luma 30.47, 1697x1121, DX11 한글 prompt 가시) | code-analyzer (가설) → CTO Lead PoC (정정) | §1.3 capture 전략의 "PoC 필요"가 "Design에서 확정"으로 격상. **Workspace title mirroring 도입 시 가변 title 위험** → printwindow fallback이 안전망 |
| C2 | GhostWin은 **PerMonitorV2 DPI-aware** (`app.manifest:5-6`) → Operator 프로세스도 `SetProcessDpiAwarenessContext(-4)` 필수 | wpf-architect (실측) | Plan NFR에 DPI 언급 없었음, D11로 추가 |
| C3 | 현재 WPF 빌드에 **Mica 활성화 코드 없음** — `grep -rn Mica src/GhostWin.App/ --include='*.cs' --include='*.xaml'` 결과 0건. `MainWindow.xaml:9 Background="#0A0A0A"` 불투명 | wpf-architect + CTO Lead 재검증 | Plan §5 R9 "Mica backdrop 노이즈" 우려 해소 — WGC 선택 시 무관 |
| C4 | `MainWindow.OnTerminalKeyDown`이 **Window 레벨 `PreviewKeyDown`** — HwndHost child focus 불필요, 부모 window focus만 충분 | wpf-architect (`MainWindow.xaml.cs:166, 212-329`) | 키 주입 단순화 |
| C5 | `MainWindow.xaml:36-61 RestoreWindowBounds`가 persisted 크기 복원 → **매 run마다 창 크기 비결정론** | wpf-architect | 테스트 시작 전 정규화 필수 (D14) |
| C6 | **`MainWindow.OnClosing`의 Task.Run + Environment.Exit(0)** 강제 종료 → Operator가 `Process.WaitForExit()` 사용 가능, exit code 0 | wpf-architect + bisect design 교차 참조 | 종료 helper 단순화 |
| C7 | Complexity 재추정: Plan의 **300-500 LOC → 860 LOC** (capture 추상화 3개 백엔드 + 8 시나리오 + helpers) | code-analyzer | Scope 현실화 |

---

## 2. Locked-in Design Decisions (D1-D20)

### 2.1 Capture Layer

| # | Decision | Value | Rationale |
|---|---|---|---|
| **D1** | Primary capturer | **`windows-capture` 1.5.0** via `WindowsCapture(window_name=<title>, cursor_capture=False, draw_border=False)`. 호출자가 PID-based EnumWindows로 HWND를 찾고 `GetWindowTextW`로 title을 추출해서 전달 | Step 2 PoC (2026-04-08) 실측: window_name 기반 캡처가 GhostWin DX11 HwndHost 영역을 정상 캡처. mean luma 30.47, 1697x1121. R2 (DX11 child black) **CLOSED**. DWM composition tree 기반으로 가려짐/최소화 처리 |
| **D2** | Fallback capturer | **`dxcam` 0.3.0** + `win32gui.GetWindowRect` crop | DXGI Desktop Duplication — monitor 전체 후 crop. 가려짐 미해결이므로 `SetForegroundWindow` 전제 필요 |
| **D3** | Last resort | **ctypes `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=0x2)`** | DX11 black frame 가능성 높음 (추측). 실측 후 사용 여부 확정 |
| **D4** | Abstract base | **`WindowCapturer` ABC** with `name: str` + `capture(hwnd) -> PIL.Image` + `save(hwnd, path)` + `self_test()` | Factory pattern으로 런타임 선택 |
| **D5** | Factory order | WGC → dxcam → PrintWindow, **첫 성공 인스턴스를 module-level 캐시** | code-analyzer factory code |
| **D6** | 환경 변수 오버라이드 | `GHOSTWIN_E2E_CAPTURER` env var로 강제 선택 (예: `dxcam`) | 디버깅/PoC 용도 |

### 2.2 Python Infrastructure

| # | Decision | Value | Rationale |
|---|---|---|---|
| **D7** | Python 버전 | **3.12.6** (실측 확인됨) | 최소 3.9 이상이면 windows-capture 호환 |
| **D8** | Virtual env | **`scripts/e2e/venv/`** — 프로젝트 내부 | 프로젝트 격리, behavior.md PowerShell 우선과 일관 |
| **D9** | Venv bootstrap | PS1이 **SHA256 해시** 기반 requirements.txt 변경 감지 → 자동 재설치 | code-analyzer bootstrap code |
| **D10** | DPI awareness | **`ctypes.windll.user32.SetProcessDpiAwarenessContext(-4)`** (PerMonitorV2) — runner.py 첫 줄 | wpf-architect C2: `app.manifest`와 coherence 필수 |

### 2.3 WPF/Win32 Interaction

| # | Decision | Value | Rationale |
|---|---|---|---|
| **D11** | Window discovery | **PID-based `EnumWindows` + `GetWindowThreadProcessId == pid` + `IsWindowVisible`** | 타이틀("GhostWin")은 workspace mirroring 도입 예정이라 가변 (wpf-architect) |
| **D12** | Focus 확보 sequence | `keybd_event(VK_MENU, ...down...up)` + `SetForegroundWindow` + `BringWindowToTop` + `SetActiveWindow` + 50ms settle + `GetForegroundWindow == hwnd` verify (3회 retry) | Windows 11 focus stealing prevention 우회 (Alt key tap 트릭) |
| **D13** | 키 주입 API | **pywinauto `top_window().type_keys()` with escaping** — `pyautogui.hotkey` 비추 | pywinauto는 `SendInput` + scan code, pyautogui는 `keybd_event` superseded |
| **D14** | 키 매핑 | Alt+V=`'%v'`, Alt+H=`'%h'`, Ctrl+T=`'^t'`, Ctrl+W=`'^w'`, Ctrl+Shift+W=`'^+w'`, Alt+←=`'%{LEFT}'` | `MainWindow.xaml.cs:217-303` exact-match 시맨틱 준수 |
| **D15** | 창 정규화 | `SetWindowPos(hwnd, HWND_TOP, 100, 100, 1280, 800, SWP_NOZORDER\|SWP_FRAMECHANGED)` — 고정 | 모니터 독립성 + PerMonitorV2 virtualization 회피 (wpf-architect) |
| **D16** | 준비 시그널 | **Tier A (HWND visible) → Tier B (WGC frame mean luma > 0.05) → Tier C (optional prompt OCR)**. Tier B timeout = R2 (HostReady race) 의심 신호 | 3-tier readiness, bisect R2 탐지 부수 효과 |

### 2.4 Test Orchestration

| # | Decision | Value | Rationale |
|---|---|---|---|
| **D17** | 실행 방식 | **Scenario chain** (순차, 누적 상태) — not isolation | qa-strategist: 현실적 UX 흐름 재현 + 8회 재시작 오버헤드 회피 + R2 노출 확률 ↓ |
| **D18** | 의존 skip 정책 | MQ-N 실패/skip → 하위 의존 MQ 자동 skip, `summary.skipped[]`에 기록. 단 MQ-4/MQ-8은 실제 상태 확인 후 동적 판단 | qa-strategist dependency graph |
| **D19** | Evaluator 호출 방식 | **수동** — 사용자가 Operator 완료 후 Claude Code에서 `Task(e2e-evaluator, ...)` 호출 | Orchestrator가 Claude Code Task tool을 trigger 불가 (current 제한) |
| **D20** | Result schema | 8-field JSON per scenario (scenario/pass/confidence/observations/issues/failure_class/evidence) + summary.json 집계 | qa-strategist spec |

---

## 3. Architecture

### 3.1 Directory Layout

```
ghostwin/
├── scripts/
│   ├── test_e2e.ps1                    [신설 — Orchestrator]
│   ├── build_ghostwin.ps1              [수정 없음]
│   ├── test_ghostwin.ps1               [수정 없음 — 단위 테스트]
│   └── e2e/                            [신설]
│       ├── requirements.txt            [의존성 핀]
│       ├── README.md                   [사용법 (선택)]
│       ├── runner.py                   [CLI 진입점]
│       ├── operator/
│       │   ├── __init__.py
│       │   ├── app_lifecycle.py        [launch_app / shutdown_app / wait_until_ready]
│       │   ├── window.py               [find_hwnd / get_native_child / focus / normalize]
│       │   ├── input.py                [send_keys / send_text / mouse_click]
│       │   ├── capture/
│       │   │   ├── __init__.py         [get_capturer() factory]
│       │   │   ├── base.py             [WindowCapturer ABC]
│       │   │   ├── wgc.py              [WgcCapturer (windows-capture)]
│       │   │   ├── dxcam_impl.py       [DxcamCapturer]
│       │   │   └── printwindow.py      [PrintWindowCapturer]
│       │   └── scenarios/
│       │       ├── __init__.py         [SCENARIO_REGISTRY]
│       │       ├── mq1_initial_render.py
│       │       ├── mq2_split_vertical.py
│       │       ├── mq3_split_horizontal.py
│       │       ├── mq4_mouse_focus.py
│       │       ├── mq5_pane_close.py
│       │       ├── mq6_new_workspace.py
│       │       ├── mq7_workspace_switch.py
│       │       └── mq8_window_resize.py
│       ├── evaluator_prompt.md         [Evaluator subagent 프롬프트 템플릿]
│       ├── venv/                       [.gitignore]
│       └── artifacts/                  [.gitignore]
│           └── {run_id: YYYYMMDD_HHMMSS}/
│               ├── MQ-1/
│               │   ├── 01_before.png (선택)
│               │   ├── 02_after.png
│               │   └── metadata.json
│               ├── MQ-2/ ...
│               └── summary.json
└── .gitignore                          [venv/, artifacts/ 추가]
```

### 3.2 Module Dependency Graph

```
scripts/test_e2e.ps1
    │ (subprocess)
    ▼
scripts/e2e/venv/Scripts/python.exe scripts/e2e/runner.py
    │
    ├─ SetProcessDpiAwarenessContext(-4)   # D10, FIRST line
    │
    ├─ argparse: --scenario / --all / --run-id
    │
    └─ for scenario in targets:
          │
          └─ operator/scenarios/mqN_xxx.run(run_id, artifact_dir)
                │
                ├─ operator/app_lifecycle.launch_app(EXE_PATH)
                │   └─ operator/window.find_hwnd_by_pid(pid)
                │
                ├─ operator/window.normalize(hwnd, x=100, y=100, w=1280, h=800)
                │
                ├─ operator/window.focus(hwnd)   # Alt-tap trick
                │
                ├─ operator/input.send_keys(hwnd, '%v')   # via pywinauto
                │
                ├─ time.sleep(800)
                │
                ├─ operator/capture.get_capturer().save(hwnd, path)
                │      │
                │      └─ WgcCapturer (primary) / DxcamCapturer / PrintWindowCapturer
                │
                └─ operator/app_lifecycle.shutdown_app(proc, hwnd)
                       # Process.WaitForExit with timeout + force kill fallback

           artifact_dir/{run_id}/MQ-N/metadata.json    written

[User in Claude Code]:
    Task(e2e-evaluator) ← reads evaluator_prompt.md + artifact_dir/{run_id}/
        │
        ├─ Read tool: evaluator_prompt.md
        ├─ Read tool: 각 MQ-N의 *.png
        └─ JSON 결과 output → aggregated into summary.json
```

### 3.3 Python Module API (사용자 대면)

**`operator/capture/base.py`**:
```python
from abc import ABC, abstractmethod
from pathlib import Path
from PIL import Image

class WindowCapturer(ABC):
    name: str

    @abstractmethod
    def capture(self, hwnd: int) -> Image.Image: ...

    def save(self, hwnd: int, out_path: Path) -> Path:
        img = self.capture(hwnd)
        out_path.parent.mkdir(parents=True, exist_ok=True)
        img.save(out_path, format="PNG", optimize=True)
        return out_path

    def self_test(self) -> None:
        """Lightweight capability check — import smoke, no real capture."""
        ...

class CaptureError(RuntimeError): ...
```

**`operator/capture/wgc.py`** (primary, code-analyzer code):
```python
import threading, queue
from windows_capture import WindowsCapture, Frame, InternalCaptureControl
from PIL import Image
from .base import WindowCapturer, CaptureError

class WgcCapturer(WindowCapturer):
    name = "windows-capture(WGC)"

    def self_test(self) -> None:
        import ctypes
        ctypes.windll.user32.GetDesktopWindow()  # smoke

    def capture(self, hwnd: int) -> Image.Image:
        result_q: queue.Queue = queue.Queue(maxsize=1)
        # Note: windows-capture 1.5.0 supports window_name, NOT window_hwnd.
        # Caller resolves HWND→title via GetWindowTextW before passing in.
        cap = WindowsCapture(cursor_capture=False, draw_border=False, window_name=window_title)

        @cap.event
        def on_frame_arrived(frame: Frame, ctl: InternalCaptureControl):
            try:
                arr = frame.frame_buffer   # (H, W, 4) BGRA numpy
                img = Image.fromarray(arr[..., [2, 1, 0, 3]])   # BGRA → RGBA
                result_q.put(img)
            finally:
                ctl.stop()

        @cap.event
        def on_closed():
            if result_q.empty():
                result_q.put(CaptureError("WGC closed without frame"))

        ctl = cap.start_free_threaded()
        try:
            obj = result_q.get(timeout=3.0)
        except queue.Empty:
            ctl.stop()
            raise CaptureError("WGC capture timeout 3s")
        if isinstance(obj, Exception):
            raise obj
        return obj
```

**`operator/capture/__init__.py`** (factory):
```python
import logging, os
from .base import WindowCapturer, CaptureError

_CACHED: WindowCapturer | None = None

def get_capturer() -> WindowCapturer:
    global _CACHED
    if _CACHED is not None:
        return _CACHED

    forced = os.environ.get("GHOSTWIN_E2E_CAPTURER")
    candidates = [("wgc", "WgcCapturer", ".wgc"),
                  ("dxcam", "DxcamCapturer", ".dxcam_impl"),
                  ("printwindow", "PrintWindowCapturer", ".printwindow")]
    if forced:
        candidates = [c for c in candidates if c[0] == forced]

    errors = []
    for name, cls_name, mod_name in candidates:
        try:
            mod = __import__(__name__ + mod_name, fromlist=[cls_name])
            cls = getattr(mod, cls_name)
            instance = cls()
            instance.self_test()
            logging.info("capture: using %s", instance.name)
            _CACHED = instance
            return instance
        except Exception as exc:
            logging.warning("capture: %s unavailable: %r", name, exc)
            errors.append((name, repr(exc)))
    raise CaptureError(f"no capturer available: {errors}")
```

**`operator/app_lifecycle.py`**:
```python
import subprocess, time, ctypes
from ctypes import wintypes
from pathlib import Path

def launch_app(exe_path: Path, timeout_s: float = 10.0) -> tuple[subprocess.Popen, int]:
    """Start GhostWin.App.exe, wait for visible top-level HWND, return (process, hwnd)."""
    proc = subprocess.Popen([str(exe_path)])
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        hwnd = _find_top_level_hwnd_by_pid(proc.pid)
        if hwnd and ctypes.windll.user32.IsWindowVisible(hwnd):
            return proc, hwnd
        time.sleep(0.05)
    proc.kill()
    raise TimeoutError(f"GhostWin window not visible within {timeout_s}s")

def _find_top_level_hwnd_by_pid(pid: int) -> int | None:
    """EnumWindows → first top-level visible HWND owned by pid."""
    # ctypes EnumWindows callback pattern (expand in Do phase)
    ...

def wait_until_ready(hwnd: int, capturer, timeout_s: float = 5.0) -> None:
    """Tier B: poll WGC frame, assert mean luma > 0.05 within timeout."""
    import numpy as np
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        try:
            img = capturer.capture(hwnd)
            arr = np.asarray(img.convert("L"))
            if arr.mean() > 12.75:   # 0.05 * 255
                return
        except Exception:
            pass
        time.sleep(0.1)
    raise TimeoutError(f"ready signal (non-black pixel) not reached within {timeout_s}s — bisect R2 suspected")

def shutdown_app(proc: subprocess.Popen, hwnd: int, grace_s: float = 5.0) -> None:
    """WM_CLOSE → wait grace_s → terminate. MainWindow.OnClosing forces Exit(0)."""
    WM_CLOSE = 0x0010
    ctypes.windll.user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
    try:
        proc.wait(timeout=grace_s)
    except subprocess.TimeoutExpired:
        proc.terminate()
        proc.wait(timeout=2.0)
```

**`operator/window.py`**:
```python
import ctypes, time
from ctypes import wintypes

VK_MENU = 0x12
KEYEVENTF_KEYUP = 0x0002

def focus(hwnd: int) -> None:
    """Alt-tap trick to bypass Windows 11 focus stealing prevention."""
    user32 = ctypes.windll.user32
    user32.keybd_event(VK_MENU, 0, 0, 0)                # Alt down
    user32.SetForegroundWindow(hwnd)
    user32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)  # Alt up
    user32.BringWindowToTop(hwnd)
    user32.SetActiveWindow(hwnd)
    time.sleep(0.05)
    for _ in range(3):
        if user32.GetForegroundWindow() == hwnd:
            return
        time.sleep(0.1)
    raise RuntimeError(f"failed to bring HWND {hwnd:#x} to foreground")

def normalize(hwnd: int, x=100, y=100, width=1280, height=800) -> None:
    """SetWindowPos to known baseline."""
    HWND_TOP = 0
    SWP_NOZORDER = 0x0004
    SWP_FRAMECHANGED = 0x0020
    ctypes.windll.user32.SetWindowPos(
        hwnd, HWND_TOP, x, y, width, height, SWP_NOZORDER | SWP_FRAMECHANGED)
```

**`operator/input.py`**:
```python
from pywinauto import Application

def send_keys_to_hwnd(hwnd: int, keys: str, pause: float = 0.05) -> None:
    """Send keystrokes via pywinauto (SendInput + scan codes).
    keys examples: '%v' (Alt+V), '^t' (Ctrl+T), '^+w' (Ctrl+Shift+W)."""
    app = Application(backend='uia').connect(handle=hwnd, timeout=2)
    window = app.window(handle=hwnd)
    window.type_keys(keys, pause=pause, with_spaces=True, set_foreground=False)
```

Do phase에서 이 스텁을 기반으로 완전 구현 + 8 시나리오 + PS1 작성.

---

## 4. MQ Scenarios (Reference)

qa-strategist의 완전 스펙은 **Council Input: qa-strategist** (Design history / council output)에서 복사하여 Do phase에서 `operator/scenarios/*.py`의 docstring + helper에 직접 반영.

### 4.1 Scenario Summary

| # | Action | 핵심 pass criteria |
|---|---|---|
| MQ-1 | 앱 시작 | Terminal area 1개, `PS ... >` 패턴 존재 |
| MQ-2 | Alt+V | 2 pane, 세로 분할선, 각 pane 프롬프트 |
| MQ-3 | Alt+H | 3 pane, 가로 분할선, 각 pane 프롬프트 |
| MQ-4 | Mouse click | `#0091FF` focus border가 클릭한 pane으로 이동 |
| MQ-5 | Ctrl+Shift+W | Pane 수 -1, sibling이 공간 점유 |
| MQ-6 | Ctrl+T | Sidebar workspace entry +1 |
| MQ-7 | Sidebar click | 활성 workspace 전환, content 변화 |
| MQ-8 | Window resize | 모든 pane 비례 확장, 글리프 정상 (**R4 검증**) |

### 4.2 Dependency Chain

```
MQ-1 (root)
├── MQ-2
│   ├── MQ-3
│   ├── MQ-4 (independent once 2 panes exist)
│   └── MQ-5
├── MQ-6
│   └── MQ-7
└── MQ-8 (needs only MQ-1)
```

Fail/skip cascade: MQ-1 실패 → 전체 skip. MQ-2 실패 → MQ-3/MQ-4/MQ-5 skip. MQ-6 실패 → MQ-7 skip.

### 4.3 Failure Classes (16)

```python
FAILURE_CLASSES = [
    "bisect_r2_suspected",      # pane 전체 black, R2 레이스 의심
    "blank_pane",               # pane black (R2 아닌)
    "split_not_executed",       # 레이아웃 변화 없음
    "wrong_pane_count",
    "wrong_split_direction",
    "focus_border_not_visible",
    "wrong_pane_focused",
    "app_crash",
    "workspace_not_created",
    "sidebar_not_updated",
    "workspace_switch_no_effect",
    "resize_not_propagated",    # R4 검증 실패
    "text_corruption",
    "prompt_not_visible",
    "error_dialog_present",
    "unknown_failure",
]
```

---

## 5. Evaluator Prompt Template

**파일**: `scripts/e2e/evaluator_prompt.md` — Do phase에서 작성. qa-strategist §2 출력을 거의 그대로 사용.

### 5.1 핵심 구성

```markdown
# GhostWin E2E Evaluator

You are evaluating a single E2E test scenario for the GhostWin Terminal application.
Return ONLY the JSON result block — no prose before or after.

## Your Task
1. Use the Read tool to load each screenshot path listed below.
2. Visually inspect per the pass criteria.
3. Return a single JSON block matching the result schema exactly.

## Scenario Information
- Scenario ID: {scenario_id}
- Description: {scenario_description}
- Screenshots: {png_list}
- Pass criteria:
{pass_criteria}

## Output Format
{json_schema_example}

## Failure Taxonomy
[16 classes from §4.3]

## Background Knowledge
- Terminal area: `#0A0A0A` 어두운 배경 사각형 (DX11 렌더)
- PowerShell prompt: `PS C:\...> ` 구조 매칭 (한국어 경로 OK, exact string 금지)
- Focus border: `#0091FF` 2px (PaneContainerControl.cs:333-338)
- Pane split: 얇은 회색/흰색 선
- Sidebar: 좌측 세로 패널
- Mica 상태: 현재 빌드는 **flat `#0A0A0A` 불투명** (MainWindow.xaml:9 실측) — wallpaper 혼입 없음
- bisect R2 risk: 첫 pane이 black이면 `bisect_r2_suspected` 분류
- Korean locale: 경로 한국어 문자 OK
```

### 5.2 Evaluator 호출 방법 (사용자 workflow)

Orchestrator가 Operator 완료 후 아래 메시지를 stdout에 출력:

```
=== E2E Run 20260407_180000 complete ===
Artifacts: scripts/e2e/artifacts/20260407_180000/
Screenshots: 14 files across 8 scenarios

To evaluate, run in Claude Code:
  Task: e2e-evaluator
  Prompt: scripts/e2e/evaluator_prompt.md + artifact paths listed in metadata.json

Or aggregate manually from per-scenario metadata.json files.
```

사용자가 Claude Code에서 Task tool 명시 호출 (D19).

---

## 6. JSON Schemas

### 6.1 Per-Scenario Result (Evaluator output)

```json
{
  "scenario": "MQ-2",
  "pass": true,
  "confidence": 0.92,
  "observations": {
    "pane_count": 2,
    "prompt_visible_per_pane": [true, true],
    "focus_border_present": "right",
    "split_direction": "vertical",
    "sidebar_workspace_count": 1,
    "window_size_before": null,
    "window_size_after": null,
    "screen_resolution_detected": "1280x800",
    "notes": "Left pane shows 'PS C:\\Users\\solitasroh>', right pane same. Vertical split at ~x=640."
  },
  "issues": [],
  "failure_class": null,
  "evidence": "Both panes contain visible PowerShell prompts. Split boundary visible."
}
```

### 6.2 Summary Aggregation (`summary.json`)

```json
{
  "run_id": "20260407_180000",
  "feature": "bisect-mode-termination",
  "framework_version": "e2e-test-harness v0.1",
  "total": 8,
  "passed": 7,
  "failed": 1,
  "skipped": 0,
  "match_rate": 87.5,
  "capturer_used": "windows-capture(WGC)",
  "scenarios": [...],
  "failures": [
    {
      "scenario": "MQ-3",
      "failure_class": "bisect_r2_suspected",
      "evidence": "Bottom-right pane entirely black after Alt+H split."
    }
  ],
  "skipped_list": [],
  "notes": "MQ-8 resize path validated via OnPaneResized. No R2 observed."
}
```

`match_rate = passed / (total - skipped) * 100` (소수점 1자리).

---

## 7. PowerShell Orchestrator (`scripts/test_e2e.ps1`)

code-analyzer의 bootstrap pattern을 그대로 채택. SHA256-based requirements.txt change detection + venv lifecycle.

```powershell
param(
    [string]$Scenario,
    [switch]$All,
    [string]$RunId = (Get-Date -Format "yyyyMMdd_HHmmss"),
    [switch]$Rebuild
)

$ErrorActionPreference = "Stop"
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$E2ERoot    = Join-Path $RepoRoot "scripts\e2e"
$VenvDir    = Join-Path $E2ERoot  "venv"
$VenvPython = Join-Path $VenvDir  "Scripts\python.exe"
$Reqs       = Join-Path $E2ERoot  "requirements.txt"
$ReqsHash   = Join-Path $VenvDir  ".requirements.sha256"

# Korean Windows: force UTF-8 + English CLI
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:PYTHONIOENCODING = 'utf-8'

function Initialize-Venv {
    if ($Rebuild -and (Test-Path $VenvDir)) {
        Write-Host "[e2e] removing existing venv (rebuild)" -ForegroundColor Yellow
        Remove-Item -Recurse -Force $VenvDir
    }
    if (-not (Test-Path $VenvPython)) {
        Write-Host "[e2e] creating venv at $VenvDir" -ForegroundColor Cyan
        & python -m venv $VenvDir
        if ($LASTEXITCODE -ne 0) {
            throw "venv creation failed — is Python 3.12+ on PATH? See .claude/rules/behavior.md (no workaround)"
        }
    }
    $currentHash = (Get-FileHash $Reqs -Algorithm SHA256).Hash
    $cachedHash  = if (Test-Path $ReqsHash) { Get-Content $ReqsHash } else { "" }
    if ($currentHash -ne $cachedHash) {
        Write-Host "[e2e] installing requirements" -ForegroundColor Cyan
        & $VenvPython -m pip install --upgrade pip
        & $VenvPython -m pip install -r $Reqs
        if ($LASTEXITCODE -ne 0) { throw "pip install failed" }
        Set-Content -Path $ReqsHash -Value $currentHash
    }
}

function Invoke-Runner {
    $runnerPy = Join-Path $E2ERoot "runner.py"
    $args = @("--run-id", $RunId)
    if ($All) { $args += "--all" }
    elseif ($Scenario) { $args += @("--scenario", $Scenario) }
    else { throw "specify -Scenario MQ-N or -All" }
    Write-Host "[e2e] runner $($args -join ' ')" -ForegroundColor Green
    & $VenvPython $runnerPy @args
    $exit = $LASTEXITCODE

    if ($exit -eq 0) {
        Write-Host "[e2e] run complete — artifacts in $E2ERoot\artifacts\$RunId" -ForegroundColor Green
        Write-Host "Next step: in Claude Code, invoke Evaluator Task with:" -ForegroundColor Cyan
        Write-Host "  prompt: $E2ERoot\evaluator_prompt.md" -ForegroundColor Cyan
        Write-Host "  artifact dir: $E2ERoot\artifacts\$RunId" -ForegroundColor Cyan
    }
    exit $exit
}

Initialize-Venv
Invoke-Runner
```

**예시 실행**:
```powershell
scripts/test_e2e.ps1 -All
scripts/test_e2e.ps1 -Scenario MQ-1
scripts/test_e2e.ps1 -Scenario MQ-8 -Rebuild   # venv 재생성
```

---

## 8. `requirements.txt` (초안)

```text
# GhostWin E2E test harness dependencies
# Pinned versions as of 2026-04-07

# Primary capture backend (Windows.Graphics.Capture API wrapper)
windows-capture==1.5.0

# Fallback capture (DXGI Desktop Duplication)
dxcam==0.3.0

# Last-resort capture + Win32 helpers
pywin32==307

# Window automation + input injection
pywinauto==0.6.9

# Image processing (save PNG, convert formats)
Pillow==11.1.0

# Numpy (WGC returns numpy array for frame buffer)
numpy==2.2.2
```

버전 핀은 Do phase 첫 단계에서 PoC 실측 후 조정. 모든 버전은 `python -m pip index versions <pkg>`로 당일 latest stable 확인 권장.

---

## 9. Implementation Order (Do phase)

**Strict 순서**:

1. **환경 bootstrap**
   - [ ] `scripts/e2e/` 디렉토리 생성
   - [ ] `requirements.txt` 작성
   - [ ] `scripts/test_e2e.ps1` 작성 — venv 생성 테스트 (`-All` 없이 venv만 초기화 확인)

2. **Capture PoC (Critical)**
   - [ ] `scripts/e2e/capture_poc.py` 임시 스크립트 작성
   - [ ] `GhostWin.App.exe` 수동 실행 상태에서 WGC 캡처 시도
   - [ ] title 추출 + `window_name=<title>` 호출 → PNG 저장 → 시각 확인 (API 정정: window_hwnd 아님)
   - [ ] **DX11 pane 영역이 캡처되는지 육안 확인** — 성공/실패 기록
   - [ ] 실패 시 dxcam → PrintWindow 차례대로 시도
   - [ ] **PoC 결과를 Do phase 완료 조건의 첫 번째 게이트로 확정**

3. **Capture abstraction 구현**
   - [ ] `operator/capture/base.py` — ABC
   - [ ] `operator/capture/wgc.py` (또는 PoC 성공한 구현)
   - [ ] `operator/capture/__init__.py` factory
   - [ ] 간단한 smoke test: desktop HWND 캡처해서 저장

4. **App lifecycle + window helpers**
   - [ ] `operator/app_lifecycle.py` — launch_app (PID 기반 EnumWindows), shutdown_app
   - [ ] `operator/window.py` — focus (Alt-tap), normalize
   - [ ] `operator/input.py` — send_keys_to_hwnd via pywinauto
   - [ ] DPI bootstrap 코드 (runner.py 첫 줄)

5. **Scenario registry + runner.py**
   - [ ] `operator/scenarios/__init__.py` — SCENARIO_REGISTRY dict
   - [ ] `runner.py` — argparse, DPI bootstrap, 시나리오 실행 루프

6. **MQ-1 구현 (canary)**
   - [ ] `scenarios/mq1_initial_render.py`
   - [ ] Actions: launch → sleep 2s → capture (WGC)
   - [ ] 스크린샷 수집 → 시각 확인
   - [ ] **MQ-1 성공 시 나머지 시나리오 구현 착수**

7. **MQ-2~MQ-8 구현**
   - [ ] 각 시나리오 파일 작성 (qa-strategist 스펙 반영)
   - [ ] 의존 skip 정책 적용

8. **Evaluator 프롬프트 + 결과 집계**
   - [ ] `evaluator_prompt.md` 작성
   - [ ] `runner.py`가 `summary.json` 작성 (per-scenario metadata 집계)

9. **`.gitignore` 갱신**
   - [ ] `scripts/e2e/venv/` 추가
   - [ ] `scripts/e2e/artifacts/` 추가

10. **Self-test + bisect retroactive QA**
    - [ ] `scripts/test_e2e.ps1 -All` 전체 실행
    - [ ] artifacts 디렉토리 확인
    - [ ] **Claude Code에서 Evaluator Task 호출** → 8 시나리오 평가
    - [ ] bisect-mode-termination task #43 (Check) 갱신 — MQ 결과 반영

11. **Documentation**
    - [ ] CLAUDE.md Key references에 e2e-test-harness 추가
    - [ ] bisect-mode-termination 상태 완료 마킹 (Check 통과 시)

### 9.1 중단 조건

- Step 2 PoC 실패 (3개 라이브러리 모두) → **사용자 보고 + 대안 논의** (우회 금지)
- Step 6 MQ-1 캡처 결과가 black pane → R2 재현 가능성, bisect hotfix 먼저 고려
- Step 10 전체 실행 시 8건 중 4건 이상 fail → 근본 원인 분석 후 재시도

---

## 10. Risks and Mitigation

| # | Risk | Impact | Likelihood | Mitigation |
|---|---|:---:|:---:|---|
| R1 | `windows-capture` 1.5.0 wheel이 Python 3.12.6에 설치 안 됨 (binary wheel 호환 이슈) | **Critical** | Low | Step 2 PoC에서 실측. 실패 시 Python 3.11로 다운그레이드 또는 source build |
| R2 | WGC가 GhostWin의 부모 HWND 캡처 시 native HwndHost child 영역이 black (DWM composition tree 미포함) | **Critical** | **CLOSED** (2026-04-08) | **Empirically verified**: Step 2 PoC에서 WGC `window_name='GhostWin'`로 1697x1121 캡처, mean luma 30.47 (>> threshold 12.75), DX11 PowerShell prompt + cyan focus border + 한글 경로 모두 가시. PrintWindow + PW_RENDERFULLCONTENT도 동등 PASS (luma 30.56, NC 영역 추가). dxcam만 DPI 좌표 ValueError 잔존 (Step 3에서 fix). Evidence: `scripts/e2e/poc_artifacts/` |
| R3 | PerMonitorV2 DPI bootstrap 누락 → GetWindowRect가 잘못된 좌표 반환 → 캡처 영역 shift | High | Medium | D10: runner.py 첫 줄 `SetProcessDpiAwarenessContext(-4)`. 부트스트랩 누락 시 명시적 assert |
| R4 | pywinauto `type_keys('%v')`가 WPF `OnTerminalKeyDown`에 도달 안 함 (SystemKey 매핑 이슈) | High | Low | wpf-architect 실사례 없음. 실패 시 ctypes `SendInput` 직접 호출로 fallback |
| R5 | 초기 pane HostReady 레이스 (bisect R2)가 MQ-1 캡처에서 black으로 나타남 | High | Medium | **이는 위험이 아니라 가치** — 프레임워크가 bisect 잔여 버그를 발견. `bisect_r2_suspected` 분류 |
| R6 | `SetWindowPos(100,100,1280,800)` 후 `MainWindow.RestoreWindowBounds`가 다시 실행되어 덮어씀 | Medium | Low | `RestoreWindowBounds`는 `OnSourceInitialized`에서 1회만 호출 (wpf-architect). Normalize는 그 이후이므로 안전 |
| R7 | Evaluator subagent의 PNG Read가 토큰 한도 초과 | Medium | Medium | 스크린샷 해상도 1280x800 유지 (다운스케일 불필요). 시나리오당 2~3장 제한 |
| R8 | 앱이 CLAUDE.md에 명시된 Mica 가정을 나중에 활성화하면 Evaluator 배경 지식 stale | Low | Low | WGC 사용 시 wallpaper 혼입 없음 (DWM composition). Mica 활성화해도 영향 최소 |
| R9 | Process cleanup 실패로 orphan GhostWin.App.exe 다수 잔존 | Medium | Medium | Scenario chain 모드이므로 프로세스 1개 유지. 시나리오 실패 시 `taskkill /F /IM GhostWin.App.exe` 강제 정리 |
| R10 | `MainWindow.OnClosing`의 `Environment.Exit(0)`가 pywinauto `connect(handle=hwnd)` 후 HWND 무효화 → AccessViolation | Medium | Low | 시나리오 종료 시 pywinauto Application 참조 해제 + `proc.wait(timeout)` 후 처리 |
| R11 | 사용자 마우스가 시나리오 실행 중 움직여 pyautogui와 충돌 | High | Medium | PS1 시작 시 경고 메시지: "E2E 실행 중 마우스/키보드 방치" |
| R12 | Windows 11 focus stealing prevention이 Alt-tap 트릭에도 실패 | High | Low | Mitigation 3회 retry + 실패 시 `AttachThreadInput` fallback |

### 10.1 Critical Risk (R1 + R2) 대응 우선순위

**Step 2 PoC 실패 시 즉각 사용자 보고**:
1. R1 실패 (`pip install` 불가) → Python 버전 변경 문의
2. R2 실패 (WGC가 DX11 영역 캡처 안 됨) → dxcam + `SetForegroundWindow` 조합 시도. 이것도 실패 시 **Claude Code에서 MCP 도구 (Puppeteer/Playwright 유사) 활용 방안 논의**

---

## 11. Open Questions (Do phase에서 해결)

1. **[최우선] PoC 결과**: WGC/dxcam/PrintWindow 중 어느 것이 GhostWin DX11 HwndHost 영역을 캡처하는가? `capture_poc.py` 실행 후 확정.
2. **부모 HWND vs 자식 HWND**: WGC로 GhostWin.App 부모 HWND를 캡처할지, 아니면 `EnumChildWindows`로 terminal child HWND만 개별 캡처할지 — PoC로 결정.
3. **SettingsService persistence path**: test run 시작 전 `appsettings.user.json`을 wipe해야 창 크기/sidebar width가 결정론적. 경로 Do phase 확인.
4. **CLAUDE.md Mica 주장 수정**: wpf-architect + CTO Lead가 "Mica 활성화 코드 없음" 실측 확인. CLAUDE.md 해당 기술을 별도 chore로 수정 필요 (본 feature 외).
5. **Evaluator 호출 자동화**: 현재 D19는 수동. 향후 MCP 도구 또는 `claude-code` CLI 간접 호출 가능 여부 — 향후 개선.
6. **Prompt OCR (Tier C)**: 준비 시그널의 세 번째 tier로 OCR 도입할지 — 필수 아님, 필요 시 tesseract 추가.

---

## 12. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-07 | Initial design. Council synthesis: code-analyzer (capture library research + architecture) + wpf-architect (DPI, Mica 검증, 키 주입, readiness) + qa-strategist (MQ spec + Evaluator prompt + JSON schema). D1-D20 lock-in. Plan 3건 가정 교정 (C1-C3). | 노수장 (CTO Lead) |
| 0.1.1 | 2026-04-08 | **D1 API patch** (Step 2 PoC empirical 결과 반영): `windows-capture 1.5.0` 시그니처 정정 `window_hwnd` → `window_name`. C1 정정, R2 CLOSED, §3.3 wgc.py 코드 스텁 + Implementation Order Step 2 호출 패턴 정정. PoC evidence: `scripts/e2e/poc_artifacts/`. 다른 결정(D2-D20) 모두 그대로 유효 | 노수장 (CTO Lead) |

---

## Council Attribution

| Agent | 핵심 기여 |
|-------|---------|
| `rkit:code-analyzer` | **Critical**: `windows-capture 1.5.0` window-specific 캡처 지원 확인 → Primary capture 확정 (API 시그니처는 Step 2 PoC에서 `window_name` 정정). DXcam/PrintWindow 비교 연구. 21 files / 860 LOC 추정. Factory pattern + venv bootstrap code. 4500 words, 20+ sources |
| `rkit:wpf-architect` | **Critical 3건**: (1) PerMonitorV2 DPI 실측 → D10 runner.py 첫 줄, (2) **Mica 부재 grep 검증** → Plan R9 우려 해소, (3) `OnTerminalKeyDown`이 Window-level이라 child focus 불요. PID-based discovery, Alt-tap 트릭, 3-tier readiness |
| `rkit:qa-strategist` | MQ-1~8 완전 스펙 + 16 failure classes + Evaluator prompt 템플릿 + JSON schema (per-scenario + summary) + scenario chain 권고 + 의존 skip 정책 |
| CTO Lead (Opus) | Mica 주장 재검증 (grep → wpf-architect 확증), D1-D20 통합, R1/R2 Critical risk 대응 전략, Implementation Order 11단계, 프레임워크가 자기 자신을 검증하는 retroactive QA 흐름 설계 |
