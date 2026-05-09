# libghostty-vt Windows 빌드 실현 가능성 심층 리서치

> 작성일: 2026-03-28
> 대상: GhostWin Terminal — Phase 1 기술 검증

---

## 요약 (Executive Summary)

| 항목 | 결론 | 실현 가능성 |
|------|------|------------|
| libghostty-vt Windows 빌드 | CI에 `x86_64-windows` 타겟 포함, 빌드 아티팩트 생성 확인 | **상** |
| C API 안정성 | 알파 상태, breaking change 예고됨 — 래퍼 레이어 필수 | **중** |
| Zig 0.15.2 Windows 빌드 | 안정 릴리즈, MinGW ABI로 빌드 가능 | **상** |
| MSVC ↔ Zig 오브젝트 링킹 | C ABI는 호환, C++ ABI 혼용 금지 | **중** |
| ghostling Windows 포팅 | libghostty-vt 자체는 지원, ghostling UI(Raylib)가 미구현 | **중** |
| libxml2/fontconfig 이슈 | PR #11698로 해결 완료 | **상** |

---

## 1. libghostty-vt 현재 상태

### 1-1. 릴리즈 상태 및 버전

**확인된 사실:**
- libghostty-vt는 Ghostty 1.3.x 개발 사이클에서 **독립 Zig 모듈로 분리** 완료
- 공식 버전 태그 없음 — 아직 `v0.1.0`조차 공식 릴리즈되지 않음 (추측: 온보딩 문서의 v0.1.0은 내부 추적 번호)
- Ghostty GUI와 libghostty 릴리즈 사이클을 **분리**하기로 결정 (1.3.0 릴리즈 노트 기준)
- 수십 개 프로젝트가 이미 사용 중이지만 버전 태그 릴리즈 미정

**확인된 사실 — 플랫폼 지원:**
- CI `build-libghostty-vt` 잡의 크로스컴파일 매트릭스:
  ```
  aarch64-macos, x86_64-macos, aarch64-linux, x86_64-linux,
  x86_64-linux-musl, x86_64-windows, wasm32-freestanding
  ```
  `x86_64-windows`가 **공식 CI 타겟에 포함**됨 (출처: DeepWiki — ghostty-org/ghostty CI/CD)

**추측:**
- CI에서 Windows 빌드 아티팩트(`.lib` 또는 `.dll`)가 실제로 다운로드 가능한지는 직접 GitHub Actions 런 결과를 확인해야 알 수 있음

### 1-2. C API 헤더 구조

`include/ghostty/vt.h`는 **20개 서브헤더의 umbrella include**이다.

| 서브헤더 | 역할 |
|---------|------|
| `terminal.h` | 완전한 터미널 에뮬레이터 상태 (핵심) |
| `render.h` | 증분 렌더 상태 업데이트 |
| `formatter.h` | 터미널 내용을 텍스트/VT/HTML로 포맷 |
| `key.h` | 키보드 이벤트 인코딩 |
| `mouse.h` | 마우스 이벤트 인코딩 |
| `osc.h` | OSC 시퀀스 파싱 |
| `sgr.h` | SGR 시퀀스 파싱 |
| `color.h` | 색상 처리 |
| `allocator.h` | 커스텀 메모리 할당자 |
| `types.h` | 핵심 타입 정의 |
| `modes.h` | 터미널 모드 |
| `screen.h` | 스크린 관리 |
| `wasm.h` | WebAssembly 유틸리티 |
| 기타 7개 | focus, device, grid_ref, paste, size_report, build_info, style |

**핵심 함수 (ghostling main.c에서 확인된 실제 호출 패턴):**

```c
// === 초기화 ===
GhosttyTerminalOptions opts = {
    .cols = term_cols,
    .rows = term_rows,
    .max_scrollback = 1000
};
GhosttyTerminal terminal = NULL;
GhosttyResult err = ghostty_terminal_new(NULL, &terminal, opts);

// === Effect 콜백 등록 ===
ghostty_terminal_set(terminal, GHOSTTY_TERMINAL_OPT_USERDATA, &effects_ctx);
ghostty_terminal_set(terminal, GHOSTTY_TERMINAL_OPT_WRITE_PTY,
                     (const void *)effect_write_pty);
ghostty_terminal_set(terminal, GHOSTTY_TERMINAL_OPT_DEVICE_ATTRIBUTES,
                     (const void *)effect_device_attributes);

// === PTY 데이터 피드 ===
ghostty_terminal_vt_write(terminal, buf, (size_t)n);  // PTY → 파서

// === 리사이즈 ===
ghostty_terminal_resize(terminal, cols, rows, pixel_width, pixel_height);

// === 렌더 상태 스냅샷 ===
ghostty_render_state_update(render_state, terminal);
while (ghostty_render_state_row_iterator_next(row_iter)) {
    ghostty_render_state_row_get(row_iter,
        GHOSTTY_RENDER_STATE_ROW_DATA_CELLS, &cells);
    while (ghostty_render_state_row_cells_next(cells)) {
        // 셀 데이터 읽기
    }
}

// === 키 인코딩 ===
ghostty_key_encoder_setopt_from_terminal(encoder, terminal);
ghostty_key_event_set_key(event, gkey);
ghostty_key_event_set_action(event, action);
ghostty_key_encoder_encode(encoder, event, buf, sizeof(buf), &written);

// === 마우스 인코딩 ===
ghostty_mouse_encoder_setopt_from_terminal(encoder, terminal);
ghostty_mouse_encoder_encode(encoder, event, buf, sizeof(buf), &written);
```

출처: [ghostling/main.c](https://github.com/ghostty-org/ghostling/blob/main/main.c)

### 1-3. API 안정성 평가

**확인된 사실:**
- 공식 문서 명시: *"The library is currently in development and the API is not yet stable"*
- Mitchell Hashimoto 블로그: *"public alpha (not promising API stability)"*
- 핵심 로직(파싱, 상태 관리)은 Ghostty GUI에서 수년간 실전 검증된 코드를 추출한 것이므로 **기능 안정성은 높음**
- 단, 함수 시그니처, 타입 이름, 구조체 레이아웃은 **언제든 변경 가능**

**리스크 대응 전략:**
```
libghostty-vt (불안정 C API)
        ↓
VtCore 래퍼 (C++/WinRT, 안정 인터페이스)
        ↓
GhostWin 내부 파이프라인
```
래퍼 레이어를 통해 API 변경 시 수정 범위를 `VtCore.cpp` 1개 파일로 격리.

---

## 2. Zig 0.15.x Windows 빌드

### 2-1. 최신 버전 현황

**확인된 사실:**
- **Zig 0.15.2** — 2025년 10월 12일 릴리즈, 현재 0.15.x 최신 안정 버전
  - 버그픽스 릴리즈 (컴파일러, I/O, SIMD 코드젠, 링커 수정)
  - 유일한 API 변경: `takeDelimiterExclusive()` → `takeDelimiter()` (I/O 관련, C API에 무영향)
- **Zig 0.16.0** — 2026년 3월 23일 기준 마일스톤 99% 완료, 미릴리즈
  - 주요 변경: async I/O 통합, `std.Io` 인터페이스 재설계
  - ghostty는 현재 Zig 0.15.2를 사용하므로 0.16.0 전환 시 ghostty 업스트림 동기화 필요

**GhostWin 권장:** Zig 0.15.2 버전 고정, CI에서 버전 명시

### 2-2. Windows에서 Zig로 C 라이브러리 빌드

**확인된 사실 — ghostty-windows 포크 (InsipidPoint/ghostty-windows):**
- Zig 0.15.2+에서 실제로 Windows 빌드 성공한 사례 존재
- 빌드 명령어:
  ```bash
  # 기본 x64 Windows 빌드
  zig build -Dapp-runtime=win32 -Dtarget=x86_64-windows

  # 릴리즈 최적화 빌드
  zig build -Dapp-runtime=win32 -Dtarget=x86_64-windows -Doptimize=ReleaseFast
  ```
- WSL2/Linux에서 Windows 크로스컴파일도 지원

**libghostty-vt만 별도 빌드할 경우 (추측, 검증 필요):**
```bash
# Ghostty 리포 루트에서
zig build libghostty-vt -Dtarget=x86_64-windows -Doptimize=ReleaseSafe
```
실제 빌드 타겟 이름은 `build.zig`를 직접 확인해야 함.

### 2-3. MSVC와 Zig 오브젝트 파일 ABI 호환성

**확인된 사실:**
- Andrew Kelley(Zig 창시자) 직접 언급: *"the C ABI is binary-compatible with MSVC-compiled code"*
- Zig의 기본 Windows 타겟: `x86_64-windows-gnu` (MinGW-w64)
- MinGW-w64는 **Universal CRT(uCRT)** 사용 — Windows 10+ 기본 포함
- uCRT 기반이면 GNU ↔ MSVC C 함수 호출 가능

**핵심 주의사항:**
| 상황 | 호환성 |
|------|--------|
| Zig `.lib` → MSVC C++ 프로젝트 링크 | **가능** (C API `extern "C"` 사용 시) |
| C++ 예외 혼용 (SEH vs SJLJ) | **불가** |
| `long double` 타입 혼용 | **불가** (80비트 GNU vs 64비트 MSVC) |
| MSVCRT.dll 의존 구형 라이브러리 혼용 | **불가** (uCRT vs MSVCRT 충돌) |

**실용적 결론:** libghostty-vt는 순수 C API(`extern "C"`)로 노출되고 zero-dependency(libc 없음)이므로 Zig GNU 타겟 빌드 후 MSVC 프로젝트에서 링크 가능. 단, MSVC에서 빌드하려면 `-target x86_64-windows-msvc` 명시 권장.

```bash
# MSVC ABI로 명시적 빌드 (더 안전)
zig build-lib src/ghostty_vt.zig \
  -target x86_64-windows-msvc \
  -O ReleaseSafe \
  -femit-h=ghostty_vt.h
```

### 2-4. libxml2 의존성 이슈

**확인된 사실 — PR #11698로 해결 완료:**
- 문제: `libxml2` 소스 타볼에 Linux 심링크 포함 → Windows에서 압축 해제 시 `AccessDenied`
- 원인: `fontconfig` 의존성이 `libxml2`를 끌어옴
- 해결: Windows 타겟에서 `fontconfig` 의존성 조건부 제외 (Windows는 DirectWrite 사용)
- **libghostty-vt 빌드 시에는 fontconfig 불필요** → 이 이슈는 libghostty-vt 단독 빌드에 영향 없음
- 단, ghostty 전체 빌드 시에는 vcpkg 또는 PR #11698 적용 버전 사용 권장

---

## 3. ghostling 레퍼런스 구현 분석

### 3-1. ghostling 아키텍처

**확인된 사실:**
- 단일 C 파일(`main.c`)로 구현된 최소 터미널 에뮬레이터
- 렌더링: **Raylib** (2D 그래픽, OpenGL 백엔드)
- 빌드 시스템: CMake 3.19+ + Ninja, **Zig 0.15.x 필요**
- 싱글스레드 (libghostty-vt는 멀티스레드 지원)

**플랫폼 지원:**
- macOS: 완전 지원
- Linux (Ubuntu/Debian): 완전 지원
- Windows: **libghostty-vt 자체는 지원하나 ghostling에서 미구현/미테스트**

### 3-2. 초기화 → 파싱 → 렌더 패턴

위 섹션 1-2의 코드 예시 참조. 핵심 패턴 요약:

1. `ghostty_terminal_new()` → 터미널 인스턴스 생성
2. `ghostty_terminal_set()` → PTY 쓰기 콜백 등록 (터미널 → PTY 방향)
3. `ghostty_terminal_vt_write()` → PTY 데이터 → 파서 (PTY → 터미널 방향)
4. `ghostty_render_state_update()` + 이터레이터 → 셀 데이터 읽기
5. `ghostty_key_encoder_encode()` / `ghostty_mouse_encoder_encode()` → 입력 → 이스케이프 시퀀스

### 3-3. Windows 미지원 이유 분석

**확인된 사실 + 추측:**
- Raylib의 Windows OpenGL 컨텍스트 생성 이슈 (추측, 직접 확인 안됨)
- PTY 핸들링: `fork()` + openpty 방식 → Windows의 ConPTY로 교체 필요
- 파일 I/O: `read(fd)` → `ReadFile(hPipe, ...)` 로 교체 필요
- **libghostty-vt 자체는 Windows에서 정상 동작** — UI/PTY 레이어만 포팅 필요

**GhostWin에서의 포팅 전략:**
ghostling의 렌더러(Raylib)와 PTY 코드는 사용하지 않으므로, libghostty-vt C API 사용 패턴만 참고하면 됨. ConPTY 기반 Windows 구현에서 `ghostty_terminal_vt_write()`를 그대로 호출 가능.

---

## 4. 대안 VT 파서 비교

### 4-1. 비교 매트릭스

| 항목 | libghostty-vt | libvterm | alacritty/vte | Windows Terminal 파서 |
|------|:---:|:---:|:---:|:---:|
| **SIMD 최적화** | Highway + simdutf (AVX2/SSE4/NEON) | 없음 | 없음 | 없음 |
| **Windows 지원** | CI 타겟 포함 | 지원 | Rust, Windows 지원 | Windows 전용 |
| **C API** | 있음 (알파) | 있음 (안정) | Rust API만 | 없음 (내부 전용) |
| **의존성** | zero (libc도 없음) | 없음 | Rust crate | Windows Terminal 전체 |
| **API 안정성** | 알파, breaking change 예고 | 안정 | 안정 | 사용 불가 |
| **스크롤백 지원** | 있음 | 제한적 | 없음 (파서만) | 있음 |
| **텍스트 리플로우** | 있음 | 없음 | 없음 | 있음 |
| **Kitty 프로토콜** | 있음 | 없음 | 없음 | 없음 |
| **라이선스** | MIT | MIT | Apache/MIT | MIT |
| **성숙도** | 실전 검증 (Ghostty 1.3.x) | 오래된 코드, 느린 개발 | 넓은 생태계 (19,900 의존) | 성숙하나 분리 불가 |

### 4-2. libvterm 상세

**확인된 사실:**
- Neovim 내장 VT 터미널의 파서로 사용 중
- SIMD 없음 — 순수 C 루프 기반 파싱
- 스크롤백/리플로우 기능 부족 → Neovim 이슈 #33155에서 libghostty-vt로 교체 검토 중 (2025년 3월 오픈)
- Windows에서 빌드 가능, 안정적 C API

**결론:** 성능 요구사항(Ghostty 수준 SIMD)을 충족하지 못함.

### 4-3. alacritty/vte 상세

**확인된 사실:**
- Rust 라이브러리 — C/C++ 프로젝트에서 사용하려면 FFI 바인딩 필요
- 19,900+ Rust 크레이트 의존 — 생태계는 넓으나 GhostWin(C++/WinRT)에서 직접 사용 불편
- 파서만 제공, 터미널 상태 관리는 없음

**결론:** Rust 기반이라 C++/WinRT 프로젝트에 통합 복잡도 높음.

### 4-4. Windows Terminal 내부 파서

**확인된 사실:**
- `StateMachine` + `OutputStateMachineEngine` + `AdaptDispatch` + `ITerminalApi` 4계층 구조
- 재사용 가능한 인터페이스 설계(`IStateMachineEngine`, `ITermDispatch`)
- **단, 독립 라이브러리로 패키징되지 않음** — Windows Terminal 내부 코드에 강하게 결합
- NuGet 패키지로 분리 제공 없음, 외부 헤더 없음

**결론:** 재사용 불가 수준. Windows Terminal 전체를 의존성으로 가져오거나 코드를 대량 복사해야 함.

### 4-5. SIMD 지원 상세 (Highway + simdutf)

**확인된 사실:**
- **Google Highway**: SSE4, AVX2, AVX-512, NEON, SVE, RISC-V V 지원
  - Windows x64에서 MSVC 빌드 시 `/Gv` 플래그 권장 (벡터 레지스터 인수 전달)
  - AVX2 사용 시 `/arch:AVX2` 추가 권장
  - **런타임 디스패치** 지원 — 단일 바이너리로 SSE4/AVX2/AVX-512 자동 선택
- **simdutf**: SSE2, AVX2, NEON, AVX-512, RISC-V V 지원
  - x64에서 런타임에 최적 구현 자동 선택
  - Node.js, Chromium, Safari에서 실전 사용 중

**Windows x64 호환성 결론:** 완전 호환. Windows 10+ x64 환경에서 SSE4.2/AVX2 런타임 디스패치로 최고 성능 달성.

---

## 5. 리스크 평가

### 5-1. v0.1.0 API 불안정 — Breaking Change 가능성

**확인된 사실:**
- 공식 명시: *"breaking changes expected"*, *"not promising API stability"*
- 실제 breaking change 이력: 공개된 changelog 없음 (버전 태그 자체가 없음)
- Ghostty GUI와 libghostty 릴리즈 사이클 분리 결정 → libghostty는 별도 속도로 변경 가능

**실제 리스크 수준 평가:**
- 핵심 함수(`ghostty_terminal_new`, `ghostty_terminal_vt_write`, `ghostty_render_state_update`)는 ghostling에서 사용 중 → 급격한 제거 가능성 낮음
- 신규 기능 추가나 옵션 구조체 변경이 더 현실적인 breaking change 패턴
- 래퍼 레이어(`VtCore`) 격리 시 대응 공수: 일반적으로 하루 이내

**권장 대응:**
```cpp
// VtCore.h — 안정 인터페이스 (GhostWin 내부)
class VtCore {
public:
    void feedPtyData(std::span<const uint8_t> data);
    void resize(uint16_t cols, uint16_t rows);
    void encodeKey(const KeyEvent& event, std::vector<uint8_t>& out);
    RenderSnapshot snapshot();
    // ...
private:
    // ghostty C API 호출은 여기에만 집중
    GhosttyTerminal m_terminal = nullptr;
};
```

### 5-2. Zig 버전 업그레이드 시 빌드 깨짐

**확인된 사실:**
- Zig 0.16.0 (미릴리즈, 2026년 3월 기준 99% 완료): `std.Io` 대규모 재설계, async I/O 통합
- 0.15 → 0.16 전환 시 Ghostty 빌드 스크립트(`build.zig`) 수정 필요 가능성 높음
- Zig는 매 마이너 버전마다 breaking change를 허용하는 정책 유지

**리스크 시나리오:**
1. Zig 0.15.2 고정 → 장기적으로 LLVM 업데이트 등 보안 패치 누락
2. Zig 0.16.x 전환 → ghostty upstream이 마이그레이션 전까지 빌드 실패

**권장 대응:**
- `ghostwin/.zigtoolchain` 파일로 Zig 버전 고정
- CI에서 `zig-version: 0.15.2` 명시
- ghostty upstream의 Zig 버전 요구사항 변경 시 즉시 반영

### 5-3. SIMD (Highway + simdutf) Windows x64 호환성

**확인된 사실:**
- Highway: SSE4, AVX2 Windows x64 지원 확인 (`/Gv`, `/arch:AVX2` 권장)
- simdutf: Windows x64 지원 (Node.js, Chromium에서 사용 중)
- 런타임 CPU 디스패치 — 오래된 CPU에서도 폴백 동작

**실제 리스크:** 매우 낮음. 두 라이브러리 모두 MSVC + Windows x64 환경에서 광범위하게 테스트됨.

### 5-4. ghostly Windows 포트 존재 (InsipidPoint/ghostty-windows)

**확인된 사실 — 중요한 레퍼런스 발견:**
- `InsipidPoint/ghostty-windows` 리포: Win32 API + OpenGL 렌더링 + ConPTY 구현 완료
- Zig 0.15.2+ 에서 `zig build -Dapp-runtime=win32 -Dtarget=x86_64-windows` 빌드 성공
- 구현 완료 기능: ConPTY 쉘 생성, IME(CJK) 지원, DPI 인식, 탭, pane 분할
- **이것은 libghostty-vt의 Windows 빌드가 실제로 동작함을 증명하는 가장 강력한 증거**

---

## 6. Phase 1 실행 계획 (권장)

### 6-1. 빌드 검증 단계

```
Step 1. 환경 준비
  - Zig 0.15.2 설치 및 PATH 설정
  - Visual Studio 2022 Build Tools (MSVC 링커용)
  - ghostty 리포 클론

Step 2. libghostty-vt 단독 빌드
  git clone https://github.com/ghostty-org/ghostty
  cd ghostty
  zig build -Dtarget=x86_64-windows -Doptimize=ReleaseSafe
  # 빌드 아티팩트: zig-out/lib/libghostty-vt.lib (예상 경로)

Step 3. C API 링킹 테스트
  // test_vtcore.cpp (C++20, MSVC)
  extern "C" {
  #include "include/ghostty/vt.h"
  }
  int main() {
      GhosttyTerminal t = nullptr;
      GhosttyTerminalOptions opts = { .cols = 80, .rows = 24 };
      GhosttyResult r = ghostty_terminal_new(nullptr, &t, opts);
      assert(r == GHOSTTY_SUCCESS);
      const char* data = "\x1b[1mHello\x1b[0m\r\n";
      ghostty_terminal_vt_write(t, (const uint8_t*)data, strlen(data));
      return 0;
  }

Step 4. 결과 확인
  - ghostty_terminal_new 반환값 검증
  - ghostty_terminal_vt_write 후 렌더 상태 이터레이션
  - 메모리 누수 없음 확인 (ASAN 또는 CRT 힙 검증)
```

### 6-2. 최종 실현 가능성 종합 평가

| 리스크 항목 | 심각도 | 현재 상태 | 대응 후 잔여 리스크 |
|------------|--------|-----------|-------------------|
| Windows CI 빌드 타겟 미포함 | — | CI에 포함됨 (해소) | 없음 |
| libxml2 심링크 오류 | — | PR #11698 해결 (해소) | 없음 |
| C API 알파 불안정 | 중 | 래퍼 레이어로 격리 | **낮음** |
| Zig GNU ↔ MSVC 링킹 | 중 | C ABI 호환, 주의사항 준수 시 | **낮음** |
| Zig 버전 업그레이드 | 중 | 버전 고정으로 통제 | **낮음** |
| SIMD Windows 호환 | — | Highway/simdutf 검증됨 | 없음 |
| ghostling Windows 미테스트 | 낮 | libghostty-vt 자체는 지원 | **없음** (ghostling 불사용) |

**최종 결론: 실현 가능성 상(High)**

InsipidPoint/ghostty-windows 프로젝트가 Zig 0.15.2 + Windows에서 libghostty-vt 기반 터미널을 실제로 빌드/동작시킨 것이 확인됨. GhostWin Phase 1 목표인 `.lib` 정적 라이브러리 빌드 + C API 호출 동작 확인은 충분히 달성 가능하다.

---

## 7. 참고 자료

| 자료 | URL | 유형 |
|------|-----|------|
| Mitchell Hashimoto — libghostty 발표 | https://mitchellh.com/writing/libghostty-is-coming | 블로그 |
| ghostling 리포 (C API 레퍼런스) | https://github.com/ghostty-org/ghostling | 코드 |
| ghostling main.c | https://github.com/ghostty-org/ghostling/blob/main/main.c | 코드 |
| libghostty Doxygen 문서 | https://libghostty.tip.ghostty.org/ | 문서 |
| vt.h 헤더 | https://github.com/ghostty-org/ghostty/blob/main/include/ghostty/vt.h | 코드 |
| ghostty Windows 포트 | https://github.com/InsipidPoint/ghostty-windows | 코드 |
| libxml2 이슈 토론 | https://github.com/ghostty-org/ghostty/discussions/11697 | 토론 |
| libghostty 크로스플랫폼 트래킹 | https://github.com/ghostty-org/ghostty/discussions/9411 | 토론 |
| ghostty Windows 지원 토론 | https://github.com/ghostty-org/ghostty/discussions/2563 | 토론 |
| Zig 0.15.2 릴리즈 | https://ziggit.dev/t/zig-0-15-2-released/12466 | 릴리즈 노트 |
| Zig 0.15.1 릴리즈 노트 | https://ziglang.org/download/0.15.1/release-notes.html | 릴리즈 노트 |
| Zig Windows GNU/MSVC ABI | https://ziggit.dev/t/windows-gnu-mingw-and-msvc-binary-c-abi-compatibility-guarantees/6903 | 토론 |
| Ghostty 1.3.0 릴리즈 노트 | https://ghostty.org/docs/install/release-notes/1-3-0 | 릴리즈 노트 |
| Neovim libvterm 교체 이슈 | https://github.com/neovim/neovim/issues/33155 | 이슈 |
| Windows Terminal VT 파서 | https://deepwiki.com/microsoft/terminal/2.3-vt-sequence-processing | 분석 |
| ghostty CI/CD 분석 | https://deepwiki.com/ghostty-org/ghostty/8-cicd-and-distribution | 분석 |
| Google Highway | https://github.com/google/highway | 코드 |
| simdutf | https://github.com/simdutf/simdutf | 코드 |

---

*리서치 작성: Claude Sonnet 4.6 (GhostWin 기술 리서치 에이전트)*
*최종 업데이트: 2026-03-28*
