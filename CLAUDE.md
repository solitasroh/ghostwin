# GhostWin Terminal — Project Rules

## 📚 Primary Knowledge Base: Obsidian Vault

**항상 먼저 참조**: `C:\Users\Solit\obsidian\note\Projects\GhostWin\`

| 범주 | 경로 | 내용 |
|------|------|------|
| 진입점 (MOC) | `_index.md` | 프로젝트 전체 지식맵 + 타임라인 |
| Architecture | `Architecture/` | 4-프로젝트 구조, DX11, ConPTY, WPF Shell, Engine Interop |
| Phases | `Phases/` | Phase 1~5 히스토리 + 설계 vs 구현 검토 결과 |
| Milestones | `Milestones/` | WPF M-1~M-14 + Codebase Review 2026-04 |
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
- **빌드**: VS GUI (Ctrl+Shift+B) 또는 `msbuild GhostWin.sln /p:Configuration=Debug /p:Platform=x64`
- **디버깅**: F5 (Mixed-mode, C# + C++ 동시 브레이크포인트)
- **libghostty-vt**: 첫 빌드 시 자동 실행 (`scripts/build_libghostty.ps1`)

상세: [[Architecture/4-project-structure]] (Obsidian)

## 프로젝트 현재 상태

- **Git 브랜치**: `feature/wpf-migration`
- **최신 마일스톤**: **M-14 Render Thread Safety & Baseline Recovery 완료 (archived)** — 82% Match Rate (Fallback Path, 완료 게이트 5개 중 4개 통과). `shared_mutex + FrameReadGuard` reader 안전 계약 + `SessionVisualState` snapshot-atomic + `force_all_dirty()` 제거 → **idle 렌더 1,643→4 frame (−99.76%)**. 20/20 테스트 PASS. 2026-04-20~23 (3 days, 21 commits).
- **확정 실행 순서** (2026-04-23): ~~M-11~~ ✅ → ~~M-11.5~~ ✅ → ~~Phase 6-A~~ ✅ → ~~Phase 6-B~~ ✅ → ~~Phase 6-C~~ ✅ → ~~M-12~~ ✅ → ~~M-13 Input UX~~ ✅ → ~~M-14 렌더 스레드 안전성~~ ✅ → **M-15 Render Baseline Comparison** (measurement follow-up — 4-pane 자동 CSV, WT/WezTerm/Alacritty 비교, load 자동화, idle CPU 절대값)
- **🎯 이 프로젝트의 존재 이유**: Windows 용 **AI 에이전트 멀티플렉서** (cmux + ghostty 성능). Phase 6 완료로 핵심 비전 실증됨. M-14 로 성능 기준선도 정량 확보.

상세 진행 상황은 Obsidian `_index.md` 타임라인 + `Milestones/` 참조.
비전 정의: `onboarding.md` (프로젝트 루트) + Obsidian `_index.md` 3대 비전 표.

## ghostty 서브모듈

- **Fork**: `solitasroh/ghostty` (private) — 팀 내부 유지, upstream PR 미예정
- **Pinned branch**: `ghostwin-patches/v1`
- **Current SHA**: `4f658b4ad` (upstream `debcffbad` 위 +1 commit)
- `.gitmodules` URL: `https://github.com/solitasroh/ghostty.git`
- **로컬 패치 (fork branch 안에 영구 보존)**:
  - OPT 15: `GHOSTTY_TERMINAL_OPT_DESKTOP_NOTIFICATION` (Phase 6-A 토스트 파이프라인용)
  - OPT 16: `GHOSTTY_TERMINAL_OPT_MOUSE_SHAPE` (M-13 FR-02 마우스 커서용)
  - 핵심 파일: `include/ghostty/vt/terminal.h` (+59) + `src/terminal/c/terminal.zig` (+40) + `src/terminal/stream_terminal.zig` (+22) + `src/build/gtk.zig` (+5)
  - `.gitignore` 에 `msvc_libc.txt` 추가 (per-machine zig 빌드 캐시 제외)
- **팀원 onboarding**: `git clone --recursive https://github.com/solitasroh/ghostwin.git` 한 줄 — patched ghostty 자동 checkout, patch apply 단계 불필요
- **upstream 동기화 (필요 시)**: `cd external/ghostty && git fetch origin && git rebase origin/main && git push fork ghostwin-patches/v1 --force-with-lease`
- 상세: `docs/00-research/ghostty-upstream-sync-analysis.md`

## PDCA Archive

- **인덱스**: `docs/archive/2026-04/_INDEX.md` (33 사이클, M-14 포함) + `docs/archive/legacy/_INDEX.md` (3 폴더)
- **활성 참조 자료**: `docs/03-analysis/concurrency/` (M-14 이전 pane-split concurrency 분석 원본), `docs/04-report/changelog.md`
- **M-14 artifacts**: `docs/archive/2026-04/m14-render-thread-safety/` (PRD/Plan/Design v1.1/Analysis/Report + `baselines/` 하위 W1/W3/W4 분석 3건 + raw CSV 3개). M-15 는 이 baselines 를 before 기준으로 직접 참조
- 그 외 `docs/{00-pm, 01-plan/features, 02-design/features, 04-report/features}` 는 모두 비어 있음 (완료 사이클 archive 됨)
- 새 PDCA 사이클: `/pdca pm {feature}` → `/pdca plan` → `/pdca design` → `/pdca do` → `/pdca analyze` → `/pdca report` → `/pdca archive --summary`
