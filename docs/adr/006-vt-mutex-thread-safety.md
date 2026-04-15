# ADR-006: vt_mutex 를 통한 VtCore 스레드 안전성 확보

- **상태**: 채택 (2026-03-29) → **개정 중** (2026-04-15) — 실제 코드 구조 반영 + M1 통합 방향 명시
- **관련**: Phase 2 Design 리뷰 C2 이슈, Phase 5-B multi-session 도입, `vt-mutex-redesign` sub-cycle

---

## 2026-04-15 개정 요약

원 ADR (2026-03-29) 은 단일 세션 시점에서 "ConPtySession 내부 단일 `vt_mutex`" 를 결정했다. 이후 Phase 5-B (multi-session, 2026-04-04 `cf66be9`) 에서 **`Session::vt_mutex` 가 추가**됐고, 3 일 뒤 `8e4e6c2` (2026-04-07) 의 "dual-mutex bug fix" 로 **렌더 경로가 `ConPtySession::vt_mutex` 로 이전**되었다. 그 결과 현재 코드는 원 ADR 이 기술한 단일 mutex 구조가 아니다.

본 개정은:
1. **실제 구조를 기술** (3 개 mutex 공존, 책임 분할)
2. **"Alacritty / WezTerm 동일 패턴" claim 을 약화** — 저장소 내 외부 근거 없음
3. **M1 통합 방향을 정식화** (`vt-mutex-redesign` sub-cycle)

---

## 배경 (원문 유지)

ConPTY I/O 스레드에서 `VtCore::write()`, 메인 스레드에서 `VtCore::resize()` 를 호출한다. 두 호출이 동시에 발생하면 libghostty-vt 내부에서 data race 발생 가능.

## 문제 (원문 유지)

- libghostty-vt 의 `ghostty_terminal_vt_write`, `ghostty_terminal_resize` 의 스레드 안전성은 **공식 문서에 명시되지 않음**
- 헤더 grep 결과 "thread-safe" / "not thread-safe" 언급 없음 — 2026-04-15 재확인
- Design 리뷰에서 3 개 에이전트 모두 이 경합을 Critical 로 지적
- "libghostty-vt 내부 mutex 보호 추측" 에 의존하는 것은 위험

## 최초 결정 (2026-03-29)

`std::mutex` (`vt_mutex`) 로 `write()` 와 `resize()` 를 상호 배제.

```cpp
// I/O thread (현재 코드, conpty_session.cpp:319)
{
    std::lock_guard lock(impl->vt_mutex);
    impl->vt_core->write({buf.get(), bytes_read});
}

// Main thread (현재 코드, conpty_session.cpp:502)
{
    std::lock_guard lock(impl_->vt_mutex);
    impl_->vt_core->resize(cols, rows);
}
```

---

## 현재 코드 구조 (2026-04-15 기준, 4 명 병렬 재검증)

### 자물쇠가 **3 개 공존**

| ID | 정의 위치 | 보호 대상 | 잡는 주체 |
|----|-----------|-----------|-----------|
| **M1** | `src/conpty/conpty_session.cpp:267` | VtCore (write / resize / 렌더 read) | I/O 스레드, `ConPtySession::resize`, engine 경로 `start_paint` |
| **M2** | `src/session/session.h:122` ("ADR-006 extension") | `SessionManager::resize_*` 의 `conpty->resize + state->resize` 원자성 | WPF UI 스레드 (HwndHost 콜백, `OnRenderSizeChanged`) |
| **M3** | `src/renderer/terminal_window.cpp:41` | `src/main.cpp` stand-alone PoC 의 VtCore 접근 | stand-alone 렌더 스레드 + 윈도우 스레드 |

### 왜 3 개가 됐는가 — 이력

| commit | 날짜 | 변화 |
|--------|------|------|
| `a22046f` | 2026-03-29 | M1 최초 도입 (단일 세션) |
| 본 ADR 채택 | 2026-03-29 | "`impl->vt_mutex` 1 개" 로 결정 |
| `cf66be9` | 2026-04-04 | Phase 5-B multi-session 도입 시 **M2 추가** ("ADR-006 extension" 주석). 의도: per-session 격리 + 3-way 동기화 |
| `5e2810e` | 2026-04-06 | M-8a/8b Surface API 도입, 렌더 경로가 M2 를 잡기 시작 |
| `8e4e6c2` | 2026-04-07 | **"dual-mutex bug fix"**. `ConPtySession::vt_mutex()` getter 노출, 렌더 경로를 **M1 으로 교체**. `ghostwin_engine.cpp:139` 주석 "Use ConPtySession's internal vt_mutex (NOT Session::vt_mutex)" 추가 |

결과: M2 의 원 의도 (3-way 동기화) 중 렌더 + IO 는 M1 으로 이전, **M2 는 resize 원자성만 담당하는 잔존물**.

### 현재 M2 의 유일한 잔존 역할

`src/session/session_manager.cpp` 의 3 경로 (`:328`, `:396`, `:414`) 에서 다음 두 호출의 원자성 보장:

```cpp
std::lock_guard lock(sess->vt_mutex);           // M2
sess->conpty->resize(cols, rows);                // 내부에서 M1 획득
sess->state->resize(cols, rows);                 // M2 보호만
```

`state->resize` 가 M1 이 아닌 M2 로 보호되기 때문에, **engine 렌더 경로 (M1) 와 resize 경로 (M2) 가 서로 다른 mutex 로 같은 `TerminalRenderState` 를 변경/판독** 한다. 잠재 race 존재. 현재는 호출 빈도 + WPF UI 스레드 단일 실행으로 실질 충돌이 드물지만, 설계적으로 부채.

---

## 근거 — 외부 참조 claim 재검증

| Project | 원 ADR 주장 | 2026-04-15 재검증 |
|---------|-------------|-------------------|
| Alacritty | "resize 를 event loop 채널로 전송하여 write 와 직렬화" | **저장소 내 근거 없음**. 외부 확인 필요 |
| WezTerm | "`Arc<Mutex<Inner>>` 로 ConPTY 상태 공유" | **저장소 내 근거 없음**. 외부 확인 필요 |

두 claim 은 원 ADR 이 특정 파일/commit 을 인용하지 않았고, 본 저장소에도 Alacritty/WezTerm 소스가 없어 **재현 불가**. 향후 검증되면 링크 추가, 그 전까지는 claim 을 약화하여 유지.

변경 후 claim: "**일반적으로 터미널 에뮬레이터는 write/resize 동시 접근을 어떤 형태로든 차단한다** — 구체적 패턴은 구현마다 다르며, 본 프로젝트에서는 `std::mutex` 를 채택".

---

## 성능 영향 (원문 유지 + 검증 노트)

- ReadFile 블록 시간이 대부분이므로 mutex 경합은 거의 발생 안 함
- 경합 발생 시 `lock_guard` overhead 는 마이크로초 단위
- `TerminalRenderState::resize` slow path 에서 `std::vector<CellData>` 재할당 + memcpy 가 mutex 아래에서 발생 — `RenderFrame::reshape` 는 monotonic high-water mark 설계라 일반 사용 시 fast path (메타데이터만) (2026-04-15 Agent 3 확인)

---

## 대안 검토 (원문 유지)

| 방안 | 판정 | 이유 |
|------|:----:|------|
| libghostty-vt 소스 확인 후 불필요 시 제거 | 보류 | upstream 변경 시 재검증 필요, 방어적 설계 유지 |
| SPSC 큐 | 보류 | Phase 2 에서는 과잉 |
| resize 를 I/O 스레드로 위임 | 기각 | ReadFile 블록 중 메시지 전달 불가 |
| **std::mutex** | **채택** | 단순, 검증된 패턴 |

---

## Phase 3 마이그레이션 노트 → 현 상태 평가

원 ADR 은 "D3D11 렌더 스레드 도입 시 `update_render_state()` 도 `vt_mutex` 에 포함" 이라 적었다. 코드는 이 방향으로 진행됐으나 **두 mutex 로 분기**되었다 (engine 경로 M1, SessionManager resize 경로 M2). 3-way 경합 차단의 본래 의도는 M1 로 일원화해야 달성됨.

---

## 개정 방향 — M1 으로 통합

### 결정

**M2 를 제거하고 M1 으로 통합**한다.

### 실행 계획

별도 sub-cycle `vt-mutex-redesign` 에서 수행. Plan 문서: `docs/01-plan/features/vt-mutex-redesign.plan.md`.

요점:

1. `ConPtySession::resize` 를 `resize_pty_only(cols,rows)` + `vt_resize_locked(cols,rows)` 로 분리 (caller 가 M1 을 잡고 묶어서 호출)
2. `SessionManager` 의 3 경로 패턴 변경
3. M2 필드 + 관련 주석 제거
4. `TerminalRenderState::resize` contract 주석 명확화 (`ConPtySession::vt_mutex()` 사용 명시)

### M3 (stand-alone PoC) 처리

`src/main.cpp` stand-alone 경로는 WPF 본선과 격리되어 있다. M3 는 본 개정 범위 밖 — 별도 cycle 또는 `main.cpp` 자체 정리 시 함께 처리.

### 안전성 근거

2026-04-15 4 명 병렬 코드 검증:

- 데드락 경로 0 건 (Agent 2)
- `state->resize` 는 leaf (다른 mutex / DX11 / COM 호출 없음) (Agent 3)
- 모든 M2 실호출 경로가 WPF UI 스레드 단일 (Agent 1)

---

## 부채 추적

| 항목 | 위치 | 상태 |
|------|------|------|
| M2 제거 | `vt-mutex-redesign` sub-cycle | Plan 갱신 완료, Design 대기 |
| M3 정리 | 별도 (main.cpp 활용도 확인 후) | 미착수 |
| `force_all_dirty()` race (시나리오 D) | `ghostwin_engine.cpp:142` | **별도 부채**, M2 와 무관 |
| `EngineService.SessionResize` dead code | `EngineService.cs:117` | 확실하지 않음 (동적 호출 확인 필요) |

---

## 관련 문서

- `docs/01-plan/features/vt-mutex-redesign.plan.md` — 정리 작업 계획 (본 ADR 과 동시 개정)
- Obsidian `ADR/adr-006-vt-mutex.md` — SoT 버전
- Obsidian `Backlog/tech-debt.md` #1
- Obsidian `Architecture/conpty-integration.md`
- Phase 5-B design archive: `docs/archive/2026-04/session-manager/session-manager.design.md` (M2 도입 의도)
