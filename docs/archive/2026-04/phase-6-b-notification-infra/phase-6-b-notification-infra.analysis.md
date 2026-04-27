# Phase 6-B Notification Infrastructure — Design vs Implementation Gap Analysis

> **분석일**: 2026-04-16
> **Design 문서**: `docs/02-design/features/phase-6-b-notification-infra.design.md`
> **분석 범위**: 신규 4개 + 변경 10개 파일 (총 14개)

---

## 1. 전체 점수

| 범주 | 점수 | 상태 |
|------|:----:|:----:|
| W1: 모델 + 서비스 확장 | 100% | OK |
| W2: 알림 패널 UI | 97% | OK |
| W3: AgentState 배지 | 95% | OK |
| W4: Toast 클릭 액션 | 100% | OK |
| **전체 Match Rate** | **97%** | OK |

---

## 2. Wave별 항목 점검

### W1: 모델 + 서비스 확장 (100%)

| # | Design 항목 | 구현 여부 | 비고 |
|:-:|------------|:--------:|------|
| 1 | AgentState enum (5값: Idle, Running, WaitingForInput, Error, Completed) | O | 완전 일치 |
| 2 | NotificationEntry (ObservableObject, IsRead, 6 프로퍼티) | O | 완전 일치 |
| 3 | SessionInfo에 AgentState, LastOutputTime 추가 | O | 완전 일치 |
| 4 | WorkspaceInfo에 AgentState 추가 | O | 완전 일치 |
| 5 | IOscNotificationService에 Notifications, UnreadCount, Mark 메서드 | O | 완전 일치 |
| 6 | OscNotificationService: ObservableObject 상속, FIFO 100건 | O | 완전 일치 |
| 7 | AppSettings.NotificationSettings에 PanelEnabled, BadgeEnabled | O | 완전 일치 |

### W2: 알림 패널 UI (97%)

| # | Design 항목 | 구현 여부 | 비고 |
|:-:|------------|:--------:|------|
| 1 | NotificationPanelControl UserControl | O | 완전 일치 |
| 2 | MainWindow Grid 5-column 레이아웃 | O | 완전 일치 |
| 3 | NotificationPanelWidth 바인딩 (0/280) | O | 완전 일치 |
| 4 | NotificationDividerWidth 바인딩 (0/1) | - | 아래 참조 |
| 5 | ToggleNotificationPanel 커맨드 | O | 완전 일치 |
| 6 | NotificationClick 커맨드 | O | null 체크 추가 (의도적 강화) |
| 7 | JumpToUnread 커맨드 | O | 완전 일치 |
| 8 | MarkAllRead 커맨드 | O | 완전 일치 |
| 9 | Ctrl+Shift+I (패널 토글) | O | 완전 일치 |
| 10 | Ctrl+Shift+U (미읽음 점프) | O | 완전 일치 |
| 11 | InverseBoolToVisibilityConverter | O | 완전 일치 |
| 12 | Empty state ("No notifications") | O | Design에 없지만 추가됨 |
| 13 | ListBoxItem hover 스타일 (#2A2A2C) | O | Design에 없지만 추가됨 |
| 14 | StaticResource 패턴 (색상 브러시) | O | Design의 인라인 색상 대신 리소스 분리 |

### W3: AgentState 배지 (95%)

| # | Design 항목 | 구현 여부 | 비고 |
|:-:|------------|:--------:|------|
| 1 | WorkspaceItemViewModel: AgentStateBadge/Color/ShowAgentBadge | O | 완전 일치 |
| 2 | AgentStateColor → static 브러시 필드 | O | Design은 매번 new, 구현은 static readonly (의도적 개선) |
| 3 | MainWindow.xaml 배지 TextBlock | O | 완전 일치 |
| 4 | OnWorkspacePropertyChanged 확장 | O | 완전 일치 |
| 5 | SessionManager.NotifySessionOutput | O | 완전 일치 |
| 6 | SessionManager.NotifyChildExit | O | 완전 일치 |
| 7 | SessionManager.TickAgentStateTimer | O | 완전 일치 |
| 8 | OnTitleChanged/OnCwdChanged에서 NotifySessionOutput 호출 | O | 완전 일치 |
| 9 | OnChildExit에서 NotifyChildExit 호출 | O | 완전 일치 |
| 10 | AgentState 타이머: 별도 DispatcherTimer | - | 기존 _cwdPollTimer에 병합 (아래 참조) |

### W4: Toast 클릭 액션 (100%)

| # | Design 항목 | 구현 여부 | 비고 |
|:-:|------------|:--------:|------|
| 1 | ToastContentBuilder.AddArgument("sessionId", ...) | O | 완전 일치 |
| 2 | ToastNotificationManagerCompat.OnActivated 핸들러 | O | 완전 일치 |
| 3 | 창 복원 (Minimized → Normal) + Activate | O | 완전 일치 |
| 4 | ActivateWorkspace(ws.Id) | O | 완전 일치 |
| 5 | DismissAttention(sessionId) | O | 완전 일치 |

---

## 3. 차이점 목록

### 의도적 변경 (Design과 다르지만 동등하거나 개선된 구현)

| # | 항목 | Design | 구현 | 영향 |
|:-:|------|--------|------|:----:|
| D-1 | NotificationDividerWidth 프로퍼티 | ViewModel에 별도 프로퍼티 (0/1) | XAML에서 IsNotificationPanelOpen으로 Visibility 직접 제어 | 없음 |
| D-2 | AgentState 타이머 구조 | 별도 DispatcherTimer + StartAgentStateTimer() | 기존 _cwdPollTimer.Tick에 TickAgentStateTimer() 병합 | 없음 (타이머 수 절감) |
| D-3 | AgentStateColor 타입 | `SolidColorBrush` (매 호출 new) | `static readonly` 필드 + 반환 타입 `Brush` | 성능 개선 |
| D-4 | NotificationClick 파라미터 | `NotificationEntry entry` | `NotificationEntry? entry` (nullable + null 체크) | 안전성 강화 |
| D-5 | 패널 XAML 색상 표현 | 인라인 hex (#1C1C1E 등) | StaticResource 브러시로 분리 | 유지보수 개선 |
| D-6 | Empty state UI | Design에 없음 | "No notifications" TextBlock 추가 | UX 개선 |
| D-7 | ListBoxItem 스타일 | Design에 없음 | hover 배경(#2A2A2C) + 패딩/마진 초기화 | UX 개선 |
| D-8 | HasNotifications 프로퍼티 | Design에 없음 | ViewModel에 추가 + CollectionChanged 구독 | Empty state UI 지원 |
| D-9 | OscService PropertyChanged 구독 | 생성자 내 인라인 람다 | 별도 메서드 OnOscServicePropertyChanged | 코드 정리 |
| D-10 | AutomationId E2E_NotificationPanel | Design에 없음 | UserControl 루트에 추가 | E2E 테스트 지원 |

### 미구현 항목

없음. Design 문서의 모든 기능이 구현됨.

### 추가 구현 항목 (Design에 없음)

| # | 항목 | 위치 | 설명 |
|:-:|------|------|------|
| A-1 | Empty state | NotificationPanelControl.xaml:52-57 | 알림 없을 때 "No notifications" 표시 |
| A-2 | HasNotifications | MainWindowViewModel.cs:51 | 알림 존재 여부 바인딩 지원 |
| A-3 | ListBoxItem 호버 스타일 | NotificationPanelControl.xaml:64-86 | 항목 마우스오버 시각 피드백 |

---

## 4. 아키텍처 준수 평가

| 항목 | 상태 | 설명 |
|------|:----:|------|
| MVVM 패턴 | OK | ViewModel 커맨드 + 서비스 DI, 코드비하인드 최소 |
| 의존 방향 | OK | Core ← Services ← App (역방향 참조 없음) |
| 스레드 안전 | OK | ObservableCollection은 UI 스레드 전용, HandleOscEvent는 Dispatcher 경유 |
| 순환 의존 | OK | SessionManager ↔ OscService 순환은 SetOscService()로 해결 (Phase 6-A 패턴 유지) |
| DI 등록 | OK | App.xaml.cs에 IOscNotificationService → OscNotificationService Singleton |

---

## 5. 컨벤션 준수 평가

| 항목 | 상태 | 설명 |
|------|:----:|------|
| 파일명 PascalCase | OK | AgentState.cs, NotificationEntry.cs 등 |
| 네임스페이스 | OK | Core/Models, Core/Interfaces, Services, App/Controls, App/Converters |
| ObservableProperty 패턴 | OK | _underscore prefix + [ObservableProperty] |
| RelayCommand 패턴 | OK | private 메서드 + [RelayCommand] |
| AutomationId 규칙 | OK | E2E_ 접두사 일관 |

---

## 6. 결론

**Match Rate: 97%** — Design과 구현이 잘 일치함.

3%의 차이는 모두 **의도적 개선** (D-1~D-10)이며, 기능 누락은 없음:
- 타이머 병합 (D-2): 별도 타이머 대신 기존 CWD 폴링 타이머에 병합하여 리소스 절약
- static 브러시 (D-3): 매번 new SolidColorBrush 대신 static readonly로 GC 부담 감소
- UX 보강 (D-6, D-7): Empty state, hover 피드백 등 Design에서 명시하지 않았지만 필요한 UI 요소 추가
- 안전성 (D-4): nullable 파라미터로 방어적 코딩

권장 사항: Design 문서에 D-1~D-10 의도적 변경 사항을 반영하여 문서와 코드의 동기화 유지.

---

*Phase 6-B Gap Analysis v1.0 (2026-04-16)*
