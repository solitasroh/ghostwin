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
| [004](docs/adr/004-utf8-source-encoding.md) | MSVC /utf-8 강제 | 한국어 Windows CP949 인코딩 충돌 |
| [005](docs/adr/005-sdk-version-pinning.md) | SDK 22621 버전 고정 | SDK 26100 shared 헤더 누락 |
| [006](docs/adr/006-vt-mutex-thread-safety.md) | vt_mutex 스레드 안전성 | write/resize 경합 방지 (Alacritty 패턴) |
| [007](docs/adr/007-r32-quad-instance-format.md) | R32 QuadInstance (68B) | R16 포맷 CreateInputLayout 타입 불일치 |
| [008](docs/adr/008-two-pass-rendering.md) | 2-Pass 렌더링 (배경→텍스트) | CJK 글리프 클리핑 방지 (4개 터미널 표준) |

## 핵심 참고 문서

| 문서 | 경로 |
|------|------|
| Upstream 동기화 분석 | `docs/00-research/ghostty-upstream-sync-analysis.md` |
| 트러블슈팅 가이드 | `docs/00-research/troubleshooting-windows-build.md` |
| Phase 1 완료 보고서 | `docs/archive/2026-03/libghostty-vt-build/libghostty-vt-build.report.md` |
| Phase 3 완료 보고서 | `docs/04-report/features/dx11-rendering.report.md` |
| Phase 3 Design | `docs/02-design/features/dx11-rendering.design.md` |
| DX11 GPU 렌더링 리서치 | `docs/00-research/research-dx11-gpu-rendering.md` |

## ghostty 서브모듈 상태

- 현재: `debcffbad` — upstream 동기화 완료 (#11950 C++ 헤더 호환 포함)
- 로컬 패치: 없음 (Phase 2에서 3건 제거, ADR-001 GNU+simd=false에서 불필요)
- 동기화 이력: `docs/00-research/ghostty-upstream-sync-analysis.md`
