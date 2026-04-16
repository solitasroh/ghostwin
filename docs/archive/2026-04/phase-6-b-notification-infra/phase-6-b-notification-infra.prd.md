# Phase 6-B PRD — 알림 인프라 (Notification Infrastructure)

> **문서 종류**: Product Requirements Document (PRD)
> **작성일**: 2026-04-16
> **Phase**: 6-B
> **소유자**: 노수장
> **선행 Phase**: Phase 6-A (OSC Hook + 알림 링) — 완료, 93% Match Rate

---

## Executive Summary

| 관점 | 내용 |
|------|------|
| **Problem** | Phase 6-A에서 amber dot + Toast 알림이 동작하지만, 3개 이상 탭을 병렬 운영하면 어떤 탭이 언제 알림을 보냈는지 추적 불가. 알림을 놓치면 에이전트가 수분~수십분 유휴 상태로 방치됨 |
| **Solution** | 알림 패널(히스토리 목록) + 에이전트 상태 배지(4-state) + Toast 클릭 액션으로 "알림 → 인지 → 탭 전환" 전체 루프 완성 |
| **Function / UX Effect** | Ctrl+Shift+I로 알림 패널 열기 → 시간순 알림 목록 확인 → 클릭으로 해당 탭 즉시 전환. 사이드바에 상태 배지(실행중/대기/오류/완료)로 각 세션 상태 한눈에 파악. Toast 클릭 시 해당 탭 자동 활성화 |
| **Core Value** | 비전 ② AI 에이전트 멀티플렉서의 **운영 인프라 완성**. Phase 6-A가 "알림 캡처"를 증명했다면, Phase 6-B는 "알림 관리 + 상태 추적"을 제공하여 5~10개 에이전트 동시 운영을 실용적으로 만듦 |

---

## 1. 배경과 동기

### 1.1 Phase 6-A가 증명한 것

Phase 6-A (커밋 `826768e`)에서 다음이 검증되었다:

- ghostty libvt → C++ 콜백 → C# `IOscNotificationService` → WPF amber dot 파이프라인 **동작**
- OSC 9/99/777 세 가지 모두 `SHOW_DESKTOP_NOTIFICATION`으로 통합 처리
- Win32 Toast 알림 (창 비활성 시)
- E2E 자동 검증 체계 (`TestOnlyInjectBytes` + `OscInjector`)

### 1.2 Phase 6-A에서 부족한 것

| 문제 | 영향 |
|------|------|
| 알림 목록이 없음 | 과거 알림을 확인할 방법 없음. 탭 amber dot은 한 번 보면 사라짐 |
| 알림 점프가 수동 | "어느 탭이 알림을 보냈는지" 알아도, 클릭해서 직접 찾아야 함 |
| 상태 배지가 없음 | 실행 중 / 대기 / 오류 / 완료 구분 불가. amber dot = "뭔가 왔다" 뿐 |
| Toast 클릭 액션 없음 | Toast를 클릭해도 아무 일 없음 (탭 전환 미구현) |
| 미읽음 즉시 점프 없음 | cmux의 `⌘⇧U` (가장 최근 미읽음 탭 즉시 전환) 에 대응하는 기능 없음 |

### 1.3 cmux 알림 시스템 참조

cmux (v0.62.2)의 알림 계층 (리서치 `docs/00-research/cmux-ai-agent-ux-research.md` §1.3):

```
시각적 피드백 계층
├── 파란 링 (Notification Ring)     ← Phase 6-A에서 amber dot으로 구현 ✅
├── 사이드바 배지                    ← Phase 6-B FR-02
├── 알림 패널 (시간순 목록)          ← Phase 6-B FR-01
└── 데스크톱 알림                    ← Phase 6-A에서 Toast로 구현 ✅
```

알림 생명주기 4단계:
1. **수신(Received)** → 알림 패널 추가 + 데스크톱 알림 발화
2. **미읽음(Unread)** → 사이드바 배지 유지
3. **읽음(Read)** → 해당 탭 조회 시 자동 소거
4. **제거(Dismissed)** → 패널에서 수동 삭제

---

## 2. 타겟 사용자

### 2.1 1차 타겟

**Claude Code 병렬 운영 개발자** — 3~10개 Claude Code 세션을 동시에 운영하며,
각 세션이 입력 대기/오류/완료 상태인지 빠르게 파악해야 하는 사용자.

### 2.2 사용자 시나리오

**시나리오 A: 5개 에이전트 동시 감시**

> 개발자가 5개 Claude Code 세션을 동시에 켜고, 각각 다른 브랜치에서 작업을 시킨다.
> 세션 3이 권한 요청(permission prompt)을 보내면:
>
> 1. 탭 3에 amber dot 점등 (Phase 6-A ✅)
> 2. **사이드바에 🔵 대기 배지 표시** (Phase 6-B FR-02)
> 3. **알림 패널에 "세션 3: 권한 필요 — 14:32" 항목 추가** (Phase 6-B FR-01)
> 4. 개발자가 `Ctrl+Shift+U`로 세션 3으로 즉시 전환 (Phase 6-B FR-01)
> 5. 승인 후 세션 3의 배지가 🔵→⚡(실행중)으로 변경 (Phase 6-B FR-02)

**시나리오 B: 외출 후 복귀**

> 개발자가 GhostWin을 띄워두고 외출.
> 복귀 시:
>
> 1. Windows 알림 센터에 Toast 2건 쌓여있음 (Phase 6-A ✅)
> 2. **Toast 클릭 → 해당 탭 자동 전환** (Phase 6-B FR-03)
> 3. **알림 패널을 열면 놓친 알림 전체 확인** (Phase 6-B FR-01)

---

## 3. 기능 요구사항

### FR-01: 알림 패널 (Notification Panel)

**우선순위**: 필수 | **규모**: 중

모든 세션의 알림을 시간순으로 통합하여 표시하는 패널.

| 항목 | 상세 |
|------|------|
| **위치** | 사이드바 우측에 오버레이 패널 (Popup 또는 Flyout) |
| **열기/닫기** | `Ctrl+Shift+I` 토글 |
| **미읽음 점프** | `Ctrl+Shift+U` → 가장 최근 미읽음 알림의 탭으로 즉시 전환 |
| **알림 항목 표시** | 시간, 세션 이름, 메시지 (title/body) |
| **알림 클릭** | 해당 세션/탭으로 자동 전환 + 해당 알림 읽음 처리 |
| **전체 삭제** | "모두 읽음" 버튼으로 전체 dismiss |
| **최대 보존 건수** | 100건 (FIFO, 초과 시 가장 오래된 것 제거) |
| **알림 억제** | 현재 활성 탭의 알림은 패널에 추가하되 미읽음 카운트에서 제외 |

**데이터 소스**: Phase 6-A의 `IOscNotificationService`가 발행하는 `OscNotificationMessage`

**cmux 대응**: `⌘⇧I` 알림 패널 + `⌘⇧U` 미읽음 점프

### FR-02: 에이전트 상태 배지 (Agent Status Badge)

**우선순위**: 필수 | **규모**: 소

사이드바 각 탭 항목에 에이전트 상태를 시각적으로 표시.

| 상태 | 조건 | 시각적 표현 | 색상 |
|------|------|-----------|------|
| **Idle** | 초기 상태, 셸 프롬프트 대기 | 표시 없음 (기본) | — |
| **Running** | 출력이 진행 중 (마지막 출력 5초 이내) | ⚡ 또는 작은 펄스 원 | #34C759 (초록) |
| **WaitingForInput** | OSC 알림 수신 (NeedsAttention=true) | 🔵 원형 배지 | #007AFF (파란) |
| **Error** | 프로세스 비정상 종료 또는 오류 OSC | ❌ X 마크 | #FF3B30 (빨간) |
| **Completed** | 프로세스 정상 종료 (exit code 0) | ✓ 체크마크 | #8E8E93 (회색) |

**상태 전환 규칙**:

```
Idle ──[출력 시작]──→ Running
Running ──[OSC 알림]──→ WaitingForInput
Running ──[5초 무출력]──→ Idle
WaitingForInput ──[사용자 입력]──→ Running
Running ──[프로세스 종료, exit=0]──→ Completed
Running ──[프로세스 종료, exit≠0]──→ Error
Error/Completed ──[새 명령]──→ Running
```

**구현 위치**: `SessionInfo`에 `AgentState` enum 필드 추가 → `WorkspaceItemViewModel`에서 바인딩

**cmux 대응**: cmux 사이드바의 에이전트 상태 표시 (리서치 §3.1)

### FR-03: Toast 클릭 액션 (Toast Click-to-Switch)

**우선순위**: 필수 | **규모**: 소

Win32 Toast 알림 클릭 시 해당 세션의 탭으로 자동 전환.

| 항목 | 상세 |
|------|------|
| **Toast 생성 시** | `ToastContentBuilder.AddArgument("sessionId", id.ToString())` |
| **클릭 핸들러** | `ToastNotificationManagerCompat.OnActivated` 이벤트에서 sessionId 추출 |
| **동작** | GhostWin 창 복원(최소화 시) → 해당 sessionId의 탭 활성화 → 알림 dismiss |
| **복수 Toast** | 각 Toast가 독립적인 sessionId를 가짐 → 올바른 탭으로 전환 |

**현재 상태**: Phase 6-A에서 Toast 발사는 구현되었으나 클릭 액션 미구현.

---

## 4. 비기능 요구사항

| 항목 | 기준 |
|------|------|
| **성능** | 알림 100건 목록 렌더링 < 16ms (60fps 유지) |
| **메모리** | 알림 히스토리 최대 100건 인메모리. SQLite 등 영속 저장은 Phase 6-B 범위 밖 |
| **스레드 안전** | OSC 콜백은 I/O thread → Dispatcher.BeginInvoke 패턴 유지 (Phase 6-A 동일) |
| **접근성** | 알림 패널 항목에 AutomationId 부여 → E2E 검증 가능 |
| **설정** | 기존 `AppSettings.Notifications` 확장: `PanelEnabled`, `BadgeEnabled` 추가 |

---

## 5. 범위 밖 (명시적 제외)

| 항목 | 이유 | 대안 |
|------|------|------|
| Named pipe 훅 서버 | Phase 6-C 범위 | `/pdca plan phase-6-c-external-integration` |
| git branch/PR 상태 표시 | Phase 6-C 범위 | 사이드바 확장 |
| 알림 히스토리 SQLite 영속 저장 | 복잡도 대비 가치 낮음 | 인메모리 100건으로 충분 |
| 알림 소리/진동 | Windows 터미널 관례상 불필요 | 사용자 요청 시 추가 |
| 알림 필터링/카테고리 분류 | 과도한 복잡도 (YAGNI) | 수요 발생 시 추가 |
| 세션별 개별 알림 설정 | 과도한 복잡도 (YAGNI) | 전역 설정으로 충분 |

---

## 6. 기술 의존성

### 6.1 Phase 6-A에서 물려받는 자산

| 자산 | Phase 6-B 활용 |
|------|---------------|
| `IOscNotificationService` | 알림 패널이 구독하는 데이터 소스 |
| `OscNotificationMessage` record | 알림 히스토리 항목의 데이터 구조 |
| `SessionInfo.NeedsAttention` / `LastOscMessage` | 에이전트 상태 배지 바인딩 |
| `WeakReferenceMessenger` 패턴 | 알림 패널 ↔ 서비스 간 느슨한 결합 |
| Toast 인프라 (`Microsoft.Toolkit.Uwp.Notifications`) | Toast 클릭 액션 확장 |
| `TestOnlyInjectBytes` + `OscInjector` | Phase 6-B E2E 검증 재사용 |

### 6.2 신규 의존성

없음. 기존 NuGet 패키지와 WPF 기본 컨트롤로 구현 가능.

---

## 7. 성공 기준

| # | 기준 | 검증 방법 |
|:-:|------|----------|
| 1 | `Ctrl+Shift+I`로 알림 패널 열림/닫힘 | 수동 확인 + E2E |
| 2 | OSC 9 주입 → 알림 패널에 항목 추가됨 | E2E (OscInjector → 패널 항목 UIA 확인) |
| 3 | 알림 항목 클릭 → 해당 탭 활성화 | 수동 확인 |
| 4 | `Ctrl+Shift+U` → 가장 최근 미읽음 탭으로 전환 | 수동 확인 + E2E |
| 5 | 사이드바에 에이전트 상태 배지 표시 | 수동 확인 |
| 6 | Toast 클릭 → GhostWin 활성화 + 해당 탭 전환 | 수동 확인 |
| 7 | 알림 100건 이상에서도 UI 60fps 유지 | 수동 확인 (성능 프로파일링) |

---

## 8. 구현 순서 제안

```
FR-01 알림 패널           ← 핵심 가치. 알림 히스토리 + 미읽음 점프
  ↓
FR-02 에이전트 상태 배지   ← 사이드바 시각화 확장
  ↓
FR-03 Toast 클릭 액션     ← 기존 Toast 기능 완성
```

**예상 기간**: 1~1.5일 (집중 세션)

- FR-01: 0.5일 (알림 모델 + WPF 패널 UI + 키보드 바인딩)
- FR-02: 0.3일 (AgentState enum + 상태 전환 + 배지 XAML)
- FR-03: 0.2일 (Toast 인자 추가 + OnActivated 핸들러)

---

## 9. 리스크

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| WPF Popup/Flyout Airspace 충돌 (D3D11 렌더링 영역 위에 WPF 오버레이) | 중 | Phase 5에서 검증된 패턴 확인. 필요 시 별도 Window 사용 |
| AgentState "Running" 판정의 false positive (출력 없이 백그라운드 작업 중) | 낮 | 5초 타임아웃 + OSC 기반 상태 우선 (OSC가 가장 신뢰할 수 있는 소스) |
| Toast OnActivated가 앱 재시작 시 호출될 수 있음 | 낮 | sessionId 유효성 체크 후 무시 |

---

## 10. 경쟁 제품 대비 차별점

| 기능 | cmux | Warp | Wave | GhostWin (6-B 목표) |
|------|:----:|:----:|:----:|:-------------------:|
| 알림 패널 (히스토리 목록) | ✅ | ❌ | ❌ | ✅ |
| 미읽음 즉시 점프 | ✅ (⌘⇧U) | ❌ | ❌ | ✅ (Ctrl+Shift+U) |
| 에이전트 상태 배지 | ✅ | ⚠️ | ❌ | ✅ |
| Toast 클릭→탭 전환 | N/A (macOS 알림) | ❌ | ❌ | ✅ |
| Windows 네이티브 | ❌ (macOS only) | ✅ | ✅ | ✅ |

**차별점**: Windows에서 유일하게 "알림 패널 + 미읽음 점프 + 에이전트 배지" 를 제공하는 터미널.

---

## 참조 문서

- Phase 6-A PRD: `docs/archive/2026-04/phase-6-a-osc-notification-ring/phase-6-a-osc-notification-ring.prd.md`
- Phase 6-A 완료 보고서: `docs/archive/2026-04/phase-6-a-osc-notification-ring/phase-6-a-osc-notification-ring.report.md`
- cmux 리서치: `docs/00-research/cmux-ai-agent-ux-research.md` (§1.3 알림 링, §1.4 알림 패널, §3.1 상태 모델)
- 프로젝트 비전: `onboarding.md` §2 킬러 피처
- 로드맵: `docs/01-plan/roadmap.md` §Phase 6-B

---

*Phase 6-B PRD v1.0 — Notification Infrastructure (2026-04-16)*
