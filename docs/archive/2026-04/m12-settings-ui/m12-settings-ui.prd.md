# M-12 PRD — 사용자 설정 UI (Settings UI)

> **문서 종류**: Product Requirements Document (PRD)
> **작성일**: 2026-04-17
> **마일스톤**: M-12
> **소유자**: 노수장
> **선행**: Phase 6 전체 완료 (6-A 93% + 6-B 97% + 6-C 95%)

---

## Executive Summary

| 관점 | 내용 |
|------|------|
| **Problem** | 모든 설정(폰트, 테마, 알림, 키바인딩)을 `%APPDATA%/GhostWin/ghostwin.json` 수동 편집으로만 변경 가능. 파일 위치를 모르거나 JSON 문법 오류 시 설정 깨짐 |
| **Solution** | XAML 기반 설정 페이지 (사이드바 내 또는 별도 패널) + Command Palette (검색 기반 명령 실행)로 GUI 설정 제공. 기존 `ISettingsService`/`AppSettings` 모델 + `SettingsChangedMessage` 이벤트 재사용 |
| **Function / UX Effect** | `Ctrl+,` 또는 메뉴로 설정 페이지 열기 → 폰트 크기/패밀리 슬라이더, 테마 선택 드롭다운, 알림 토글, 키바인딩 편집. 변경 즉시 반영 (FileWatcher + UpdateCellMetrics). `Ctrl+Shift+P` Command Palette로 빠른 명령 실행 |
| **Core Value** | 터미널 기본기 완성. JSON 편집 없이 설정 변경 → 일반 사용자 접근성 향상. cmux/Windows Terminal/Warp 대비 경쟁력 확보 |

---

## 1. 배경과 동기

### 1.1 현재 상태

```
설정 변경 경로 (현재)
├── 1. %APPDATA%/GhostWin/ghostwin.json 파일 위치 파악
├── 2. 텍스트 에디터로 JSON 수동 편집
├── 3. 저장 → FileSystemWatcher 감지 (50ms debounce)
├── 4. SettingsChangedMessage → MainWindowViewModel.ApplySettings()
└── 5. 엔진 반영 (폰트 → UpdateCellMetrics)
```

사용자 체감 문제:
- JSON 파일 위치를 모름
- snake_case 프로퍼티 이름을 모름
- 오타 → JSON 파싱 실패 → 기본값으로 리셋
- 어떤 설정이 가능한지 발견 불가능

### 1.2 경쟁 제품 설정 UI 비교

| 터미널 | 설정 방식 | UI 스타일 |
|--------|----------|----------|
| **Windows Terminal** | `settings.json` + GUI 설정 페이지 | 탭 기반 페이지 (Appearance/Interaction/Rendering) |
| **cmux** | `~/.config/cmux/cmux.toml` + 앱 내 설정 | macOS 설정 시트 |
| **Warp** | 앱 내 Settings | 검색 가능한 설정 목록 |
| **VS Code** | `settings.json` + GUI + Command Palette | 검색 가능한 키-값 편집기 |
| **GhostWin (현재)** | `ghostwin.json` 수동 편집만 | **GUI 없음** |

---

## 2. 타겟 사용자

**모든 GhostWin 사용자** — 특히 터미널 커스터마이징에 익숙하지 않은 사용자.
Phase 6 기능(알림 패널/배지/Named Pipe)을 토글할 수 있는 GUI도 필요.

---

## 3. 기능 요구사항

### FR-01: 설정 페이지 (Settings Page)

**우선순위**: 필수 | **규모**: 중

| 항목 | 상세 |
|------|------|
| **열기** | `Ctrl+,` 또는 사이드바 하단 기어 아이콘 |
| **위치** | 터미널 영역을 대체하는 전체 페이지 (WT 패턴) 또는 오버레이 패널 |
| **닫기** | `Esc` 또는 X 버튼 → 터미널 복귀 |
| **저장** | 변경 즉시 Save (debounce 300ms) → ghostwin.json 업데이트 |

**설정 카테고리**:

| 카테고리 | 설정 항목 | 바인딩 대상 |
|---------|---------|-----------|
| **외관** | 테마 (dark/light), Mica 배경 | `Appearance`, `Titlebar.UseMica` |
| **폰트** | 패밀리, 크기, 셀 비율 | `Terminal.Font.*` → `UpdateCellMetrics` |
| **사이드바** | 표시, 너비, CWD 표시, git 표시 | `Sidebar.*` |
| **알림** | 링 활성화, Toast, 패널, 배지 | `Notifications.*` |
| **키바인딩** | 액션별 키 조합 편집 | `Keybindings` Dictionary |

### FR-02: Command Palette

**우선순위**: 선택 | **규모**: 중

| 항목 | 상세 |
|------|------|
| **열기** | `Ctrl+Shift+P` |
| **UI** | 오버레이 텍스트박스 + 필터링 리스트 (VS Code 패턴) |
| **Airspace** | WPF Popup은 D3D11과 충돌 → **별도 최상위 Window** (Airspace 우회) |
| **명령 목록** | 새 탭, 탭 닫기, 분할, 설정 열기, 테마 전환 등 |
| **검색** | 퍼지 매칭 (명령 이름 substring) |

---

## 4. 비기능 요구사항

| 항목 | 기준 |
|------|------|
| **설정 반영 지연** | < 300ms (Save → FileWatcher → ApplySettings) |
| **폰트 변경** | UpdateCellMetrics 호출 → atlas 재생성 → 렌더 스레드 재시작 (기존 경로) |
| **JSON 호환** | 기존 ghostwin.json 수동 편집과 완전 호환. UI 편집과 수동 편집 공존 |
| **접근성** | 모든 설정 항목에 AutomationId |
| **Airspace** | Command Palette는 D3D11 SwapChain 위에 표시 → 별도 Window 필수 |

---

## 5. 범위 밖

| 항목 | 이유 |
|------|------|
| 프로파일 시스템 (WT의 profiles.json) | 복잡도 과다. v1은 단일 프로파일 |
| 셸별 설정 (PowerShell vs cmd) | v1 범위 밖 |
| 컬러 스키마 에디터 | 내장 테마 선택만 v1 |
| 설정 가져오기/내보내기 | v2 |
| 키바인딩 충돌 감지 | v2 |

---

## 6. 기술 의존성

### 6.1 기존 자산 (100% 재사용)

| 자산 | M-12 활용 |
|------|----------|
| `AppSettings` 모델 | 설정 페이지의 바인딩 대상 |
| `ISettingsService` | Load/Save/StartWatching |
| `SettingsChangedMessage` | 설정 변경 → UI 반영 |
| `UpdateCellMetrics` | 폰트/DPI 변경 즉시 반영 |
| FileSystemWatcher | 수동 편집과 GUI 편집 공존 |

### 6.2 Phase 5-D 아카이브 참조

`docs/archive/2026-04/settings-system/` — C++ 시절 설계 (10 내장 테마, KeyMap, Observer 패턴). C# 이행 시 단순화됨.

---

## 7. 성공 기준

| # | 기준 | 검증 |
|:-:|------|------|
| 1 | `Ctrl+,`로 설정 페이지 열림 | 수동 확인 |
| 2 | 폰트 크기 변경 → 터미널 즉시 반영 | 수동 확인 |
| 3 | 테마 전환 (dark/light) | 수동 확인 |
| 4 | 알림 토글 → Phase 6 기능 on/off | 수동 확인 |
| 5 | 설정 페이지에서 변경 → ghostwin.json에 저장됨 | 파일 확인 |
| 6 | ghostwin.json 수동 편집 → 설정 페이지에 반영 | FileWatcher 확인 |
| 7 | Command Palette (`Ctrl+Shift+P`) 동작 (선택) | 수동 확인 |

---

## 8. 구현 순서 제안

```
FR-01 설정 페이지
  ├── W1: SettingsPageViewModel + 데이터 바인딩
  ├── W2: 설정 페이지 XAML (카테고리별 섹션)
  ├── W3: 폰트 변경 → UpdateCellMetrics 연동
  ├── W4: Save 즉시 반영 + FileWatcher 양방향 동기화
  └── W5: Ctrl+, 키 바인딩 + 페이지 전환 로직
      ↓
FR-02 Command Palette (선택)
  ├── W6: CommandPaletteWindow (별도 최상위 Window)
  └── W7: 명령 목록 + 퍼지 검색
```

**예상 기간**: 1.5~2일 (FR-01만 필수, FR-02는 선택)

---

## 9. 리스크

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| Airspace 충돌 (Command Palette) | 중 | 별도 Window 사용 (Phase 6-B 알림 패널에서 검증된 Grid Column 패턴과 다름) |
| 폰트 변경 시 렌더 스레드 stop/start | 낮 | UpdateCellMetrics가 이미 처리 (dpi-scaling-integration에서 검증) |
| 설정 페이지 ↔ 터미널 전환 시 포커스 | 중 | 설정 닫기 시 터미널 HwndHost에 포커스 복원 필요 |
| JSON 직렬화 호환성 | 낮 | 기존 SettingsService.Save()의 병합 패턴 유지 |

---

## 참조

- 로드맵: `docs/01-plan/roadmap.md` §M-12
- 설정 시스템 아카이브: `docs/archive/2026-04/settings-system/`
- AppSettings 모델: `src/GhostWin.Core/Models/AppSettings.cs`
- SettingsService: `src/GhostWin.Services/SettingsService.cs`

---

*M-12 PRD v1.0 — Settings UI (2026-04-17)*
