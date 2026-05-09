# GhostWin Terminal — Project Rules

## 📚 Primary Knowledge Base: Obsidian Vault

**항상 먼저 참조**: `C:\Users\Solit\obsidian\note\Projects\GhostWin\`

| 범주         | 경로            | 내용                                                     |
| ------------ | --------------- | -------------------------------------------------------- |
| 진입점 (MOC) | `_index.md`     | 프로젝트 전체 지식맵 + 타임라인                          |
| Architecture | `Architecture/` | 4-프로젝트 구조, DX11, ConPTY, WPF Shell, Engine Interop |
| Phases       | `Phases/`       | Phase 1~5 히스토리 + 설계 vs 구현 검토 결과              |
| Milestones   | `Milestones/`   | WPF M-1~M-14 + Codebase Review 2026-04                   |
| ADR          | `ADR/`          | 아키텍처 결정 13건 (이론, 대안 비교)                     |
| Backlog      | `Backlog/`      | 기술부채 현황 + follow-up cycles                         |

### 활용 원칙

1. **프로젝트 맥락/아키텍처 질문** → Obsidian vault 먼저 읽기
2. **새 기능 구현 시** → 관련 Architecture + ADR 문서 참조
3. **Phase/마일스톤 이력** → Obsidian Phases/ + Milestones/ 참조
4. **잔여 작업 확인** → Backlog/ 참조
5. **구현 완료 후** → Obsidian 문서 업데이트 (새 마일스톤/ADR 추가)
6. **재검토/분석 결과** → 반드시 Obsidian에 반영 (코드와 단일 소스)

## 상세 규칙

빌드/행동 규칙은 `.claude/rules/`에 분리되어 경로별 자동 로드.

| 규칙 파일                            | 적용 범위                                                           |
| ------------------------------------ | ------------------------------------------------------------------- |
| `.claude/rules/behavior.md`          | 항상 (의존성 대응, 빌드 실패, 스크립트)                             |
| `.claude/rules/commit.md`            | 항상 (커밋 메시지 형식, AI 언급 금지)                               |
| `.claude/rules/documentation.md`     | 항상 (설명/설계/계획/보고 문서 — 쉬운 한국어 + 다이어그램 + 비교표) |
| `.claude/rules/build-environment.md` | GhostWin.sln, _.vcxproj, _.csproj, scripts/, external/ghostty/      |

## 빌드 (2026-04-14 — VS 통합)

- **IDE**: `GhostWin.sln` (VS 18 Insiders, v145 toolset)
- **빌드**: VS GUI (Ctrl+Shift+B) 또는 `msbuild GhostWin.sln /p:Configuration=Debug /p:Platform=x64`
- **디버깅**: F5 (Mixed-mode, C# + C++ 동시 브레이크포인트)
- **libghostty-vt**: 첫 빌드 시 자동 실행 (`scripts/build_libghostty.ps1`)
  'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'

상세: [[Architecture/4-project-structure]] (Obsidian)

## 프로젝트 현재 상태

- **Git 브랜치**: `feature/wpf-migration`
- **🎉 M-16 시리즈 완성** — A/B/C/D/F 5/5 closure (M-16-E 측정 1결함은 P3 선택, 미수행). **비전 ① "cmux 기능 탑재" 완성 마무리** (2026-05-09).
- **활성 사이클**: 없음 (M-16-F archive 후 다음 사이클 미정)
- **잔여 backlog (선택/mini)**:
  - **m16-f-tooltip-followup** mini — Settings 17 interactive controls (CheckBox/ComboBox/Slider) ToolTip 보강. FR-02 Partial closure (M-16-F 91% Match Rate 의 1 Partial 항목, 라벨 self-describing 으로 사용 차단 0)
  - **M-14 / M-15 회귀 측정** — render-state-test + idle p95 baseline 비교 (수동 manual run, M-16-F NFR deferred 항목)
  - **M-16-E** 측정 (1결함, 선택)
  - **M-16-G** P3 11건 누적 정리 (L2 / L5 / NEW-C / C-NEW-2 / F11 / F14 / A3 / A4 / A6 / A7 / A8) — 후속 audit 사이클
  - mini 4건: `m16-a-spacing-extra`, `m16-a-cursor-hover`, `m16-a-mainwindow-a11y`, `m16-b-mica-visibility` (OS wallpaper architectural limit)
- **직전 archived**: **M-16-F UI 체감 마감 (91%, 2026-05-09)** ← M-16-D cmux UX 패리티 (94%, 2026-04-30) ← M-16-C 터미널 렌더 (92%, 2026-04-29) ← M-16-B 윈도우 셸 (92%, 2026-04-29) ← M-16-A 디자인 시스템 (96%, 2026-04-29). 상세는 Obsidian `Milestones/` + `docs/archive/2026-04|2026-05/_INDEX.md`
- **2026-05 정착 정책 (M-16-F closure 산출)**:
  - 영어 단일 운영 (`memory project_english_only_ui.md`) — resx / CultureInfo / FlowDirection 변경 절대 금지
  - 자동화 검증 인프라 별도 트랙 (`tests/GhostWin.Automation.*` 사용자 manual 트랙) — PDCA 사이클의 deliverable 아님
- **🎯 비전 정렬**: Windows 용 **AI 에이전트 멀티플렉서** (cmux + ghostty 성능). cmux 기능 탑재 완성 → 다음은 비전 ② AI 에이전트 멀티플렉서 강화 또는 ③ 성능 우수 (M-15 Stage B 등) 로 전환 가능.

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

- **인덱스**:
  - `docs/archive/2026-05/_INDEX.md` (1 사이클, M-16-F)
  - `docs/archive/2026-04/_INDEX.md` (42 사이클, M-14/M-15 Stage A/M-16-A/B/C/D 포함)
  - `docs/archive/2026-03/_INDEX.md` (8 사이클, libghostty-vt-build 등 초기 Phase)
  - `docs/archive/legacy/_INDEX.md` (5 폴더: winui3-integration / wpf-hybrid-poc / m1-m3-verification / handoff-phase4b-ime / research)
- **활성 docs (코드와 함께 보존)**:
  - `docs/00-research/` 4건 — `2026-04-28-ui-completeness-audit.md` (M-16 A/B/C/D 출처, 39결함) + `2026-05-08-ui-completeness-audit.md` (M-16-F 출처, 24결함) + `cmux-ai-agent-ux-research.md` (roadmap 인용) + `ghostty-upstream-sync-analysis.md` (서브모듈 워크플로)
  - `docs/03-analysis/concurrency/pane-split-concurrency-20260406.md` (M-14 이전 분석 원본)
  - `docs/04-report/changelog.md` (전체 마일스톤 changelog)
  - `docs/01-plan/roadmap.md`, `docs/05-learning/01-terminal-parser-and-simd.md`, `docs/05-learning/02-conpty-integration-and-shutdown.md`, `docs/adr/` 14건
- **주요 archive artifacts** (참조 빈도 높음):
  - `docs/archive/2026-04/m14-render-thread-safety/` (PRD/Plan/Design v1.1/Analysis/Report + `baselines/` W1/W3/W4 + raw CSV 3개)
  - `docs/archive/2026-04/m15-render-baseline-comparison/` (Plan/Design/Analysis/Report + `baselines/` idle/resize-4pane/load + MeasurementDriver C# + `measure_render_baseline.ps1`)
  - `docs/archive/2026-04/m16-{a,b,c,d}-*/` + `docs/archive/2026-05/m16-f-ui-completion/` (M-16 시리즈 5건 풀세트, Match Rate 91~96%)
- **빈 폴더 (다음 사이클 대기)**: `docs/00-pm/`, `docs/01-plan/features/`, `docs/02-design/features/`, `docs/04-report/features/`
- **새 PDCA 사이클**: `/pdca pm {feature}` → `/pdca plan` → `/pdca design` → `/pdca do` → `/pdca analyze` → `/pdca report` → `/pdca archive --summary`
