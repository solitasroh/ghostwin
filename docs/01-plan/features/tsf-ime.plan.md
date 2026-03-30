# tsf-ime Plan

> **Feature**: 한글 IME TSF (Text Services Framework) 지원
> **Project**: GhostWin Terminal
> **Phase**: 4-B (Master: winui3-integration FR-08)
> **Date**: 2026-03-30
> **Author**: Solit
> **Dependency**: winui3-shell (A) 완료 후 (TSF는 XAML 컨텍스트에서 동작)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3에서 한글 입력 시 자모가 분리되어 전송됨 (조합 중 상태 미지원) |
| **Solution** | TSF `ITfContextOwner` + `ITfContextOwnerCompositionSink` 구현으로 조합 문자를 실시간 렌더링하고 완성 후 ConPTY 전달 |
| **Function/UX** | 한글 입력 시 조합 중 글리프가 커서 위치에 실시간 표시되고, 완성 시 셸에 전달 |
| **Core Value** | 한국어 사용자 필수 기능. 한글 터미널 사용성의 근본적 개선 |

---

## 1. Background

Phase 3에서는 `WM_CHAR`로 전달되는 완성된 문자만 ConPTY에 전송한다.
한글처럼 조합형 입력이 필요한 언어는 IME(Input Method Editor)를 통해 조합 상태를 관��해야 한다.

Windows에서 TSF (Text Services Framework)는 IME의 표준 프레임워크이다.
Windows Terminal은 `TsfDataProvider` 클래스에서 TSF 인터페이스를 구현한다.

### TSF 핵심 인터페이��

| 인터페이스 | 역할 |
|-----------|------|
| `ITfContextOwner` | TSF 컨텍스트 소유자 등록 |
| `ITfContextOwnerCompositionSink` | 조합 시작/업데이트/종료 알림 수신 |
| `ITfEditSession` | 편집 세션에서 조합 텍스트 읽기 |

---

## 2. Functional Requirements

### FR-01: TsfProvider 클래스 구현
- `ITfContextOwner` + `ITfContextOwnerCompositionSink` COM 인터페이스 구현
- `ITfThreadMgr` 초기화 + 컨텍스트 생성
- SwapChainPanel의 포커스 이벤트와 TSF 활성화/비활성화 연동

### FR-02: 조합 문자 실시간 렌더링
- 조합 중(`composing`) 문자를 GlyphAtlas에서 래스터화
- 커서 위치에 조합 중 글리프 표��� (밑줄 또는 하이라이트로 구분)
- 조합 업데이트 시 이전 조합 문자 지우고 새 조합 문자 렌더

### FR-03: 조합 완료 → ConPTY 전달
- 조합 완료(`committed`) 시 UTF-8 인코딩하여 ConPTY `send_input()` 호출
- 한글 완성 문자 지원 (한글 전용 범위 — CJK 범용은 향후 확장)

### FR-04: IME 후보창 위치 지정
- `ITfContextOwner::GetScreenExt()` / `GetTextExt()`로 후보창 위치 반환
- SwapChainPanel 내 커서 위치 → 스크린 좌표 변환
- 후보창이 터미널 커서 근처에 표시

---

## 3. Implementation Steps

| # | Task | DoD |
|---|------|-----|
| S1 | `TsfProvider` COM 클래스 스캐폴딩 (ITfContextOwner) | 컴파일 성공 + TSF 컨텍스트 등록 |
| S2 | ITfContextOwnerCompositionSink 구현 | 조합 시작/업데이트/종료 콜백 수신 |
| S3 | 조합 중 문자 → GlyphAtlas → 커서 위치 렌더링 | 한글 조합 중 글리프 ��면 표시 |
| S4 | 조합 완료 → UTF-8 → ConPTY send_input | 한글 완성 문자 셸 입력 동작 |
| S5 | IME 후보창 위치 지정 (GetTextExt) | 후보창 커서 근처 표시 |
| S6 | 포커스 전환 시 TSF ��성화/비활성화 | 다른 앱 전환 후 복귀 시 IME 상태 유지 |

---

## 4. Definition of Done

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | 한글 조합 입력 (ㅎ→하→한→한글) 실시간 표시 | 커서 위치에 조합 중 글리프 |
| 2 | 한글 완성 → 셸 전달 | echo 한글 출력 확인 |
| 3 | IME 후보창 위치 정상 | 커서 근처에 후보 표시 |
| 4 | 영문 모드 전환 (한/영) | 기존 키보드 입력 불변 |
| 5 | 기존 테스트 PASS 유지 | 7/7 PASS |

---

## 5. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | TSF COM 인터페이스 구현 복잡도 | 상 | Windows Terminal TsfDataProvider 패턴 참고. 최소 인터페이스부터 점진적 확장 |
| R2 | WinUI3 + TSF 통합 알려진 이슈 | 중 | WindowsAppSDK GitHub Issues 모니터링. HWND interop 폴백 준비 |
| R3 | 다른 IME (중국어, 일본어) 호환성 | 하 | 범위 외 — 한글 전용. 향후 확장 가능하도록 TSF 구조는 범용 유지 |

---

## 6. References

| Document | Path |
|----------|------|
| WinUI3 + DX11 리서치 (Section 5: 한국어 IME) | `docs/00-research/research-winui3-dx11.md` |
| Windows Terminal TsfDataProvider | github.com/microsoft/terminal |
| Phase 3 키보드 입력 (교체 대상) | `src/renderer/terminal_window.cpp` |
