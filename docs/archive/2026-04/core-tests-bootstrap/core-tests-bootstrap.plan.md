# Core Tests Bootstrap — Planning Document

> **Summary**: GhostWin.Core 대상 xUnit 단위 테스트 프로젝트 신설. PaneNode T-1~T-5 (pane-split design §11) 구현으로 pane-split 부채 청산의 안전망 확보.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 — 부채 청산 P0-1
> **Author**: 노수장
> **Date**: 2026-04-07
> **Status**: Draft
> **Previous**: `docs/03-analysis/pane-split-workspace-completeness-v0.5.md` (10-agent consensus — qa-strategist F, 자동화 테스트 커버리지 7%)

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 솔루션 전체에 `*.Tests.csproj` **0건**. `PaneNode.RemoveLeaf` grandparent splice 등 순수 로직조차 회귀 방어 수단이 없음. 이번 세션 `3abd213` fix에서 동일 세션에 **5건의 critical bug**가 동시 발견된 사실 자체가 테스트 부재의 비용을 정량으로 보여줌 (10-agent 평가 §1 C3 인용). |
| **Solution** | `tests/GhostWin.Core.Tests` 신설 (xUnit + FluentAssertions). pane-split design §11 T-1~T-5 단위 테스트 5건을 **PaneNode 순수 로직만** 대상으로 구현. Moq/WPF/HwndHost 의존 0, 외부 프로세스 0. `dotnet test` 단일 명령으로 초록불/빨간불 판정. |
| **Function/UX Effect** | 사용자 가시 기능 변경 없음. 개발자 관점: Phase 5-E.5 후속 작업(BISECT 종료, 종료 경로 단일화, PropertyChanged detach)의 회귀를 감지할 수 있는 안전망 확보. Phase 5-F session-restore 시 pane-split 관련 회귀를 CI/로컬에서 10초 내 판정. |
| **Core Value** | 테스트 인프라는 **한 번 세팅하면 반복 투자 수익**이 나는 자본재. 가장 ROI가 높은 순수 로직부터 착수해 "테스트 프로젝트가 있다"는 사실 자체로 후속 T-6~T-11 (Moq 기반 Service 통합 테스트) 진입 장벽을 제거. |

---

## 1. Overview

### 1.1 Purpose

GhostWin 솔루션에 **단위 테스트 인프라 자체가 부재**한 상태를 해소한다. 이번 feature의 목표는 "테스트를 많이 쓰는 것"이 아니라 "테스트를 쓸 수 있는 상태로 만드는 것"이다.

첫 대상은 `GhostWin.Core.Models.PaneNode` — 순수 immutable-style 트리 자료구조이므로 외부 의존(WPF, HwndHost, Dispatcher, 엔진 DLL)이 0이다. 같은 이유로 10-agent 평가에서 "ROI 최고"로 합의된 지점이다 (qa-strategist, code-analyzer, cto-lead, enterprise-expert 동의).

### 1.2 Background

**평가 근거** (`docs/03-analysis/pane-split-workspace-completeness-v0.5.md`):

- §Executive Summary 6차원 중 **자동화 테스트 커버리지 7% (qa-strategist F)**
- §1 C3 Critical Issue: "자동화 테스트 부재"
  - `*.Tests.csproj` 0건
  - design §11 Test Plan T-1~T-14 전부 계획만, 구현 없음
  - `PaneNode.RemoveLeaf` grandparent splice가 순수 함수임에도 단위 테스트 없음
  - 이번 세션의 10건 crash fix도 regression test 없음
- §4 Top 10 Recommendations P0 #1: "테스트 인프라 부트스트랩"
- §5 Roadmap Phase 5-E.5 첫 번째 항목

**설계 문서 근거** (`docs/02-design/features/pane-split.design.md` §11 Test Plan):

| # | 시나리오 | 기대 결과 |
|---|---------|----------|
| T-1 | CreateLeaf → Split(Vertical) | branch(Left=oldLeaf, Right=newLeaf), (old,new) 반환 |
| T-2 | Split on branch node | throw InvalidOperationException |
| T-3 | RemoveLeaf (2-pane → 1-pane) | 부모가 surviving child로 대체 |
| T-4 | GetLeaves on 3-level tree | 올바른 DFS 순서 |
| T-5 | FindLeaf(존재하지 않는 sessionId) | null 반환 |

이 5건은 이미 **설계 문서가 스펙을 고정한 상태**이므로 Plan에서 요구사항을 새로 발굴할 필요 없이 바로 구현 가능.

### 1.3 Related Documents

- `docs/02-design/features/pane-split.design.md` §11 Test Plan (스펙 출처)
- `docs/03-analysis/pane-split-workspace-completeness-v0.5.md` §1 C3, §4 P0 #1, §5 Roadmap
- `src/GhostWin.Core/Models/PaneNode.cs` (SUT — System Under Test)
- `CLAUDE.md` — TODO "Phase 5-E 잔여 품질 항목"

---

## 2. Scope

### 2.1 In Scope

- [ ] `tests/` 솔루션 폴더 신설 (루트 `tests/` 디렉토리)
- [ ] `tests/GhostWin.Core.Tests/GhostWin.Core.Tests.csproj` 신설
  - TargetFramework: `net10.0` (WPF 의존 없음 → `-windows` 불필요)
  - Package: `xunit` 최신 stable, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `FluentAssertions`
  - ProjectReference: `../../src/GhostWin.Core/GhostWin.Core.csproj`
- [ ] `GhostWin.sln` 또는 기존 솔루션 파일에 테스트 프로젝트 추가 (솔루션 파일 존재 여부는 Design 단계에서 확인)
- [ ] `tests/GhostWin.Core.Tests/Models/PaneNodeTests.cs` — T-1~T-5 구현
- [ ] `scripts/test_ghostwin.ps1` 신설 — `dotnet test` 래퍼 (`build_ghostwin.ps1` 스타일 일관)
- [ ] 모든 테스트 초록 확인 (local PASS)
- [ ] CLAUDE.md "TODO — Phase 5-E 잔여 품질 항목"의 테스트 관련 항목 갱신

### 2.2 Out of Scope

- **T-6~T-11 (PaneLayoutService, WorkspaceService 통합 테스트)**: Moq 도입 및 `IEngineService`/`ISessionManager` 등 의존성 셋업 필요 → 후속 feature로 분리
- **T-12~T-14 (C++ stress/race 테스트)**: `surface_manager_race_test.cpp` ASAN/TSAN. 별도 feature
- **FlaUI 기반 WPF UI 자동화** (qa-strategist Top 3 추가 권고): `MainWindow` 같은 HwndHost 화면은 별도 feature
- **CI 파이프라인 구성** (GitHub Actions / Azure DevOps): 현재 레포에 CI 파일 부재, 이 feature에서 신설하지 않음. Design 단계에서 로컬 실행만 스펙함
- **Code coverage 측정** (coverlet): 첫 번째 통과가 목표, 커버리지 측정 도구 추가는 후속
- **Zero Script QA JSON 로깅 전환**: `ghostwin-crash.log` 구조화는 Phase 6-B CrashLog 강화 feature로 분리

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status |
|----|-------------|----------|--------|
| FR-01 | `tests/GhostWin.Core.Tests` xUnit 프로젝트 신설, `GhostWin.Core.csproj`만 참조 | High | Pending |
| FR-02 | T-1: `CreateLeaf` → `Split(Vertical, ...)` 호출 시 반환 tuple 및 트리 구조 검증 | High | Pending |
| FR-03 | T-2: branch 노드에 `Split()` 호출 시 `InvalidOperationException` | High | Pending |
| FR-04 | T-3: 2-pane 트리에서 `RemoveLeaf` 후 부모가 surviving child로 대체 — **paneId 보존 invariant** 포함 | High | Pending |
| FR-05 | T-4: 3-level 트리 `GetLeaves()` DFS 순서 검증 (left 먼저, right 나중) | High | Pending |
| FR-06 | T-5: 존재하지 않는 sessionId로 `FindLeaf` 호출 시 `null` | High | Pending |
| FR-07 | `scripts/test_ghostwin.ps1` — `dotnet test` 래퍼, `build_ghostwin.ps1` 스타일 환경 설정 포함 | Medium | Pending |
| FR-08 | 솔루션 파일에 테스트 프로젝트 추가 (솔루션 파일 존재 시) 또는 standalone 실행 경로 문서화 | Medium | Pending |
| FR-09 | 테스트 프로젝트 `bin/obj` `.gitignore` 확인 | Low | Pending |

### 3.2 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| 실행 시간 | `dotnet test` 전체 실행 < 10초 (JIT warm-up 포함) | `Measure-Command { dotnet test }` |
| 외부 의존 | WPF/HwndHost/엔진 DLL/파일시스템 의존 0 | 코드 리뷰 + `csproj` 참조 확인 |
| 결정론 | 동일 입력 시 100회 반복 동일 결과 (flaky 0%) | CI 없음 → 로컬 `for ($i=0;$i -lt 10;$i++) { dotnet test }` |
| 가독성 | 각 테스트 메서드가 Arrange/Act/Assert 3블록으로 분리, 메서드명이 테스트 의도 설명 | 코드 리뷰 |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] `tests/GhostWin.Core.Tests.csproj` 존재, `dotnet restore` 성공
- [ ] T-1~T-5 5개 테스트 모두 Green
- [ ] `scripts/test_ghostwin.ps1` 단독 실행 시 테스트 PASS 출력
- [ ] 기존 `scripts/build_ghostwin.ps1` 영향 없음 (빌드 회귀 0)
- [ ] `PaneNode.cs` 수정 없음 (테스트만 추가, SUT 비변경 원칙)
- [ ] CLAUDE.md TODO에서 "Phase 5-E 잔여 품질 항목" 중 테스트 관련 상태 업데이트

### 4.2 Quality Criteria

- [ ] Gap Analysis Match Rate ≥ 90% (Design ↔ 구현)
- [ ] 테스트 코드 자체에 lint 에러 0
- [ ] FluentAssertions 사용 (정합성: 다른 테스트 라이브러리 혼용 금지)
- [ ] 테스트 메서드명 컨벤션 `MethodName_Scenario_ExpectedResult` (.NET 권고)

---

## 5. Risks and Mitigation

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| xUnit 최신 버전과 .NET 10 Preview 호환성 문제 | Medium | Medium | Design 단계에서 정확한 버전 핀 결정. 필요 시 .NET 9 LTS로 target 조정 |
| 솔루션 파일(`.sln`) 존재 여부 불명 — 루트 레벨 solution 없이 개별 csproj만 사용 중 (`ls *.sln` 결과 `external/wt-ref/Scratch.sln`만 확인됨) | Medium | High | Design 단계에서 확인. 솔루션 부재 시 standalone csproj + PS1 스크립트로 진행 |
| `build_ghostwin.ps1`이 C++ CMake 중심 → .NET 테스트 스크립트와 역할 분리 필요 | Low | Medium | `test_ghostwin.ps1`는 별도 스크립트로 신설, 기존 스크립트 수정 0 |
| `PaneNode.RemoveLeaf` 테스트 시 static state 없는지 검증 필요 (design v0.3에서 `_nextId` 제거됨) | Low | Low | 현재 `PaneNode.cs` 확인 — static state 없음 ✅ |
| 테스트 추가가 "새 기능"으로 오인되어 pane-split 동작에 영향을 주는 방향으로 확장될 위험 (scope creep) | Medium | Low | Out of Scope 엄수. SUT 코드 수정 금지 원칙 |
| "확실하지 않음": .NET 10 `net10.0-windows` vs `net10.0` target 분리가 WPF 프로젝트 참조와 충돌할 가능성 | Low | Low | Core 프로젝트는 이미 WPF 비의존(`GhostWin.Core.csproj` 확인 필요), Design 단계 확인 |

---

## 6. Architecture Considerations

### 6.1 Project Level

본 프로젝트는 rkit Enterprise 수준 데스크톱 앱(WPF + 네이티브 Engine DLL, Clean Architecture 4-project). 테스트 프로젝트도 같은 솔루션 구조를 따르되, `tests/` 루트 분리로 프로덕션 아티팩트와 명확히 구분한다.

```
ghostwin/
├── src/
│   ├── GhostWin.Core/          ← SUT
│   ├── GhostWin.Interop/
│   ├── GhostWin.Services/
│   └── GhostWin.App/
└── tests/
    └── GhostWin.Core.Tests/    ← 본 feature
        ├── GhostWin.Core.Tests.csproj
        └── Models/
            └── PaneNodeTests.cs
```

### 6.2 Key Architectural Decisions

| Decision | Options | Selected | Rationale |
|----------|---------|----------|-----------|
| 테스트 프레임워크 | xUnit / NUnit / MSTest | **xUnit** | 10-agent 평가 qa-strategist 권고. .NET 표준 de facto, `[Fact]`/`[Theory]` 간결 |
| Assertion 라이브러리 | FluentAssertions / Shouldly / xUnit built-in | **FluentAssertions** | 가독성 최상, grandparent splice 같은 구조 검증에 `BeEquivalentTo()` 유용 |
| Mocking | Moq / NSubstitute / 없음 | **없음** (이 feature에서) | PaneNode는 외부 의존 0. Mock 도입 자체가 scope creep. T-6 이후 도입 |
| 폴더 구조 | tests/ 루트 / src/ 하위 `*.Tests` / 프로젝트별 Tests 폴더 | **tests/ 루트 분리** | .NET 커뮤니티 표준, `dotnet test tests/` 명령 단순 |
| 테스트 target framework | `net10.0` / `net10.0-windows` | **`net10.0`** | PaneNode는 WPF 비의존. windows target은 불필요한 결합 |
| 실행 스크립트 | dotnet test 직접 / PS1 래퍼 | **PS1 래퍼** | `.claude/rules/behavior.md` "PowerShell 우선" 규칙 + 기존 `build_*.ps1` 패턴 일관성 |

### 6.3 Clean Architecture 정합성

```
Dependency Direction (그대로 유지):
GhostWin.App → Services → Interop → Core
                                       ↑
                       GhostWin.Core.Tests
                       (참조 Core만, 역방향 의존 없음)
```

`GhostWin.Core.Tests`는 `Core`만 참조한다. Services/Interop/App은 참조하지 않는다 (T-6~T-11은 별도 `GhostWin.Services.Tests` 프로젝트에서 처리). 이는 Clean Architecture의 "바깥쪽 계층이 안쪽을 참조한다" 원칙을 테스트 프로젝트에도 그대로 적용.

---

## 7. Convention Prerequisites

### 7.1 Existing Project Conventions

- [x] `CLAUDE.md` 존재 + `.claude/rules/*.md` 분리 로드됨
- [x] `.claude/rules/behavior.md` — PowerShell 스크립트 우선, 우회 금지
- [x] `.claude/rules/commit.md` — 커밋 메시지 영문, AI 언급 금지
- [x] `.claude/rules/build-environment.md` — 빌드 스크립트 강제
- [ ] `.editorconfig` — 존재 여부 Design 단계에서 확인
- [ ] C# 코드 스타일(.NET 기본 or StyleCop) — Design 단계에서 확인

### 7.2 Conventions to Define/Verify

| Category | Current State | To Define | Priority |
|----------|---------------|-----------|:--------:|
| 테스트 메서드 네이밍 | 없음 | `MethodName_Scenario_ExpectedResult` 영문 | High |
| 테스트 파일 위치 | 없음 | SUT와 동일 네임스페이스 트리 미러 (`Core/Models/PaneNode.cs` ↔ `Core.Tests/Models/PaneNodeTests.cs`) | High |
| Arrange/Act/Assert 블록 | 없음 | 빈 줄로 3블록 분리 | Medium |
| 테스트 카테고리 (Trait) | 없음 | 본 feature는 `[Trait("Category", "Unit")]` 하나만 | Low |

### 7.3 Environment Variables Needed

없음 — 테스트는 외부 의존 0.

### 7.4 Build Script Integration

| 스크립트 | 역할 | 신설/수정 |
|---------|------|:--------:|
| `scripts/build_ghostwin.ps1` | C++ 엔진 + WPF 빌드 | 수정 없음 |
| `scripts/build_libghostty.ps1` | libghostty-vt 빌드 | 수정 없음 |
| `scripts/test_ghostwin.ps1` | `dotnet test tests/GhostWin.Core.Tests` | **신설** |

---

## 8. Next Steps

1. [ ] Design 문서 작성 — `docs/02-design/features/core-tests-bootstrap.design.md`
   - csproj 정확한 패키지 버전 핀
   - 솔루션 파일 존재 여부 확인 및 통합 방식 결정
   - `test_ghostwin.ps1` 상세 스크립트 구조
   - T-1~T-5 각 테스트의 Arrange/Act/Assert 의사코드
2. [ ] Do: 구현 + local 초록 확인
3. [ ] Check: gap-detector로 Design ↔ 구현 Match Rate
4. [ ] Report: 완료 보고서, CLAUDE.md TODO 갱신
5. [ ] **다음 feature 진입 준비**: Phase 5-E.5 P0-2 (BISECT mode 종료) 시 이 테스트 스위트를 회귀 방어 수단으로 사용

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-07 | Initial draft. pane-split v0.5 10-agent 평가 기반 Phase 5-E.5 P0-1 | 노수장 |
