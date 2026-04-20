# M-13 PRD — 입력 UX (Input UX)

> **문서 종류**: Product Requirements Document (PRD)
> **작성일**: 2026-04-16
> **마일스톤**: M-13
> **소유자**: 노수장
> **선행**: M-12 Settings UI 완료, Phase 6 전체 완료

> **사후 정정 (2026-04-20)**  
> 실제 구현은 본 PRD 초안과 두 군데가 달라졌다.
> 1. FR-02 진입점은 `GHOSTTY_ACTION_MOUSE_SHAPE` action handler 가 아니라 **ghostty terminal option callback (`GHOSTTY_TERMINAL_OPT_MOUSE_SHAPE`)** 이다.
> 2. WPF 쪽 적용은 `Cursor` 프로퍼티보다 **Win32 `SetCursor` 직접 호출**이 최종 채택됐다.
>
> 최종 구현/검증은 `docs/04-report/features/m13-input-ux.report.md` 와 `docs/03-analysis/m13-input-ux.analysis.md` §13 기준으로 본다.

---

## Executive Summary

| 관점 | 내용 |
|------|------|
| **Problem** | 한글 입력 시 조합 중인 글자가 화면에 보이지 않아 사용자가 "지금 뭘 치고 있는지" 알 수 없음. 또한 vim/htop 등 TUI 앱이 커서 모양 변경을 요청해도 마우스 포인터가 항상 화살표로 고정됨 |
| **Solution** | (1) TSF preedit 문자를 DX11 렌더러 커서 위치에 오버레이로 표시 (2) ghostty terminal option mouse-shape 콜백을 Win32 커서로 매핑 |
| **Function / UX Effect** | 한글 입력 시 조합 중인 글자(`ㅎ` -> `하` -> `한`)가 커서 위치에 실시간 표시. TUI 앱의 커서 모양 변경 요청이 마우스 포인터에 즉시 반영 |
| **Core Value** | 터미널 기본기 완성. CJK 입력 UX에서 Windows Terminal/Warp 수준 달성. 한국어 사용자 진입 장벽 제거 |

---

## 1. 배경과 동기

### 1.1 현재 상태

```
한글 입력 흐름 (현재)
├── 1. 키 입력 (ㅎ, ㅏ, ㄴ)
├── 2. TSF (Text Services Framework) 조합 처리  ← 정상 동작
├── 3. CompositionPreview 구조체에 조합 텍스트 저장  ← 정상 동작
├── 4. session.composition 에 조합 문자열 보관  ← 정상 동작
├── 5. 렌더러가 오버레이로 표시  ← ★ 미구현 ★
└── 6. 조합 확정 → ConPTY 전송  ← 정상 동작
```

사용자 체감 문제:
- 한글 자모를 입력해도 화면에 아무 변화 없음 (확정될 때까지)
- "ㅎ -> 하 -> 한 -> 한글" 과정을 시각적으로 확인할 수 없음
- 오타를 눈으로 잡을 수 없어 확정 후 지우고 다시 입력해야 함
- 한국어 사용자가 GhostWin을 "한글 입력이 안 되는 터미널"로 오해

마우스 커서 모양 문제:
- vim의 insert 모드에서 `I-beam` 커서를 기대하지만 항상 화살표
- 링크 위에서 `pointer` 커서를 기대하지만 항상 화살표
- ghostty API에 `GHOSTTY_ACTION_MOUSE_SHAPE` 콜백이 정의되어 있으나 GhostWin 엔진에서 미처리

### 1.2 기술 인프라 현황 (이미 구현된 것)

| 구성요소 | 상태 | 위치 |
|---------|------|------|
| TSF 초기화 + 조합 처리 | 완료 (94 E2E 테스트) | `src/tsf/` |
| `CompositionPreview` 구조체 | 완료 | `src/tsf/tsf_handle.h:23` |
| `IDataProvider::HandleCompositionUpdate` | 완료 | `src/tsf/tsf_handle.h:47` |
| `Session::composition` 필드 | 완료 (`ime_mutex` 보호) | `src/session/session.h:136` |
| `RenderState::force_all_dirty()` | 완료 (IME용 주석) | `src/renderer/render_state.h:119` |
| ghostty `ghostty_action_mouse_shape_e` | API 정의 완료 (34종 커서, `ghostty.h:660-696`) | `external/ghostty/include/ghostty.h:660` |
| ghostty `GHOSTTY_ACTION_MOUSE_SHAPE` | 액션 타입 정의 완료 | `external/ghostty/include/ghostty.h:900` |

**핵심 관찰**: 조합 미리보기의 데이터 파이프라인 5/6 단계가 이미 구현되어 있고, 마우스 커서 모양의 API도 이미 정의되어 있음. 남은 작업은 "렌더링"과 "콜백 연결"뿐.

### 1.3 경쟁 제품 IME / 커서 비교

| 터미널 | 조합 미리보기 | 미리보기 위치 | 마우스 커서 변경 |
|--------|-------------|-------------|---------------|
| **Windows Terminal** | 커서 위치 인라인 | 셀 정확 위치 | 지원 (DECSCUSR 기반) |
| **cmux** | 커서 위치 인라인 | 셀 정확 위치 | 지원 (ghostty 콜백) |
| **Warp** | 입력 영역 별도 | 하단 입력 바 | 지원 |
| **WezTerm** | 커서 위치 인라인 | 셀 정확 위치 | 지원 |
| **Alacritty** | 미지원 (조합 후 전송) | N/A | 미지원 |
| **GhostWin (현재)** | **미지원** | N/A | **미지원** |
| **GhostWin (M-13)** | 커서 위치 인라인 | 셀 정확 위치 | 지원 (34종 커서) |

**차별화 포인트**:
- Alacritty보다 우수한 CJK UX (조합 미리보기 + 마우스 커서)
- Windows Terminal/WezTerm과 동등한 수준 달성
- Warp의 "별도 입력 영역" 방식이 아닌 전통적 인라인 방식 (터미널 관습 유지)

---

## 2. 타겟 사용자

### 2.1 페르소나

#### P1: 한국어 개발자 (Primary)

| 항목 | 내용 |
|------|------|
| **프로필** | Windows에서 CLI 중심 개발 (PowerShell, WSL, SSH) |
| **핵심 니즈** | 한글로 커밋 메시지, 주석, git branch 이름 입력 시 조합 과정이 보여야 함 |
| **현재 고충** | 한글 조합이 안 보여서 "입력이 안 되나?" 혼란. 확정 전 오타 수정 불가 |
| **기대** | Windows Terminal 수준의 자연스러운 한글 입력 경험 |

#### P2: CJK 사용자 (Secondary)

| 항목 | 내용 |
|------|------|
| **프로필** | 일본어/중국어 입력이 필요한 개발자 |
| **핵심 니즈** | IME 조합 미리보기 (일본어 로마지→히라가나, 중국어 핀인→한자) |
| **현재 고충** | 동일 (조합 안 보임) |
| **기대** | TSF 기반이므로 일본어/중국어 IME도 동일하게 동작 |

#### P3: TUI 앱 사용자 (Secondary)

| 항목 | 내용 |
|------|------|
| **프로필** | vim, neovim, htop, lazygit 등 TUI 앱 주력 사용자 |
| **핵심 니즈** | TUI 앱이 요청한 커서 모양이 마우스 포인터에 반영되어야 함 |
| **현재 고충** | vim insert 모드에서도 화살표 커서. 링크/버튼 위에서도 화살표 |
| **기대** | 컨텍스트에 맞는 마우스 포인터 (I-beam, pointer, resize 등) |

---

## 3. 기능 요구사항

### FR-01: 조합 미리보기 (Composition Preview Overlay)

| 항목 | 설명 |
|------|------|
| **우선순위** | P0 (Must-Have) |
| **설명** | TSF 조합 중인 문자를 DX11 렌더러의 커서 위치에 실시간 오버레이로 표시 |
| **입력** | `Session::composition` (wstring, `ime_mutex` 보호) |
| **출력** | 커서 셀 위치에 조합 문자 렌더링 (배경 하이라이트 + 밑줄) |

**상세 동작**:

```
조합 미리보기 렌더링 흐름

1. TSF OnUpdateComposition
   → CompositionPreview{text="한", active=true}
   → Session::composition = "한"

2. 렌더 루프 (매 프레임)
   ├── ime_mutex 잠금
   ├── composition 문자열 읽기
   ├── ime_mutex 해제
   └── composition이 비어있지 않으면:
       ├── 현재 커서 위치(row, col) 계산
       ├── 조합 문자를 해당 셀에 그리기
       │   ├── 배경: 반투명 강조색 (예: 파란색 20%)
       │   └── 밑줄: 1px 실선 (IME 활성 표시)
       └── CJK 전각 문자면 2셀 폭으로 렌더링

3. TSF OnEndComposition
   → CompositionPreview{text="", active=false}
   → Session::composition = ""
   → 오버레이 제거 (정상 렌더링으로 복귀)
```

**수용 기준**:
- AC-01: "ㅎ" 입력 시 커서 위치에 "ㅎ"이 밑줄과 함께 표시됨
- AC-02: "하" 조합 시 기존 "ㅎ"이 "하"로 교체 표시됨
- AC-03: 조합 확정 시 오버레이 제거되고 확정 문자가 정상 출력됨
- AC-04: CJK 전각 문자(한/중/일)가 2셀 폭으로 올바르게 표시됨
- AC-05: 고DPI(150%, 200%) 환경에서 오버레이 위치/크기 정확함
- AC-06: 조합 중 pane 전환 시 이전 pane의 오버레이가 제거됨

### FR-02: 마우스 커서 모양 변경 (Mouse Cursor Shape)

| 항목 | 설명 |
|------|------|
| **우선순위** | P1 (Should-Have) |
| **설명** | ghostty VT 파서의 `GHOSTTY_ACTION_MOUSE_SHAPE` 콜백을 WPF `Cursor` 속성에 매핑 |
| **입력** | ghostty action 콜백 `mouse_shape` (34종 enum) |
| **출력** | WPF 터미널 영역의 마우스 커서 변경 |

**ghostty 커서 매핑표** (PRD 초안 예시 — 정식 매핑은 `fr-02-mouse-cursor-shape.plan.md` §7 참조):

> ⚠️ **정정 (2026-04-18)**: 본 PRD 초안의 매핑표는 일부 항목이 Plan §7과 불일치한다 (예: `ALL_SCROLL`, `COPY`, `MOVE`). 단일 진실(single source of truth)은 Plan §7로 통일. 본 표는 PM 단계 의도만 보여주는 historical 예시로 유지.

| ghostty enum | WPF Cursor | 용도 |
|-------------|-----------|------|
| `DEFAULT` | `Cursors.Arrow` | 기본 |
| `TEXT` | `Cursors.IBeam` | 텍스트 선택 영역 |
| `POINTER` | `Cursors.Hand` | 링크 hover |
| `HELP` | `Cursors.Help` | 도움말 |
| `PROGRESS` | `Cursors.AppStarting` | 로딩 중 |
| `WAIT` | `Cursors.Wait` | 대기 |
| `CROSSHAIR` | `Cursors.Cross` | 정밀 선택 |
| `NOT_ALLOWED` | `Cursors.No` | 금지 |
| `GRAB` / `GRABBING` | `Cursors.Hand` | 드래그 |
| `EW_RESIZE` | `Cursors.SizeWE` | 좌우 리사이즈 |
| `NS_RESIZE` | `Cursors.SizeNS` | 상하 리사이즈 |
| `NESW_RESIZE` | `Cursors.SizeNESW` | 대각선 리사이즈 |
| `NWSE_RESIZE` | `Cursors.SizeNWSE` | 대각선 리사이즈 |
| `ALL_SCROLL` | (Plan §7.2에서 Arrow fallback으로 정정됨) | 전방향 스크롤 |
| 기타 (`ZOOM_IN`, `COPY` 등) | (Plan §7에서 항목별 정확 매핑) | — |

전체 34종 + 정확한 1차 30종 / fallback 4종 분리는 Plan §7.1 / §7.2 참조.

**수용 기준**:
- AC-07: vim insert 모드 진입 시 마우스 커서가 I-beam으로 변경됨
- AC-08: vim normal 모드 복귀 시 마우스 커서가 화살표로 복귀됨
- AC-09: pane 간 이동 시 각 pane의 마우스 커서 상태가 독립적으로 유지됨
- AC-10: 34종 ghostty 커서 enum 전체가 매핑되어 있음 (1차 30종 + Arrow fallback 4종, 자세한 매핑은 `fr-02-mouse-cursor-shape.plan.md` §7 참조)

---

## 4. 비기능 요구사항

### 4.1 성능

| 항목 | 기준 |
|------|------|
| 조합 오버레이 렌더링 지연 | < 1 프레임 (16.6ms @ 60Hz) |
| `ime_mutex` 잠금 시간 | < 100us (wstring 복사만) |
| 커서 모양 변경 지연 | < 50ms (WPF Dispatcher) |
| GPU 추가 부하 | 무시 가능 (1-2 셀 추가 렌더링) |

### 4.2 호환성

| 항목 | 기준 |
|------|------|
| IME | Microsoft 한글 IME, Google 한글 입력기, 일본어 IME, 중국어 IME |
| DPI | 100%, 125%, 150%, 200%, 250% |
| 셸 | cmd, PowerShell, WSL, SSH |
| TUI 앱 | vim, neovim, htop, lazygit, fzf |

### 4.3 안전성

| 항목 | 기준 |
|------|------|
| 스레드 안전 | `ime_mutex`로 main(W)/render(R) 동시 접근 보호 (기존 패턴 유지) |
| 조합 취소 | `CancelComposition()` 호출 시 오버레이 즉시 제거 |
| 세션 종료 | 세션 close 시 조합 상태 정리 (메모리 누수 없음) |

---

## 5. 기술 실현 가능성 분석

### 5.1 조합 미리보기 — 실현 가능성: 높음

**근거**:

1. **데이터 파이프라인 5/6 완성**: TSF → `CompositionPreview` → `Session::composition` 경로가 이미 구현됨. 남은 건 렌더러에서 읽어서 그리는 것뿐.

2. **렌더러 인프라 준비 완료**: `force_all_dirty()` 메서드가 "IME composition overlay"용으로 주석까지 달려 있음 (`render_state.h:119`). 설계 시점에 이미 고려된 기능.

3. **참조 구현 존재**: Windows Terminal은 `AtlasEngine`에서 조합 문자를 커서 위치에 그림. GhostWin의 DX11 인스턴싱 렌더러에서 동일 접근 가능.

4. **스레드 안전성 확보**: `ime_mutex`가 이미 존재하고 main(W)/render(R) 패턴이 확립됨.

**리스크**:

| 리스크 | 확률 | 완화 |
|--------|------|------|
| CJK 전각 문자 2셀 폭 계산 오류 | 낮음 | 기존 CJK 렌더링 코드 재사용 |
| 고DPI에서 오버레이 위치 어긋남 | 중간 | DPI 스케일 팩터를 셀 좌표 계산에 반영 |
| 일본어 장문 조합(かな→漢字) 시 다중 셀 | 중간 | 커서 위치부터 오른쪽으로 확장 렌더링 |

### 5.2 마우스 커서 모양 — 실현 가능성: 높음

**근거**:

1. **ghostty API 정의 완료**: `ghostty_action_mouse_shape_e`에 34종 커서가 정의됨 (`ghostty.h:660-696`).

2. **콜백 메커니즘 존재**: `GHOSTTY_ACTION_MOUSE_SHAPE` 액션 타입이 정의됨 (`ghostty.h:900`). 엔진 액션 콜백에 case를 추가하면 됨.

3. **WPF Cursor API 단순**: `FrameworkElement.Cursor = Cursors.IBeam` 한 줄로 변경 가능. Dispatcher를 통한 UI 스레드 마샬링만 필요.

4. **매핑 직관적**: 34종 중 30종은 WPF `Cursors` 클래스에 직접 대응 (Arrow / IBeam / Hand / Help / Wait / AppStarting / Cross / No / SizeWE / SizeNS / SizeNESW / SizeNWSE). 나머지 4종(`CONTEXT_MENU`, `ALL_SCROLL`, `ZOOM_IN`, `ZOOM_OUT`)은 `Arrow` fallback. 단방향 resize 4종(`N/E/S/W_RESIZE`)은 양방향(`SizeNS/SizeWE`)에 흡수. 자세한 매핑은 `fr-02-mouse-cursor-shape.plan.md` §7 참조.

**리스크**:

| 리스크 | 확률 | 완화 |
|--------|------|------|
| 엔진(C++) → WPF(C#) 콜백 경로 미구현 | 중간 | 기존 `set_title` 콜백 패턴 참조하여 동일 구조 |
| 커서 변경이 너무 빈번하여 깜빡임 | 낮음 | 이전 값과 비교하여 변경 시에만 업데이트 |

---

## 6. Opportunity Solution Tree

```
[Outcome] 한국어 사용자가 GhostWin을 기본 터미널로 채택
│
├── [Opportunity] 한글 입력 시 조합 과정이 안 보임
│   ├── [Solution] DX11 커서 위치 오버레이 (★ 채택)
│   │   └── 기존 인프라 재사용, 참조 구현(WT) 존재
│   ├── [Solution] 별도 팝업 윈도우로 조합 표시
│   │   └── ✗ 비표준, 터미널 관습 위반
│   └── [Solution] 하단 상태바에 조합 표시
│       └── ✗ 시선 이동 필요, UX 저하
│
├── [Opportunity] TUI 앱의 커서 모양 요청이 무시됨
│   ├── [Solution] ghostty 콜백 → WPF Cursor (★ 채택)
│   │   └── API 이미 정의, WPF 매핑 직관적
│   └── [Solution] 커스텀 커서 아이콘 사용
│       └── ✗ 과도한 복잡도, 유지보수 부담
│
└── [Opportunity] IME 후보창 위치가 부정확
    └── [Solution] GetTextExt/GetScreenExt 정밀 좌표 (기구현)
        └── TSF ITfContextOwner에서 이미 처리
```

---

## 7. Value Proposition (JTBD 6-Part)

### 한글 조합 미리보기

| 파트 | 내용 |
|------|------|
| **When** | 한글로 커밋 메시지, 주석, 파일 이름을 입력할 때 |
| **I want to** | 조합 중인 글자가 실시간으로 화면에 보이길 원한다 |
| **So I can** | 오타를 즉시 발견하고 확정 전에 수정할 수 있다 |
| **Unlike** | 현재 GhostWin (조합 안 보임) 또는 Alacritty (조합 미지원) |
| **Our product** | 커서 위치에 밑줄과 함께 조합 문자를 실시간 표시한다 |
| **Which means** | Windows Terminal과 동등한 한글 입력 경험을 제공한다 |

### 마우스 커서 모양

| 파트 | 내용 |
|------|------|
| **When** | vim, htop, lazygit 등 TUI 앱을 사용할 때 |
| **I want to** | 앱이 요청한 커서 모양이 마우스 포인터에 반영되길 원한다 |
| **So I can** | 현재 컨텍스트(편집/선택/링크/리사이즈)를 직관적으로 파악할 수 있다 |
| **Unlike** | 현재 GhostWin (항상 화살표) |
| **Our product** | ghostty VT 파서의 34종 커서 요청을 WPF 커서에 매핑한다 (1차 30종 + Arrow fallback 4종) |
| **Which means** | TUI 앱 사용자에게 자연스러운 인터랙션을 제공한다 |

---

## 8. Lean Canvas

| 섹션 | 내용 |
|------|------|
| **Problem** | (1) 한글 조합 안 보임 (2) TUI 커서 모양 무시 |
| **Customer Segments** | 한국어 개발자, CJK 사용자, TUI 앱 사용자 |
| **Unique Value** | ghostty 성능 + 완전한 CJK IME 지원 + TUI 커서 — Windows에서 유일 |
| **Solution** | DX11 조합 오버레이 + ghostty cursor_shape 콜백 연결 |
| **Channels** | GitHub Releases, 개발자 커뮤니티, 한국 개발자 포럼 |
| **Revenue Streams** | 오픈소스 (MIT) — 사용자 기반 확대가 목표 |
| **Cost Structure** | 개발 인력 1인, 기존 인프라 재사용으로 추가 비용 최소 |
| **Key Metrics** | 한글 입력 관련 이슈 0건 달성, 커서 모양 변경 커버리지 30/34종 (1차 매핑) + 4종 Arrow fallback |
| **Unfair Advantage** | TSF 파이프라인 5/6 이미 구현, ghostty API 정의 완료 |

---

## 9. 시장 규모 추정 (TAM/SAM/SOM)

| 단계 | 추정 | 근거 |
|------|------|------|
| **TAM** | Windows CLI 개발자 ~500만명 | Stack Overflow 2024 Survey: Windows 45%, 개발자 총 ~2600만명 중 CLI 사용 약 50% |
| **SAM** | CJK 입력이 필요한 Windows 개발자 ~150만명 | 한/중/일 개발자 비율 약 30% |
| **SOM** | GhostWin 초기 한국어 사용자 ~500명 | GitHub star 기반 + 한국 개발자 커뮤니티 침투율 |

---

## 10. 구현 로드맵

### 10.1 단계별 실행 계획

| 순서 | 작업 | 규모 | 의존성 |
|:----:|------|:----:|--------|
| 1 | **FR-01 조합 오버레이 렌더링** | 중 | 없음 |
| 1a | 렌더 루프에서 `composition` 읽기 + 커서 위치 셀 계산 | 소 | - |
| 1b | DX11 인스턴싱으로 조합 문자 렌더링 (배경 + 밑줄) | 중 | 1a |
| 1c | CJK 전각 2셀 처리 + 다중 문자 조합 | 소 | 1b |
| 2 | **FR-02 마우스 커서 모양** | 소 | 없음 |
| 2a | 엔진 액션 콜백에 `MOUSE_SHAPE` case 추가 | 소 | - |
| 2b | C++ → C# Interop 경로 구축 (기존 패턴 참조) | 소 | 2a |
| 2c | WPF `Cursor` 프로퍼티 매핑 + 테스트 | 소 | 2b |

### 10.2 일정 추정

| 작업 | 예상 소요 |
|------|----------|
| FR-01 조합 오버레이 | 1-2일 |
| FR-02 마우스 커서 | 0.5-1일 |
| 통합 테스트 + 엣지 케이스 | 0.5일 |
| **합계** | **2-3.5일** |

---

## 11. 테스트 전략

### 11.1 단위 테스트

- 조합 문자열 → 셀 좌표 변환 정확성
- ghostty 커서 enum → WPF Cursor 매핑 전수 검증
- `ime_mutex` 잠금/해제 순서 검증

### 11.2 E2E 테스트

- 한글 "가나다" 입력 → 조합 과정 스크린샷 비교 (FlaUI)
- vim 모드 전환 시 커서 모양 변경 검증
- 고DPI 환경 렌더링 정확성

### 11.3 수동 테스트 체크리스트

- [ ] 한글 조합 미리보기가 커서 위치에 표시됨
- [ ] 조합 확정 후 오버레이 제거됨
- [ ] 일본어 IME 조합 미리보기 동작
- [ ] 중국어 핀인 입력 미리보기 동작
- [ ] vim insert 모드 → I-beam 커서
- [ ] vim normal 모드 → Arrow 커서
- [ ] 150% DPI에서 오버레이 위치 정확
- [ ] pane 전환 시 조합 상태/커서 모양 독립 유지

---

## 12. 비전 기여도

| 비전 축 | 기여 |
|---------|------|
| 1. cmux 기능 탑재 | 간접 (터미널 기본기 완성) |
| 2. AI 에이전트 멀티플렉서 | 간접 (Claude Code 한글 프롬프트 입력 UX 개선) |
| 3. 타 터미널 대비 성능 우수 | **직접** (CJK IME 지원 품질에서 WT/WezTerm 수준 달성, Alacritty 초과) |

---

## 다음 단계

```
PRD 완료 → /pdca plan m13-input-ux
(이 PRD가 Plan 문서에 자동 참조됩니다)
```

---

*PM Agent Team 분석 | 프레임워크: pm-skills (MIT License) by Pawel Huryn*
