# nerd-font-fallback Design

> **Feature**: Nerd Font 폰트 폴백 체인 확장
> **Project**: GhostWin Terminal
> **Phase**: 4-D (Master: winui3-integration FR-10)
> **Date**: 2026-03-30
> **Author**: Solit
> **Plan**: `docs/01-plan/features/nerd-font-fallback.plan.md`

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3의 GlyphAtlas가 하드코딩된 4개 폴백 폰트 리스트만 순회하여 Nerd Font 심볼(U+E000~U+F8FF)이 표시되지 않음 |
| **Solution** | `IDWriteFontFallbackBuilder`로 유니코드 범위별 커스텀 폴백 체인 구축 + Nerd Font 자동 감지 |
| **Function/UX** | Starship/oh-my-posh 프롬프트 아이콘, Powerline 심볼, 기본 이모지 표시 |
| **Core Value** | 현대 터미널 사용자 기대 충족. 하드코딩 폴백 → 확장 가능한 체인으로 구조 개선 |

---

## 1. Current State Analysis

### 1.1 현재 폴백 로직 (`glyph_atlas.cpp:253-285`)

```cpp
// 하드코딩된 4개 폰트 순차 시도
const wchar_t* fallback_fonts[] = {
    L"Malgun Gothic", L"Microsoft YaHei", L"Yu Gothic", L"Segoe UI Emoji", nullptr
};
// ... for loop: FindFamilyName → GetGlyphIndices → break on hit
```

**문제점:**
1. Nerd Font 폰트가 리스트에 없어 PUA 심볼 미표시
2. 매 글리프마다 `GetSystemFontCollection` + `FindFamilyName` 반복 호출 (비효율)
3. 폰트 추가/변경 시 코드 수정 필요 (확장 불가)

### 1.2 변경 대상

| File | Lines | Current | Change |
|------|-------|---------|--------|
| `glyph_atlas.cpp` | 38-67 (Impl) | font_face 1개 | + `custom_fallback`, + `nerd_font_name` |
| `glyph_atlas.cpp` | 73-150 (init_dwrite) | 폰트 1개 초기화 | + 폴백 체인 빌드 + Nerd Font 감지 |
| `glyph_atlas.cpp` | 221-288 (rasterize) | 하드코딩 폴백 루프 | → `custom_fallback->MapCharacters()` |
| `glyph_atlas.h` | AtlasConfig | font_family만 | + nerd_font_family (선택) |

---

## 2. Detailed Design

### 2.1 Impl 구조체 추가 멤버

```cpp
struct GlyphAtlas::Impl {
    // ... 기존 멤버 ...
    ComPtr<IDWriteFontFallback> custom_fallback;  // 커스텀 폴백 체인
    std::wstring nerd_font_name;                  // 감지된 Nerd Font 패밀리명 (빈 문자열이면 미설치)
    // ...
    bool build_fallback_chain(const AtlasConfig& config);
};
```

### 2.2 Nerd Font 자동 감지

`init_dwrite()` 내에서 시스템 폰트 컬렉션을 탐색하여 Nerd Font 설치 여부를 확인한다.

```cpp
bool GlyphAtlas::Impl::build_fallback_chain(const AtlasConfig& config) {
    ComPtr<IDWriteFactory2> factory2;
    if (FAILED(dwrite_factory.As(&factory2))) return false;

    ComPtr<IDWriteFontFallbackBuilder> builder;
    if (FAILED(factory2->CreateFontFallbackBuilder(&builder))) return false;

    ComPtr<IDWriteFontCollection> collection;
    dwrite_factory->GetSystemFontCollection(&collection);

    // 1. Nerd Font 자동 감지
    const wchar_t* nerd_candidates[] = {
        L"CaskaydiaCove Nerd Font",
        L"CaskaydiaCove NF",
        L"JetBrainsMono Nerd Font",
        L"JetBrainsMono NF",
        L"Hack Nerd Font",
        L"Hack NF",
        L"FiraCode Nerd Font",
        L"FiraCode NF",
        nullptr
    };
    for (auto* name = nerd_candidates; *name; ++name) {
        UINT32 idx = 0; BOOL exists = FALSE;
        collection->FindFamilyName(*name, &idx, &exists);
        if (exists) {
            nerd_font_name = *name;
            break;
        }
    }

    // 2. CJK 폴백 매핑 (U+2E80~U+9FFF, U+F900~U+FAFF, U+3000~U+303F)
    const wchar_t* cjk_fonts[] = { L"Malgun Gothic", L"Microsoft YaHei", L"Yu Gothic" };
    DWRITE_UNICODE_RANGE cjk_ranges[] = {
        { 0x2E80, 0x9FFF },   // CJK Radicals ~ CJK Unified Ideographs
        { 0x3000, 0x303F },   // CJK Symbols and Punctuation
        { 0xF900, 0xFAFF },   // CJK Compatibility Ideographs
        { 0xAC00, 0xD7AF },   // Hangul Syllables
    };
    builder->AddMapping(cjk_ranges, 4, cjk_fonts, 3, collection.Get(),
                        nullptr, nullptr, 0, 1.0f);

    // 3. Nerd Font PUA 매핑 (설치된 경우만)
    if (!nerd_font_name.empty()) {
        const wchar_t* nf_fonts[] = { nerd_font_name.c_str() };
        DWRITE_UNICODE_RANGE nerd_ranges[] = {
            { 0xE000, 0xE0FF },   // Powerline + Powerline Extra
            { 0xE200, 0xE2FF },   // Seti-UI + Custom
            { 0xE700, 0xE7FF },   // Devicons
            { 0xEA60, 0xEBEB },   // Codicons
            { 0xF000, 0xF2FF },   // Font Awesome
            { 0xF300, 0xF8FF },   // Font Awesome Extension + Weather + Material
        };
        builder->AddMapping(nerd_ranges, 6, nf_fonts, 1, collection.Get(),
                            nullptr, nullptr, 0, 1.0f);
    }

    // 4. Emoji 매핑 (U+1F300~U+1F9FF, U+2600~U+27BF)
    const wchar_t* emoji_fonts[] = { L"Segoe UI Emoji", L"Segoe UI Symbol" };
    DWRITE_UNICODE_RANGE emoji_ranges[] = {
        { 0x2600, 0x27BF },   // Miscellaneous Symbols + Dingbats
        { 0x1F300, 0x1F9FF }, // Emoji
    };
    builder->AddMapping(emoji_ranges, 2, emoji_fonts, 2, collection.Get(),
                        nullptr, nullptr, 0, 1.0f);

    // 5. 시스템 기본 폴백을 체인 끝에 추가
    ComPtr<IDWriteFontFallback> system_fallback;
    factory2->GetSystemFontFallback(&system_fallback);
    builder->AddMappings(system_fallback.Get());

    // 6. 빌드
    return SUCCEEDED(builder->CreateFontFallback(&custom_fallback));
}
```

### 2.3 `rasterize_glyph` 폴백 로직 교체

**Before** (`glyph_atlas.cpp:221-288`): 하드코딩 폰트 리스트 순회

**After**: `IDWriteFontFallback::MapCharacters()` 사용

```cpp
font_face->GetGlyphIndices(&codepoint, 1, &glyph_index);
if (glyph_index == 0 && codepoint > 0x7F) {
    // MapCharacters로 폴백 폰트 탐색
    if (custom_fallback) {
        wchar_t wch[3] = {};
        int wlen = 0;
        if (codepoint <= 0xFFFF) {
            wch[0] = (wchar_t)codepoint;
            wlen = 1;
        } else {
            uint32_t cp = codepoint - 0x10000;
            wch[0] = (wchar_t)(0xD800 + (cp >> 10));
            wch[1] = (wchar_t)(0xDC00 + (cp & 0x3FF));
            wlen = 2;
        }

        // IDWriteTextAnalysisSource 구현 필요 — 단순 래퍼 사용
        ComPtr<IDWriteFont> mapped_font;
        UINT32 mapped_len = 0;
        float mapped_scale = 1.0f;

        HRESULT hr = custom_fallback->MapCharacters(
            /* analysisSource */ text_analysis_source(wch, wlen),
            /* textPosition */ 0,
            /* textLength */ wlen,
            /* baseFontCollection */ nullptr,
            /* baseFamilyName */ nullptr,
            /* baseWeight */ DWRITE_FONT_WEIGHT_REGULAR,
            /* baseStyle */ DWRITE_FONT_STYLE_NORMAL,
            /* baseStretch */ DWRITE_FONT_STRETCH_NORMAL,
            &mapped_len, &mapped_font, &mapped_scale);

        if (SUCCEEDED(hr) && mapped_font) {
            ComPtr<IDWriteFontFace> ff_face;
            mapped_font->CreateFontFace(&ff_face);
            if (ff_face) {
                uint16_t gi = 0;
                ff_face->GetGlyphIndices(&codepoint, 1, &gi);
                if (gi != 0) {
                    fallback_face = ff_face;
                    face_to_use = fallback_face.Get();
                    glyph_index = gi;
                }
            }
        }
    }
    if (glyph_index == 0) return entry;
}
```

### 2.4 IDWriteTextAnalysisSource 최소 구현

`MapCharacters()`에는 `IDWriteTextAnalysisSource` 인터페이스가 필요하다. 단일 문자열만 반환하는 최소 구현:

```cpp
class SimpleTextAnalysisSource : public IDWriteTextAnalysisSource {
    const wchar_t* text_;
    UINT32 len_;
    ULONG ref_ = 1;
public:
    SimpleTextAnalysisSource(const wchar_t* t, UINT32 l) : text_(t), len_(l) {}

    // IUnknown
    ULONG STDMETHODCALLTYPE AddRef() override { return ++ref_; }
    ULONG STDMETHODCALLTYPE Release() override {
        if (--ref_ == 0) { delete this; return 0; } return ref_;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IDWriteTextAnalysisSource)) {
            *ppv = this; AddRef(); return S_OK;
        }
        *ppv = nullptr; return E_NOINTERFACE;
    }

    // IDWriteTextAnalysisSource
    HRESULT STDMETHODCALLTYPE GetTextAtPosition(UINT32 pos, const WCHAR** text, UINT32* len) override {
        if (pos >= len_) { *text = nullptr; *len = 0; }
        else { *text = text_ + pos; *len = len_ - pos; }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetTextBeforePosition(UINT32 pos, const WCHAR** text, UINT32* len) override {
        if (pos == 0 || pos > len_) { *text = nullptr; *len = 0; }
        else { *text = text_; *len = pos; }
        return S_OK;
    }
    DWRITE_READING_DIRECTION STDMETHODCALLTYPE GetParagraphReadingDirection() override {
        return DWRITE_READING_DIRECTION_LEFT_TO_RIGHT;
    }
    HRESULT STDMETHODCALLTYPE GetLocaleName(UINT32, UINT32*, const WCHAR** name) override {
        *name = L"en-us"; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetNumberSubstitution(UINT32, UINT32*, IDWriteNumberSubstitution** sub) override {
        *sub = nullptr; return S_OK;
    }
};
```

### 2.5 AtlasConfig 확장

```cpp
struct AtlasConfig {
    // ... 기존 ...
    const wchar_t* nerd_font_family = nullptr;  // 사용자 지정 Nerd Font (nullptr이면 자동 감지)
};
```

### 2.6 글리프 크기 정규화

기존 폴백 스케일링 로직(`glyph_atlas.cpp:300-304`)을 그대로 재사용. Nerd Font 글리프도 `face_to_use != font_face.Get()` 조건에 해당하므로 자동으로 셀 초과 시 축소된다.

추가로 Nerd Font 아이콘은 폭도 초과할 수 있으므로 수평 중앙 정렬 오프셋 계산:

```cpp
// 폴백 폰트 글리프 수평 중앙 정렬
if (face_to_use != font_face.Get()) {
    float glyph_advance = gm.advanceWidth * scale;
    if (glyph_advance < (float)cell_w) {
        entry.offset_x += ((float)cell_w - glyph_advance) * 0.5f;
    }
}
```

---

## 3. Implementation Order

| Step | Task | Files | DoD |
|------|------|-------|-----|
| S1 | `SimpleTextAnalysisSource` COM 클래스 | `glyph_atlas.cpp` (상단 추가) | 컴파일 성공 |
| S2 | `build_fallback_chain()` 구현 + Nerd Font 감지 | `glyph_atlas.cpp` (Impl 메서드) | 폴백 체인 빌드 성공 로그 |
| S3 | `rasterize_glyph` 폴백 로직 교체 (MapCharacters) | `glyph_atlas.cpp:221-288` | 하드코딩 루프 제거 |
| S4 | 글리프 수평 중앙 정렬 | `glyph_atlas.cpp:300-310` | Nerd Font 아이콘 셀 중앙 |
| S5 | AtlasConfig에 nerd_font_family 추가 | `glyph_atlas.h` | 사용자 지정 옵션 |

### 의존 관계

```
S1 → S2 → S3 (순차, 핵심 경로)
S3 → S4 (정렬은 폴백 동작 후)
S5 (S2와 병행 가능)
```

---

## 4. Test Plan

### 4.1 자동 테스트

| # | Test | Expected |
|---|------|----------|
| T1 | 기존 23/23 테스트 | PASS (회귀 없음) |
| T2 | dx11_render_test S7 글리프 래스터화 | 9/9 + Nerd Font 추가 글리프 |

### 4.2 육안 검증

| # | Test | Expected |
|---|------|----------|
| V1 | Starship 프롬프트 | 폴더/git 아이콘 표시 |
| V2 | `echo -e '\uE0B0\uE0B2'` | Powerline 화살표 표시 |
| V3 | Nerd Font 미설치 환경 | 크래시 없이 빈칸/대체 |
| V4 | 한글 + CJK | 기존 Malgun Gothic 불변 |
| V5 | 이모지 (😀) | Segoe UI Emoji 표시 |

---

## 5. QC Criteria

| # | Criteria | Target |
|---|----------|--------|
| QC-01 | Nerd Font PUA 심볼 렌더링 | Powerline + Devicons 표시 |
| QC-02 | 이모지 기본 렌더링 | Segoe UI Emoji 폴백 |
| QC-03 | 미설치 graceful 처리 | 크래시 없음 |
| QC-04 | CJK 폴백 호환 | 한글 렌더링 불변 |
| QC-05 | 기존 테스트 23/23 PASS | 회귀 없음 |

---

## Version History

| Version | Date | Author | Changes |
|---------|------|--------|---------|
| 1.0 | 2026-03-30 | Solit | Initial design |
