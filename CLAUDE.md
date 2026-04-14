# GhostWin Terminal — Project Rules

## 📚 Primary Knowledge Base: Obsidian Vault

**항상 먼저 참조**: `C:\Users\Solit\obsidian\note\Projects\GhostWin\`

| 범주 | 경로 | 내용 |
|------|------|------|
| 진입점 (MOC) | `_index.md` | 프로젝트 전체 지식맵 + 타임라인 |
| Architecture | `Architecture/` | 4-프로젝트 구조, DX11, ConPTY, WPF Shell, Engine Interop |
| Phases | `Phases/` | Phase 1~5 히스토리 + 설계 vs 구현 검토 결과 |
| Milestones | `Milestones/` | WPF M-1~M-13 + Codebase Review 2026-04 |
| ADR | `ADR/` | 아키텍처 결정 13건 (이론, 대안 비교) |
| Backlog | `Backlog/` | 기술부채 현황 + follow-up cycles |

### 활용 원칙

1. **프로젝트 맥락/아키텍처 질문** → Obsidian vault 먼저 읽기
2. **새 기능 구현 시** → 관련 Architecture + ADR 문서 참조
3. **Phase/마일스톤 이력** → Obsidian Phases/ + Milestones/ 참조
4. **잔여 작업 확인** → Backlog/ 참조
5. **구현 완료 후** → Obsidian 문서 업데이트 (새 마일스톤/ADR 추가)
6. **재검토/분석 결과** → 반드시 Obsidian에 반영 (코드와 단일 소스)

## 상세 규칙

빌드/행동 규칙은 `.claude/rules/`에 분리되어 경로별 자동 로드.

| 규칙 파일 | 적용 범위 |
|-----------|-----------|
| `.claude/rules/behavior.md` | 항상 (의존성 대응, 빌드 실패, 스크립트) |
| `.claude/rules/commit.md` | 항상 (커밋 메시지 형식, AI 언급 금지) |
| `.claude/rules/documentation.md` | 항상 (설명/설계/계획/보고 문서 — 쉬운 한국어 + 다이어그램 + 비교표) |
| `.claude/rules/build-environment.md` | GhostWin.sln, *.vcxproj, *.csproj, scripts/, external/ghostty/ |

## 빌드 (2026-04-14 — VS 통합)

- **IDE**: `GhostWin.sln` (VS 18 Insiders, v145 toolset)
- **빌드**: VS GUI (Ctrl+Shift+B) 또는 `dotnet build GhostWin.sln`
- **디버깅**: F5 (Mixed-mode, C# + C++ 동시 브레이크포인트)
- **libghostty-vt**: 첫 빌드 시 자동 실행 (`scripts/build_libghostty.ps1`)

상세: [[Architecture/4-project-structure]] (Obsidian)

## 프로젝트 현재 상태

- **Git 브랜치**: `feature/wpf-migration`
- **최신 마일스톤**: Codebase Review (2026-04-14) 완료
- **다음**: M-11 Session Restore

상세 진행 상황은 Obsidian `_index.md` 타임라인 + `Milestones/` 참조.

## ghostty 서브모듈

- 현재: `debcffbad` — upstream 동기화 완료 (#11950 포함)
- 로컬 패치: 없음 (ADR-001 GNU+simd=false)
- 상세: `docs/00-research/ghostty-upstream-sync-analysis.md`
