# ADR-002: C 브릿지 레이어로 ghostty 헤더 격리

- **상태**: 채택
- **날짜**: 2026-03-29
- **관련**: Phase 1 VtCore 래퍼 설계

## 문제

ghostty C 헤더에서 `typedef struct GhosttyTerminal* GhosttyTerminal;` 패턴을 사용한다. 이것은 C에서는 합법적이지만 **MSVC C++ 컴파일러에서 에러**가 발생한다 (C2040: struct 태그와 typedef 이름 충돌).

`extern "C" {}` 블록 안에서 include해도 MSVC C++은 이 패턴을 거부한다.

## 검토한 옵션

| 옵션 | 장점 | 단점 |
|------|------|------|
| ghostty 헤더를 C++에서 직접 include | 단순 | MSVC에서 컴파일 불가 |
| void*로 캐스팅 + extern "C" | 래퍼 불필요 | 여전히 헤더 include 시 에러 |
| **C 브릿지 파일(.c) 분리** | **정석 FFI 패턴** | 파일 1개 추가 |
| ghostty 헤더를 직접 수정 | 근본 해결 | 업스트림 패치 필요, 유지보수 부담 |

## 결정

**`vt_bridge.c` (순수 C) + `vt_bridge.h` (C/C++ 호환) + `vt_core.cpp` (C++)** 3-파일 구조 채택.

## 근거

1. C 라이브러리를 C++에서 사용할 때 헤더 호환 문제가 있으면 C 브릿지를 두는 것이 표준적인 FFI 패턴
2. ghostty C 헤더를 수정하면 업스트림과 diverge하여 서브모듈 업데이트 시 충돌
3. `vt_bridge.c`만 ghostty 헤더를 include — 변경 격리 범위 최소화
4. `vt_bridge.h`는 `void*`만 노출하므로 C/C++ 모두에서 사용 가능

## 구조

```
vt_bridge.h   — C/C++ 호환 인터페이스 (void* 핸들)
vt_bridge.c   — 순수 C, ghostty 헤더 include, C API 호출
vt_core.h     — C++ 공개 인터페이스 (ghostwin::VtCore)
vt_core.cpp   — C++ 구현, vt_bridge.h만 include
```
