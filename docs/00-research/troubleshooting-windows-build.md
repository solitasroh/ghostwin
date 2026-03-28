# GhostWin — Windows 빌드 트러블슈팅 가이드

> 2026-03-29 Phase 1 빌드 과정에서 발견된 에러와 해결 방법 정리

---

## 1. LibCRuntimeNotFound

**에러**:
```
error: failed to find libc installation: LibCRuntimeNotFound
```

**원인**: Zig가 MSVC C 런타임 경로를 자동 탐색하지 못함. `vswhere.exe`가 PATH에 없거나, VS 설치가 Zig의 탐색 로직과 호환되지 않음.

**해결**:
```
# msvc_libc.txt 파일 생성 (경로는 환경에 맞게 수정)
include_dir=C:\Program Files (x86)\Windows Kits\10\Include\10.0.22621.0\ucrt
sys_include_dir=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36014\include
crt_dir=C:\Program Files (x86)\Windows Kits\10\Lib\10.0.22621.0\ucrt\x64
msvc_lib_dir=C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36014\lib\x64
kernel32_lib_dir=C:\Program Files (x86)\Windows Kits\10\Lib\10.0.22621.0\um\x64
gcc_dir=

# 빌드 시 --libc 플래그 추가
zig build ... --libc msvc_libc.txt
```

**주의**: Windows SDK 버전에 따라 include와 lib 경로가 다를 수 있음. `ucrt.lib`이 있는 SDK 버전과 `stdio.h`가 있는 SDK 버전이 다를 수 있으므로 각각 확인.

---

## 2. MSVC C++ typedef 충돌 (C2040)

**에러**:
```
error C2040: 'GhosttyTerminal': 'GhosttyTerminal *'의 간접 참조 수준이 'GhosttyTerminal'과 다릅니다
```

**원인**: ghostty 헤더의 `typedef struct GhosttyTerminal* GhosttyTerminal;` 패턴이 C에서는 합법적이지만 MSVC C++에서는 struct 태그와 typedef 이름이 같은 네임스페이스에서 충돌.

**해결**: C 브릿지 파일 사용 (ADR-002 참조)
- `vt_bridge.c` (순수 C)에서만 ghostty 헤더 include
- `vt_bridge.h`에서 `void*`로 핸들 노출
- C++ 코드는 `vt_bridge.h`만 include

---

## 3. -nostdinc++ 경고를 에러로 승격

**에러**:
```
error: argument unused during compilation: '-nostdinc++'
```

**원인**: Zig가 MSVC 타겟에서도 Clang에 `-nostdinc++` 플래그를 전달하지만, MSVC 모드의 Clang은 이를 인식하지 못함. Zig가 경고를 에러로 승격.

**해결**: `src/build/SharedDeps.zig`에서 MSVC 블록에 추가:
```zig
"-Wno-unused-command-line-argument",
```

---

## 4. std::min_element 미발견 (MSVC <algorithm> 미포함)

**에러**:
```
error: no member named 'min_element' in namespace 'std'
```

**원인**: `codepoint_width.cpp`가 `<iterator>`만 include. GCC/Clang은 `<iterator>`가 `<algorithm>`을 transitively include하지만 MSVC는 하지 않음.

**해결**: `src/simd/codepoint_width.cpp`에 추가:
```cpp
#include <algorithm>
```

---

## 5. DLL에서 ACCESS_VIOLATION crash

**에러**:
```
exit code -1073741819 (0xC0000005 ACCESS_VIOLATION)
ghostty_terminal_new 호출 시 발생
```

**원인**: Zig가 DLL 내에 static CRT(`libucrt.lib`)를 링크하지만, DLL의 `_DllMainCRTStartup`에서 CRT 힙 초기화가 실행되지 않음. `malloc`이 미초기화 힙에 접근하여 crash.

**해결**: DLL 대신 static lib 사용 + `-Dsimd=false` (CRT 의존 제거)
```
zig build -Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false
```

**참고**: Zig 이슈 — ziglang/zig#24052, ziglang/zig#19746

---

## 6. CRT 불일치 (LNK2038)

**에러**:
```
error LNK2038: 'RuntimeLibrary'에 대한 불일치가 검색되었습니다.
'MT_StaticRelease' 값이 'MDd_DynamicDebug' 값과 일치하지 않습니다.
```

**원인**: ghostty-vt-static.lib이 `/MT`(정적 CRT)로 빌드되었는데, CMake 프로젝트가 `/MDd`(동적 디버그 CRT)를 사용.

**해결**: `-Dsimd=false`로 빌드하면 CRT에 의존하지 않으므로 불일치 없음. 또는 `CMAKE_MSVC_RUNTIME_LIBRARY`를 `/MT`로 통일.

---

## 7. msvcrtd.lib 미발견 (LNK1104)

**에러**:
```
LINK : fatal error LNK1104: 'MSVCRTD.lib' 파일을 열 수 없습니다.
```

**원인**: VS 2026 Community에서 C++ Desktop 워크로드가 미설치이거나, MSVC 버전이 오래됨 (14.50에는 동적 CRT lib 없음, 14.51에 있음).

**해결**:
1. VS Installer에서 "C++를 사용한 데스크톱 개발" 워크로드 설치
2. `vcvarsall.bat x64 -vcvars_ver=14.51`로 최신 MSVC 버전 지정

---

## 8. vcvarsall 기본 버전이 오래됨

**에러**: CMake가 MSVC 14.50을 선택하지만 동적 CRT lib이 14.51에만 있음.

**원인**: `vcvarsall.bat x64`가 기본적으로 설치된 가장 오래된(?) MSVC 버전을 선택.

**해결**:
```
vcvarsall.bat x64 -vcvars_ver=14.51
```

---

## 빌드 환경 요약 (2026-03-29 기준)

| 항목 | 값 |
|------|-----|
| Zig | 0.15.2 |
| VS | 2026 Community (v18) |
| MSVC | 14.51.36014 (`-vcvars_ver=14.51`) |
| Windows SDK | 10.0.22621.0 (include/lib), 10.0.26100.0 (일부) |
| CMake | 4.0.0-rc2 |
| Ninja | 설치됨 |
| ghostty 빌드 | `-Demit-lib-vt=true -Dapp-runtime=none -Dtarget=x86_64-windows-gnu -Dsimd=false` |
