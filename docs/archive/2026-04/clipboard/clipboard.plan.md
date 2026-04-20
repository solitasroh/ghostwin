# Clipboard Plan -- GhostWin M-10.5

> **Summary**: 텍스트 선택/DX11 하이라이트 완성 이후, Ctrl+C/V 복사/붙여넣기 + 보안 필터링 + Bracketed Paste + OSC 52를 3단계로 구현하여 터미널을 "일상 도구"로 전환한다.
>
> **Project**: GhostWin Terminal
> **Phase**: M-10.5 Clipboard Copy/Paste
> **Date**: 2026-04-11
> **Status**: Draft
> **PRD**: `docs/00-pm/clipboard.prd.md` (v1.1)
> **Research**: `docs/00-research/research-clipboard-copy-paste.md`

---

## Executive Summary

| Perspective         | Content |
|---------------------|---------|
| **Problem**         | GhostWin은 M-10에서 텍스트 선택과 DX11 하이라이트를 완성했지만 클립보드 기능이 전혀 없다. Ctrl+C는 항상 SIGINT(0x03)를 보내고 Ctrl+V는 아무 동작도 하지 않아, 터미널의 가장 기본적인 복사/붙여넣기 작업이 불가능하다. |
| **Solution**        | (1) Ctrl+C 이중 역할 + Ctrl+V 안전 붙여넣기(C0/C1 필터 + Bracketed Paste + 줄바꿈 정규화) + OSC 52 Write/Read를 Phase 1로 구현하고, (2) 멀티라인/대용량 경고를 Phase 2로, (3) 우클릭 메뉴/Copy-on-Select를 Phase 3으로 단계 투입한다. |
| **기능적 UX 효과**  | "선택하면 바로 복사, 붙여넣기는 안전하게" -- 사용자가 서버 작업 중 위험한 멀티라인 명령을 실수로 실행하는 사고를 차단하면서, CJK 전각 문자도 정확히 복사된다. |
| **핵심 가치**       | WT 수준 기능 범위 + Alacritty 수준 성능 + WPF 네이티브 안정성으로, "데모용 터미널"에서 "일상 도구"로 전환하는 gate 조건을 충족한다. |

---

## 1. 범위와 목표

### 1.1 핵심 목표

1. Ctrl+C로 선택 텍스트 복사, 비선택 시 SIGINT 전송 (WT/Alacritty 공통 동작)
2. Ctrl+V로 안전한 붙여넣기 (C0/C1 필터 + Bracketed Paste + 줄바꿈 정규화)
3. OSC 52 Write/Read로 원격 앱(vim, tmux) 클립보드 연동
4. 멀티라인/대용량 경고 다이얼로그로 안전성 차별화
5. 우클릭 컨텍스트 메뉴, Copy-on-Select 등 편의 기능

### 1.2 성공 기준

| 지표                     | 목표                                       | 측정 방법                           |
| ------------------------ | ------------------------------------------ | ----------------------------------- |
| Ctrl+C/V 동작 정확성     | 선택 시 복사 100%, 비선택 시 SIGINT 100%   | 수동 smoke 테스트                   |
| CJK 복사 정확성          | 한글/일본어/중국어 왕복 일치율 100%        | CJK 텍스트 왕복 검증                |
| 보안 필터링              | C0/C1 제어 코드 + ESC 시퀀스 100% 차단     | 악성 페이로드 paste 시 무해화 확인  |
| Bracketed Paste          | vim/zsh 등 bracket-aware 앱에서 정상 동작  | vim insert mode paste 검증          |
| 클립보드 조작 지연       | 100ms 이내                                 | 주관적 체감 (필요 시 StopWatch)     |

### 1.3 Out of Scope

- HTML/RTF 복사 (WT만 지원하는 고급 기능 -- 후속 고려)
- Block 선택 (Alt+드래그) -- 별도 마일스톤
- 이미지 paste (cmux만 지원하는 니치 기능)
- 브로드캐스트 paste (멀티 pane 동시 전송 -- WT만 지원)

---

## 2. 기술 현황 분석

### 2.1 이미 준비된 인프라

| 항목                              | 상태 | 위치                                                              |
| --------------------------------- | :--: | ----------------------------------------------------------------- |
| `GetSelectedText` C API           | 완료 | `EngineService.cs:164` -> `ghostwin_engine.cpp:863`               |
| 텍스트 선택 (Cell/Word/Line)      | 완료 | `TerminalHostControl.cs` (M-10)                                   |
| DX11 선택 하이라이트              | 완료 | `ghostwin_engine.cpp:154`                                         |
| `SelectionChanged` 이벤트 발행    | 완료 | `TerminalHostControl.cs:59` (소비자 없음)                         |
| `SelectionState.Clear()` 메서드   | 완료 | `SelectionState.cs:92`                                            |
| `VT_MODE_BRACKETED_PASTE` 상수   | 완료 | `vt_bridge.h:152`                                                 |
| `vt_bridge_mode_get()` C 함수     | 완료 | `vt_bridge.c:363` -> `ghostty_terminal_mode_get()`                |
| `VtCore::mode_get()` C++ 래퍼     | 완료 | `vt_core.cpp:180`                                                 |
| `ISettingsService` + JSON 핫 리로드 | 완료 | `GhostWin.Core/Interfaces/ISettingsService.cs` (M-4)             |
| `IEngineService.WriteSession()`   | 완료 | `EngineService.cs` -> `gw_session_write()` -> `ConPty::send_input()` |
| Ghostty 클립보드 콜백 타입 정의   | 완료 | `ghostty.h:978~989` (read/write/confirm_read)                    |
| `ghostty_surface_text()` API      | 완료 | `ghostty.h:1103` (bracketed paste 자동 처리)                     |
| `edit.paste` 키바인딩 등록        | 완료 | `settings_manager.cpp:302` (핸들러 미구현)                        |

### 2.2 구현이 필요한 항목

| 항목                                  | 필요 작업                                                                                |
| ------------------------------------- | ---------------------------------------------------------------------------------------- |
| **`gw_session_mode_get` C API**       | `ghostwin_engine.cpp`에 export 함수 추가. `VtCore::mode_get()` 호출                     |
| **`NativeEngine.gw_session_mode_get`** | `NativeEngine.cs`에 P/Invoke 선언                                                       |
| **`IEngineService.GetMode()`**        | `IEngineService.cs` 인터페이스 + `EngineService.cs` 구현                                |
| **`ClipboardService` (새 서비스)**    | `GhostWin.Services` 프로젝트에 Copy/Paste/Filter/Normalize 로직 캡슐화                  |
| **Ctrl+C 이중 역할 분기**            | `MainWindow.xaml.cs:372` -- HasSelection 체크 후 복사 또는 SIGINT                        |
| **Ctrl+V 붙여넣기 파이프라인**        | Clipboard.GetText -> C0/C1 필터 -> 줄바꿈 정규화 -> Bracketed Paste 감싸기 -> WriteSession |
| **복사 후 선택 해제**                 | `SelectionState.Clear()` + `SetSelection(active=false)` + DX11 하이라이트 클리어          |
| **OSC 52 콜백 연결**                  | Ghostty runtime 콜백(`write_clipboard_cb`/`read_clipboard_cb`) 등록 경로 구축             |
| **멀티라인/대용량 경고 다이얼로그**   | WPF `Window` 기반 확인 다이얼로그 + 설정 연동                                             |
| **우클릭 컨텍스트 메뉴**              | WPF `ContextMenu` -- HwndHost 위 Airspace 문제 우회 필요 (Popup 방식)                     |
| **설정 JSON 클립보드 섹션**           | `AppSettings.cs`에 `ClipboardSettings` 추가 + `ISettingsService` 핫 리로드 연동           |

### 2.3 Paste 처리 경로 선택

두 가지 접근이 가능하다:

**경로 A: `ghostty_surface_text()` 사용 (cmux 패턴)**
- Ghostty 코어가 bracketed paste 자동 처리
- 단점: 현재 GhostWin에 surface 인스턴스 매핑 경로가 없음. C0/C1 필터는 호출 전에 직접 해야 함

**경로 B: `ConPty::send_input()` 직접 전송 (WT 패턴) -- 채택**
- 이미 `IEngineService.WriteSession()` -> `gw_session_write()` -> `ConPty::send_input()` 경로가 완비
- C0/C1 필터, 줄바꿈 정규화, bracket 감싸기를 WPF 레이어에서 완전 제어
- WT가 동일한 방식 (`ControlCore::PasteText`)

**채택 근거**: 경로 B는 기존 인프라를 그대로 활용하며, 보안 필터링/줄바꿈 정규화를 C# 레이어에서 정밀 제어할 수 있다. Bracketed paste bracket 감싸기만 직접 구현하면 된다. `mode_get` P/Invoke 1개 추가로 충분하다.

---

## 3. 구현 계획

### 3.1 Phase 1: 필수 복사/붙여넣기 (2~3일)

Phase 1은 클립보드 없이는 터미널을 사용할 수 없다는 gate 조건을 해소하는 최소 범위다.

#### FR-01: Ctrl+C 이중 역할

| 항목          | 내용 |
| ------------- | ---- |
| **수정 위치** | `src/GhostWin.App/MainWindow.xaml.cs` -- `OnTerminalKeyDown` 메서드 (현재 line 372) |
| **현재 동작** | `Key.C && Ctrl` -> 항상 `"\x03"` 전송 |
| **목표 동작** | active pane에 선택이 있으면 `ClipboardService.Copy()` 호출, 없으면 기존대로 `"\x03"` 전송 |
| **의존성**    | `TerminalHostControl._selection.IsActive` 상태 접근 (pane focus 기반), `ClipboardService` |
| **참조 구현** | WT `ControlInteractivity.cpp:227` -- `HasSelection()` false면 `return false` -> 키 이벤트 터미널 전달 |
| **수락 기준** | (1) 텍스트 선택 상태에서 Ctrl+C -> 클립보드에 텍스트 저장 (2) 비선택 상태에서 Ctrl+C -> `^C` 전송 확인 |

구현 순서:
1. Active pane의 `TerminalHostControl` 인스턴스에서 `_selection.IsActive` 및 `_selection.CurrentRange` 접근 경로 확보
2. `MainWindow.OnTerminalKeyDown`의 `Key.C && Ctrl` 분기에 HasSelection 체크 추가
3. 선택 있으면 -> `IEngineService.GetSelectedText()` 호출 -> `Clipboard.SetText()` -> 선택 해제

#### FR-02: Ctrl+V 붙여넣기 + 보안 필터링

| 항목          | 내용 |
| ------------- | ---- |
| **수정 위치** | `src/GhostWin.App/MainWindow.xaml.cs` -- `OnTerminalKeyDown`에 `Key.V && Ctrl` 분기 추가 |
| **새 파일**   | `src/GhostWin.Services/ClipboardService.cs` |
| **보안 필터** | HT(0x09), LF(0x0A), CR(0x0D) 외 모든 C0(0x00~0x1F) 및 C1(0x80~0x9F) 제거 (WT 방식) |
| **의존성**    | `System.Windows.Clipboard` (STA 필수), `IEngineService.WriteSession()` |
| **참조 구현** | WT `ControlCore::PasteText` -- C0/C1 필터 + bracket 감싸기 |
| **수락 기준** | ESC 시퀀스가 포함된 악성 텍스트 paste 시 제어 코드가 제거되어 무해화 |

보안 필터링 규칙 (WT `FilterStringForPaste` 동일):
```
허용: HT(0x09), LF(0x0A), CR(0x0D)
제거: 0x00~0x08, 0x0B~0x0C, 0x0E~0x1F (C0 중 허용 3개 제외)
제거: 0x80~0x9F (C1 전체)
```

#### FR-03: Bracketed Paste Mode (DECSET 2004)

| 항목            | 내용 |
| --------------- | ---- |
| **Engine 수정** | `src/engine-api/ghostwin_engine.cpp`에 `gw_session_mode_get()` export 함수 추가 |
| **Engine 헤더** | `src/engine-api/ghostwin_engine.h`에 `GWAPI int gw_session_mode_get(...)` 선언 |
| **P/Invoke**    | `src/GhostWin.Interop/NativeEngine.cs`에 `gw_session_mode_get` 추가 |
| **인터페이스**  | `src/GhostWin.Core/Interfaces/IEngineService.cs`에 `bool GetMode(uint sessionId, ushort modeValue)` 추가 |
| **사용 위치**   | `ClipboardService.Paste()` -- mode 2004 활성 시 `\x1b[200~` ... `\x1b[201~` 감싸기 |
| **참조 구현**   | WT `ControlCore::PasteText`, WezTerm `terminalstate/mod.rs:823` |
| **수락 기준**   | vim insert mode에서 paste 시 auto-indent가 작동하지 않음 (bracket이 정상 동작) |

C API 구현 경로:
```
gw_session_mode_get(engine, sessionId, modeValue, &out)
  -> Session::vt_core->mode_get(modeValue)
    -> vt_bridge_mode_get(terminal, mode_value, &out_value)
      -> ghostty_terminal_mode_get(terminal, mode, &out)
```

#### FR-04: 줄바꿈 정규화

| 항목          | 내용 |
| ------------- | ---- |
| **구현 위치** | `ClipboardService.NormalizeNewlines()` 내부 유틸 메서드 |
| **변환 규칙** | `\r\n` -> `\r`, 단독 `\n` -> `\r` (WT + Alacritty 공통 패턴) |
| **적용 조건** | Bracketed paste mode가 **비활성**일 때만 적용. Bracketed mode에서는 원본 유지 |
| **근거**      | 터미널 프로토콜에서 Enter = CR(0x0D). Windows 클립보드는 CRLF 기반 |
| **참조 구현** | WT `FilterStringForPaste`, Alacritty `event.rs:1376` |
| **수락 기준** | 메모장에서 복사한 멀티라인 텍스트가 bash에서 줄바꿈 정상 처리 |

#### FR-05: Ctrl+Shift+C/V 전용 키

| 항목          | 내용 |
| ------------- | ---- |
| **수정 위치** | `MainWindow.xaml.cs` -- `OnTerminalKeyDown`에 Shift 수정자 분기 추가 |
| **동작**      | Ctrl+Shift+C: 선택 있으면 복사 (없으면 무시). Ctrl+Shift+V: 항상 paste |
| **근거**      | WT/Linux 터미널 표준 단축키. Ctrl+C가 SIGINT와 충돌하므로 명시적 복사 경로 필요 |
| **수락 기준** | 선택 없이 Ctrl+Shift+C 누르면 아무 일 없음, 선택 있으면 복사 |

#### FR-06: Shift+Insert 붙여넣기

| 항목          | 내용 |
| ------------- | ---- |
| **수정 위치** | `MainWindow.xaml.cs` -- `OnTerminalKeyDown`에 `Key.Insert && Shift` 분기 추가 |
| **동작**      | Ctrl+V와 동일한 paste 파이프라인 호출 |
| **근거**      | Windows 전통 paste 단축키. 4개 참조 터미널 모두 지원 |
| **수락 기준** | Shift+Insert로 paste 정상 동작 |

#### FR-07: 복사 후 선택 해제

| 항목          | 내용 |
| ------------- | ---- |
| **수정 위치** | `ClipboardService.Copy()` 완료 후 -> pane의 `SelectionState.Clear()` + `IEngineService.SetSelection(active=false)` |
| **동작**      | Ctrl+C 또는 Ctrl+Shift+C로 복사 완료 후 선택 영역과 DX11 하이라이트를 동시에 해제 |
| **참조 구현** | WT/Alacritty 공통 동작. 복사 완료를 시각적으로 확인하는 UX |
| **수락 기준** | 복사 후 하이라이트가 즉시 사라짐 |

#### FR-14: OSC 52 Write+Read

| 항목                | 내용 |
| ------------------- | ---- |
| **구현 방식**       | Ghostty runtime 콜백 등록 방식 |
| **Write 경로**      | Ghostty 코어 -> `write_clipboard_cb` -> WPF `Clipboard.SetText()` |
| **Read 경로**       | Ghostty 코어 -> `read_clipboard_cb` -> 보안 정책 확인 -> `Clipboard.GetText()` -> `ghostty_surface_complete_clipboard_request()` |
| **Write 보안**      | 해당 session이 focused 상태일 때만 허용 |
| **Read 보안**       | 기본 `deny`. 설정 `clipboard.osc52Read`로 `"ask"` (확인 다이얼로그) 또는 `"allow"` 전환 가능 |
| **차별화**          | WT는 OSC 52 Read 미구현, WezTerm은 no-op. Windows 터미널 중 최초 Read 지원 |
| **Engine 수정**     | `ghostwin_engine.cpp`에 콜백 등록 경로 추가. `GwCallbackContext`에 클립보드 콜백 2종 추가 |
| **참조 구현**       | cmux `GhosttyTerminalView.swift:1533` (write_clipboard_cb), Alacritty `OnlyCopy` 기본 |
| **수락 기준**       | SSH 세션에서 `printf '\x1b]52;c;...\x07'` (OSC 52 Write) 실행 시 클립보드에 텍스트 저장 |

OSC 52 콜백 연결 경로:
```
ghostty_runtime_config_s {
    .write_clipboard_cb = on_write_clipboard,  // OSC 52 Write
    .read_clipboard_cb = on_read_clipboard,    // OSC 52 Read
    .confirm_read_clipboard_cb = on_confirm_read_clipboard,
}
```

현재 `ghostty_runtime_config_s` 콜백은 vt_bridge 레이어에서 설정되지 않고 있으므로, 별도 조사가 필요하다. GhostWin이 Ghostty를 VT 코어로만 사용하고 surface/runtime 레이어를 사용하지 않는 아키텍처이므로, **OSC 52 Write/Read는 VT 코어의 OSC 파싱 이벤트를 직접 처리하는 방식**이 필요할 수 있다.

**OSC 52 구현 전략 (Design 단계 확정 필요)**:
- 전략 A: `vt_bridge`에 OSC 콜백 등록 경로 추가 (ghostty terminal 수준)
- 전략 B: ConPTY 출력 스트림에서 OSC 52 시퀀스를 직접 파싱 (WT 방식)
- 전략 C: Ghostty runtime config의 콜백 체인을 VT-only 모드에서도 활성화

Design 단계에서 Ghostty 소스의 OSC 52 처리 흐름을 추적하여 최적 경로를 확정한다.

### 3.2 Phase 2: 안전성 (+1일)

Phase 2는 WT만 제공하는 안전성 기능을 추가하여 경쟁 차별화한다. Alacritty/WezTerm에는 없는 기능.

#### FR-08: 멀티라인 경고 다이얼로그

| 항목                | 내용 |
| ------------------- | ---- |
| **새 파일**         | `src/GhostWin.App/Dialogs/PasteWarningDialog.xaml` + `.cs` |
| **트리거 조건**     | paste 텍스트에 줄바꿈이 2줄 이상 (즉, `\n` 또는 `\r` 1개 이상 포함) |
| **다이얼로그 내용** | 줄 수 표시 + 첫 3줄 미리보기 + [붙여넣기] [취소] 버튼 + "다시 묻지 않기" 체크박스 |
| **설정 키**         | `clipboard.warnOnMultiLinePaste` (기본: `true`) |
| **참조 구현**       | WT `TerminalPage::_PasteTextWithBroadcast` |
| **수락 기준**       | 3줄 텍스트 paste 시 경고 표시, 체크박스 체크 후 경고 미표시 |

#### FR-09: 대용량(5KiB+) 경고

| 항목          | 내용 |
| ------------- | ---- |
| **트리거 조건** | paste 텍스트의 UTF-8 바이트 크기가 `largePasteThreshold` (기본 5120) 이상 |
| **다이얼로그** | FR-08과 동일한 `PasteWarningDialog` 재사용 (메시지만 다름: "약 XKB 텍스트를 붙여넣으려 합니다") |
| **설정 키**   | `clipboard.warnOnLargePaste` (기본: `true`), `clipboard.largePasteThreshold` (기본: `5120`) |
| **참조 구현** | WT 5KiB 임계값 |
| **수락 기준** | 6KiB 텍스트 paste 시 크기 경고 표시 |

#### FR-10: TrimPaste

| 항목          | 내용 |
| ------------- | ---- |
| **구현 위치** | `ClipboardService.Paste()` 파이프라인 내 |
| **동작**      | 단일 라인 paste 시 후행 공백/탭/개행 자동 제거. 앞쪽 공백은 유지 |
| **비활성 조건** | 멀티라인이면 비활성. Bracketed paste mode 활성 시 비활성 |
| **설정 키**   | `clipboard.trimPaste` (기본: `true`) |
| **참조 구현** | WT TrimPaste 동작 (단일 라인만, 앞 공백 유지) |
| **수락 기준** | `" ls -la \n"` paste 시 `" ls -la"` 결과 전달 (앞 공백 유지, 뒤 공백/개행 제거) |

### 3.3 Phase 3: 편의/확장 (+1~2일)

#### FR-11: 우클릭 컨텍스트 메뉴

| 항목          | 내용 |
| ------------- | ---- |
| **새 파일**   | `src/GhostWin.App/Controls/TerminalContextMenu.cs` (Popup 기반) |
| **메뉴 항목** | [복사] [붙여넣기] [모두 선택] |
| **조건**      | 선택 없으면 [복사] 비활성 (회색) |
| **기술 이슈** | HwndHost 위 WPF ContextMenu는 Airspace 문제로 가려질 수 있음. Popup Window 방식 필요 |
| **참조 구현** | WT (설정으로 토글), cmux |
| **수락 기준** | 터미널 영역 우클릭 시 메뉴 표시, 복사/붙여넣기 동작 |

#### FR-12: Copy-on-Select 설정 옵션

| 항목          | 내용 |
| ------------- | ---- |
| **구현 위치** | `TerminalHostControl.cs` -- `SelectionChanged` 이벤트 소비자 추가 |
| **동작**      | 마우스 선택 완료(WM_LBUTTONUP) 시 자동으로 클립보드에 복사 |
| **설정 키**   | `clipboard.copyOnSelect` (기본: `false`) |
| **참조 구현** | WT `copyOnSelect`, Alacritty `selection.save_to_clipboard` |
| **수락 기준** | 설정 `true` 시 텍스트 드래그 완료와 동시에 클립보드에 복사 |

#### FR-13: 설정 JSON 클립보드 섹션

| 항목          | 내용 |
| ------------- | ---- |
| **수정 위치** | `src/GhostWin.Core/Models/AppSettings.cs` |
| **새 모델**   | `ClipboardSettings` 클래스 추가 |
| **설정 키 목록** | `copyOnSelect`, `trimPaste`, `warnOnMultiLinePaste`, `warnOnLargePaste`, `largePasteThreshold`, `osc52Write`, `osc52Read` |
| **핫 리로드** | M-4 `ISettingsService` 핫 리로드로 즉시 반영 (기존 인프라 활용) |
| **수락 기준** | `settings.json`에서 `clipboard.copyOnSelect: true` 저장 후 자동 반영 |

---

## 4. 아키텍처

### 4.1 레이어별 수정 범위

| 레이어 | 프로젝트 | 수정 내용 |
| ------ | -------- | --------- |
| **Engine C API** | `src/engine-api/` | `gw_session_mode_get()` 1개 함수 추가 |
| **Engine 헤더** | `src/engine-api/ghostwin_engine.h` | API 선언 1개 추가 |
| **Interop** | `src/GhostWin.Interop/` | P/Invoke 1개 (`gw_session_mode_get`), `EngineService.GetMode()` 구현 |
| **Core 인터페이스** | `src/GhostWin.Core/` | `IEngineService`에 `GetMode()` 추가, `ClipboardSettings` 모델, `IClipboardService` 인터페이스 |
| **Services** | `src/GhostWin.Services/` | `ClipboardService` 신규 (Copy/Paste/Filter/Normalize 로직) |
| **App** | `src/GhostWin.App/` | `MainWindow.xaml.cs` 키 분기 수정, `PasteWarningDialog`, 우클릭 메뉴 |

### 4.2 Paste 처리 파이프라인

```
[사용자 Ctrl+V / Shift+Insert / Ctrl+Shift+V]
    |
    v
MainWindow.OnTerminalKeyDown
    |
    v
ClipboardService.PasteAsync(sessionId)
    |
    +-- (1) Clipboard.GetText() -- STA Dispatcher
    |
    +-- (2) 크기 체크 -- FR-09 대용량 경고 (>= 5KiB)
    |
    +-- (3) 멀티라인 체크 -- FR-08 멀티라인 경고
    |
    +-- (4) TrimPaste -- FR-10 단일 라인 후행 공백 제거
    |
    +-- (5) C0/C1 보안 필터링 -- FR-02
    |       허용: HT(0x09), LF(0x0A), CR(0x0D)
    |       제거: 나머지 C0(0x00~0x1F) + C1(0x80~0x9F)
    |
    +-- (6) mode_get(2004) 체크
    |       |
    |       +-- Bracketed=true: "\x1b[200~" + text + "\x1b[201~"
    |       +-- Bracketed=false: 줄바꿈 정규화 (\r\n->\r, \n->\r)
    |
    +-- (7) IEngineService.WriteSession(sessionId, bytes)
    |
    +-- (8) ScrollViewport(sessionId, int.MaxValue) -- 자동 스크롤
```

### 4.3 ClipboardService 설계

```csharp
// src/GhostWin.Core/Interfaces/IClipboardService.cs
public interface IClipboardService
{
    Task<bool> CopyAsync(uint sessionId, SelectionRange range);
    Task<bool> PasteAsync(uint sessionId);
    bool HasSelection(uint paneId);
}
```

`ClipboardService`는 DI로 주입되며, `IEngineService`, `ISettingsService`에 의존한다. `System.Windows.Clipboard`는 STA 스레드에서만 접근 가능하므로 `Dispatcher.InvokeAsync`로 마셜링한다.

OLE 재시도 로직 (NFR-03):
```
최대 3회 재시도, 50ms 간격
ExternalException (CLIPBRD_E_CANT_OPEN) catch -> 재시도
3회 초과 시 무시 (사용자에게 오류 표시 안 함)
```

---

## 5. 기술 위험과 완화

| 위험 | 영향 | 확률 | 완화 전략 | 검증 시점 |
| ---- | :--: | :--: | --------- | --------- |
| `mode_get` P/Invoke 래핑 실패 | 높음 | 낮음 | C++ 래퍼 이미 존재 (`VtCore::mode_get`). export 함수 추가만 필요. Phase 1 첫 작업에서 즉시 검증 | Phase 1 Day 1 |
| WPF `Clipboard` OLE 점유 경합 | 중간 | 중간 | `try/catch` + 3회 재시도 (50ms 간격). 사용 빈도가 낮아(초당 수 회 미만) 병목 아님 | Phase 1 |
| Bracketed paste에서 필터링이 bracket 마커 훼손 | 높음 | 낮음 | 처리 순서 보장: 필터링 먼저 -> bracket 감싸기 나중 (WT 구현과 동일) | Phase 1 |
| OSC 52 콜백 연결 경로 부재 | 중간 | 중간 | GhostWin이 Ghostty를 VT 코어로만 사용하므로 runtime 콜백이 연결되지 않을 수 있음. Design 단계에서 Ghostty 소스 추적 필요. 최악의 경우 ConPTY 출력 스트림에서 OSC 52 직접 파싱 (WT 방식 대안) | Phase 1 Day 2~3 |
| HwndHost 위 Airspace 문제로 ContextMenu 가림 | 중간 | 중간 | Popup Window 방식 우회 (Phase 3). M-12 Command Palette과 동일 이슈이므로 공통 해결 | Phase 3 |
| 대용량 paste (1MB+) 시 UI 프리즈 | 중간 | 낮음 | FR-09 대용량 경고로 선제 차단 + 비동기 필터링 | Phase 2 |

---

## 6. 의존성

### 6.1 선행 완료 항목 (모두 충족)

| 항목 | 상태 | 비고 |
| ---- | :--: | ---- |
| M-10 텍스트 선택 + DX11 하이라이트 | 완료 | Cell/Word/Line 선택, `GetSelectedText` C API |
| M-4 설정 시스템 | 완료 | `ISettingsService` + JSON 핫 리로드 |
| `VT_MODE_BRACKETED_PASTE` 상수 | 완료 | `vt_bridge.h:152` |
| `vt_bridge_mode_get()` C 함수 | 완료 | `vt_bridge.c:363` |

### 6.2 Phase 간 의존성

| Phase | 의존하는 Phase | 내용 |
| :---: | :---: | ---- |
| Phase 2 | Phase 1 | Phase 2의 경고 다이얼로그는 Phase 1의 paste 파이프라인에 삽입 |
| Phase 3 (FR-12) | Phase 1 | Copy-on-Select는 Phase 1의 `ClipboardService.Copy()` 재사용 |
| Phase 3 (FR-13) | Phase 2 | 설정 섹션은 Phase 2의 설정 키를 포함 |

### 6.3 외부 의존성

| 항목 | 상태 | 비고 |
| ---- | :--: | ---- |
| `System.Windows.Clipboard` | .NET 10 내장 | WPF 기본 제공. 추가 패키지 불필요 |
| Ghostty `ghostty_terminal_mode_get` | 확인 | `external/ghostty` 서브모듈에 이미 포함 |

---

## 7. 구현 순서 (마일스톤)

### Phase 1: 필수 복사/붙여넣기 (Day 1~3)

| Step | 작업 | 산출물 | 검증 |
| :--: | ---- | ------ | ---- |
| 1 | `gw_session_mode_get` C API + P/Invoke + `IEngineService.GetMode()` | engine 수정 3파일, interop 2파일, core 1파일 | `GetMode(sessionId, 2004)` 호출 성공 |
| 2 | `IClipboardService` 인터페이스 + `ClipboardService` 구현 (Copy/Paste/Filter/Normalize) | core 1파일, services 1파일 | 단위 로직 검증 |
| 3 | `MainWindow.xaml.cs` 키 분기 수정: Ctrl+C 이중 역할 + Ctrl+V paste + Ctrl+Shift+C/V + Shift+Insert | app 1파일 수정 | Ctrl+C/V smoke 테스트 |
| 4 | 복사 후 선택 해제 (SelectionState.Clear + SetSelection(false)) | app 수정 | 복사 후 하이라이트 사라짐 확인 |
| 5 | OSC 52 Write/Read 콜백 연결 (Design 단계 경로 확정 후) | engine + interop 수정 | SSH에서 OSC 52 Write 검증 |

### Phase 2: 안전성 (Day 4)

| Step | 작업 | 산출물 | 검증 |
| :--: | ---- | ------ | ---- |
| 6 | `PasteWarningDialog` XAML + 멀티라인/대용량 경고 | app 2파일 신규 | 3줄 텍스트 / 6KiB 텍스트 paste 시 경고 |
| 7 | TrimPaste + `ClipboardSettings` 모델 + 설정 연동 | core 1파일, services 수정, app 수정 | 단일 라인 후행 공백 제거 + 설정 핫 리로드 |

### Phase 3: 편의/확장 (Day 5~6, 별도 마일스톤 가능)

| Step | 작업 | 산출물 | 검증 |
| :--: | ---- | ------ | ---- |
| 8 | 우클릭 컨텍스트 메뉴 (Popup 방식) | app 1파일 신규 | 우클릭 시 메뉴 표시 |
| 9 | Copy-on-Select + 설정 JSON 클립보드 섹션 | app + core 수정 | 설정 `true` 시 자동 복사 |

---

## 8. 테스트 전략

### 8.1 수동 Smoke 테스트 (Phase 1 필수)

| ID | 시나리오 | 기대 결과 |
| -- | -------- | --------- |
| S1 | 텍스트 선택 후 Ctrl+C -> 메모장에 Ctrl+V | 선택 텍스트가 메모장에 표시 |
| S2 | 텍스트 비선택 상태에서 Ctrl+C | `^C` 표시 (SIGINT 전송) |
| S3 | 메모장에서 3줄 복사 후 bash에 Ctrl+V | 3줄이 각각 실행됨 (줄바꿈 정규화) |
| S4 | vim insert mode에서 Ctrl+V paste | auto-indent 미작동 (bracket 정상) |
| S5 | ESC 시퀀스 포함 텍스트 paste | 제어 코드 제거, 무해화된 텍스트만 입력 |
| S6 | Ctrl+Shift+C (선택 없음) | 아무 일 없음 |
| S7 | Ctrl+Shift+V | paste 동작 |
| S8 | Shift+Insert | paste 동작 |
| S9 | 한글 텍스트 복사 -> paste 왕복 | 글자 깨짐 없이 정확히 일치 |
| S10 | 복사 후 DX11 하이라이트 상태 | 하이라이트 즉시 사라짐 |
| S11 | 다른 앱이 클립보드 점유 중 Ctrl+C | 재시도 후 정상 복사 또는 무시 (앱 크래시 없음) |

### 8.2 Phase 2 테스트

| ID | 시나리오 | 기대 결과 |
| -- | -------- | --------- |
| S12 | 3줄 텍스트 paste (경고 활성) | 경고 다이얼로그 표시, [붙여넣기] 클릭 시 정상 paste |
| S13 | "다시 묻지 않기" 체크 후 3줄 paste | 경고 미표시, 바로 paste |
| S14 | 6KiB 텍스트 paste | "약 6KB" 크기 경고 표시 |
| S15 | `" ls -la \n"` paste (TrimPaste 활성) | `" ls -la"` 전달 |

### 8.3 Phase 3 테스트

| ID | 시나리오 | 기대 결과 |
| -- | -------- | --------- |
| S16 | 터미널 우클릭 (텍스트 선택 상태) | [복사] [붙여넣기] [모두 선택] 메뉴 표시 |
| S17 | 터미널 우클릭 (비선택 상태) | [복사] 회색 비활성 |
| S18 | Copy-on-Select 설정 `true` + 텍스트 드래그 | 드래그 완료 시 자동 복사 |

---

## 9. 파일 변경 요약

### 신규 파일

| 파일 | Phase | 설명 |
| ---- | :---: | ---- |
| `src/GhostWin.Core/Interfaces/IClipboardService.cs` | 1 | 클립보드 서비스 인터페이스 |
| `src/GhostWin.Core/Models/ClipboardSettings.cs` | 2 | 클립보드 설정 모델 |
| `src/GhostWin.Services/ClipboardService.cs` | 1 | Copy/Paste/Filter/Normalize 구현 |
| `src/GhostWin.App/Dialogs/PasteWarningDialog.xaml` | 2 | 경고 다이얼로그 XAML |
| `src/GhostWin.App/Dialogs/PasteWarningDialog.xaml.cs` | 2 | 경고 다이얼로그 코드비하인드 |
| `src/GhostWin.App/Controls/TerminalContextMenu.cs` | 3 | 우클릭 컨텍스트 메뉴 |

### 수정 파일

| 파일 | Phase | 수정 내용 |
| ---- | :---: | --------- |
| `src/engine-api/ghostwin_engine.h` | 1 | `gw_session_mode_get` 선언 추가 |
| `src/engine-api/ghostwin_engine.cpp` | 1 | `gw_session_mode_get` 구현 + OSC 52 콜백 |
| `src/GhostWin.Interop/NativeEngine.cs` | 1 | `gw_session_mode_get` P/Invoke |
| `src/GhostWin.Interop/EngineService.cs` | 1 | `GetMode()` 구현 |
| `src/GhostWin.Core/Interfaces/IEngineService.cs` | 1 | `GetMode()` 인터페이스 추가 |
| `src/GhostWin.Core/Models/AppSettings.cs` | 2 | `ClipboardSettings` 프로퍼티 추가 |
| `src/GhostWin.App/MainWindow.xaml.cs` | 1 | Ctrl+C 이중 역할, Ctrl+V paste, Shift+Insert, Ctrl+Shift+C/V |
| `src/GhostWin.App/App.xaml.cs` | 1 | DI에 `IClipboardService` 등록 |

---

## 10. 일정 요약

| Phase   | 포함 FR              | 예상 소요 | 누적 |
| :-----: | -------------------- | :-------: | :--: |
| Phase 1 | FR-01~FR-07, FR-14   |   2~3일   | 3일  |
| Phase 2 | FR-08~FR-10          |   +1일    | 4일  |
| Phase 3 | FR-11~FR-13          |  +1~2일   | 6일  |

Phase 1 완료 시점에서 터미널이 "일상 도구"로 사용 가능해진다. Phase 2~3은 품질 개선이므로 별도 마일스톤으로 분리 가능하다.

---

## 참조 문서

| 문서 | 경로 |
| ---- | ---- |
| Clipboard PRD v1.1 | `docs/00-pm/clipboard.prd.md` |
| 클립보드 기술 리서치 (4 터미널 비교) | `docs/00-research/research-clipboard-copy-paste.md` |
| WPF Migration Plan | `docs/01-plan/features/wpf-migration.plan.md` |
| WPF Migration Design | `docs/02-design/features/wpf-migration.design.md` |
| M-10 마우스 입력 아카이브 | `docs/archive/2026-04/mouse-input/` |
| Settings System (M-4) 아카이브 | `docs/archive/2026-04/settings-system/` |
| Roadmap | `docs/01-plan/roadmap.md` |

---

## Version History

| Version | Date | Changes |
| ------- | ---- | ------- |
| 0.1 | 2026-04-11 | Initial Plan -- PRD v1.1 + 리서치 기반 작성 |
