# WPF WinExe 프로젝트 테스트 참조 — BC-06 Research

> **결론 (2026-04-14)**: WPF WinExe + UseWPF 프로젝트는 .NET 10 테스트 라이브러리에서 **문제 없이 참조 가능**. Backlog follow-up #5 가 가정한 "library 참조 제약" 가설은 empirical 로 반박됨.

## 배경

- `Backlog/follow-up-cycles.md` #5 `first-pane-regression-tests` (LOW, pending): "WPF WinExe library 참조 제약 조사"
- 가설: `GhostWin.App` (`OutputType=WinExe`, `UseWPF=true`, `net10.0-windows`) 를 xUnit 테스트 프로젝트에서 참조하면 빌드 실패하거나 런타임 문제 발생

## 검증 방법

Pre-M11 Group 3 BC-06 spike — minimal test project 생성:

- **위치**: `tests/GhostWin.App.Tests/`
- **파일**: `GhostWin.App.Tests.csproj` (xUnit 2.9.3, FluentAssertions 7.0.0, TFM `net10.0-windows`, `UseWPF=true`) + `SmokeTest.cs` (단일 `[Fact]`)
- **명령**: `dotnet build ... -c Release` → `dotnet test ... -c Release --no-build`

## 결과

| 단계 | 결과 |
|------|------|
| 복원 | ✅ 1.85s |
| 빌드 | ✅ **0 warning, 0 error** (2.02s) |
| 테스트 실행 | ✅ **1/1 PASS** (867ms) |

## 세부 제약 사항

| 제약 | 영향 | 대응 |
|------|:----:|------|
| **TFM 일치 필요** | 테스트 프로젝트도 `net10.0-windows` + `UseWPF=true` 로 맞춰야 함 | csproj 에 명시 |
| **`internal` 타입 접근** | `GhostWin.App.Diagnostics.KeyDiag` 등 internal → CS0122 | `InternalsVisibleTo` 속성 또는 public 화 |
| **WinExe 런타임 실행** | 테스트 프로세스가 WinExe 의 `Main()` 을 실행하지 않음 | 문제 없음 — 테스트는 어셈블리 로드만 필요 |
| **WPF UI 스레드 의존 코드** | `Dispatcher` / `Application.Current` 등 needs test fixture | `WpfFact` / `STAThread` 헬퍼 또는 ViewModel 만 테스트 (권장) |

## 권장 테스트 전략

1. **View (XAML) 테스트 지양** — WPF UI 는 수동/E2E 영역
2. **ViewModel + 순수 로직 테스트 집중** — `TerminalTabViewModel`, `WorkspaceItemViewModel`, `MainWindowViewModel` 등 **public** 타입
3. **Diagnostic 코드 (KeyDiag, RenderDiag)** 테스트 시 `InternalsVisibleTo("GhostWin.App.Tests")` 추가 또는 테스트용 public wrapper 도입
4. **First-pane regression 테스트**: 실제 렌더 파이프라인이 HwndHost + DX11 + ghostty 에 의존하므로 단위 테스트로는 한계. **렌더 상태 (RenderState, SessionManager) level 에서 간접 검증** 권장

## 후속 조치

- `tests/GhostWin.App.Tests/` 유지 (앞으로 regression test 추가 가능한 baseline)
- `GhostWin.Core.Tests`, `GhostWin.Engine.Tests` 와 동등 위치로 승격
- Solution 파일에 등록 필요 여부 확인 (현재 .sln 에 추가 안 됨 — 후속 작업)

## 산출물

- `tests/GhostWin.App.Tests/GhostWin.App.Tests.csproj` (신규)
- `tests/GhostWin.App.Tests/SmokeTest.cs` (신규, 단일 smoke)
- 본 문서
