# Tab Sidebar (Phase 5-B) Gap Analysis Report

> **분석 대상**: tab-sidebar (Phase 5-B)
> **디자인 문서**: `docs/02-design/features/tab-sidebar.design.md` (v1.4)
> **구현 경로**: `src/ui/`, `src/platform/`, `src/vt-core/`, `src/conpty/`, `src/app/`
> **분석 일자**: 2026-04-03

---

## Overall Scores

| 카테고리 | 점수 | 상태 |
|----------|:----:|:----:|
| 데이터 모델 (Section 3) | 95% | OK |
| TabSidebar 클래스 (Section 4) | 92% | OK |
| GhostWinApp 통합 (Section 5) | 82% | WARN |
| 키보드 단축키 (Section 6) | 100% | OK |
| 파일 구조 (Section 7) | 95% | OK |
| cpp.md 컴플라이언스 (Section 1.2.1) | 96% | OK |
| VT Bridge OSC API (Section 3.3) | 80% | WARN |
| **전체** | **91%** | **OK** |

---

## 1. 데이터 모델 (Section 3) - 95%

### 일치하는 항목

| 디자인 항목 | 구현 위치 | 상태 |
|-------------|-----------|:----:|
| TabItemData 구조체 (session_id, title, cwd_display, is_active) | `tab_sidebar.h:38-43` | OK |
| IObservableVector + single_threaded_observable_vector | `tab_sidebar.h:98-99`, `tab_sidebar.cpp:26` | OK |
| items_source_ + items_ 1:1 동기화 | `tab_sidebar.cpp:194,206-207` | OK |
| CanReorderItems(true) + AllowDrop(true) | `tab_sidebar.cpp:31-32` | OK |
| ShortenCwd 축약 규칙 (~, ~/Documents, 마지막 컴포넌트, C:\) | `cwd_query.cpp:126-155` | OK |
| GetProcessCwd (PEB ReadProcessMemory 3x) | `cwd_query.cpp:69-86` | OK |
| GetDeepestChildPid (CreateToolhelp32Snapshot) | `cwd_query.cpp:90-112` | OK |
| GetShellCwd (deepest child fallback) | `cwd_query.cpp:116-122` | OK |
| HandleGuard RAII 래퍼 | `cwd_query.h:18-31` | OK |

### 차이 항목

| 항목 | 디자인 | 구현 | 영향 | 심각도 |
|------|--------|------|------|:------:|
| ShortenCwd 파라미터 타입 | `const std::wstring&` (Section 3.2) | `std::wstring_view` (cwd_query.h:54) | 개선 — cpp.md wstring_view 규칙 준수 | LOW (구현이 더 우수) |

---

## 2. TabSidebar 클래스 (Section 4) - 92%

### 일치하는 항목

| 디자인 항목 | 구현 위치 | 상태 |
|-------------|-----------|:----:|
| Public API 7개 제한 | `tab_sidebar.h:65-71` (initialize, root, request_new_tab, request_close_active, update_dpi, toggle_visibility, is_visible) | OK |
| friend class GhostWinApp | `tab_sidebar.h:80` | OK |
| SelectionGuard RAII | `tab_sidebar.h:105-109` | OK |
| Private on_* 핸들러 5개 | `tab_sidebar.h:82-86` | OK |
| setup_listview/setup_add_button 분리 | `tab_sidebar.cpp:25-57` | OK |
| create_text_panel/create_close_button/create_tab_item_ui 분리 | `tab_sidebar.cpp:119-178` | OK |
| kBaseWidth = 220.0 | `tab_sidebar.h:113` | OK |
| DPI-aware sidebar_width (round formula) | `tab_sidebar.cpp:18-21` | OK |
| Non-copyable 명시 삭제 | `tab_sidebar.h:74-76` | OK |

### 차이 항목

| 항목 | 디자인 | 구현 | 영향 | 심각도 |
|------|--------|------|------|:------:|
| initialize() 시그니처 | `initialize(float dpi_scale, SessionManager* mgr, NewTabFn fn, void* ctx)` — 개별 파라미터 4개 | `initialize(const TabSidebarConfig& config)` — 구조체 1개 | cpp.md "parameters <= 3" 규칙을 구조체로 더 엄격히 준수 | LOW (구현이 더 우수) |
| root() 반환 타입 | `winui::UIElement` | `winui::FrameworkElement` | FrameworkElement는 UIElement의 서브타입. Grid::SetColumn은 UIElement를 받으므로 호환됨 | LOW |
| update_active_highlight() 별도 메서드 | Section 4.1에 `update_active_highlight(SessionId)` 선언 | 구현에서 제거됨 — rebuild_list()가 create_tab_item_ui() 재생성으로 대체 | 디자인 대비 단순화. rebuild_list가 활성 탭 하이라이트를 포함하므로 기능 동일 | LOW |
| on_activated에서 rebuild_list 호출 | 디자인은 `update_active_highlight(id)` 호출 | 구현은 `rebuild_list()` 호출 (tab_sidebar.cpp:224) | 전체 리스트 재구성이라 비효율적일 수 있으나, 탭 수가 10개 이하이므로 실질 영향 없음 | LOW |
| create_text_panel cwd 빈 문자열 처리 | 디자인은 cwd_block을 항상 생성 | 구현은 `if (!data.cwd_display.empty())` 조건 추가 (tab_sidebar.cpp:131) | 개선 — 빈 CWD일 때 불필요한 TextBlock 생성 방지 | LOW (구현이 더 우수) |
| NewTabFn 타입 별칭 | 디자인: `using NewTabFn = void(*)(void* ctx);` (public typedef) | 구현: 별칭 없이 `void(*new_tab_fn)(void* ctx)` 직접 선언 (TabSidebarConfig 내부) | 사소한 스타일 차이 | LOW |
| update_item DRY 템플릿 | 디자인에 없음 | `tab_sidebar.h:124-125`, `tab_sidebar.cpp:228-237` | 추가 개선 — on_title_changed/on_cwd_changed 중복 제거 | LOW (구현이 더 우수) |

---

## 3. GhostWinApp 통합 (Section 5) - 82%

### 일치하는 항목

| 디자인 항목 | 구현 위치 | 상태 |
|-------------|-----------|:----:|
| Grid col0=Auto, col1=Star | `winui_app.cpp:538-545` | OK |
| grid.UseLayoutRounding(true) | `winui_app.cpp:535` | OK |
| m_tab_sidebar 멤버 | `winui_app.h:64` | OK |
| create_new_session 추출 | `winui_app.cpp:493-509` | OK |
| m_sidebar_col 멤버 | `winui_app.h:99` | OK |
| SessionEvents.on_created → on_session_created | `winui_app.cpp:1594-1597` | OK |
| SessionEvents.on_closed → on_session_closed + 앱 종료 | `winui_app.cpp:1598-1605` | OK |
| SessionEvents.on_activated → on_session_activated | `winui_app.cpp:1606-1609` | OK |
| SessionEvents.on_child_exit → DispatcherQueue → close_session | `winui_app.cpp:1610-1616` | OK |
| Ctrl+T → m_tab_sidebar.request_new_tab() | `winui_app.cpp:317-321` | OK |
| Ctrl+W → m_tab_sidebar.request_close_active() | `winui_app.cpp:324-328` | OK |

### 차이 항목 (Missing Features)

| 항목 | 디자인 위치 | 설명 | 영향 | 심각도 |
|------|-------------|------|------|:------:|
| on_title_changed SessionEvent 미연결 | Section 5.2 (L731-737) | 디자인은 `events.on_title_changed`를 DispatcherQueue로 UI 스레드 전환하여 `m_tab_sidebar.on_title_changed()` 호출 설계. 구현에서 **미등록** — `winui_app.cpp:1592-1617`에 on_title_changed/on_cwd_changed 콜백 설정 코드 없음 | 탭 제목이 실시간 업데이트되지 않음 | **HIGH** |
| on_cwd_changed SessionEvent 미연결 | Section 5.2 (L738-744) | 위와 동일. CWD 변경이 탭에 반영되지 않음 | 탭 CWD 서브텍스트 미표시 | **HIGH** |
| m_cwd_poll_timer (PEB 2초 폴링) | Section 5.4 | 디자인은 `DispatcherTimer m_cwd_poll_timer`로 2초 PEB 폴링 + `poll_cwd()` 메서드 설계. 구현에서 **미구현** — `winui_app.h`에 타이머/메서드 없음 | OSC 미지원 셸(cmd.exe 등)에서 CWD 표시 불가 | **MEDIUM** |
| SessionManager poll_titles_and_cwd() | Section 3.3.3 (L366-369) | `session_manager.h/cpp`에 title/CWD 폴링 로직 추가 설계. 구현에서 **미구현** | PEB 폴링 인프라 부재 | **MEDIUM** |

---

## 4. 키보드 단축키 (Section 6) - 100%

모든 설계 단축키가 정확히 구현됨.

| 단축키 | 디자인 | 구현 위치 | 상태 |
|--------|--------|-----------|:----:|
| Ctrl+T | request_new_tab() | `winui_app.cpp:317-321` | OK |
| Ctrl+W | request_close_active() | `winui_app.cpp:324-328` | OK |
| Ctrl+Tab | activate_next | `winui_app.cpp:331-336` | OK |
| Ctrl+Shift+Tab | activate_prev | `winui_app.cpp:333` | OK |
| Ctrl+1~9 | id_at(n-1) → activate | `winui_app.cpp:339-347` | OK |
| Ctrl+Shift+PageUp/Down | move_session | `winui_app.cpp:358-373` | OK |
| Ctrl+Shift+B | toggle_visibility | `winui_app.cpp:350-355` | OK |
| Ctrl+9 마지막 탭 폴백 | WT 동작 참조 | `winui_app.cpp:344-345` | OK |

---

## 5. 파일 구조 (Section 7) - 95%

### 일치하는 항목

| 파일 | 디자인 | 구현 | 상태 |
|------|--------|------|:----:|
| `src/ui/tab_sidebar.h` | 신규 (~90 LOC) | 132줄 | OK |
| `src/ui/tab_sidebar.cpp` | 신규 (~300 LOC) | 273줄 | OK |
| `src/platform/cwd_query.h` | 신규 (~30 LOC) | 56줄 | OK |
| `src/platform/cwd_query.cpp` | 신규 (~140 LOC) | 157줄 | OK |
| `src/app/winui_app.h` 수정 | TabSidebar 멤버 + m_sidebar_col | OK | OK |
| `src/app/winui_app.cpp` 수정 | OnLaunched 통합 + 단축키 정식화 | OK | OK |
| `src/vt-core/vt_bridge.h` 수정 | OSC 콜백 API 추가 | OK | OK |
| `src/vt-core/vt_bridge.c` 수정 | OSC 콜백 구현 | OK | OK |
| `src/vt-core/vt_core.h` 수정 | set_title_callback, get_title, get_pwd | OK | OK |
| `src/vt-core/vt_core.cpp` 수정 | 위 메서드 구현 | OK | OK |
| `src/conpty/conpty_session.h` 수정 | child_pid() 접근자 | OK | OK |
| `src/conpty/conpty_session.cpp` 수정 | child_pid() 구현 | OK | OK |
| `CMakeLists.txt` 수정 | tab_sidebar.cpp + cwd_query.cpp 추가 | `CMakeLists.txt:157-158` | OK |

### 차이 항목

| 항목 | 디자인 | 구현 | 영향 | 심각도 |
|------|--------|------|------|:------:|
| `src/platform/` 위치 | 디자인 초기(Section 3.3)에서 `src/ui/cwd_query.cpp` → v1.4에서 `src/platform/`으로 이동 | `src/platform/cwd_query.h/cpp` | 완전 일치 (v1.4 기준) | - |
| session_manager.h/cpp 수정 | poll_titles_and_cwd + fire_title_event/fire_cwd_event 추가 예정 | **미구현** (Section 3의 gap과 동일) | PEB 폴링 인프라 부재 | MEDIUM |

---

## 6. cpp.md 컴플라이언스 (Section 1.2.1) - 96%

| 규칙 | 설계 요구 | 구현 상태 | 상태 |
|------|-----------|-----------|:----:|
| RAII SelectionGuard | 생성자 set / 소멸자 reset | `tab_sidebar.h:106-109` | OK |
| RAII HandleGuard | HANDLE 자동 해제 | `cwd_query.h:18-31` | OK |
| Rule of Zero | 복사 삭제 명시, 이동/소멸 컴파일러 생성 | `tab_sidebar.h:74-76` | OK |
| Public 메서드 <= 7 | 7개 | 7개 (initialize, root, request_new_tab, request_close_active, update_dpi, toggle_visibility, is_visible) | OK |
| 함수 본문 <= 40줄 | 모든 함수 분리 | 최대 함수: on_session_created ~16줄, rebuild_list ~10줄 | OK |
| 매개변수 <= 3 | TabSidebarConfig 구조체 사용 | `tab_sidebar.h:46-51` | OK |
| Lambda [this] 캡처 | 소유 관계로 안전 | 모든 람다에 Lifetime 주석 | OK |
| 함수 포인터 패턴 | std::function 사용 금지 | `void(*new_tab_fn)(void* ctx)` 패턴 | OK |
| Include 순서 (IWYU) | standard -> third-party -> project | `tab_sidebar.cpp:5-12`, `cwd_query.cpp:6-12` | OK |
| wstring_view 사용 | ShortenCwd 파라미터 | `cwd_query.h:54` | OK |
| constexpr over #define | kBaseWidth, PEB offsets | `tab_sidebar.h:113`, `cwd_query.cpp:25-29` | OK |

### 미세 차이

| 항목 | 설명 | 심각도 |
|------|------|:------:|
| `tab_sidebar.h` include 순서 | `<vector>` (standard) 다음에 WinRT headers (third-party) 다음에 project. 올바름 | - |
| `cwd_query.cpp` include 순서 주석 | "project first (cwd_query.h includes windows.h)" — 자체 헤더 우선 규칙은 IWYU 변형. 주석으로 근거 설명됨 | LOW |

---

## 7. VT Bridge OSC API (Section 3.3) - 80%

### 일치하는 항목

| 디자인 항목 | 구현 위치 | 상태 |
|-------------|-----------|:----:|
| VtTitleChangedFn 콜백 타입 | `vt_bridge.h:162` | OK |
| vt_bridge_set_title_callback | `vt_bridge.h:166`, `vt_bridge.c:363-375` | OK |
| vt_bridge_get_title (UTF-8) | `vt_bridge.h:172`, `vt_bridge.c:377-386` | OK |
| vt_bridge_get_pwd (OSC 7) | `vt_bridge.h:178`, `vt_bridge.c:388-397` | OK |
| VtCore::set_title_callback | `vt_core.h:118` | OK |
| VtCore::get_title / get_pwd | `vt_core.h:121-123`, `vt_core.cpp:189-205` | OK |

### 차이 항목

| 항목 | 디자인 | 구현 | 영향 | 심각도 |
|------|--------|------|------|:------:|
| 콜백 구조체 VtOscCallbacks | Section 3.3.1에서 `VtOscCallbacks { on_title_changed, on_cwd_changed, userdata }` 구조체 설계 | `VtTitleChangedFn` 단일 콜백 + `vt_bridge_set_title_callback()` 개별 등록 | 디자인 대비 단순화. CWD는 별도 콜백 없이 get_pwd()로 폴링 | LOW |
| vt_bridge_set_osc_callbacks 통합 API | 디자인에 있음 | 개별 set_title_callback으로 대체 | 기능 동등 | LOW |
| OSC 9;9 (WT 호환) 파싱 | 경로 B: WT 호환 CWD 경로 | **미구현** — libghostty OSC 9;9 서브커맨드 미지원 | PowerShell prompt의 OSC 9;9 미감지. PEB 폴링으로 폴백 가능 | **MEDIUM** |
| I/O thread에서 title 콜백 → UI thread 전환 | Section 5.2에서 DispatcherQueue 경유 설계 | winui_app.cpp에서 on_title_changed/on_cwd_changed **미연결** (Section 3 gap 재확인) | title/CWD 콜백이 도달해도 UI에 반영 안 됨 | **HIGH** |

---

## 종합 Gap Summary

### Missing Features (디자인 O, 구현 X)

| ID | 항목 | 디자인 위치 | 설명 | 심각도 |
|----|------|-------------|------|:------:|
| M-01 | on_title_changed SessionEvent 연결 | Section 5.2, L731-737 | DispatcherQueue로 UI 스레드 전환 후 TabSidebar::on_title_changed() 호출 | **HIGH** |
| M-02 | on_cwd_changed SessionEvent 연결 | Section 5.2, L738-744 | DispatcherQueue로 UI 스레드 전환 후 TabSidebar::on_cwd_changed() 호출 | **HIGH** |
| M-03 | m_cwd_poll_timer (PEB 2초 폴링) | Section 5.4 | OSC 미지원 셸 대응 DispatcherTimer + poll_cwd() | **MEDIUM** |
| M-04 | SessionManager::poll_titles_and_cwd() | Section 3.3.3 | fire_title_event / fire_cwd_event 메서드 | **MEDIUM** |
| M-05 | OSC 9;9 (WT 호환 CWD) 파싱 | Section 3.3.2, 경로 B | libghostty OSC 9 서브커맨드 처리 | **MEDIUM** |

### Added Features (디자인 X, 구현 O)

| ID | 항목 | 구현 위치 | 설명 | 심각도 |
|----|------|-----------|------|:------:|
| A-01 | TabSidebarConfig 구조체 | `tab_sidebar.h:46-51` | 디자인은 개별 파라미터, 구현은 config 구조체. cpp.md params<=3 더 엄격히 준수 | LOW (개선) |
| A-02 | update_item 템플릿 헬퍼 | `tab_sidebar.h:124-125` | on_title_changed/on_cwd_changed DRY 개선 | LOW (개선) |
| A-03 | create_text_panel 빈 CWD 조건 분기 | `tab_sidebar.cpp:131` | CWD 없을 때 TextBlock 미생성 | LOW (개선) |

### Changed Features (디자인 != 구현)

| ID | 항목 | 디자인 | 구현 | 영향 |
|----|------|--------|------|------|
| C-01 | initialize() 시그니처 | 4개 개별 파라미터 | TabSidebarConfig 구조체 | LOW (개선) |
| C-02 | root() 반환 타입 | UIElement | FrameworkElement | LOW (호환) |
| C-03 | update_active_highlight 제거 | 별도 메서드 | rebuild_list()로 통합 | LOW (단순화) |
| C-04 | VtOscCallbacks 구조체 | 통합 구조체 | 개별 set_title_callback | LOW (동등) |

---

## 권장 조치

### 즉시 조치 (HIGH)

1. **M-01/M-02**: `winui_app.cpp` StartTerminal() 내 SessionEvents 설정에 on_title_changed / on_cwd_changed 콜백 추가. 디자인 Section 5.2 코드 블록 참조. I/O 스레드 -> DispatcherQueue.TryEnqueue -> UI 스레드 전환 필수.

```cpp
events.on_title_changed = [](void* ctx, SessionId id, const std::wstring& title) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    std::wstring t = title;
    app->m_window.DispatcherQueue().TryEnqueue([app, id, t = std::move(t)]() {
        app->m_tab_sidebar.on_title_changed(id, t);
    });
};
events.on_cwd_changed = [](void* ctx, SessionId id, const std::wstring& cwd) {
    auto* app = static_cast<GhostWinApp*>(ctx);
    std::wstring c = cwd;
    app->m_window.DispatcherQueue().TryEnqueue([app, id, c = std::move(c)]() {
        app->m_tab_sidebar.on_cwd_changed(id, c);
    });
};
```

### 후속 조치 (MEDIUM)

2. **M-03/M-04**: PEB CWD 2초 폴링 타이머 구현. `winui_app.h`에 `m_cwd_poll_timer` + `poll_cwd()` 추가. SessionManager에 fire_cwd_event 인프라 추가.

3. **M-05**: OSC 9;9 파싱은 libghostty 내부 수정이 필요하므로 Phase 6으로 연기 가능. 현재 OSC 7 + PEB 폴링 조합으로 대부분의 셸 커버 가능.

### 디자인 문서 업데이트

4. **C-01**: initialize() 시그니처를 TabSidebarConfig 구조체 패턴으로 문서 반영 (구현이 더 우수).
5. **C-03**: update_active_highlight 제거를 문서에 반영 (rebuild_list 통합 결정).
6. **A-02**: update_item 템플릿 헬퍼를 문서에 반영.

---

## Match Rate 산출 근거

| 영역 | 가중치 | 점수 | 가중 점수 |
|------|:------:|:----:|:---------:|
| 데이터 모델 (Section 3) | 15% | 95% | 14.25 |
| TabSidebar 클래스 (Section 4) | 25% | 92% | 23.00 |
| GhostWinApp 통합 (Section 5) | 25% | 82% | 20.50 |
| 키보드 단축키 (Section 6) | 10% | 100% | 10.00 |
| 파일 구조 (Section 7) | 10% | 95% | 9.50 |
| cpp.md 컴플라이언스 | 10% | 96% | 9.60 |
| VT Bridge OSC API | 5% | 80% | 4.00 |
| **합계** | **100%** | | **90.85%** |

반올림: **91%**

---

## 결론

디자인과 구현의 전체 Match Rate는 **91%**로, 양호한 수준이다.

핵심 UI 구조(TabSidebar 클래스, 탭 아이템 UI, ListView 이벤트, 키보드 단축키)는 디자인과 거의 완벽히 일치하며, 일부 구현이 디자인보다 더 나은 패턴을 적용했다 (TabSidebarConfig 구조체, update_item DRY 템플릿, 빈 CWD 조건 분기).

**주요 Gap은 title/CWD 실시간 반영 파이프라인의 마지막 단계**이다. VT Bridge에 OSC title/CWD API가 이미 구현되어 있으나, GhostWinApp의 SessionEvents에서 on_title_changed/on_cwd_changed 콜백이 연결되지 않아 UI까지 전달되지 않는다. 이 2개 콜백 연결만으로 title 실시간 반영이 즉시 동작할 수 있다.

PEB CWD 폴링 타이머(M-03/M-04)는 OSC 미지원 셸(cmd.exe) 대응용이므로 우선순위는 중간이다.
