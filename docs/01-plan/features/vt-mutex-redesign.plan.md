# vt-mutex-redesign — Sub-cycle Plan Placeholder

> **Parent milestone**: [[Milestones/pre-m11-backlog-cleanup]] Group 4 #10
> **Status**: Placeholder (Plan 상세 미착수)
> **Triggered by**: Tech Debt #1 (2026-04-13 Phase 5 검토) — 이전 수정이 **되돌림** 됨
> **Date**: 2026-04-14

## 배경

- `ADR-006` 에서 도입된 `vt_mutex` 가 **Session 레벨과 ConPty::Impl 레벨에 각각 존재** (이중화)
- 이전 통합 시도: 위임 메서드 방식 → **shutdown race 유발** → 되돌림
- 데드락 위험 + shutdown 안정성 동시 확보 필요

## Executive Summary (계획 단계)

| Perspective | Content |
|-------------|---------|
| **Problem** | Session / ConPty::Impl 두 mutex 가 독립 존재 → 재진입 / 락 순서 복잡. 위임 방식은 shutdown race 로 불가 |
| **Solution** | 후보 A: single-writer 모델 + atomic 상태머신. B: reader-writer lock. C: lock-free 큐 + 단일 consumer thread. Design phase 에서 결정 |
| **Function/UX Effect** | 내부 안정성 — 사용자 체감 없음. Regression 방지 목적 |
| **Core Value** | ADR-006 개정 + 데드락 위험 제거 |

## 진입 조건

- [ ] Pre-M11 Backlog Cleanup Group 1~3, 5 완료 (본 cycle 이 유일한 open blocker 가 될 때)
- [ ] `docs/archive/` 의 이전 vt_mutex 통합 시도 (되돌림 cycle) 원인 재분석
- [ ] Shutdown sequence 현 상태 재검증 (graceful shutdown 4ac96bd 이후)

## 예상 범위

- Plan: ~300 LOC 문서 (대안 3건 평가 + RCA)
- Design: ~500 LOC (ADR-006 개정 초안 포함)
- Do: 중~대 — `conpty_session.cpp`, `session_manager.cpp`, `vt_core.cpp` 영향
- Check/Act: unit test 추가 + concurrency stress test

## 관련 문서

- [[ADR/adr-006-vt-mutex]]
- [[Architecture/conpty-integration]]
- [[Backlog/tech-debt]] #1
- 이전 시도 archive: `docs/archive/` 에서 `vt_mutex` 검색 필요
