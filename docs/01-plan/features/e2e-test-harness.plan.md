# E2E Test Harness — Planning Document

> **Summary**: Python-based dual-agent E2E test framework. Pre-scripted Operator (pywinauto + pyautogui + mss)가 시나리오 조작 + 스크린샷 수집, Claude Code Task subagent Evaluator가 PNG를 읽고 pass/fail 판정. 첫 소비자는 bisect-mode-termination MQ-1~8 retroactive 검증.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 추가 부채 청산 (10-agent v0.5 §1 C3 "자동화 테스트 부재" — unit 이후 E2E 계층 보강)
> **Author**: 노수장
> **Date**: 2026-04-07
> **Status**: Draft
> **Previous**:
> - `docs/04-report/core-tests-bootstrap.report.md` — 단위 테스트 계층 확보 (PaneNode 9/9)
> - `docs/01-plan/features/bisect-mode-termination.plan.md` — Do 완료, QA 보류
> - `docs/03-analysis/pane-split-workspace-completeness-v0.5.md` §1 C3 qa-strategist F(7%)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | core-tests-bootstrap로 단위 테스트(7% → PaneNode 100%) 기반은 확보했으나, **UI/통합 계층 자동화는 여전히 0%**. 추가로, **표준 `mss`/`pyautogui.screenshot()` 캡처는 GhostWin에 부적합** — 모니터 좌표 기반이라 target 창 위의 다른 창을 캡처하고, 터미널 렌더 영역이 **native HWND + DX11 swap chain**이라 GDI 기반 `PrintWindow`로도 swap chain back buffer를 못 봄. `bisect-mode-termination`의 MQ-1~8 같은 수동 QA가 feature마다 반복 비용이 높고, `OnHostReady` silent failure 같은 잠재 이슈는 수동 클릭만으로는 안정적으로 검증 불가. |
| **Solution** | **Dual-agent pattern**: (1) Operator = Python 스크립트(`scripts/e2e/operator.py`), pywinauto/pyautogui로 **target window 명시 조작** + **Windows.Graphics.Capture API (WGC) 기반** `windows-capture` 또는 `dxcam` (DXGI Desktop Duplication)으로 **HWND 정밀 캡처** (DX11 back buffer 포함). (2) Evaluator = Claude Code Task subagent, Read tool로 PNG 로드 + vision 평가 + JSON 반환. (3) Orchestrator = PS1 스크립트(`scripts/test_e2e.ps1`). 첫 소비자는 bisect-mode-termination **MQ-1~8** retroactive 검증. **캡처 라이브러리 최종 선택은 Design 단계 PoC로 결정** (WGC vs DXGI vs PrintWindow+PW_RENDERFULLCONTENT). |
| **Function/UX Effect** | 사용자 가시 변경 0. 개발자 관점: (a) `scripts/test_e2e.ps1` 단일 명령으로 bisect MQ-1~8 자동 실행 (~수 분 예상, 확실하지 않음), (b) 향후 P0-3/P0-4 및 모든 Phase 5 feature가 동일 프레임워크 사용, (c) regression 검출이 수동 → 자동으로 전환되어 feature 회전율 상승, (d) HwndHost 영역의 시각 정합성(한글 렌더, 분할 경계, focus 테두리 등)을 vision LLM이 의미적으로 판독. |
| **Core Value** | "**테스트 인프라 있음 vs 없음**"의 두 번째 0→1 전환 (단위 → E2E). core-tests-bootstrap는 PaneNode 순수 로직을 방어했고, 본 feature는 **PaneNode와 엔진 DLL 사이의 integration + 실제 렌더링 결과**를 방어. 10-agent 평가 qa-strategist F 등급에서 "C 등급"으로 진입 (확실하지 않음 — 정량화 어려움). rkit dual-agent 방법론을 **제품 QA에 직접 적용**하는 최초 사례. |

---

## 1. Overview

### 1.1 Purpose

GhostWin의 UI/렌더링 레이어를 **재현 가능하고 시각적으로 검증된 방식**으로 자동 테스트하기 위한 프레임워크 구축. 첫 사용처는 bisect-mode-termination의 MQ-1~8 수동 QA 8건의 retroactive 자동화.

**왜 "별도 feature"인가**:
- 본 feature 자체는 GhostWin의 product 기능이 아니라 **개발 infra**
- bisect-mode-termination의 scope에 포함시키면 두 feature의 scope가 섞여 책임 경계 흐려짐
- 향후 P0-3/P0-4 및 Phase 5-F/5-G 등에서도 동일 프레임워크를 재사용할 자본재
- PDCA 관점에서 별도 cycle로 추적하는 것이 ROI 측정에도 유리

### 1.2 Why Dual-Agent + Why Window-Precise Capture

WPF + HwndHost 아키텍처의 특수성:

| 영역 | 구조 | pywinauto UI tree | mss/pyautogui 캡처 | 검증 방법 |
|---|---|---|---|---|
| Title bar, Sidebar, Menu | WPF visual tree | ✅ 접근 가능 | ✅ OK | UI element + 시각 |
| **Terminal rendering** (pane 내부) | **native HWND + DX11 swap chain** | ❌ **opaque** | ❌ **DX11 back buffer 보이지 않음** | **WGC/DXGI 캡처 + 시각 인식 필수** |
| Focus border | WPF Border | ✅ | ✅ | UI 또는 시각 |
| Pane 분할 경계 | WPF Grid | ✅ | ✅ | UI 또는 시각 |
| 한글 텍스트 렌더 | DirectWrite + DX11 | ❌ | ❌ | **WGC 캡처 + 시각 인식 필수** |

**표준 mss/pyautogui의 근본 한계** (사용자 지적):

1. **모니터 좌표 기반 캡처** → target 창 위에 다른 창이 오면 그것을 캡처
2. **최소화/오클루전 상태에서 capture 불가**
3. **GDI-based PrintWindow는 DX11 swap chain을 못 봄** (DXGI front buffer만 가능하며 compositional issue)
4. **WPF custom WindowChrome + Mica backdrop**이 screen coord 계산에 오프셋 발생 (확실하지 않음)

### 1.3 Capture Library Strategy

본 feature의 **가장 중요한 기술 의사결정**은 HWND 정밀 캡처 라이브러리 선택. 후보 4개:

| # | 방법 | API 레벨 | DX11 지원 | Python 라이브러리 | 상태 |
|---|---|---|:---:|---|---|
| A | **Windows.Graphics.Capture API (WGC)** | Win10 1803+, OS 정식 API. 스니핑 도구/OBS/Snagit이 사용 | ✅ | `windows-capture` (PyPI) | **권장 primary** |
| B | **DXGI Desktop Duplication** | DX API, GPU-level full desktop 캡처 후 crop | ✅ | `dxcam` (ra1nty/DXcam) | **권장 fallback** |
| C | `PrintWindow` + `PW_RENDERFULLCONTENT` flag 0x2 | Win8.1+, 기존 GDI 확장 | 부분 (확실하지 않음) | `pywin32` + ctypes | 테스트 대상 |
| D | `mss` + `GetWindowRect` 크롭 + foreground 강제 | 모니터 전체 캡처 후 crop | ❌ (DX 불안정) | `mss` + `pywinauto` | **비추** (기존 패턴의 문제) |

**Design 단계 PoC 순서**:
1. Option A `windows-capture` — GhostWin에 대해 `capture_by_window(hwnd)` 호출 → PNG 저장 → pane 내 DX 렌더 내용이 보이는지 확인
2. 실패 시 Option B `dxcam` — 전체 데스크탑 캡처 후 `GetWindowRect`로 크롭
3. 실패 시 Option C `PrintWindow + PW_RENDERFULLCONTENT`
4. 모든 옵션 실패 시 **사용자에게 보고 + 대안 논의** (우회 금지 원칙)

Terminal 내부 렌더링이 native 영역이므로 rule-based OCR도 신뢰도가 낮음 (ClearType/CJK advance-centering 등). Vision LLM의 시각적 이해가 가장 강건한 평가 수단.

### 1.3 Related Documents

- `docs/01-plan/features/bisect-mode-termination.plan.md` — 첫 소비 시나리오 출처
- `docs/02-design/features/bisect-mode-termination.design.md` §5.2 — MQ-1~8 테이블 (원본 spec)
- `docs/03-analysis/pane-split-workspace-completeness-v0.5.md` §1 C3 — qa-strategist F 평가
- `scripts/run_wpf.ps1` — 기존 앱 실행 스크립트 (Operator가 참조)
- `scripts/build_ghostwin.ps1` — PS1 스크립트 스타일 참조
- `src/GhostWin.App/bin/x64/Release/net10.0-windows/GhostWin.App.exe` — 대상 바이너리

---

## 2. Scope

### 2.1 In Scope

**Python 환경 구성**
- [ ] `scripts/e2e/` 디렉토리 신설
- [ ] `scripts/e2e/requirements.txt` — 의존성 핀. 후보: `pywinauto` (window find + input), `windows-capture` (WGC primary), `dxcam` (DXGI fallback), `pywin32` (ctypes baseline), `Pillow` (이미지 처리)
- [ ] Virtual environment 전략 — 첫 실행 시 자동 생성 + 활성화 (PS1 스크립트가 처리)
- [ ] Python 3.12.x 가정 (`python --version` → 3.12.6 확인됨)

**HWND 정밀 캡처 PoC** (Design 전제 조건)
- [ ] `scripts/e2e/capture_poc.py` — Option A/B/C를 각각 시도하여 GhostWin의 DX11 렌더 영역이 캡처되는지 실측
- [ ] PoC 결과를 Design 문서에 근거로 인용 (어느 라이브러리가 작동했는가)
- [ ] 선택된 라이브러리만 `requirements.txt`에 정식 등재

**Operator 구현**
- [ ] `scripts/e2e/operator.py` — 시나리오 함수 라이브러리
- [ ] 앱 실행 헬퍼: `launch_app()` — `GhostWin.App.exe` subprocess 시작, main window handle 획득까지 대기
- [ ] 종료 헬퍼: `shutdown_app()` — graceful close (Alt+F4) + process kill fallback
- [ ] 스크린샷 헬퍼: `capture(tag, window_handle)` — mss로 윈도우 영역 캡처 → `artifacts/{run_id}/{scenario}_{tag}.png`
- [ ] 시나리오 함수 8개:
  - `scenario_mq1_initial_render()` — 앱 시작 후 초기 workspace/pane이 프롬프트 표시할 때까지 대기, before/after 스크린샷
  - `scenario_mq2_split_vertical()` — Alt+V, 2 pane 상태 캡처
  - `scenario_mq3_split_horizontal()` — Alt+H, 3 pane 상태 캡처
  - `scenario_mq4_mouse_focus()` — 마우스 click pane 전환, focus border 캡처
  - `scenario_mq5_pane_close()` — Ctrl+Shift+W focused pane close, sibling 재배치 캡처
  - `scenario_mq6_new_workspace()` — Ctrl+T, sidebar 새 entry 확인
  - `scenario_mq7_workspace_switch()` — sidebar click workspace 전환
  - `scenario_mq8_window_resize()` — **Top Risk 4 대응**: 창 크기 조절 후 pane 리사이즈 검증

**Evaluator 구현**
- [ ] `scripts/e2e/evaluator_prompt.md` — Evaluator subagent 프롬프트 템플릿 (Task tool에 전달)
- [ ] 평가 JSON 스키마: `{"scenario": "MQ-1", "pass": true/false, "evidence": "...", "issues": [...]}`
- [ ] 시나리오별 pass criteria 정의 (MQ-1: "PowerShell prompt 문자열이 pane 영역에 보인다" 등)

**Orchestrator 구현**
- [ ] `scripts/test_e2e.ps1` — 전체 파이프라인
  1. venv 초기화 (최초 1회) + 활성화
  2. requirements.txt 설치 확인
  3. 각 시나리오마다:
     - Operator 실행: `python scripts/e2e/runner.py --scenario MQ-1`
     - 종료 후 artifacts/{run_id}/ 에 스크린샷 확인
  4. 모든 시나리오 완료 후 결과 요약 JSON 생성
- [ ] `scripts/e2e/runner.py` — CLI 진입점 (`--scenario` 인자로 단일 시나리오 실행)
- [ ] 아티팩트 디렉토리 구조:
  ```
  scripts/e2e/artifacts/
  └── {run_id: YYYYMMDD_HHMMSS}/
      ├── MQ-1_before.png
      ├── MQ-1_after.png
      ├── MQ-2_before.png
      ├── ...
      └── summary.json
  ```

**통합 & 검증**
- [ ] 본 feature의 Check phase에서 **자기 자신을 사용해** bisect-mode-termination MQ-1~8 실행 (retroactive QA)
- [ ] 결과가 9/9 PaneNode + 8/8 MQ PASS면 bisect-mode-termination Check phase 동시 완료
- [ ] CLAUDE.md Phase 5-E.5 섹션에 P0-2 완료 상태 업데이트 (MQ 결과 반영)

**.gitignore 갱신**
- [ ] `scripts/e2e/venv/` 추가 (virtual environment 커밋 금지)
- [ ] `scripts/e2e/artifacts/` 추가 (스크린샷 커밋 금지, 로컬 아티팩트)

### 2.2 Out of Scope

- **CI 파이프라인 통합** — GitHub Actions 등 별도 feature
- **Parallel scenario execution** — 단일 앱 인스턴스로 직렬 실행만
- **Video recording** — still frame만, MP4 녹화는 YAGNI
- **Custom HTML test report UI** — summary.json이면 충분
- **Cross-platform** — Windows 전용 (pywinauto가 Win32 specific)
- **Reusable generic framework** — GhostWin-specific 고정 (scope 확정됨)
- **LLM-driven 동적 operator** — Pre-scripted 고정
- **Unit test 시나리오** — core-tests-bootstrap이 담당
- **C# FlaUI 연동** — Python 단일 경로
- **Pixel-perfect diff regression** — vision LLM의 의미적 평가만
- **Input record & replay** — 하드코딩된 sequence만
- **pytest 통합** — Python test runner로 pytest 사용하지 않음 (단순 CLI 스크립트)
- **Operator 또는 Evaluator의 Anthropic API 직접 호출** — Claude Code Task tool을 통해서만 Evaluator 호출

### 2.3 Out of Scope 근거 요약

각 Out of Scope 항목이 **YAGNI** 원칙에 부합함을 명시: 현재 요구(bisect MQ-1~8 + 향후 유사 시나리오)로는 단일 앱 인스턴스 직렬 실행이 충분하고, CI/비디오/reusable framework는 **근거 없는 예측**에 의한 확장.

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | `scripts/e2e/` 디렉토리 구조 신설 (operator.py, runner.py, evaluator_prompt.md, requirements.txt) | High | Pending |
| FR-02 | `launch_app()` — GhostWin.App.exe subprocess 시작 + main window handle 대기 | High | Pending |
| FR-03 | `capture(tag, hwnd)` — mss로 윈도우 영역 PNG 캡처 | High | Pending |
| FR-04 | `shutdown_app()` — graceful close + fallback kill | High | Pending |
| FR-05 | MQ-1 시나리오: 초기 렌더 — before/after 캡처 | High | Pending |
| FR-06 | MQ-2 시나리오: Alt+V split | High | Pending |
| FR-07 | MQ-3 시나리오: Alt+H split | High | Pending |
| FR-08 | MQ-4 시나리오: 마우스 click pane focus | High | Pending |
| FR-09 | MQ-5 시나리오: Ctrl+Shift+W pane close | High | Pending |
| FR-10 | MQ-6 시나리오: Ctrl+T new workspace | High | Pending |
| FR-11 | MQ-7 시나리오: sidebar click workspace switch | High | Pending |
| FR-12 | MQ-8 시나리오: **창 리사이즈** (bisect Top Risk 4 검증) | **Critical** | Pending |
| FR-13 | `runner.py --scenario MQ-N` CLI | High | Pending |
| FR-14 | `scripts/test_e2e.ps1` PS1 wrapper, venv 자동 초기화 | High | Pending |
| FR-15 | Evaluator 프롬프트 템플릿 + 시나리오별 pass criteria | High | Pending |
| FR-16 | `summary.json` 생성 — 각 시나리오의 screenshot 경로 + 메타 | Medium | Pending |
| FR-17 | `.gitignore`에 venv/ 및 artifacts/ 추가 | Medium | Pending |
| FR-18 | Check phase에서 프레임워크 자기 자신으로 bisect MQ-1~8 retroactive 실행 | **Critical** | Pending |

### 3.2 Non-Functional Requirements

| Category | Criteria | Measurement |
|----------|----------|-------------|
| 실행 시간 | MQ-1~8 전체 < 5분 (wall clock, 확실하지 않음 — 수동 측정 후 조정) | `Measure-Command` |
| 결정론 | 동일 입력 시 3회 연속 동일 pass/fail 결과 | 수동 반복 |
| 독립성 | Python 단독 실행 가능 (`scripts/test_e2e.ps1` 외 의존 없음) | 타 머신 실행 테스트는 out of scope |
| 가독성 | 각 시나리오 함수가 30 lines 이내 + docstring 포함 | 코드 리뷰 |
| 라이선스 | pywinauto (BSD), pyautogui (BSD), mss (MIT), Pillow (MIT-CMU) | 패키지 메타 |
| 한글 Windows | 앱이 한국어 locale에서 정상 실행 + 스크린샷 mojibake 없음 | 수동 확인 |
| 아티팩트 격리 | 각 run이 고유 timestamp 디렉토리 생성, 덮어쓰기 없음 | 파일시스템 확인 |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] `scripts/e2e/` 디렉토리 + 모든 파일 생성
- [ ] `scripts/test_e2e.ps1 -Scenario MQ-1` 단일 시나리오 실행 → 스크린샷 수집 → Evaluator PASS
- [ ] `scripts/test_e2e.ps1 -All` 모든 시나리오 순차 실행 → summary.json 생성
- [ ] bisect-mode-termination MQ-1~8 **8/8 PASS** (retroactive validation)
- [ ] 기존 `scripts/test_ghostwin.ps1` PaneNode 9/9 **회귀 0**
- [ ] 기존 `scripts/build_ghostwin.ps1` 빌드 **회귀 0**
- [ ] `.gitignore` venv/ artifacts/ 추가, 신규 Python 소스만 커밋
- [ ] CLAUDE.md Phase 5-E.5 P0-2 완료 상태 업데이트

### 4.2 Quality Criteria

- [ ] gap-detector Match Rate ≥ 90%
- [ ] Python 파일 lint 에러 0 (ruff 또는 flake8 사용 시 — 선택)
- [ ] 각 시나리오 함수의 Arrange/Act/Assert 구분 주석
- [ ] `.claude/rules/behavior.md` "우회 금지" 준수 — 시나리오 실패 시 근본 원인 분석

---

## 5. Risks and Mitigation

| # | Risk | Impact | Likelihood | Mitigation |
|---|------|:---:|:---:|------------|
| R1 | pywinauto의 WPF UI tree 인식 제한 — HwndHost child는 탐색 불가 | High | **High** | 의도된 동작. 시각 인식으로 우회. pywinauto는 main window 찾기/키 전송 용도로만 사용, UI element 쿼리는 사용 안 함 |
| R2 | GhostWin 앱이 비표준 WindowChrome (Mica, custom titlebar) 사용 → pywinauto `Application().connect()` 실패 | **High** | Medium | `pyautogui.getWindowsWithTitle("GhostWin")` 또는 Win32 `FindWindow`로 fallback. Design 단계에서 실측 |
| R3 | 한국어 Windows + 특수 포커스 상태에서 pyautogui 키 이벤트가 Alt 등 시스템 키를 엉뚱한 대상에 전달 | Medium | Medium | `pyautogui.hotkey('alt', 'v')` 대신 `pywinauto.keyboard.send_keys('%v')` 사용 (target window 명시) |
| R4 | Evaluator subagent가 PNG를 Read tool로 읽을 때 토큰 소비량 폭증 | Medium | Medium | 스크린샷을 1920x1080에서 960x540으로 다운스케일 (Pillow). 시나리오당 2~3장 제한 |
| R5 | 앱 종료가 graceful하지 않아 orphan 프로세스 → 다음 시나리오 간섭 | High | Medium | 각 시나리오 시작 전 `taskkill /IM GhostWin.App.exe /F` 강제 정리 (옵션 플래그로) |
| R6 | 스크린샷 캡처 타이밍이 렌더링보다 빨라 빈 화면 캡처 | High | **High** | 각 액션 후 `time.sleep(N)` + 더 안전하게 "쿼리 polling" 가능한 지점은 pywinauto로 대기. 시나리오별 wait 상수 정의 |
| R7 | pyautogui 마우스 이동이 사용자 마우스 물리적으로 움직임 → 사용자가 앱 쓸 때 충돌 | **High** | Low | 개발자 공지: "E2E 실행 중 마우스/키보드 방치". Orchestrator 시작 시 경고 메시지 |
| R8 | venv 경로가 프로젝트 외부 드라이브(D:)에 있으면 상대 경로 문제 | Low | Low | `scripts/e2e/venv/` 프로젝트 내부 배치 |
| R9 | **[Critical, 사용자 지적]** 표준 스크린샷 (mss/pyautogui)이 target window만 정확히 캡처하지 못함 — 오버랩된 다른 창 포함, DX11 swap chain 내용 미포함. GhostWin은 native HwndHost child라 GDI 기반 PrintWindow도 불안정. | **Critical** | **High** | **§1.3 Capture Library Strategy** — Design 단계에서 `windows-capture` (WGC) primary → `dxcam` (DXGI) fallback → `PrintWindow+PW_RENDERFULLCONTENT` last resort 순서로 PoC 실측. 모두 실패 시 사용자 보고 + 재논의 |
| R9b | WGC API 초기화 실패 (Win10 1803 이전 또는 DPI 환경 이슈) | High | Low | `windows-capture` 패키지 버전 호환성 확인. Windows 11 환경에서 기본 지원 예상 (확실하지 않음) |
| R9c | dxcam이 전체 데스크탑 캡처 후 crop → target 창 위의 다른 창이 섞임 | Medium | Medium | Operator가 각 시나리오 시작 시 `pywinauto.Application.top_window().set_focus()` + foreground 강제로 target 창을 최상위로 올림. 그 후 캡처 |
| R10 | Python 버전 차이 (3.12 vs 3.13)로 의존성 호환 문제 | Low | Low | requirements.txt에 버전 핀 + Python 버전 체크 |
| R11 | Claude Code Task tool이 동시 subagent 제한이 있어 시나리오 8개를 순차 평가해야 함 | Low | Medium | 순차 실행이 기본. Evaluator 호출 1회당 여러 시나리오 batch 가능하도록 프롬프트 설계 |
| R12 | bisect-mode-termination의 잠재 R2 (초기 pane HostReady 레이스)가 실제로 재현되면 MQ-1 실패 → 본 feature가 real bug를 발견 | **High** | **Medium** | **이는 위험이 아니라 가치**. real bug 발견 시 별도 hotfix feature로 분리 후 재실행 |

---

## 6. Architecture Considerations

### 6.1 Directory Layout

```
ghostwin/
├── scripts/
│   ├── e2e/                       [신설]
│   │   ├── requirements.txt       [핀: pywinauto/pyautogui/mss/Pillow]
│   │   ├── operator.py            [시나리오 함수 라이브러리]
│   │   ├── runner.py              [CLI 진입점]
│   │   ├── evaluator_prompt.md    [Evaluator 프롬프트 템플릿]
│   │   ├── README.md              [사용법 (선택, YAGNI 검토)]
│   │   ├── venv/                  [.gitignore, 첫 실행 시 자동 생성]
│   │   └── artifacts/             [.gitignore, 스크린샷 + summary.json]
│   │       └── {run_id}/
│   ├── build_ghostwin.ps1         [수정 없음]
│   ├── test_ghostwin.ps1          [수정 없음 — 단위 테스트]
│   ├── test_e2e.ps1               [신설 — E2E 테스트]
│   └── run_wpf.ps1                [참조]
├── src/                           [수정 없음]
├── docs/                          [수정 없음]
└── .gitignore                     [venv/, artifacts/ 추가]
```

### 6.2 실행 흐름

```
사용자 명령: scripts/test_e2e.ps1 -All
    │
    ▼
PS1 Orchestrator
    │
    ├─ venv 초기화 (최초 1회)
    ├─ requirements.txt 설치 확인
    │
    ├─ for 각 시나리오 in [MQ-1..MQ-8]:
    │   │
    │   ├─ taskkill /F /IM GhostWin.App.exe (cleanup)
    │   │
    │   ├─ python runner.py --scenario MQ-N
    │   │    │
    │   │    ├─ operator.launch_app()
    │   │    ├─ operator.scenario_mqN_xxx()
    │   │    │    ├─ before 스크린샷
    │   │    │    ├─ pywinauto 키/마우스 액션
    │   │    │    ├─ 적절한 sleep (렌더 대기)
    │   │    │    └─ after 스크린샷
    │   │    └─ operator.shutdown_app()
    │   │
    │   └─ artifacts/{run_id}/MQ-N_{before,after}.png 저장
    │
    ├─ 모든 시나리오 완료 후:
    │   ├─ summary.json 작성
    │   └─ "Run Evaluator" 프롬프트 출력 (사용자가 Claude Code에서 Task 호출)
    │
    ▼
사용자가 Claude Code에서:
    Task(evaluator, "scripts/e2e/evaluator_prompt.md + artifacts/{run_id}/")
        │
        ▼
    Evaluator subagent
        ├─ evaluator_prompt.md Read
        ├─ artifacts/{run_id}/*.png Read (각 시나리오당 2~3장)
        ├─ 시나리오별 pass criteria 평가
        └─ JSON 반환: {"total": 8, "passed": N, "failed": M, "scenarios": [...]}
```

**확실하지 않음**: Orchestrator가 Evaluator를 자동 호출할 수 있는지. 현재 설계는 "사용자가 artifacts 생성 후 Claude Code에서 명시적으로 Task 호출" 방식. Design 단계에서 자동화 가능성 검토.

### 6.3 Key Architectural Decisions

| Decision | Options | Selected | Rationale |
|----------|---------|----------|-----------|
| Python 런처 | system python / venv / poetry / conda | **venv** | 프로젝트 격리, 추가 툴 불요. `scripts/e2e/venv/` |
| UI 자동화 | pywinauto / pyautogui / 둘 다 | **둘 다** | pywinauto = window find + key send (target 명시 가능), pyautogui = screenshot fallback, mss = 빠른 screenshot |
| 스크린샷 | pyautogui.screenshot / mss / PIL.ImageGrab | **mss** | 가장 빠르고 멀티모니터 지원 |
| 이미지 포맷 | PNG / JPG / BMP | **PNG** | 무손실, 텍스트 선명도 중요 |
| 해상도 | 원본 (1920x1080+) / 다운스케일 | **다운스케일** (960x540) | Evaluator 토큰 절약. 시나리오 판독에 충분 |
| 시나리오 함수 스타일 | pytest / unittest / 순수 함수 | **순수 함수** | 단순, 외부 test framework 의존 0 |
| 평가 프로토콜 | 자연어 / JSON / YAML | **JSON** | 프로그래밍적 집계 가능 |
| 스크립트 언어 | PowerShell / Python / Bash | **PS1 orchestrator + Python operator** | 기존 `build_ghostwin.ps1` 패턴 일관, .claude/rules/behavior.md PowerShell 우선 |
| Evaluator 호출 | 자동 / 수동 | **수동** (Design에서 재검토) | Claude Code Task tool이 subprocess에서 트리거 불가. 사용자가 명시 호출 |

### 6.4 Clean Architecture 정합성

본 feature는 **test infrastructure**이므로 GhostWin의 core Clean Architecture에 개입하지 않음. `scripts/e2e/`는 완전히 독립된 도구로 `src/` 의존 없음 (단, 실행 대상 exe는 필요).

---

## 7. Convention Prerequisites

### 7.1 Existing Project Conventions

- [x] `.claude/rules/behavior.md` — 우회 금지, 근거 기반. **특히 본 feature의 Check phase 자기 검증에 적용**
- [x] `.claude/rules/commit.md` — 영문 커밋, AI 언급 없음
- [x] `.claude/rules/build-environment.md` — PS1 우선
- [x] core-tests-bootstrap의 D13/D14 패턴 — PS1 스크립트 UTF-8 + `DOTNET_CLI_UI_LANGUAGE=en` (본 feature도 한국어 Windows 고려)

### 7.2 Conventions to Define

- [ ] **Python 코드 스타일**: PEP 8 + type hints. `ruff` 또는 `flake8` 도입은 별도 결정 (Design)
- [ ] **시나리오 함수 네이밍**: `scenario_{mq_id}_{short_description}()` (예: `scenario_mq1_initial_render`)
- [ ] **스크린샷 파일명**: `{scenario_id}_{tag}.png` (예: `MQ-1_before.png`, `MQ-1_after.png`)
- [ ] **run_id 포맷**: `{YYYYMMDD}_{HHMMSS}` (예: `20260407_181500`)

### 7.3 Environment Requirements

| 항목 | 최소 | 현재 확인 |
|---|---|---|
| Python | 3.12+ | 3.12.6 ✅ |
| Windows | 10+ (HwndHost composition) | 11 ✅ |
| GhostWin.App.exe | Release 빌드 | `src/GhostWin.App/bin/x64/Release/net10.0-windows/GhostWin.App.exe` 존재 확인 ✅ |
| Disk | ~50MB (venv + artifacts) | 충분 |

---

## 8. Next Steps

1. [ ] `/pdca team design e2e-test-harness` — Design phase Slim 3-agent council
   - **code-analyzer**: Python 디렉토리 구조 + runner/operator 파일 분리, 함수 시그니처 설계
   - **wpf-architect**: HwndHost + custom titlebar 환경에서 pywinauto `Application().connect()` 동작 검증 방법
   - **qa-strategist**: MQ-1~8 각 시나리오의 pass criteria 세부 정의 + Evaluator 프롬프트 템플릿
2. [ ] Design 후 Do 단계 구현
3. [ ] Check: 프레임워크 자기 검증 + bisect MQ-1~8 retroactive
4. [ ] Report + CLAUDE.md 갱신
5. [ ] 다음 feature: P0-3 종료 경로 단일화 (본 프레임워크로 QA)

---

## 9. Open Questions (Design에서 해결)

1. **[최우선] 캡처 라이브러리 최종 선택**: `windows-capture` (WGC) / `dxcam` (DXGI) / `PrintWindow+PW_RENDERFULLCONTENT` 중 GhostWin의 DX11 HwndHost 영역을 안정적으로 캡처하는 방법. `scripts/e2e/capture_poc.py`로 실측 후 결정. **이 결정 전까지 다른 시나리오 구현 착수 불가** (모든 시나리오가 이 캡처에 의존)
2. **Evaluator 자동 호출 가능 여부**: Orchestrator가 subprocess에서 Claude Code Task를 트리거할 수 있는가? 불가능하면 수동 호출 후 결과 JSON 파일 생성으로 결합
3. **앱 시작 대기 조건**: `launch_app()` 이 "렌더 준비 완료"를 어떻게 감지하는가? 고정 sleep? HWND 생성 polling? 프롬프트 문자열 OCR?
4. **창 크기/위치 일정화**: 각 run에서 앱 창을 같은 크기(1280x800)로 리사이즈 + 화면 중앙 위치 고정 해야 스크린샷 정합성 확보. `SetWindowPos` 직접 호출
5. **Mica backdrop + 투명도가 Evaluator 판독에 영향**: 배경이 wallpaper와 섞여 보일 수 있음. 해결책 옵션: (a) 사전 데스크탑 배경 단색 설정 권고, (b) Evaluator 프롬프트에 "wallpaper 노이즈 무시" 명시, (c) WGC 캡처는 해당 window만 캡처하므로 wallpaper 영향 자체가 없음 — **WGC 선택 시 이 이슈 해소**
6. **MQ-5 paneId 보존 invariant 시각 검증**: pane close 후 "sibling의 paneId가 보존되었는가"는 시각적으로 검증 불가 — terminal 프롬프트 문자열/history 보존으로 proxy 검증
7. **MQ-8 창 리사이즈 방식**: pyautogui가 WindowChrome 창 edge drag 가능한가? 불가능하면 Win32 `SetWindowPos` 직접 호출
8. **foreground 상태 보장**: WGC는 오클루전에 강건하지만 pywinauto 키 입력은 foreground 필요. 각 시나리오 시작 시 `top_window().set_focus()` 호출 패턴 확립

---

## 10. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-07 | Initial draft. bisect-mode-termination 수동 QA를 트리거로 발의. Pre-scripted Python + Task subagent dual-agent 패턴 확정 (사용자 결정) | 노수장 |
