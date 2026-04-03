# multi-session-ui Master Plan

> **Summary**: Phase 5 — 제품 수준 터미널 완성. 다중 세션, 수직 탭 사이드바, 설정 시스템, Pane 분할, 세션 복원.
>
> **Project**: GhostWin Terminal
> **Phase**: 5
> **Author**: 노수장
> **Date**: 2026-04-03
> **Status**: Draft
> **Previous Phase**: Phase 4 (winui3-integration) — 전체 완료 + glyph-metrics 93%

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | 현재 GhostWin은 단일 ConPTY 세션만 지원. 탭 전환·pane 분할·설정 조정이 불가능하여 일상 사용 터미널로 부족 |
| **Solution** | SessionManager로 세션 격리, 수직 탭 사이드바(cmux 패턴), JSON+GUI 설정 시스템, Pane 분할 레이아웃 엔진, 세션 복원을 5개 Sub-Feature로 분리 구현 |
| **Function/UX Effect** | Ctrl+T로 새 탭, Ctrl+W로 닫기, Alt+H/V로 pane 분할, 설정 패널에서 폰트·색상·간격 실시간 조정, 재시작 시 레이아웃 복원 |
| **Core Value** | WT/Alacritty 대체 가능한 제품 수준 터미널. Phase 6 AI 에이전트 특화의 기반 |

---

## 1. User Intent Discovery

### 1.1 Core Purpose
**제품 수준 터미널 완성** — 일상 사용 가능한 멀티세션 터미널로 WT/Alacritty를 대체할 수준.

### 1.2 Target Users
- 개발자 (일상 터미널 대체)
- AI 에이전트 사용자 (Phase 6 기반)

### 1.3 Success Criteria
- [ ] 탭 추가/제거/전환 (Ctrl+T/W/Tab)
- [ ] 다중 ConPTY 독립 세션 (탭별 독립 셸)
- [ ] JSON 설정 파일 + GUI 설정 패널
- [ ] Pane 분할 (수평/수직)
- [ ] 수직 탭 사이드바 (CWD, git 정보)
- [ ] 세션 복원 (CWD + 레이아웃)
- [ ] 드래그 탭 순서 변경

---

## 2. Alternatives Explored

| Approach | 설명 | 선택 |
|----------|------|:----:|
| **A: Session-First** | 세션 격리 → 탭 UI → 설정 → Pane → 복원. Sub-Feature 분리 패턴. | **채택** |
| B: UI-First | 사이드바 UI → 세션 연결. 시각 결과 빠르나 리팩토링 필요. | — |
| C: All-in-One | 전부 하나의 PDCA. 범위 과대. | — |

**채택 근거**: Phase 4에서 7개 Sub-Feature 분리로 성공 (평균 96% match rate). 동일 패턴 적용.

---

## 3. YAGNI Review

### In Scope (Phase 5)
- [x] SessionManager (다중 ConPTY 격리)
- [x] 수직 탭 사이드바 + 탭 전환/추가/제거/드래그
- [x] JSON 설정 파일 + GUI 설정 패널
- [x] Pane 분할 (수평/수직)
- [x] 세션 복원 (CWD + 레이아웃)

### Out of Scope (Phase 6+)
- OSC 훅 수신 (9/777/99) → Phase 6
- Named Pipe 훅 서버 → Phase 6
- Notification Ring + 알림 패널 → Phase 6
- git/PR 상태 사이드바 → Phase 6
- WebView2 인앱 브라우저 → Phase 6+
- 탭별 독립 폰트/테마 → 2차 개선
- tmux on WSL 세션 attach → 2차 개선

---

## 4. Architecture

### 4.1 핵심 설계 결정

| 결정 | 선택 | 근거 |
|------|------|------|
| 렌더링 인프라 | **공유** (Atlas/Renderer/QuadBuilder) | 전역 폰트 설정, 메모리 효율 |
| 세션 로직 | **격리** (ConPTY/VTCore/RenderState/TSF) | 독립 셸 프로세스, 상태 분리 |
| 설정 형식 | **JSON + GUI** | WT 패턴. JSON이 진실 원천, GUI는 편의 인터페이스 |
| 탭 위치 | **수직 사이드바** (좌측) | cmux 패턴. Phase 6 AI 알림 배지 확장 대비 |
| Sub-Feature 분리 | **5개 독립 PDCA** | Phase 4 성공 패턴 |

### 4.2 컴포넌트 다이어그램

```
┌─────────────────────────────────────────────────────┐
│                    GhostWinApp                       │
│                                                     │
│  ┌─────────────┐    ┌──────────────────────────┐    │
│  │ TabSidebar  │    │     SessionManager       │    │
│  │             │◄──►│                          │    │
│  │  - 탭 목록   │    │  Session 0 ─┐           │    │
│  │  - CWD 표시  │    │  Session 1 ─┤  Vector   │    │
│  │  - 드래그    │    │  Session N ─┘           │    │
│  │  - 추가/삭제 │    │                          │    │
│  └─────────────┘    │  각 Session:             │    │
│                     │    ConPTY + VTCore        │    │
│  ┌─────────────┐    │    + RenderState + TSF   │    │
│  │SettingsPanel│    └──────────┬───────────────┘    │
│  │ JSON + GUI  │──────────────►│                    │
│  └─────────────┘    설정 적용   │ active session    │
│                               ▼                     │
│  ┌─────────────────────────────────────────────┐    │
│  │           Shared Rendering Infra            │    │
│  │  GlyphAtlas ← DX11Renderer ← QuadBuilder   │    │
│  └─────────────────────────────────────────────┘    │
│                                                     │
│  ┌─────────────────────────────────────────────┐    │
│  │           PaneLayout (Phase 5-D)            │    │
│  │  Tree<Pane> — 수평/수직 분할, 중첩           │    │
│  │  각 Pane → Session 매핑                      │    │
│  └─────────────────────────────────────────────┘    │
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

### 4.4 설정 시스템 (JSON)

```jsonc
// %APPDATA%/GhostWin/ghostwin.json
{
  "font": {
    "family": "JetBrainsMono NF",
    "size": 11.25,
    "cell_width_scale": 1.0,
    "cell_height_scale": 1.0,
    "glyph_offset_x": 0.0,
    "glyph_offset_y": 0.0
  },
  "colors": {
    "background": "#1e1e2e",
    "foreground": "#cdd6f4",
    "cursor": "#f5e0dc"
  },
  "window": {
    "padding": { "left": 8, "top": 4, "right": 8, "bottom": 4 },
    "dynamic_padding": true,
    "mica_enabled": true
  },
  "keybindings": {
    "new_tab": "Ctrl+T",
    "close_tab": "Ctrl+W",
    "next_tab": "Ctrl+Tab",
    "split_horizontal": "Alt+H",
    "split_vertical": "Alt+V"
  }
}
```

---

## 5. Sub-Feature Map (5개 독립 PDCA)

```
multi-session-ui (Master Plan)
├── A: session-manager       [독립]     다중 ConPTY 격리 + SessionManager
├── B: tab-sidebar           [A 이후]   수직 탭 사이드바 + 탭 전환/추가/드래그
├── C: settings-system       [독립]     JSON 설정 + GUI 패널
├── D: pane-split            [A 이후]   수평/수직 Pane 분할 레이아웃 엔진
└── E: session-restore       [A+B+D]   CWD + 레이아웃 직렬화/복원
```

| ID | Feature | 의존성 | 예상 규모 | 상태 |
|----|---------|--------|-----------|:----:|
| A | session-manager | 없음 | 중 — Session struct, 다중 ConPTY 관리, 활성 세션 전환 | **95% 완료** |
| B | tab-sidebar | A 완료 | 대 — WinUI3 Code-only ListView, 드래그, CWD 표시, 단축키 | 대기 |
| C | settings-system | 없음 | 중 — JSON 파싱, 런타임 리로드, GUI 패널 | 대기 |
| D | pane-split | A 완료 | 대 — Tree 레이아웃 엔진, 분할/리사이즈, 포커스 관리 | 대기 |
| E | session-restore | A+B+D | 소 — JSON 직렬화, 시작 시 복원 | 대기 |

### 구현 순서

```
Phase 5-A: SessionManager (기반)
    ↓
Phase 5-B: TabSidebar (세션 기반 UI)    Phase 5-C: Settings (독립)
    ↓
Phase 5-D: PaneSplit (탭 내부 분할)
    ↓
Phase 5-E: SessionRestore (전체 완성 후)
```

---

## 6. Functional Requirements

### FR-01: SessionManager (Phase 5-A)
- 다중 Session 생성/삭제
- 활성 세션 전환 (비활성 세션은 VT 파싱만, 렌더링 suspend)
- 세션별 ConPTY + VTCore + RenderState + TSF 격리
- ADR-006 vt_mutex를 세션별로 확장

### FR-02: 탭 사이드바 (Phase 5-B)
- 좌측 수직 사이드바 (cmux 패턴)
- 탭 추가 (Ctrl+T), 닫기 (Ctrl+W), 전환 (Ctrl+Tab, Ctrl+1~9)
- 탭에 CWD 표시 (OSC 7 또는 ConPTY title)
- 드래그로 탭 순서 변경
- 마지막 탭 닫으면 앱 종료

### FR-03: JSON 설정 (Phase 5-C)
- `%APPDATA%/GhostWin/ghostwin.json` 기본 경로
- 폰트, 색상, 간격 (glyph-metrics 연동), 키바인딩, 창 패딩
- 파일 변경 감지 → 런타임 리로드 (FileSystemWatcher)
- 기본 설정 생성 (첫 실행 시)

### FR-04: GUI 설정 패널 (Phase 5-C)
- WinUI3 ContentDialog 또는 NavigationView 기반
- 폰트 선택, 크기, 색상 테마, 간격 조정 슬라이더
- 변경 → JSON 파일에 저장 → 런타임 반영

### FR-05: Pane 분할 (Phase 5-D)
- 수평 (Alt+H) / 수직 (Alt+V) 분할
- Tree<Pane> 레이아웃 — 중첩 분할 지원
- Pane 리사이즈 (드래그 경계선)
- 포커스 이동 (Alt+방향키)
- Pane 닫기 → 레이아웃 재배치

### FR-06: 세션 복원 (Phase 5-E)
- 앱 종료 시 레이아웃 + CWD를 JSON으로 직렬화
- 재시작 시 탭 구성 + Pane 레이아웃 + CWD 복원
- 실행 중인 프로세스 복원은 미지원 (cmux와 동일 제한)

---

## 7. Non-Functional Requirements

| NFR | 목표 | 측정 방법 |
|-----|------|-----------|
| NFR-01 | 탭 전환 < 50ms | 시각 지연 없음 |
| NFR-02 | 세션 10개 이상 안정 동작 | 10탭 열고 30분 사용 |
| NFR-03 | 설정 리로드 < 100ms | JSON 변경 후 반영 시간 |
| NFR-04 | 메모리: 탭당 < 20MB 추가 | Task Manager 측정 |
| NFR-05 | 기존 Phase 4 테스트 유지 | 10/10 PASS |

---

## 8. Risks

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| WinUI3 Code-only로 ListView/드래그 구현 난이도 | 상 | 중 | WT의 TabView 구현 참고. 최소 기능 먼저 |
| 다중 세션 스레드 동기화 복잡도 | 상 | 중 | ADR-006 패턴 확장. 세션별 독립 mutex |
| Pane 분할 레이아웃 엔진 복잡도 | 중 | 중 | WT의 Pane::_Split 참고. Tree 구조 단순화 |
| JSON 파싱 라이브러리 선택 | 하 | 하 | nlohmann/json (header-only, MIT) 또는 RapidJSON |
| 세션 복원 시 CWD 접근 권한 | 하 | 하 | 존재하지 않는 경로면 기본 디렉토리로 폴백 |

---

## 9. Brainstorming Log

| Phase | 결정 | 근거 |
|-------|------|------|
| Q1 | 제품 수준 터미널 완성 | WT/Alacritty 대체 목표 |
| Q2 | JSON + GUI 설정 병행 | WT 패턴. JSON이 진실 원천 |
| Q3 | 전체 8개 항목 모두 필수 | YAGNI에서 모두 선택 |
| Approach | Session-First (A) | Phase 4 성공 패턴 답습 |
| Architecture | 렌더링 공유 + 세션 격리 | 메모리 효율 + 독립 프로세스 |
| 탭 스타일 | 수직 사이드바 | cmux 패턴, Phase 6 확장 대비 |

---

## 10. Next Steps

1. [ ] `/pdca design session-manager` — Phase 5-A 설계
2. [ ] `/pdca design tab-sidebar` — Phase 5-B 설계
3. [ ] `/pdca design settings-system` — Phase 5-C 설계
4. [ ] 순차 구현 (A → B/C 병렬 → D → E)

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-03 | Initial draft (Plan Plus brainstorming) | 노수장 |
