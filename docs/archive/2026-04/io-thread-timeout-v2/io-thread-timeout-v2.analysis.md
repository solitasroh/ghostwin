# io-thread-timeout-v2 — Gap Analysis

> **Feature**: io-thread-timeout-v2
> **Phase**: Check (PDCA)
> **Date**: 2026-04-15
> **Analyzer**: rkit:gap-detector (opus)
> **Design**: [io-thread-timeout-v2.design.md](../02-design/features/io-thread-timeout-v2.design.md)
> **Plan**: [io-thread-timeout-v2.plan.md](../01-plan/features/io-thread-timeout-v2.plan.md)

---

## Executive Summary

| 항목 | 값 |
|------|:--:|
| **Match Rate** | **100%** (7/7) |
| Gap 수 | **0** |
| 빌드 | ✅ MSBuild 성공 |
| 경고 | ✅ **0 건** |
| 테스트 | ✅ vt_core_test 10/10 |
| 결론 | **Report 진입 권장** |

---

## 1. 요구사항 대조표

| # | Design 요구 | 실제 위치 | 일치 |
|:-:|------------|-----------|:----:|
| 4.1a | `std::async` 교훈 주석 블록 + [futures.async]/5 + cppreference URL | `conpty_session.cpp:360-371` | ✅ |
| 4.1b-1 | `CancelIoEx(output_read, nullptr)` 호출 (null 체크) | `conpty_session.cpp:389` | ✅ |
| 4.1b-2 | `ERROR_NOT_FOUND` 정상 처리 | `:391` | ✅ |
| 4.1b-3 | ghostty + MSDN URL 주석 | `:387-388` | ✅ |
| 4.1c | 단계 번호 1→2→3(신규)→4→5→6 재조정 | 전체 주석 | ✅ |
| 4.2 | 추가 include 불필요 | `<windows.h>` 기존 포함 | ✅ |
| 4.3 | `log_win_error` 재사용 | `:394` | ✅ |

## 2. 코드 품질

| 지표 | 결과 |
|------|:----:|
| null-safety | ✅ `impl_->output_read && !CancelIoEx(...)` 단락 평가 |
| 에러 분기 | ✅ `ERROR_NOT_FOUND` 정상, 그 외만 로그 |
| 기존 동작 보존 | ✅ input_write/hpc/join/output_read/child wait 시퀀스 유지 |
| RAII | ✅ UniqueHandle/UniquePcon 변경 없음 |
| 회귀 | ✅ vt_core_test 10/10 |

## 3. 리스크 실현 (Design §8)

| 리스크 | 실현 | 근거 |
|--------|:----:|------|
| `output_read` 핸들 무효화 | ❌ | null 체크 방어 |
| `CancelIoEx` 미반응 (엣지케이스) | ⚠️ 런타임 미검증 | F5 수동 검증 필요 |
| `ERROR_NOT_FOUND` 외 로그 노이즈 | ⚠️ 런타임 미검증 | F5 수동 관찰 필요 |
| 단계 번호 혼동 | ❌ | 정적 일관성 확인 |

## 4. 결론

**Match Rate 100%**. Design 전 항목 충족, Gap 0.

### 다음 단계

1. ✅ Report phase 진입 (`/pdca report io-thread-timeout-v2`)
2. F5 수동 검증으로 런타임 리스크 2 건 최종 해소 (권장, 선택)
3. Report 후 Obsidian `Backlog/tech-debt.md` #6 + `pre-m11-backlog-cleanup.md` Group 4 #11 완료 표기

---

## 참조

- `src/conpty/conpty_session.cpp:359-418` (구현)
- `docs/02-design/features/io-thread-timeout-v2.design.md` (설계)
- `docs/01-plan/features/io-thread-timeout-v2.plan.md` (계획)
