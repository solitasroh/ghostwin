# conpty-integration Planning Document

> **Summary**: ConPTY 세션을 생성하고 전용 I/O 스레드로 자식 프로세스 출력을 수신하여 VtCore에 연결한다
>
> **Project**: GhostWin Terminal
> **Version**: 0.1.0
> **Author**: Solit
> **Date**: 2026-03-29
> **Status**: Draft

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | Phase 1에서 빌드 검증된 libghostty-vt(VtCore)가 실제 ConPTY 출력을 수신하여 파싱할 수 있는 경로가 아직 구현되지 않았다 |
| **Solution** | Windows ConPTY API로 의사 콘솔 세션을 생성하고, 전용 I/O 스레드에서 동기 ReadFile 루프로 출력을 수신하여 VtCore.write()에 전달하는 파이프라인을 구축한다 |
| **Function/UX Effect** | cmd.exe/pwsh.exe 등 자식 프로세스의 VT 출력이 libghostty-vt 파서를 통해 구조화된 터미널 상태로 변환된다 |
| **Core Value** | GhostWin 4-스레드 모델(ConPTY I/O → VT 파싱 → 렌더 → UI)의 앞 2단계를 실현하여 터미널로서의 핵심 데이터 흐름을 확립한다 |

### Research Confidence: High

> ConPTY는 Windows 10 1809 이상에서 안정적으로 지원되며, 공식 문서와 Windows Terminal 샘플이 충분하다.
> 대안 기술(WinPTY, Raw Console API, SSH 루프백) 비교 완료 — ConPTY가 libghostty-vt 아키텍처에 유일하게 적합.
> 상세: `docs/00-research/conpty-research.md` (Section 1~6: API 상세, Section 7: 대안 비교)

---

## 1. Overview

### 1.1 Purpose

Phase 1에서 검증된 VtCore(libghostty-vt C API 래퍼)에 실제 ConPTY 출력을 공급하는 파이프라인을 구현한다. 이 단계가 완료되면 GhostWin은 자식 프로세스(cmd.exe, pwsh.exe 등)를 실행하고 그 출력을 구조화된 터미널 상태로 변환할 수 있게 된다.

### 1.2 Background

- Phase 1(libghostty-vt-build)에서 libghostty-vt Windows 빌드 + C API 호출 검증 완료 (7/7 PASS, 96% match)
- ConPTY(Windows Pseudo Console)는 Windows 10 1809 이상에서 지원되는 공식 의사 콘솔 API
- 입출력은 UTF-8 + VT 시퀀스 — libghostty-vt와 직접 호환
- GhostWin 4-스레드 모델: I/O 스레드 → 파싱 스레드 → 렌더 스레드 → UI 스레드
- 본 Phase에서는 I/O 스레드 + 파싱(VtCore) 연결까지 구현

### 1.3 Related Documents

- `docs/00-research/conpty-research.md` — ConPTY API 심층 리서치
- `docs/archive/2026-03/libghostty-vt-build/libghostty-vt-build.report.md` — Phase 1 완료 보고서
- `docs/00-research/ghostty-upstream-sync-analysis.md` — upstream 동기화 분석
- `docs/adr/001-simd-false-gnu-target.md` — windows-gnu + simd=false 결정
- `docs/adr/002-c-bridge-pattern.md` — C 브릿지 패턴 결정
- `docs/adr/003-dll-dynamic-crt.md` — DLL 방식 결정

---

## 2. Scope

### 2.1 In Scope

- [ ] ghostty 서브모듈 upstream 동기화 (C++ 헤더 호환 #11950 반영)
- [ ] ConPTY 세션 관리 클래스 (`ConPtySession`) 구현
  - CreatePipe 2쌍 + CreatePseudoConsole + CreateProcess
  - 자식 프로세스(cmd.exe, pwsh.exe) 실행
  - ClosePseudoConsole + 정리(teardown) 순서 준수
- [ ] 전용 I/O 스레드 — 동기 ReadFile 루프로 outputReadSide 수신
- [ ] I/O 스레드 → VtCore.write() 연결 (SPSC 큐 또는 직접 호출)
- [ ] 키보드 입력 → inputWriteSide WriteFile 기본 경로
- [ ] ResizePseudoConsole 연동 (VtCore.resize()와 동기화)
- [ ] Ctrl+C 시그널 전달 (0x03 바이트 전송)
- [ ] 정리(shutdown) 시 교착 상태 방지 패턴 구현
- [ ] 콘솔 테스트 프로그램 — ConPTY + VtCore 통합 동작 검증

### 2.2 Out of Scope

- 한국어 IME / TSF 통합 (Phase 3 이후)
- WSL 통합 (Phase 3 이후)
- DirectX 11 렌더링 연동 (Phase 3)
- WinUI3 UI 프레임워크 (Phase 4)
- Named Pipe + IOCP 비동기 패턴 (다중 pane 최적화, 추후)
- 환경 변수 커스텀 관리 UI
- 탭/분할 pane 관리

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | Priority | Research Note |
|----|-------------|----------|---------------|
| FR-01 | ConPTY 세션 생성 (CreatePseudoConsole) | High | conpty-research.md Section 1.2 |
| FR-02 | 자식 프로세스 생성 (cmd.exe/pwsh.exe) | High | PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE |
| FR-03 | 전용 I/O 스레드에서 동기 ReadFile 루프 | High | conpty-research.md Section 2.2 |
| FR-04 | I/O 출력 → VtCore.write() 전달 | High | Phase 1 VtCore API 검증 완료 |
| FR-05 | 키보드 입력 → inputWriteSide WriteFile | High | UTF-8 바이트 전송 |
| FR-06 | ResizePseudoConsole + VtCore.resize() 동기화 | Medium | conpty-research.md Section 1.3 |
| FR-07 | Ctrl+C 시그널 전달 (0x03) | Medium | conpty-research.md Section 6.3 |
| FR-08 | 정리(shutdown) 교착 상태 방지 | High | conpty-research.md Section 6.5 |
| FR-09 | 자식 프로세스 종료 감지 및 세션 정리 | Medium | ReadFile 실패 반환으로 감지 |
| FR-10 | 환경 변수 TERM=xterm-256color 설정 | Low | conpty-research.md Section 6.4 |

### 3.2 ConPTY 핵심 API 목록

| # | API | Purpose | 필수 |
|---|-----|---------|:----:|
| 1 | `CreatePipe` | 입출력 파이프 쌍 생성 | **필수** |
| 2 | `CreatePseudoConsole` | 의사 콘솔 생성 | **필수** |
| 3 | `ResizePseudoConsole` | 터미널 크기 변경 | **필수** |
| 4 | `ClosePseudoConsole` | 의사 콘솔 종료 | **필수** |
| 5 | `InitializeProcThreadAttributeList` | 속성 목록 초기화 | **필수** |
| 6 | `UpdateProcThreadAttribute` | PSEUDOCONSOLE 속성 설정 | **필수** |
| 7 | `CreateProcessW` | 자식 프로세스 생성 | **필수** |

### 3.3 VtCore 연동 API (Phase 1에서 검증 완료)

| API | Purpose | 연동 지점 |
|-----|---------|----------|
| `VtCore::create(cols, rows)` | 터미널 인스턴스 생성 | ConPTY 세션 시작 시 |
| `VtCore::write(data)` | VT 데이터 입력 | I/O 스레드 ReadFile 결과 전달 |
| `VtCore::update_render_state()` | 렌더 상태 조회 | 테스트에서 파싱 결과 확인용 |
| `VtCore::resize(cols, rows)` | 터미널 크기 변경 | ResizePseudoConsole과 동기 호출 |

### 3.4 Non-Functional Requirements

| Category | Criteria | Measurement Method |
|----------|----------|-------------------|
| 호환성 | Windows 10 1809+ (ConPTY 최소 지원 버전) | 빌드 + 실행 테스트 |
| 스레드 안전성 | I/O 스레드와 메인 스레드 간 데이터 경합 없음 | ThreadSanitizer 또는 수동 검증 |
| 교착 상태 | Shutdown 시 5초 내 완료 | WaitForSingleObject 타임아웃 |
| 읽기 버퍼 | 64KB (ConPTY 내부 파이프 버퍼와 정렬) | 코드 리뷰 |
| 빌드 시간 | 증분 빌드 30초 이내 | 빌드 로그 |

---

## 4. Success Criteria

### 4.1 Definition of Done

- [ ] `ConPtySession` 클래스가 cmd.exe를 자식 프로세스로 실행
- [ ] I/O 스레드가 cmd.exe 출력을 ReadFile로 수신
- [ ] 수신된 VT 데이터가 VtCore.write()를 통해 파싱됨
- [ ] VtCore.update_render_state()에서 dirty 상태 감지 (파싱 결과 존재 확인)
- [ ] 키보드 입력이 inputWriteSide를 통해 자식 프로세스에 전달됨
- [ ] ResizePseudoConsole 호출 시 자식 프로세스가 새 크기를 인식
- [ ] 정리(shutdown) 시 교착 상태 없이 5초 내 완료
- [ ] 자식 프로세스 종료 시 세션이 깔끔하게 정리됨

### 4.2 Quality Criteria

- [ ] 빌드 경고 0개 (libghostty-vt 내부 제외)
- [ ] 리소스 누수 없음 (핸들, 스레드, 메모리)
- [ ] I/O 스레드 종료 시 graceful shutdown 확인
- [ ] MSVC Debug/Release 두 구성 모두 빌드 및 실행 성공

---

## 5. Risks and Mitigation

### 5.1 리스크 매트릭스

| # | Risk | Impact | Likelihood | Mitigation | Research Status |
|---|------|--------|------------|------------|:---------------:|
| R1 | upstream 동기화 시 로컬 패치 충돌 | High | Medium | #11950에서 C++ 호환 수정됨 — 로컬 패치 3건 제거 가능 | **확인됨** |
| R2 | ClosePseudoConsole 교착 상태 (Win10) | High | Medium | 별도 스레드에서 호출 + 출력 파이프 계속 읽기 패턴 | **리서치 확인** |
| R3 | I/O 스레드 ↔ VtCore 스레드 안전성 | Medium | Medium | VtCore.write()를 I/O 스레드에서 직접 호출 (단일 소유자) 또는 SPSC 큐 | 설계 시 결정 |
| R4 | ConPTY 파이프 핸들 닫기 순서 오류 | High | Low | conpty-research.md의 정리 순서 엄수 | **리서치 확인** |
| R5 | 대용량 출력 시 I/O 버퍼 오버플로 | Medium | Low | 64KB 읽기 버퍼 + 파싱 스레드 분리 | 리서치 권고 |
| R6 | DLL 방식(ADR-003)과 ConPTY 런타임 호환 | Low | Low | Kernel32.dll은 Windows 기본 — 충돌 가능성 극히 낮음 | **확인됨** |

### 5.2 선결 조건

| 항목 | 상태 | 조치 |
|------|------|------|
| ghostty upstream 동기화 | **필수** | #11950 C++ 헤더 호환 반영, 로컬 패치 3건 제거 |
| Phase 1 VtCore 빌드 정상 | **완료** | 7/7 테스트 PASS |
| Windows SDK 10.0.22621.0 | **완료** | Phase 1에서 확인 |
| MSVC 14.51 | **완료** | Phase 1에서 확인 |

---

## 6. Architecture Considerations

### 6.1 Project Level Selection

| Level | Characteristics | Selected |
|-------|-----------------|:--------:|
| **Custom (Native)** | C++/Zig 네이티브 레이어드 아키텍처 | **Yes** |

> Phase 1과 동일. PDCA 워크플로우와 범용 에이전트만 활용.

### 6.2 Terminal I/O 방식 선택 근거

| 방식 | VT 호환 | libghostty-vt 호환 | 유지보수 | 채택 |
|------|:-------:|:------------------:|:--------:|:----:|
| **ConPTY** | Yes | **Yes** | 활발 (MS 공식) | **선택** |
| WinPTY | Yes (변환) | Yes | 중단 (2019~) | 제외 |
| Raw Console API | **No** | **No** | 레거시 | 제외 |
| SSH 루프백 | Yes | Yes | 활발 | 제외 (과도) |

**ConPTY 선택 이유**: libghostty-vt가 VT 시퀀스를 입력으로 받으므로 VT 출력을 제공하는 ConPTY가 자연스러운 매칭. VS Code, Alacritty, Hyper, WezTerm 등 주요 터미널이 ConPTY 채택 완료. GhostWin 타겟(Win10 1809+)에서 100% 사용 가능.

> 상세 비교: `docs/00-research/conpty-research.md` Section 7

### 6.3 Key Architectural Decisions

| Decision | Options | Selected | Rationale |
|----------|---------|----------|-----------|
| I/O 패턴 | 전용 스레드 동기 ReadFile / Named Pipe + IOCP | **전용 스레드 동기 ReadFile** | 단일 ConPTY 인스턴스에서 충분, 단순성 우선 |
| I/O ↔ VtCore 연결 | 직접 호출 / SPSC 큐 | **설계 시 결정** | 직접 호출이 단순하나 스레드 안전성 검토 필요 |
| 읽기 버퍼 크기 | 4KB / 64KB / 256KB | **64KB** | ConPTY 내부 파이프 버퍼와 정렬 (리서치 권고) |
| 자식 프로세스 기본값 | cmd.exe / pwsh.exe / 환경 감지 | **pwsh.exe (fallback: cmd.exe)** | PowerShell이 현대적 Windows 기본 셸 |
| 정리 순서 | 동기 / 비동기 | **비동기 (별도 스레드)** | Win10 교착 상태 방지 |

### 6.4 데이터 흐름 (Phase 2 구현 범위)

```
                     Phase 2 구현 범위
┌─────────────────────────────────────────────────────┐
│                                                     │
│  [ConPTY]                                           │
│     │ CreatePseudoConsole                            │
│     │ CreateProcess(cmd.exe/pwsh.exe)                │
│     ▼                                               │
│  outputReadSide ──ReadFile──► [I/O 스레드]           │
│                                    │                │
│                              VtCore.write(buf)      │
│                                    │                │
│                                    ▼                │
│                              [VtCore 파싱]           │
│                                    │                │
│                       update_render_state()          │
│                                    │                │
│                                    ▼                │
│                            [RenderInfo]              │
│                          (Phase 3에서 D3D11 연결)    │
│                                                     │
│  [키보드 입력] ──WriteFile──► inputWriteSide          │
│                                    │                │
│                              [ConPTY → 자식]         │
│                                                     │
│  [리사이즈] ──► ResizePseudoConsole + VtCore.resize() │
│                                                     │
└─────────────────────────────────────────────────────┘
```

### 6.5 폴더 구조 (Phase 2 추가분)

```
ghostwin/
├── src/
│   ├── vt-core/                    # Phase 1 (기존)
│   │   ├── vt_bridge.h
│   │   ├── vt_bridge.c
│   │   ├── vt_core.h
│   │   └── vt_core.cpp
│   └── conpty/                     # Phase 2 (신규)
│       ├── conpty_session.h        # ConPTY 세션 관리 인터페이스
│       └── conpty_session.cpp      # 세션 생성/I/O 스레드/정리 구현
├── tests/
│   ├── vt_core_test.cpp            # Phase 1 (기존)
│   └── conpty_integration_test.cpp # Phase 2 (신규) — ConPTY + VtCore 통합 테스트
├── CMakeLists.txt                  # conpty 소스 추가
└── ...
```

### 6.6 ConPtySession 클래스 초안

```cpp
namespace ghostwin {

class ConPtySession {
public:
    struct Config {
        uint16_t cols = 80;
        uint16_t rows = 24;
        std::wstring shell_path;       // 빈 문자열이면 자동 감지
        std::wstring initial_dir;      // 빈 문자열이면 현재 디렉토리
    };

    static std::unique_ptr<ConPtySession> create(const Config& config);
    ~ConPtySession();

    // 키보드 입력 전송
    bool send_input(std::span<const uint8_t> data);

    // 리사이즈
    bool resize(uint16_t cols, uint16_t rows);

    // 자식 프로세스 생존 확인
    bool is_alive() const;

    // VtCore 접근 (읽기 전용)
    const VtCore& vt_core() const;
    VtCore& vt_core();

private:
    ConPtySession();
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace ghostwin
```

---

## 7. Convention Prerequisites

### 7.1 코딩 컨벤션 (Phase 1 계승 + Phase 2 추가)

| Category | Rule | Priority |
|----------|------|:--------:|
| **네이밍** | C++ — PascalCase(클래스), snake_case(함수/변수), UPPER_SNAKE(상수) | High |
| **네이밍** | 파일명 — snake_case.cpp/.h | High |
| **래퍼 패턴** | ConPTY API 직접 노출 금지, ConPtySession을 통해서만 접근 | High |
| **핸들 관리** | RAII 패턴 필수 — 소멸자에서 모든 핸들 해제 | High |
| **스레드 안전** | I/O 스레드 외부에서 파이프 핸들 직접 접근 금지 | High |
| **에러 처리** | HRESULT/BOOL 반환값 체크 필수 | Medium |
| **정리 순서** | conpty-research.md Section 3.4 순서 엄수 | High |

### 7.2 환경 변수 / 빌드 요구사항

| Item | Version/Value | Purpose | Note |
|------|---------------|---------|------|
| Zig | 0.15.2 (고정) | libghostty-vt 빌드 | Phase 1 동일 |
| MSVC | 14.51 | C/C++ 컴파일러 | Phase 1 동일 |
| CMake | 4.0+ | 빌드 시스템 | Phase 1 동일 |
| Windows SDK | 10.0.22621.0 | ConPTY API (Kernel32) | Phase 1 동일 |
| 최소 타겟 OS | Windows 10 1809 | ConPTY 최초 지원 버전 | 신규 |

---

## 8. Implementation Guide

### 8.1 구현 순서

```
1. ghostty upstream 동기화 (#11950 반영, 로컬 패치 제거)
2. Phase 1 빌드 재검증 (upstream 동기화 후 7/7 테스트 재확인)
3. ConPtySession 클래스 골격 구현
   - CreatePipe 2쌍 + CreatePseudoConsole
   - STARTUPINFOEX 구성 + CreateProcessW
   - 핸들 RAII 관리
4. I/O 스레드 구현 (동기 ReadFile 루프, 64KB 버퍼)
5. I/O 스레드 → VtCore.write() 연결
6. 키보드 입력 → inputWriteSide 경로 구현
7. ResizePseudoConsole + VtCore.resize() 연동
8. Shutdown 교착 상태 방지 패턴 구현
9. 통합 테스트 작성 (cmd.exe 실행 → 출력 수신 → RenderInfo 확인)
```

### 8.2 핵심 레퍼런스

| 자료 | 용도 | 우선순위 |
|------|------|:--------:|
| `docs/00-research/conpty-research.md` | ConPTY API 상세 + 교착 상태 패턴 | **최우선** |
| Windows Terminal EchoCon 샘플 | 초기화 패턴 레퍼런스 | **최우선** |
| Windows Terminal MiniTerm 샘플 | 양방향 I/O 패턴 | 참고 |
| `src/vt-core/vt_core.h` | VtCore 연동 인터페이스 | **최우선** |
| ghostty-upstream-sync-analysis.md | upstream 동기화 체크리스트 | 선결 조건 |

---

## 9. Next Steps

1. [ ] Design 문서 작성 (`conpty-integration.design.md`)
2. [ ] ghostty upstream 동기화 실행
3. [ ] Phase 1 빌드 재검증
4. [ ] ConPtySession 구현 시작

---

## Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-03-29 | Initial draft — ConPTY 리서치 기반 | Solit |
