# tsf-ime Design

> **Feature**: 한글 IME 입력 지원 (TSF + Hidden Win32 HWND)
> **Project**: GhostWin Terminal
> **Phase**: 4-B (Master: winui3-integration FR-08)
> **Date**: 2026-03-30
> **Author**: Solit
> **Plan**: `docs/01-plan/features/tsf-ime.plan.md`
> **Revision**: 2.0
> **Scope**: 한글 IME + 키 입력 + CJK 렌더링

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | WinUI3 InputSite와 TSF가 충돌하여 IME 콜백이 정상 동작하지 않음. IMM32 서브클래스는 XAML 이벤트 간섭 리스크 |
| **Solution** | WinUI3와 분리된 Hidden Win32 HWND에서 TSF AssociateFocus로 한글 IME 처리. WM_CHAR/WM_KEYDOWN으로 영문/특수키 처리 |
| **Function/UX** | 한글 조합 중 오버레이 표시, Space/Enter 확정, BS 조합 취소, Ctrl+V 붙여넣기, Alt+키 readline, vim/tmux DECCKM |
| **Core Value** | 한국어 사용자 필수 기능. WT/Alacritty 수준의 IME 입력 품질 |

---

## 1. Approach Decision

### 1.1 IMM32 vs TSF

| 방식 | 장점 | 단점 |
|------|------|------|
| IMM32 | 구현 단순 | WinUI3 InputSite 충돌, 종성 분리 제어 어려움 |
| **TSF** | WT 동일 패턴, 종성 분리 정밀 제어, COM 표준 | COM 인터페이스 구현 필요 |

**결정: TSF** — v1.0에서 IMM32를 선택했으나, WinUI3 InputSite와의 TSF 충돌 문제로 전환.
Hidden Win32 HWND를 생성하여 WinUI3와 분리된 입력 경로 확보 (WT 동일 패턴).

### 1.2 Hidden Win32 HWND

```cpp
void GhostWinApp::CreateInputHwnd(HWND parent) {
    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = InputWndProc;
    wc.lpszClassName = L"GhostWinInput";
    RegisterClassExW(&wc);
    m_input_hwnd = CreateWindowExW(0, L"GhostWinInput", L"",
        WS_CHILD, 0, 0, 1, 1, parent, nullptr, nullptr, this);
}
```

### 1.3 WM_CHAR 기반 이중 입력 방지

TSF가 조합 중일 때 `WM_CHAR`에서 일반 문자를 무시. 제어 문자(Enter/Tab/Escape/BS)는 통과.

```
IME OFF (영문):  WM_CHAR → SendUtf8 (기존 경로)
IME ON  (한글):
  조합 중:     TSF 콜백 → HandleCompositionUpdate → m_composition
  조합 확정:   TSF 콜백 → HandleOutput → SendUtf8
               WM_CHAR 한글 문자 → SKIP (HasActiveComposition)
  제어 문자:   WM_CHAR Enter/Tab/Escape/BS → passthrough (Bug #4 fix)
```

---

## 2. Architecture

### 2.1 컴포넌트 구조

```
GhostWin Window (WinUI3)
  +-- SwapChainPanel (DX11 렌더링)
  +-- Hidden Win32 HWND (m_input_hwnd) <-- 입력 전용
       +-- WM_CHAR --> 영문/제어문자 --> SendUtf8 --> ConPTY
       +-- WM_KEYDOWN/WM_SYSKEYDOWN --> HandleKeyDown
       |    +-- Ctrl+V --> PasteFromClipboard (bracket paste mode)
       |    +-- Shift+Tab --> \033[Z
       |    +-- Alt+letter --> \033 + char (readline)
       |    +-- Arrow keys --> \033[A~D or \033OA~D (DECCKM)
       |    +-- F1~F12, Home/End, PageUp/Down, Delete/Insert
       +-- TSF AssociateFocus --> 한글 IME 콜백
       |    +-- OnStartComposition --> 조합 시작 (pending 취소)
       |    +-- DoCompositionUpdate --> 조합/확정 분리 (GUID_PROP_COMPOSING)
       |    +-- OnEndComposition --> BS 감지(GetKeyState) + pending send
       |    +-- HandleOutput --> SendUtf8 --> ConPTY
       +-- 50ms SetTimer --> 포커스 유지 (WinUI3 탈취 대응)
```

### 2.2 공유 상태

| 변수 | 타입 | 접근 | 보호 |
|------|------|------|------|
| `m_composition` | `std::wstring` | UI(write via TSF), Render(read) | `m_ime_mutex` |
| `m_pending_high_surrogate` | `wchar_t` | UI(write/read) | single-thread |
| `m_tsf` | `TsfHandle` | UI(methods) | COM STA |

### 2.3 파일 구조

```
src/
+-- tsf/
|   +-- tsf_implementation.h/cpp  <-- TSF COM 인터페이스 구현
|   +-- tsf_handle.h              <-- pimpl wrapper + IDataProvider
+-- app/
|   +-- winui_app.h/cpp           <-- Hidden HWND, InputWndProc, HandleKeyDown
|   +-- ime_handler.h/cpp         <-- (미사용, 레거시)
+-- vt-core/
|   +-- vt_bridge.h/c             <-- + vt_bridge_mode_get (DECCKM/bracket paste)
|   +-- vt_core.h/cpp             <-- + VtCore::mode_get()
+-- renderer/
|   +-- glyph_atlas.h/cpp         <-- + advance_x, CJK no-height-scale
|   +-- quad_builder.cpp           <-- + advance-centering, cell-height clipping
```

---

## 3. TSF Implementation

### 3.1 TsfImplementation COM 클래스

`ITfContextOwner`, `ITfContextOwnerCompositionSink`, `ITfTextEditSink` 구현.

| 콜백 | 역할 |
|------|------|
| `OnStartComposition` | 조합 카운터++, pending send 취소 (종성 분리 대응) |
| `OnUpdateComposition` | no-op (OnEndEdit → DoCompositionUpdate에서 처리) |
| `OnEndComposition` | 조합 카운터--, BS 감지(GetKeyState), 오버레이 클리어 |
| `OnEndEdit` | Edit session 요청 → DoCompositionUpdate |
| `DoCompositionUpdate` | GUID_PROP_COMPOSING으로 조합/확정 분리, HandleOutput 전달 |

### 3.2 BS 취소 vs Space 확정 구분

```cpp
// OnEndComposition에서 BS vs Space/Enter 판별
bool bs_pressed = (GetKeyState(VK_BACK) & 0x8000) != 0;
if (!m_lastComposing.empty() && !bs_pressed) {
    // Space/Enter 확정 → pending send (TF_E_LOCKED 백업)
    PostMessage(hwnd, WM_USER + 50, 0, 0);
} else if (bs_pressed) {
    // BS 취소 → 전송 안 함 (ghost 방지)
}
```

### 3.3 종성 분리 처리

```
"한" + ㅏ → OnEndComposition("한") → OnStartComposition("하")
OnStartComposition이 pending "한" 취소 → DoCompositionUpdate가 정확한 "하" 전송
```

---

## 4. Key Handling (HandleKeyDown)

| 키 | 동작 | VT 시퀀스 |
|----|------|----------|
| Ctrl+V | 클립보드 붙여넣기 | (bracket paste mode 시 `\033[200~`...`\033[201~`) |
| Ctrl+A-Z (V 제외) | 제어 코드 전송 | `vk - 'A' + 1` |
| Alt+letter | ESC prefix (readline) | `\033` + char |
| Shift+Tab | 역방향 탭 | `\033[Z` |
| Arrow keys | 커서 이동 (DECCKM 대응) | `\033[A~D` or `\033OA~D` |
| F1~F12 | 기능키 | `\033OP` ~ `\033[24~` |
| Home/End/PgUp/PgDn | 내비게이션 | `\033[H/F/5~/6~` |

### 4.1 VT Mode Query (vt_bridge)

```cpp
bool decckm = m_session->vt_core().mode_get(VT_MODE_DECCKM);
bool bracket = m_session->vt_core().mode_get(VT_MODE_BRACKETED_PASTE);
```

`vt_bridge_mode_get()` → `ghostty_terminal_mode_get()` 래핑.

---

## 5. Composition Rendering

### 5.1 RenderLoop 인라인 오버레이

```cpp
if (has_comp && m_atlas) {
    for (wchar_t c : comp) {
        // 배경 quad (0xFF443344 보라빛 회색)
        // 글리프 quad (흰색 전경, atlas lookup)
        // wide char: col += 2
    }
}
```

### 5.2 comp_just_cleared 강제 리드로우

오버레이 해제 시 RenderLoop이 프레임 스킵하여 잔상 방지:
```cpp
bool comp_just_cleared = (prev_had_comp && !has_comp);
if (!dirty && !has_comp && !comp_just_cleared) { Sleep(1); continue; }
```

### 5.3 CJK 글리프 간격 (glyph_atlas + quad_builder)

| 문제 | 해결 |
|------|------|
| CJK fallback 높이 축소 → advance 축소 → gap | CJK wide는 높이 축소 건너뛰기 |
| advance < 2*cell_w → gap | advance-centering (gap 대칭 분배) |
| 세로 오버플로우 | quad_builder cell-height clipping |

---

## 6. Implementation Order (실제 진행)

| Step | Task | 결과 |
|------|------|------|
| S1 | Hidden HWND 생성 + TSF AssociateFocus | WinUI3 InputSite 충돌 해결 |
| S2 | TSF DoCompositionUpdate → HandleOutput → ConPTY | 한글 확정 전달 |
| S3 | TSF HandleCompositionUpdate → m_composition | 조합 미리보기 |
| S4 | WM_CHAR HasActiveComposition 이중 입력 방지 | Bug #4 fix 포함 |
| S5 | RenderLoop 인라인 오버레이 렌더링 | comp_just_cleared 포함 |
| S6 | TSF GetTextExt/GetScreenExt 후보창 위치 | IDataProvider |
| S7 | Ctrl+V, Shift+Tab, Alt+key, DECCKM | HandleKeyDown 확장 |
| S8 | BS ghost fix (GetKeyState 감지) | OnEndComposition |
| S9 | CJK 간격 개선 (no-height-scale + advance-centering) | glyph_atlas + quad_builder |

---

## 7. Test Plan

### Tier 1: 단위 테스트 (headless, 33개)

| 테스트 | 파일 | 수 |
|--------|------|----|
| VT 한글 UTF-8 | tests/vt_core_test.cpp | 10 |
| ConPTY UTF-8 왕복 | tests/conpty_integration_test.cpp | 10 |
| QuadBuilder 렌더 | tests/quad_korean_test.cpp | 13 |

### Tier 2: E2E pyautogui (61개)

| 카테고리 | 파일 | 수 |
|---------|------|----|
| A: 한글 조합 | scripts/tests/test_a_hangul.py | 21 |
| B: 특수키/제어 | scripts/tests/test_b_special.py | 18 |
| C: 렌더링 시각 | scripts/tests/test_c_render.py | 8 |
| D: 포커스/윈도우 | scripts/tests/test_d_focus.py | 7 |
| E: 유니코드/인코딩 | scripts/tests/test_e_unicode.py | 7 |

---

## 8. QC Criteria

| # | Criteria | Status |
|---|----------|--------|
| QC-01 | 한글 조합 실시간 표시 (ㅎ→하→한) | PASS |
| QC-02 | 한글 확정 → ConPTY 전달 (echo 한글) | PASS |
| QC-03 | 이중 입력 방지 (WM_CHAR + TSF) | PASS |
| QC-04 | 영문 모드 불변 | PASS |
| QC-05 | IME 후보창 위치 (커서 근처) | PASS |
| QC-06 | BS 조합 취소 (ghost 없음) | PASS |
| QC-07 | Ctrl+V 클립보드 붙여넣기 | PASS |
| QC-08 | Shift+Tab, Alt+key readline | PASS |
| QC-09 | DECCKM (vim/tmux 화살표) | PASS |
| QC-10 | CJK 간격 (Alacritty 동등) | PASS |
| QC-11 | E2E 61/61 PASS | PASS |

---

## 9. Risks

| # | Risk | Impact | Mitigation | 결과 |
|---|------|--------|------------|------|
| R1 | WinUI3 InputSite TSF 충돌 | 상 | Hidden HWND로 분리 | 해결 |
| R2 | WM_CHAR + TSF 이중 입력 | 상 | HasActiveComposition + 제어문자 passthrough | 해결 |
| R3 | 렌더 스레드 경합 | 중 | m_ime_mutex | 해결 |
| R4 | BS ghost (OnEndComposition pending send) | 상 | GetKeyState(VK_BACK) 감지 | 해결 |
| R5 | CJK 높이 축소 → advance 축소 → gap | 중 | no-height-scale + advance-centering | 해결 |
| R6 | Grayscale AA 선명도 (vs ClearType) | 중 | ADR-010 구조적 제약. DPI-aware 렌더링으로 개선 가능 | 미해결 |

---

## 10. Related Documents

| Document | Path |
|----------|------|
| Plan | `docs/01-plan/features/tsf-ime.plan.md` |
| Phase 4-A 보고서 | `docs/archive/2026-03/winui3-shell/winui3-shell.report.md` |
| ADR-009 Code-only WinUI3 | `docs/adr/009-winui3-codeonly-cmake.md` |
| ADR-010 Grayscale AA | `docs/adr/010-grayscale-aa-composition.md` |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial design - IMM32 방식 |
| 2.0 | 2026-04-01 | Solit | TSF 전환 반영, 키 핸들링/CJK 간격/테스트 인프라 추가 |
