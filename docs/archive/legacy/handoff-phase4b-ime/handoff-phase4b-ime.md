# Phase 4-B 한글 IME Handoff

## 현재 상태: TextBox 기반 접근 실패 → TSF 정석 구현 필요

### 결론

TextBox + 폴링/휴리스틱 접근은 **근본적으로 불가능**. 20+ 버전 반복에도 해결 안 됨.
**WT와 동일한 TSF(Text Services Framework) 직접 구현**이 유일한 정석.

---

## 실패한 접근들 (시도 + 실패 원인)

| # | 접근 | 실패 원인 | 근거 |
|---|------|----------|------|
| 1 | TSF COM 직접 (`ITfContextOwner`) | `AssociateFocus` 미사용 → 콜백 미수신 | Agent 분석으로 확인 |
| 2 | `CoreTextEditContext` | WinUI3 Desktop "not planned" (MS Issue #4239) | MS 공식 |
| 3 | IMM32 HWND Subclass | WinUI3 HWND에 WM_IME 미도달 | 실제 테스트 |
| 4 | `InputPreTranslateKeyboardSource` | XAML Islands 전용 (`ContentPreTranslateMessage` 필요) | Agent 분석 |
| 5 | `CharacterReceived` | 한글 확정 음절에 미발화 | 실제 테스트 (Image 8) |
| 6 | `TextCompositionStarted/Ended` | 8:2 비대칭 (Started 8회, Ended 2회) | 로그 확인 |
| 7 | `ImmGetCompositionString` | WinUI3에서 항상 빈값 | 진단 로그 확인 |
| 8 | TextBox.Text() + 5ms/8ms 타이머 폴링 | 조합/확정 구분 불가 (마지막 한글=조합 휴리스틱 실패) | 20+ 반복 실패 |
| 9 | TextBox.Text() + TextChanging 이벤트 | TextChanging이 조합 중 미발화 | 실제 키보드 테스트 |
| 10 | `m_comp_ended` 플래그 | 정상 조합 전환에서도 발화 → 사이드 이펙트 | Image: 한글 무시됨 |

---

## 다음 세션에서 해야 할 것: TSF + AssociateFocus

### WT가 하는 방식 (Agent 2 분석 결과)

```
1. TSF COM 초기화 (TF_TMAE_CONSOLE)           ← 우리도 성공 (10/10 PASS)
2. FindWindowOfActiveTSF() → InputSite HWND   ← 우리 미구현 ★
3. AssociateFocus(inputSiteHwnd, documentMgr)  ← 우리 미구현 ★
4. TSF 콜백으로 조합/확정 수신                    ← 우리는 콜백 안 옴 (2,3 누락 때문)
```

### FindWindowOfActiveTSF 구현 방법 (WT 소스)

```cpp
// WT: src/tsf/Implementation.cpp
HWND FindWindowOfActiveTSF() {
    // ITfThreadMgr에서 첫 번째 DocumentMgr 열거
    wil::com_ptr<IEnumTfDocumentMgrs> enumDocumentMgrs;
    _threadMgrEx->EnumDocumentMgrs(enumDocumentMgrs.addressof());

    wil::com_ptr<ITfDocumentMgr> document;
    enumDocumentMgrs->Next(1, document.addressof(), nullptr);

    wil::com_ptr<ITfContext> context;
    document->GetTop(context.addressof());

    wil::com_ptr<ITfContextView> view;
    context->GetActiveView(view.addressof());

    HWND hwnd;
    view->GetWnd(&hwnd);  // InputSite HWND 반환
    return hwnd;
}
```

### AssociateFocus 호출 (WT 소스)

```cpp
// WT: src/tsf/Implementation.cpp
void AssociateFocus(IDataProvider* provider) {
    _provider = provider;
    HWND hwnd = _provider->GetHwnd();  // InputSite HWND

    wil::com_ptr<ITfDocumentMgr> prev;
    _threadMgrEx->AssociateFocus(hwnd, _documentMgr.get(), prev.addressof());
}
```

### 핵심: SetFocus vs AssociateFocus

- `SetFocus(documentMgr)`: 즉시 포커스 설정 (우리가 했던 것) → WinUI3 내부 TSF와 충돌
- `AssociateFocus(hwnd, documentMgr)`: HWND에 포커스가 올 때 자동 활성화 → WinUI3와 공존

---

## 기존 TSF 코드 위치

| 파일 | 상태 | 설명 |
|------|------|------|
| `src/tsf/tsf_handle.h` | 구현됨 | IDataProvider, TsfHandle (pimpl) |
| `src/tsf/tsf_implementation.h` | 구현됨 | Implementation COM 클래스 선언 |
| `src/tsf/tsf_implementation.cpp` | 구현됨 | TSF COM 초기화/콜백 (AssociateFocus, FindWindowOfActiveTSF 누락) |
| `tests/tsf_init_test.cpp` | 통과 | 10/10 PASS — TSF 초기화 자체는 정상 |

### 추가 구현 필요 사항

1. **`FindWindowOfActiveTSF()`** — TsfImplementation에 추가
2. **`AssociateFocus()`** — Focus() 메서드를 AssociateFocus 기반으로 변경
3. **`winui_app.cpp`** — TextBox + 타이머 제거, TSF 콜백 기반으로 전환
4. **IDataProvider 구현** — GhostWinApp에서 HWND, 커서 위치, HandleOutput 제공

---

## 검증된 사실 (진단 로그 증명)

1. `TextBox.Text()`에 조합 텍스트 포함 ✅ (U+314E→U+D558→U+D55C)
2. TSF COM 초기화 정상 ✅ (10/10 PASS)
3. `ImmGetCompositionString` = WinUI3에서 빈값 ❌
4. `CharacterReceived` = 한글 확정에 미발화 ❌
5. `InputPreTranslateKeyboardSource` = XAML Islands 전용 ❌
6. TextComposition Started/Ended = 8:2 비대칭 ❌
7. "마지막 한글 = 조합" 휴리스틱 = Backspace/화살표에서 실패 ❌

---

## 현재 코드 상태 (커밋 안 됨)

`src/app/winui_app.cpp` — TextBox + 8ms 타이머 + OnTextChanging (실패 상태)
- 영문 입력: 동작 ✅
- 한글 조합: 부분 동작 (빠른 타이핑 시)
- Backspace: 실패 ❌ (ㅎ 잔존)
- 화살표 이중 렌더링: 수정됨 (FlushComposition)

### 권장: 커밋하지 말고 TSF로 완전 교체

---

## Agent 분석 결과 요약

### Agent 2 (WT TermControl 분석) — 완료

> WT는 TextBox를 사용하지 않는다. TSF 직접 구현.
> PreviewKeyDown + CharacterReceived + TsfDataProvider.
> Backspace: TSF가 조합 중이면 TSF가 처리, 아니면 terminal에 DEL.
> 한국어: TS_SS_TRANSITORY 플래그, reconversion 미지원.

### Agent 5 (아키텍처 설계) — 완료

> TextChanging 이벤트 기반 Diff 엔진 설계 (TextBox 방식의 최선).
> 하지만 근본적으로 TextBox.Text()로는 조합/확정 구분 불가.
> TSF 직접 사용이 유일한 근본 해결.

### Agent 4 (WinUI3 이벤트 순서) — 완료

> PreviewKeyDown은 Backspace에 발화함 (tunneling).
> CharacterReceived는 IME 활성 시 미발화.
> TextChanging → TextChanged → TextCompositionChanged 순서 (조합 중에도).
> IME 활성 시 VK_PROCESSKEY(229)로 키 전달.

### Agent 1 (코드 버그) — 완료

> 8개 버그 식별. P0: Backspace ConPTY 미전송, 자모 필터 과도.
> 근본 원인: "마지막 한글=조합" 휴리스틱이 조합/확정 구분 불가.

### Agent 3 (Alacritty/winit) — 완료

> winit: 순수 Win32 HWND + WM_IME_COMPOSITION 직접 처리.
> IME 조합 중 Backspace → IME가 WM_KEYDOWN 소비 → 앱에 미도달.
> Alacritty: IME 조합 중이면 key_input에서 즉시 반환.

---

## 빌드 방법

```powershell
# 반드시 스크립트 사용 (cmake 직접 실행 금지)
powershell -ExecutionPolicy Bypass -File scripts/build_ghostwin.ps1 -Config Release

# incremental 빌드 (build 디렉토리 유지)
powershell -ExecutionPolicy Bypass -File scripts/build_incremental.ps1
```

## 실행

```
build\ghostwin_winui.exe    # Phase 4 WinUI3 앱 (IME 작업 대상)
build\ghostwin_terminal.exe # Phase 3 PoC (순수 Win32, IME 없음)
```
