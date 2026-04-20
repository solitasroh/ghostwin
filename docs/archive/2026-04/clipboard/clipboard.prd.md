# Clipboard PRD -- GhostWin M-10.5

> Version 1.0 | 작성일: 2026-04-11

## Executive Summary

| 관점               | 내용                                                                                                                                                                                                                                                                               |
| ------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Problem**        | GhostWin은 텍스트 선택과 DX11 하이라이트까지 완성했지만 클립보드 코드가 전혀 없어서, 사용자가 선택한 텍스트를 복사하거나 외부 텍스트를 붙여넣을 수 없다. Ctrl+C는 항상 SIGINT(0x03)를 보내고, Ctrl+V는 아무 동작도 하지 않는다. 이 상태에서는 터미널을 일상 도구로 사용할 수 없다. |
| **Solution**       | Ctrl+C 이중 역할(선택 있으면 복사, 없으면 SIGINT) + Ctrl+V 안전 붙여넣기(C0/C1 필터, Bracketed Paste, 줄바꿈 정규화) + 멀티라인/대용량 경고 다이얼로그를 3단계로 구현한다.                                                                                                         |
| **기능적 UX 효과** | "선택하면 바로 복사, 붙여넣기는 안전하게" -- 개발자가 서버 작업 중 위험한 멀티라인 명령을 실수로 실행하는 사고를 차단하면서, CJK 전각 문자도 정확히 복사된다.                                                                                                                      |
| **핵심 가치**      | WT 수준 기능 범위 + Alacritty 수준 성능 + WPF 네이티브 안정성으로, "데모용 터미널"에서 "일상 도구"로 전환하는 gate 조건을 충족한다.                                                                                                                                                |

---

## 1. Product Overview

### 1.1 기본 정보

| 항목          | 내용                                |
| ------------- | ----------------------------------- |
| Feature       | Clipboard Copy/Paste                |
| Milestone     | M-10.5                              |
| 선행 마일스톤 | M-10 마우스 입력 (완료, 2026-04-11) |
| 후속 마일스톤 | M-11 세션 지속성                    |
| 브랜치        | `feature/wpf-migration`             |

### 1.2 배경과 동기

GhostWin은 Phase 1~4 (DX11 렌더링 + ConPTY + WPF Shell + IME + ClearType)를 거쳐
Phase 5 (다중 세션/Workspace/Pane) + M-10 (마우스 클릭/스크롤/텍스트 선택)까지 완성했다.
그러나 **클립보드 기능이 전혀 없는 상태**로, 터미널의 가장 기본적인 작업 흐름이 끊겨 있다.

클립보드는 터미널이 "기술 데모"에서 "일상 도구"로 전환하는 gate 조건이다.
4개 참조 터미널(WT, Alacritty, WezTerm, cmux) 모두 클립보드를 핵심 기능으로 제공하며,
이 없이는 얼리 어답터조차 일상 사용을 포기한다.

현재 코드 상태:

- `GetSelectedText` C API 완비 (UTF-8, 후행 공백 트림)
- 텍스트 선택 (Cell/Word/Line) + DX11 하이라이트 완료
- `SelectionChanged` 이벤트 발행 중 (소비자 없음)
- `VT_MODE_BRACKETED_PASTE` 상수 + `mode_get()` C++ API 존재
- `edit.paste` 키바인딩 등록 완료 (핸들러 미구현)
- Ctrl+C는 `MainWindow.xaml.cs:376`에서 항상 `0x03` 전송

### 1.3 성공 지표

| 지표                     | 목표                                                        | 측정 방법                          |
| ------------------------ | ----------------------------------------------------------- | ---------------------------------- |
| **Ctrl+C/V 동작 정확성** | 선택 시 복사 100%, 비선택 시 SIGINT 100%                    | 수동 smoke 테스트                  |
| **CJK 복사 정확성**      | 한글/일본어/중국어 전각 문자 복사-붙여넣기 왕복 일치율 100% | CJK 텍스트 왕복 검증               |
| **보안 필터링**          | C0/C1 제어 코드 + ESC 시퀀스 100% 차단                      | 악성 페이로드 paste 시 무해화 확인 |
| **Bracketed Paste**      | vim/zsh 등 bracket-aware 앱에서 정상 동작                   | vim insert mode paste 검증         |
| **클립보드 조작 지연**   | 100ms 이내                                                  | 주관적 체감 + StopWatch (필요 시)  |

---

## 2. Opportunity Analysis

### 2.1 Opportunity Solution Tree (Discovery 결과)

PM Discovery 에이전트가 식별한 6개 기회 영역:

| ID     | Opportunity                               | Score | Phase |
| ------ | ----------------------------------------- | :---: | :---: |
| **O1** | 선택 텍스트를 클립보드에 넣을 방법이 없음 | 1.00  |   1   |
| **O2** | 안전한 붙여넣기 불가 (보안 필터 없음)     | 1.00  |   1   |
| **O4** | Ctrl+C가 항상 SIGINT -- 복사 불가능       | 0.90  |   1   |
| **O3** | 멀티라인/대용량 붙여넣기 위험             | 0.85  |   2   |
| **O6** | WT 사용자 단축키 비호환 (Ctrl+Shift+C/V)  | 0.63  |   1   |
| **O5** | OSC 52 원격 클립보드 미지원               | 0.60  |   1   |

### 2.2 우선순위 결정 근거

- **Phase 1 (O1+O2+O4+O5+O6)**: 클립보드 없이는 터미널을 쓸 수 없다. Score 0.90 이상 3건을 포함하며, O6(WT 키 호환)도 구현 비용이 극소이므로 동시 투입. O5(OSC 52)는 WT가 Read 미지원하므로 Write+Read 동시 구현으로 차별화
- **Phase 2 (O3)**: 멀티라인/대용량 경고는 안전성 차별화 포인트. WT만 제공하는 기능으로 경쟁 우위
- **Phase 3**: 우클릭 메뉴, Copy-on-Select 등 편의 기능

### 2.3 3단계 구현 전략

```
Phase 1 (필수)          Phase 2 (안전성)       Phase 3 (편의/확장)
─────────────          ──────────────        ────────────────
Ctrl+C 이중 역할        멀티라인 경고           우클릭 컨텍스트 메뉴
Ctrl+V 안전 paste       대용량(5KiB+) 경고     Copy-on-Select 설정
Bracketed Paste Mode    TrimPaste              설정 JSON 클립보드 섹션
줄바꿈 정규화                                   설정 JSON 클립보드 섹션
Ctrl+Shift+C/V 전용
Shift+Insert
복사 후 선택 해제
OSC 52 Write+Read
```

---

## 3. User Personas (JTBD)

### Persona 1: DevOps 김 -- 서버 관리자

| 항목            | 내용                                                                  |
| --------------- | --------------------------------------------------------------------- |
| **직무**        | 10대 이상 Linux 서버 SSH 관리                                         |
| **주요 도구**   | PowerShell, SSH, kubectl, Ansible                                     |
| **핵심 JTBD**   | "서버 로그에서 오류 메시지를 빠르게 복사해서 Slack에 붙여넣고 싶다"   |
| **핵심 Pain**   | 멀티라인 명령을 실수로 paste하면 프로덕션 서버에 위험한 명령이 실행됨 |
| **기대 Gain**   | 멀티라인 경고 + C0/C1 필터링으로 안전한 붙여넣기                      |
| **전환 트리거** | WT에서 멀티라인 경고 없이 paste 사고를 겪은 적 있음                   |

### Persona 2: Full-stack 이 -- WSL 개발자

| 항목            | 내용                                                                     |
| --------------- | ------------------------------------------------------------------------ |
| **직무**        | React + Node.js 풀스택, WSL2 Ubuntu 환경                                 |
| **주요 도구**   | vim, tmux, Docker, git                                                   |
| **핵심 JTBD**   | "StackOverflow 코드를 복사해서 vim에 paste할 때 서식이 깨지지 않았으면"  |
| **핵심 Pain**   | Bracketed paste 미지원 터미널에서 vim auto-indent가 코드를 엉망으로 만듦 |
| **기대 Gain**   | Bracketed paste + 줄바꿈 정규화로 vim/zsh에서 깨끗한 paste               |
| **전환 트리거** | Alacritty GPU 성능은 좋지만 Windows 네이티브가 아니라 불편               |

### Persona 3: 박 팀장 -- CJK 파워 유저

| 항목            | 내용                                                                      |
| --------------- | ------------------------------------------------------------------------- |
| **직무**        | 다국어 웹 서비스 운영, 한/중/일 로그 분석                                 |
| **주요 도구**   | grep, awk, jq, 한글 경로 + CJK 로그                                       |
| **핵심 JTBD**   | "한글이 포함된 에러 메시지를 정확히 복사해서 이슈 트래커에 등록하고 싶다" |
| **핵심 Pain**   | 전각 문자 셀 폭 불일치로 복사하면 글자가 잘리거나 공백이 추가됨           |
| **기대 Gain**   | CJK 전각 셀 정밀 처리 + 후행 공백 트림으로 깨끗한 복사                    |
| **전환 트리거** | WT에서 CJK 복사 시 글자 잘림 경험                                         |

---

## 4. Competitive Landscape

### 4.1 기능 비교 매트릭스

| 기능             |      WT       |    Alacritty    |    WezTerm     |       cmux       |   **GhostWin (목표)**    |
| ---------------- | :-----------: | :-------------: | :------------: | :--------------: | :----------------------: |
| Ctrl+C 이중 역할 |       O       |        O        |       O        |        O         |     **O (Phase 1)**      |
| Bracketed Paste  |       O       |        O        |       O        |        O         |     **O (Phase 1)**      |
| C0/C1 보안 필터  | O (가장 엄격) | 부분 (ESC+^C만) | 부분 (de-fang) |   Ghostty 내부   | **O (WT 수준, Phase 1)** |
| 줄바꿈 정규화    |       O       | O (비bracket만) |  O (플랫폼별)  |   Ghostty 내부   |     **O (Phase 1)**      |
| 멀티라인 경고    |   O (설정)    |        X        |       X        | O (Ghostty 설정) |     **O (Phase 2)**      |
| 대용량 경고      |   O (5KiB)    |        X        |       X        |        X         |     **O (Phase 2)**      |
| TrimPaste        |       O       |        X        |       X        |        X         |     **O (Phase 2)**      |
| HTML/RTF 복사    |       O       |        X        |       X        |        X         |      X (후속 고려)       |
| Copy-on-Select   |       O       |        O        |       O        |        O         |     **O (Phase 3)**      |
| OSC 52 Write     |       O       |        O        |       O        |        O         |     **O (Phase 1)**      |
| OSC 52 Read      |       X       |    O (설정)     |       X        |     O (설정)     |     **O (Phase 1)**      |
| 우클릭 메뉴      |       O       |        X        |       X        |        O         |     **O (Phase 3)**      |
| Block 선택       |       O       |        O        |       O        |        O         |         X (후속)         |

### 4.2 차별화 전략 (3축)

| 축                 | 내용                                                                      | 근거                                                                   |
| ------------------ | ------------------------------------------------------------------------- | ---------------------------------------------------------------------- |
| **CJK 정밀도**     | 전각 문자 셀 폭 + 단어 경계 + 후행 공백 트림이 정확하게 동작              | M-10에서 구현한 CJK word boundary + DX11 하이라이트가 이미 차별화 기반 |
| **안전한 Paste**   | WT 수준 C0/C1 필터 + 멀티라인/대용량 경고 (Alacritty/WezTerm에 없음)      | DevOps 서버 관리자에게 사고 방지 핵심 기능                             |
| **서식 유지 복사** | 후속으로 HTML/RTF 복사 지원 가능 (WT만 제공하는 기능, WPF에서 자연스러움) | WPF `Clipboard.SetData`가 multi-format 지원                            |

---

## 5. Value Proposition & Lean Canvas

### 5.1 JTBD 6-Part Value Proposition

| 파트                   | 내용                                                                          |
| ---------------------- | ----------------------------------------------------------------------------- |
| **When**               | 터미널에서 로그/코드를 분석하거나, 외부 텍스트를 붙여넣을 때                  |
| **I want to**          | 선택한 텍스트를 한 번에 복사하고, 외부 텍스트를 안전하게 붙여넣고 싶다        |
| **So I can**           | 위험 명령 실행 사고 없이 빠르게 작업을 이어갈 수 있다                         |
| **Unlike**             | Alacritty (멀티라인 경고 없음), WezTerm (대용량 경고 없음)                    |
| **Our product**        | WT 수준 보안 필터 + CJK 정밀 복사 + GPU 렌더링 성능을 하나로 제공한다         |
| **Key differentiator** | CJK 전각 문자 복사 정확도, 멀티라인/대용량 경고, WPF 네이티브 클립보드 안정성 |

### 5.2 Lean Canvas

| 블록                  | 내용                                                                                               |
| --------------------- | -------------------------------------------------------------------------------------------------- |
| **Problem**           | (1) 클립보드 기능 부재로 터미널 사용 불가 (2) Ctrl+C가 항상 SIGINT (3) 보안 필터 없는 paste는 위험 |
| **Customer Segments** | Windows 개발자: DevOps/SRE, 풀스택, CJK 다국어 환경 사용자                                         |
| **Unique Value Prop** | WT 기능 범위 + Alacritty 성능 + WPF 네이티브 안정성                                                |
| **Solution**          | 3단계 구현: 필수 Copy/Paste -> 안전성 경고 -> 편의/확장                                            |
| **Channels**          | GitHub 릴리스, 개발자 커뮤니티 (X/Reddit), CJK 개발자 포럼                                         |
| **Revenue Streams**   | 오픈소스 (사용자 기반 확대 -> 후원/스폰서)                                                         |
| **Cost Structure**    | 개인 개발자 시간 (Phase 1 약 1-2일, Phase 2 약 1일)                                                |
| **Key Metrics**       | 클립보드 사용 빈도, CJK 복사 정확도, paste 사고 0건                                                |
| **Unfair Advantage**  | Ghostty VT 코어 + DX11 GPU 렌더링 + WPF 네이티브 통합 (동일 조합 경쟁자 없음)                      |

### 5.3 핵심 가정 (검증 필요)

|  #  | 가정                                                      | 검증 방법                                                | 위험도 |
| :-: | --------------------------------------------------------- | -------------------------------------------------------- | :----: |
| A1  | WPF `Clipboard` API가 CF_UNICODETEXT를 정확히 처리        | 한글/일본어/이모지 왕복 테스트                           |  낮음  |
| A2  | `mode_get(2004)` P/Invoke 래핑이 가능                     | `ghostwin_engine.cpp`에 wrapper 추가 후 호출             |  낮음  |
| A3  | C0/C1 필터가 정상적인 텍스트를 훼손하지 않음              | HT/LF/CR 허용 목록 기반 필터 + 엣지 케이스 테스트        |  중간  |
| A4  | 멀티라인 경고 UX가 작업 흐름을 과도하게 방해하지 않음     | "다시 묻지 않기" 체크박스 + 설정으로 비활성화 가능       |  중간  |
| A5  | `ghostty_surface_text()`로 bracketed paste가 자동 처리됨  | cmux 참조 코드 확인 + 실제 vim 테스트                    |  낮음  |
| A6  | OLE 클립보드 점유 시 재시도 로직이 필요                   | `Clipboard.SetText` 실패 시 `CLIPBRD_E_CANT_OPEN` 재시도 |  중간  |
| A7  | Copy-on-Select 기본값 false가 Windows 사용자 기대에 부합  | WT/Alacritty 모두 기본 false                             |  낮음  |
| A8  | 줄바꿈 정규화 `\r\n`->`\r`, `\n`->`\r`이 모든 쉘에서 안전 | bash/zsh/PowerShell + vim/nano 테스트                    |  낮음  |

---

## 6. Product Requirements

### 6.1 Phase 1: 필수 복사/붙여넣기 (Must-Have)

#### FR-01: Ctrl+C 이중 역할

| 항목          | 내용                                                                               |
| ------------- | ---------------------------------------------------------------------------------- |
| **설명**      | 텍스트 선택이 있으면 클립보드에 복사, 없으면 SIGINT(0x03) 전송                     |
| **현재 상태** | `MainWindow.xaml.cs:376` -- 항상 `0x03` 전송                                       |
| **구현 위치** | `MainWindow.xaml.cs` HandleKeyDown 분기                                            |
| **참조**      | WT `ControlInteractivity.cpp:227`, Alacritty `input/mod.rs:323`                    |
| **수락 기준** | 선택 상태에서 Ctrl+C -> 클립보드에 텍스트 저장, 비선택 상태에서 Ctrl+C ->`^C` 전송 |

#### FR-02: Ctrl+V 붙여넣기 + 보안 필터링

| 항목          | 내용                                                                               |
| ------------- | ---------------------------------------------------------------------------------- |
| **설명**      | 클립보드 텍스트를 읽어 C0/C1 제어 코드를 필터링한 후 세션에 전달                   |
| **보안 필터** | HT(0x09), LF(0x0A), CR(0x0D) 외 모든 C0(0x00~0x1F) 및 C1(0x80~0x9F) 제거 (WT 방식) |
| **구현 위치** | `MainWindow.xaml.cs` + 새 `ClipboardHelper` 유틸리티                               |
| **참조**      | WT `ControlCore::PasteText`, Alacritty `event.rs:1376`                             |
| **수락 기준** | ESC 시퀀스가 포함된 악성 텍스트 paste 시 제어 코드가 제거되어 무해화               |

#### FR-03: Bracketed Paste Mode (DECSET 2004)

| 항목          | 내용                                                                                                                          |
| ------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| **설명**      | 터미널이 bracketed paste mode(2004)를 활성화한 경우 paste 텍스트를 `\x1b[200~` ... `\x1b[201~`로 감싸서 전송                  |
| **현재 상태** | `VT_MODE_BRACKETED_PASTE` 상수 + `VtCore::mode_get()` API 존재, P/Invoke 미노출                                               |
| **구현 작업** | (1)`ghostwin_engine.cpp`에 `mode_get` wrapper 추가 (2) `EngineService.cs`에 P/Invoke (3) paste 시 mode 체크 후 bracket 감싸기 |
| **참조**      | WT `ControlCore::PasteText`, WezTerm `terminalstate/mod.rs:823`                                                               |
| **수락 기준** | vim insert mode에서 paste 시 auto-indent 미작동 (bracket이 정상 동작)                                                         |

#### FR-04: 줄바꿈 정규화

| 항목          | 내용                                                                  |
| ------------- | --------------------------------------------------------------------- |
| **설명**      | Windows 클립보드의 `\r\n`을 `\r`로, 단독 `\n`을 `\r`로 변환           |
| **근거**      | 터미널 프로토콜에서 Enter = CR(0x0D). WT + Alacritty 공통 패턴        |
| **적용 조건** | 비 bracketed paste mode에서만 적용 (bracketed mode는 원본 유지)       |
| **수락 기준** | Windows 메모장에서 복사한 멀티라인 텍스트가 bash에서 줄바꿈 정상 처리 |

#### FR-05: Ctrl+Shift+C/V 전용 키

| 항목          | 내용                                                                                                   |
| ------------- | ------------------------------------------------------------------------------------------------------ |
| **설명**      | Ctrl+Shift+C는 항상 복사 (선택 유무 무관하게 선택 있으면 복사, 없으면 무시). Ctrl+Shift+V는 항상 paste |
| **근거**      | WT/Linux 터미널 표준 단축키. 선택 없이 Ctrl+C를 SIGINT로 보내면서도 명시적 복사 경로 필요              |
| **수락 기준** | 선택 없이 Ctrl+Shift+C 누르면 아무 일 없음, 선택 있으면 복사                                           |

#### FR-06: Shift+Insert 붙여넣기

| 항목          | 내용                                                 |
| ------------- | ---------------------------------------------------- |
| **설명**      | Shift+Insert를 Ctrl+V와 동일한 paste 동작으로 매핑   |
| **근거**      | Windows 전통 paste 단축키. 4개 참조 터미널 모두 지원 |
| **수락 기준** | Shift+Insert로 paste 정상 동작                       |

#### FR-07: 복사 후 선택 해제

| 항목          | 내용                                                                       |
| ------------- | -------------------------------------------------------------------------- |
| **설명**      | Ctrl+C 또는 Ctrl+Shift+C로 복사 완료 후 선택 영역과 DX11 하이라이트를 해제 |
| **근거**      | WT/Alacritty 공통 동작. 사용자가 복사 완료를 시각적으로 확인               |
| **수락 기준** | 복사 후 하이라이트가 사라짐                                                |

### 6.2 Phase 2: 안전성 (Should-Have)

#### FR-08: 멀티라인 경고 다이얼로그

| 항목                | 내용                                                                              |
| ------------------- | --------------------------------------------------------------------------------- |
| **설명**            | paste 텍스트에 줄바꿈이 2줄 이상이면 확인 다이얼로그 표시                         |
| **다이얼로그 내용** | 줄 수 표시 + 첫 3줄 미리보기 + [붙여넣기] [취소] 버튼 + "다시 묻지 않기" 체크박스 |
| **설정 키**         | `clipboard.warnOnMultiLinePaste` (기본: `true`)                                   |
| **참조**            | WT `TerminalPage::_PasteTextWithBroadcast`                                        |
| **수락 기준**       | 3줄 텍스트 paste 시 경고 표시, 체크박스로 비활성화 가능                           |

#### FR-09: 대용량(5KiB+) 경고

| 항목          | 내용                                                                                        |
| ------------- | ------------------------------------------------------------------------------------------- |
| **설명**      | paste 텍스트가 5KiB 이상이면 크기 표시 + 확인 다이얼로그                                    |
| **설정 키**   | `clipboard.warnOnLargePaste` (기본: `true`), `clipboard.largePasteThreshold` (기본: `5120`) |
| **참조**      | WT 5KiB 임계값                                                                              |
| **수락 기준** | 6KiB 텍스트 paste 시 "약 6KB 텍스트를 붙여넣으려 합니다" 경고 표시                          |

#### FR-10: TrimPaste

| 항목          | 내용                                                                         |
| ------------- | ---------------------------------------------------------------------------- |
| **설명**      | 단일 라인 paste 시 후행 공백/탭/개행을 자동 제거                             |
| **적용 조건** | 단일 라인만. 멀티라인이면 비활성. Bracketed paste mode에서도 비활성          |
| **설정 키**   | `clipboard.trimPaste` (기본: `true`)                                         |
| **참조**      | WT TrimPaste 동작                                                            |
| **수락 기준** | " ls -la \n" paste 시 "ls -la" 결과 전달 (앞 공백은 유지, 뒤 공백/개행 제거) |

### 6.3 Phase 3: 편의/확장 (Nice-to-Have)

#### FR-11: 우클릭 컨텍스트 메뉴

| 항목     | 내용                                                          |
| -------- | ------------------------------------------------------------- |
| **설명** | 터미널 영역 우클릭 시 [복사] [붙여넣기] [모두 선택] 메뉴 표시 |
| **조건** | 선택 없으면 [복사] 비활성 (회색)                              |
| **참조** | WT (설정으로 토글), cmux                                      |

#### FR-12: Copy-on-Select 설정 옵션

| 항목        | 내용                                                       |
| ----------- | ---------------------------------------------------------- |
| **설명**    | 마우스로 텍스트 선택 완료 시 자동으로 클립보드에 복사      |
| **설정 키** | `clipboard.copyOnSelect` (기본: `false`)                   |
| **참조**    | WT `copyOnSelect`, Alacritty `selection.save_to_clipboard` |

#### FR-13: 설정 JSON 클립보드 섹션

| 항목             | 내용                                                                                           |
| ---------------- | ---------------------------------------------------------------------------------------------- |
| **설명**         | `settings.json`에 `clipboard` 섹션 추가 (M-4 설정 시스템 활용)                                 |
| **설정 키 목록** | `copyOnSelect`, `trimPaste`, `warnOnMultiLinePaste`, `warnOnLargePaste`, `largePasteThreshold` |
| **핫 리로드**    | M-4 `ISettingsService` hot reload로 즉시 반영                                                  |

#### FR-14: OSC 52 Write+Read (focused 체크) → Phase 1로 승격

| 항목             | 내용                                                                                     |
| ---------------- | ---------------------------------------------------------------------------------------- |
| **설명**         | 원격 앱(vim, tmux 등)이 OSC 52 시퀀스로 클립보드 Write/Read 요청 시 처리                 |
| **Write 보안**   | 해당 surface/session이 focused 상태일 때만 허용                                          |
| **Read 보안**    | 기본 `deny`. 설정으로 `ask`(확인 다이얼로그) 또는 `allow` 전환 가능                      |
| **설정 키**      | `clipboard.osc52Write` (기본: `true`), `clipboard.osc52Read` (기본: `"deny"`)            |
| **차별화 근거**  | WT는 Read 미구현, WezTerm은 no-op. Alacritty+cmux만 Read 지원 → GhostWin이 Windows에서 최초 |
| **참조**         | Alacritty (`OnlyCopy` 기본), cmux (`clipboard-read: ask`), WT (focused 체크)             |

### 6.4 Non-Functional Requirements

| ID         | 요구사항                                    | 기준                                                                          | 근거                                                                              |
| ---------- | ------------------------------------------- | ----------------------------------------------------------------------------- | --------------------------------------------------------------------------------- |
| **NFR-01** | 클립보드 조작 100ms 이내                    | `Clipboard.SetText`/`GetText` 포함 전체 경로                                  | WPF OLE 클립보드는 Win32 대비 느리지만 사용자 입력 빈도(초당 수 회)에서 병목 아님 |
| **NFR-02** | CJK 전각 문자 정확한 선택/복사              | 한글/일본어/중국어 + 이모지 왕복 테스트                                       | M-10 CJK word boundary 기반.`GetSelectedText` UTF-8 트림 완비                     |
| **NFR-03** | OLE 클립보드 점유 시 재시도 + graceful 실패 | `CLIPBRD_E_CANT_OPEN` 발생 시 3회 재시도 (50ms 간격), 실패 시 무시            | 다른 앱이 클립보드를 점유 중일 때 `ExternalException` 발생 가능                   |
| **NFR-04** | UI 스레드 블로킹 0ms                        | WPF Clipboard API는 STA 필수이므로 Dispatcher 호출, 무거운 필터링은 별도 처리 | 대용량 텍스트 필터링이 UI를 멈추지 않도록                                         |

---

## 7. Market Sizing

### 7.1 시장 규모

| 계층                          |  규모 | 산출 근거                                                   |
| ----------------------------- | ----: | ----------------------------------------------------------- | --- |
| **TAM** (전체 터미널 시장)    | $850M | 전세계 개발자 2,700만명 x 터미널 사용률 60% x 연간 가치 $52 |     |
| **SAM** (Windows 터미널 시장) | $120M | Windows 개발자 비율 ~40% x 적극 사용자 30% x 연간 가치 $37  |     |
| **SOM** (GhostWin 획득 가능)  | $1.2M | SAM의 1% (CJK + GPU 터미널 니치)                            |

### 7.2 Beachhead Segment 분석

4개 후보 세그먼트를 Geoffrey Moore 기준 4항목(1~5점)으로 평가:

| 세그먼트                 | Burning Pain | Willingness to Pay | Winnable Share | Referral Potential | **합계** |
| ------------------------ | :----------: | :----------------: | :------------: | :----------------: | :------: |
| **한국/일본 CJK 개발자** |      5       |         3          |       5        |         4          |  **17**  |
| DevOps/SRE (멀티서버)    |      4       |         4          |       3        |         3          |    14    |
| WSL 풀스택 개발자        |      4       |         3          |       2        |         4          |    13    |
| 임베디드/MCU 개발자      |      3       |         2          |       3        |         2          |    10    |

**Beachhead 선정: 한국/일본 CJK 개발자**

- **Burning Pain (5/5)**: CJK 전각 문자 복사 시 글자 잘림/공백 추가가 일상적 고통. 기존 터미널 모두 CJK 처리가 불완전
- **Willingness to Pay (3/5)**: 오픈소스 기반이라 직접 지불보다는 커뮤니티 기여/추천으로 보상
- **Winnable Share (5/5)**: CJK 정밀 처리 GPU 터미널이 Windows에 전무. 경쟁 0
- **Referral Potential (4/5)**: 한국/일본 개발자 커뮤니티(X, 블로그, 카페)에서 활발한 도구 공유 문화

### 7.3 90일 고객 획득 계획

| 기간        | 활동                                                              | 목표                            |
| ----------- | ----------------------------------------------------------------- | ------------------------------- |
| **1~30일**  | M-10.5 Phase 1 완성 + GitHub 릴리스 + 한국 개발자 커뮤니티 포스팅 | 얼리 어답터 50명                |
| **31~60일** | Phase 2 안전성 추가 + CJK 복사 정확도 데모 영상                   | 사용자 200명 + 피드백 수집      |
| **61~90일** | Phase 3 편의 기능 + 블로그 포스트 시리즈 (CJK 터미널 개발기)      | 사용자 500명 + GitHub star 100+ |

---

## 8. Risks & Assumptions

### 8.1 기술 위험

| 위험                                             | 영향 | 확률 | 완화 전략                                                                                                           |
| ------------------------------------------------ | :--: | :--: | ------------------------------------------------------------------------------------------------------------------- |
| `mode_get` P/Invoke 래핑 실패                    | 높음 | 낮음 | C++ wrapper 이미 존재 (`VtCore::mode_get`). `ghostwin_engine.cpp`에 export 함수 추가만 필요                         |
| WPF `Clipboard` OLE 점유 경합                    | 중간 | 중간 | `try/catch` + 3회 재시도 (50ms 간격). WT 주석 참조: Win32 API가 300~1000x 빠르지만, 사용 빈도가 낮아 WPF API로 충분 |
| Bracketed paste에서 필터링이 bracket 마커를 훼손 | 높음 | 낮음 | 필터링 후 bracket 감싸기 순서 보장 (WT 구현 참조)                                                                   |
| 대용량 paste (1MB+) 시 UI 프리즈                 | 중간 | 낮음 | 5KiB 경고 + 비동기 필터링으로 선제 차단                                                                             |
| CJK surrogate pair 처리 오류                     | 중간 | 낮음 | `GetSelectedText`가 UTF-8 기반으로 이미 정확. WPF `Clipboard.SetText`도 UTF-16 자동 변환                            |

### 8.2 핵심 가정 요약

Section 5.3의 8개 가정(A1~A8) 참조. 가장 검증 우선순위가 높은 3개:

1. **A2 (mode_get P/Invoke)**: Phase 1 구현 첫 번째 단계에서 즉시 검증 가능
2. **A6 (OLE 점유 재시도)**: Phase 1 구현 중 `ExternalException` 핸들링으로 검증
3. **A4 (멀티라인 경고 UX)**: Phase 2에서 사용자 피드백으로 검증

### 8.3 GTM 전략

| 단계            | 활동                                                              | 채널            |
| --------------- | ----------------------------------------------------------------- | --------------- |
| **Pre-launch**  | M-10.5 Phase 1 완성 + 내부 smoke 테스트                           | --              |
| **Launch day**  | GitHub Release + 한국 개발자 커뮤니티 (X, GeekNews, velog) 포스팅 | 커뮤니티, SNS   |
| **Post-launch** | CJK 복사 정밀도 데모 영상 + 비교 테스트 결과 공유                 | YouTube, 블로그 |

**핵심 메시지**: "선택하면 바로 복사, 붙여넣기는 안전하게 -- GPU 터미널에서 한글이 깨지지 않는 클립보드"

---

## Release Plan

### v1 Scope (M-10.5)

|    Phase    | 포함 FR     | 상대 일정 | 비고                               |
| :---------: | ----------- | :-------: | ---------------------------------- |
| **Phase 1** | FR-01~FR-07, FR-14 |   2~3일   | 필수. 이것 없이는 터미널 사용 불가. OSC 52 Write+Read 포함 |
| **Phase 2** | FR-08~FR-10        |   +1일    | 안전성 차별화. Phase 1 직후 구현                           |
| **Phase 3** | FR-11~FR-13        |  +1~2일   | 편의/확장. 별도 마일스톤 가능                              |

### 후속 고려 (v1 이후)

| 기능                           | 우선순위 | 비고                                        |
| ------------------------------ | :------: | ------------------------------------------- |
| HTML/RTF 복사                  |  MEDIUM  | WT만 지원. WPF `Clipboard.SetData` 활용   |
| Block 선택 (Alt+드래그)        |  MEDIUM  | 4개 터미널 모두 지원. 선택 모듈 확장 필요 |
| 이미지 paste                   |   LOW    | cmux만 지원. 니치 기능                    |
| 브로드캐스트 paste (멀티 pane) |   LOW    | WT만 지원. Workspace 확장 필요            |

---

## 참조 문서

| 문서                                 | 경로                                                |
| ------------------------------------ | --------------------------------------------------- |
| 클립보드 기술 리서치 (4 터미널 비교) | `docs/00-research/research-clipboard-copy-paste.md` |
| WPF Migration Plan                   | `docs/01-plan/features/wpf-migration.plan.md`       |
| WPF Migration Design                 | `docs/02-design/features/wpf-migration.design.md`   |
| Roadmap                              | `docs/01-plan/roadmap.md`                           |
| M-10 마우스 입력 아카이브            | `docs/archive/2026-04/mouse-input/`                 |
| Settings System (M-4) 아카이브       | `docs/archive/2026-04/settings-system/`             |

---

## Version History

| Version | Date       | Changes                                                               |
| ------- | ---------- | --------------------------------------------------------------------- |
| 1.0     | 2026-04-11 | Initial PRD -- PM 3 agent 분석 통합 (Discovery + Strategy + Research) |
| 1.1     | 2026-04-11 | OSC 52 Write+Read를 Phase 3 → Phase 1로 승격. Read 보안 요구사항 추가 (기본 deny, 설정 ask/allow) |
