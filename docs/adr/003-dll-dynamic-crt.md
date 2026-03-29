# ADR-003: DLL 방식 유지 (GNU static lib → MSVC 링커 불호환 확인)

- **상태**: 채택 — DLL 방식 유지
- **날짜**: 2026-03-29
- **갱신**: 2026-03-29 (static lib 직접 링크 불가 확인)
- **관련**: Phase 1 빌드 구성

## 배경

프로젝트에서 CRT 상태 분리 이슈를 방지하기 위해 `.dll + /MD` (동적 CRT) 조합을 선택했다.

## 발견된 문제 1: MSVC 타겟 DLL CRT 미초기화

ghostty-vt DLL을 MSVC 타겟으로 빌드하면 Zig가 DLL 내에 static CRT(`libucrt.lib`)를 링크한다. 그런데 DLL의 `_DllMainCRTStartup`에서 CRT 힙 초기화(`_acrt_initialize_heap`)가 실행되지 않아 `malloc`이 미초기화 힙에서 ACCESS_VIOLATION을 발생시킨다.

이것은 Zig의 Windows DLL CRT 초기화 메커니즘 제한이며, ghostty 코드의 문제가 아니다.

## 발견된 문제 2: GNU static lib + MSVC 링커 COMDAT 불호환

`ghostty-vt-static.lib`(`x86_64-windows-gnu` 타겟)을 MSVC 링커(link.exe)로 직접 링크하면:

```
ghostty-vt-static.lib(compiler_rt.obj) : fatal error LNK1143:
잘못되었거나 손상되었습니다. 0x5 COMDAT 정의에 대한 기호가 없습니다.
```

Zig GNU 타겟이 생성하는 `compiler_rt.obj`의 COMDAT 심볼 구조가 MSVC 링커와 호환되지 않는다.

## 결정: DLL 방식 유지

**`ghostty-vt.dll` + `ghostty-vt.lib`(import lib) + `-Dsimd=false` + `x86_64-windows-gnu`** 사용.

- DLL이 GNU/MSVC 간 **격리 계층** 역할 — MSVC 링커는 import lib만 참조
- `compiler_rt.obj` 등 Zig 런타임은 DLL 내부에 격리되어 MSVC 링커가 보지 않음
- `-Dsimd=false`로 CRT 의존성 제거 → DLL CRT 미초기화 문제 회피 (7/7 테스트 PASS)

## 검토한 옵션

| 방식 | 결과 | 비고 |
|------|------|------|
| GNU DLL + import lib | **7/7 PASS** | 채택 — DLL이 격리 계층 |
| GNU static lib 직접 링크 | **LNK1143** | compiler_rt COMDAT 불호환 |
| MSVC 타겟 DLL | ACCESS_VIOLATION | CRT 미초기화 crash |
| MSVC 타겟 static lib | LNK2038 / 심볼 누락 | ADR-001 참조 |

## CRT 상태 분리에 대한 결론

| 조합 | CRT 개수 | 상태 분리 |
|------|:--------:|:---------:|
| DLL(simd=false) + /MD | 1개 (시스템 DLL) | 없음 |

ghostty-vt가 `-Dsimd=false`로 CRT를 사용하지 않으므로, DLL 내에 별도 CRT가 로드되지 않고 CRT 상태 분리가 발생하지 않는다.

## 향후

Zig 업스트림에서 다음 중 하나가 해결되면 재검토:
- GNU static lib의 MSVC 링커 호환성 개선
- Windows DLL CRT 초기화 문제 해결

참고 이슈: ziglang/zig#24052, ziglang/zig#19746
