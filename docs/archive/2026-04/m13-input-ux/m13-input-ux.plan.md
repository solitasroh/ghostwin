# Plan — M-13: 입력 UX (Input UX)

> **문서 종류**: Plan
> **작성일**: 2026-04-17
> **PRD 참조**: `docs/00-pm/m13-input-ux.prd.md`
> **선행 완료**: M-12 Settings UI (97%), Phase 6 전체 완료
> **비전 축**: 터미널 기본기 완성 (CJK 입력 UX)

---

## Executive Summary

| 관점 | 내용 |
|------|------|
| **Problem** | 한글 입력 시 조합 중 글자가 화면에 안 보임 (TSF preedit 미표시). TUI 앱의 커서 모양 변경 요청이 무시됨 |
| **Solution** | TSF `Session::composition` → DX11 렌더 루프에서 커서 위치에 오버레이 렌더링. ghostty `GHOSTTY_ACTION_MOUSE_SHAPE` → C++ 콜백 → C# → WPF `Cursor` 매핑 |
| **Function / UX Effect** | 한글 "ㅎ→하→한" 조합 과정이 커서 위치에 실시간 표시 (밑줄 + 반투명 배경). vim insert 모드에서 I-beam 커서, 링크 위에서 hand 커서 |
| **Core Value** | CJK 입력 UX에서 Windows Terminal/WezTerm 동등 수준 달성. 한국어 사용자 진입 장벽 제거. Alacritty 대비 명확한 우위 |

---

## 1. 현재 상태 — TSF 파이프라인 5/6 단계 구현 완료

```
TSF 파이프라인
├── 1. 키 입력 → TSF DispatchKeyEvent        ✅ 완료
├── 2. TSF 조합 처리 (한글 자모 조합)          ✅ 완료
├── 3. CompositionPreview 구조체 생성          ✅ 완료
├── 4. HandleCompositionUpdate → Session::composition  ✅ 완료
├── 5. ★ 렌더 루프에서 오버레이 표시 ★        ← M-13 FR-01
└── 6. 조합 확정 → ConPTY 전송                ✅ 완료
```

**핵심 사실**: `RenderState::force_all_dirty()` 메서드에 "IME composition overlay" 주석이 이미 있음. 설계 시점부터 이 기능이 의도되었음.

---

## 2. 기능 요구사항 상세

### FR-01: 조합 미리보기 (Composition Preview Overlay)

**핵심 아이디어**: 렌더 루프의 `render_surface()` 함수 마지막에 `Session::composition`을 읽어서, 비어있지 않으면 커서 위치에 QuadInstance를 추가로 생성하여 DX11로 그리기.

#### 2.1.1 렌더링 위치

```
render_surface() {
  // 1. start_paint (기존)
  // 2. builder.build (기존, 터미널 셀 렌더링)
  // 3. selection highlight (기존, M-10c)
  // 4. ★ composition overlay (신규, M-13) ★
  //    - ime_mutex 잠금
  //    - session->composition 읽기
  //    - 비어있지 않으면:
  //      - 커서 위치(row, col) 계산
  //      - 배경 QuadInstance (반투명 파란색, shading_type=2)
  //      - 글리프 QuadInstance (조합 문자 렌더링)
  //      - 밑줄 QuadInstance (1px, 셀 하단)
  // 5. upload_and_draw (기존)
}
```

#### 2.1.2 CJK 전각 처리

조합 문자가 CJK 전각(한글, 일본어 등)이면 2셀 폭으로 렌더링:

```cpp
bool is_wide = (cp >= 0x1100 && cp <= 0x115F) ||  // 한글 자모
               (cp >= 0x2E80 && cp <= 0x9FFF) ||  // CJK 통합
               (cp >= 0xAC00 && cp <= 0xD7AF) ||  // 한글 음절
               (cp >= 0xF900 && cp <= 0xFAFF);     // CJK 호환
uint16_t cell_span = is_wide ? 2 : 1;
```

#### 2.1.3 스레드 안전

```
렌더 스레드 (render_surface)        UI 스레드 (TSF 콜백)
────────────────────                ─────────────────────
ime_mutex.lock()                    ime_mutex.lock()
auto comp = session->composition;   session->composition = preview.text;
ime_mutex.unlock()                  ime_mutex.unlock()
```

`ime_mutex` 보유 시간: wstring 복사 ~수 μs. 렌더 프레임 영향 무시할 수준.

### FR-02: 마우스 커서 모양 변경

**핵심 아이디어**: ghostty VT 파서의 action handler에서 `GHOSTTY_ACTION_MOUSE_SHAPE`를 감지하여 콜백 → C++ → C# → WPF `Cursor` 설정.

#### 2.2.1 콜백 파이프라인

```
ghostty libvt (Zig)
  ↓ action: GHOSTTY_ACTION_MOUSE_SHAPE
  ↓ ghostty_action_u.mouse_shape = GHOSTTY_MOUSE_SHAPE_IBEAM
  ↓
vt_bridge.c/cpp (C wrapper)
  ↓ VtMouseShapeFn 콜백 호출
  ↓
GwCallbacks.OnMouseShape (C# P/Invoke)
  ↓ Dispatcher.BeginInvoke
  ↓
SessionManager.UpdateMouseShape(sessionId, shape)
  ↓
SessionInfo.MouseShape = shape
  ↓
TerminalHostControl.Cursor = MapToCursor(shape)
```

#### 2.2.2 ghostty action handler 구현 위치

현재 GhostWin은 ghostty action을 처리하지 않습니다. `vt_bridge`에 action handler 콜백을 추가해야 합니다. 기존 `VtDesktopNotifyFn` (Phase 6-A) 패턴과 동일.

---

## 3. 구현 순서 (5 Waves)

| Wave | 범위 | 의존 | 검증 | 예상 |
|:----:|------|:---:|------|:----:|
| **W1** | C++ composition 오버레이 렌더링 (render_surface 확장) | — | 한글 입력 → 조합 표시 확인 | 3시간 |
| **W2** | CJK 전각 처리 + 밑줄 + 배경 강조 | W1 | 한/중/일 문자 정상 표시 | 1시간 |
| **W3** | ghostty action handler → VtMouseShapeFn 콜백 (C++) | — | 콜백 호출 로그 확인 | 2시간 |
| **W4** | C# P/Invoke + WPF Cursor 매핑 | W3 | vim insert → I-beam 확인 | 1.5시간 |
| **W5** | 통합 검증 + 엣지 케이스 (pane 전환, DPI, 리사이즈) | W1-W4 | 수동 전수 테스트 | 1시간 |

**총 예상**: ~8.5시간 (2일)

---

## 4. 변경 파일 예상

### 4.1 C++ (5~6개)

| 파일 | 변경 |
|------|------|
| `ghostwin_engine.cpp` | `render_surface()` 끝에 composition overlay 렌더링 추가 |
| `quad_builder.h/cpp` | `build_composition()` 메서드 추가 (조합 문자 QuadInstance 생성) |
| `vt_bridge.h/c` | `VtMouseShapeFn` 콜백 타입 + 등록 함수 추가 |
| `vt_core.h/cpp` | action handler에서 MOUSE_SHAPE 감지 → 콜백 호출 |
| `session.h` | `mouse_shape` 필드 추가 |

### 4.2 C# (4~5개)

| 파일 | 변경 |
|------|------|
| `NativeCallbacks.cs` | `OnMouseShape` P/Invoke 콜백 |
| `NativeEngine.cs` | `GwCallbacks.OnMouseShape` 슬롯 |
| `SessionInfo.cs` | `MouseShape` 프로퍼티 |
| `TerminalHostControl.cs` | `Cursor` 매핑 로직 |
| `MainWindow.xaml.cs` | `OnMouseShape` 콜백 등록 |

---

## 5. 설계 결정 (Design 단계에서 확정)

| # | 결정 항목 | 선택지 | 현재 기울기 |
|:-:|----------|--------|:-----------:|
| D-1 | 조합 오버레이 렌더링 위치 | A: render_surface 끝 (selection 패턴) / B: 별도 패스 | **A** |
| D-2 | 조합 배경 색상 | A: 반투명 파란 (#007AFF 20%) / B: 반투명 회색 | **A** |
| D-3 | 조합 밑줄 | A: 1px 실선 (WT 패턴) / B: 점선 | **A** |
| D-4 | ghostty action 처리 | A: vt_bridge 콜백 / B: C# 폴링 | **A** |
| D-5 | 마우스 커서 적용 범위 | A: 활성 pane만 / B: 모든 pane | **A** |

---

## 6. 리스크

| 리스크 | 심각도 | 대응 |
|--------|:------:|------|
| **ghostty action handler 미구현** | 중 | VtDesktopNotifyFn (Phase 6-A) 패턴 재사용. ghostty의 action union 구조 분석 필요 |
| **ime_mutex 렌더 성능** | 낮 | wstring 복사 ~수 μs. 16ms 프레임 예산 내 무시 가능 |
| **CJK 전각 판정 정확도** | 낮 | ghostty의 `is_wide_codepoint()` 함수 재사용 (이미 quad_builder.cpp에 존재) |
| **DPI 변경 시 오버레이 위치** | 낮 | cell_w/cell_h가 DPI 반영 (dpi-scaling-integration에서 검증) |
| **pane 전환 시 잔여 오버레이** | 중 | composition 비움 시 force_all_dirty() → 자동 제거. 확인 필요 |

---

## 7. 성공 기준

| # | 기준 | 중요도 |
|:-:|------|:------:|
| 1 | "ㅎ" 입력 → 커서 위치에 "ㅎ" + 밑줄 표시 | 필수 |
| 2 | "ㅎ→하→한" 조합 과정 실시간 표시 | 필수 |
| 3 | 조합 확정 → 오버레이 제거 + 정상 출력 | 필수 |
| 4 | CJK 전각 2셀 폭 정상 | 필수 |
| 5 | vim insert → I-beam, normal → Arrow | 필수 |
| 6 | 고DPI 환경 오버레이 위치 정확 | 필수 |
| 7 | pane 전환 시 잔여 오버레이 없음 | 필수 |
| 8 | 34종 커서 매핑 (1차 30종 + Arrow fallback 4종) — 자세한 매핑은 `fr-02-mouse-cursor-shape.plan.md` §7 | 선택 |

---

## 참조

- **PRD**: `docs/00-pm/m13-input-ux.prd.md`
- **TSF 구현**: `src/tsf/` (94 E2E 테스트)
- **ADR-011**: TSF + Hidden HWND 한글 IME
- **렌더 루프**: `src/engine-api/ghostwin_engine.cpp` render_surface()
- **ghostty API**: `external/ghostty/include/ghostty.h` GHOSTTY_ACTION_MOUSE_SHAPE

---

*M-13 Plan v1.0 — Input UX (2026-04-17)*
