# GhostWin Roadmap (2026-04-10 기준)

> Phase 1~4 + WPF 마이그레이션 M-1~M-7 + Phase 5-A~E + P0 부채 청산 전체 완료.
> 이 문서는 남은 작업을 마일스톤 단위로 정리하고, 의존성과 순서를 명확히 한다.

---

## 현재 위치

```
Phase 1 ✅ → Phase 2 ✅ → Phase 3 ✅ → Phase 4 ✅ → Phase 5-A~E ✅ → P0 전체 ✅
                                                                          ↓
                                                                  ★ 여기 ★
```

**앱 상태**: DX11 렌더링 + ConPTY + WPF Shell + 다중 Workspace/Pane 동작.
**부족한 것**: 마우스, 클립보드, 세션 복원 등 터미널 기본 기능.

---

## 마일스톤 계획

### M-10: 터미널 기본 조작 완성 (최우선)

> 목표: "일상 터미널로 사용 가능" 수준 달성

| 순서 | Feature | 의존성 | 예상 규모 | 설명 |
|:----:|---------|--------|:---------:|------|
| 1 | **마우스 입력** | 없음 | 중 | 클릭 포커스, 스크롤, 텍스트 선택 (ghostty mouse event 연동) |
| 2 | **복사/붙여넣기** | #1 (선택 필요) | 소 | Ctrl+C/V 클립보드, 마우스 선택 영역 복사 |
| 3 | **조합 미리보기** | 없음 | 소 | TSF preedit → 렌더러 오버레이 (한글 입력 UX) |

**완료 기준**: 마우스로 텍스트 선택 → 복사 → 붙여넣기 → 한글 입력 조합 미리보기 동작.

### M-11: 세션 지속성

> 목표: 앱 재시작 시 작업 환경 유지

| 순서 | Feature | 의존성 | 예상 규모 | 설명 |
|:----:|---------|--------|:---------:|------|
| 1 | **session-restore** (Phase 5-F) | M-10 없이 가능 | 중 | CWD + pane 레이아웃 JSON 직렬화 → 시작 시 복원 |
| 2 | Workspace title mirror | #1 | 소 | Active pane의 session title/cwd가 sidebar에 반영 |

**완료 기준**: 앱 종료 → 재시작 시 이전 workspace/pane 레이아웃 + CWD 복원.

### M-12: 사용자 설정 UI

> 목표: JSON 수동 편집 없이 설정 변경 가능

| 순서 | Feature | 의존성 | 예상 규모 | 설명 |
|:----:|---------|--------|:---------:|------|
| 1 | **Settings UI** | M-10 (mouse 필요) | 중 | XAML 설정 페이지 (테마, 폰트, 키바인딩 등) |
| 2 | Command Palette | #1 | 중 | Airspace 우회 Popup Window, 검색 기반 명령 실행 |

**완료 기준**: 설정 UI에서 테마/폰트 변경 → 즉시 반영.

---

## 의존성 다이어그램

```
M-10 터미널 기본 조작
  ├─ 1. 마우스 입력
  │    └─ 2. 복사/붙여넣기
  └─ 3. 조합 미리보기 (독립)

M-11 세션 지속성 ──── M-10과 병렬 가능
  ├─ 1. session-restore
  └─ 2. workspace title mirror

M-12 설정 UI ──── M-10 완료 후
  ├─ 1. Settings UI
  └─ 2. Command Palette

기술 부채 ──── 어느 시점에서든 삽입 가능
  ├─ vt_mutex 통합
  ├─ SessionManager 리팩토링
  └─ Follow-up Cycles (LOW)
```

---

## 기술 부채 (마일스톤 사이에 삽입)

우선순위순 정리. 마일스톤 작업 사이 여유가 있을 때 처리.

| 우선순위 | 항목 | 규모 | 삽입 시점 권장 |
|:--------:|------|:----:|---------------|
| MEDIUM | `repro-script-fix` | ~30 LOC | M-10 전 |
| micro | `runner-py-feature-field-cleanup` | ~5 LOC | 아무 때나 |
| LOW | `MoveFocus spatial navigation` | 중 | M-11 이후 |
| LOW | `CrashLog 파일 회전 + LocalAppData` | 소 | M-11 이후 |
| LOW | `vt_mutex 통합` | 중 | M-12 이후 |
| LOW | `SessionManager 리팩토링` | 중 | M-12 이후 |
| LOW | `adr-011-timer-review` | ~10 LOC | 아무 때나 |
| LOW | `keydiag-*` 3건 | 소 | 아무 때나 |
| LOW | `e2e-flaui-cross-validation` | 실행만 | 아무 때나 |
| LOW | `유휴 GPU 실측` | 측정만 | 아무 때나 |

---

## 진행 규칙

1. **마일스톤 단위로 진행** — M-10 내 순서대로 완료 후 M-11로
2. **각 feature는 PDCA** — Plan → Design → Do → Check → Archive
3. **기술 부채는 마일스톤 사이에 삽입** — 마일스톤 전환 시 1~2개 처리
4. **M-10과 M-11은 병렬 가능** — session-restore는 마우스/클립보드 없이도 동작

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | Initial roadmap (P0 전체 완료 후 작성) |
