# GhostWin Roadmap (2026-04-11 기준)

> Phase 1~4 + WPF 마이그레이션 M-1~M-9 + Phase 5-A~E + P0 부채 + M-10 마우스 완료.
> 이 문서는 남은 작업을 마일스톤 단위로 정리하고, 의존성과 순서를 명확히 한다.

---

## 현재 위치

```
Phase 1~4 ✅ → M-1~M-9 ✅ → Phase 5-A~E ✅ → P0 전체 ✅ → M-10 마우스 ✅
                                                                              ↓
                                                                      ★ 여기 ★
```

**앱 상태**: DX11 렌더링 + ConPTY + WPF Shell + 다중 Workspace/Pane + 마우스 클릭/스크롤/선택.
**부족한 것**: 클립보드, 세션 복원, 설정 UI, 조합 미리보기.

---

## 완료된 마일스톤

| 마일스톤 | 내용 | 완료일 | Archive |
|----------|------|:------:|---------|
| **M-10** | 마우스 입력 (클릭/스크롤/선택/DX11 하이라이트/CJK) | 2026-04-11 | `docs/archive/2026-04/mouse-input/` |
| P0 전체 | 부채 청산 10건 (종료 경로, PropertyChanged 등) | 2026-04-10 | 각 항목별 archive |
| Phase 5-A~E | session/tab/titlebar/settings/pane-split | ~2026-04-08 | 각 항목별 archive |

---

## 남은 마일스톤

### M-10.5: 복사/붙여넣기 (최우선)

> 목표: 선택한 텍스트를 클립보드로 복사/붙여넣기

| 순서 | Feature | 의존성 | 예상 규모 | 설명 |
|:----:|---------|--------|:---------:|------|
| 1 | **Ctrl+C 복사** | M-10 Selection 완료 | 소 | `GetSelectedText` API 이미 있음. 클립보드 write만 추가 |
| 2 | **Ctrl+V 붙여넣기** | 없음 | 소 | 클립보드 read → `WriteSession` 전달 |
| 3 | **우클릭 메뉴** (선택적) | #1+#2 | 소 | 복사/붙여넣기 컨텍스트 메뉴 |

### M-11: 세션 지속성

> 목표: 앱 재시작 시 작업 환경 유지

| 순서 | Feature | 의존성 | 예상 규모 | 설명 |
|:----:|---------|--------|:---------:|------|
| 1 | **session-restore** (Phase 5-F) | 없음 | 중 | CWD + pane 레이아웃 JSON 직렬화 → 시작 시 복원 |
| 2 | Workspace title mirror | #1 | 소 | Active pane의 session title/cwd가 sidebar에 반영 |

### M-12: 사용자 설정 UI

> 목표: JSON 수동 편집 없이 설정 변경 가능

| 순서 | Feature | 의존성 | 예상 규모 | 설명 |
|:----:|---------|--------|:---------:|------|
| 1 | **Settings UI** | 없음 | 중 | XAML 설정 페이지 (테마, 폰트, 키바인딩 등) |
| 2 | Command Palette | #1 | 중 | Airspace 우회 Popup Window, 검색 기반 명령 실행 |

### M-13: 입력 UX 완성

| 순서 | Feature | 의존성 | 예상 규모 | 설명 |
|:----:|---------|--------|:---------:|------|
| 1 | **조합 미리보기** | 없음 | 소 | TSF preedit → 렌더러 오버레이 (한글 입력 UX) |
| 2 | **마우스 커서 모양** | 없음 | 소 | ghostty cursor_shape 콜백 → WPF Cursor 변경 |

---

## 의존성 다이어그램

```
M-10.5 복사/붙여넣기 ← M-10 Selection 완료 ✅
  ↓
M-11 세션 지속성 ──── M-10.5와 병렬 가능
  ↓
M-12 설정 UI
  ↓
M-13 입력 UX ──── 독립 (어느 시점에서든)

기술 부채 ──── 마일스톤 사이에 삽입
```

---

## 기술 부채 (마일스톤 사이에 삽입)

| 우선순위 | 항목 | 규모 | 비고 |
|:--------:|------|:----:|------|
| MEDIUM | `repro-script-fix` | ~30 LOC | AMSI window-capture |
| micro | `runner-py-feature-field-cleanup` | ~5 LOC | hardcoded field |
| LOW | `MoveFocus spatial navigation` | 중 | 좌표 기반 포커스 |
| LOW | `CrashLog 파일 회전` | 소 | %LocalAppData% |
| LOW | `vt_mutex 통합` | 중 | 이중 mutex |
| LOW | `SessionManager 리팩토링` | 중 | 17 public → SRP |
| LOW | `keydiag-*` 3건 + `adr-011` | 소 | 진단 코드 정리 |

---

## 진행 규칙

1. **마일스톤 단위로 진행** — 순서대로 완료 후 다음으로
2. **각 feature는 PDCA** — PM(선택) → Plan → Design → Do → Check → Archive
3. **주요 feature는 벤치마킹 선행** — 참조 터미널 코드베이스 조사 후 설계
4. **기술 부채는 마일스톤 사이에 삽입**
5. **커밋은 테스트 완료 후에만**

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-10 | Initial roadmap |
| 0.2 | 2026-04-11 | M-10 완료 반영. M-10.5 복사/붙여넣기 추가. M-13 입력 UX 분리 |
