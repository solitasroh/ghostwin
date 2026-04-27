# propertychanged-detach Planning Document

> **Summary**: WorkspaceService.CreateWorkspace의 익명 람다 PropertyChanged 구독을 CloseWorkspace에서 해제
>
> **Project**: GhostWin Terminal
> **Author**: Claude + 노수장
> **Date**: 2026-04-10
> **Status**: Draft

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | `WorkspaceService.CreateWorkspace`에서 `sessionInfo.PropertyChanged`에 익명 람다를 구독하지만, `CloseWorkspace`에서 해제하지 않아 SessionInfo → WorkspaceInfo 참조가 GC되지 않음 |
| **Solution** | 익명 람다를 named handler로 변환하고 `WorkspaceEntry`에 저장, `CloseWorkspace`에서 `-=` 해제 |
| **Function/UX Effect** | Workspace 닫기 후 메모리 누수 방지. 장시간 사용 시 안정성 향상 |
| **Core Value** | P0-4 마지막 기술 부채 해소 — P0 항목 전체 완료 |

---

## 1. Overview

### 1.1 Purpose

`WorkspaceService.cs:66` 에서 `sessionInfo.PropertyChanged += (_, e) => { ... }` 익명 람다 구독. `CloseWorkspace`에서 `_entries.Remove` 만 수행하고 구독 해제 없음.

SessionInfo가 SessionManager에 남아있는 한(또는 GC root에서 도달 가능한 한), 해당 람다가 WorkspaceInfo를 캡처하여 메모리 누수 발생.

### 1.2 Background

10-agent v0.5 평가 §4에서 P0-4로 식별. CLAUDE.md에 명시: "`WorkspaceService.cs:62-71` 람다 누수, `CloseWorkspace`에서 unsubscribe".

---

## 2. Scope

### 2.1 In Scope

- [ ] FR-01: 익명 람다를 named handler로 변환
- [ ] FR-02: `WorkspaceEntry`에 handler 참조 저장
- [ ] FR-03: `CloseWorkspace`에서 `PropertyChanged -=` 해제

### 2.2 Out of Scope

- SessionManager 리팩토링 (17 public → SRP) — 별도 기술 부채
- WorkspaceItemViewModel dispose — 이미 MainWindowViewModel에서 처리

---

## 3. Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-01 | 익명 람다 → named handler | High |
| FR-02 | WorkspaceEntry에 handler/sessionInfo 저장 | High |
| FR-03 | CloseWorkspace에서 PropertyChanged -= | High |

---

## 4. Affected Files

| File | Change |
|------|--------|
| `src/GhostWin.Services/WorkspaceService.cs` | CreateWorkspace + WorkspaceEntry + CloseWorkspace |

**1파일, ~10 LOC 변경 예상**

---

## 5. Success Criteria

- [ ] 빌드 성공
- [ ] CloseWorkspace 후 PropertyChanged handler 해제 확인
- [ ] 기존 E2E regression 없음

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-10 | Initial draft | Claude + 노수장 |
