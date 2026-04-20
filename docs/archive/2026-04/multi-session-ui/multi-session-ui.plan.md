# multi-session-ui Master Plan

> **Summary**: Phase 5 — CMUX 호환 제품 수준 터미널 완성. 다중 세션, 수직 탭 사이드바, Modern C++ 기반 설정 시스템, Pane 분할, 세션 복원.
>
> **Project**: GhostWin Terminal
> **Phase**: 5
> **Author**: 노수장 (Updated for CMUX Alignment & Modern C++)
> **Date**: 2026-04-05
> **Status**: Approved & Refined
> **Previous Phase**: Phase 4 (winui3-integration) — 전체 완료 + glyph-metrics 93%

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 현재 GhostWin은 단일 ConPTY 세션만 지원하며, 탭 전환·pane 분할·설정 조정이 불가능. 또한 향후 AI 에이전트(CMUX 패턴)를 수용할 아키텍처 기반이 부재함. |
| **Solution** | SessionManager로 세션 격리, 수직 탭 사이드바, Clean Architecture 기반 JSON 설정 시스템, Pane 분할 레이아웃 엔진, 세션 복원을 5개 Sub-Feature로 분리 구현. |
| **Function/UX Effect** | Ctrl+T로 새 탭, Ctrl+W로 닫기, Alt+H/V로 pane 분할. 설정 파일(JSON) 핫 리로드로 폰트·색상·에이전트 알림 등 실시간 조정, 재시작 시 레이아웃 복원. |
| **Core Value** | WT/Alacritty 대체 가능한 제품 수준 터미널이자, Phase 6 AI 에이전트 특화 기능을 완벽히 수용할 수 있는 CMUX 호환 멀티플렉서의 완성. |

---

## 1. User Intent Discovery

### 1.1 Core Purpose
**제품 수준 터미널 완성 및 CMUX 기반 마련** — 일상 사용 가능한 멀티세션 터미널 기능을 제공하며, 내부적으로는 AI 에이전트 알림 및 소켓 통신 설정을 수용할 수 있는 견고한 설정 시스템을 구축.

### 1.2 Target Users
- 개발자 (일상 터미널 대체)
- AI 에이전트 사용자 (Phase 6 기반)

### 1.3 Success Criteria
- [ ] 탭 추가/제거/전환 (Ctrl+T/W/Tab)
- [ ] 다중 ConPTY 독립 세션 (탭별 독립 셸)
- [ ] JSON 설정 파일 (`ghostwin.json`) 기반 핫 리로드 (CMUX 호환 파싱)
- [ ] Pane 분할 (수평/수직)
- [ ] 수직 탭 사이드바 (CWD, git 정보 등)
- [ ] 세션 복원 (CWD + 레이아웃)
- [ ] 드래그 탭 순서 변경

---

## 2. Constraints & Technical Principles

### 2.1 Technology Stack
- **Standard**: **Modern C++ (20/23)** - `std::jthread`, `std::shared_mutex`, `std::filesystem` 활용
- **Library**: `nlohmann/json` (Header-only)

### 2.2 Architectural Principles (CRITICAL)
- **Clean Architecture**: 도메인(Entity), 인터페이스(ISettingsProvider 등), 인프라 계층의 엄격한 분리
- **RAII**: 모든 리소스(핸들, 스레드, FileSystemWatcher)는 RAII를 통해 수명 관리
- **Dependency Injection (DI)**: 서브시스템은 구체 클래스가 아닌 인터페이스에 의존
- **Observer Pattern**: 설정 변경 시 각 서브시스템(Renderer, Sidebar 등)에 이벤트 통보

---

## 3. YAGNI Review & CMUX Alignment

### In Scope (Phase 5)
- [x] SessionManager (다중 ConPTY 격리)
- [x] 수직 탭 사이드바 + 탭 전환/추가/제거/드래그
- [x] CMUX 호환 설정 시스템 (`terminal`, `multiplexer`, `agent` 도메인 분리 및 JSON 핫 리로드)
- [x] Pane 분할 (수평/수직)
- [x] 세션 복원 (CWD + 레이아웃)

### Out of Scope (Phase 6+)
- **실제** OSC 훅 수신 처리 (9/777/99) → Phase 6
- **실제** Named Pipe 훅 서버 통신 구현 → Phase 6
- **실제** Notification Panel UI 및 데스크톱 Toast 발화 → Phase 6
- WebView2 인앱 브라우저 렌더링 → Phase 6+
- tmux on WSL 세션 attach → 2차 개선

*(참고: Phase 5에서는 설정 데이터를 로드하고 관리하는 '구조(SettingsManager)'만 마련하며, 실제 동작(Behavior)은 Phase 6에서 구현합니다.)*

---

## 4. Architecture

### 4.1 핵심 설계 결정

| 결정 | 선택 | 근거 |
|------|------|------|
| 렌더링 인프라 | **공유** (Atlas/Renderer/QuadBuilder) | 전역 폰트 설정, 메모리 효율 |
| 세션 로직 | **격리** (ConPTY/VTCore/RenderState/TSF) | 독립 셸 프로세스, 상태 분리 |
| 설정 아키텍처 | **Clean Architecture / Observer** | 강결합 제거, 인터페이스 기반 DI |
| 탭 위치 | **수직 사이드바** (좌측) | cmux 패턴. Phase 6 AI 알림 배지 확장 대비 |
| Sub-Feature 분리 | **5개 독립 PDCA** | Phase 4 성공 패턴 |

### 4.2 컴포넌트 다이어그램 (Observer & DI 기반)

```text
┌─────────────────────────────────────────────────────┐
│                    GhostWinApp                       │
│                                                     │
│  ┌──────────────────────────┐    ┌─────────────┐    │
│  │     SettingsManager      │    │ TabSidebar  │    │
│  │   (ISettingsProvider)    │◄──►│  - 탭 목록   │    │
│  │    JSON Watcher (RAII)   │    │  - CWD 표시  │    │
│  └─────────────┬────────────┘    └─────────────┘    │
│                │ Observer (DI)                      │
│                ▼                                    │
│  ┌──────────────────────────┐    ┌──────────────────────────┐
│  │  Shared Rendering Infra  │    │     SessionManager       │
│  │ DX11Renderer, QuadBuilder│    │  Session 0 ─┐           │
│  └──────────────────────────┘    │  Session 1 ─┤  Vector   │
│                                  │  Session N ─┘           │
│                                  │                         │
│  ┌──────────────────────────┐    │  각 Session:            │
│  │  PaneLayout (Phase 5-D)  │    │    ConPTY + VTCore       │
│  │ Tree<Pane> 분할, 중첩      │    │    + RenderState + TSF  │
│  └──────────────────────────┘    └──────────────────────────┘
└─────────────────────────────────────────────────────┘
```

### 4.3 Session 구조체

```cpp
struct Session {
    uint32_t id;
    std::unique_ptr<ConPtySession> conpty;
    std::unique_ptr<VTCore> vt;
    std::unique_ptr<TerminalRenderState> state;
    std::unique_ptr<TsfHandle> tsf;
    std::wstring cwd;           // 현재 작업 디렉토리
    std::wstring title;         // 탭 제목
    bool active = false;        // 렌더링 활성 여부
};
```

### 4.4 설정 시스템 (CMUX 호환 JSON)

```jsonc
// %APPDATA%/GhostWin/ghostwin.json
{
  "terminal": {
    "font": { "family": "JetBrainsMono NF", "size": 11.25 },
    "colors": { "theme": "catppuccin-mocha", "background": "#1e1e2e" }
  },
  "multiplexer": {
    "sidebar": { "visible": true, "width": 250, "show_git": true, "show_cwd": true },
    "behavior": { "auto_restore_layout": true }
  },
  "agent": {
    "socket": { "enabled": true, "path": "\\\\.\\pipe\\ghostwin", "mode": "process_only" },
    "notifications": { "ring_width": 2.0, "desktop_toast_enabled": true }
  },
  "keybindings": {
    "workspace.create": "Ctrl+T",
    "workspace.close": "Ctrl+W",
    "surface.split_horizontal": "Alt+H",
    "surface.split_vertical": "Alt+V"
  }
}
```

---

## 5. Sub-Feature Map (5개 독립 PDCA)

```
multi-session-ui (Master Plan)
├── A: session-manager       [독립]     다중 ConPTY 격리 + SessionManager
├── B: tab-sidebar           [A 이후]   수직 탭 사이드바 + 탭 전환/추가/드래그
├── C: settings-system       [독립]     Clean Arch 기반 JSON 설정 파싱 및 Observer 전파
├── D: pane-split            [A 이후]   수평/수직 Pane 분할 레이아웃 엔진
└── E: session-restore       [A+B+D]   CWD + 레이아웃 직렬화/복원
```

| ID | Feature | 의존성 | 예상 규모 | 상태 |
|----|---------|--------|-----------|:----:|
| A | session-manager | 없음 | 중 — Session struct, 다중 ConPTY 관리, 활성 세션 전환 | **95% 완료** |
| B | tab-sidebar | A 완료 | 대 — WinUI3 Code-only ListView, 드래그, CWD 표시, 단축키 | 대기 |
| C | settings-system | 없음 | 중 — Modern C++ 파서, RAII 워처, DI 인터페이스 | 대기 (설계 완료) |
| D | pane-split | A 완료 | 대 — Tree 레이아웃 엔진, 분할/리사이즈, 포커스 관리 | 대기 |
| E | session-restore | A+B+D | 소 — JSON 직렬화, 시작 시 복원 | 대기 |

### 구현 순서

```
Phase 5-A: SessionManager (기반)
    ↓
Phase 5-B: TabSidebar (세션 기반 UI)    Phase 5-C: Settings (독립, DI 제공자)
    ↓
Phase 5-D: PaneSplit (탭 내부 분할)
    ↓
Phase 5-E: SessionRestore (전체 완성 후)
```

---

## 6. Functional Requirements

### FR-01: SessionManager (Phase 5-A)
- 다중 Session 생성/삭제 및 활성 세션 전환 (렌더링 suspend 지원)
- 세션별 ConPTY + VTCore + RenderState + TSF 격리
- ADR-006 vt_mutex를 세션별로 확장

### FR-02: 탭 사이드바 (Phase 5-B)
- 좌측 수직 사이드바 (cmux 패턴)
- 탭 추가/닫기/전환 및 탭에 CWD 표시
- 드래그로 탭 순서 변경
- 마지막 탭 닫으면 앱 종료

### FR-03: Settings System (Phase 5-C)
- `%APPDATA%/GhostWin/ghostwin.json` 파싱 (CMUX 도메인 구조 준수)
- **Modern C++ / RAII**: `std::jthread` 및 `FileWatcherRAII`를 통한 파일 변경 감지 핫 리로드 (< 100ms)
- **Observer Pattern**: 변경 사항을 `ISettingsObserver` 인터페이스를 통해 각 서브시스템에 브로드캐스트
- 문법 오류 시 Fallback 유지 및 에러 로깅

### FR-04: GUI 설정 패널 (Phase 5-C 2차)
- *추후 확장을 위한 기반만 마련, 1차는 JSON 편집 위주*

### FR-05: Pane 분할 (Phase 5-D)
- 수평/수직 분할 (Tree<Pane> 레이아웃) 및 드래그 리사이즈
- 포커스 이동 (Alt+방향키) 및 Pane 닫기 시 재배치

### FR-06: 세션 복원 (Phase 5-E)
- 앱 종료 시 레이아웃 + CWD를 JSON으로 직렬화 및 재시작 시 복원

---

## 7. Non-Functional Requirements

| NFR | 목표 | 측정 방법 |
|-----|------|-----------|
| NFR-01 | 탭 전환 < 50ms | 시각 지연 없음 |
| NFR-02 | 세션 10개 이상 안정 동작 | 10탭 열고 30분 사용 |
| NFR-03 | 설정 리로드 < 100ms | JSON 변경 후 반영 시간 |
| NFR-04 | 메모리: 탭당 < 20MB 추가 | Task Manager 측정 |
| NFR-05 | 아키텍처 결합도 최소화 | UI/Renderer 코드가 nlohmann/json 헤더를 인클루드하지 않음 (DI 준수) |

---

## 8. Risks

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| WinUI3 Code-only로 ListView/드래그 구현 난이도 | 상 | 중 | WT의 TabView 구현 참고. 최소 기능 먼저 |
| 다중 세션 스레드 동기화 복잡도 | 상 | 중 | ADR-006 패턴 확장. 세션별 독립 mutex |
| FileSystemWatcher 및 다중 스레드 데드락 | 상 | 하 | `std::shared_mutex` 기반의 안전한 Reader/Writer Lock 적용 |
| Pane 분할 레이아웃 엔진 복잡도 | 중 | 중 | WT의 Pane::_Split 참고. Tree 구조 단순화 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-03 | Initial draft (Plan Plus brainstorming) | 노수장 |
| 0.2 | 2026-04-05 | CMUX Alignment, Modern C++, Clean Architecture 요구사항 반영 | AI Agent |
