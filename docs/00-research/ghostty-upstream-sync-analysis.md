# ghostty upstream 동기화 분석

> **분석일**: 2026-03-29
> **현재 서브모듈**: `562e7048c` (2026-03-28)
> **Upstream HEAD**: `debcffbad` (2026-03-28)
> **미동기화 커밋**: 25건
> **판단**: Phase 2 시작 시 동기화 필수
> **관련 ADR**: [002](../adr/002-c-bridge-pattern.md), [003](../adr/003-dll-dynamic-crt.md)

---

## 1. 현재 상태

### 서브모듈 로컬 패치 (HEAD → 현재)

현재 서브모듈에는 MSVC 호환을 위한 **로컬 패치 3건**이 적용되어 있다:

| 파일 | 변경 | 목적 |
|------|------|------|
| `include/ghostty/vt/terminal.h` | `GhosttyTerminalImpl*` → `GhosttyTerminal*` | MSVC C++ typedef 충돌 회피 |
| `include/ghostty/vt/render.h` | `GhosttyRenderStateImpl*` → `GhosttyRenderState*` (3개 타입) | 동일 |
| 기타 8개 헤더 | `FooImpl*` → `Foo*` | 동일 |

이 패치는 ADR-002(C 브릿지 패턴) 도입 전 적용되었으며, `vt_bridge.c`가 ghostty 헤더를 순수 C로만 include하기 때문에 **현재 빌드에서는 불필요**하다.

---

## 2. Upstream 변경 분류 (25건)

### A. GhostWin 직접 영향 — 동기화 필수 (5건)

| 커밋 | 날짜 | 설명 | 영향도 |
|------|------|------|:------:|
| `debcffb` | 03-28 | **libghostty: make headers C++ compatible** (#11950) | **High** |
| `1fcd80d` | 03-28 | C++ header compatibility — `typedef struct Foo*` → `typedef struct FooImpl*` | **High** |
| `8813261` | 03-28 | **libghostty: expose version information** via build options and C API | Medium |
| `741f1d1` | 03-28 | **example/c-vt-stream** — C 예제 추가 | Low |
| `0f6e733` | 03-28 | **build: use VERSION file** if present, expose via libghostty (#11932) | Low |

### B. Windows 빌드 관련 (3건)

| 커밋 | 날짜 | 설명 | 영향도 |
|------|------|------|:------:|
| `3187b18` | 03-28 | benchmark: disable test on windows (#11930) | Low |
| `60c7e76` | 03-28 | benchmark: disable test on windows | Low |
| `e20b506` | 03-28 | fix: replace hardcoded locale.h constants with TranslateC (#11920) | Low |

### C. 무관 (17건)

VOUCHED list 업데이트(4건), GTK kinetic scrolling(2건), iTerm2 colorschemes(2건), CLI edit-config(3건), doc clarify(2건), locale.h fix duplicate(1건), CLI dupe args(2건), build prep(1건).

---

## 3. 핵심 변경: C++ 헤더 호환 (#11950)

### 변경 내용

```c
// Before (현재 upstream, 우리 로컬 패치 전 상태)
typedef struct GhosttyTerminal* GhosttyTerminal;

// After (upstream #11950)
typedef struct GhosttyTerminalImpl* GhosttyTerminal;
```

12개 opaque handle typedef에 `Impl` suffix가 추가되었다. 이는 C++에서 struct tag와 typedef가 같은 namespace를 공유하는 문제를 해결한다.

### 영향받는 헤더 (12개 타입)

| 헤더 | 변경된 타입 |
|------|-----------|
| `terminal.h` | `GhosttyTerminalImpl*` |
| `render.h` | `GhosttyRenderStateImpl*`, `GhosttyRenderStateRowIteratorImpl*`, `GhosttyRenderStateRowCellsImpl*` |
| `formatter.h` | `GhosttyFormatterImpl*` |
| `key/encoder.h` | `GhosttyKeyEncoderImpl*` |
| `key/event.h` | `GhosttyKeyEventImpl*` |
| `mouse/encoder.h` | `GhosttyMouseEncoderImpl*` |
| `mouse/event.h` | `GhosttyMouseEventImpl*` |
| `osc.h` | `GhosttyOscParserImpl*`, `GhosttyOscCommandImpl*` |
| `sgr.h` | `GhosttySgrParserImpl*` |

### GhostWin에 미치는 영향

**기존 C 소비자와 소스 호환됨** — struct tag는 직접 참조된 적이 없으므로, `GhosttyTerminal` typedef 자체는 동일하게 유지된다. `vt_bridge.c`에서 `GhosttyTerminal term;` 같은 코드는 변경 없이 동작한다.

---

## 4. ADR-002 재검토

### 현재 ADR-002 결정

> "C 브릿지 레이어로 ghostty 헤더의 MSVC C++ 호환 문제 해결"

### Upstream #11950 이후

ghostty 헤더가 공식적으로 C++ 호환되었으므로, ADR-002의 **1차 동기**(MSVC C++ typedef 충돌)는 해소되었다.

### C 브릿지 유지 근거 (여전히 유효)

| 근거 | 설명 |
|------|------|
| **API 격리** | ghostty API 변경 시 수정 범위를 `vt_bridge.c` 1개 파일로 제한 |
| **void* 핸들 패턴** | ghostty 타입을 GhostWin 전체에 노출하지 않음 |
| **빌드 격리** | ghostty 헤더의 include 경로/의존성이 `vt_bridge.c`에만 영향 |
| **Phase 2+ 확장** | ConPTY 연동 시 추가 래핑 로직이 C 브릿지에 집중됨 |

**결론**: C 브릿지는 유지하되, ADR-002 문서에 "1차 동기는 upstream에서 해소됨, API 격리가 주된 유지 근거"로 갱신 필요.

---

## 5. 동기화 시 예상 작업

### 5.1 로컬 패치 충돌 해결

현재 로컬 패치는 upstream과 **정반대 방향**:
- 로컬: `FooImpl* → Foo*` (C++ 비호환 방향으로 되돌림)
- Upstream: `Foo* → FooImpl*` (C++ 호환 방향으로 전환)

**해결 방법**: 로컬 패치 3건을 **제거**하고 upstream 그대로 수용. `vt_bridge.c`는 순수 C이므로 `Impl` suffix 변경이 영향 없음.

### 5.2 새 헤더 파일

| 파일 | 내용 | 조치 |
|------|------|------|
| `include/ghostty/vt/build_info.h` | 버전 정보 API (5개 enum 추가) | 로컬 패치에서 이미 일부 삭제됨 → upstream 버전으로 교체 |

### 5.3 빌드 검증

동기화 후 반드시 수행:
1. `scripts/build_libghostty.ps1` — Zig 빌드 (새 소스 포함)
2. `scripts/build_ghostwin.ps1` — CMake 빌드 + 7/7 테스트
3. `vt_bridge.c` 컴파일 확인 — 헤더 변경이 C API에 영향 없는지

### 5.4 예상 소요 시간

| 작업 | 시간 |
|------|------|
| 서브모듈 업데이트 + 로컬 패치 제거 | 10분 |
| Zig 빌드 | 2분 |
| CMake 빌드 + 테스트 | 3분 |
| 문서 갱신 (ADR-002, CLAUDE.md) | 15분 |
| **합계** | **~30분** |

---

## 6. 동기화 체크리스트 (Phase 2 시작 시)

- [ ] `cd external/ghostty && git fetch origin main`
- [ ] `git checkout origin/main` (로컬 패치 제거)
- [ ] `cd ../.. && git add external/ghostty` (서브모듈 포인터 갱신)
- [ ] `scripts/build_libghostty.ps1` — Zig 빌드 확인
- [ ] `scripts/build_ghostwin.ps1` — 7/7 테스트 PASS 확인
- [ ] `vt_bridge.c` 컴파일 정상 확인 (typedef 변경 무영향)
- [ ] ADR-002 갱신: "1차 동기 해소, API 격리가 주된 유지 근거"
- [ ] CLAUDE.md 갱신: "ghostty 소스에 MSVC 호환 패치 3건 적용됨" 문구 제거
- [ ] 커밋: "chore: sync ghostty submodule to upstream (C++ header compat)"

---

## 7. 새 API 활용 가능성 (Optional)

### Version API (`build_info.h`)

```c
ghostty_build_info(GHOSTTY_BUILD_INFO_VERSION_STRING, &version_str);
ghostty_build_info(GHOSTTY_BUILD_INFO_VERSION_MAJOR, &major);
```

Phase 2 이후 디버그 로그에 ghostty 버전을 출력하는 용도로 활용 가능. 우선순위 낮음.

### cpp-vt-stream 예제

upstream에 C++ 예제(`example/cpp-vt-stream`)가 추가됨. VtCore 래퍼 설계의 레퍼런스로 참고 가능.
