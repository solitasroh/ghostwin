# 복사/붙여넣기 리서치 — 4개 터미널 비교 분석

> 작성: 2026-04-11 | 대상: M-10.5 Clipboard 기능 설계 입력

## 1. 조사 대상

| 터미널                    | 언어  | UI 프레임워크     | 클립보드 라이브러리                                 |
| ------------------------- | ----- | ----------------- | --------------------------------------------------- |
| **Windows Terminal (WT)** | C++   | WinUI3            | Win32 API 직접 (`OpenClipboard`/`SetClipboardData`) |
| **Alacritty**             | Rust  | winit/glutin      | `copypasta` crate → `clipboard-win`                 |
| **WezTerm**               | Rust  | 자체 window crate | `clipboard_win` crate                               |
| **cmux**                  | Swift | AppKit (macOS)    | `NSPasteboard` + Ghostty C API 콜백                 |

## 2. 아키텍처 비교

### 2.1 계층 구조

| 터미널    | 계층 수 | 클립보드 접근 위치        | 이벤트 방식                                              |
| --------- | :-----: | ------------------------- | -------------------------------------------------------- |
| WT        |    4    | App 최상위 (TerminalPage) | 이벤트 버블링 (`WriteToClipboard`/`PasteFromClipboard`)  |
| Alacritty |    3    | UI 레이어 (event.rs)      | `Event::ClipboardStore`/`ClipboardLoad`                  |
| WezTerm   |    4    | GUI TermWindow            | `window.set_clipboard()`/`get_clipboard()`               |
| cmux      |    3    | Runtime 콜백              | `write_clipboard_cb`/`read_clipboard_cb` (Ghostty C API) |

**공통 패턴**: 터미널 코어는 클립보드에 직접 접근하지 않고, UI/Runtime 레이어로 이벤트/콜백을 올려 OS 클립보드 조작을 위임한다.

### 2.2 GhostWin 적용

GhostWin은 **cmux와 동일한 Ghostty 기반**이므로:

- `ghostty_surface_text()`로 paste하면 bracketed paste 자동 처리 (cmux 확인)
- `write_clipboard_cb`/`read_clipboard_cb` 콜백 패턴 활용 가능
- 다만 현재 GhostWin은 Ghostty 콜백을 사용하지 않고 자체 `GetSelectedText` C API를 구현했으므로, WPF `Clipboard` API로 직접 처리하는 것이 자연스러움

## 3. Copy 흐름 비교

### 3.1 Ctrl+C 이중 역할 (전 터미널 공통)

```
if (텍스트 선택 있음) → 클립보드에 복사
else → ^C (SIGINT, 0x03) 터미널에 전송
```

- **WT**: `ControlInteractivity.cpp:227` — `HasSelection()` false면 `return false` → 키 이벤트 터미널 전달
- **Alacritty**: `input/mod.rs:323` — `Action::Copy` → `copy_selection()`
- **WezTerm**: `commands.rs:642` — `CopyTo(ClipboardCopyDestination)` 액션
- **cmux**: `GhosttyTerminalView.swift:5943` — `performBindingAction("copy_to_clipboard")`

### 3.2 데이터 포맷

| 포맷                        |    WT    | Alacritty |     WezTerm      | cmux |
| --------------------------- | :------: | :-------: | :--------------: | :--: |
| Plain Text (CF_UNICODETEXT) |    O     |     O     |        O         |  O   |
| HTML                        | O (설정) |     X     |        X         |  X   |
| RTF                         | O (설정) |     X     |        X         |  X   |
| ANSI Escape 포함 텍스트     | O (옵션) |     X     | O (Lua callback) |  X   |

**결론**: 기본은 Plain Text만. HTML/RTF는 WT만 기본 지원. ANSI Escape 포함은 고급 기능.

### 3.3 Trailing Whitespace 트리밍

| 터미널            | 방식                                                        |
| ----------------- | ----------------------------------------------------------- |
| WT                | `GetLastNonSpaceColumn()` (block selection 별도 설정)       |
| Alacritty         | Block:`trim_end()`, Simple: 마지막 `\n` strip               |
| WezTerm           | `trim_end()` (마지막 줄만)                                  |
| cmux              | Ghostty 설정 `clipboard-trim-trailing-spaces` (기본 true)   |
| **GhostWin 현재** | `GetSelectedText` C API에서 후행 공백 자동 트림 (구현 완료) |

### 3.4 Copy-on-Select (마우스 선택 시 자동 복사)

| 터미널    |             기본값             | 설정 키                                           |
| --------- | :----------------------------: | ------------------------------------------------- |
| WT        |            `false`             | `copyOnSelect`                                    |
| Alacritty |            `false`             | `selection.save_to_clipboard`                     |
| WezTerm   |     마우스 바인딩으로 제어     | `CompleteSelection(ClipboardAndPrimarySelection)` |
| cmux      | macOS `true` / Windows `false` | `copy-on-select` (Ghostty config)                 |

## 4. Paste 흐름 비교

### 4.1 Bracketed Paste Mode (DECSET 2004)

```
\x1b[200~ + (필터링된 텍스트) + \x1b[201~
```

| 터미널    | 구현 위치                  | 보안 필터링                                 |
| --------- | -------------------------- | ------------------------------------------- |
| WT        | `ControlCore::PasteText`   | C0/C1 제어 코드 제거 (HT/LF/CR 제외)        |
| Alacritty | `event.rs:1376`            | `\x1b` + `\x03` 제거                        |
| WezTerm   | `terminalstate/mod.rs:823` | 내장 `\x1b[200~`/`\x1b[201~` 제거 (de-fang) |
| cmux      | Ghostty Core 자동 처리     | Ghostty 내부 (`ghostty_surface_text()`)     |

**보안 필터링 비교:**

- **WT**: 가장 엄격 — HT/LF/CR 외 모든 C0/C1 제어 코드 제거
- **Alacritty**: ESC + Ctrl+C만 제거
- **WezTerm**: 중첩된 bracket 마커만 제거 (de-fang)
- **GhostWin 권장**: WT 방식 (가장 안전)

### 4.2 줄바꿈 정규화

| 터미널    | 변환 규칙                                  | 조건                        |
| --------- | ------------------------------------------ | --------------------------- |
| WT        | `\n` → `\r` (앞에 `\r` 없을 때)            | 항상 (FilterStringForPaste) |
| Alacritty | `\r\n` → `\r`, `\n` → `\r`                 | 비 bracketed 모드만         |
| WezTerm   | 플랫폼별 (Windows:`\r\n`, 비Windows: `\r`) | 비 bracketed 모드만         |
| cmux      | Ghostty Core 처리                          | —                           |

**GhostWin 권장**: `\r\n` → `\r`, `\n` → `\r` (WT + Alacritty 공통 패턴)

### 4.3 대용량/멀티라인 붙여넣기 경고

| 터미널    |  멀티라인 경고   | 대용량 경고 |            임계값            |
| --------- | :--------------: | :---------: | :--------------------------: |
| WT        |  O (설정 가능)   |      O      |            5 KiB             |
| Alacritty |        X         |      X      |              —               |
| WezTerm   |        X         |      X      |              —               |
| cmux      | O (Ghostty 설정) |      X      | `clipboard-paste-protection` |

**GhostWin 권장**: WT 방식 — 멀티라인 + 대용량 경고 다이얼로그 (차별화 포인트)

### 4.4 TrimPaste

| 터미널    | 방식                                                                            |
| --------- | ------------------------------------------------------------------------------- |
| WT        | 단일 라인: 끝 공백/탭/개행 트림. 멀티라인: 트림 안 함. BracketedPaste 시 비활성 |
| Alacritty | 없음                                                                            |
| WezTerm   | 없음                                                                            |
| cmux      | 없음                                                                            |

**GhostWin 권장**: WT 방식 TrimPaste 도입 (사용자 편의)

## 5. OSC 52 지원 비교

| 기능                       |       WT       |        Alacritty         |  WezTerm  |         cmux          |
| -------------------------- | :------------: | :----------------------: | :-------: | :-------------------: |
| OSC 52 Write (앱→클립보드) |       O        |            O             |     O     |           O           |
| OSC 52 Read (클립보드→앱)  |       X        |         O (설정)         | X (no-op) |       O (설정)        |
| 보안 게이트                | focused + 설정 | focused +`OnlyCopy` 기본 |   없음    | `clipboard-read: ask` |

**공통 보안 원칙**: OSC 52 Write는 기본 허용, Read는 기본 거부 또는 확인 요구.

**GhostWin 권장**: Phase 1에서 Write/read 지원 (focused 체크 필수)

## 6. 선택 모드 비교

| 모드           |       WT        |   Alacritty   |    WezTerm     |      cmux       | GhostWin 현재 |
| -------------- | :-------------: | :-----------: | :------------: | :-------------: | :-----------: |
| Cell (문자)    |        O        |  O (Simple)   |       O        |        O        |       O       |
| Word (단어)    |  O (더블클릭)   | O (Semantic)  |  O (더블클릭)  |        O        |       O       |
| Line (줄)      | O (트리플클릭)  |       O       | O (트리플클릭) |        O        |       O       |
| Block (사각형) | O (Alt+드래그)  | O (Ctrl+클릭) | O (Alt+드래그) |        O        |       X       |
| Vi mode        |        X        |       O       |       X        | O (Cmd+Shift+M) |       X       |
| Mark mode      | O (키보드 선택) |       X       |       X        |        X        |       X       |

**GhostWin M-10.5 범위**: Cell/Word/Line 이미 완료. Block은 후속 고려.

## 7. 고유 UX 기능 비교

| 기능                              |     WT      | Alacritty | WezTerm |   cmux    |
| --------------------------------- | :---------: | :-------: | :-----: | :-------: |
| 파일 드롭 → 경로 붙여넣기         |      O      |     X     |    X    |     X     |
| 이미지 붙여넣기 → 임시파일 경로   |      X      |     X     |    X    |     O     |
| HTML/RTF 복사                     |      O      |     X     |    X    |     X     |
| 브로드캐스트 붙여넣기 (멀티 pane) |      O      |     X     |    X    |     X     |
| 우클릭 컨텍스트 메뉴              |  O (설정)   |     X     |    X    |     O     |
| 스마트 인용부호 정규화            | O (conhost) |     X     |    X    |     X     |
| 검색 연동 (Find Selection)        |      X      |     X     |    X    | O (Cmd+E) |

## 8. 성능 고려사항

### Win32 API vs WPF Clipboard

WT 주석 (`TerminalPage.cpp:2953`):

> "The old Win32 clipboard API is somewhere in the order of 300-1000x faster than the WinRT one"

WPF `System.Windows.Clipboard`는 내부적으로 OLE 클립보드를 사용하며, Win32 직접 호출보다 느림.
다만 터미널 클립보드 조작은 사용자 입력 기반 (초당 수 회 미만)이므로 **성능 병목이 아님**.

**GhostWin 권장**: `System.Windows.Clipboard` 사용 (간결성 우선). 성능 이슈 발생 시 P/Invoke로 전환.

## 9. GhostWin 현재 상태 요약

### 이미 준비된 인프라

| 항목                           | 상태 | 위치                                               |
| ------------------------------ | :--: | -------------------------------------------------- |
| `GetSelectedText` C API        | 완료 | `EngineService.cs:164` → `ghostwin_engine.cpp:863` |
| 텍스트 선택 (Cell/Word/Line)   | 완료 | `TerminalHostControl.cs` (M-10)                    |
| DX11 선택 하이라이트           | 완료 | `ghostwin_engine.cpp:154`                          |
| `SelectionChanged` 이벤트      | 완료 | `TerminalHostControl.cs:59` (소비자 없음)          |
| `VT_MODE_BRACKETED_PASTE` 상수 | 완료 | `vt_bridge.h:156`                                  |
| `edit.paste` 키바인딩 등록     | 완료 | `settings_manager.cpp:302` (핸들러 미구현)         |
| 설정 시스템 (M-4)              | 완료 | `ISettingsService` + JSON hot reload               |

### 구현 필요 항목

| 항목                | 필요 작업                                                       |
| ------------------- | --------------------------------------------------------------- |
| **Ctrl+C 복사**     | `MainWindow.xaml.cs:379` 분기 — 선택 있으면 복사, 없으면 SIGINT |
| **Ctrl+V 붙여넣기** | 클립보드 읽기 → 필터링 →`WriteSession` 전달                     |
| **Bracketed Paste** | `VtCore::mode_get()` P/Invoke 래퍼 노출 + 래핑 로직             |
| **줄바꿈 정규화**   | `\r\n` → `\r`, `\n` → `\r`                                      |
| **보안 필터링**     | C0/C1 제어 코드 제거 (WT 방식)                                  |
| **Ctrl+Shift+C/V**  | 명시적 복사/붙여넣기 (선택 무관)                                |
| **Shift+Insert**    | 붙여넣기 대체 단축키                                            |
| **우클릭 메뉴**     | 컨텍스트 메뉴 (복사/붙여넣기)                                   |
| **멀티라인 경고**   | 다이얼로그 + 설정                                               |
| **OSC 52**          | 엔진 콜백 → WPF 클립보드 쓰기                                   |

## 10. 참조 파일 위치

### Windows Terminal

- `references/terminal/src/cascadia/TerminalApp/TerminalPage.cpp` — 클립보드 네임스페이스 (line 62~210)
- `references/terminal/src/cascadia/TerminalControl/ControlCore.cpp` — PasteText/CopySelection
- `references/terminal/src/cascadia/TerminalControl/ControlInteractivity.cpp` — Ctrl+C 이중 역할
- `references/terminal/src/buffer/out/textBuffer.cpp` — HTML/RTF 생성
- `references/terminal/src/interactivity/win32/clipboard.hpp` — conhost 레거시 클립보드

### Alacritty

- `references/alacritty/alacritty/src/clipboard.rs` — Clipboard 추상화 (85줄)
- `references/alacritty/alacritty/src/event.rs:1369` — paste() 핵심 로직
- `references/alacritty/alacritty/src/input/mod.rs:323` — Copy/Paste 액션
- `references/alacritty/alacritty_terminal/src/selection.rs` — Selection 모델

### WezTerm

- `references/wezterm/wezterm-gui/src/termwindow/clipboard.rs` — copy/paste 통합
- `references/wezterm/term/src/terminalstate/mod.rs:823` — send_paste (de-fang)
- `references/wezterm/term/src/config.rs:8` — NewlineCanon
- `references/wezterm/config/src/keyassignment.rs` — ClipboardCopyDestination/PasteSource

### cmux

- `references/cmux/Sources/GhosttyTerminalView.swift:1533` — write_clipboard_cb
- `references/cmux/Sources/GhosttyTerminalView.swift:1251` — read_clipboard_cb
- `references/cmux/Sources/GhosttyTerminalView.swift:881` — Vim Copy Mode
- `references/cmux/Sources/GhosttyTerminalView.swift:102` — GhosttyPasteboardHelper
