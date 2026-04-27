# Design-Implementation Gap Analysis Report: M-12 Settings UI

> **문서 종류**: Analysis (설계-구현 Gap 분석)
> **분석일**: 2026-04-16
> **설계 문서**: `docs/02-design/features/m12-settings-ui.design.md`
> **구현 경로**: `src/GhostWin.App/`, `src/GhostWin.Core/`, `src/GhostWin.Services/`

---

## 1. 한 줄 요약

W5(키바인딩 UI, v2 연기) 제외 시 **97% 일치**. 모든 차이는 UX 개선 또는 아키텍처 적응이며 기능 누락/설계 위반 없음.

---

## 2. 전체 점수

| 카테고리 | 점수 | 상태 |
|----------|:----:|:----:|
| W1: SettingsPageViewModel | 97% | OK |
| W2: SettingsPageControl XAML | 95% | OK |
| W3: 페이지 전환 + 포커스 | 95% | OK |
| W4: 폰트 UpdateCellMetrics + 루프 방지 | 100% | OK |
| W5: 키바인딩 편집 UI | 0% | 미구현 (v2 연기, 의도적) |
| W6: CommandPaletteWindow | 98% | OK |
| W7: OpenJson + 통합 검증 | 100% | OK |
| **전체 (W5 제외)** | **97%** | OK |
| **전체 (W5 포함)** | **83%** | OK (의도적 간소화) |

---

## 3. Wave별 상세 분석

### 3.1 W1: SettingsPageViewModel (97%)

14개 ObservableProperty, _suppressSave, LoadFromSettings, ScheduleSave(300ms), ApplyAndSave, LoadSystemFonts 모두 설계와 일치.

**추가 구현 (설계에 없음)**:

| 항목 | 구현 위치 | 설명 | 영향 |
|------|----------|------|------|
| `ThemeOptions` 컬렉션 | SettingsPageViewModel.cs:41 | `["dark", "light"]` — ComboBox 바인딩 개선 | 낮음 |
| `OpenJsonCommand` RelayCommand | SettingsPageViewModel.cs:106-115 | W7 기능을 VM에 직접 구현 | 낮음 |

### 3.2 W2: SettingsPageControl XAML (95%)

4개 카테고리, 스타일 리소스, 모든 입력 컨트롤 설계 일치.

**변경 사항**:

| 항목 | 설계 | 구현 | 영향 |
|------|------|------|------|
| 헤더 레이아웃 | ← 버튼 + "Settings" + X 버튼 | "← Settings" 단일 버튼, X 버튼 없음 | 낮음 |
| Theme ComboBox | ComboBoxItem 하드코딩 | ItemsSource=ThemeOptions 바인딩 | 낮음 (개선) |
| SidebarWidth 슬라이더 | 슬라이더만 | 슬라이더 + `{0}px` 텍스트 추가 | 낮음 (UX 개선) |
| CloseSettings 바인딩 | 직접 바인딩 | RelativeSource AncestorType=Window | 낮음 (필수 변경) |

### 3.3 W3: 페이지 전환 + 포커스 (95%)

Column 4 겹침, IsSettingsOpen Visibility 토글, Ctrl+,/Esc 키바인딩, 포커스 복원, 기어 아이콘 모두 일치.

**변경 사항**:

| 항목 | 설계 | 구현 | 영향 |
|------|------|------|------|
| PaneContainer 숨김 | PaneContainer만 InverseBoolToVis | 감싸는 Border 전체에 적용 | 낮음 (개선) |

### 3.4 W4: 폰트 UpdateCellMetrics + 루프 방지 (100%)

App.xaml.cs SettingsChangedMessage 구독, UpdateCellMetrics 호출, _suppressWatcher 플래그, 100ms 재활성화, OnFileChanged 가드, SettingsPageVM.LoadFromSettings 갱신 모두 설계와 완전 일치.

### 3.5 W5: 키바인딩 편집 UI (0% — 의도적 v2 연기)

설계 문서 Section 7에 상세 설계 포함되어 있으나, Section 11 S-3에서 "v2 범위"로 연기 명시. 사용자 분석 기준에서도 "Design에 있지만 v2로 연기" 확인.

### 3.6 W6: CommandPaletteWindow (98%)

별도 Window, SearchBox, 퍼지 검색, Enter/Esc/화살표 키, MouseDoubleClick, 8개 명령 등록, CommandInfo record 모두 일치.

**추가 구현**: ListBoxItem 선택/호버 스타일, CaretBrush, E2E AutomationId.

### 3.7 W7: OpenJson (100%)

OpenJson 버튼 XAML + OpenJsonCommand (Process.Start UseShellExecute) 완전 일치.

---

## 4. 의도적 간소화 확인 (설계 문서 Section 11)

| # | 항목 | 구현 상태 | 확인 |
|:-:|------|----------|:----:|
| S-1 | 프로파일 시스템 | 미구현 | OK |
| S-2 | 컬러 스키마 에디터 | 미구현 | OK |
| S-3 | 키바인딩 충돌 자동 해제 | W5 전체 미구현 | OK |
| S-4 | Mica 즉시 적용 | "(restart required)" 표시 | OK |
| S-5 | 설정 검색 | Command만 검색 | OK |
| S-6 | 다국어 | 영어 하드코딩 | OK |

---

## 5. 권장 조치

### 문서 갱신 필요 (낮은 우선순위)

1. 설계 문서 헤더 레이아웃을 실제 구현 ("← Settings" 단일 버튼) 으로 갱신
2. ThemeOptions 컬렉션, SidebarWidth px 표시 등 추가 구현 반영
3. W5를 "v2 범위" 주석으로 명확 분리 (현재 상세 설계 포함으로 혼동 여지)

### 즉시 조치 필요

없음.

---

## 6. 결론

설계-구현 일치율 **97%** (W5 v2 연기 제외). 구현이 설계를 충실히 따랐으며, 일부 차이는 모두 UX 개선이나 바인딩 아키텍처 적응에 해당. 기능 누락, 응답 형식 불일치, 설계 위반 없음.

---

*M-12 Settings UI Gap Analysis v1.0 (2026-04-16)*
