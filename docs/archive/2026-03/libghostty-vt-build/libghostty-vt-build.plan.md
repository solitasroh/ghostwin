# libghostty-vt-build Planning Document

> **Summary**: libghostty-vt 라이브러리를 Windows에서 정적 빌드하고 C API 호출을 검증한다
>
> **Project**: GhostWin Terminal
> **Version**: 0.3.0
> **Author**: Solit
> **Date**: 2026-03-29
> **Status**: Implementation Complete — 7/7 Tests Passed

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | GhostWin의 VT 파싱 코어인 libghostty-vt가 Windows에서 빌드 가능한지 검증되지 않았다 |
| **Solution** | Zig 0.15.2 + `-Dtarget=x86_64-windows-gnu -Dsimd=false`로 static lib 빌드, C 브릿지 패턴으로 MSVC C++ 호환 래퍼 구현 |
| **Function/UX Effect** | 빌드 검증 성공 시 GhostWin 전체 파이프라인(VT→ConPTY→D3D11→WinUI3) 실현 가능성이 확인된다 |
| **Core Value** | 프로젝트 핵심 의존성의 실현 가능성을 조기에 검증하여 기술 리스크를 제거한다 |

### Research Confidence: High

> 5개 병렬 리서치 에이전트의 교차 검증 결과, Phase 1 실현 가능성은 **상(High)**.
> ghostty-windows 포크에서 실제 빌드+동작이 확인되어 난이도가 대폭 감소됨.
> 상세: `docs/00-research/technical-feasibility-report.md`

---

## 1. Overview

### 1.1 Purpose

GhostWin Terminal의 VT 파싱 코어로 선정된 libghostty-vt(Ghostty 프로젝트의 독립 VT 파서 라이브러리)가 Windows 환경에서 정상적으로 빌드되고, C API를 통해 호출 가능한지 검증한다.

이 단계가 실패하면 대안 VT 파서를 검토해야 하므로, 프로젝트 전체 아키텍처에 영향을 미치는 **게이트키퍼 검증**이다.

### 1.2 Background

- libghostty-vt는 Ghostty(v1.3.1, MIT) 프로젝트에서 분리된 독립 VT 파서
- **[리서치 업데이트]** 공식 버전 태그 없음 — onboarding.md의 "v0.1.0"은 내부 추적 번호. 커밋 해시로 버전 고정 필요
- Zig 0.15.2+ 필요, zero-dependency (libc 의존성도 없음)
- SIMD 최적화 (Highway + simdutf) — 대용량 출력에서 성능 우위
- **[리서치 확인]** Windows CI `build-libghostty-vt` 잡에 `x86_64-windows` 공식 포함
- **[리서치 발견]** InsipidPoint/ghostty-windows 포크에서 Zig 0.15.2 + Win32 + ConPTY로 실제 빌드+동작 확인
- ghostling(레퍼런스 구현)은 아직 Windows 미지원이나, 라이브러리 자체는 지원

### 1.3 Related Documents

- onboarding.md — 프로젝트 전체 컨텍스트
- `docs/00-research/technical-feasibility-report.md` — 기술 실현 가능성 통합 보고서
- `docs/00-research/research-libghostty-vt-windows.md` — libghostty-vt 상세 리서치
- libghostty-vt API 문서: libghostty.tip.ghostty.org
- ghostling 레퍼런스 구현: github.com/ghostty-org/ghostling
- **[리서치 발견]** ghostty-windows 포크: github.com/InsipidPoint/ghostty-windows

---

## 2. Scope

### 2.1 In Scope

- [ ] Zig 0.15.2 설치 및 Windows 환경 구성
- [ ] ghostty 소스 클론 및 libghostty-vt Windows .lib 빌드 (`-Dtarget=x86_64-windows-msvc`)
- [ ] ~~vcpkg를 통한 libxml2 사전 설치~~ **[리서치 업데이트]** PR #11698로 해결 완료 — libxml2 불필요
- [ ] C API 호출 테스트 프로그램 작성 (핵심 6개 함수 검증)
- [ ] VtCore 래퍼 레이어 인터페이스 초안 설계
- [ ] 빌드 결과 및 호환성 문서화
- [ ] **[추가]** ghostty-windows 포크의 빌드 설정을 레퍼런스로 분석

### 2.2 Out of Scope

- ConPTY 연동 (Phase 2)
- DirectX 11 렌더링 (Phase 3)
- WinUI3 UI 구현 (Phase 4)
- 성능 벤치마크 (Phase 5에서 수행)
- CI/CD 파이프라인 구축
- **[추가]** `ghostty_render_state_update`의 증분 렌더 상태와 D3D11 dirty row 연동 (Phase 2~3)

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Status | Research Note |
|----|-------------|----------|--------|---------------|
| FR-01 | Zig 0.15.2로 libghostty-vt를 Windows x64 .lib로 빌드 | High | Pending | ghostty-windows에서 검증됨 |
| FR-02 | C/C++ 프로젝트에서 .lib를 링크하여 C API 호출 | High | Pending | `-target x86_64-windows-msvc` 필요 |
| FR-03 | VT 파서 초기화 + ANSI escape sequence 파싱 | High | Pending | 핵심 6개 함수로 검증 |
| FR-04 | VtCore 래퍼 인터페이스로 API 격리 | Medium | Pending | API 변경 범위를 파일 1개로 제한 |
| FR-05 | 빌드 절차 자동화 (빌드 스크립트) | Medium | Pending | CMake + zig build 연동 |
| FR-06 | **[추가]** OSC 시퀀스 파싱 콜백 동작 확인 | Medium | Pending | OSC 9/777/99 — Phase 4 AI UX 기반 |
| FR-07 | **[추가]** 증분 렌더 상태 업데이트 API 동작 확인 | Medium | Pending | Phase 3 D3D11 연동 기반 |

### 3.2 C API 핵심 함수 목록 (리서치에서 확인)

ghostling `main.c` 및 ghostty-windows 포크에서 확인된 핵심 C API:

| # | Function | Purpose | 검증 필수 |
|---|----------|---------|:---------:|
| 1 | `ghostty_terminal_new` | 터미널 인스턴스 생성 | **필수** |
| 2 | `ghostty_terminal_vt_write` | VT 데이터 입력 (ConPTY 출력 → 파서) | **필수** |
| 3 | `ghostty_render_state_update` | 증분 렌더 상태 조회 | **필수** |
| 4 | `ghostty_terminal_destroy` | 터미널 인스턴스 해제 | **필수** |
| 5 | `ghostty_terminal_input` | 키 입력 전달 | 권장 |
| 6 | `ghostty_terminal_resize` | 터미널 크기 변경 | 권장 |

### 3.3 C API 헤더 구조 (리서치에서 확인)

`include/ghostty/vt.h` — 20개 서브헤더의 umbrella include:

| 서브헤더 | 역할 | Phase 1 관련 |
|---------|------|:----------:|
| `terminal.h` | 터미널 에뮬레이터 상태 (핵심) | **핵심** |
| `render.h` | 증분 렌더 상태 업데이트 | **핵심** |
| `formatter.h` | 내용을 텍스트/VT/HTML로 포맷 | 참고 |
| 기타 17개 | 이벤트, 설정, 유틸리티 등 | Phase 2+ |

### 3.4 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| 빌드 호환성 | Windows 10/11 x64, MSVC 2022 + Zig 0.15.2 | CI 빌드 성공 |
| 빌드 시간 | 클린 빌드 5분 이내 | 빌드 로그 타임스탬프 |
| 바이너리 크기 | .lib 정적 라이브러리 10MB 이내 | 파일 크기 측정 |
| API 안정성 | VtCore 래퍼로 API 변경 영향 격리 | 래퍼 계층 존재 여부 |
| **[추가]** ABI 호환성 | C ABI(`extern "C"`)만 사용, C++ ABI 혼용 금지 | 링크 테스트 |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] `libghostty-vt.lib` Windows x64 정적 라이브러리 빌드 성공
- [ ] C API 테스트 프로그램이 VT 파서를 초기화하고 `\x1b[31mHello\x1b[0m` 파싱 성공
- [ ] **[변경]** 핵심 4개 함수 (`new`, `vt_write`, `render_state_update`, `destroy`) 호출 검증
- [ ] VtCore 래퍼 헤더(.h) 초안 작성 완료
- [ ] 빌드 절차 문서화 (README 또는 빌드 스크립트)
- [ ] 알려진 리스크와 워크어라운드 문서화

### 4.2 Quality Criteria

- [ ] 빌드 경고(warning) 0개 (또는 libghostty-vt 내부 경고만 허용)
- [ ] 메모리 누수 없음 (테스트 프로그램에서 init/destroy 사이클 검증)
- [ ] C API 호출 시 crash 없음
- [ ] **[추가]** MSVC Release/Debug 두 구성 모두 링크 성공

---

## 5. Risks and Mitigation

### 5.1 리스크 매트릭스 (리서치 반영)

| # | Risk | Impact | Likelihood | Mitigation | Research Status |
|---|------|--------|------------|------------|:---------------:|
| R1 | libghostty-vt API 불안정 (공식 태그 없음) | High | High | VtCore 래퍼 격리 (파일 1개로 변경 범위 제한) | **확인 — 대응책 검증됨** |
| R2 | ~~libxml2 빌드 의존성~~ | ~~High~~ | ~~Medium~~ | ~~vcpkg 사전 설치~~ | **해결됨 (PR #11698)** |
| R3 | Zig 0.15.x 빌드 시스템 변경 | Medium | Medium | Zig 0.15.2 버전 고정, `.zig-version` 파일 | 리서치 확인 |
| R4 | MSVC ↔ Zig ABI 호환성 | High | Low | C ABI만 사용, `-target x86_64-windows-msvc` 명시 | **리서치에서 조건 확인** |
| R5 | Windows SIMD 미동작 | Medium | Low | Ghostty Windows CI에서 검증됨, fallback 존재 | **리서치 확인** |
| R6 | **[추가]** 커밋 해시 기반 버전 고정 시 업스트림 변경 추적 부담 | Medium | Medium | 분기별 업데이트 + VtCore 래퍼 테스트로 검증 |

### 5.2 해소된 리스크

| 리스크 | 해소 근거 |
|--------|----------|
| Windows에서 빌드 자체가 불가능할 가능성 | ghostty-windows 포크에서 실제 빌드+동작 확인 |
| libxml2/fontconfig 의존성 | PR #11698로 Windows에서 fontconfig 조건부 제외 |
| Zig→MSVC 링킹 불가능 | C ABI는 호환, `-target x86_64-windows-msvc`로 해결 |

---

## 6. Architecture Considerations

### 6.1 Project Level Selection

| Level | Characteristics | Recommended For | Selected |
|-------|-----------------|-----------------|:--------:|
| **Starter** | 간단한 구조 | 정적 사이트 | ☐ |
| **Dynamic** | 기능 기반 모듈 | 웹앱, SaaS MVP | ☐ |
| **Enterprise** | 엄격한 레이어 분리, DI | 대규모 시스템 | ☐ |
| **Custom (Native)** | C++/Zig 네이티브 레이어드 아키텍처 | Windows 데스크톱 앱 | ☑ |

> bkit 레벨 분류에 정확히 해당하지 않으므로 Custom(Native)으로 분류.
> PDCA 워크플로우와 범용 에이전트만 활용.

### 6.2 Key Architectural Decisions (리서치 반영)

| Decision | Options | Selected | Rationale |
|----------|---------|----------|-----------|
| VT 파서 | libghostty-vt / libvterm / 자체 구현 | libghostty-vt | ghostty-windows 포크에서 실제 동작 확인, MIT |
| 빌드 타겟 | `x86_64-windows-msvc` / `x86_64-windows-gnu` | **x86_64-windows-gnu** | **[ADR-001]** MSVC 타겟은 CRT 초기화 문제, GNU는 Zig 내장 libc |
| SIMD | 활성화 / 비활성화 | **-Dsimd=false** | **[ADR-001]** CRT 독립, C++ 의존성 제거, 공식 예제와 동일 |
| 빌드 시스템 | CMake + Zig / Zig 단독 / MSBuild | CMake(Ninja) + Zig | C/C++ 프로젝트는 CMake, Zig는 libghostty-vt 빌드용 |
| 링킹 방식 | 정적(.lib) / 동적(.dll) | **정적(.lib)** | **[ADR-003]** DLL은 CRT 미초기화 crash, static lib은 안전 |
| API 격리 | 직접 호출 / C 브릿지 / C++ 래퍼 | **C 브릿지 + C++ 래퍼** | **[ADR-002]** MSVC C++ typedef 충돌 회피, 정석 FFI 패턴 |
| 버전 고정 | git tag / commit hash | **commit hash** | 공식 태그 없으므로 해시로 고정 |

### 6.3 4-스레드 모델과의 연결 (리서치 교차 검증)

```
Phase 1에서 검증할 경로:
─────────────────────────────────────────────────
  [ConPTY 출력] → ghostty_terminal_vt_write() → [파싱 스레드]
                                                      │
                                    ghostty_render_state_update()
                                                      │
                                                      ▼
                                              [증분 렌더 상태]
                                    (Phase 3에서 D3D11 dirty row와 연결)
─────────────────────────────────────────────────
```

교차 검증 결과:
- libghostty-vt의 증분 렌더 상태 → AtlasEngine 패턴의 dirty row 추적과 자연스럽게 연결
- ConPTY 동기 파이프 → 전용 I/O 스레드 → lock-free 큐 → 파싱 스레드 모델 호환

### 6.4 폴더 구조 (Phase 1 기준)

```
ghostwin/
├── docs/
│   ├── 00-research/                # 기술 리서치 문서 (6개)
│   ├── 01-plan/features/           # Plan 문서
│   └── 02-design/features/         # Design 문서
├── external/
│   └── ghostty/                    # ghostty 소스 (서브모듈, 커밋 해시 고정)
├── src/
│   └── vt-core/
│       ├── vt_core.h               # VtCore 래퍼 인터페이스
│       └── vt_core.cpp             # VtCore 래퍼 구현
├── tests/
│   └── vt_core_test.cpp            # C API 핵심 4함수 호출 테스트
├── scripts/
│   └── build_libghostty.bat        # libghostty-vt 빌드 스크립트
├── CMakeLists.txt
├── .zig-version                    # Zig 버전 고정 (0.15.2)
├── onboarding.md
└── .gitignore
```

---

## 7. Convention Prerequisites

### 7.1 코딩 컨벤션 (Phase 1 최소 정의)

| Category | Rule | Priority |
|----------|------|:--------:|
| **네이밍** | C++ — PascalCase(클래스), snake_case(함수/변수), UPPER_SNAKE(상수) | High |
| **네이밍** | 파일명 — snake_case.cpp/.h | High |
| **래퍼 패턴** | libghostty-vt API 직접 노출 금지, VtCore 래퍼를 통해서만 접근 | High |
| **ABI 규칙** | **[추가]** C ABI(`extern "C"`)만 사용, C++ 심볼 혼용 금지 | High |
| **에러 처리** | C API 반환값 체크 필수, 실패 시 로그 출력 | Medium |
| **빌드** | Debug/Release 두 구성 모두 빌드 성공 필수 | Medium |

### 7.2 환경 변수 / 빌드 요구사항 (리서치 반영)

| Item | Version/Value | Purpose | Research Note |
|------|---------------|---------|---------------|
| Zig | **0.15.2** (고정) | libghostty-vt 빌드 | ghostty-windows에서 검증된 버전 |
| MSVC | **14.51.36014** (VS 2026 Community, `-vcvars_ver=14.51`) | C/C++20 컴파일러 | 14.50은 동적 CRT 누락 |
| CMake | 4.0+ | 빌드 시스템 | |
| Ninja | 설치됨 | CMake 빌드 백엔드 | |
| Windows SDK | **10.0.22621.0** (include/lib) | Windows API | 26100은 ucrt include 누락 |
| Zig 빌드 플래그 | **`-Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false`** | CRT 독립 static lib 빌드 | **[ADR-001]** |

---

## 8. Implementation Guide (리서치 기반 구체화)

### 8.1 빌드 순서

```bash
# 1. Zig 설치
scoop install zig@0.15.2   # 또는 공식 바이너리 다운로드

# 2. ghostty 소스 클론 (커밋 해시 고정)
git clone https://github.com/ghostty-org/ghostty external/ghostty
cd external/ghostty
git checkout <target-commit-hash>

# 3. libghostty-vt 빌드
zig build -Dapp-runtime=win32 -Dtarget=x86_64-windows-msvc

# 4. .lib 파일 확인
ls zig-out/lib/  # libghostty-vt.lib 존재 확인

# 5. CMake 프로젝트 빌드 (테스트)
cd ../..
cmake -B build -G "Visual Studio 17 2022"
cmake --build build --config Release
```

### 8.2 핵심 레퍼런스

| 자료 | 용도 | 우선순위 |
|------|------|:--------:|
| InsipidPoint/ghostty-windows | 빌드 설정 + Win32 런타임 레퍼런스 | **최우선** |
| ghostty-org/ghostling/main.c | C API 사용 패턴 (6개 핵심 함수) | **최우선** |
| ghostty CI `build-libghostty-vt` | 빌드 매트릭스 + 플래그 확인 | 참고 |
| include/ghostty/vt.h | C API 헤더 구조 (20개 서브헤더) | 참고 |

---

## 9. Next Steps

1. [ ] Design 문서 작성 (`libghostty-vt-build.design.md`)
2. [ ] Zig 0.15.2 설치 및 ghostty 소스 준비
3. [ ] ghostty-windows 포크의 빌드 설정 분석
4. [ ] Windows 빌드 테스트 → 구현 시작

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-03-28 | Initial draft | Solit |
| 0.2 | 2026-03-28 | 리서치 결과 반영 | Solit |
| **0.3** | **2026-03-29** | **구현 완료 반영: windows-gnu+simd=false 전환 (ADR-001), C 브릿지 패턴 (ADR-002), DLL→static 회귀 (ADR-003), MSVC 14.51, 7/7 테스트 PASS** | **Solit** |
