# tsf-ime Design

> **Feature**: 한글 IME 입력 지원 (IMM32 + HWND Subclass)
> **Project**: GhostWin Terminal
> **Phase**: 4-B (Master: winui3-integration FR-08)
> **Date**: 2026-03-30
> **Author**: Solit
> **Plan**: `docs/01-plan/features/tsf-ime.plan.md`
> **Revision**: 1.0
> **Scope**: 한글 전용 (CJK 범용은 향후 확장)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | WinUI3 CharacterReceived는 완성된 문자만 전달. 한글 조합 중(ㅎ→하→한) 상태를 표시할 수 없음 |
| **Solution** | WinUI3 Window HWND를 획득하여 IMM32 서브클래싱. WM_IME_COMPOSITION으로 조합/확정 분리 처리 |
| **Function/UX** | 한글 입력 시 조합 중 글리프가 커서 위치에 밑줄과 함께 실시간 표시. 확정 시 ConPTY 전달 |
| **Core Value** | 한국어 사용자 필수 기능. 조합형 한글이 터미널에서 자연스럽게 동작 |

---

## 1. Approach Decision

### 1.1 IMM32 vs TSF

| 방식 | 장점 | 단점 |
|------|------|------|
| **IMM32** | 구현 단순, Win32 표준, HWND 서브클래스로 완결 | 레거시 API |
| TSF | 현대적, UWP/WinUI 네이티브 | COM 인터페이스 다수, 구현 복잡 |

**결정: IMM32** — WinUI3 데스크톱 앱에서 HWND를 획득 가능하므로 IMM32가 최소 구현 경로. 한글 전용 범위에서 TSF의 복잡도는 불필요.

### 1.2 WinUI3 HWND 획득

```cpp
#include <microsoft.ui.xaml.window.h>  // IWindowNative

HWND GetWindowHandle(winrt::Microsoft::UI::Xaml::Window const& window) {
    auto windowNative = window.as<IWindowNative>();
    HWND hwnd = nullptr;
    winrt::check_hresult(windowNative->get_WindowHandle(&hwnd));
    return hwnd;
}
```

### 1.3 CharacterReceived와의 공존

WinUI3 `CharacterReceived` 이벤트는 IME 확정 문자도 전달한다. IMM32에서 확정 문자를 직접 처리하면 **이중 입력**이 발생한다.

**해결:** IME 조합 중일 때 `CharacterReceived`에서 한글 확정 문자를 무시하고, IMM32 `GCS_RESULTSTR`에서만 ConPTY에 전달.

```
IME OFF (영문):  CharacterReceived → send_input (기존 경로 유지)
IME ON  (한글):
  조합 중:     WM_IME_COMPOSITION + GCS_COMPSTR → 조합 문자 렌더링
  조합 확정:   WM_IME_COMPOSITION + GCS_RESULTSTR → send_input
               CharacterReceived에서 해당 문자 → Handled=true (무시)
```

---

## 2. Architecture

### 2.1 컴포넌트 구조

```
┌─────────────────────────────────────────────┐
│ UI Thread (ASTA)                             │
│                                              │
│  WndProc (HWND Subclass)                     │
│    WM_IME_COMPOSITION                        │
│      GCS_COMPSTR → m_composition (atomic)    │
│      GCS_RESULTSTR → send_input              │
│    WM_IME_STARTCOMPOSITION                   │
│      → SetCompositionWindow (후보창 위치)      │
│    WM_IME_ENDCOMPOSITION                     │
│      → m_composition 초기화                   │
│                                              │
│  CharacterReceived                           │
│    m_composing == true ? Handled : send_input│
├─────────────────────────────────────────────┤
│ Render Thread                                │
│    m_composition 읽기 → 커서 위치에 렌더링     │
│    (밑줄 스타일 QuadInstance)                  │
└─────────────────────────────────────────────┘
```

### 2.2 공유 상태

| 변수 | 타입 | 접근 | 보호 |
|------|------|------|------|
| `m_composition` | `std::wstring` | UI(write), Render(read) | `m_ime_mutex` |
| `m_composing` | `std::atomic<bool>` | UI(write), Render/CharReceived(read) | atomic |
| `m_ime_cursor_col` | `std::atomic<uint16_t>` | UI(write) | atomic |
| `m_ime_cursor_row` | `std::atomic<uint16_t>` | UI(write) | atomic |

### 2.3 파일 구조

```
src/
├── app/
│   ├── winui_app.h         ← + IME 멤버 변수, WndProc 선언
│   ├── winui_app.cpp       ← + HWND 서브클래스, IME 메시지 처리
│   └── main_winui.cpp      ← 변경 없음
├── renderer/
│   ├── quad_builder.h/cpp  ← + build_composition() 메서드
│   └── ...                 ← 변경 없음
```

---

## 3. IME Message Handling

### 3.1 HWND Subclass 등록

```cpp
// OnLaunched 또는 Panel.Loaded에서 호출
void GhostWinApp::SetupImeSubclass() {
    auto windowNative = m_window.as<IWindowNative>();
    winrt::check_hresult(windowNative->get_WindowHandle(&m_hwnd));

    // WndProc 서브클래스 등록
    SetWindowSubclass(m_hwnd, ImeSubclassProc, /*subclassId=*/1,
                      reinterpret_cast<DWORD_PTR>(this));
}
```

### 3.2 WndProc 서브클래스 콜백

```cpp
static LRESULT CALLBACK ImeSubclassProc(
        HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam,
        UINT_PTR /*id*/, DWORD_PTR refData) {
    auto* app = reinterpret_cast<GhostWinApp*>(refData);

    switch (msg) {
    case WM_IME_STARTCOMPOSITION: {
        app->OnImeStartComposition();
        return 0;  // 기본 IME 창 표시 억제
    }
    case WM_IME_COMPOSITION: {
        app->OnImeComposition(hwnd, lParam);
        return 0;
    }
    case WM_IME_ENDCOMPOSITION: {
        app->OnImeEndComposition();
        return 0;
    }
    case WM_NCDESTROY:
        RemoveWindowSubclass(hwnd, ImeSubclassProc, 1);
        break;
    }
    return DefSubclassProc(hwnd, msg, wParam, lParam);
}
```

### 3.3 조합/확정 처리

```cpp
void GhostWinApp::OnImeStartComposition() {
    m_composing.store(true, std::memory_order_release);

    // IME 후보창 위치 설정
    HIMC hImc = ImmGetContext(m_hwnd);
    if (hImc) {
        auto cursor = m_state ? m_state->frame().cursor : CursorInfo{};
        COMPOSITIONFORM cf{};
        cf.dwStyle = CFS_POINT;
        cf.ptCurrentPos.x = cursor.x * m_atlas->cell_width();
        cf.ptCurrentPos.y = cursor.y * m_atlas->cell_height();
        ImmSetCompositionWindow(hImc, &cf);
        ImmReleaseContext(m_hwnd, hImc);
    }
}

void GhostWinApp::OnImeComposition(HWND hwnd, LPARAM lParam) {
    HIMC hImc = ImmGetContext(hwnd);
    if (!hImc) return;

    if (lParam & GCS_RESULTSTR) {
        // 확정 문자 → ConPTY 전달
        LONG bytes = ImmGetCompositionStringW(hImc, GCS_RESULTSTR, nullptr, 0);
        if (bytes > 0) {
            std::wstring result(bytes / sizeof(wchar_t), L'\0');
            ImmGetCompositionStringW(hImc, GCS_RESULTSTR, result.data(), bytes);
            char utf8[16];
            int len = WideCharToMultiByte(CP_UTF8, 0,
                result.data(), (int)result.size(),
                utf8, sizeof(utf8), nullptr, nullptr);
            if (len > 0 && m_session) {
                m_session->send_input({
                    reinterpret_cast<uint8_t*>(utf8), static_cast<size_t>(len)});
            }
        }
    }

    if (lParam & GCS_COMPSTR) {
        // 조합 중 문자 → 렌더링용 저장
        LONG bytes = ImmGetCompositionStringW(hImc, GCS_COMPSTR, nullptr, 0);
        std::wstring comp;
        if (bytes > 0) {
            comp.resize(bytes / sizeof(wchar_t));
            ImmGetCompositionStringW(hImc, GCS_COMPSTR, comp.data(), bytes);
        }
        {
            std::lock_guard lock(m_ime_mutex);
            m_composition = std::move(comp);
        }
    }

    ImmReleaseContext(hwnd, hImc);
}

void GhostWinApp::OnImeEndComposition() {
    m_composing.store(false, std::memory_order_release);
    {
        std::lock_guard lock(m_ime_mutex);
        m_composition.clear();
    }
}
```

### 3.4 CharacterReceived 수정

```cpp
m_panel.CharacterReceived([self = get_strong()](auto&&,
        CharacterReceivedRoutedEventArgs const& e) {
    if (!self->m_session) return;

    // IME 조합 중이면 확정 문자를 무시 (IMM32에서 직접 처리)
    wchar_t ch = e.Character();
    if (self->m_composing.load(std::memory_order_acquire) && ch >= 0xAC00 && ch <= 0xD7A3) {
        e.Handled(true);
        return;
    }

    // 기존 처리 유지 (영문, surrogate pair, 제어 문자)
    // ...
});
```

한글 완성형 유니코드 범위 `0xAC00~0xD7A3` (가~힣)으로 판별.

---

## 4. Composition Rendering

### 4.1 렌더 스레드에서 조합 문자 표시

```cpp
// RenderLoop 내부 — 정상 렌더 후 조합 오버레이
if (m_composing.load(std::memory_order_acquire)) {
    std::wstring comp;
    {
        std::lock_guard lock(m_ime_mutex);
        comp = m_composition;
    }
    if (!comp.empty()) {
        auto cursor = m_state->frame().cursor;
        uint32_t comp_count = builder.build_composition(
            comp, cursor.x, cursor.y, *m_atlas, m_renderer->context(),
            std::span<QuadInstance>(m_staging).subspan(count));
        count += comp_count;
    }
}

if (count > 0) {
    m_renderer->upload_and_draw(m_staging.data(), count);
}
```

### 4.2 QuadBuilder::build_composition

```cpp
uint32_t QuadBuilder::build_composition(
        const std::wstring& text,
        uint16_t cursor_col, uint16_t cursor_row,
        GlyphAtlas& atlas, ID3D11DeviceContext* ctx,
        std::span<QuadInstance> out) {
    uint32_t idx = 0;
    uint16_t col = cursor_col;

    for (wchar_t ch : text) {
        uint32_t cp = static_cast<uint32_t>(ch);
        auto glyph = atlas.lookup_or_rasterize(ctx, cp, 0);

        if (idx + 2 > out.size()) break;

        // 배경 (밑줄 하이라이트 — 조합 중 표시)
        auto& bg = out[idx++];
        bg = {};
        bg.pos_x = col * cell_w_;
        bg.pos_y = cursor_row * cell_h_;
        bg.size_x = static_cast<uint16_t>(cell_w_);
        bg.size_y = static_cast<uint16_t>(cell_h_);
        bg.bg_packed = 0xFF444444;  // 조합 배경색 (어두운 회색)
        bg.fg_packed = 0xFF444444;
        bg.shading_type = 0;  // TextBackground

        // 글리프
        if (glyph.valid) {
            auto& fg = out[idx++];
            fg = {};
            fg.pos_x = col * cell_w_ + static_cast<uint16_t>(glyph.offset_x);
            fg.pos_y = cursor_row * cell_h_ + static_cast<uint16_t>(
                baseline_ - glyph.offset_y);
            fg.size_x = static_cast<uint16_t>(glyph.width);
            fg.size_y = static_cast<uint16_t>(glyph.height);
            fg.tex_u = static_cast<uint16_t>(glyph.u);
            fg.tex_v = static_cast<uint16_t>(glyph.v);
            fg.tex_w = static_cast<uint16_t>(glyph.width);
            fg.tex_h = static_cast<uint16_t>(glyph.height);
            fg.fg_packed = 0xFFFFFFFF;  // 흰색 전경
            fg.bg_packed = 0x00000000;
            fg.shading_type = 1;  // TextGlyph
        }

        col += (cp >= 0x1100 && cp <= 0x11FF) || (cp >= 0xAC00 && cp <= 0xD7A3) ? 2 : 1;
    }
    return idx;
}
```

---

## 5. Implementation Order

| Step | Task | Files | DoD |
|------|------|-------|-----|
| S1 | HWND 획득 + 서브클래스 등록 | `winui_app.h/cpp` | HWND 서브클래스 등록 성공, WM_IME 메시지 수신 확인 |
| S2 | WM_IME_COMPOSITION 처리 (GCS_RESULTSTR) | `winui_app.cpp` | 한글 확정 문자가 ConPTY에 전달 (`echo 한글` 동작) |
| S3 | WM_IME_COMPOSITION 처리 (GCS_COMPSTR) | `winui_app.cpp` | 조합 중 문자가 m_composition에 저장 |
| S4 | CharacterReceived 이중 입력 방지 | `winui_app.cpp` | 한글 확정 시 이중 입력 없음 |
| S5 | build_composition() 렌더링 | `quad_builder.h/cpp`, `winui_app.cpp` | 조합 중 글리프가 커서 위치에 표시 |
| S6 | IME 후보창 위치 (ImmSetCompositionWindow) | `winui_app.cpp` | 후보창이 커서 근처에 표시 |

---

## 6. Test Plan

| # | Test | Expected |
|---|------|----------|
| T1 | 영문 입력 (IME OFF) | 기존과 동일 동작 |
| T2 | 한글 조합 (ㅎ→하→한→한글) | 조합 중 글리프 실시간 표시 |
| T3 | 한글 확정 → echo | 셸에 한글 출력 |
| T4 | 한/영 전환 | 한글↔영문 자유 전환 |
| T5 | 백스페이스로 조합 취소 | 조합 문자 삭제 |
| T6 | 빠른 타이핑 | 조합 누락 없음 |
| T7 | IME 후보창 위치 | 커서 근처에 표시 |
| T8 | 기존 테스트 유지 | 7/7 PASS |

---

## 7. QC Criteria

| # | Criteria | Target |
|---|----------|--------|
| QC-01 | 한글 조합 실시간 표시 | ㅎ→하→한→한글 조합 과정 렌더링 |
| QC-02 | 한글 확정 → ConPTY 전달 | echo 한글 정상 출력 |
| QC-03 | 이중 입력 방지 | IMM32 + CharacterReceived 충돌 없음 |
| QC-04 | 영문 모드 불변 | 한/영 전환 후 영문 입력 동일 |
| QC-05 | IME 후보창 위치 | 터미널 커서 근처 |
| QC-06 | 기존 테스트 유지 | 7/7 PASS |

---

## 8. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | WinUI3 HWND 서브클래스가 XAML 이벤트 경로 간섭 | 중 | DefSubclassProc 호출로 기본 처리 유지. IME 메시지만 가로챔 |
| R2 | CharacterReceived와 IMM32 이중 입력 | 상 | m_composing flag + 한글 유니코드 범위 필터링 |
| R3 | 렌더 스레드에서 m_composition 읽기 경합 | 중 | m_ime_mutex (짧은 임계 구간) |
| R4 | IME 후보창 DPI 스케일 미적용 | 하 | CompositionScale 적용하여 물리 좌표 변환 |

---

## 9. Related Documents

| Document | Path |
|----------|------|
| Plan | `docs/01-plan/features/tsf-ime.plan.md` |
| WinUI3 + DX11 리서치 (Section 5: IME) | `docs/00-research/research-winui3-dx11.md` |
| Phase 4-A 보고서 | `docs/archive/2026-03/winui3-shell/winui3-shell.report.md` |
| ADR-009 Code-only WinUI3 | `docs/adr/009-winui3-codeonly-cmake.md` |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial design — 한글 전용 IMM32 방식 |
