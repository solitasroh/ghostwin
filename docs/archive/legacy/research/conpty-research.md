# ConPTY (Windows Pseudo Console) 심층 리서치

> GhostWin Terminal 프로젝트 — 기술 리서치 문서
> 작성일: 2026-03-28
> 상태: 초안

---

## 목차

1. [ConPTY API 상세 분석](#1-conpty-api-상세-분석)
2. [비동기 IO 패턴](#2-비동기-io-패턴)
3. [Windows Terminal ConPTY 샘플 분석](#3-windows-terminal-conpty-샘플-분석)
4. [WSL 통합](#4-wsl-통합)
5. [한국어 IME 처리](#5-한국어-ime-처리)
6. [리스크 및 알려진 이슈](#6-리스크-및-알려진-이슈)
7. [대안 기술 비교](#7-대안-기술-비교)
8. [GhostWin 구현 권고사항](#8-ghostwin-구현-권고사항)

---

## 1. ConPTY API 상세 분석

### 1.1 개요 및 최소 요구사항

| 항목 | 값 | 확인 여부 |
|------|-----|-----------|
| 최소 클라이언트 OS | Windows 10 October 2018 Update (version 1809) | **확인됨** |
| 최소 서버 OS | Windows Server 2019 | **확인됨** |
| 헤더 파일 | `ConsoleApi.h` (via `WinCon.h`, `Windows.h` include) | **확인됨** |
| 라이브러리 | `Kernel32.lib` | **확인됨** |
| DLL | `Kernel32.dll` | **확인됨** |

> 출처: [learn.microsoft.com/windows/console/createpseudoconsole](https://learn.microsoft.com/en-us/windows/console/createpseudoconsole)

**실현 가능성: 상** — Windows 10 1809 이상 타겟이면 무조건 사용 가능. 런타임 감지(LoadLibrary + GetProcAddress)는 불필요.

---

### 1.2 CreatePseudoConsole

```c
HRESULT WINAPI CreatePseudoConsole(
    _In_  COORD   size,     // 초기 버퍼 크기 (문자 단위 열×행)
    _In_  HANDLE  hInput,   // 입력 스트림 핸들 (동기 I/O만 허용)
    _In_  HANDLE  hOutput,  // 출력 스트림 핸들 (동기 I/O만 허용)
    _In_  DWORD   dwFlags,  // 0 또는 PSEUDOCONSOLE_INHERIT_CURSOR(1)
    _Out_ HPCON*  phPC      // 생성된 의사 콘솔 핸들 수신
);
```

#### 파라미터 세부 설명

- **size**: `COORD` 구조체 (`SHORT X, SHORT Y`). 터미널 표시 창의 문자 단위 크기. 자식 프로세스가 `GetConsoleScreenBufferInfoEx` 등 호출 시 이 값이 반환됨.
- **hInput**: `CreatePipe`로 생성한 파이프의 읽기 핸들 (inputReadSide). **반드시 동기 I/O** 핸들이어야 함. OVERLAPPED 핸들 불가.
- **hOutput**: `CreatePipe`로 생성한 파이프의 쓰기 핸들 (outputWriteSide). **반드시 동기 I/O** 핸들.
- **dwFlags**:
  - `0`: 표준 의사 콘솔 생성
  - `PSEUDOCONSOLE_INHERIT_CURSOR` (0x01): 호출자의 현재 커서 위치 상속. 고급 시나리오 전용. 이 플래그 사용 시 반드시 별도 스레드에서 커서 상속 쿼리 메시지에 응답해야 함 (미응답 시 교착 상태).
- **phPC**: 생성된 `HPCON` 핸들 수신 포인터. `ClosePseudoConsole`로 반드시 해제.

#### 반환값

- `S_OK`: 성공
- `HRESULT` 에러 코드: 실패

#### 핵심 특징

- 입출력 스트림은 **UTF-8 인코딩** 텍스트 + VT 시퀀스
- 출력 스트림: VT 시퀀스로 인코딩된 텍스트 → 터미널이 디코딩하여 렌더링
- 입력 스트림: 평문 UTF-8 키보드 입력 + VT 시퀀스로 인코딩된 제어 키/마우스 이벤트

> 출처: [learn.microsoft.com/windows/console/createpseudoconsole](https://learn.microsoft.com/en-us/windows/console/createpseudoconsole)

---

### 1.3 ResizePseudoConsole

```c
HRESULT WINAPI ResizePseudoConsole(
    _In_ HPCON hPC,    // CreatePseudoConsole에서 받은 핸들
    _In_ COORD size    // 새로운 버퍼 크기 (문자 단위)
);
```

#### 동작 방식

- 내부 버퍼 크기를 변경하여 `GetConsoleScreenBufferInfo` 계열 함수가 올바른 크기를 반환하도록 함
- 자식 프로세스에 `SIGWINCH`에 해당하는 이벤트 전달
- 반환값: `S_OK` (성공) 또는 HRESULT 에러

#### 사용 예시

```c
void OnWindowResize(int newWidth, int newHeight)
{
    COORD size;
    size.X = (SHORT)newWidth;
    size.Y = (SHORT)newHeight;
    ResizePseudoConsole(m_hPC, size);
}
```

> 출처: [learn.microsoft.com/windows/console/resizepseudoconsole](https://learn.microsoft.com/en-us/windows/console/resizepseudoconsole)

---

### 1.4 ClosePseudoConsole

```c
void WINAPI ClosePseudoConsole(
    _In_ HPCON hPC    // 닫을 의사 콘솔 핸들
);
```

#### 동작 방식

- 연결된 모든 자식 프로세스에 `CTRL_CLOSE_EVENT` 전송
- 자식 프로세스들이 연결 해제될 때까지 출력을 계속 쓸 수 있음

#### 중요한 버전별 동작 차이 (확인됨)

| Windows 버전 | 동작 |
|-------------|------|
| Windows 11 24H2 (build 26100) 이상 | **즉시 반환** (교착 상태 방지 개선) |
| 이전 버전 | 모든 클라이언트가 연결 해제될 때까지 **무기한 대기** |

#### 교착 상태 회피 패턴

이전 Windows 버전에서 교착 상태를 피하려면:

1. `ClosePseudoConsole`을 출력 파이프를 읽는 스레드와 **다른 스레드**에서 호출
2. 또는 `ClosePseudoConsole` 호출 전에 출력 파이프를 먼저 닫기
3. `ClosePseudoConsole` 호출 후에도 출력 파이프가 닫힐 때까지 계속 읽기

> 출처: [learn.microsoft.com/windows/console/closepseudoconsole](https://learn.microsoft.com/en-us/windows/console/closepseudoconsole)

---

### 1.5 HPCON 핸들 관리

```c
// HPCON은 불투명(opaque) 핸들 타입
// <consoleapi.h>에 정의됨
typedef VOID* HPCON;
```

**핸들 수명 주기:**

1. `CreatePseudoConsole` → HPCON 생성
2. 자식 프로세스에 `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` 속성으로 전달
3. `CreateProcess` 완료 후 → ConPTY에 제공했던 파이프 핸들(inputReadSide, outputWriteSide) 닫기
4. 세션 종료 시 → `ClosePseudoConsole(hPC)` 호출

---

### 1.6 파이프 설정 (CreatePipe)

```c
BOOL CreatePipe(
    [out] PHANDLE               hReadPipe,       // 읽기 핸들
    [out] PHANDLE               hWritePipe,      // 쓰기 핸들
    [in]  LPSECURITY_ATTRIBUTES lpPipeAttributes, // NULL이면 상속 불가
    [in]  DWORD                 nSize            // 버퍼 크기 제안값 (0=시스템 기본값)
);
```

#### ConPTY에서의 파이프 구성

```
호스트 프로세스                    ConPTY                   자식 프로세스
─────────────────────────────────────────────────────────────────────
inputWriteSide ──write──► inputReadSide ──[변환]──► stdin of child
outputReadSide ◄──read─── outputWriteSide ◄──[변환]── stdout of child
```

```c
HANDLE inputReadSide, inputWriteSide;   // 입력 파이프
HANDLE outputReadSide, outputWriteSide; // 출력 파이프

// 파이프 2쌍 생성
CreatePipe(&inputReadSide, &inputWriteSide, NULL, 0);
CreatePipe(&outputReadSide, &outputWriteSide, NULL, 0);

// ConPTY에는 inputReadSide + outputWriteSide 제공
CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, &hPC);

// CreateProcess 완료 후 ConPTY 측 핸들 닫기 (필수!)
CloseHandle(inputReadSide);
CloseHandle(outputWriteSide);

// 호스트는 inputWriteSide(입력 전송), outputReadSide(출력 수신) 보유
```

> **주의**: CreateProcess 후 `inputReadSide`, `outputWriteSide`를 닫지 않으면
> 파이프 브레이크 감지가 되지 않아 교착 상태 가능

> 출처: [learn.microsoft.com/windows/console/creating-a-pseudoconsole-session](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)

---

### 1.7 PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE 설정

```c
HRESULT PrepareStartupInformation(HPCON hPC, STARTUPINFOEX* pSIEx)
{
    STARTUPINFOEX siEx = {};
    siEx.StartupInfo.cb = sizeof(STARTUPINFOEX);

    // 1단계: 필요한 바이트 수 계산
    size_t bytesRequired = 0;
    InitializeProcThreadAttributeList(NULL, 1, 0, &bytesRequired);

    // 2단계: 메모리 할당
    siEx.lpAttributeList =
        (PPROC_THREAD_ATTRIBUTE_LIST)HeapAlloc(GetProcessHeap(), 0, bytesRequired);

    // 3단계: 속성 목록 초기화
    InitializeProcThreadAttributeList(siEx.lpAttributeList, 1, 0, &bytesRequired);

    // 4단계: PSEUDOCONSOLE 속성 설정
    UpdateProcThreadAttribute(
        siEx.lpAttributeList,
        0,
        PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
        hPC,
        sizeof(hPC),
        NULL,
        NULL
    );

    *pSIEx = siEx;
    return S_OK;
}
```

#### CreateProcess 호출

```c
// EXTENDED_STARTUPINFO_PRESENT 플래그 필수
CreateProcessW(
    NULL,
    cmdLineMutable,
    NULL,
    NULL,
    FALSE,                                  // bInheritHandles = FALSE
    EXTENDED_STARTUPINFO_PRESENT,           // 필수 플래그
    NULL,                                   // 환경 변수 (NULL=부모 상속)
    NULL,                                   // 작업 디렉토리
    &siEx.StartupInfo,
    &pi
);
```

> **주의**: 의사 콘솔이 초기화 중 닫히면 자식 프로세스에서 `0xc0000142` 에러 다이얼로그 표시

---

## 2. 비동기 I/O 패턴

### 2.1 ConPTY와 동기/비동기 I/O의 제약

**핵심 제약사항 (확인됨):**

- `CreatePseudoConsole`에 전달하는 `hInput`, `hOutput`은 **동기 I/O 전용** 핸들이어야 함
- `OVERLAPPED` 구조체를 요구하는 비동기 핸들은 사용 불가
- 즉, **ConPTY 내부 파이프 자체는 동기 I/O**

그러나 호스트 측에서 출력을 읽는 `outputReadSide`는 **별도 스레드에서 동기 ReadFile**로 처리하거나, 또는 Named Pipe로 래핑하여 IOCP와 연결하는 패턴이 사용됨.

> 출처: [learn.microsoft.com/windows/console/creating-a-pseudoconsole-session](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)

---

### 2.2 전용 I/O 스레드 패턴 (권장)

공식 문서에서 **교착 상태 방지**를 위해 각 통신 채널을 별도 스레드로 서비스하도록 강력 권고:

```c
// 출력 읽기 스레드 (outputReadSide를 소유)
DWORD WINAPI OutputReaderThread(LPVOID param)
{
    HANDLE hOutputRead = (HANDLE)param;
    char buffer[4096];
    DWORD bytesRead;

    while (ReadFile(hOutputRead, buffer, sizeof(buffer), &bytesRead, NULL))
    {
        if (bytesRead == 0) break;
        // VT 시퀀스 파싱 큐에 버퍼 전달
        PushToVtParseQueue(buffer, bytesRead);
    }
    return 0;
}

// 입력 쓰기 함수 (inputWriteSide를 소유, UI 스레드에서 호출)
void SendInput(HANDLE hInputWrite, const char* data, size_t len)
{
    DWORD bytesWritten;
    WriteFile(hInputWrite, data, (DWORD)len, &bytesWritten, NULL);
}
```

**GhostWin 스레드 모델 적용:**

| 스레드 | 역할 | I/O 핸들 |
|--------|------|---------|
| I/O 스레드 | `outputReadSide` 동기 ReadFile 루프 | outputReadSide |
| UI 스레드 | 키 입력 → WriteFile | inputWriteSide |
| 파싱 스레드 | lock-free 큐에서 VT 데이터 소비 | - |
| 렌더 스레드 | 파싱 결과를 D3D11로 렌더링 | - |

---

### 2.3 IOCP 기반 비동기 패턴 (심화)

익명 파이프는 IOCP와 직접 연결 불가하지만, **Named Pipe로 래핑**하면 IOCP 사용 가능:

```c
// Named Pipe 생성 (OVERLAPPED 모드)
HANDLE hPipe = CreateNamedPipeW(
    L"\\\\.\\pipe\\ghostwin_output",
    PIPE_ACCESS_INBOUND | FILE_FLAG_OVERLAPPED,
    PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
    1,          // 최대 인스턴스
    65536,      // 출력 버퍼
    65536,      // 입력 버퍼
    0,          // 기본 타임아웃
    NULL
);

// IOCP에 연결
HANDLE hIOCP = CreateIoCompletionPort(hPipe, NULL, (ULONG_PTR)hPipe, 0);

// 비동기 읽기
OVERLAPPED ov = {};
char buf[65536];
DWORD bytesRead;
ReadFile(hPipe, buf, sizeof(buf), &bytesRead, &ov);
// ERROR_IO_PENDING 반환됨

// 완료 대기 (워커 스레드)
DWORD transferred;
ULONG_PTR key;
LPOVERLAPPED pOv;
GetQueuedCompletionStatus(hIOCP, &transferred, &key, &pOv, INFINITE);
// transferred만큼 buf에 데이터가 채워짐
```

**추측**: 단일 ConPTY 인스턴스에서는 전용 I/O 스레드가 더 단순. IOCP는 다중 pane(수십 개 이상) 환경에서 이점이 있음.

---

### 2.4 버퍼링 전략

| 시나리오 | 권장 버퍼 크기 | 이유 |
|---------|--------------|------|
| 일반 쉘 출력 | 4KB ~ 16KB | 대부분의 명령어 출력 |
| `ls -la` 등 대용량 | 64KB ~ 256KB | 단편화 최소화 |
| SSH 원격 세션 | 64KB | 네트워크 지연 완충 |
| AI 에이전트 스트림 | 64KB + 동적 확장 | Claude Code 출력은 연속적 대용량 가능 |

**대용량 출력 버퍼링 패턴:**

```c
// I/O 스레드: 원형 버퍼(ring buffer) 또는 lock-free SPSC 큐 사용
// 생산자: I/O 스레드 (ReadFile)
// 소비자: 파싱 스레드 (libghostty-vt 호출)

// 최소 버퍼: 65536 바이트 (64KB)
// 이유: ConPTY 내부 파이프 버퍼가 64KB 단위로 플러시
static char readBuf[65536];
DWORD bytesRead;
while (ReadFile(hOutputRead, readBuf, sizeof(readBuf), &bytesRead, NULL)) {
    spsc_queue_push(g_parseQueue, readBuf, bytesRead);
}
```

---

## 3. Windows Terminal ConPTY 샘플 분석

### 3.1 샘플 디렉토리 구조

```
github.com/microsoft/terminal/samples/ConPTY/
├── EchoCon/      — 기본 단방향 샘플 (출력만 표시)
├── MiniTerm/     — 양방향 터미널 샘플 (입력+출력)
└── GUIConsole/   — GUI 통합 샘플
```

> 출처: [github.com/microsoft/terminal/tree/main/samples/ConPTY](https://github.com/microsoft/terminal/tree/main/samples/ConPTY)

---

### 3.2 EchoCon 샘플 구조 분석

**목적**: ConPTY 기초 — 자식 프로세스 출력을 파이프로 읽어 콘솔에 표시

**초기화 패턴:**

```c
// 1. 호스트 콘솔 VT 처리 활성화
DWORD mode;
GetConsoleMode(GetStdHandle(STD_OUTPUT_HANDLE), &mode);
SetConsoleMode(GetStdHandle(STD_OUTPUT_HANDLE),
               mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);

// 2. 파이프 2쌍 생성
CreatePseudoConsoleAndPipes(&hPC, &hOutputRead, &hInputWrite);

// 3. 리스너 스레드 시작 (출력 읽기)
_beginthread(PipeListener, 0, hOutputRead);

// 4. STARTUPINFOEX 구성
InitializeStartupInfoAttachedToPseudoConsole(&siEx, hPC);

// 5. 자식 프로세스 생성 (예: ping localhost)
CreateProcessW(NULL, L"ping localhost", NULL, NULL, FALSE,
               EXTENDED_STARTUPINFO_PRESENT, NULL, NULL,
               &siEx.StartupInfo, &pi);

// 6. ConPTY 측 파이프 핸들 닫기 (필수)
CloseHandle(inputReadSide);
CloseHandle(outputWriteSide);
```

**PipeListener 스레드:**

```c
void PipeListener(LPVOID arg)
{
    HANDLE hOutputRead = (HANDLE)arg;
    HANDLE hStdOut = GetStdHandle(STD_OUTPUT_HANDLE);
    char buf[512];
    DWORD bytesRead, bytesWritten;

    // printf 대신 WriteFile 사용 — VT 시퀀스 손상 방지
    while (ReadFile(hOutputRead, buf, sizeof(buf), &bytesRead, NULL)) {
        WriteFile(hStdOut, buf, bytesRead, &bytesWritten, NULL);
    }
}
```

**정리 순서:**

1. 자식 프로세스 핸들 닫기 (`CloseHandle(pi.hProcess)`, `CloseHandle(pi.hThread)`)
2. 속성 목록 해제 (`DeleteProcThreadAttributeList`)
3. `ClosePseudoConsole(hPC)` — 자식 프로세스 자동 종료
4. 파이프 핸들 닫기 (`outputReadSide`, `inputWriteSide`)

---

### 3.3 MiniTerm 샘플 구조 (확인된 패턴)

**목적**: 양방향 대화형 터미널 — 키 입력 전달 + 출력 표시

**주요 차이점** (EchoCon 대비):
- UI 스레드에서 콘솔 키 입력 읽기 → `inputWriteSide`로 WriteFile
- 별도 스레드에서 `outputReadSide` ReadFile 루프 실행
- VT 입력 모드 활성화 (`ENABLE_VIRTUAL_TERMINAL_INPUT`)

---

### 3.4 초기화 → 읽기/쓰기 루프 → 정리 전체 패턴

```
[초기화]
1. CreatePipe × 2쌍
2. CreatePseudoConsole(size, inputReadSide, outputWriteSide, flags, &hPC)
3. InitializeProcThreadAttributeList + UpdateProcThreadAttribute(PSEUDOCONSOLE)
4. CreateProcess(..., EXTENDED_STARTUPINFO_PRESENT, ..., &siEx.StartupInfo, &pi)
5. CloseHandle(inputReadSide) + CloseHandle(outputWriteSide)  ← 반드시 닫기

[런타임]
6. [I/O 스레드] ReadFile(outputReadSide) 루프 → VT 파싱 큐
7. [UI 스레드]  WriteFile(inputWriteSide) ← 키 입력/마우스 이벤트

[리사이즈]
8. ResizePseudoConsole(hPC, newSize)

[정리]
9. ClosePseudoConsole(hPC)    ← 자식 프로세스 종료 유발
10. [I/O 스레드]에서 ReadFile이 실패 반환 → 루프 종료
11. CloseHandle(outputReadSide) + CloseHandle(inputWriteSide)
12. WaitForSingleObject(pi.hProcess, ...) ← 자식 종료 대기
13. CloseHandle(pi.hProcess) + CloseHandle(pi.hThread)
```

---

## 4. WSL 통합

### 4.1 wsl.exe 호출 방식 (ConPTY)

**확인됨**: GhostWin은 ConPTY + `wsl.exe` 호출로 WSL을 지원할 수 있음.

```c
// wsl.exe를 ConPTY 자식 프로세스로 실행
// 특정 배포판 지정:
WCHAR cmdLine[] = L"wsl.exe --distribution Ubuntu";

// 기본 배포판:
WCHAR cmdLine[] = L"wsl.exe";

// 특정 디렉토리에서 시작:
WCHAR cmdLine[] = L"wsl.exe --cd ~";

// CreateProcess에 위 cmdLine 사용 (EXTENDED_STARTUPINFO_PRESENT)
```

**wsl.exe 주요 옵션:**

| 옵션 | 설명 |
|------|------|
| `--distribution <이름>` | 특정 WSL 배포판 실행 |
| `--user <사용자>` | 특정 사용자로 실행 |
| `--cd <경로>` | 시작 디렉토리 지정 |
| `--exec <명령>` | 쉘 없이 직접 명령 실행 |

---

### 4.2 WSL1 vs WSL2 차이점 (ConPTY 관점)

| 항목 | WSL1 | WSL2 |
|------|------|------|
| 아키텍처 | 시스템 콜 변환 레이어 | Hyper-V 경량 VM (실제 Linux 커널) |
| 시스템 콜 호환성 | 부분적 | **완전** (100%) |
| 파일 시스템 성능 (Windows 파일) | **빠름** | 느림 (VM 경계 crossing) |
| 파일 시스템 성능 (Linux 파일) | 느림 | **빠름** |
| Docker 지원 | 제한적 | **완전** |
| systemd 지원 | 없음 | **있음** |
| 네트워크 | 브리지 (동일 IP) | NAT (별도 IP, 재시작마다 변경) |
| ConPTY 통합 | 동일 (`wsl.exe` 호출) | 동일 (`wsl.exe` 호출) |
| 직렬 포트 접근 | 가능 | **불가** |

**GhostWin 권고**: WSL2 기본 사용. Windows 파일 접근이 많은 경우 WSL1 선택 옵션 제공.

> 출처: [learn.microsoft.com/windows/wsl/compare-versions](https://learn.microsoft.com/en-us/windows/wsl/compare-versions)

---

### 4.3 WSL API 직접 사용 vs wsl.exe

**확인된 사실**: 공개된 WSL C API (`<wslapi.h>`)는 배포판 등록/해제 등 관리 기능만 제공하며, ConPTY와 같은 PTY 연결용 API는 공개되어 있지 않음.

**결론**: `wsl.exe`를 ConPTY 자식 프로세스로 실행하는 방식이 유일한 실용적 방법 (Windows Terminal도 동일 방식 사용).

---

## 5. 한국어 IME 처리

### 5.1 ConPTY에서의 IME 입력 흐름

**확인됨**: ConPTY 자체는 IME를 인식하지 않음. IME 처리는 **호스트 터미널 에뮬레이터의 책임**.

```
[키보드] → [IME/TSF] → [WM_IME_COMPOSITION / ITfContextOwner]
    → [호스트 터미널] → [조합 문자 표시] → [확정 시 WriteFile(inputWriteSide)]
```

IME가 확정한 UTF-8 문자열을 `inputWriteSide` 파이프에 WriteFile로 전송하면 됨.

---

### 5.2 TSF(Text Services Framework) 연동 방식

**확인됨**: Windows Terminal은 `ITfContextOwner` 인터페이스를 구현하여 TSF와 통합.

#### ITfContextOwner 주요 메서드

| 메서드 | 역할 |
|--------|------|
| `GetWnd` | TSF에 창 핸들 제공 |
| `GetScreenExt` | 텍스트 렌더링 영역의 화면 좌표 제공 |
| `GetTextExt` | 특정 문자 위치의 화면 좌표 제공 (IME 후보창 위치 결정에 사용) |
| `GetACPFromPoint` | 화면 좌표 → 문자 위치 변환 |
| `GetStatus` | 문서 상태 (읽기 전용 여부 등) 반환 |
| `GetAttribute` | TSF 속성 값 반환 |

#### TSF 초기화 (C++/WinRT 기준)

```cpp
// 1. TSF 스레드 매니저 생성
winrt::com_ptr<ITfThreadMgr> threadMgr;
CoCreateInstance(CLSID_TF_ThreadMgr, nullptr, CLSCTX_INPROC_SERVER,
                 IID_PPV_ARGS(&threadMgr));

// 2. 클라이언트 ID 획득
TfClientId clientId;
threadMgr->Activate(&clientId);

// 3. 문서 매니저 생성
winrt::com_ptr<ITfDocumentMgr> docMgr;
threadMgr->CreateDocumentMgr(&docMgr);

// 4. 컨텍스트 생성 (ITfContextOwner 구현체 연결)
winrt::com_ptr<ITfContext> context;
TfEditCookie editCookie;
docMgr->CreateContext(clientId, 0, contextOwner, &context, &editCookie);
docMgr->Push(context.get());

// 5. 입력 포커스 설정
threadMgr->SetFocus(docMgr.get());
```

#### TsfDataProvider (Windows Terminal 구현 패턴, 확인됨)

Windows Terminal `TermControl.cpp`에서 `TsfDataProvider` 클래스가:
- `GetHwnd`: 창 핸들 반환
- `GetViewport`: 뷰포트 크기 반환
- `GetCursorPosition`: 커서 위치 반환
- `HandleOutput`: 터미널 출력을 TSF에 전달

> 출처: Windows Terminal TermControl.cpp (github.com/microsoft/terminal) — 간접 확인

---

### 5.3 조합 중 문자 표시 처리

**IME 조합 문자 렌더링 패턴:**

```
[WM_IME_STARTCOMPOSITION] → 조합 시작 플래그 설정
[WM_IME_COMPOSITION]      → 조합 중 문자열 읽기 (ImmGetCompositionString)
    → 커서 위치에 underline 스타일로 표시 (조합 중임을 시각화)
    → ConPTY에는 전송하지 않음
[WM_IME_ENDCOMPOSITION]   → 조합 종료
    → 확정된 문자열 UTF-8로 인코딩 → WriteFile(inputWriteSide)
```

**중요**: 조합 중 문자를 ConPTY에 전송하면 자식 프로세스가 미완성 문자를 처리하여 화면이 깨짐. 확정 이후에만 전송.

---

### 5.4 알려진 한국어 IME 이슈 및 해결책

**확인된 이슈 목록 (github.com/microsoft/terminal):**

| 이슈 번호 | 내용 | 상태 |
|----------|------|------|
| #4963 | 한글 조합 중 문자 겹침 현상 | **해결됨** (P1 우선순위) |
| #4226 | 한글 IME 미작동 | **해결됨** (v1.0) |
| #12920 | 입력 중 밑줄 표시 | **해결됨** (2022년 4월) |
| #16537 | 한글→한자 전환 불가 | 계획하지 않음으로 종료 |

**GPU 가속 터미널에서 IME 불안정 원인 (추측 포함):**

1. **WM_IME_* 메시지와 SwapChainPanel 충돌** (추측): WinUI3의 SwapChainPanel이 HWND를 직접 소유하지 않아 IME 메시지 라우팅이 불안정할 수 있음.
2. **확인됨**: 解決策은 `HWND` 기반 TSF 통합 — `ITfContextOwner::GetWnd`가 실제 Win32 HWND를 반환하도록 구성.
3. **확인됨**: `ImmAssociateContextEx` 로 IME 컨텍스트를 명시적으로 관리.

**GhostWin 권고:**

- WinUI3 + SwapChainPanel 환경에서는 반드시 **TSF(Text Services Framework) 직접 구현** 사용
- IMM32 레거시 API 의존 금지
- 커서 위치 좌표를 TSF에 정확히 제공하여 IME 후보창이 올바른 위치에 표시되도록 함

---

## 6. 리스크 및 알려진 이슈

### 6.1 ConPTY 알려진 버그 목록

> 출처: github.com/microsoft/terminal (2026-03-28 기준 열린 이슈 138개)

| 이슈 | 내용 | 영향도 |
|------|------|--------|
| #19621 | ResizePseudoConsole 중 DSR CPR 시퀀스 인터럽션 | 중 |
| #19644 | 비활성 스크린 버퍼에 쓸 때 커서 이동 미작동 | 중 |
| #19682 | DECDHL 이중높이 문자 렌더링 오류 | 하 |
| #17580 | SetConsoleActiveScreenBuffer 시 유니코드 출력 손상 | 상 |
| #18723 | Git Bash SSH 대용량 출력 문자 손실 | 상 |

---

### 6.2 Windows 버전별 동작 차이

| Windows 버전 | 주요 차이점 |
|-------------|-----------|
| 10 1809 | ConPTY 최초 도입. 기본 기능. |
| 10 1903 | WSL2 지원 시작 (ConPTY와 직접 관계 없음) |
| 10 2004 | ConPTY 안정성 개선 |
| 11 24H2 (build 26100) | **ClosePseudoConsole 즉시 반환** — 이전 버전에서 교착 상태 유발 가능성 있었던 버그 수정 |

---

### 6.3 시그널 처리 (Ctrl+C, Ctrl+Break)

**확인됨**: ConPTY의 시그널 처리 메커니즘:

1. **Ctrl+C 처리**:
   - 터미널 에뮬레이터가 `0x03` (ASCII ETX) 바이트를 `inputWriteSide`로 WriteFile
   - ConPTY 내부의 VT Interactivity 레이어가 `0x03`을 INPUT_RECORD의 `CTRL+C` 이벤트로 변환
   - 자식 프로세스에 `CTRL_C_EVENT` 전달

2. **Ctrl+Break 처리**:
   - `0x1c` (ASCII FS) 또는 VT 시퀀스로 전달
   - `CTRL_BREAK_EVENT` 생성

3. **GenerateConsoleCtrlEvent 대안**:
   ```c
   // 터미널 에뮬레이터가 직접 Ctrl+C를 보내는 방법
   // VT 입력 모드: 0x03 바이트를 inputWriteSide에 WriteFile
   char ctrlC = 0x03;
   DWORD written;
   WriteFile(hInputWrite, &ctrlC, 1, &written, NULL);
   ```

---

### 6.4 환경 변수 전달

**확인됨**: `CreateProcess`의 `lpEnvironment` 파라미터로 전달:

```c
// NULL: 부모 프로세스 환경 상속 (일반적 경우)
CreateProcessW(NULL, cmdLine, NULL, NULL, FALSE,
               EXTENDED_STARTUPINFO_PRESENT,
               NULL,  // 환경 변수: NULL = 부모 상속
               NULL,
               &siEx.StartupInfo, &pi);

// 커스텀 환경 변수 전달
WCHAR envBlock[] =
    L"PATH=C:\\Windows\\System32\0"
    L"TERM=xterm-256color\0"  // 중요: 터미널 타입 설정
    L"\0";                     // 이중 NULL 종료

CreateProcessW(NULL, cmdLine, NULL, NULL, FALSE,
               EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
               envBlock,
               NULL,
               &siEx.StartupInfo, &pi);
```

**GhostWin 권고**: `TERM=xterm-256color` 또는 `TERM=xterm-ghostwin` 환경 변수 설정 권장.

---

### 6.5 교착 상태 시나리오 정리

다음 상황에서 교착 상태 발생 가능:

| 시나리오 | 원인 | 해결책 |
|---------|------|--------|
| `ClosePseudoConsole`을 출력 읽기 스레드와 동일 스레드에서 호출 | 최종 프레임 출력이 버퍼에 남아 차단 | 별도 스레드에서 호출 |
| `PSEUDOCONSOLE_INHERIT_CURSOR` 사용 후 커서 쿼리 미응답 | 커서 상속 핸드셰이크 미완료 | 비동기 응답 스레드 구현 |
| CreateProcess 후 `inputReadSide`, `outputWriteSide` 미닫기 | 파이프 브레이크 미감지 | CreateProcess 직후 반드시 닫기 |
| 출력 파이프 미드레인 상태로 `ClosePseudoConsole` 호출 | 출력 버퍼 가득 참 | ClosePseudoConsole 전에 파이프 닫기 또는 계속 읽기 |

---

## 7. 대안 기술 비교

### 7.1 비교 대상

Windows에서 자식 프로세스의 터미널 I/O를 호스트 애플리케이션이 수신하는 방법은 크게 4가지가 존재한다.

### 7.2 ConPTY (선택됨)

```
[자식 프로세스] → [ConPTY 내부 VT 변환] → [동기 파이프] → [호스트]
```

- Microsoft 공식 API (Windows 10 1809+, 2018년)
- 출력이 UTF-8 + VT 시퀀스로 변환되어 제공
- Windows Terminal이 사용하는 방식

| 장점 | 단점 |
|------|------|
| 공식 지원, 지속적 개선 | Windows 10 1809 미만 미지원 |
| VT 출력 → libghostty-vt 직접 호환 | 내부 VT 변환 오버헤드 (마이크로초 수준) |
| 업계 표준 (VS Code, Alacritty, Hyper 등 채택) | 알려진 버그 138건 (대부분 경미) |
| 양방향 입출력 + 리사이즈 지원 | 파이프는 동기 I/O만 허용 |

### 7.3 WinPTY (레거시)

```
[자식 프로세스] → [숨겨진 콘솔 창] → [WinPTY 화면 스크래핑] → [VT 변환] → [호스트]
```

- ConPTY 이전의 사실상 표준 (github.com/rprichard/winpty, Ryan Prichard)
- 숨겨진 conhost.exe 창을 생성하고 `ReadConsoleOutput`으로 화면을 주기적으로 스크래핑
- VS Code, Git Bash, mintty가 과거 사용 → **현재 대부분 ConPTY로 전환 완료**

| 장점 | 단점 |
|------|------|
| Windows 7/8 지원 | **화면 스크래핑** — 근본적 성능 한계 (폴링 주기에 따라 지연) |
| 오래된 실적, 안정성 검증됨 | 숨겨진 콘솔 창 생성 → 리소스 낭비 |
| | 유지보수 사실상 중단 (2019년 이후 비활성) |
| | ConPTY 대비 VT 변환 정확도 낮음 (스크래핑 한계) |
| | 전체 화면 애플리케이션(vim, htop) 렌더링 불완전 |

**VS Code의 전환 사례**: microsoft/vscode#45693에서 ConPTY 전환 논의. Windows 10 1903 이상에서 기본값으로 ConPTY 사용.

### 7.4 Raw Console API (ReadConsoleOutput)

```
[자식 프로세스] → [콘솔 버퍼] → [ReadConsoleOutput 직접 읽기] → [CHAR_INFO 배열]
```

- Win32 콘솔 화면 버퍼를 직접 읽는 저수준 방식
- VT 시퀀스가 아닌 `CHAR_INFO`(문자 + 속성 비트마스크) 구조체 배열

| 장점 | 단점 |
|------|------|
| 가장 저수준, 변환 오버헤드 없음 | **VT 시퀀스 아님** → libghostty-vt 사용 불가 |
| Windows 2000부터 지원 | 폴링 방식 — 변경 감지를 위해 주기적 버퍼 스캔 필요 |
| | 현대 VT 인식 앱(vim, htop, SSH)과 비호환 |
| | MS가 VT 기반으로 이동 중 — deprecated 방향 |
| | 16비트 속성 = 제한된 색상 (256color/TrueColor 미지원) |

### 7.5 SSH 로컬 루프백

```
[sshd 로컬 실행] → [SSH 프로토콜] → [PTY 출력] → [호스트]
```

- 로컬에 OpenSSH 서버를 실행하고 접속하는 방식
- Chrome Secure Shell(hterm)이 유사한 접근

| 장점 | 단점 |
|------|------|
| 진짜 Unix PTY (WSL 경유 시) | SSH 서버 의존성 (추가 설치/설정 필요) |
| 원격 접속과 동일 코드 재사용 가능 | 로컬 터미널에 SSH는 과도한 복잡도 |
| | 레이턴시 추가 (암호화/복호화 오버헤드) |
| | Windows 네이티브 프로그램(cmd.exe, pwsh)과 부자연스러움 |

### 7.6 비교 요약

| 기준 | ConPTY | WinPTY | Raw Console API | SSH 루프백 |
|------|:------:|:------:|:---------------:|:----------:|
| VT 시퀀스 출력 | **Yes** | Yes (변환) | **No** | Yes |
| libghostty-vt 호환 | **Yes** | Yes | **No** | Yes |
| 성능 | 우수 | 보통 | 최고 (단, 비호환) | 낮음 |
| 최소 Windows | 10 1809 | 7 | 2000 | 10 (OpenSSH) |
| 유지보수 상태 | **활발** | 중단 | 안정 (레거시) | 활발 |
| 복잡도 | 보통 | 높음 | 높음 | 매우 높음 |
| 업계 채택 | **주류** | 퇴조 | 극소수 | 극소수 |

### 7.7 선택 근거

**ConPTY 선택 이유:**

1. **아키텍처 호환성**: libghostty-vt가 VT 시퀀스를 입력으로 받으므로, VT 출력을 제공하는 ConPTY가 자연스러운 매칭
2. **업계 흐름**: VS Code, Alacritty, Hyper, WezTerm 등 주요 터미널이 ConPTY로 전환 완료
3. **공식 지원**: Microsoft가 Windows Terminal을 위해 개발하고 지속 개선 중
4. **GhostWin 타겟**: Windows 10 1809+ → ConPTY 100% 사용 가능, WinPTY 호환 레이어 불필요

**성능 참고**: ConPTY의 내부 VT 변환 오버헤드는 마이크로초 수준이며, 실제 병목은 VT 파싱과 GPU 렌더링에서 발생한다. 대용량 출력(수만 줄)에서도 ConPTY 자체가 병목이 된 사례는 확인되지 않음 (Windows Terminal 성능 테스트 기준).

---

## 8. GhostWin 구현 권고사항

### 8.1 ConPTY 초기화 체크리스트

- [ ] `CreatePipe` 2쌍 생성 (동기 I/O 파이프)
- [ ] `GetConsoleScreenBufferInfo`로 초기 터미널 크기 결정 또는 설정값 사용
- [ ] `CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, &hPC)`
- [ ] `InitializeProcThreadAttributeList` + `UpdateProcThreadAttribute(PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE)`
- [ ] `CreateProcessW(..., EXTENDED_STARTUPINFO_PRESENT, ...)` — 환경 변수에 `TERM=xterm-256color` 포함
- [ ] CreateProcess 완료 후 즉시 `CloseHandle(inputReadSide)` + `CloseHandle(outputWriteSide)`
- [ ] I/O 전용 스레드 시작 (`ReadFile` 루프)

### 8.2 비동기 I/O 구현 권고

- **1차 MVP**: 전용 I/O 스레드 + 동기 ReadFile 루프 (단순, 충분히 효율적)
- **2차 최적화**: Named Pipe 래핑 + IOCP (다중 pane 10개 이상 환경)
- 읽기 버퍼: **64KB** 권장 (ConPTY 내부 파이프 버퍼와 정렬)
- lock-free SPSC 큐로 I/O 스레드 ↔ 파싱 스레드 연결

### 8.3 IME 구현 우선순위

1. **필수**: TSF `ITfContextOwner` 구현 — WinUI3 SwapChainPanel 환경에서 IME 안정성 확보
2. **필수**: 조합 중 문자를 ConPTY에 전송하지 않음 (확정 이후만 전송)
3. **필수**: `ITfContextOwner::GetTextExt` 정확한 커서 좌표 반환 (IME 후보창 위치)
4. **선택**: 조합 중 underline 렌더링으로 사용자에게 시각 피드백

### 8.4 리사이즈 구현

```cpp
// WinUI3 SwapChainPanel의 SizeChanged 이벤트에서 호출
void OnPanelSizeChanged(float newWidth, float newHeight, float charWidth, float charHeight)
{
    COORD newSize;
    newSize.X = (SHORT)(newWidth / charWidth);
    newSize.Y = (SHORT)(newHeight / charHeight);

    if (newSize.X != m_lastSize.X || newSize.Y != m_lastSize.Y)
    {
        ResizePseudoConsole(m_hPC, newSize);
        m_lastSize = newSize;
    }
}
```

### 8.5 정리(teardown) 구현

```cpp
void Shutdown()
{
    // 1. 입력 쓰기 핸들 닫기 → 자식이 EOF 감지 가능
    CloseHandle(m_hInputWrite);
    m_hInputWrite = INVALID_HANDLE_VALUE;

    // 2. ClosePseudoConsole은 I/O 스레드와 다른 스레드에서 호출
    // Windows 11 24H2 미만에서 교착 상태 방지
    ClosePseudoConsole(m_hPC);
    m_hPC = nullptr;

    // 3. I/O 스레드의 ReadFile이 실패 반환 → 스레드 종료 대기
    WaitForSingleObject(m_hIOThread, 5000);
    CloseHandle(m_hOutputRead);

    // 4. 자식 프로세스 종료 대기
    WaitForSingleObject(m_hChildProcess, 5000);
    CloseHandle(m_hChildProcess);
    CloseHandle(m_hChildThread);
}
```

---

## 참고 자료

| 문서 | URL | 신뢰도 |
|------|-----|--------|
| ConPTY 세션 생성 공식 문서 | learn.microsoft.com/windows/console/creating-a-pseudoconsole-session | **공식** |
| CreatePseudoConsole API | learn.microsoft.com/windows/console/createpseudoconsole | **공식** |
| ResizePseudoConsole API | learn.microsoft.com/windows/console/resizepseudoconsole | **공식** |
| ClosePseudoConsole API | learn.microsoft.com/windows/console/closepseudoconsole | **공식** |
| ConPTY 소개 블로그 | devblogs.microsoft.com/commandline/windows-command-line-introducing-the-windows-pseudo-console-conpty/ | **공식** |
| VT 시퀀스 참조 | learn.microsoft.com/windows/console/console-virtual-terminal-sequences | **공식** |
| IOCP 문서 | learn.microsoft.com/windows/win32/fileio/i-o-completion-ports | **공식** |
| WSL 버전 비교 | learn.microsoft.com/windows/wsl/compare-versions | **공식** |
| TSF ITfContextOwner | learn.microsoft.com/windows/win32/api/msctf/nn-msctf-itfcontextowner | **공식** |
| EchoCon 샘플 | github.com/microsoft/terminal/samples/ConPTY/EchoCon | **공식** |
| Windows Terminal IME 이슈 #4963 | github.com/microsoft/terminal/issues/4963 | **공식** |
| WinPTY 저장소 | github.com/rprichard/winpty | **공식** |
| VS Code ConPTY 전환 논의 | github.com/microsoft/vscode/issues/45693 | **공식** |

---

*GhostWin Terminal — ConPTY 리서치 v1.0*
*최종 업데이트: 2026-03-28*
