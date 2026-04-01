# ADR-011: TSF + Hidden Win32 HWND for IME Input

## Status
Accepted (2026-04-01)

## Context
Phase 4-B에서 한글 IME 입력을 구현해야 했다. 초기 설계(tsf-ime.design.md v1.0)에서는
IMM32 + HWND Subclass 방식을 선택했으나, 구현 과정에서 WinUI3 InputSite와 TSF 충돌이
발생하여 방식을 전환해야 했다.

### 시도한 접근법

1. **IMM32 + WinUI3 HWND Subclass** (v1.0 설계)
   - `SetWindowSubclass()`로 WinUI3 Window HWND를 서브클래싱
   - WM_IME_COMPOSITION으로 조합/확정 분리
   - 문제: WinUI3 InputSite가 자체 TSF 컨텍스트를 가지고 있어 IMM32 메시지가
     간섭받음. CharacterReceived와의 이중 입력 문제 해결이 복잡

2. **TextBox IME Composition** (중간 시도)
   - 숨겨진 WinUI3 TextBox를 IME 입력 프록시로 사용
   - TextBox.TextChanged로 조합 텍스트 추출
   - 문제: TextBox의 비동기 clear와 composition 타이밍 충돌

3. **TSF + Hidden Win32 HWND** (최종 선택)
   - WinUI3와 완전히 분리된 1x1 child HWND 생성
   - TSF AssociateFocus로 해당 HWND에 IME 컨텍스트 연결
   - WM_CHAR/WM_KEYDOWN으로 비-IME 입력 처리

## Decision
**TSF + Hidden Win32 HWND** 방식을 채택한다.

### 근거
- Windows Terminal이 동일한 패턴 사용 (검증된 아키텍처)
- WinUI3 InputSite와 완전 분리되어 TSF 충돌 없음
- ITfContextOwnerCompositionSink로 종성 분리 등 edge case 정밀 제어 가능
- WM_CHAR가 TranslateMessage를 통해 정상 생성되어 영문 입력 경로 보존

### 구현 핵심
- `CreateWindowExW(0, "GhostWinInput", "", WS_CHILD, 0,0,1,1, parent, ...)`
- `AssociateFocus(m_input_hwnd, m_documentMgr, ...)`
- 50ms SetTimer로 WinUI3 포커스 탈취 대응
- BS 취소 vs Space 확정 구분: `GetKeyState(VK_BACK)` in OnEndComposition

## Consequences

### Positive
- IME 입력이 WT와 동등하게 동작 (99% design match)
- 종성 분리, BS 취소, 서로게이트 쌍 등 edge case 모두 해결
- E2E 테스트 61/61 PASS

### Negative
- COM 인터페이스 구현 (TsfImplementation ~640 lines)으로 코드량 증가
- 50ms 포커스 타이머가 다른 UI 요소와 충돌할 가능성 (Phase 5에서 대응 필요)
- ime_handler.h/cpp가 레거시 코드로 잔존 (TextBox 방식 시도 흔적)

## Related
- ADR-009: WinUI3 Code-only CMake
- ADR-010: Grayscale AA Composition
- tsf-ime Design v2.0: `docs/archive/2026-04/tsf-ime/tsf-ime.design.md`
