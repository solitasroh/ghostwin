# nerd-font-fallback PDCA Completion Report

> **Feature**: Nerd Font 폰트 폴백 체인 확장
> **Project**: GhostWin Terminal
> **Phase**: 4-D (Master: winui3-integration FR-10)
> **Date**: 2026-03-30
> **Match Rate**: 96%
> **Iterations**: 1 (Supplementary PUA-A 범위 추가)

---

## Executive Summary

### 1.1 Overview

| Item | Value |
|------|-------|
| Feature | nerd-font-fallback |
| Duration | 1 day (2026-03-30) |
| Files Changed | 2 (glyph_atlas.cpp, glyph_atlas.h) |
| Lines Added | ~187 |
| Lines Removed | ~47 |
| Match Rate | 96% |

### 1.2 Results

| Metric | Target | Actual |
|--------|--------|--------|
| FR Implementation | 3/3 | 3/3 (100%) |
| DoD Completion | 5/5 | 5/5 (100%) |
| QC Criteria | 5/5 | 5/5 (100%) |
| Tests | All pass | 23/23 PASS |
| Overall Match Rate | 90% | 96% |

### 1.3 Value Delivered

| Perspective | Result |
|-------------|--------|
| **Problem** | 하드코딩 4개 폰트 리스트만 순회하여 Nerd Font 심볼(U+E000~U+F8FF, U+F0001~U+F1AF0)이 tofu(□)로 표시됨 |
| **Solution** | `IDWriteFontFallbackBuilder`로 CJK→Nerd Font PUA(7범위)→Emoji→System 커스텀 체인 구축. 8개 Nerd Font 패밀리 자동 감지 + supplementary plane 직접 조회 |
| **Function/UX** | Starship 프롬프트 Windows 아이콘, 폴더 아이콘, git 브랜치 아이콘 정상 표시. Windows Terminal과 동등한 수준 |
| **Core Value** | 하드코딩 폴백 → 확장 가능한 체인으로 구조 개선. R8G8B8A8 포맷 활용으로 컬러 이모지 준비 완료 |

---

## 2. Implementation Summary

### 2.1 Architecture

```
Primary Font (Cascadia Mono)
    ↓ GetGlyphIndices == 0?
    ↓
IDWriteFontFallback::MapCharacters()
    ├── CJK (Malgun Gothic, YaHei, Yu Gothic)     U+2E80~U+9FFF, U+AC00~U+D7AF
    ├── Nerd Font PUA (auto-detected)              U+E000~U+F8FF
    ├── Emoji (Segoe UI Emoji/Symbol)              U+2600~U+27BF, U+1F300~U+1F9FF
    └── System default fallback
    ↓ MapCharacters 실패?
    ↓
Direct nerd_font_face->GetGlyphIndices()           U+F0001~U+F1AF0 (Supplementary PUA-A)
    ↓ 결과
GlyphEntry (+ 수평 중앙 정렬)
```

### 2.2 Key Changes

| Component | Before | After |
|-----------|--------|-------|
| 폴백 방식 | 하드코딩 `fallback_fonts[]` 4개 | `IDWriteFontFallback` 커스텀 체인 |
| Nerd Font | 미지원 | 8개 패밀리 자동 감지 + PUA 7범위 매핑 |
| Emoji | Segoe UI Emoji (리스트 내 포함) | 전용 Unicode 범위 매핑 |
| Supplementary Plane | 미지원 | `nerd_font_face` 직접 조회 |
| 글리프 정렬 | 좌측 정렬 | 좁은 폴백 글리프 수평 중앙 정렬 |

---

## 3. Lessons Learned

| Topic | Learning |
|-------|---------|
| IDWriteFontFallback + Supplementary Plane | `MapCharacters`가 U+10000 이상 코드포인트에서 폰트를 찾지 못할 수 있음. 직접 `GetGlyphIndices` 조회 필요 |
| AddMapping 시그니처 | `dwrite_2.h`의 실제 시그니처는 8개 인자. 설계 시 API 문서를 헤더 파일에서 직접 확인할 것 |
| Nerd Font 패밀리명 | v3에서 "NF" 접미사와 "Nerd Font" 접미사가 혼재. 양쪽 모두 감지 후보에 포함 필요 |

---

## Version History

| Version | Date | Author |
|---------|------|--------|
| 1.0 | 2026-03-30 | Solit |
