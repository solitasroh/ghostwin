# GhostWin Terminal — 기술 실현 가능성 통합 리서치 보고서

> **작성일**: 2026-03-28
> **목적**: onboarding.md 기획의 기술 실현 가능성 교차 검증 및 요구사항 충족 여부 확인
> **리서치 방법**: 5개 병렬 에이전트 독립 조사 → 교차 검증 → 통합 분석

---

## Executive Summary

| 기술 레이어 | 실현 가능성 | 핵심 근거 | 최대 리스크 |
|------------|:----------:|----------|-----------|
| **L0. libghostty-vt** | **상** | ghostty-windows 포크에서 실제 빌드+동작 확인 | API 불안정 (v0.1.0 미릴리즈) |
| **L1. ConPTY** | **상** | Windows 10 1809+ 공식 API, Windows Terminal 실전 검증 | ClosePseudoConsole 교착 (Win11 24H2 미만) |
| **L2. DirectX 11 렌더러** | **상** | AtlasEngine 20B QuadInstance 패턴 완전 분석 | CJK 폰트 fallback CPU 85% 병목 |
| **L3. WinUI3 UI Shell** | **중~상** | SwapChainPanel+D3D11 확립, App SDK 1.8.6 안정 | 수직 탭 커스텀 구현 필요, IME TSF 난이도 |
| **L4. AI 에이전트 UX** | **상** | cmux UX 패턴 완전 분석, OSC hooks + Named Pipe 아키텍처 설계 완료 | 클린룸 재구현 라이선스 리스크 (낮음) |

### 종합 판정: **프로젝트 실현 가능 (Go)**

모든 핵심 기술 레이어에서 실현 가능성 "중" 이상 확인. 치명적 블로커 없음.

---

## 1. 레이어별 교차 검증 결과

### 1.1 L0: libghostty-vt (VT 파싱 코어)

**리서치 에이전트**: libghostty-vt 에이전트
**교차 검증 대상**: DirectX 11 에이전트 (렌더 상태 연동), ConPTY 에이전트 (파이프라인 연결)

#### 확인된 사실

| 항목 | 결과 | 출처 |
|------|------|------|
| Windows CI 빌드 | `x86_64-windows` 공식 CI 매트릭스 포함 | ghostty-org/ghostty CI/CD |
| 실제 동작 검증 | InsipidPoint/ghostty-windows 포크에서 빌드+실행 확인 | GitHub 포크 |
| C API 핵심 함수 | `ghostty_terminal_new`, `ghostty_terminal_vt_write`, `ghostty_render_state_update` 등 6개 | ghostling main.c |
| 빌드 명령 | `zig build -Dapp-runtime=win32 -Dtarget=x86_64-windows` | ghostty-windows |
| libxml2 이슈 | PR #11698로 해결 완료 (Windows에서 fontconfig 조건부 제외) | ghostty PR |
| Zig-MSVC ABI | C ABI(`extern "C"`)는 호환, `-target x86_64-windows-msvc` 권장 | Zig 문서 |

#### 교차 검증 결과

- **DirectX 11 에이전트와의 교차**: libghostty-vt의 `ghostty_render_state_update`가 증분 렌더 상태를 제공 → AtlasEngine 패턴의 dirty row 추적과 자연스럽게 연결됨. **호환성 확인**.
- **ConPTY 에이전트와의 교차**: ConPTY 출력 파이프 → libghostty-vt `ghostty_terminal_vt_write` 입력 경로가 명확. 동기 I/O 파이프와 전용 파싱 스레드 모델이 일치. **호환성 확인**.

#### 리스크 대응

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| API 불안정 (공식 버전 태그 없음) | 높음 | VtCore 래퍼 레이어로 격리 (파일 1개로 변경 범위 제한) |
| Zig 버전 변경 시 빌드 깨짐 | 중간 | `zig-version` 파일로 고정, CI에서 검증 |

---

### 1.2 L1: ConPTY (PTY 레이어)

**리서치 에이전트**: ConPTY 에이전트
**교차 검증 대상**: libghostty-vt 에이전트 (파이프라인 입력), WinUI3 에이전트 (IME 처리)

#### 확인된 사실

| 항목 | 결과 | 출처 |
|------|------|------|
| API 함수 | CreatePseudoConsole, ResizePseudoConsole, ClosePseudoConsole (Kernel32.dll) | MS 공식 문서 |
| 최소 OS | Windows 10 1809 | MS 공식 문서 |
| I/O 방식 | 동기 I/O 파이프만 허용, 별도 스레드 필수 | MS 공식 문서 (교착 경고) |
| WSL 통합 | `wsl.exe`를 ConPTY 자식으로 실행 (Windows Terminal 동일 방식) | 실전 검증 |
| ClosePseudoConsole 교착 | Win11 24H2 미만에서 무기한 대기 가능 | GitHub 이슈 |

#### 교차 검증 결과

- **libghostty-vt와의 교차**: ConPTY 출력 → 전용 I/O 스레드 → lock-free 큐 → 파싱 스레드(libghostty-vt). onboarding.md의 4-스레드 모델과 정확히 일치. **호환성 확인**.
- **WinUI3 에이전트와의 교차**: IME 처리 책임이 ConPTY가 아닌 호스트(WinUI3) 측임을 양측 에이전트가 독립적으로 확인. **조합 문자는 ConPTY에 전송 금지, 확정 후만 전송**. 일관된 결론. **교차 검증 일치**.

#### 리스크 대응

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| ClosePseudoConsole 교착 | 높음 | 별도 스레드에서 호출 + 타임아웃 감시 |
| SSH 대용량 출력 시 문자 손실 | 중간 | 읽기 버퍼 64KB 이상 확보 |
| 시그널 처리 (Ctrl+C) | 낮음 | GenerateConsoleCtrlEvent 또는 VT 시퀀스 전송 |

---

### 1.3 L2: DirectX 11 (렌더링 레이어)

**리서치 에이전트**: DirectX 11 에이전트
**교차 검증 대상**: WinUI3 에이전트 (SwapChainPanel), libghostty-vt 에이전트 (렌더 상태)

#### 확인된 사실

| 항목 | 결과 | 출처 |
|------|------|------|
| AtlasEngine QuadInstance | 20바이트: shadingType(2B)+renditionScale(2B)+position(4B)+size(4B)+texcoord(4B)+color(4B) | Windows Terminal 소스 |
| Glyph atlas 전략 | stb_rect_pack + LRU 퇴출 (초기 grow-only에서 개선) | AtlasEngine 코드 |
| CJK 폰트 병목 | `IDWriteFontFallback::MapCharacters()`가 CPU 시간 85% 소모 | AtlasEngine 개발자 인정 |
| 유휴 GPU 0% | invalidatedRows dirty flag + 조건부 Present() | AtlasEngine 코드 |
| 단일 draw call | 인스턴스 버퍼로 전체 화면 문자를 1회 DrawInstanced | AtlasEngine 코드 |
| 메모리 문제 | Windows Terminal 탭 10개 시 1.1~2.5GB (탭당 별도 D3D11 디바이스) | 성능 벤치마크 |

#### 교차 검증 결과

- **WinUI3 에이전트와의 교차**: SwapChainPanel에서 `ISwapChainPanelNative`로 D3D11 스왑체인 연결하는 패턴을 양측 에이전트가 독립적으로 확인. `CreateSwapChainForComposition` + `DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL` 필수. **교차 검증 일치**.
- **DPI 이슈**: WinUI3 에이전트는 `CompositionScaleX/Y` 보정 필요를 보고, DX11 에이전트는 SwapChainPanel DPI 버그(이슈 #5888)를 보고. 동일 문제를 다른 관점에서 확인. **교차 검증 보강**.
- **GhostWin 경쟁력**: Windows Terminal의 탭당 별도 D3D11 디바이스 문제를 **단일 디바이스 공유 아키텍처**로 해결하면 메모리 사용량에서 우위 확보 가능.

#### 리스크 대응

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| CJK 폰트 fallback 병목 | 높음 | MapCharacters 결과 캐싱 (해시맵) |
| DPI 변경 시 깜박임 | 중간 | CompositionScaleX/Y 보정 + 스왑체인 리사이즈 |
| Glyph atlas 메모리 | 낮음 | LRU 퇴출 + 최대 크기 제한 |

---

### 1.4 L3: WinUI3 (UI Shell 레이어)

**리서치 에이전트**: WinUI3 에이전트
**교차 검증 대상**: DirectX 11 에이전트 (SwapChain), ConPTY 에이전트 (IME), cmux 에이전트 (탭/알림 UX)

#### 확인된 사실

| 항목 | 결과 | 출처 |
|------|------|------|
| Windows App SDK | v1.8.6 (2026-03-18), 지원 종료 2026-09-09 | MS 릴리즈 노트 |
| SwapChainPanel+D3D11 | `ISwapChainPanelNative::SetSwapChain` 패턴 확립 | MS 문서 + Terminal 코드 |
| 수직 탭 | TabView 미지원, NavigationView(Left) 또는 ListView 커스텀 | WinUI3 문서 |
| Toast 알림 | `AppNotificationBuilder` API (packaged 앱) | MS 문서 |
| 시스템 트레이 | `Shell_NotifyIcon` + HWND interop | Win32 API 문서 |
| 한국어 IME | TSF `ITfContextOwner` 직접 구현 필요, 난이도 높음 | Terminal TsfDataProvider |

#### 교차 검증 결과

- **cmux 에이전트와의 교차**: cmux의 수직 탭 사이드바(Workspace Sidebar)를 WinUI3에서 구현하려면 ListView + 커스텀 DataTemplate이 가장 적합. cmux 에이전트가 정리한 사이드바 표시 항목(git branch, PR 상태, 포트, 알림 배지)을 WinUI3의 `InfoBadge` + 커스텀 컨트롤로 구현 가능. **매핑 확인**.
- **ConPTY 에이전트와의 교차**: IME 처리는 WinUI3 측 책임 (ConPTY는 IME 미인식). 초기에는 IMM32(`WM_IME_COMPOSITION`) → 이후 TSF 전환의 단계적 접근 권장. 양측 에이전트 일치. **교차 검증 일치**.
- **App SDK 지원 종료 리스크**: 1.8.x가 2026-09-09 종료이나, 2.0이 이미 Preview. MVP 기간 내 문제없음.

#### 리스크 대응

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| IME TSF 구현 난이도 | 높음 | Phase 4에서 처리, IMM32로 MVP 먼저 |
| 수직 탭 커스텀 개발 | 중간 | ListView + DataTemplate (cmux UX 참고) |
| ISwapChainPanelNative vs Native2 | 낮음 | Native(v1)로 시작, 필요 시 Native2 전환 |

---

### 1.5 L4: AI 에이전트 UX (멀티플렉서 레이어)

**리서치 에이전트**: cmux AI UX 에이전트
**교차 검증 대상**: ConPTY 에이전트 (OSC 시퀀스), WinUI3 에이전트 (UI 구현), libghostty-vt 에이전트 (파서 콜백)

#### 확인된 사실

| 항목 | 결과 | 출처 |
|------|------|------|
| cmux 아키텍처 | libghostty + WebKit + Unix Socket 3-레이어 | GitHub, HN |
| 알림 시스템 | OSC 9/777/99 + CLI, 4단계 생명주기 (수신→미읽음→읽음→제거) | cmux 공식 문서 |
| 세션 복원 | 레이아웃+CWD 복원 가능, 프로세스 상태 복원 불가 | cmux 공식 문서 |
| Claude Code hooks | `Notification`, `Stop`, `CwdChanged` 이벤트 | Claude Code 문서 |
| Named Pipe 아키텍처 | ghostwin-hook.exe → `\\.\pipe\ghostwin-hook` → GhostWin 훅 서버 | 설계 제안 |
| 에이전트 상태 | Idle→Running→WaitingForInput→Error 4-상태 모델 | cmux 분석 기반 설계 |

#### 교차 검증 결과

- **libghostty-vt 에이전트와의 교차**: libghostty-vt가 OSC 시퀀스 파싱을 제공 → 콜백에서 OSC 9/777/99를 직접 처리 가능. cmux 에이전트의 OSC 처리 의사 코드가 libghostty-vt C API와 호환됨. **호환성 확인**.
- **WinUI3 에이전트와의 교차**: cmux의 알림 배지 → WinUI3 `InfoBadge`, Toast 알림 → `AppNotificationBuilder`, 알림 패널 → `Flyout`. 모든 UI 요소에 대응하는 WinUI3 컴포넌트 존재. **매핑 확인**.
- **라이선스**: cmux AGPL-3.0에 대해 코드 직접 포팅은 금지. UX 패턴만 참고하는 클린룸 재구현은 법적으로 안전 (Oracle v. Google 판례 참고). **리스크 낮음**.

#### 리스크 대응

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| AGPL 라이선스 오염 | 낮음 | 코드 포팅 금지, UX 패턴만 참고, 클린룸 재구현 |
| 에이전트 상태 감지 정확도 | 중간 | OSC hooks + Named Pipe 이중 감지 |
| 다중 에이전트 UX 복잡도 | 중간 | 통합 알림 패널 + 미읽음 점프로 단순화 |

---

## 2. 스레드 모델 교차 검증

onboarding.md에서 정의한 4-스레드 모델의 실현 가능성을 모든 에이전트 결과로 교차 검증:

```
┌──────────────────────────────────────────────────────────────────┐
│                    4-Thread Architecture                         │
├──────────────┬──────────────┬──────────────┬────────────────────┤
│  UI Thread   │ Render Thread│ Parse Thread │   I/O Thread       │
│  (WinUI3)    │ (D3D11)      │ (libghostty) │   (ConPTY)         │
├──────────────┼──────────────┼──────────────┼────────────────────┤
│ XAML 이벤트  │ Present()    │ vt_write()   │ ReadFile()         │
│ 탭/pane 관리 │ glyph atlas  │ render_state │ WriteFile()        │
│ IME 입력     │ GPU 인스턴싱 │ _update()    │ IOCP 완료 포트     │
│ 알림 패널    │ dirty flag   │ OSC 콜백     │ 64KB 버퍼          │
├──────────────┴──────────────┴──────────────┴────────────────────┤
│              Lock-free Queue (Inter-thread Communication)        │
└─────────────────────────────────────────────────────────────────┘
```

| 연결 구간 | 검증 결과 | 근거 |
|-----------|----------|------|
| I/O → Parse | **확인** | ConPTY 동기 파이프 → 전용 스레드 → lock-free 큐 → 파싱 스레드 |
| Parse → Render | **확인** | `ghostty_render_state_update` → dirty rows → 조건부 렌더 |
| Render → UI | **확인** | SwapChainPanel이 XAML 트리와 D3D11 스왑체인 브리지 역할 |
| UI → I/O | **확인** | 키 입력 → VT 인코딩 → WriteFile (ConPTY 입력 파이프) |

**결론**: 4-스레드 모델은 모든 레이어의 기술 제약과 호환됨.

---

## 3. 요구사항 충족 매트릭스

onboarding.md의 킬러 피처별 요구사항 충족 여부:

### 3.1 경량 + 최적 성능

| 요구사항 | 충족 여부 | 근거 |
|---------|:--------:|------|
| Ghostty 수준의 빠른 VT 파싱 (SIMD) | **충족** | libghostty-vt SIMD(Highway+simdutf), Windows CI 검증 |
| GPU 인스턴싱 단일 draw call | **충족** | AtlasEngine 20B QuadInstance 패턴 분석 완료 |
| 최소 메모리 사용 | **충족 가능** | 단일 D3D11 디바이스 공유로 Terminal 대비 우위 |
| 유휴 시 GPU ~0% | **충족** | dirty flag + 조건부 Present 패턴 확인 |

### 3.2 AI 에이전트 멀티플렉서

| 요구사항 | 충족 여부 | 근거 |
|---------|:--------:|------|
| cmux 스타일 수직 탭 사이드바 | **충족 (커스텀)** | ListView + DataTemplate, TabView 수직 미지원 |
| 에이전트 알림 시스템 | **충족** | OSC 9/777/99 + Named Pipe 훅 서버 |
| 통합 알림 패널 | **충족** | WinUI3 Flyout + InfoBadge |
| 수평/수직 pane 분할 | **충족** | 중첩 Grid + GridSplitter (Community Toolkit) |

### 3.3 Claude Code 팀 에이전트 지원

| 요구사항 | 충족 여부 | 근거 |
|---------|:--------:|------|
| OSC hooks 연동 | **충족** | libghostty-vt OSC 파싱 → 콜백 처리 |
| 탭별 에이전트 상태 배지 | **충족** | 4-상태 모델 + Named Pipe 이벤트 |
| Named pipe 기반 훅 서버 | **충족** | ghostwin-hook.exe 아키텍처 설계 완료 |

### 3.4 Windows 네이티브 통합

| 요구사항 | 충족 여부 | 근거 |
|---------|:--------:|------|
| ConPTY (cmd, PS, WSL, SSH) | **충족** | 공식 API, Windows Terminal 동일 방식 |
| 한국어 IME | **부분 충족** | IMM32 MVP → TSF 전환 (단계적 접근) |
| Win32 Toast 알림 | **충족** | AppNotificationBuilder API |
| WinUI3 네이티브 룩앤필 | **충족** | Windows App SDK 1.8.6 |

---

## 4. 새로 발견된 사항 (onboarding.md에 없는 것)

리서치 과정에서 onboarding.md에 기술되지 않은 중요 사항:

| # | 발견 사항 | 영향 | 권장 조치 |
|---|----------|------|----------|
| 1 | **ghostty-windows 포크 존재** — libghostty-vt + Win32 + ConPTY로 실제 동작하는 터미널 | Phase 1 난이도 대폭 감소 | 레퍼런스로 적극 활용 |
| 2 | **CJK 폰트 fallback CPU 85% 병목** | 한국어 사용 시 성능 저하 가능 | MapCharacters 결과 캐싱 필수 설계 |
| 3 | **ClosePseudoConsole 교착 상태** (Win11 24H2 미만) | 프로세스 종료 시 행 가능 | 별도 스레드 호출 + 타임아웃 |
| 4 | **Windows Terminal 탭당 D3D11 디바이스 분리 = 메모리 문제** | GhostWin 차별화 기회 | 단일 디바이스 공유 아키텍처 채택 |
| 5 | **cmux 세션 복원은 부분적** (프로세스 상태 미복원) | 2차 목표 범위 재조정 | tmux on WSL 연동으로 우회 |
| 6 | **ISwapChainPanelNative2** — Windows Terminal이 핸들 기반 전달 사용 | 추후 고려 사항 | v1(Native)로 시작, 필요 시 전환 |
| 7 | **libghostty-vt 공식 버전 태그 없음** | onboarding.md의 "v0.1.0"은 내부 추적 번호 | 커밋 해시로 버전 고정 |

---

## 5. 리스크 통합 매트릭스

모든 에이전트가 보고한 리스크를 심각도 × 발생 가능성으로 정렬:

| 순위 | 리스크 | 레이어 | 심각도 | 가능성 | 대응 상태 |
|:----:|--------|--------|:------:|:------:|:---------:|
| 1 | libghostty-vt API 불안정 | L0 | 높음 | 높음 | VtCore 래퍼 격리 |
| 2 | CJK 폰트 fallback 병목 | L2 | 높음 | 높음 | 캐싱 설계 필요 |
| 3 | ClosePseudoConsole 교착 | L1 | 높음 | 중간 | 별도 스레드+타임아웃 |
| 4 | 한국어 IME TSF 구현 난이도 | L3 | 높음 | 중간 | 단계적 접근 (IMM32→TSF) |
| 5 | 수직 탭 커스텀 개발 공수 | L3 | 중간 | 높음 | ListView 기반 구현 |
| 6 | SwapChainPanel DPI 버그 | L2/L3 | 중간 | 중간 | CompositionScale 보정 |
| 7 | Zig 빌드 시스템 변경 | L0 | 중간 | 중간 | 버전 고정 + CI 검증 |
| 8 | App SDK 1.8 지원 종료 | L3 | 낮음 | 확실 | MVP 기간 내 무관, 2.0 전환 준비 |
| 9 | AGPL 라이선스 오염 | L4 | 높음 | 낮음 | 클린룸 재구현 |

---

## 6. Phase 1 실행 권장사항

리서치 결과를 바탕으로 Phase 1 (libghostty-vt Windows 빌드 검증) 구체화:

### 6.1 빌드 순서

```
1. Zig 0.15.2 설치 (scoop install zig 또는 공식 바이너리)
2. ghostty 소스 클론 (git clone https://github.com/ghostty-org/ghostty)
3. libghostty-vt 빌드:
   zig build -Dapp-runtime=win32 -Dtarget=x86_64-windows-msvc
4. .lib 파일 확인 (zig-out/lib/)
5. C API 테스트 프로젝트 생성 (CMake + MSVC)
6. ghostty_terminal_new → ghostty_terminal_vt_write → ghostty_render_state_update 호출 검증
7. VtCore 래퍼 헤더 작성
```

### 6.2 핵심 참고 자료

| 자료 | 용도 |
|------|------|
| InsipidPoint/ghostty-windows | 빌드 설정 + ConPTY 연동 레퍼런스 |
| ghostty-org/ghostling/main.c | C API 사용 패턴 |
| ghostty CI `build-libghostty-vt` | 빌드 매트릭스 설정 |

---

## 7. 개별 리서치 문서 목록

| # | 문서 | 위치 |
|---|------|------|
| 1 | libghostty-vt Windows 빌드 리서치 | `docs/01-plan/research-libghostty-vt-windows.md` |
| 2 | DirectX 11 GPU 렌더링 리서치 | `docs/research-dx11-gpu-rendering.md` |
| 3 | ConPTY 심층 리서치 | `docs/01-plan/features/conpty-research.md` |
| 4 | WinUI3 + D3D11 통합 리서치 | `docs/01-plan/research-winui3-dx11.md` |
| 5 | cmux AI 에이전트 UX 리서치 | `docs/research/cmux-ai-agent-ux-research.md` |

---

*GhostWin Terminal — Technical Feasibility Report v1.0*
*작성일: 2026-03-28*
