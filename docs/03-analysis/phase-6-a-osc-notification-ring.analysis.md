# Gap Analysis — Phase 6-A: OSC Hook + 알림 링

> **분석일**: 2026-04-16
> **Design 문서**: `docs/02-design/features/phase-6-a-osc-notification-ring.design.md`
> **Plan 문서**: `docs/01-plan/features/phase-6-a-osc-notification-ring.plan.md`

---

## 한 줄 요약

Design 6개 Wave 중 W1-W4 + W6의 핵심은 구현 완료, W3 Settings/W5 E2E Tier 3은 미구현. 전체 매치율 **78%**.

---

## 전체 점수

| 범주 | 점수 | 상태 |
|------|:----:|:----:|
| C++ 콜백 파이프라인 (W1) | 95% | OK |
| C# 서비스 계층 (W2) | 85% | OK |
| WPF UI (W3) | 70% | 주의 |
| Win32 Toast (W4) | 90% | OK |
| E2E / TestOnlyInjectBytes (W5) | 50% | 미달 |
| 설정 스키마 | 0% | 미구현 |
| **전체** | **78%** | 주의 |

---

## 1. 구현 완료 항목 (Design = 구현)

### W1 — C++ OSC 콜백 파이프라인

| Design 항목 | 구현 파일 | 일치 여부 | 비고 |
|-------------|----------|:---------:|------|
| `VtOscNotifyFn` typedef | `vt_bridge.h:185` `VtDesktopNotifyFn` | 변경 | 이름 변경 (아래 상세) |
| `vt_bridge_set_osc_notify_callback()` | `vt_bridge.h:191` `vt_bridge_set_desktop_notify_callback()` | 변경 | 이름 변경 |
| vt_bridge.c 콜백 저장 + ghostty 옵션 등록 | `vt_bridge.c:385-405` | 일치 | `GHOSTTY_TERMINAL_OPT_DESKTOP_NOTIFICATION=15` 사용 |
| vt_core.h `set_osc_notify_callback()` | `vt_core.h:124` `set_desktop_notify_callback()` | 변경 | 이름 변경 |
| vt_core.cpp 구현 | `vt_core.cpp:201-205` | 일치 | |
| ghostwin_engine.h `GwOscNotifyFn` | `ghostwin_engine.h:44-46` | 일치 | |
| GwCallbacks에 `on_osc_notify` 슬롯 | `ghostwin_engine.h:57` | 일치 | |
| ghostwin_engine.cpp `make_session_events()` OSC lambda | `ghostwin_engine.cpp:110-120` | 일치 | `Utf8ToWide` + `string_util.h` 사용 |
| session_manager.h `SessionEvents` OSC 슬롯 | `session_manager.h:50-53` | 일치 | |
| session_manager.cpp `fire_osc_notify_event` | `session_manager.cpp:487-498` | 일치 | |
| conpty_session.h `VtDesktopNotifyFn` | `conpty_session.h:52-55` | 일치 | |
| conpty_session.cpp pending buffer + I/O dispatch | `conpty_session.cpp:279-286, 343-344` | 일치 | |

### W2 — C# 서비스 계층

| Design 항목 | 구현 파일 | 일치 여부 | 비고 |
|-------------|----------|:---------:|------|
| NativeEngine.cs `GwCallbacks` 구조체 확장 | `NativeEngine.cs:16` `OnOscNotify` | 일치 | Sequential 순서 정확 |
| NativeCallbacks.cs `OnOscNotify` | `NativeCallbacks.cs:83-92` | 일치 | `Dispatcher.BeginInvoke` 사용 (ADR-006) |
| EngineService.cs 콜백 등록 | `EngineService.cs:40-41` | 일치 | |
| `IOscNotificationService.cs` (NEW) | `IOscNotificationService.cs:1-7` | 변경 | `int oscKind` 파라미터 제거됨 (아래 상세) |
| `OscNotificationService.cs` (NEW) | `OscNotificationService.cs:1-61` | 부분 | 설정 검사 없음 (아래 상세) |
| `OscNotificationMessage.cs` (NEW) | `OscNotificationMessage.cs:1-3` | 변경 | `int OscKind` 필드 제거됨 |
| SessionInfo.cs `NeedsAttention` + `LastOscMessage` | `SessionInfo.cs:19-22` | 부분 | `AttentionRaisedAt` 미구현 |
| GwCallbackContext.OnOscNotify | `IEngineService.cs:110` | 일치 | |
| App.xaml.cs DI 등록 | `App.xaml.cs:43` | 일치 | |
| App.xaml.cs `SetOscService` | `App.xaml.cs:51-52` | 일치 | 순환 의존 해결 패턴 |

### W3 — WPF UI

| Design 항목 | 구현 파일 | 일치 여부 | 비고 |
|-------------|----------|:---------:|------|
| Ellipse 8x8 amber dot | `MainWindow.xaml:295-300` | 부분 | `AutomationProperties.AutomationId` 미설정 |
| `BoolToVisibilityConverter` | `MainWindow.xaml:21` | 일치 | |
| ToolTip `LastOscMessage` | `MainWindow.xaml:300` | 일치 | |
| Auto-dismiss `ActivateSession()` | `SessionManager.cs:86` | 일치 | |

### W4 — Win32 Toast

| Design 항목 | 구현 파일 | 일치 여부 | 비고 |
|-------------|----------|:---------:|------|
| `Microsoft.Toolkit.Uwp.Notifications` NuGet | `GhostWin.App.csproj:17` v7.1.3 | 일치 | |
| Toast 발사 코드 | `App.xaml.cs:101-113` | 변경 | OscNotificationService 내부가 아닌 App.xaml.cs Messenger 구독 |
| 창 비활성 조건 | `App.xaml.cs:104` `MainWindow?.IsActive == true` | 일치 | |

---

## 2. 미구현/부분 구현 Gap 리스트

### 미구현 (Design에 있지만 없음)

| # | Design 항목 | Design 위치 | 심각도 | 설명 |
|:-:|-------------|-------------|:------:|------|
| G-1 | `IsNeedsAttention()` 메서드 | Design §4.4 | 낮 | 인터페이스에서 제거됨. 호출부 없음 (YAGNI 판단 타당) |
| G-2 | `AttentionRaisedAt` 프로퍼티 | Design §4.7 | 낮 | SessionInfo에 미구현. 현재 사용처 없음 |
| G-3 | `NotificationSettings` 모델 | Design §5.3, §11 | 중 | AppSettings에 `Notifications` 프로퍼티 없음. `RingEnabled`/`ToastEnabled` 토글 불가 |
| G-4 | `ISettingsService` 설정 검사 | Design §4.5 L269-270 | 중 | OscNotificationService에서 설정 확인 로직 없음 (`_settings.Current.Notifications.RingEnabled`) |
| G-5 | `AutomationProperties.AutomationId` | Design §5.1 | 중 | Ellipse에 `E2E_NotificationRing_{Id}` AutomationId 미설정 → E2E UIA 검증 불가 |
| G-6 | E2E Tier 3 `NotificationRingScenarios.cs` | Design §7.2 | 중 | 신규 테스트 파일 미생성. `Tier3_UiaProperty/` 에 `.gitkeep`만 존재 |
| G-7 | `gw_session_write()` C++ API | Design §7.1 | 미해당 | TestOnlyInjectBytes가 기존 `IEngineService.WriteSession`으로 구현 (Design의 별도 API 불필요) |

### 부분 구현 (Design과 차이)

| # | Design 항목 | Design 명세 | 구현 | 영향 |
|:-:|-------------|-------------|------|:----:|
| P-1 | `HandleOscEvent` 시그니처 | `(uint sessionId, int oscKind, string title, string body)` | `(uint sessionId, string title, string body)` — `oscKind` 제거 | 낮 |
| P-2 | `OscNotificationMessage` 필드 | `record(uint SessionId, int OscKind, string Title, string Body)` | `record(uint SessionId, string Title, string Body)` — `OscKind` 제거 | 낮 |
| P-3 | C++ 콜백 이름 | `VtOscNotifyFn` / `set_osc_notify_callback` | `VtDesktopNotifyFn` / `set_desktop_notify_callback` | 없음 |
| P-4 | `GwOscNotifyFn` 시그니처 | `int kind` 파라미터 포함 | `int kind` 파라미터 없음 | 낮 |
| P-5 | Toast 발사 위치 | `OscNotificationService.ShowToast()` 내부 | `App.xaml.cs` Messenger 구독 핸들러 | 없음 |
| P-6 | debounce 범위 | Design: 같은 세션 100ms | 구현: 전역 100ms (모든 세션 공유) | 낮 |

---

## 3. Design에 없지만 추가된 항목 (Extra)

| # | 항목 | 구현 위치 | 설명 |
|:-:|------|----------|------|
| E-1 | WorkspaceInfo `NeedsAttention` + `LastOscMessage` | `WorkspaceInfo.cs:34-37` | Design에는 SessionInfo만 명시. Workspace 레벨 전파 추가 |
| E-2 | `IWorkspaceService.FindWorkspaceBySessionId()` | `WorkspaceService.cs:231` | OSC 알림의 Workspace 전파용 |
| E-3 | `OscNotificationService`에 `IWorkspaceService` DI | `OscNotificationService.cs:10` | Workspace 레벨 dot 갱신용 |
| E-4 | ghostty `stream_terminal.zig` Effects 확장 | `stream_terminal.zig:81` | Design에 명시 없음. ghostty 코어 패치 |
| E-5 | ghostty `c/terminal.zig` 트램폴린 + Option=15 | `c/terminal.zig:49,85,283,320` | Design에 명시 없음. ghostty C API 확장 |
| E-6 | ghostty `terminal.h` C typedef | `terminal.h:411,567-569` | Design에 명시 없음. ghostty 헤더 패치 |
| E-7 | `conpty_session` pending buffer 패턴 | `conpty_session.cpp:279-286` | Design에 명시 없는 I/O 안전 버퍼링 |
| E-8 | `SessionManager.SetOscService()` 순환 의존 해결 | `SessionManager.cs:22-23` | Design에 없는 DI 구조 변경 |

---

## 4. 비교표: Design vs 구현

```
Design 명세 파일 수                        구현 파일 수
───────────────────────────               ──────────────────
C++ 5파일 (vt_bridge h/cpp,               C++ 8파일 (+conpty h/cpp,
    vt_core h/cpp, ghostwin_engine h/cpp)      session_manager h/cpp)
C# 8파일 (3 NEW + 5 수정)                 C# 9파일 (3 NEW + 6 수정)
WPF 2파일                                 WPF 2파일
E2E 3파일 (1 NEW)                         E2E 2파일 (0 NEW)
Settings 1파일                            Settings 0파일
─────────                                 ─────────
총 19파일 예상                             총 21파일 변경 + 2파일 미구현
```

| 관점 | Design 명세 | 실제 구현 |
|------|-------------|----------|
| 콜백 이름 패턴 | `VtOscNotifyFn` | `VtDesktopNotifyFn` (ghostty API 이름 준수) |
| `oscKind` 파라미터 | 전 계층에 전달 | 제거 (ghostty가 1종으로 통합하므로 불필요) |
| Toast 위치 | OscNotificationService 내부 | App.xaml.cs Messenger 구독 (SRP 분리) |
| 설정 토글 | `Notifications.RingEnabled/ToastEnabled` | 미구현 (항상 활성) |
| E2E 자동 검증 | Tier 3 NotificationRingScenarios | 미구현 |
| Workspace 전파 | 미명세 | 구현됨 (E-1, E-2, E-3) |

---

## 5. 위험 / 개선 제안

### 즉시 조치 필요 (Match Rate 향상)

| # | 항목 | 조치 | 우선순위 |
|:-:|------|------|:--------:|
| A-1 | `NotificationSettings` 모델 + AppSettings 확장 (G-3, G-4) | `AppSettings`에 `Notifications` 프로퍼티 추가, `OscNotificationService`에서 설정 확인 | 높 |
| A-2 | Ellipse `AutomationId` (G-5) | `AutomationProperties.AutomationId="{Binding Id, StringFormat=E2E_NotificationRing_{0}}"` 추가 | 높 |
| A-3 | E2E Tier 3 테스트 (G-6) | `NotificationRingScenarios.cs` 생성 (OscInjector → UIA dot 검증) | 중 |

### 문서 업데이트 필요 (Design 반영)

| # | 항목 | 변경 내용 |
|:-:|------|----------|
| D-1 | `oscKind` 파라미터 제거 | Design 전체에서 `int oscKind`/`int kind` 제거. ghostty가 1종으로 통합하므로 불필요 — 의도적 간소화 |
| D-2 | 콜백 이름 `DesktopNotify` | Design의 `OscNotify` → `DesktopNotify`로 C++ 레이어 명칭 정정 |
| D-3 | Toast 위치 변경 | Design §6.2를 App.xaml.cs Messenger 패턴으로 갱신 |
| D-4 | Workspace 전파 추가 (E-1~E-3) | Design §4.5에 WorkspaceInfo 전파 로직 추가 |
| D-5 | ghostty 패치 3파일 추가 (E-4~E-6) | Design §3에 ghostty 소스 수정 항목 명시 |
| D-6 | conpty pending buffer (E-7) | Design §3에 I/O thread 안전 버퍼링 패턴 추가 |

### 의도적 차이 (기록용)

| # | 항목 | 근거 |
|:-:|------|------|
| I-1 | `IsNeedsAttention()` 제거 | 호출부 없음. `SessionInfo.NeedsAttention` 직접 참조로 충분 (YAGNI) |
| I-2 | `AttentionRaisedAt` 미구현 | 현재 사용처 없음. Phase 6-B 알림 히스토리에서 필요 시 추가 |
| I-3 | debounce 전역 vs 세션별 | 전역 debounce가 더 단순하고 초당 100회 fuzz에도 충분. 세션별은 과설계 |

---

## 6. 매치율 산출 근거

```
전체 비교 항목: 32개
- 완전 일치: 18개
- 의도적 변경 (기능 동일): 6개 (P-1~P-6)
- 미구현: 5개 (G-1~G-6 중 G-7 제외)
- 추가 구현: 8개 (E-1~E-8, 페널티 없음)

매치율 = (18 + 6 x 0.8) / 32 x 100 ≈ 78%
```

**78%** → "상당 부분 일치하지만 설정 토글과 E2E 자동 검증이 빠져 있어 동기화 필요."

---

## 7. 동기화 옵션 제안

| 옵션 | 대상 | 작업량 |
|------|------|:------:|
| **A. 구현 보완** | G-3, G-4 (Settings), G-5 (AutomationId), G-6 (E2E Tier 3) | ~3-4시간 |
| **B. Design 갱신** | D-1~D-6 반영 | ~1시간 |
| **C. 양쪽 동시** (권장) | A + B | ~4-5시간 |

**권장: 옵션 C** — 구현 보완으로 매치율 90%+ 달성 후, Design도 실제 구현에 맞춰 갱신.

---

*End of Gap Analysis — Phase 6-A: OSC Hook + Notification Ring*
