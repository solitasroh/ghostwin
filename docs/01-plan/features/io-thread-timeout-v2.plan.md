# io-thread-timeout-v2 — Sub-cycle Plan Placeholder

> **Parent milestone**: [[Milestones/pre-m11-backlog-cleanup]] Group 4 #11
> **Status**: Placeholder (Plan 상세 미착수)
> **Triggered by**: Tech Debt #6 (2026-04-13 Phase 2 검토) — 이전 `std::async` 접근 **되돌림** (UB 발생)
> **Date**: 2026-04-14

## 배경

- `conpty_session.cpp:239-241` 소멸자의 `io_thread.join()` 에 타임아웃 없음
- 극단적 시나리오 (`hpc.reset()` 후 `ReadFile` 비반환) 에서 소멸자 영구 블록 가능
- 이전 `std::async + wait_for` 시도: **std::async 객체의 소멸이 detach 를 보장하지 않음** → UB
- 실제 hang 은 드물지만 graceful shutdown 안정성에 직결

## Executive Summary (계획 단계)

| Perspective | Content |
|-------------|---------|
| **Problem** | `io_thread.join()` 타임아웃 부재. std::async fallback 은 UB. 드물지만 종료 시 hang 가능 |
| **Solution** | 후보 A: `std::jthread` + `std::stop_token` 으로 협조적 종료. B: IOCP + `CancelIoEx` 로 ReadFile 강제 취소. C: 워치독 스레드 + detach + 경고 로그. Design 에서 결정 |
| **Function/UX Effect** | 앱 종료 hang 제거 — 사용자 체감 (앱 안 닫힘 문제) |
| **Core Value** | Graceful shutdown 의 마지막 퍼즐. ADR 신규 가능 |

## 진입 조건

- [ ] Graceful shutdown 3a28730 (Phase 5 review) 상태 이해
- [ ] 이전 `std::async` 시도의 UB 원인 정확히 식별 (detached future? destructor block?)
- [ ] Windows IOCP + overlapped I/O 마이그레이션 대안 평가

## 예상 범위

- Plan: ~200 LOC (후보 3건 trade-off 분석)
- Design: ~400 LOC (선택된 접근의 구조 + shutdown 시퀀스 업데이트)
- Do: `conpty_session.cpp` 중심, `hpc.reset()` 호출 시점 재구성
- Check: shutdown scenario repro test (kill signal / window close / app exit)

## 관련 문서

- [[Architecture/conpty-integration]]
- [[Backlog/tech-debt]] #6
- 참고: Phase 5 graceful shutdown (commit 3a28730)
