# ADR-001: libghostty-vt를 -Dsimd=false + windows-gnu 타겟으로 빌드

- **상태**: 채택
- **날짜**: 2026-03-29
- **관련**: Phase 1 libghostty-vt 빌드 검증

## 문제

libghostty-vt를 `x86_64-windows-msvc` 타겟으로 빌드하면:
1. SIMD 활성화 시 C++ 의존성(highway, simdutf)이 MSVC CRT를 요구
2. DLL 빌드: static CRT(`libucrt.lib`)가 DLL 내에서 초기화되지 않아 `ghostty_terminal_new`에서 ACCESS_VIOLATION
3. Static lib 빌드: `/MT`로 빌드되어 `/MD` 프로젝트와 CRT 불일치 (`LNK2038`)
4. MSVC 타겟에서 `__favor`, `__isa_available` 등 MSVC 내부 심볼 누락

## 검토한 옵션

| 옵션 | 장점 | 단점 |
|------|------|------|
| MSVC + SIMD + DLL | 최고 성능 | CRT 미초기화 crash, 해결 불가 |
| MSVC + SIMD + static | SIMD 성능 | CRT 불일치, MSVC 심볼 누락 |
| MSVC + simd=false | MSVC 네이티브 | `__favor` 등 MSVC 심볼 누락 |
| **GNU + simd=false** | **CRT 독립, 빌드 안정** | SIMD 성능 없음 |

## 결정

**`-Dtarget=x86_64-windows-gnu -Dsimd=false`** 채택.

## 근거

1. `-Dsimd=false`는 공식 `c-vt-cmake-static` 예제에서도 사용하는 방식
2. SIMD 없이 빌드 시 CRT 의존성 완전 제거 — `VirtualAlloc` 직접 사용
3. GNU 타겟은 Zig 내장 MinGW libc 사용 — `--libc` 파일 불필요, 환경 의존성 제거
4. lib 크기 53MB → 7.8MB로 감소
5. MSVC C++ 프로젝트와 C ABI로 링크 시 GNU/MSVC 구분 무관

## 결과

- 빌드 명령: `zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false`
- 7/7 테스트 PASS
- 향후 SIMD 성능이 필요하면 Zig 업스트림의 Windows MSVC DLL CRT 초기화 문제 해결 후 재검토
