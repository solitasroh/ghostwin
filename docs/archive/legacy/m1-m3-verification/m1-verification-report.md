# M-1 Solution Structure — Design 대비 구현 검증

> **빌드 상태**: ✅ `dotnet build GhostWin.slnx -c Release` — 경고 0, 오류 0 (1.57초)
> **검증 일시**: 2026-04-06 14:13

---

## 1. 프로젝트 구조 검증

### 1.1 솔루션 파일

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| 솔루션 파일 | `GhostWin.sln` | `GhostWin.slnx` (XML 형식) | ⚠️ |
| 프로젝트 수 | 4개 | 4개 | ✅ |
| 폴더 구조 | `/src/` 아래 | `/src/` 아래 | ✅ |

> [!NOTE]
> `.slnx`는 .NET 10의 새로운 XML 기반 솔루션 형식. 기능적으로 `.sln`과 동등. 문제 없음.

### 1.2 .csproj 파일 상세 비교

#### GhostWin.Core.csproj

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| TargetFramework | `net10.0` | `net10.0` | ✅ |
| UseWPF | 없음 | 없음 | ✅ |
| Nullable | enable | enable | ✅ |
| CommunityToolkit.Mvvm | `8.*` | `8.*` | ✅ |

#### GhostWin.Interop.csproj

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| TargetFramework | `net10.0-windows` | `net10.0-windows` | ✅ |
| UseWPF | true | true | ✅ |
| AllowUnsafeBlocks | true | true | ✅ |
| ProjectReference → Core | ✅ | ✅ | ✅ |

#### GhostWin.Services.csproj

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| TargetFramework | `net10.0` | `net10.0` | ✅ |
| UseWPF | 없음 | 없음 | ✅ |
| ProjectReference → Core | ✅ | ✅ | ✅ |
| DI.Abstractions `10.*` | ✅ | ✅ | ✅ |

#### GhostWin.App.csproj

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| OutputType | WinExe | WinExe | ✅ |
| TargetFramework | `net10.0-windows` | `net10.0-windows` | ✅ |
| UseWPF | true | true | ✅ |
| AllowUnsafeBlocks | true | true | ✅ |
| ApplicationManifest | `app.manifest` | `app.manifest` | ✅ |
| ProjRef → Core | ✅ | ✅ | ✅ |
| ProjRef → Interop | ✅ | ✅ | ✅ |
| ProjRef → Services | ✅ | ✅ | ✅ |
| WPF-UI | `3.*` | `3.*` | ✅ |
| CommunityToolkit.Mvvm | `8.*` | `8.*` | ✅ |
| MS.Extensions.DI | `10.*` | `10.*` | ✅ |

> **csproj 판정: 4/4 완전 일치** ✅

---

## 2. DI 구성 검증 (App.xaml.cs)

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| `ApplicationThemeManager.Apply(Dark)` | ✅ | ✅ | ✅ |
| `ServiceCollection` 사용 | ✅ | ✅ | ✅ |
| `ISettingsService → SettingsService` | Singleton | Singleton | ✅ |
| `MainWindowViewModel` | Singleton | Singleton | ✅ |
| `Ioc.Default.ConfigureServices` | ✅ | ✅ | ✅ |
| `IEngineService → EngineService` | Singleton | ❌ **미등록** | ⚠️ |
| `ISessionManager → SessionManager` | Singleton | ❌ **미등록** | ⚠️ |
| `TerminalTabViewModel` | Transient | ❌ **미등록** | ⚠️ |
| `GCHandle` 핀닝 | ✅ | ❌ **없음** | ⚠️ |

> [!IMPORTANT]
> Design에서는 `IEngineService`, `ISessionManager`, `TerminalTabViewModel`도 DI에 등록하지만, M-1은 스텁 단계이므로 구현체가 아직 없습니다. 이는 **M-2/M-3에서 추가 예정**이므로 M-1 범위에서는 합리적입니다.
>
> 단, `GCHandle` 핀닝은 Design §3.1.2에서 M-1에 포함되어 있으나 구현에 없습니다. 이것은 **M-2에서 콜백 구현 시 필요**하므로, M-2로 미룬 것이라면 Design을 업데이트하거나 코멘트 추가가 필요합니다.

---

## 3. FluentWindow 검증

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| `ui:FluentWindow` 사용 | ✅ | ✅ | ✅ |
| `WindowBackdropType="Mica"` | ✅ | ✅ | ✅ |
| `ExtendsContentIntoTitleBar="True"` | ✅ | ✅ | ✅ |
| `Title` 바인딩 | `{Binding WindowTitle}` | 하드코딩 `"GhostWin Terminal"` | ⚠️ |
| Grid 레이아웃 | Sidebar + GridSplitter + Terminal | Sidebar + Terminal (Splitter 없음) | ⚠️ |
| 사이드바 Width | `200 MinWidth=120 MaxWidth=400` | `200` (고정) | ⚠️ |
| App.xaml 리소스 | ThemesDictionary + ControlsDictionary | ✅ 동일 | ✅ |

> [!NOTE]
> Title 바인딩과 GridSplitter는 M-3(세션/탭 관리)에서 구현 예정이므로, M-1 스텁에서 없는 것은 합리적입니다.

---

## 4. Core 프로젝트 인터페이스 검증

### 4.1 IEngineService

| API | Design | 구현 | 판정 |
|-----|--------|------|:----:|
| `IsInitialized` | ✅ | ✅ | ✅ |
| `Initialize(GwCallbackContext)` | `GwCallbackContext` 파라미터 | **파라미터 없음** `Initialize()` | ⚠️ |
| `RenderInit` | ✅ | ✅ | ✅ |
| `RenderResize` | ✅ | ✅ | ✅ |
| `RenderSetClearColor` | ✅ | ❌ **누락** | ❌ |
| `RenderStart` | ✅ | ✅ | ✅ |
| `RenderStop` | ✅ | ✅ | ✅ |
| `CreateSession` | ✅ | `SessionCreate` (이름 다름) | ⚠️ |
| `CloseSession` | ✅ | `SessionClose` (이름 다름) | ⚠️ |
| `ActivateSession` | ✅ | `SessionActivate` (이름 다름) | ⚠️ |
| `WriteSession` | ✅ | `SessionWrite` (이름 다름) | ⚠️ |
| `ResizeSession` | ✅ | `SessionResize` (이름 다름) | ⚠️ |
| `TsfAttach` | ✅ | ✅ | ✅ |
| `TsfFocus` | ✅ | ✅ | ✅ |
| `TsfUnfocus` | ✅ | ✅ | ✅ |
| `TsfSendPending` | ✅ | ✅ | ✅ |
| `SessionCount` | 프로퍼티 | **메서드** `SessionCount()` | ⚠️ |
| `ActiveSessionId` | 프로퍼티 | **메서드** `ActiveSessionId()` | ⚠️ |
| `PollTitles` | ✅ | ❌ **누락** | ❌ |
| `Shutdown` | ✅ | ❌ **누락** | ❌ |
| API 총 수 | 19개 매핑 | 16개 | ❌ |

> [!WARNING]
> **3개 API 누락**: `RenderSetClearColor`, `PollTitles`, `Shutdown`
> **메서드명 통일**: Design은 `CreateSession` / 구현은 `SessionCreate` 등 — 네이밍 컨벤션이 다름
> **프로퍼티 vs 메서드**: `SessionCount`/`ActiveSessionId`가 Design은 프로퍼티, 구현은 메서드

### 4.2 ISessionManager

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| `ActiveSessionId` | `uint?` | `uint?` | ✅ |
| `Sessions` | `IReadOnlyList<SessionInfo>` | `IReadOnlyList<SessionInfo>` | ✅ |
| `CreateSession` 반환 | `uint` (세션 ID) | `SessionInfo` (전체 객체) | ⚠️ |
| `CloseSession` | ✅ | ✅ | ✅ |
| `ActivateSession` | ✅ | ✅ | ✅ |
| `UpdateTitle` | ✅ | ❌ **누락** | ❌ |
| `UpdateCwd` | ✅ | ❌ **누락** | ❌ |
| `WriteToActive` | ❌ Design에 없음 | ✅ **추가됨** | ⚠️ |
| `ResizeActive` | ❌ Design에 없음 | ✅ **추가됨** | ⚠️ |

### 4.3 ISettingsService

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| `Current` | `AppSettings` | `AppSettings` | ✅ |
| `SettingsFilePath` | ✅ | ❌ **없음** | ❌ |
| `Load()` | ✅ | `Reload()` (이름 다름) | ⚠️ |
| `Save()` | ✅ | ❌ **없음** | — |
| `StartWatching()` | ✅ | ❌ **없음** | — |
| `StopWatching()` | ✅ | ❌ **없음** | — |

> [!NOTE]
> `Save`, `StartWatching`, `StopWatching`은 M-4 범위이므로 M-1에서 없는 것은 정상. 다만 `SettingsFilePath`와 `Load()`→`Reload()` 이름 차이는 M-4 구현 시 주의 필요.

### 4.4 SessionInfo 모델

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| 상속 | `ObservableObject` | **상속 없음** (POCO) | ❌ |
| `Id` | `init` | `init` | ✅ |
| `Title` | `SetProperty` 알림 | 일반 `set` | ❌ |
| `Cwd` | `SetProperty` 알림 | 일반 `set` | ❌ |
| `IsActive` | `SetProperty` 알림 | 일반 `set` | ❌ |

> [!WARNING]
> Design에서 `SessionInfo`는 `ObservableObject`를 상속하여 `SetProperty`로 PropertyChanged를 발생시킵니다. 구현은 일반 POCO 클래스입니다. M-3에서 탭 UI 바인딩 시 **PropertyChanged 알림이 동작하지 않아** UI가 갱신되지 않는 문제가 발생합니다.

### 4.5 AppSettings 모델

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| `Appearance` | `string` (최상위) | `AppAppearance.Appearance` (중첩) | ⚠️ |
| `Sidebar` | `SidebarSettings` | `SidebarSettings` | ✅ |
| `Sidebar.Width` 기본값 | `200` | `250` | ❌ |
| `Titlebar` | `TitlebarSettings` | ❌ **없음** | ❌ |
| `Keybindings` | `Dictionary<string,string>` | `Dictionary<string,string>` | ✅ |
| `sealed class` | ✅ | 일반 `class` | ⚠️ |

### 4.6 SessionEvents

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| 메시지 타입 | `sealed record` | `sealed class : ValueChangedMessage<T>` | ⚠️ |
| `SessionCreatedMessage` | `record(uint)` | `ValueChangedMessage<uint>` | ⚠️ |
| `SessionChildExitMessage` | ✅ | ❌ **누락** | ❌ |

> [!NOTE]
> `ValueChangedMessage<T>` 기반은 기능적으로 동등하지만, Design의 `sealed record` 패턴과 다릅니다. `ValueChangedMessage`는 `OldValue`를 트래킹하므로 단순 이벤트에는 과도할 수 있습니다.

---

## 5. 판정 요약

### M-1 완료 기준 (Design §3.1)

| 기준 | 판정 |
|------|:----:|
| 4프로젝트 (Core/Interop/Services/App) 생성 | ✅ |
| 의존성 설정 (프로젝트 참조 + NuGet) | ✅ |
| `dotnet build` 성공 | ✅ |
| DI 컨테이너 구성 (App.xaml.cs) | ✅ (스텁 수준) |
| 빈 FluentWindow (Mica + 다크모드) 표시 | ✅ |
| **WPF-UI 4.x 게이트 평가** | ❌ **미실시** (3.x 유지) |

### GAP 요약 (M-2 진입 전 주의)

| # | 심각도 | 항목 | 조치 시점 |
|---|:------:|------|----------|
| 1 | 🔴 | `SessionInfo`가 `ObservableObject` 미상속 → M-3에서 바인딩 실패 | M-2 또는 M-3 시작 전 |
| 2 | 🔴 | `IEngineService` 3개 API 누락 (`RenderSetClearColor`, `PollTitles`, `Shutdown`) | M-2 시작 시 |
| 3 | 🟡 | `IEngineService` 메서드명 Design과 불일치 (`SessionCreate` vs `CreateSession`) | M-2에서 통일 |
| 4 | 🟡 | `AppSettings.Sidebar.Width` 기본값 250 ≠ Design의 200 | 즉시 수정 가능 |
| 5 | 🟡 | `TitlebarSettings` 누락 | M-5에서 필요 |
| 6 | 🟡 | `GCHandle` 핀닝 미구현 | M-2에서 콜백 구현 시 |
| 7 | 🟢 | `SessionChildExitMessage` 누락 | M-3에서 추가 |
