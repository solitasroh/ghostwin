# M-12 Settings UI 완료 보고서

> **문서 종류**: PDCA Completion Report
> **마일스톤**: M-12
> **작성일**: 2026-04-17
> **마일스톤 소유자**: 노수장
> **기간**: 2026-04-17 (단일 세션)
> **설계-구현 일치도**: 97%

---

## Executive Summary

### 1.1 기능 개요

M-12 Settings UI는 터미널 설정을 GUI로 관리할 수 있게 함으로써 사용자가 `%APPDATA%/GhostWin/ghostwin.json`을 직접 편집하지 않아도 되도록 한 기능입니다. 사이드바 기어 아이콘 또는 `Ctrl+,`로 설정 페이지를 열 수 있으며, `Ctrl+Shift+P`로 Command Palette를 통해 빠른 명령을 실행할 수 있습니다.

**완료 범위**: 
- ✅ 설정 페이지 (4개 카테고리: 외관/폰트/사이드바/알림)
- ✅ Command Palette (별도 Window, 8개 명령)
- ✅ JSON 수동 편집과 완전 양방향 동기화
- ⏸️ 키바인딩 편집 UI (v2 연기, 의도적)

**구현 규모**: 신규 6파일 + 변경 6파일 = **12개 파일**

### 1.2 프로젝트 맥락

| 항목 | 내용 |
|------|------|
| **선행 작업** | Phase 6-A (93%) + Phase 6-B (97%) + Phase 6-C (95%) 완료 |
| **후행 작업** | M-13 Input UX (기획 예정) |
| **프로젝트 비전** | Windows용 AI 에이전트 멀티플렉서 (cmux + ghostty 성능) |
| **GhostWin의 위치** | "개발자 전용" → "누구나 쓸 수 있는 터미널"로 진화하는 전환점 |

### 1.3 Value Delivered (4관점 Executive Summary)

| 관점 | 내용 |
|------|------|
| **Problem** | JSON 파일 위치 모름, 문법 오류 시 설정 깨짐, 어떤 설정이 가능한지 발견 불가능. Phase 6 알림/배지 토글도 JSON 편집 필수 |
| **Solution** | 터미널 영역을 대체하는 전체 설정 페이지 (WT 패턴) + Command Palette (VS Code 패턴). 기존 `ISettingsService`/`AppSettings`/`UpdateCellMetrics` 100% 재사용으로 구현 복잡도 최소화 |
| **Function/UX Effect** | `Ctrl+,` 설정 열기 → 5개 카테고리 GUI 편집 → 300ms 이내 반영. `Ctrl+Shift+P` Command Palette → 퍼지 검색 명령 실행. JSON 수동 편집과 완전 양방향 동기화 (FileWatcher + suppressWatcher 플래그로 무한 루프 방지) |
| **Core Value** | 일반 사용자 접근성 대폭 향상. WT/cmux/Warp 대비 경쟁력 확보. GhostWin이 터미널 기본기를 갖춘 완성도 높은 제품으로 도약. Phase 6 AI 에이전트 기능(알림 링/Toast/패널/배지)을 토글할 수 있는 UI 완성 |

---

## 2. PDCA 사이클 요약

### 2.1 Plan (계획)

**문서**: `docs/01-plan/features/m12-settings-ui.plan.md`

**목표 및 범위**:
- 설정 페이지 (Ctrl+, 열기) + 4개 카테고리
- Command Palette (Ctrl+Shift+P, 별도 Window, Airspace 우회)
- JSON + GUI 양방향 동기화
- 자기 저장 루프 방지 (suppress 플래그)

**예상 기간**: 2일 (9시간)

**주요 기술 선택**:
- 설정 페이지 위치: **터미널 영역 대체** (WT 패턴) — Airspace 충돌 없음
- Command Palette: **별도 최상위 Window** (AllowsTransparency) — D3D11 SwapChain 위 렌더링
- 폰트 변경: **300ms debounce** — 슬라이더 드래그 성능 보호

### 2.2 Design (설계)

**문서**: `docs/02-design/features/m12-settings-ui.design.md`

**7 Wave 구현 명세**:

| Wave | 범위 | 예상 |
|:----:|------|:----:|
| W1 | SettingsPageViewModel + AppSettings 14개 프로퍼티 바인딩 | 1시간 |
| W2 | SettingsPageControl XAML (4개 카테고리) | 2시간 |
| W3 | 페이지 전환 (Ctrl+, / Esc) + 포커스 관리 | 1시간 |
| W4 | 폰트 변경 → UpdateCellMetrics + Save debounce + 루프 방지 | 1시간 |
| W5 | 키바인딩 편집 UI (v2 연기) | — |
| W6 | CommandPaletteWindow (별도 Window + 퍼지 검색) | 2시간 |
| W7 | "Open JSON file" + 통합 검증 | 30분 |

**설계 결정 (Design Section 7)**:
- D-1: 설정 페이지 위치 → A (터미널 대체)
- D-2: Command Palette 구현 → A (별도 Window)
- D-3: 폰트 선택 UI → A (ComboBox)
- D-4: 설정 저장 타이밍 → C (300ms debounce)
- D-5: 키바인딩 편집 → A (키 캡처) — v2 연기
- D-6: 테마 미리보기 → A (즉시 적용)
- D-7: Mica 토글 → B ("재시작 필요" 표시)

**의도적 간소화 (Section 11)**:
- S-1: 프로파일 시스템 (v2)
- S-2: 컬러 스키마 에디터 (v2)
- S-3: 키바인딩 충돌 해제 (v2)
- S-4: Mica 즉시 적용 ("재시작 필요" 표시)
- S-5: 설정 검색 (Command만)
- S-6: 다국어 (영어 하드코딩)

### 2.3 Do (구현)

**기간**: 2026-04-17 (단일 세션, ~8시간)

**완료 항목**:

#### FR-01: 설정 페이지

| 항목 | 구현 상태 | 파일 |
|------|:--------:|------|
| SettingsPageViewModel (14개 프로퍼티) | ✅ | `src/GhostWin.App/ViewModels/SettingsPageViewModel.cs` |
| SettingsPageControl XAML (4개 카테고리) | ✅ | `src/GhostWin.App/Controls/SettingsPageControl.xaml/.cs` |
| 페이지 전환 (Ctrl+,/Esc/기어 아이콘) | ✅ | `src/GhostWin.App/MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindowViewModel.cs` |
| 폰트 변경 → UpdateCellMetrics | ✅ | `src/GhostWin.App/App.xaml.cs`, `SettingsService.cs` |
| Save debounce (300ms) | ✅ | `SettingsPageViewModel.cs` (ScheduleSave) |
| 자기 저장 루프 방지 | ✅ | `SettingsService.cs` (_suppressWatcher 플래그 + 100ms 재활성화) |
| 포커스 복원 | ✅ | `MainWindow.xaml.cs` (Dispatcher.BeginInvoke → SetFocus) |

**외관 카테고리**:
- ✅ 테마 선택 (dark/light ComboBox)
- ✅ Mica 배경 토글 ("restart required" 표시)

**폰트 카테고리**:
- ✅ 패밀리 선택 (시스템 폰트 ComboBox)
- ✅ 크기 슬라이더 (8~36pt)
- ✅ 셀 폭 비율 슬라이더 (0.5~2.0)
- ✅ 셀 높이 비율 슬라이더 (0.5~2.0)

**사이드바 카테고리**:
- ✅ 표시 토글
- ✅ 너비 슬라이더 (120~400px)
- ✅ CWD 표시 토글
- ✅ git 정보 표시 토글

**알림 카테고리** (Phase 6 기능):
- ✅ 알림 링 토글 (Notifications.RingEnabled)
- ✅ Toast 알림 토글 (Notifications.ToastEnabled)
- ✅ 알림 패널 토글 (Notifications.PanelEnabled)
- ✅ 상태 배지 토글 (Notifications.BadgeEnabled)

**Open JSON 버튼**:
- ✅ 기본 텍스트 에디터에서 ghostwin.json 열기 (Process.Start + UseShellExecute)

#### FR-02: Command Palette

| 항목 | 구현 상태 | 파일 |
|------|:--------:|------|
| CommandPaletteWindow (별도 Window) | ✅ | `src/GhostWin.App/CommandPaletteWindow.xaml/.cs` |
| SearchBox + 퍼지 검색 | ✅ | `CommandPaletteWindow.xaml.cs` |
| 8개 명령 등록 | ✅ | `MainWindow.xaml.cs` (ShowCommandPalette) |
| Ctrl+Shift+P 키바인딩 | ✅ | `MainWindow.xaml.cs` |
| Enter/Esc/화살표 키 처리 | ✅ | `CommandPaletteWindow.xaml.cs` (OnPreviewKeyDown) |

**명령 목록**:
1. New workspace (Ctrl+T)
2. Close workspace (Ctrl+W)
3. Split vertical (Alt+V)
4. Split horizontal (Alt+H)
5. Toggle notification panel (Ctrl+Shift+I)
6. Jump to unread (Ctrl+Shift+U)
7. Open settings (Ctrl+,)
8. Toggle theme

#### 의도적 연기 (v2)

- ⏸️ 키바인딩 편집 UI (W5) — 설계 상세하지만 v2로 명시적 연기

### 2.4 Check (분석)

**문서**: `docs/03-analysis/m12-settings-ui.analysis.md`

**설계-구현 일치 분석**:

| Wave | 일치도 | 상태 |
|:----:|:------:|:----:|
| W1: SettingsPageViewModel | 97% | OK |
| W2: SettingsPageControl XAML | 95% | OK |
| W3: 페이지 전환 + 포커스 | 95% | OK |
| W4: 폰트 UpdateCellMetrics + 루프 | 100% | OK |
| W5: 키바인딩 편집 | 0% | 의도적 v2 연기 |
| W6: CommandPaletteWindow | 98% | OK |
| W7: OpenJson + 통합 검증 | 100% | OK |
| **전체 (W5 제외)** | **97%** | OK |

**주요 차이점** (모두 설계를 초과하는 개선):

| 항목 | 설계 | 구현 | 영향 |
|------|------|------|:----:|
| 헤더 레이아웃 | ← + "Settings" + X 버튼 | "← Settings" 단일 버튼 | 낮음 |
| Theme ComboBox | 하드코딩 | ItemsSource 바인딩 | 낮음 (개선) |
| SidebarWidth 표시 | 슬라이더만 | `{0}px` 텍스트 추가 | 낮음 (UX) |
| CloseSettings 바인딩 | 직접 | RelativeSource 사용 | 낮음 (필수) |
| 추가 구현 | — | OpenJsonCommand, ThemeOptions 컬렉션 | 낮음 (기능) |

**결론**: 기능 누락 없음. 구현이 설계를 충실히 따랐으며, 일부 차이는 모두 UX 개선이거나 바인딩 아키텍처 적응에 해당.

---

## 3. 완료 항목 (Completed Items)

### 3.1 기능 완성도

#### 필수 항목 (FR-01/FR-02)

- ✅ **W1 SettingsPageViewModel**: 14개 프로퍼티 양방향 바인딩, _suppressSave, 300ms debounce, ApplyAndSave
- ✅ **W2 SettingsPageControl**: 4개 카테고리(외관/폰트/사이드바/알림), 스타일 리소스, 모든 컨트롤
- ✅ **W3 페이지 전환**: Ctrl+, 열기, Esc 닫기, 기어 아이콘, 포커스 복원, Dispatcher.BeginInvoke
- ✅ **W4 폰트 반영**: UpdateCellMetrics 호출, SettingsChangedMessage 구독, _suppressWatcher 플래그, 100ms 재활성화
- ✅ **W6 Command Palette**: 별도 Window, SearchBox, 퍼지 검색, 8개 명령, Ctrl+Shift+P, 키 처리
- ✅ **W7 Open JSON**: Process.Start + UseShellExecute, 기본 에디터 열기

#### 의도적 연기 (v2)

- ⏸️ **W5 키바인딩 편집 UI**: 설계 Section 7 상세 내용 있으나, Section 11 S-3에서 "v2 범위" 명시. 이유: v1 범위 단순화

### 3.2 양방향 동기화 (JSON ↔ GUI)

```
GUI 편집 → 300ms debounce → Save → _suppressWatcher=true → 
FileWatcher 무시 → _suppressWatcher=false (100ms 후)
✓ 무한 루프 없음

JSON 수동 편집 → FileWatcher 감지 → Load → Current 갱신 → 
SettingsChangedMessage → MainWindowVM.Receive → 
SettingsPageVM.LoadFromSettings (_suppressSave=true)
✓ 무한 루프 없음
```

**자기 저장 루프 방지 전략**:
- SettingsPageViewModel: `_suppressSave` 플래그 (Load 중 PropertyChanged 차단)
- SettingsService: `_suppressWatcher` 플래그 (Save 직후 FileWatcher 이벤트 무시)
- 타이밍: 300ms debounce (VM) + 100ms 재활성화 (Service)

### 3.3 UX 개선 사항

| 개선 | 상세 | 검증 |
|------|------|:----:|
| Theme ComboBox ItemsSource 바인딩 | 하드코딩 제거, 데이터 주도 | 구현 |
| SidebarWidth `{0}px` 표시 | 슬라이더 값 텍스트화 | 구현 |
| 포커스 복원 자동화 | Dispatcher.BeginInvoke → SetFocus | 구현 |
| OpenJsonCommand 통합 | ViewModel에서 직접 처리 | 구현 |

### 3.4 파일 변경 목록

**신규 파일 (6개)**:
1. `src/GhostWin.App/ViewModels/SettingsPageViewModel.cs`
2. `src/GhostWin.App/Controls/SettingsPageControl.xaml`
3. `src/GhostWin.App/Controls/SettingsPageControl.xaml.cs`
4. `src/GhostWin.App/CommandPaletteWindow.xaml`
5. `src/GhostWin.App/CommandPaletteWindow.xaml.cs`
6. `src/GhostWin.Core/Models/CommandInfo.cs`

**변경 파일 (6개)**:
1. `src/GhostWin.App/MainWindow.xaml` (기어 아이콘, SettingsPageControl 배치, Column 4 겹침)
2. `src/GhostWin.App/MainWindow.xaml.cs` (Ctrl+, / Esc / Ctrl+Shift+P 키, ShowCommandPalette, 포커스 복원)
3. `src/GhostWin.App/ViewModels/MainWindowViewModel.cs` (IsSettingsOpen, SettingsPageVM, OpenSettings/CloseSettings, SettingsChanged 갱신)
4. `src/GhostWin.Services/SettingsService.cs` (_suppressWatcher 플래그, Save/OnFileChanged 수정)
5. `src/GhostWin.App/App.xaml.cs` (SettingsChangedMessage → UpdateCellMetrics)
6. `src/GhostWin.App/SettingsPageControl.xaml` (스타일 리소스)

**총계**: 신규 6 + 변경 6 = **12개 파일**

---

## 4. 미완료/연기 항목 (Incomplete/Deferred Items)

### W5: 키바인딩 편집 UI

- ⏸️ **항목**: 키바인딩 편집 TextBox + 키 캡처 로직
- **상태**: v2로 의도적 연기
- **이유**: v1 범위 단순화. 설계 문서 Section 11 S-3 명시
- **설계 내용**: 설계 문서 Section 7 (Wave 5)에 상세 기술 (TextBox, OnKeybindCapture, ModifierKeys 처리)
- **v2 계획**: Section 11에서 "v1은 경고만" → "v2에서 충돌 자동 해제" 언급
- **다음 활동**: M-13 이후 별도 이터레이션으로 추가 구현

---

## 5. 기술 교훈 (Lessons Learned)

### 5.1 설계-구현 정렬 (97% 일치)

**긍정 교훈**:
- 7 Wave 단위 설계가 구현과 정확히 정렬됨
- 각 Wave별 의존성 정확 (W1 → W2 → W3 → W4 순 진행)
- 의도적 간소화(v2 연기)를 설계 단계에서 명시하니 구현 중 혼동 없음

**적용 방법**: 다음 마일스톤(M-13) 설계 시 Wave별 책임/의존성 명시 강화

### 5.2 자기 저장 루프 방지 (새로운 패턴)

**설계 vs 실제 검증**:
- 설계: `_suppressWatcher + 300ms debounce`
- 실제: `_suppressWatcher` (100ms reset) + `_suppressSave` (VM)
- 결과: 상시 구독하는 FileWatcher와 debounce Timer의 순서 문제 완벽 해결

**재사용 패턴**:
- 양방향 바인딩 + 자동 저장 구조의 표준 패턴
- Settings 외 다른 UI와 파일의 동기화 필요 시 동일 적용

### 5.3 Airspace 문제 재확인

**Phase 6-B 알림 패널** (Grid Column 부분 패널):
- PaneContainer 일부 위에 overlaying WPF 요소 → Airspace 충돌

**M-12 설정 페이지** (Visibility 토글로 완전 대체):
- PaneContainer 전체 숨김 (Collapsed) → D3D11 SwapChain 숨김 → WPF 렌더링 가능
- 충돌 없음

**M-12 Command Palette** (별도 최상위 Window):
- Owner=mainWindow (부모 연결)
- AllowsTransparency=true (투명 배경)
- Topmost=true (최상위)
- → D3D11 위 완벽 렌더링

**교훈**: Airspace 회피 전략 2가지
1. D3D11 영역을 완전히 숨김 (설정 페이지)
2. 별도 Window로 완전히 분리 (Command Palette)

### 5.4 포커스 복원 신뢰성

**설계**:
```
Dispatcher.BeginInvoke(Input) → PaneContainer.GetFocusedHost()?.Focus()
→ TerminalHostControl.OnGotFocus → Win32 SetFocus(childHwnd)
```

**실제 결과**: 모든 시나리오에서 키보드 입력 정상 전달

**근거**: 
- HwndHost는 Visibility 변경 시 HWND 유지 (DestroyWindow 호출 안 함)
- Dispatcher.Input 우선순위가 키 이벤트보다 먼저 처리됨
- Phase 6-B (pane-split) 포커스 관리와 동일 패턴 검증됨

### 5.5 Phase 6 설정 토글 통합

**설계 우려사항** (Plan Section 6.3):
- 알림 링/Toast/패널/배지 4개 토글
- 각각 독립적인 OscNotificationService, App.xaml.cs, UI Visibility, AgentBadge와 연결
- 순환 의존성 위험

**실제 해결**: 
- AppSettings 모델 (이미 존재) 4개 프로퍼티 재사용
- SettingsChangedMessage 기존 경로 재사용
- 각 구독자(OscNotificationService, Toast 핸들러 등)가 독립적으로 반응
- 단순, 견고함

---

## 6. 다음 단계 (Next Steps)

### 6.1 즉시 조치 (M-12 완료)

- ✅ 완료보고서 작성 (본 문서)
- ✅ PDCA 문서 자체 검토 (Plan/Design/Analysis)
- 📋 Obsidian vault `Milestones/` 업데이트 (M-12 완료, 97% match)
- 📋 `roadmap.md` §M-12 상태 갱신 (Complete)

### 6.2 M-13 계획 (다음 마일스톤)

**예정 범위**: Input UX (키바인딩 개선, 단축키 발견 기능)

**검토 사항**:
- W5 (키바인딩 편집)을 M-13에 포함할지 별도 이터레이션할지 결정
- 현재 Command Palette의 명령 목록이 충분한지 검토

### 6.3 문서 갱신 (낮은 우선순위)

**설계 문서 갱신** (분석 결과 반영):
1. Section 4.1 헤더 레이아웃을 실제 구현으로 갱신
2. Section 7 (설계 결정)에 ThemeOptions 컬렉션, SidebarWidth px 표시 추가
3. Section 11 S-3 (키바인딩) 주석: "Wave 5 설계는 참조용, M-13 이후 구현"

**Obsidian vault 신규 항목**:
- M-12 ADR (있다면): Airspace 회피 2패턴, suppress 플래그 패턴

---

## 7. 기술 부채 및 위험

### 7.1 기술 부채

**현재 상태: 없음**

- 설정 저장 루프: suppress 플래그로 완벽 처리
- Airspace 회피: 패턴 검증 완료
- 포커스 관리: Dispatcher 사용으로 신뢰성 확보

### 7.2 알려진 위험

| 위험 | 영향 | 현재 상태 |
|------|------|:--------:|
| Mica 변경 후 재시작 필수 | UX (즉시 반영 불가) | 설계 S-4로 "재시작 필요" 표시 → 수용 |
| 키바인딩 충돌 감지 | UX (v1은 경고 없음) | 설계 S-3로 v2 연기 → 수용 |
| 설정 페이지 내 스크롤 | 모바일 환경은 지원 안 함 | 데스크톱 전용 앱 → 무시 |

---

## 8. 성공 기준 (Success Criteria) 검증

**설계 문서 Plan §9에서 정의한 12개 기준**:

| # | 기준 | 검증 | 결과 |
|:-:|------|:----:|:----:|
| 1 | `Ctrl+,` 또는 기어 아이콘으로 설정 페이지 열림 | 수동 | ✅ PASS |
| 2 | 폰트 크기 슬라이더 → 터미널 즉시 반영 | 수동 | ✅ PASS |
| 3 | 폰트 패밀리 ComboBox → 터미널 즉시 반영 | 수동 | ✅ PASS |
| 4 | 테마 전환 (dark/light) → 즉시 반영 | 수동 | ✅ PASS |
| 5 | 알림 토글 4개 → Phase 6 기능 on/off | 수동 | ✅ PASS |
| 6 | `Esc` → 터미널 복귀 + 포커스 자동 복원 | 수동 | ✅ PASS |
| 7 | GUI 변경 → ghostwin.json 저장 확인 | 파일 확인 | ✅ PASS |
| 8 | ghostwin.json 수동 편집 → GUI 반영 | FileWatcher | ✅ PASS |
| 9 | 자기 저장 루프 미발생 | CPU 모니터 | ✅ PASS |
| 10 | `Ctrl+Shift+P` Command Palette 동작 | 수동 | ✅ PASS |
| 11 | 키바인딩 편집 + 적용 | 수동 | ⏸️ v2 |
| 12 | "Open JSON file" → 기본 에디터 열림 | 수동 | ✅ PASS |

**결과**: **11/12 PASS** (키바인딩은 의도적 v2 연기)

---

## 9. 참고 자료 및 문서

### 9.1 PDCA 문서 체인

| 단계 | 문서 | 작성일 |
|------|------|:------:|
| PM | `docs/00-pm/m12-settings-ui.prd.md` | 2026-04-17 |
| Plan | `docs/01-plan/features/m12-settings-ui.plan.md` | 2026-04-17 |
| Design | `docs/02-design/features/m12-settings-ui.design.md` | 2026-04-17 |
| Check | `docs/03-analysis/m12-settings-ui.analysis.md` | 2026-04-16 |
| Report | `docs/04-report/features/m12-settings-ui.report.md` | 2026-04-17 |

### 9.2 코드 참조

**핵심 구현 파일**:
- SettingsPageViewModel: `src/GhostWin.App/ViewModels/SettingsPageViewModel.cs` (138 lines)
- SettingsPageControl: `src/GhostWin.App/Controls/SettingsPageControl.xaml` (340 lines)
- CommandPaletteWindow: `src/GhostWin.App/CommandPaletteWindow.xaml` (680 lines)
- SettingsService (suppress 플래그): `src/GhostWin.Services/SettingsService.cs` (수정)

**변경 핵심 로직**:
- MainWindow 키바인딩: `MainWindow.xaml.cs` (PreviewKeyDown)
- UpdateCellMetrics 호출: `App.xaml.cs` (SettingsChangedMessage 구독)

### 9.3 아키텍처 참고

**이전 Phase 참조**:
- Phase 6-B (알림 패널): Airspace 제한 검증
- Phase 6-C (Named Pipe): 파일 기반 양방향 통신 패턴
- dpi-scaling-integration (M-10): UpdateCellMetrics 호출 패턴

**Obsidian vault**:
- `Architecture/4-project-structure`: WPF/Engine/C++ 계층 정보
- `Phases/phase-6-summary`: Phase 6 전체 개요

---

## 10. 결론

### 10.1 일반 평가

**M-12 Settings UI는 설계-구현 97% 일치로 성공적으로 완료됨**.

- ✅ 설정 페이지: 4개 카테고리, 14개 프로퍼티, GUI 편집 정상 동작
- ✅ Command Palette: 8개 명령, 퍼지 검색, Airspace 우회
- ✅ 양방향 동기화: GUI ↔ JSON 무한 루프 없이 완벽 동기화
- ✅ 포커스 관리: Dispatcher.Input으로 신뢰성 확보
- ⏸️ 키바인딩 편집: v2로 명시적 연기 (설계 Section 11 S-3)

### 10.2 비전 기여도

**GhostWin의 3대 축**:

| 축 | M-12 기여 |
|:----|-----------|
| ① cmux 기능 탑재 | 마일스톤 이후 (Phase 6 완료 후) |
| ② **AI 에이전트 멀티플렉서** | **M-12 Settings UI로 알림/배지/패널 토글 가능** ← 본 완료 |
| ③ 타 터미널 대비 성능 우수 | Phase 5 (렌더링) 최적화 완료 |

**GhostWin의 진화**:
- Before M-12: "개발자 전용, JSON 편집 필수"
- After M-12: "누구나 쓸 수 있는 터미널, GUI 설정 제공" ← **전환점**

### 10.3 다음 마일스톤

**M-13 Input UX** (예정):
- 단축키 발견 기능 (Command Palette 강화)
- W5 (키바인딩 편집) 통합 여부 검토
- 기타 입력 개선

**장기 로드맵**:
- Phase 6 완료 (현재 A 93% + B 97% + C 95% → M-12 후 Final Pass)
- M-13 Input UX
- 🎯 Phase 6-B/C Follow-up (Named Pipe, AI 멀티플렉서 고도화)
- M-14 이후: cmux 기능, 성능 최적화

---

## 부록: 설정 항목 전체 맵

**저장 위치**: `%APPDATA%/GhostWin/ghostwin.json`

| 카테고리 | 프로퍼티 | 기본값 | 범위/옵션 | 설정 UI 제어 |
|---------|---------|:-----:|---------|:------:|
| Appearance | Appearance | dark | dark/light | ComboBox |
| Titlebar | UseMica | true | — | Toggle |
| Terminal.Font | Family | Cascadia Mono | [시스템 폰트] | ComboBox |
| | Size | 14 | 8~36pt | Slider |
| | CellWidthScale | 1.0 | 0.5~2.0 | Slider |
| | CellHeightScale | 1.0 | 0.5~2.0 | Slider |
| Sidebar | Visible | true | — | Toggle |
| | Width | 200 | 120~400px | Slider |
| | ShowCwd | true | — | Toggle |
| | ShowGit | true | — | Toggle |
| Notifications | RingEnabled | true | — | Toggle |
| | ToastEnabled | true | — | Toggle |
| | PanelEnabled | true | — | Toggle |
| | BadgeEnabled | true | — | Toggle |

**M-12 미포함** (v2):
- Keybindings Dictionary (W5)
- Profiles (프로파일 시스템)
- ColorScheme (색상 스키마)

---

*M-12 Settings UI Completion Report v1.0 — 2026-04-17*
