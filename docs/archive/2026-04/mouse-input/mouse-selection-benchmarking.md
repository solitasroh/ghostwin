# Mouse Selection Benchmarking: 5개 터미널 텍스트 선택 줄단위 분석

> **Author**: Claude + 노수장
> **Date**: 2026-04-11
> **Status**: Complete
> **Scope**: M-10c 텍스트 선택 구현을 위한 참조 구현 분석

---

## 1. 핵심 발견: GhostWin의 결정적 제약

### cmux 패턴 (이상적이지만 사용 불가)

cmux는 Selection을 **전혀 자체 구현하지 않음**:
- 마우스 이벤트를 `ghostty_surface_mouse_*`로 전달만
- Selection 시각화(하이라이트)는 **ghostty 렌더러가 자동 처리**
- `ghostty_surface_has_selection` / `read_selection` / `clear_selection`으로 조회/해제만
- Shift bypass 판단도 libghostty가 함

**GhostWin은 `-Demit-lib-vt=true` 빌드라 `ghostty_surface_*` API 미포함** → cmux 패턴 사용 불가.

### 따라야 할 패턴: WT/Alacritty/WezTerm (자체 Selection 구현)

GhostWin은 WT, Alacritty, WezTerm처럼 **GUI 측에서 Selection을 직접 관리**해야 합니다.

---

## 2. 크로스 분석

### Selection 관리 위치

| Terminal | 관리 위치 | 시각화 | Selection 구조체 |
|----------|----------|--------|-----------------|
| **ghostty** | Terminal 내부 (`Screen.select`) | 렌더러 자동 | `Selection` (tracked/untracked Pin) |
| **cmux** | **libghostty에 완전 위임** | ghostty 렌더러 | N/A (API 호출만) |
| **WT** | Terminal Core (`SelectionInfo`) | 렌더러 자동 | `SelectionInfo {start, end, pivot, blockSelection, active}` |
| **Alacritty** | GUI 레이어 (`Selection`) | Grid 렌더 시 `SelectionRange` 참조 | `Selection {ty, region: Range<Anchor>}` |
| **WezTerm** | GUI 레이어 (per-pane `Selection`) | 렌더 시 `SelectionRange` 참조 | `Selection {origin, range, seqno, rectangular}` |

### 클릭 카운트 감지

| Terminal | 방식 | 임계값 | 최대 |
|----------|------|:------:|:----:|
| **ghostty** | `left_click_count` + `mouse_interval` + 거리 `cell_width` 이내 | config | 3 (1→2→3→1) |
| **WT** | `_numberOfClicks` + `_multiClickTimer` | system | 3 (cycling) |
| **Alacritty** | `ClickState` enum (None→Click→Double→Triple) | 400ms | 3 |
| **WezTerm** | `LastMouseClick.streak` + `CLICK_INTERVAL` 500ms + 같은 cell | 500ms | 무제한 (cycling) |

### 선택 모드

| 모드 | ghostty | WT | Alacritty | WezTerm |
|------|---------|-----|-----------|---------|
| **Cell/Simple** | single drag (60% threshold) | `SelectionExpansion::Char` | `SelectionType::Simple` | `SelectionMode::Cell` |
| **Word** | double click (`selectWord`) | `::Word` | `::Semantic` (escape chars) | `::Word` |
| **Line** | triple click (`selectLine`) | `::Line` | `::Lines` | `::Line` |
| **Block/Rect** | Ctrl/Super+Alt drag | Alt+drag (`blockSelection`) | Ctrl+drag (`::Block`) | Alt+drag (`::Block`) |
| **Semantic Zone** | Ctrl+triple (`selectOutput`) | — | — | `::SemanticZone` |

### 단어 경계 문자

| Terminal | 경계 문자 |
|----------|----------|
| **ghostty** | `selection_word_chars` (config) |
| **WT** | `_wordDelimiters` (config) |
| **Alacritty** | `` ,│`\|:"' ()[]{}<>\t `` (SEMANTIC_ESCAPE_CHARS) |
| **WezTerm** | `word_boundary` (config) |

### 자동 스크롤 (드래그 시 뷰포트 밖)

| Terminal | 방식 | 간격 |
|----------|------|:----:|
| **ghostty** | `selection_scroll_active` → IO 메시지 큐 | IO 스레드 |
| **WT** | `_TryStartAutoScroll` → velocity 기반 | distance 비례 |
| **Alacritty** | `update_selection_scrolling` → 15ms 타이머 | 15ms, distance/step 비례 |
| **WezTerm** | `extend_selection_at_mouse_cursor` 내부 `scroll_to_point` | 즉시 |

### Shift 바이패스 (마우스 모드에서 선택 강제)

| Terminal | 방식 |
|----------|------|
| **ghostty** | `mouseShiftCapture` — Shift 누르면 mouse reporting 우회 |
| **WT** | `_canSendVTMouseInput` — Shift 누르면 false 반환 |
| **Alacritty** | `modifiers.shift_key() \|\| !mouse_mode()` 분기 |
| **WezTerm** | `bypass_mouse_reporting_modifiers` (기본 Shift) 설정 |

### 클립보드 복사 시점

| Terminal | 시점 |
|----------|------|
| **ghostty** | 릴리스 시 `copy_on_select` 설정에 따라 |
| **WT** | 릴리스 시 `CopyOnSelect` 설정에 따라 |
| **Alacritty** | 릴리스 시 `copy_selection(ClipboardType::Selection)` |
| **WezTerm** | 릴리스 시 `CompleteSelection(ClipboardAndPrimarySelection)` |

---

## 3. GhostWin M-10c 설계 지침

### 참조 모델: Alacritty + WezTerm 하이브리드

| 항목 | Alacritty 참조 | WezTerm 참조 | GhostWin 적용 |
|------|---------------|-------------|---------------|
| **Selection 구조체** | `Selection {ty, region: Range<Anchor>}` | `Selection {origin, range}` | WPF 측 `SelectionState` 클래스 |
| **Anchor** | `Point + Side` | `SelectionCoordinate {x, y}` | `(row, col, side)` |
| **모드** | Simple/Block/Semantic/Lines | Cell/Word/Line/Block/SemanticZone | Simple/Word/Line/Block (4종) |
| **드래그 확장** | `update_selection(point, side)` | `extend_selection_at_mouse_cursor` | WndProc에서 동기 처리 |
| **시각화** | Grid 렌더 시 `SelectionRange` 참조 | 렌더 시 `SelectionRange` 참조 | **DX11 render pass에서 반전 색상** |
| **텍스트 읽기** | `term.selection_to_string()` | `selection_text()` | VtCore에서 screen buffer 읽기 API 필요 |

### GhostWin 특수 과제

1. **screen buffer 읽기 API 없음** — ghostty VtCore에서 셀 텍스트를 읽는 C API가 필요
   - `ghostty_terminal_screen_*` 계열 API가 libvt에 포함되는지 확인 필요
   - 없으면 VtCore에 `get_cell_text(row, col)` wrapper 추가

2. **Selection 시각화** — DX11 렌더러에서 선택 영역 하이라이트
   - Option A: 2nd render pass에서 반전 색상 오버레이
   - Option B: WPF Adorner/overlay (Airspace 문제 가능)
   - **Option A 권장** (ghostty/WT/Alacritty 모두 GPU 렌더러에서 처리)

3. **좌표 변환** — pixel → cell 변환이 필요
   - M-10a에서 이미 `ghostty_mouse_encoder`의 pixel→cell 변환 사용 중
   - Selection은 WPF 측에서 직접 cell 좌표 계산 필요 (engine API 추가)

---

## 4. 구현 복잡도 평가

| 항목 | 복잡도 | 근거 |
|------|:------:|------|
| Selection 상태 관리 (start/end/mode) | **LOW** | Alacritty `Selection` 구조체 ~100줄 |
| 클릭 카운트 감지 (single/double/triple) | **LOW** | 타이머 + 카운터 ~30줄 |
| 드래그 확장 | **MEDIUM** | origin 기반 방향 결정 + cell 변환 |
| 단어/줄 선택 | **HIGH** | screen buffer 접근 + boundary 탐색 필요 |
| 시각화 (하이라이트) | **HIGH** | DX11 render pass 수정 또는 WPF overlay |
| 자동 스크롤 | **MEDIUM** | 타이머 + scrollback viewport |
| 텍스트 읽기 (클립보드) | **HIGH** | screen buffer → text 변환 API 필요 |

**총 예상**: M-10a/M-10b 대비 2~3배 복잡. 별도 PDCA 사이클 권장.

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-11 | 5개 터미널 Selection 코드 줄단위 분석 완료 |
