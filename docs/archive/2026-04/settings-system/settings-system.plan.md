# settings-system Plan (Revised for CMUX Alignment)

> **Summary**: JSON 설정 시스템 — 하드코딩 제거 및 CMUX 호환 멀티플렉서/AI 에이전트 환경 설정 시스템. `ghostwin.json`을 통해 터미널 외형, 워크스페이스 레이아웃, 에이전트 알림(Notification Ring) 및 Socket API 동작을 제어.
>
> **Project**: GhostWin Terminal
> **Author**: 노수장 (Refined by AI Agent)
> **Date**: 2026-04-05
> **Status**: Approved & Refined
> **Dependency**: Phase 4 (WinUI3 + Multiplexer), Hook Server
> **Parent**: Phase 5 multi-session-ui Master Plan — Sub-Feature D

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 기존 터미널 설정의 하드코딩 문제와 더불어, CMUX 스타일의 에이전트 연동 기능(알림 링, 소켓 API, 사이드바 메타데이터)을 제어할 중앙화된 설정 구조가 부재함. |
| **Solution** | `%APPDATA%/GhostWin/ghostwin.json` 단일 JSON 파일에 모든 설정을 외부화. CMUX 도메인(`terminal`, `multiplexer`, `agent`)에 맞춰 구조를 분리하고 `FileSystemWatcher`로 런타임 핫 리로드 지원. |
| **Function/UX Effect** | AI 에이전트의 상태(Notification Ring) 색상, 사이드바 정보(Git/Port/PR) 노출 여부, 소켓 API 권한 등을 즉각 변경 가능. (재빌드 또는 앱 재시작 불필요) |
| **Core Value** | Windows 네이티브 환경에서 CMUX 수준의 에이전트/멀티플렉서 커스터마이징 경험 제공 및 향후 GUI 설정 패널의 데이터 기반 마련. |

---

## 1. User Intent Discovery

### 1.1 Core Purpose
**하드코딩 제거 + CMUX 기능 런타임 커스터마이징** — 터미널 렌더링 설정(폰트 등) 외부화는 물론, 리서치된 CMUX 핵심 UX(알림 링, 데스크톱 알림 억제, Socket API 보안, 사이드바 실시간 메타데이터)를 JSON으로 제어 가능하게 함.

### 1.2 Success Criteria
- [ ] 앱 시작 시 `ghostwin.json` 파싱 후 도메인별 설정 객체 로드
- [ ] 잘못된 JSON 파싱 시 fallback 유지 및 UI 에러 알림
- [ ] JSON 변경 시 런타임 즉시 적용 (< 100ms 파싱 및 반영)
- [ ] CMUX 호환 액션셋 (`workspace.*`, `surface.*`, `notification.*`, `browser.*`) 키바인딩 매핑
- [ ] 사이드바 정보(Git, Port, PR)의 개별 토글 기능 지원

---

## 2. Constraints & Technical Principles

### 2.1 Technology Stack
- **Library**: `nlohmann/json` (Header-only, ADL serialization)
- **Standard**: **Modern C++ (20/23)** - `std::jthread`, `std::shared_mutex`, `std::filesystem` 적극 활용

### 2.2 Architectural Principles (CRITICAL)
- **Clean Architecture**: 도메인(Entity), 인터페이스, 인프라 계층의 엄격한 분리 (인터페이스 기반 DI)
- **RAII**: 모든 시스템 리소스(File Handle, Thread, FileSystemWatcher)는 RAII를 통해 수명 주기 관리
- **Patterns**:
    - **Observer Pattern**: 설정 변경 시 각 서브시스템(Renderer, Sidebar 등)에 즉시 통보
    - **Command Pattern**: Action ID 기반 키바인딩 매핑 및 실행
- **Extensibility**: C 스타일의 절차적 코드와 전역 변수 지양, 인터페이스 기반의 재사용 가능한 객체 지향 설계

---

## 3. Alternatives Explored & CMUX Alignment

| Approach | 설명 | 선택 가부 |
|----------|------|:----:|
| **A: 단일 JSON (GhostWin 패턴)** | `ghostwin.json` 하나에 계층형으로 섹션 구분 | **채택 (단순성/효율성)** |
| **B: CMUX 2-tier 패턴** | `.conf`(렌더링) + `.json`(앱/에이전트) 분리 | 기각 (자체 엔진이므로 통합 가능) |

**결정 근거**: CMUX는 터미널 엔진(libghostty)과 앱 계층이 분리되어 있어 설정 파일도 나뉘었으나, GhostWin은 자체 렌더링 스택(DX11)을 사용하므로 **단일 파일 내에서 논리적 섹션 분리**만으로 충분함.

---

## 3. CMUX 기능 기반 설정 범위 (In Scope)

### 1. Terminal (전통적 설정)
- 폰트(가족, 크기, 안티앨리어싱), 커서 스타일(Block/Bar/Underline, 깜빡임)
- 10개 내장 테마 (Catppuccin, Dracula 등) 및 개별 색상 오버라이드
- 창 패딩, Mica/Acrylic 투명도 효과

### 2. Multiplexer (워크스페이스/사이드바)
- 사이드바 가시성 및 폭(Width)
- **사이드바 메타데이터 토글**: `show_git`, `show_ports`, `show_pr`, `show_cwd`, `show_latest_alert`
- 세션 복원 정책 (`auto_restore`): 레이아웃(`layout`), 작업 디렉토리(`cwd`), 터미널 스크롤백(`scrollback`), 인앱 브라우저 히스토리(`browser_history`)

### 3. Agent & Notifications (CMUX 핵심)
- **Socket API**: Named Pipe 경로, 접근 권한 모드 (`off`, `process_only`, `allow_all`)
- **Agent States & Notifications**: 
  - 상태별 색상: 대기 중(`waiting`), 실행 중(`running`), 에러(`error`), 완료(`completed`)
  - Notification Ring(테두리 하이라이트) 굵기
  - Notification Panel (알림 패널): 위치 및 자동 숨김 설정
  - 데스크톱 알림(Toast): 활성화 여부 및 포커스 시 억제(`suppress_when_focused`) 기능
- **In-app Browser (WebView2)**: 브라우저 활성화 여부, 에이전트 자동화 권한 모드
- **Progress**: `set-progress` 명령으로 표시되는 진행률 표시줄(Progress bar) 색상 및 활성화 옵션 설정

### 4. Keybindings
- CMUX API 명칭을 따르는 액션 매핑 (`notification.toggle_panel`, `notification.jump_unread`, `surface.split_right` 등)

---

## 4. Architecture

### 4.1 데이터 흐름
`ghostwin.json` 저장 → `ReadDirectoryChangesW` 감지 → `SettingsManager` 파싱 → `ChangedFlags` 브로드캐스트 → UI/Renderer/HookServer 즉시 반영.

### 4.2 JSON 구조 명세 예시

```jsonc
{
  "terminal": {
    "font": { "family": "JetBrainsMono NF", "size": 11.25 },
    "colors": { "theme": "catppuccin-mocha", "background_opacity": 0.85 },
    "cursor": { "style": "block", "blinking": true }
  },
  "multiplexer": {
    "sidebar": {
      "visible": true,
      "width": 250,
      "show_git": true,
      "show_ports": true,
      "show_pr": true,
      "show_cwd": true,
      "show_latest_alert": true
    },
    "behavior": {
      "auto_restore": {
        "layout": true,
        "cwd": true,
        "scrollback": false,
        "browser_history": false
      }
    }
  },
  "agent": {
    "socket": { "enabled": true, "path": "\\\\.\\pipe\\ghostwin", "mode": "process_only" },
    "notifications": {
      "ring_width": 2.5,
      "colors": {
        "waiting": "#89b4fa",
        "running": "#a6e3a1",
        "error": "#f38ba8",
        "completed": "#a6adc8"
      },
      "panel": { "position": "right", "auto_hide": false },
      "desktop_toast": { "enabled": true, "suppress_when_focused": true }
    },
    "progress": { "visible": true, "color": "#f9e2af" },
    "browser": { "enabled": true, "automation_allowed": true }
  },
  "keybindings": {
    "workspace.create": "Ctrl+T",
    "notification.toggle_panel": "Ctrl+Shift+I",
    "notification.jump_unread": "Ctrl+Shift+U",
    "browser.open_current_url": "Ctrl+Shift+B"
  }
}
```

---

## 5. Implementation Roadmap

1. **Step 1**: `nlohmann/json` 통합 및 `AppSettings` 구조체 정의
2. **Step 2**: `SettingsManager` (Load/Save/Hot-Reload) 구현
3. **Step 3**: 10종 내장 테마 (`builtin_themes.h`) 정의
4. **Step 4**: `KeyMap` 시스템 구현 (Action ID 기반 디스패치)
5. **Step 5**: 각 서브시스템(DX11 Renderer, Sidebar ViewModel, Hook Server) 연동

---

## 6. Functional & Non-Functional Requirements

- **FR-01**: CMUX 도메인별 구조화 파싱 지원
- **FR-02**: 문법 오류 시 Fallback 유지 및 에러 Toast 노출
- **NFR-01**: 설정 로드 시간 < 10ms
- **NFR-02**: 런타임 리로드 반영 시간 < 100ms
- **NFR-03**: 에디터 저장 시 중복 트리거 방지를 위한 200ms Debounce 적용

---

## 7. Risks & Mitigation

| Risk | Impact | Mitigation |
|------|--------|------------|
| 잘못된 JSON 파싱 크래시 | 상 | try-catch 블록 및 유효성 검증 스키마 적용 |
| 폰트 변경 시 아틀라스 재생성 깜빡임 | 중 | 더블 버퍼링 기법으로 텍스처 교체 |
| 키바인딩 충돌 | 하 | 중복 키 선언 시 마지막 설정 우선 적용 및 로그 경고 |

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-05 | Initial draft | 노수장 |
| 0.2 | 2026-04-05 | CMUX 리서치 기반 사이드바/브라우저/알림 설정 보강 및 구조 정리 | AI Agent |

---

## Brainstorming Log

- **Q: 왜 YAML이나 TOML이 아닌 JSON인가?** -> A: Windows 앱 생태계(Windows Terminal, VS Code)에서 JSON이 표준이며, nlohmann/json 라이브러리의 성능과 편의성이 뛰어남.
- **Q: 사이드바 메타데이터 폴링 주기는?** -> A: Git(1s), Port(5s), PR(30s) 정도로 설정하되, 설정 파일에서 이 주기를 조정할 수 있는 기능은 YAGNI로 판단하여 제외.
