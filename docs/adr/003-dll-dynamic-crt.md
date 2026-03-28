# ADR-003: DLL + /MD (동적 CRT) 방식 채택 후 static lib으로 회귀

- **상태**: 폐기 → static lib으로 회귀
- **날짜**: 2026-03-29
- **관련**: Phase 1 빌드 구성

## 문제

프로젝트에서 CRT 상태 분리 이슈를 방지하기 위해 `.dll + /MD` (동적 CRT) 조합을 선택했다.

## 발견된 문제

ghostty-vt DLL을 MSVC 타겟으로 빌드하면 Zig가 DLL 내에 static CRT(`libucrt.lib`)를 링크한다. 그런데 DLL의 `_DllMainCRTStartup`에서 CRT 힙 초기화(`_acrt_initialize_heap`)가 실행되지 않아 `malloc`이 미초기화 힙에서 ACCESS_VIOLATION을 발생시킨다.

이것은 Zig의 Windows DLL CRT 초기화 메커니즘 제한이며, ghostty 코드의 문제가 아니다.

## 회귀 결정

**`ghostty-vt-static.lib` + `-Dsimd=false` + `x86_64-windows-gnu`** 사용.

- SIMD=false 시 CRT에 의존하지 않음 (`VirtualAlloc` 직접 사용)
- static lib이 EXE에 합쳐지므로 CRT 상태 분리 이슈 없음
- DLL + /MD의 원래 이점(CRT 공유)이 불필요해짐

## CRT 상태 분리에 대한 결론

| 조합 | CRT 개수 | 상태 분리 |
|------|:--------:|:---------:|
| static .lib + /MT | 1개 (EXE 내장) | 없음 |
| static .lib + /MD | 1개 (시스템 DLL) | 없음 |
| **.lib (simd=false)** | **CRT 불필요** | **없음** |

ghostty-vt가 CRT를 사용하지 않으므로 어떤 CRT 방식을 사용해도 충돌 없음.

## 향후

Zig 업스트림에서 Windows DLL CRT 초기화 문제가 해결되면 DLL 방식을 재검토.
참고 이슈: ziglang/zig#24052, ziglang/zig#19746
