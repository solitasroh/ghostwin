# nerd-font-fallback Plan

> **Feature**: Nerd Font 폰트 폴백 체인 확장
> **Project**: GhostWin Terminal
> **Phase**: 4-D (Master: winui3-integration FR-10)
> **Date**: 2026-03-30
> **Author**: Solit
> **Dependency**: 없음 (현재 Win32 HWND에서 독립 구현 가능)

---

## Executive Summary

| Perspective | Description |
|-------------|-------------|
| **Problem** | Phase 3의 GlyphAtlas가 Primary + CJK 2단계 폴백만 지원하여 Nerd Font 심볼 (U+E000~U+F8FF)이 표시되지 않음 |
| **Solution** | DirectWrite `IDWriteFontFallback` 커스텀 빌더로 Primary → CJK → Nerd Font → Emoji 4단계 폴백 ���인 구축 |
| **Function/UX** | oh-my-posh, Starship 등 프롬프트의 아이콘/심볼이 정상 표시 |
| **Core Value** | 현대 터미널 사용자 기대 수준 충족. Powerline + 개발 도구 아이콘 지원 |

---

## 1. Background

Phase 3에서는 `GlyphAtlas`가 기본 폰트(Cascadia Mono)에서 글리프를 찾지 못하면 Malgun Gothic으로 폴백한다.
그러나 Nerd Font 심볼(PUA 영역)이나 Emoji는 두 폰트 모두에 없어 tofu(□)로 표시된다.

DirectWrite는 `IDWriteFontFallbackBuilder`를 통해 유니코드 범위별 커스텀 폴백을 정의할 수 있다.

---

## 2. Functional Requirements

### FR-01: IDWriteFontFallback 커스텀 체인 구축
- `IDWriteFontFallbackBuilder::AddMapping()` 으로 유니코드 범위별 폰트 매핑
- 체인: Primary (Cascadia Mono) → CJK (Malgun Gothic) → Nerd Font → Emoji (Segoe UI Emoji)
- Nerd Font 매핑 범위: U+E000~U+E0FF (Powerline), U+E200~U+E2FF (Seti-UI), U+E700~U+E7FF (Devicons), U+F000~U+F8FF (Font Awesome)

### FR-02: Nerd Font 자동 감지
- 시스템에 설치된 Nerd Font 패밀리 탐색 (`IDWriteFontCollection`)
- 감지 순서: "CaskaydiaCove Nerd Font" → "JetBrainsMono Nerd Font" → "Hack Nerd Font"
- 미설치 시 PUA 범위 폴백 건너뜀 (tofu 대신 빈칸)

### FR-03: 글리프 크기 정규화
- Nerd Font 글리프가 셀 크기 초과 시 Phase 3 한글 폴백 스케일링 패턴 재사용
- 아이콘이 셀 중앙에 정렬되도록 오프셋 조정

---

## 3. Implementation Steps

| # | Task | DoD |
|---|------|-----|
| S1 | IDWriteFontFallbackBuilder 커스텀 체인 구축 | 유니코드 범위별 폰트 매핑 등록 |
| S2 | Nerd Font 패밀리 자동 감지 (IDWriteFontCollection) | 설치된 Nerd Font 이름 검출 |
| S3 | GlyphAtlas에 폴백 체인 통합 (기존 2-tier → 4-tier) | Nerd Font 심볼 래스터화 성공 |
| S4 | 글리프 크기 정규화 + 셀 중앙 정렬 | 아이콘이 셀 안에 정상 배치 |
| S5 | Starship/oh-my-posh 프롬프트 아이콘 표시 테스트 | Powerline + Devicons 육안 확인 |

---

## 4. Definition of Done

| # | Criteria | Verification |
|---|----------|-------------|
| 1 | Nerd Font 심볼 (U+E000~U+F8FF) 렌더링 | Powerline 화살표, 폴더 ���이콘 표시 |
| 2 | Emoji 기본 표시 (Segoe UI Emoji) | 😀 등 기본 이모지 렌더링 |
| 3 | Nerd Font 미설치 시 graceful 처리 | 크래시 없이 빈칸 또는 대체 문자 |
| 4 | 기존 한글 폴�� 정상 유지 | Malgun Gothic 렌더링 불변 |
| 5 | 기존 테스트 PASS 유지 | 23/23 PASS |

---

## 5. Risks

| # | Risk | Impact | Mitigation |
|---|------|--------|------------|
| R1 | Nerd Font 패밀리명 다양성 | 중 | 주요 5개 패밀리 + 사용자 설정 옵션 |
| R2 | PUA 영역 폰트 간 충돌 | 하 | 우선순위 명확한 매핑 순서 |

---

## 6. References

| Document | Path |
|----------|------|
| Phase 3 GlyphAtlas (한글 폴백) | `src/renderer/glyph_atlas.h/cpp` |
| DX11 GPU 렌더링 리서치 | `docs/00-research/research-dx11-gpu-rendering.md` |
