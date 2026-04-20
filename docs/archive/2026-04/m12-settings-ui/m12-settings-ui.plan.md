# Plan — M-12: 사용자 설정 UI (Settings UI)

> **문서 종류**: Plan
> **작성일**: 2026-04-17
> **PRD 참조**: `docs/00-pm/m12-settings-ui.prd.md`
> **선행 완료**: Phase 6 (A 93% + B 97% + C 95%), M-11 세션 복원 (96%)
> **비전 축**: 터미널 기본기 완성

---

## Executive Summary

| 관점 | 내용 |
|------|------|
| **Problem** | 모든 설정을 `%APPDATA%/GhostWin/ghostwin.json` 수동 편집으로만 변경 가능. 파일 위치 모름, JSON 문법 오류 시 설정 깨짐, 어떤 설정이 가능한지 발견 불가능. Phase 6 알림/배지 토글도 JSON 편집 필요 |
| **Solution** | 터미널 영역을 대체하는 전체 설정 페이지 (WT 패턴) + Command Palette (VS Code 패턴). 기존 `ISettingsService`/`AppSettings`/`SettingsChangedMessage`/`UpdateCellMetrics` 100% 재사용. 설정 변경 → 즉시 Save → FileWatcher → 양방향 동기화 |
| **Function / UX Effect** | `Ctrl+,` 설정 열기 → 5개 카테고리(외관/폰트/사이드바/알림/키바인딩) GUI 편집 → 300ms 이내 반영. `Ctrl+Shift+P` Command Palette → 퍼지 검색 명령 실행. `Esc` 또는 X로 터미널 복귀 시 포커스 자동 복원 |
| **Core Value** | JSON 수동 편집 탈피 → 일반 사용자 접근성 대폭 향상. WT/cmux/Warp 대비 경쟁력 확보. GhostWin이 "개발자 전용"에서 "누구나 쓸 수 있는 터미널"로 진화하는 전환점 |

---

## 1. 경쟁 제품 심층 분석

### 1.1 Windows Terminal 설정 UI 분석

WT는 2026년 기준 설정 UI를 **별도 전용 창**으로 분리하는 리디자인을 진행 중:

| 항목 | WT 현행 | WT 리디자인 (2026) |
|------|---------|-------------------|
| **위치** | 인앱 전체 페이지 | 별도 전용 창 |
| **카테고리** | Startup, Interaction, Appearance, Rendering, Profiles | Startup, Interaction, Personalization, Compatibility |
| **저장** | JSON 편집 + GUI 공존 | 자동 저장 (auto-save) |
| **검색** | v1.25에서 설정 검색 추가 | 강화된 통합 검색 |
| **프로파일** | 드래그앤드롭 프로파일 관리 | 동일 + 시각적 피드백 |
| **키바인딩** | v1.25에서 리치 에디터 추가 | 동일 |

### 1.2 경쟁 제품 비교 매트릭스

| 기능 | WT | cmux | Warp | VS Code | **GhostWin (M-12 목표)** |
|------|:--:|:----:|:----:|:-------:|:------------------------:|
| GUI 설정 페이지 | ✅ | ✅ | ✅ | ✅ | ✅ |
| JSON 편집 공존 | ✅ | ✅ | ❌ | ✅ | ✅ |
| 설정 검색 | ✅ (v1.25) | ❌ | ✅ | ✅ | ✅ (Command Palette) |
| 즉시 반영 (live preview) | ✅ | ✅ | ✅ | ✅ | ✅ |
| Command Palette | ✅ | ❌ | ✅ | ✅ | ✅ |
| 프로파일 시스템 | ✅ | ❌ | ✅ | N/A | ❌ (v2) |
| AI 에이전트 설정 | ❌ | ✅ | ⚠️ | ❌ | ✅ (알림/배지/Named Pipe) |
| 양방향 동기화 | ✅ | ✅ | ❌ | ✅ | ✅ |

### 1.3 GhostWin 차별점

| # | 차별점 | 상세 |
|:-:|--------|------|
| 1 | **AI 에이전트 설정 통합** | 알림 링/Toast/패널/배지/Named Pipe 토글이 설정 UI에 통합. WT/Warp에는 없는 Phase 6 고유 기능 |
| 2 | **JSON + GUI 완전 양방향** | GUI 편집 → JSON 저장 + JSON 수동 편집 → GUI 반영 (FileWatcher). WT와 동일 수준 |
| 3 | **Command Palette + 설정 검색** | `Ctrl+Shift+P`로 명령 + 설정 항목 통합 검색. WT v1.25 수준 |
| 4 | **경량 (별도 프레임워크 없음)** | WPF 기본 컨트롤만 사용. Electron 기반 경쟁자 대비 메모리/시작 시간 우위 |

---

## 2. 사용자 경험 시나리오 (UX 누락 방지)

### 시나리오 A: 처음 사용하는 개발자

> GhostWin을 설치하고 처음 실행.
> 1. 사이드바 하단의 ⚙ 기어 아이콘 발견 → 클릭
> 2. 설정 페이지 열림 (터미널 영역 대체)
> 3. "외관" 카테고리에서 폰트 크기 14→16으로 슬라이더 조작
> 4. **즉시** 터미널 미리보기 없이 설정 저장 (돌아가면 반영됨)
> 5. `Esc` → 터미널 복귀, 폰트 16pt로 변경되어 있음

### 시나리오 B: Phase 6 알림 설정

> Claude Code 5개 세션 운영 중. Toast 알림이 너무 많음.
> 1. `Ctrl+,` → 설정 페이지
> 2. "알림" 카테고리 → Toast 알림 토글 OFF
> 3. 알림 링은 유지, Toast만 비활성화
> 4. `Esc` → 작업 복귀

### 시나리오 C: 키바인딩 변경

> `Ctrl+Shift+I` 알림 패널 대신 `Ctrl+I`로 변경하고 싶음.
> 1. `Ctrl+,` → "키바인딩" 카테고리
> 2. "알림 패널 토글" 항목 → 키 조합 편집
> 3. 기존 `Ctrl+Shift+I` → `Ctrl+I`로 변경
> 4. 저장 즉시 반영

### 시나리오 D: Command Palette로 빠른 명령

> 명령 이름은 알지만 단축키를 모름.
> 1. `Ctrl+Shift+P` → Command Palette 열림
> 2. "split" 입력 → "수직 분할", "수평 분할" 필터링
> 3. 항목 클릭 → 명령 실행
> 4. 팔레트 자동 닫힘

### 시나리오 E: JSON 고급 편집

> 파워 사용자가 JSON을 직접 편집.
> 1. 설정 페이지 하단 "JSON으로 편집" 링크 클릭
> 2. 기본 텍스트 에디터에서 ghostwin.json 열림
> 3. 수동 편집 + 저장
> 4. FileWatcher 감지 → GhostWin 설정 자동 반영 (50ms)

### 시나리오 F: 설정 오류 복구

> JSON 수동 편집 중 문법 오류 발생.
> 1. 파싱 실패 → **이전 설정 유지** (기존 SettingsService.Load() 동작)
> 2. 설정 페이지는 마지막 유효 설정 표시
> 3. 사용자가 JSON 오류 수정 → 자동 반영

---

## 3. 기능 요구사항 상세

### FR-01: 설정 페이지 (Settings Page)

#### 3.1.1 페이지 구조

```
┌──────────────────────────────────────────────────────────┐
│ GhostWin                    ─  □  ✕                     │
├──────────┬───────────────────────────────────────────────┤
│ 사이드바  │                                               │
│          │   ← Settings ─────────────────────── [✕]      │
│ ⚙ 설정  │                                               │
│          │   외관                                         │
│          │   ┌─────────────────────────────────────┐     │
│          │   │ 테마      [Dark         ▼]          │     │
│          │   │ Mica 배경  [■ On]                    │     │
│          │   └─────────────────────────────────────┘     │
│          │                                               │
│          │   폰트                                         │
│          │   ┌─────────────────────────────────────┐     │
│          │   │ 패밀리    [Cascadia Mono  ▼]         │     │
│          │   │ 크기      [──●───────] 14pt          │     │
│          │   │ 셀 폭 비율 [──────●───] 1.0          │     │
│          │   │ 셀 높이 비율[──────●───] 1.0          │     │
│          │   └─────────────────────────────────────┘     │
│          │                                               │
│          │   알림 (Phase 6)                               │
│          │   ┌─────────────────────────────────────┐     │
│          │   │ 알림 링     [■ On]                    │     │
│          │   │ Toast 알림  [□ Off]                   │     │
│          │   │ 알림 패널   [■ On]                    │     │
│          │   │ 상태 배지   [■ On]                    │     │
│          │   └─────────────────────────────────────┘     │
│          │                                               │
│          │   ─── JSON으로 편집 ───                        │
└──────────┴───────────────────────────────────────────────┘
```

#### 3.1.2 UI 위치 결정

| 선택지 | 장점 | 단점 | 판정 |
|--------|------|------|:----:|
| A: 터미널 영역 대체 (WT 패턴) | Airspace 충돌 없음, 전체 화면 활용 | 터미널과 설정 동시 확인 불가 | **채택** |
| B: 사이드 패널 (Phase 6-B 패턴) | 터미널 동시 확인 가능 | 좁은 공간, 설정 항목 많으면 스크롤 과다 | 기각 |
| C: 별도 Window (WT 리디자인) | 가장 유연 | Window 관리 복잡, 설정 변경 미리보기 어려움 | v2 |

**근거**: WT 현행 방식이 가장 검증됨. Phase 6-B의 알림 패널은 Grid Column이었지만, 설정 페이지는 항목이 많아서 전체 화면이 적합. Airspace 문제도 없음 (D3D11 SwapChain을 숨기고 WPF 페이지로 대체).

#### 3.1.3 카테고리 + 설정 항목 전체 목록

| 카테고리 | 설정 항목 | 컨트롤 | 바인딩 | 반영 경로 |
|---------|---------|--------|--------|----------|
| **외관** | 테마 | ComboBox (dark/light) | `Appearance` | `ApplicationThemeManager.Apply` |
| | Mica 배경 | ToggleSwitch | `Titlebar.UseMica` | 런타임 불가 (재시작 필요) |
| **폰트** | 패밀리 | ComboBox (시스템 폰트) | `Terminal.Font.Family` | `UpdateCellMetrics` |
| | 크기 | Slider (8~36pt) | `Terminal.Font.Size` | `UpdateCellMetrics` |
| | 셀 폭 비율 | Slider (0.5~2.0) | `Terminal.Font.CellWidthScale` | `UpdateCellMetrics` |
| | 셀 높이 비율 | Slider (0.5~2.0) | `Terminal.Font.CellHeightScale` | `UpdateCellMetrics` |
| **사이드바** | 표시 | ToggleSwitch | `Sidebar.Visible` | `MainWindowVM.SidebarVisible` |
| | 너비 | Slider (120~400) | `Sidebar.Width` | `MainWindowVM.SidebarWidth` |
| | CWD 표시 | ToggleSwitch | `Sidebar.ShowCwd` | `MainWindowVM.ShowCwd` |
| | git 정보 표시 | ToggleSwitch | `Sidebar.ShowGit` | sidebarXAML Visibility |
| **알림** | 알림 링 | ToggleSwitch | `Notifications.RingEnabled` | `OscNotificationService` |
| | Toast 알림 | ToggleSwitch | `Notifications.ToastEnabled` | `App.xaml.cs Toast` |
| | 알림 패널 | ToggleSwitch | `Notifications.PanelEnabled` | NotificationPanel Visibility |
| | 상태 배지 | ToggleSwitch | `Notifications.BadgeEnabled` | AgentBadge Visibility |
| **키바인딩** | 액션별 키 조합 | TextBox + 키 캡처 | `Keybindings[actionId]` | 런타임 키 핸들러 |

#### 3.1.4 설정 저장 전략

```
사용자가 Slider/ComboBox/Toggle 변경
  ↓
SettingsPageViewModel.PropertyChanged
  ↓ (300ms debounce)
ISettingsService.Save() → ghostwin.json 업데이트
  ↓
FileSystemWatcher 감지 (50ms debounce)
  ↓
SettingsService.Load() → Current 갱신
  ↓
OnSettingsReloaded 콜백 → Dispatcher.BeginInvoke
  ↓
SettingsChangedMessage 전파
  ↓
MainWindowViewModel.ApplySettings()
+ UpdateCellMetrics (폰트 변경 시)
```

**자기 저장 루프 방지**: FileWatcher가 자신의 Save를 감지하여 무한 루프가 되는 문제.
- 해결: Save 직전에 `_suppressWatcher = true` 플래그 설정, Save 완료 후 100ms 뒤 리셋.
- 근거: WT도 동일한 패턴 사용 (suppress flag).

#### 3.1.5 "JSON으로 편집" 링크

```csharp
Process.Start(new ProcessStartInfo(settingsService.SettingsFilePath)
{
    UseShellExecute = true  // 기본 텍스트 에디터로 열기
});
```

### FR-02: Command Palette

#### 3.2.1 Airspace 문제 해결

**문제**: WPF Popup/Flyout은 D3D11 SwapChain 위에 렌더링되지 않음 (Airspace 제한).

**해결**: **별도 최상위 Window** (투명 배경, 오버레이 스타일).

```csharp
var palette = new CommandPaletteWindow
{
    Owner = mainWindow,        // 부모 연결
    WindowStyle = WindowStyle.None,
    AllowsTransparency = true,
    Background = Brushes.Transparent,
    ShowInTaskbar = false,
    Topmost = true,
};
palette.ShowDialog();  // 모달
```

- `Owner = mainWindow` → GhostWin 창 이동 시 따라감
- `AllowsTransparency = true` → 반투명 배경
- 모달 → 뒤 터미널과 입력 충돌 없음

#### 3.2.2 명령 목록

| 명령 | 액션 | 기본 키바인딩 |
|------|------|:------------:|
| 새 워크스페이스 | `CreateWorkspace` | `Ctrl+T` |
| 워크스페이스 닫기 | `CloseWorkspace` | `Ctrl+W` |
| 수직 분할 | `SplitVertical` | `Alt+V` |
| 수평 분할 | `SplitHorizontal` | `Alt+H` |
| 알림 패널 토글 | `ToggleNotificationPanel` | `Ctrl+Shift+I` |
| 미읽음 점프 | `JumpToUnread` | `Ctrl+Shift+U` |
| 설정 열기 | `OpenSettings` | `Ctrl+,` |
| JSON 편집 | `OpenSettingsJson` | — |
| 테마 전환 | `ToggleTheme` | — |

#### 3.2.3 검색 알고리즘

퍼지 매칭: substring 기반 (v1). 입력 텍스트가 명령 이름의 부분 문자열이면 매칭.

```csharp
// "split" → "수직 분할", "수평 분할"
// "theme" → "테마 전환"
// "notify" → "알림 패널 토글"
var filtered = commands.Where(c =>
    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
    c.ActionId.Contains(query, StringComparison.OrdinalIgnoreCase));
```

---

## 4. 구현 순서 (7 Waves)

| Wave | 범위 | 의존 | 검증 | 예상 |
|:----:|------|:---:|------|:----:|
| **W1** | SettingsPageViewModel + AppSettings 바인딩 | — | 빌드 성공 | 1시간 |
| **W2** | 설정 페이지 XAML (외관+폰트+사이드바+알림 4개 카테고리) | W1 | 수동: 페이지 렌더링 확인 | 2시간 |
| **W3** | 설정 ↔ 터미널 페이지 전환 (`Ctrl+,`, `Esc`, 기어 아이콘) | W2 | 수동: 전환 + 포커스 복원 | 1시간 |
| **W4** | 폰트 변경 → UpdateCellMetrics 연동 + Save debounce | W3 | 수동: 슬라이더 → 폰트 크기 변경 | 1시간 |
| **W5** | 키바인딩 편집 UI + 키 캡처 | W3 | 수동: 키 변경 → 동작 확인 | 1.5시간 |
| **W6** | Command Palette (별도 Window + 퍼지 검색) | — | 수동: Ctrl+Shift+P → 검색 → 실행 | 2시간 |
| **W7** | "JSON으로 편집" + 양방향 동기화 검증 + 자기 루프 방지 | W4 | JSON 편집 → GUI 반영, GUI → JSON 반영 | 30분 |

**총 예상**: ~9시간 (2일)

---

## 5. 변경 파일 예상

### 5.1 신규 파일 (5~7개)

| 파일 | 프로젝트 | 내용 |
|------|---------|------|
| `SettingsPageViewModel.cs` | GhostWin.App/ViewModels | 설정 페이지 ViewModel |
| `SettingsPageControl.xaml/.cs` | GhostWin.App/Controls | 설정 페이지 UserControl |
| `CommandPaletteWindow.xaml/.cs` | GhostWin.App | Command Palette 별도 Window |
| `CommandInfo.cs` | GhostWin.Core/Models | 명령 정보 모델 |

### 5.2 변경 파일 (5~7개)

| 파일 | 변경 내용 |
|------|----------|
| `MainWindow.xaml` | 설정 페이지 전환 (Visibility 토글, 기어 아이콘) |
| `MainWindow.xaml.cs` | Ctrl+, / Ctrl+Shift+P / Esc 키 바인딩 |
| `MainWindowViewModel.cs` | OpenSettings/CloseSettings 커맨드, IsSettingsOpen |
| `SettingsService.cs` | Save 시 suppress watcher 플래그 |
| `AppSettings.cs` | ShowGit → Sidebar 표시 연동 (이미 존재, 확인만) |

---

## 6. 리스크 전수 분석

### 6.1 Airspace 충돌 (심각도: 중)

| 시나리오 | 위험 | 대응 |
|---------|------|------|
| 설정 페이지가 D3D11 SwapChain 위에 표시 | WPF 요소가 HwndHost 아래에 묻힘 | **터미널 영역 대체 (SwapChain 숨김)** |
| Command Palette가 터미널 위에 오버레이 | Popup Airspace 충돌 | **별도 최상위 Window** (AllowsTransparency) |
| 설정 페이지에서 터미널 복귀 시 SwapChain 재활성화 | 깜빡임 또는 검은 화면 | PaneContainerControl.Visibility 토글 검증 필요 |

### 6.2 포커스 관리 (심각도: 중)

| 시나리오 | 위험 | 대응 |
|---------|------|------|
| 설정 페이지 열기 → 터미널 HwndHost 포커스 해제 | Space/Enter 키가 설정 UI로 갈 수 있음 | 설정 열기 시 PaneContainer 숨김 + 포커스 이동 |
| 설정 닫기 → 터미널 HwndHost 포커스 복원 | 포커스가 복원되지 않으면 키 입력 불가 | **명시적 포커스 복원**: `PaneContainer.Focus()` + 활성 TerminalHostControl에 `SetFocus` |
| Command Palette 모달 Window → 메인 윈도우 포커스 | 모달이므로 자동 처리됨 | `ShowDialog()` 사용 |

### 6.3 자기 저장 루프 (심각도: 중)

| 시나리오 | 위험 | 대응 |
|---------|------|------|
| GUI Save → FileWatcher 감지 → Load → SettingsChanged → GUI 갱신 → PropertyChanged → Save → 무한 | CPU 100% + 무한 저장 | **suppress 플래그** + 300ms debounce |

### 6.4 폰트 변경 성능 (심각도: 낮)

| 시나리오 | 위험 | 대응 |
|---------|------|------|
| 슬라이더 드래그 중 매 프레임 UpdateCellMetrics 호출 | atlas 재생성 + 렌더 스레드 stop/start 연속 | **300ms debounce**: 슬라이더 놓은 후 300ms 뒤 1회 적용 |

### 6.5 키바인딩 충돌 (심각도: 낮)

| 시나리오 | 위험 | 대응 |
|---------|------|------|
| 같은 키 조합을 두 액션에 할당 | 어떤 액션이 실행될지 불확실 | v1: **경고 표시** (빨간 텍스트). v2: 충돌 자동 해제 |

### 6.6 렌더 스레드 경쟁 (심각도: 낮)

| 시나리오 | 위험 | 대응 |
|---------|------|------|
| 설정 페이지↔터미널 전환 중 리사이즈 | M-14 span 경쟁 조건 재발 | Phase 6-B 방어 가드 (row() 범위 체크) 이미 적용 |

---

## 7. 설계 결정 (Design 단계에서 확정)

| # | 결정 항목 | 선택지 | 현재 기울기 | 근거 |
|:-:|----------|--------|:-----------:|------|
| D-1 | 설정 페이지 위치 | A: 터미널 대체 / B: 사이드 패널 / C: 별도 Window | **A** | WT 검증 패턴, Airspace 안전 |
| D-2 | Command Palette 구현 | A: 별도 Window / B: WPF Adorner / C: Popup | **A** | Airspace 유일한 해결책 |
| D-3 | 폰트 선택 UI | A: ComboBox (시스템 폰트) / B: TextBox 직접 입력 | **A** | 사용자 편의 |
| D-4 | 설정 저장 타이밍 | A: 즉시 (auto-save) / B: "저장" 버튼 / C: debounce | **C** (300ms) | 슬라이더 드래그 성능 보호 |
| D-5 | 키바인딩 편집 | A: 키 캡처 TextBox / B: 드롭다운 조합 | **A** | VS Code/WT 패턴 |
| D-6 | 테마 미리보기 | A: 즉시 적용 / B: 미리보기 패널 | **A** | 단순. 변경 후 마음에 안 들면 되돌리기 |
| D-7 | Mica 토글 반영 | A: 즉시 (가능 시) / B: "재시작 필요" 표시 | **B** | Mica는 윈도우 생성 시점에만 설정 가능 |

---

## 8. Phase 6 교훈 적용

| 교훈 | M-12 적용 |
|------|----------|
| ListBox 포커스 탈취 (Phase 6-B) | 설정 페이지의 모든 입력 컨트롤은 터미널 모드에서 Focusable=False (설정 모드에서만 활성화) |
| Airspace 회피 (Phase 6-B) | 설정 페이지: 터미널 영역 대체. Command Palette: 별도 Window |
| 자기 저장 루프 방지 | SettingsService에 suppress 플래그 추가 |
| msbuild 전체 빌드 의무 | 설정 페이지 추가 후 솔루션 빌드 검증 |
| 렌더 스레드 안전 | UpdateCellMetrics 호출 시 기존 stop/start 패턴 유지 |
| 의도적 간소화 명시 | v1 범위 밖 항목 (프로파일, 충돌 해제, Mica 즉시 적용) Design에서 S-* 표기 |

---

## 9. 성공 기준

| # | 기준 | 중요도 |
|:-:|------|:------:|
| 1 | `Ctrl+,` 또는 기어 아이콘으로 설정 페이지 열림 | 필수 |
| 2 | 폰트 크기 슬라이더 → 터미널 즉시 반영 | 필수 |
| 3 | 폰트 패밀리 ComboBox → 터미널 즉시 반영 | 필수 |
| 4 | 테마 전환 (dark/light) → 즉시 반영 | 필수 |
| 5 | 알림 토글 4개 → Phase 6 기능 on/off | 필수 |
| 6 | `Esc` → 터미널 복귀 + 포커스 자동 복원 | 필수 |
| 7 | GUI 변경 → ghostwin.json 저장 확인 | 필수 |
| 8 | ghostwin.json 수동 편집 → GUI 반영 | 필수 |
| 9 | 자기 저장 루프 발생하지 않음 | 필수 |
| 10 | `Ctrl+Shift+P` Command Palette 동작 | 선택 |
| 11 | 키바인딩 편집 + 적용 | 선택 |
| 12 | "JSON으로 편집" → 기본 에디터 열림 | 선택 |

---

## 10. 범위 밖 (YAGNI — 명시적 제외)

| 항목 | 이유 | 대안 |
|------|------|------|
| 프로파일 시스템 (WT profiles) | 과도한 복잡도. v1은 단일 프로파일 | v2에서 추가 |
| 컬러 스키마 에디터 | 내장 테마 선택으로 충분 | v2 또는 JSON 편집 |
| 설정 가져오기/내보내기 | v1 불필요 | v2 |
| 키바인딩 충돌 자동 해제 | v1은 경고만 | v2 |
| Mica 즉시 적용 | WPF 윈도우 생성 시점 제한 | "재시작 필요" 표시 |
| 폰트 미리보기 | 즉시 적용이 미리보기 역할 | 불필요 |
| 다국어 설정 UI | 영어/한국어 하드코딩 | v2 리소스 파일 |

---

## 11. 다음 단계

1. **`/pdca design m12-settings-ui`** — Wave별 코드 수준 구현 명세
2. **구현** — W1 → W7 순서
3. **수동 검증** — 12개 성공 기준 전수 확인

---

## 참조

- **PRD**: `docs/00-pm/m12-settings-ui.prd.md`
- **WT 설정 UI 리디자인**: [Windows Terminal Settings Redesign](https://windowsnews.ai/article/windows-terminal-settings-redesign-new-dedicated-window-streamlined-ui-and-simplified-configuration.411212)
- **설정 시스템 아카이브**: `docs/archive/2026-04/settings-system/`
- **AppSettings**: `src/GhostWin.Core/Models/AppSettings.cs`
- **SettingsService**: `src/GhostWin.Services/SettingsService.cs`

---

*M-12 Plan v1.0 — Settings UI (2026-04-17)*
