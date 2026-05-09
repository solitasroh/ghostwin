# Phase 4-B 한글 IME Handoff v2

## 현재 상태: TSF 기반 IME 동작 + 7개 Critical 버그 수정 완료 + 테스트 인프라 구축 중

---

## 아키텍처 (최종)

```
GhostWin Window (WinUI3)
  ├── SwapChainPanel (DX11 렌더링)
  └── Hidden Win32 HWND (m_input_hwnd) ← 입력 전용
       ├── WM_CHAR → 영문/제어문자 → SendUtf8 → ConPTY
       ├── WM_KEYDOWN → 특수키 → SendVt (VT 시퀀스)
       ├── TSF AssociateFocus → 한글 IME 콜백
       │    ├── OnStartComposition → 조합 시작
       │    ├── DoCompositionUpdate → 조합/확정 분리 (GUID_PROP_COMPOSING)
       │    ├── OnEndComposition → PostMessage 지연 전송 (종성분리 대응)
       │    └── HandleOutput → SendUtf8 → ConPTY
       └── 50ms SetTimer → 포커스 유지 (WinUI3 탈취 대응)
```

### 핵심 설계 결정

| 결정 | 근거 |
|------|------|
| Hidden Win32 HWND | WinUI3 InputSite와 TSF 충돌 회피 |
| TSF (ITextStoreACP2 불필요) | WT와 동일 패턴 — CreateContext에 ITfContextOwnerCompositionSink 전달 시 TSF 내장 text store 활성화 |
| PostMessage 지연 전송 | TF_ES_SYNC가 TF_E_LOCKED로 실패 + 종성분리 시 m_lastComposing ≠ 실제 확정 대응 |
| comp_just_cleared 강제 리드로우 | 오버레이 해제 시 RenderLoop이 프레임 스킵하여 잔상 발생 방지 |
| 50ms SetTimer | WinUI3가 반복적으로 XAML 포커스를 탈취하므로 지속적 복원 필요 |

---

## 수정된 7개 Critical 버그

| # | 버그 | 수정 | 파일 |
|---|------|------|------|
| 1 | isSyllable 범위 부족 (자모/한자/이모지 전송 누락) | isSyllable 제거, PostMessage 지연 전송으로 전환 | tsf_implementation.cpp |
| 2 | m_lastComposing ≠ 실제 확정 (종성분리 시 잘못된 글자) | OnStartComposition에서 pending 취소, DoCompositionUpdate가 정확한 finalizedText 전송 | tsf_implementation.cpp |
| 3 | Ctrl+C/Escape TSF 미정리 (조합 상태 오염) | CancelComposition() 메서드 추가, HandleKeyDown에서 호출 | tsf_implementation.cpp, winui_app.cpp |
| 4 | Enter 타이밍 race (HasActiveComposition으로 삼켜짐) | 제어문자(CR/Tab/ESC/BS) HasActiveComposition 예외 처리 | winui_app.cpp |
| 5 | WM_CHAR 서로게이트 쌍 미처리 (이모지 깨짐) | m_pending_high_surrogate 멤버로 결합 | winui_app.cpp |
| 6 | WM_KILLFOCUS 조합 미정리 (Alt+Tab 누수) | m_composition 클리어 + surrogate 리셋 | winui_app.cpp |
| 7 | KillTimer 미호출 (앱 종료 시 dangling) | ShutdownRenderThread에 KillTimer 추가 | winui_app.cpp |

### 발견+수정된 렌더링 버그

| 버그 | 근본 원인 | 수정 |
|------|----------|------|
| BS 3회 후 ㅎ 잔상 | RenderLoop `!dirty && !has_comp` → 프레임 스킵 | `comp_just_cleared` 플래그로 1프레임 강제 리드로우 |

---

## 테스트 인프라

### Tier 1: 단위 테스트 (headless, CI 가능) — 33개

| 테스트 | 파일 | 결과 |
|--------|------|------|
| VT 한글 UTF-8 (10개) | tests/vt_core_test.cpp | 10/10 PASS |
| ConPTY UTF-8 왕복 (10개) | tests/conpty_integration_test.cpp | 10/10 PASS |
| QuadBuilder 렌더 (13개) | tests/quad_korean_test.cpp | 13/13 PASS |

### Tier 2: pyautogui E2E (시각 검증) — 45개

| 카테고리 | 파일 | 테스트 수 | 결과 |
|---------|------|----------|------|
| A: 한글 조합 | scripts/tests/test_a_hangul.py | 20 | 20/20 PASS |
| B: 특수키/제어 | scripts/tests/test_b_special.py | 14 | 14/14 PASS |
| C: 렌더링 시각 | scripts/tests/test_c_render.py | 4 | 4/4 PASS |
| D: 포커스/윈도우 | scripts/tests/test_d_focus.py | 7 | 7/7 PASS (E 포함) |

**실행**: `python scripts/run_all_tests.py`

### Tier 2 신뢰도 한계 (감사 결과)

| 한계 | 설명 |
|------|------|
| pixel_diff 0.0001 threshold | 커서 블링크로 false positive 가능 |
| has_glyph on 전체 윈도우 | 프롬프트 텍스트와 구분 불가 |
| "no crash" 주체 검증 | 프로세스 생존 ≠ 올바른 동작 |

### 개선 진행 중: OpenCV 자동 캘리브레이션

| 항목 | 상태 |
|------|------|
| `scripts/tests/calibration.py` | 구현 완료 (Calibrator 클래스) |
| grid_info.json 앱 덤프 | 구현 완료 (`{"grid_x":220,"grid_y":0,"cell_w":9,"cell_h":19}`) |
| mss 고속 캡처 | 설치 완료 |
| OpenCV 템플릿 매칭 | 설치 완료, 통합 테스트 미완 |
| 기존 45개 테스트에 정밀 검증 적용 | **미완 — 다음 세션** |

### Tier 3: in-process --test-ime — 12개

| 상태 | 설명 |
|------|------|
| T1~T4 | 동작 (foreground 확보 시) |
| T5~T12 | 한/영 전환 감지 문제 (GetKeyboardLayout ≠ IME 모드) |
| EnsureKoreanLayout | ImmGetOpenStatus로 교체 필요 |

---

## 코드 파일 상태

### 핵심 수정 파일

| 파일 | 주요 변경 |
|------|----------|
| `src/tsf/tsf_implementation.h` | CancelComposition, SendPendingDirectSend, m_pendingDirectSend 멤버 |
| `src/tsf/tsf_implementation.cpp` | PostMessage 지연 전송, CancelComposition, staleSession 가드, comp_just_cleared |
| `src/tsf/tsf_handle.h` | CancelComposition, SendPendingDirectSend, g_tsf_composition_start_count |
| `src/app/winui_app.h` | m_pending_high_surrogate, m_test_mode, RunImeTest |
| `src/app/winui_app.cpp` | Hidden HWND + InputWndProc, HandleKeyDown, WM_USER+50/99, 50ms 타이머, grid_info 덤프 |
| `src/conpty/conpty_session.h` | g_tap_mutex, g_tap_input, g_tap_echo |
| `src/conpty/conpty_session.cpp` | tap 콜백 호출 (mutex 보호) |

### 테스트 파일

| 파일 | 설명 |
|------|------|
| `tests/vt_core_test.cpp` | T8~T10 한글 UTF-8 단위테스트 |
| `tests/conpty_integration_test.cpp` | T9~T10 한글 왕복 테스트 |
| `tests/quad_korean_test.cpp` | Q1~Q3 QuadBuilder headless 테스트 (WARP) |
| `scripts/tests/helpers.py` | pyautogui 공통 헬퍼 (키 입력, 캡처, 픽셀 분석) |
| `scripts/tests/calibration.py` | OpenCV 자동 캘리브레이션 모듈 |
| `scripts/tests/test_a_hangul.py` | 한글 조합 20개 |
| `scripts/tests/test_b_special.py` | 특수키/제어 14개 |
| `scripts/tests/test_c_render.py` | 렌더링 시각 4개 |
| `scripts/tests/test_d_focus.py` | 포커스/윈도우 7개 |
| `scripts/tests/test_e_unicode.py` | 유니코드/인코딩 7개 |
| `scripts/run_all_tests.py` | 전체 테스트 실행기 |
| `scripts/test_bs_precise.py` | BS 단계별 캡처 (진단용) |
| `scripts/test_ime_external.py` | pyautogui 외부 검증 (초기 버전) |

---

## 다음 세션에서 해야 할 것

### 1순위: OpenCV 캘리브레이션 통합

1. `calibration.py`의 `find_char_in_image()` 완성 — 셀 단위 정밀 크롭 + 템플릿 매칭
2. grid_info.json 좌표를 사용한 정확한 셀 캡처
3. 기존 45개 테스트에 정밀 검증 적용 (has_glyph → 템플릿 매칭)
4. 캘리브레이션 레퍼런스 생성 프로토콜 확정

### 2순위: 미발견 잠재 버그 검증

에이전트 시나리오 분석에서 발견된 미검증 항목:

| 항목 | 설명 |
|------|------|
| 종성분리 정확성 | "하나"(G,K,S,K) 입력 시 실제로 "하"+"나"가 ConPTY에 전송되는지 |
| 조합 중 Escape | vim 모드 전환 시나리오 |
| DECCKM 미지원 | vim/tmux 화살표 동작 |
| Shift+Tab | 역방향 자동완성 |
| Alt+키 | readline 단축키 |
| 클립보드 붙여넣기 | Ctrl+V 전용 경로 + Bracket paste mode |

### 3순위: 테스트 안정성

| 항목 | 설명 |
|------|------|
| EnsureKoreanLayout | GetKeyboardLayout → ImmGetOpenStatus 교체 |
| test_c access violation | 앱 크래시 후 후속 테스트 보호 |
| test_e OpenClipboard | 재시도 로직 안정화 |

---

## 빌드 방법

```powershell
# 반드시 스크립트 사용 (cmake 직접 실행 금지)
powershell -ExecutionPolicy Bypass -File scripts/build_ghostwin.ps1 -Config Release

# 단위 테스트
build\vt_core_test.exe        # 10 PASS
build\conpty_integration_test.exe  # 10 PASS
build\quad_korean_test.exe    # 13 PASS

# pyautogui E2E
python scripts/run_all_tests.py   # 45 PASS

# 정밀 BS 테스트
python scripts/test_bs_precise.py

# --test-ime (in-process)
build\ghostwin_winui.exe --test-ime
```

## 실행

```
build\ghostwin_winui.exe    # Phase 4 WinUI3 앱
```
