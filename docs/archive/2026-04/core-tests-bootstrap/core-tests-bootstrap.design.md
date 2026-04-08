# Core Tests Bootstrap — Design Document

> **Summary**: `tests/GhostWin.Core.Tests` xUnit 프로젝트 신설. PaneNode에 대해 9개 단위 테스트(T-1~T-5 + 3건 보강) 구현. 프로젝트 최초 테스트 인프라.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 P0-1 — 부채 청산
> **Author**: 노수장 (Council: dotnet-expert / qa-strategist / code-analyzer, CTO Lead synthesis by Opus)
> **Date**: 2026-04-07
> **Status**: Council-reviewed
> **Plan**: `docs/01-plan/features/core-tests-bootstrap.plan.md`

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 솔루션 전체 `*.Tests.csproj` 0건. Plan 문서가 FluentAssertions를 명시했으나 Design council에서 **v8.0부터 상용 라이선스 전환** (Xceed)을 확인 — 라이선스 리스크와 기술 선택을 Design 단계에서 고정해야 함. `PaneNode.RemoveLeaf` grandparent splice는 현재 **순수 로직조차 회귀 방어 수단 0**. |
| **Solution** | `tests/GhostWin.Core.Tests/` 신설 — `net10.0` + xUnit 2.9.3 + **FluentAssertions 7.0.0 (마지막 Apache-2.0 버전, 상한 고정)** + PaneNode만 참조. 9개 테스트 (T-1~T-5 core 5건 + T-3a root parent / T-3c root removal / T-4b single leaf 3건 보강). PS1 래퍼 `scripts/test_ghostwin.ps1`. `.sln` 없이 per-csproj 실행. |
| **Function/UX Effect** | 사용자 기능 변경 0. 개발자 관점: `dotnet test tests/GhostWin.Core.Tests/` 단일 명령으로 10초 내 9/9 Green 확인. 후속 P0-2 BISECT 종료/P0-3 종료 경로 단일화 작업 시 `PaneNode` 관련 회귀를 즉각 감지. grandparent splice paneId 보존 invariant는 `BeSameAs` reference-equality 검증으로 명시적 lock-in. |
| **Core Value** | "**테스트 있음 vs 없음**"의 0 → 1 전환. FluentAssertions 라이선스 의사결정이 Plan→Design 단계에서 원인 분석을 통해 올바르게 드러난 것은 rkit council 방법론의 직접적 가치 (v0.5 10-agent 평가 §4 P0 #1 권고의 구체 실현). |

---

## 1. Overview

### 1.1 Council Synthesis

본 Design은 3개 council agent (`rkit:dotnet-expert`, `rkit:qa-strategist`, `rkit:code-analyzer`)의 병렬 리뷰 결과를 CTO Lead가 통합한 문서.

**합의 영역 (3/3 agents)**:
- `TargetFramework`: `net10.0` (GhostWin.Core와 동일, WPF 비의존)
- `.sln` 생략 — 현재 `build_ghostwin.ps1`/`build_wpf_poc.ps1`가 solution 파일을 경유하지 않음
- SUT(`PaneNode.cs`) 수정 금지 — static state 0, 외부 의존 0, 모든 API public
- 디렉토리 레이아웃: `tests/GhostWin.Core.Tests/Models/PaneNodeTests.cs`
- 테스트 메서드 네이밍: `MethodName_Scenario_ExpectedResult`

**충돌 영역 (resolved by user decision 2026-04-07)**:
- Assertion 라이브러리: FluentAssertions 7.0.0 vs Shouldly 4.3.0 vs xUnit 기본 vs FA 6.12.2
- **확정**: **FluentAssertions 7.0.0** (마지막 Apache-2.0 버전, Plan 명시 유지, 문법 친숙성)

### 1.2 Why This Design Is Small But Critical

본 feature는 코드 라인 수로는 작으나(csproj 1개, 테스트 파일 1개, PS1 스크립트 1개), **프로젝트 첫 테스트 인프라**이므로 모든 결정이 향후 convention 기준선이 된다. `GhostWin.Services.Tests`, `GhostWin.Interop.Tests` 등 후속 테스트 프로젝트는 본 Design의 csproj 구조를 복제할 것이므로 **버전 핀, 폴더 구조, 네이밍, Trait 전략**을 지금 확정한다.

### 1.3 Related Documents

- `docs/01-plan/features/core-tests-bootstrap.plan.md` (authoritative scope)
- `docs/02-design/features/pane-split.design.md` §11 T-1~T-5 (test spec 출처)
- `docs/03-analysis/pane-split-workspace-completeness-v0.5.md` §1 C3, §4 P0 #1
- `src/GhostWin.Core/Models/PaneNode.cs` (SUT, 93 lines)
- `src/GhostWin.Core/Models/IReadOnlyPaneNode.cs` (SUT 인터페이스)

---

## 2. Locked-in Design Decisions

| # | 결정 | 값 | 근거 |
|---|------|-----|------|
| D1 | Test framework | **xUnit 2.9.3** | netstandard2.0 기반으로 모든 .NET 위에서 동작, 다운로드 수 870M(v2) vs 18M(v3), T-1~T-5는 `[Fact]` 기본 기능만 사용하므로 v3 추가 기능 불필요 |
| D2 | Assertion library | **FluentAssertions 7.0.0** (사용자 결정) | 마지막 Apache-2.0 버전. v8+는 Xceed 상용 라이선스. `Version="7.0.0"` 고정 핀 (minor floating 금지) |
| D3 | Test SDK | **Microsoft.NET.Test.Sdk 18.4.0** | 현재 NuGet 최신 stable |
| D4 | Runner | **xunit.runner.visualstudio 3.1.5** | v1/v2/v3 모두 지원, `PrivateAssets=all` 전이 의존 차단 |
| D5 | TargetFramework | **`net10.0`** | `GhostWin.Core.csproj` 실측 확인 — `net10.0` (WPF 비의존). `-windows` suffix 불필요 |
| D6 | Nullable / ImplicitUsings | **enable / enable** | Core 프로젝트와 동일. `PaneNode?` 반환 타입 테스트에 필수 |
| D7 | LangVersion | **latest** (C# 13 on net10.0) | 제약 없음 |
| D8 | IsPackable | **false** | 테스트 프로젝트는 NuGet 배포 대상 아님 |
| D9 | Solution file | **생성하지 않음** | `.sln` 없이 per-csproj 직접 호출이 현재 프로젝트의 실제 workflow. 별도 `chore: add GhostWin.sln` 커밋이 필요하면 이 feature 외부에서 처리 |
| D10 | 테스트 메서드 네이밍 | **`MethodName_Scenario_ExpectedResult`** | .NET Foundation 공식 가이드. `Split_OnLeafNode_*`, `RemoveLeaf_GrandparentSplice_*` 형태로 IDE 그룹핑 자연스러움 |
| D11 | Trait 전략 | **`[Trait("Category", "Unit")]` 단일** | 규모 대비 세분화 overhead 회피. 후속 Integration 도입 시 확장 |
| D12 | FluentAssertions 핵심 이디엄 | `BeSameAs` (reference equality — paneId 보존 검증의 핵심) / `Throw<T>().WithMessage(...)` / `ContainInOrder` / `HaveCount` / `BeNull` | qa-strategist 권고 |
| D13 | 빌드 스크립트 | **`scripts/test_ghostwin.ps1` 신설** (기존 스크립트 수정 0) | `.claude/rules/behavior.md` "PowerShell 우선", `build_ghostwin.ps1` 스타일 일관 |
| D14 | 한국어 Windows 인코딩 | `[Console]::OutputEncoding = UTF8` + `DOTNET_CLI_UI_LANGUAGE=en` | `build_ghostwin.ps1`의 `VSLANG=1033` 패턴을 .NET CLI 버전으로 적용 |

---

## 3. Architecture / Dependency Graph

### 3.1 Directory Layout

```
ghostwin/
├── src/
│   ├── GhostWin.Core/              ← SUT 프로젝트 (net10.0)
│   │   └── Models/
│   │       ├── PaneNode.cs         ← SUT
│   │       └── IReadOnlyPaneNode.cs
│   ├── GhostWin.Interop/           ← 의존 금지
│   ├── GhostWin.Services/          ← 의존 금지 (T-6~T-11 영역)
│   └── GhostWin.App/               ← 의존 금지
├── tests/                          ← [신설]
│   └── GhostWin.Core.Tests/        ← [신설]
│       ├── GhostWin.Core.Tests.csproj
│       └── Models/
│           └── PaneNodeTests.cs
└── scripts/
    ├── build_ghostwin.ps1          ← 수정 없음
    ├── build_libghostty.ps1        ← 수정 없음
    ├── build_wpf_poc.ps1           ← 수정 없음
    └── test_ghostwin.ps1           ← [신설]
```

### 3.2 Dependency Direction (Clean Architecture 정합)

```
┌─────────────────────────┐
│ GhostWin.Core.Tests     │ ─── 참조 ──▶  GhostWin.Core
└─────────────────────────┘                    ▲
                                                │
              (Interop/Services/App은 의존하지 않음)
```

테스트 프로젝트는 **Core만 참조**. Clean Architecture 의존성 방향("바깥쪽이 안쪽을 참조")을 테스트 레이어에도 그대로 적용.

**허용 import** (code-analyzer 검증):
- `GhostWin.Core.Models.PaneNode`
- `GhostWin.Core.Models.IReadOnlyPaneNode`
- `GhostWin.Core.Models.SplitOrientation` (enum)

`SessionInfo`, `WorkspaceInfo`, `FocusDirection` 등 다른 `GhostWin.Core.Models` 타입도 기술적으로는 참조 가능하지만, PaneNode 테스트에서 사용하지 않음 (YAGNI).

### 3.3 Transitive Dependency

`ProjectReference`로 Core를 참조하면 `CommunityToolkit.Mvvm 8.*`이 transitively 포함된다. PaneNode는 MVVM 의존 0이지만 restore 시간에는 영향 없음 (낮은 리스크).

---

## 4. Detailed Design

### 4.1 `tests/GhostWin.Core.Tests/GhostWin.Core.Tests.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.4.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <!--
      FluentAssertions 7.0.0 is the last Apache-2.0 version.
      v8+ switched to Xceed commercial license.
      Do NOT bump major without license review.
    -->
    <PackageReference Include="FluentAssertions" Version="7.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\GhostWin.Core\GhostWin.Core.csproj" />
  </ItemGroup>

</Project>
```

### 4.2 Test Case Inventory

**총 9개 테스트 메서드** — Plan §3.1 FR-02~FR-06의 T-1~T-5를 충실히 구현하되, code-analyzer가 식별한 핵심 edge case 3건을 보강.

| # | Test Method | Source | Priority |
|---|-------------|--------|:--------:|
| 1 | `Split_OnLeafNode_ProducesCorrectBranchStructure` | T-1 | High |
| 2 | `Split_OnBranchNode_ThrowsInvalidOperationException` | T-2 | High |
| 3 | `RemoveLeaf_WhenParentIsRoot_ReturnsReparentedSurvivingSibling` | T-3a (2-pane case) | High |
| 4 | `RemoveLeaf_GrandparentSplice_PreservesSurvivingSiblingPaneId` | T-3b (3-level, paneId 보존 invariant) | **Critical** |
| 5 | `RemoveLeaf_WhenLeafIsRoot_ReturnsNull` | T-3c / E1 | Medium |
| 6 | `GetLeaves_OnThreeLevelTree_ReturnsDfsOrderLeftFirst` | T-4 | High |
| 7 | `GetLeaves_OnSingleLeafTree_ReturnsSelfOnly` | T-4b / E5 | Medium |
| 8 | `FindLeaf_WithExistingSessionId_ReturnsCorrectLeaf` | T-5a (positive) | Medium |
| 9 | `FindLeaf_WithNonExistentSessionId_ReturnsNull` | T-5b (negative, Plan 원본 T-5) | High |

**Deferred (이 feature 외)**:
- E2: `RemoveLeaf` with leaf not in tree (contract 불명확) — follow-up
- E4: `Split` with `oldLeafId == newLeafId` (가드 부재) — YAGNI
- E6/E7: `FindLeaf(0)`, `FindParent(this)` — 사용 사례 없음
- `FindLeafById`, `FindParent` 별도 테스트 — 후속 배치

### 4.3 Test Method Pseudocode

**핵심 invariant 주석 형태**:
- T-4 (Grandparent splice): `root.Left.Should().BeSameAs(leafB)` — `Be` 아님. `BeSameAs`가 paneId 보존의 핵심 검증
- T-2: `.WithMessage("Cannot split branch node")` — 소스 하드코딩 메시지 lock-in (wildcard 금지)
- 모든 `uint` 리터럴에 `u` suffix 명시 — FluentAssertions 타입 엄격성

#### 4.3.1 `PaneNodeTests.cs` 전체 의사코드

```csharp
using FluentAssertions;
using GhostWin.Core.Models;
using Xunit;

namespace GhostWin.Core.Tests.Models;

public class PaneNodeTests
{
    // ── T-1 ──────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Split_OnLeafNode_ProducesCorrectBranchStructure()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);

        // Act
        var (oldLeaf, newLeaf) = root.Split(
            SplitOrientation.Vertical,
            newSessionId: 20,
            oldLeafId: 2,
            newLeafId: 3);

        // Assert — tuple은 (oldLeaf, newLeaf) 순서
        root.Left.Should().BeSameAs(oldLeaf);
        root.Right.Should().BeSameAs(newLeaf);

        // 분기 노드 invariant
        root.IsLeaf.Should().BeFalse();
        root.SessionId.Should().BeNull();
        root.SplitDirection.Should().Be(SplitOrientation.Vertical);

        // 리프 노드 상태
        oldLeaf.Id.Should().Be(2u);
        oldLeaf.SessionId.Should().Be(10u);     // 기존 세션 보존
        oldLeaf.IsLeaf.Should().BeTrue();

        newLeaf.Id.Should().Be(3u);
        newLeaf.SessionId.Should().Be(20u);     // 새 세션
        newLeaf.IsLeaf.Should().BeTrue();
    }

    // ── T-2 ──────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void Split_OnBranchNode_ThrowsInvalidOperationException()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);

        // Act
        Action act = () => root.Split(
            SplitOrientation.Horizontal,
            newSessionId: 30,
            oldLeafId: 4,
            newLeafId: 5);

        // Assert — 소스의 literal 메시지 lock-in
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("Cannot split branch node");
    }

    // ── T-3a (2-pane, root as parent) ────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveLeaf_WhenParentIsRoot_ReturnsReparentedSurvivingSibling()
    {
        // Arrange
        //   root(id=1)
        //   /       \
        // left(2)  right(3, sid=20)
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);
        var left = root.Left!;    // id=2, sid=10
        var right = root.Right!;  // id=3, sid=20

        // Act — left 제거
        var newRoot = root.RemoveLeaf(left);

        // Assert — surviving sibling이 새 root
        newRoot.Should().BeSameAs(right);       // reference equality: paneId 보존
        newRoot!.Id.Should().Be(3u);
        newRoot.SessionId.Should().Be(20u);
    }

    // ── T-3b (grandparent splice, 핵심 invariant) ─────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveLeaf_GrandparentSplice_PreservesSurvivingSiblingPaneId()
    {
        // Arrange — 3-level 트리
        //            root(id=1)
        //           /          \
        //    branch(id=2)    leafC(id=5, sid=30)
        //       /      \
        //  leafA(3,10) leafB(4,20)
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 99);
        root.Split(SplitOrientation.Vertical, newSessionId: 30, oldLeafId: 2, newLeafId: 5);
        root.Left!.Split(SplitOrientation.Horizontal, newSessionId: 20, oldLeafId: 3, newLeafId: 4);

        var leafA = root.Left!.Left!;   // id=3
        var leafB = root.Left!.Right!;  // id=4 — surviving sibling

        // Act — leafA 제거. 기대: grandparent(root).Left이 branch 대신 leafB로 splice
        var newRoot = root.RemoveLeaf(leafA);

        // Assert — root 자체는 바뀌지 않음
        newRoot.Should().BeSameAs(root);

        // CRITICAL: root.Left이 leafB (정확히 동일한 객체, NOT 복사본)
        root.Left.Should().BeSameAs(leafB);
        root.Left!.Id.Should().Be(4u);           // paneId 보존 — 재할당 없음
        root.Left.SessionId.Should().Be(20u);

        // leafC(Right)는 영향 없음
        root.Right!.Id.Should().Be(5u);
        root.Right.SessionId.Should().Be(30u);
    }

    // ── T-3c (root removal) ─────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void RemoveLeaf_WhenLeafIsRoot_ReturnsNull()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);

        // Act
        var newRoot = root.RemoveLeaf(root);

        // Assert — 빈 트리
        newRoot.Should().BeNull();
    }

    // ── T-4 (DFS 3-level) ───────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void GetLeaves_OnThreeLevelTree_ReturnsDfsOrderLeftFirst()
    {
        // Arrange
        //            root(id=1, sid=99)
        //           /               \
        //       branch(id=2)      leafR(id=5, sid=40)
        //       /       \
        //   leafLL(3,99)  leafLR(4,20)
        //
        // Split 체인 추적:
        //   1) root(sid=99).Split → oldLeaf(id=2, sid=99), newLeaf(id=5, sid=40)
        //   2) branch(sid=99).Split → oldLeaf(id=3, sid=99), newLeaf(id=4, sid=20)
        // 기대 DFS: [id=3, id=4, id=5], [sid=99, sid=20, sid=40]
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 99);
        root.Split(SplitOrientation.Vertical, newSessionId: 40, oldLeafId: 2, newLeafId: 5);
        root.Left!.Split(SplitOrientation.Horizontal, newSessionId: 20, oldLeafId: 3, newLeafId: 4);

        // Act
        var leaves = root.GetLeaves().ToArray();

        // Assert
        leaves.Should().HaveCount(3);
        leaves.Select(n => n.Id).Should().ContainInOrder(3u, 4u, 5u);
        leaves.Select(n => n.SessionId).Should().ContainInOrder(99u, 20u, 40u);
    }

    // ── T-4b (single leaf) ─────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void GetLeaves_OnSingleLeafTree_ReturnsSelfOnly()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);

        // Act
        var leaves = root.GetLeaves().ToArray();

        // Assert
        leaves.Should().HaveCount(1);
        leaves[0].Should().BeSameAs(root);
    }

    // ── T-5a (positive) ────────────────────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void FindLeaf_WithExistingSessionId_ReturnsCorrectLeaf()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);

        // Act
        var found = root.FindLeaf(20);

        // Assert
        found.Should().NotBeNull();
        found!.SessionId.Should().Be(20u);
        found.Id.Should().Be(3u);
    }

    // ── T-5b (negative, Plan 원본 T-5) ─────────────────────────
    [Fact]
    [Trait("Category", "Unit")]
    public void FindLeaf_WithNonExistentSessionId_ReturnsNull()
    {
        // Arrange
        var root = PaneNode.CreateLeaf(id: 1, sessionId: 10);
        root.Split(SplitOrientation.Vertical, newSessionId: 20, oldLeafId: 2, newLeafId: 3);

        // Act
        var found = root.FindLeaf(999);

        // Assert
        found.Should().BeNull();
    }
}
```

### 4.4 `scripts/test_ghostwin.ps1`

```powershell
#Requires -Version 5.1
<#
.SYNOPSIS
    GhostWin xUnit test runner (pure .NET, no C++ build required).

.DESCRIPTION
    Runs `dotnet test` for tests/GhostWin.Core.Tests. Does NOT invoke vcvarsall.

.PARAMETER Configuration
    Debug (default) or Release.

.PARAMETER PassThru
    Additional arguments forwarded to `dotnet test`.

.EXAMPLE
    scripts/test_ghostwin.ps1
    scripts/test_ghostwin.ps1 -Configuration Release
    scripts/test_ghostwin.ps1 -- --filter "FullyQualifiedName~RemoveLeaf"
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$PassThru = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Korean Windows: force UTF-8 + English CLI to avoid CP949 mojibake
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$testProject = Join-Path $root 'tests\GhostWin.Core.Tests\GhostWin.Core.Tests.csproj'

if (-not (Test-Path $testProject)) {
    Write-Error "Test project not found: $testProject"
    exit 1
}

Write-Host '[1/2] Restoring packages...' -ForegroundColor Cyan
& dotnet restore $testProject
if ($LASTEXITCODE -ne 0) {
    Write-Error 'dotnet restore failed'
    exit 1
}

Write-Host "[2/2] Running tests (Configuration=$Configuration)..." -ForegroundColor Cyan
$testArgs = @($testProject, '-c', $Configuration, '--no-restore') + $PassThru
& dotnet test @testArgs

$exitCode = $LASTEXITCODE
if ($exitCode -eq 0) {
    Write-Host 'ALL TESTS PASSED' -ForegroundColor Green
} else {
    Write-Host "Tests FAILED (exit code: $exitCode)" -ForegroundColor Red
}

exit $exitCode
```

---

## 5. Implementation Order (for Do phase)

Do phase는 **strict 순서대로 진행**. 각 단계가 다음의 전제 조건.

1. **디렉토리 생성**: `mkdir tests/GhostWin.Core.Tests/Models`
2. **csproj 작성**: `tests/GhostWin.Core.Tests/GhostWin.Core.Tests.csproj` (§4.1 XML 그대로)
3. **PaneNodeTests.cs 작성**: `tests/GhostWin.Core.Tests/Models/PaneNodeTests.cs` (§4.3 pseudocode → 실제 C#)
4. **PS1 스크립트 작성**: `scripts/test_ghostwin.ps1` (§4.4 그대로)
5. **로컬 실행 검증**: `powershell -ExecutionPolicy Bypass -File scripts/test_ghostwin.ps1` → **9/9 PASS** 기대
6. **`.gitignore` 확인**: `tests/**/bin/`, `tests/**/obj/` 패턴이 이미 커버되는지 확인 (dotnet-expert 보고: 기존 `**/bin/`, `**/obj/`로 자동 커버) → 변경 없음 예상
7. **CLAUDE.md TODO 갱신**: "Phase 5-E 잔여 품질 항목"에서 테스트 관련 항목 상태 업데이트

### 5.1 중단 조건

다음 중 하나라도 발생하면 Do 중단 + 원인 분석:

- `dotnet restore` 실패 → 패키지 버전 호환성 이슈 (D1~D4 재검토)
- `dotnet build` 실패 → csproj 구성 또는 TargetFramework 불일치
- 테스트 빌드 성공 but 1개 이상 테스트 Red → **SUT 이해 오류**. SUT 수정이 아니라 테스트 assertion을 재검토. .claude/rules/behavior.md "우회 금지" 원칙 준수
- 의심 사항 있을 시 qa-strategist council input 재확인

### 5.2 SUT 수정 금지 원칙 (code-analyzer 강조)

Do phase에서 `src/GhostWin.Core/Models/PaneNode.cs` **절대 수정 금지**. 
만약 테스트가 실패한다면:
1. 먼저 테스트 assertion을 검토 (오류 가능성)
2. SUT 실제 동작을 Read로 재확인
3. Plan/Design의 가정을 재검토
4. 그래도 버그로 확정되면 **별도 feature로 분리** (`pane-node-fix-N.plan.md`)

이 원칙은 Plan §4.1 DoD와 일치.

---

## 6. Verification Plan (이 feature를 어떻게 검증할지)

| 검증 항목 | 방법 | 합격 기준 |
|-----------|------|-----------|
| csproj 구조 | `dotnet restore tests/GhostWin.Core.Tests/` | exit 0, 경고 0 |
| 빌드 | `dotnet build tests/GhostWin.Core.Tests/ -c Debug` | exit 0, 경고 0 |
| 테스트 | `scripts/test_ghostwin.ps1` | 9/9 Green |
| 실행 시간 | `Measure-Command { scripts/test_ghostwin.ps1 }` | < 30초 (cold start), < 10초 (warm) |
| 결정론 | 10회 연속 실행 | 동일 결과 (flaky 0) |
| SUT 비변경 | `git diff src/GhostWin.Core/Models/PaneNode.cs` | empty |
| 기존 빌드 회귀 | `scripts/build_ghostwin.ps1` | 기존과 동일 동작 |
| 기존 WPF 빌드 회귀 | `scripts/build_wpf_poc.ps1` | 기존과 동일 동작 |
| Gap Analysis | `/pdca analyze core-tests-bootstrap` (gap-detector 에이전트) | Match Rate ≥ 90% |

---

## 7. Non-Functional Requirements

| # | 범주 | 기준 | 측정 |
|---|------|------|------|
| NFR-01 | 실행 시간 | 전체 9개 테스트 warm 실행 < 10초 | `Measure-Command` |
| NFR-02 | 외부 의존 | 파일시스템/네트워크/엔진 DLL 접근 0 | 코드 리뷰 + `ProjectReference` 확인 |
| NFR-03 | 결정론 | flaky 0% (10회 연속 동일 결과) | 로컬 `for` 루프 |
| NFR-04 | 가독성 | AAA 3블록 분리, 의도 명확한 메서드명 | 코드 리뷰 |
| NFR-05 | 라이선스 클린 | Apache-2.0/MIT/BSD만 사용 | 패키지 라이선스 확인 |
| NFR-06 | 한국어 Windows 호환 | UTF-8 콘솔 출력 깨짐 없음 | 수동 확인 |

---

## 8. Risks and Mitigation

| # | Risk | Impact | Likelihood | Mitigation |
|---|------|--------|:---:|------------|
| R1 | `Microsoft.NET.Test.Sdk 18.4.0`의 `net10.0` 공식 지원 미확인 | Medium | Medium | nuspec에 `net8.0` TFM group 확인됨, `net10.0`에서 폴백 동작 기대. 실패 시 Test.Sdk 버전 downgrade 또는 Core TFM을 `net8.0`으로 임시 통일 — **확실하지 않음**, Do 단계에서 실측 |
| R2 | FluentAssertions 7.0.0 + .NET 10 호환성 | Low | Low | v7.0.0은 Nov 2024 릴리즈로 netstandard2.0 기반, 모든 .NET 버전 호환. 실행 확인 후 이슈 시 6.12.2로 downgrade |
| R3 | `uint` 리터럴 타입 엄격성 (FluentAssertions가 `int` 리터럴 거부) | Low | Medium | 모든 `Should().Be(x)` 호출에 `u` suffix 명시 (§4.3 pseudocode 이미 반영) |
| R4 | `CommunityToolkit.Mvvm 8.*`이 `ProjectReference`로 transitively 포함 | Low | High | 의도된 동작. PaneNode는 MVVM 비의존이므로 테스트에서 사용 안 함. restore 시간만 약간 증가 |
| R5 | 한국어 Windows 콘솔 `dotnet test` 출력 깨짐 | Medium | High | PS1 스크립트에 `DOTNET_CLI_UI_LANGUAGE=en` + UTF-8 OutputEncoding 적용 (D14) |
| R6 | Do phase에서 테스트 실패 시 SUT 수정 유혹 | Medium | Low | §5.2 "SUT 수정 금지" 원칙 + .claude/rules/behavior.md 우회 금지 |
| R7 | T-3b grandparent splice 트리 구성의 `SessionId` 전파 추적 오류 | Medium | Medium | qa-strategist 권고대로 각 Arrange 블록에 트리 다이어그램 주석 유지. §4.3 이미 반영 |
| R8 | FluentAssertions 7.0.0이 NuGet에서 yanked/제거될 가능성 | Low | Very Low | 공식 deprecation 공지 없음 (추측). 만일 발생 시 Shouldly 4.3.0으로 스왑 (대안 이미 연구됨) |

---

## 9. Open Questions / 확실하지 않음

1. **`.sln` 파일 생성을 chore로 후속 처리할지 여부** — 본 feature 외부. 현재 결정: 하지 않음.
2. **`Directory.Build.props`** 도입 시점 — 2번째 테스트 프로젝트 추가 시점으로 deferred.
3. **coverlet code coverage** 도입 — 후속 feature.
4. **CI 파이프라인** (GitHub Actions) — 레포에 CI 파일 부재, 별도 feature.

---

## 10. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-07 | Initial design. Council synthesis from dotnet-expert + qa-strategist + code-analyzer. FluentAssertions 7.0.0 확정 (사용자 결정) | 노수장 (CTO Lead) |

---

## Council Attribution

| Agent | Contribution |
|-------|-------------|
| `rkit:dotnet-expert` | csproj XML, 버전 핀 (xUnit 2.9.3, Test.Sdk 18.4.0, Runner 3.1.5), FluentAssertions 라이선스 조사 (v8+ Xceed 상용 확인), PS1 스크립트 전체, `.sln` 생략 권고 |
| `rkit:qa-strategist` | 테스트 네이밍 컨벤션, AAA 템플릿, T-1~T-5 전체 pseudocode, T-3b grandparent splice 3-level 트리 설계 (paneId 보존 invariant), FluentAssertions 이디엄 권고, `BeSameAs` vs `Be` 구분 |
| `rkit:code-analyzer` | `PaneNode.cs` / `IReadOnlyPaneNode.cs` 전수 분석, public API 인벤토리, 7개 invariant 도출, E1/E2/E3/E5/E8 edge case 식별, SUT 수정 불필요 확정, 허용/금지 import 목록 |
| CTO Lead (Opus) | Council 충돌 조정 (FluentAssertions vs Shouldly → 사용자 결정), Design 문서 통합, Implementation Order, NFR, Risk 매트릭스 |
