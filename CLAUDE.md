# GhostWin Terminal — Project Rules

## 상세 규칙

빌드/행동 규칙은 `.claude/rules/`에 분리되어 경로별로 자동 로드됨.

| 규칙 파일 | 적�� 범위 |
|-----------|----------|
| `.claude/rules/behavior.md` | 항상 (의존성 대응, 빌드 실패, 스크립트) |
| `.claude/rules/commit.md` | 항상 (���밋 메시지 형식, AI 언급 금지) |
| `.claude/rules/build-environment.md` | CMakeLists.txt, scripts/, external/ghostty/ |

## 아키텍처 결정 (ADR)

| ADR | 결정 | 근거 |
|-----|------|------|
| [001](docs/adr/001-simd-false-gnu-target.md) | windows-gnu + simd=false | CRT 독립 |
| [002](docs/adr/002-c-bridge-pattern.md) | C 브릿지 레이어 | MSVC C++ typedef 충돌 회피 |
| [003](docs/adr/003-dll-dynamic-crt.md) | DLL 방식 유지 | GNU static lib MSVC 링커 COMDAT 불호환 |

## 핵심 참고 문서

| 문서 | 경로 |
|------|------|
| Upstream 동기화 분석 | `docs/00-research/ghostty-upstream-sync-analysis.md` |
| 트러블슈팅 가이드 | `docs/00-research/troubleshooting-windows-build.md` |
| Phase 1 완료 보고서 | `docs/04-report/libghostty-vt-build.report.md` |

## ghostty 서브모듈 상태

- 현재: `562e7048c` — MSVC 호환 패치 3건 적용 중
- **Phase 2 시작 시 upstream 동기화 필수** (#11950 C++ 헤더 호환 수정)
- 동기화 체크리스트: `docs/00-research/ghostty-upstream-sync-analysis.md` Section 6
