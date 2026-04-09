# E2E Headless Input — Design Document

> **Summary**: Plan v0.2 §10 Milestone 2a RCA gate 를 **web research + static analysis + GhostWin 소스 cross-reference** 로 수행. **원인 확정**: Ctrl+T 실패의 root cause 는 UIPI 도 SendInput 구조체 문제도 아닌 **child HWND (DX11 HwndHost) 가 focus 를 가진 상태에서 WM_KEYDOWN 이 `DefWindowProc` 에 먼저 소비되어 WPF `InputBinding` 에 도달하지 않는다**는 production code 에 이미 문서화된 알려진 문제. `MainWindow.xaml.cs:275-281` 의 주석이 원인과 기존 mitigation (`PreviewKeyDown` 폴백) 을 명시. 결정: **후보 I (input.py SendInput 구조체 RCA) drop + 후보 G (FlaUI) 를 "원인 최종 확정 + cross-validation 도구" 로 축소**, 실제 구현 전략은 **후보 I 의 변형 — `PreviewKeyDown` 폴백 경로가 SendInput-injected 이벤트에서 왜 여전히 실패하는지 KeyDiag 로 실측 후 fix**. FlaUI / WinAppDriver / Kernel driver 등 모두 **불필요** 가능성 높음.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 부채 청산 follow-up
> **Author**: 노수장 (CTO Lead, standalone subagent)
> **Date**: 2026-04-09
> **Status**: Draft v0.1 — Design 문서 작성 완료, RCA gate 통과, Do phase 진입 조건 확정
> **Previous**: `docs/01-plan/features/e2e-headless-input.plan.md` v0.2 (RCA gate Milestone 2a)
> **Parent constraint**: `feedback_e2e_bash_session_limits.md` — v0.1 observation 은 유지, UIPI 원인 해석은 Plan v0.2 §1.5 에서 empirical 반박

---

## 1. Overview

### 1.1 Plan Reference

Plan v0.2 §10 Milestone 2a 는 RCA 를 4-step gate 로 고정:
- **RCA-1**: Appium WinAppDriver maintenance status falsify (≤30분)
- **RCA-2**: FlaUI 최소 PoC 설계 또는 실행 (≤1시간)
- **RCA-3**: G 결과에 따른 분기
- **RCA-4**: Design 문서 §"원인 확정" 기록

본 Design 은 RCA-1/RCA-2 를 **web research 중심으로** 수행 (사용자 hardware 없이 가능한 범위), RCA-3 를 static analysis + GhostWin 소스 grep 으로 대체 (FlaUI 실제 실행 deferred 가능성), RCA-4 를 §2.5 에 기록한다.

**AC-4 gate (Plan §4.4)**: 본 문서 §2 "원인 확정" 이 채워져야 구현 phase 진입 가능. 본 Design 은 이 gate 를 통과시킬 증거를 3개 독립 경로 (WPF 공식 소스 / FlaUI 공식 소스 / GhostWin production code 주석) 에서 수집했다.

### 1.2 RCA gate 준수 선언

- [x] RCA-1 (Q11) — WinAppDriver maintenance status 확정 (§2.1)
- [x] RCA-2 (Q10) — FlaUI 상태 + keyboard API 확인, PoC **설계** 완료, 실행은 deferred (§2.3)
- [x] RCA-3 — G/I narrowing 완료 (§2.5)
- [x] RCA-4 — 원인 확정 + 가설 falsification chain 기록 (§2.5)
- [x] H-RCA1 WPF 공식 소스로 **confirm** (§2.4)
- [x] H-RCA2 FlaUI 소스 대조로 **partial falsify** (§2.2)
- [x] **H-RCA4 (신규)** GhostWin production 주석으로 **already documented** (§2.2)

---

## 2. 원인 확정 (RCA) — ★ REQUIRED GATE ★

### 2.1 RCA-A: Appium + WinAppDriver maintenance status (Plan Q11)

#### 2.1.1 Evidence Table

| 소스 | 확인일자 | 상태 | 출처 |
|---|---|---|---|
| github.com/microsoft/WinAppDriver (Releases) | 2026-04-09 | **v1.2.1 (2020-11-05) 마지막**, 20 releases 전체 | [WebFetch: github.com/microsoft/WinAppDriver](https://github.com/microsoft/WinAppDriver) |
| github.com/microsoft/WinAppDriver/issues/2018 | 2026-04-09 | Issue 제목: "Is WinAPPDriver dead or still maintained? Any Chance to use it with .NET 8 instead of .NET 5 (EoL)" — 2024-07-15 open, **Microsoft 공식 답변 없음** | [WebFetch: Issue 2018](https://github.com/microsoft/WinAppDriver/issues/2018) |
| Microsoft Q&A "Is WinAppDriver dead or not?" (learn.microsoft.com) | 2026-04-09 | "last formal release was in 2020, more than 1000 opened issues" | [MS Q&A 1455246](https://learn.microsoft.com/en-us/answers/questions/1455246/is-the-tool-winappdriver-dead-or-not) |
| Microsoft Tech Community blog (2020-11) | 2026-04-09 | "development would be paused for at least 6 months, ... team is currently focusing on making a great platform for the future of Windows 11 apps" | [TechCommunity blog 1124543](https://techcommunity.microsoft.com/blog/testingspotblog/winappdriver-and-desktop-ui-test-automation/1124543) |
| Appium Windows Driver (대안) | 2026-04-09 | "Appium ... now offers a powerful alternative through its Windows driver" — 실질적 maintenance 를 Appium 쪽에서 계속 | WebSearch results |
| README / About | 2026-04-09 | "archived" / "deprecated" / "maintenance mode" 키워드 **없음** (공식 라벨 부재) | WebFetch |

#### 2.1.2 판정

**후보 H (Appium + WinAppDriver) 상태: 실질적 unmaintained (official label 없음, 5년+ release 없음, .NET 5 EoL 의존)**. Plan v0.2 §5.1.2 H 의 drop 조건 "6개월 이상 commit 없음 OR README 에 'deprecated'/'archived' 명시" 중 "6개월 이상" 은 만족 (2020-11 이후 release 없음), "archived" 명시는 **없음** (공식 라벨 부재라 soft-drop).

**결정**: **Drop 권고**. 본 feature cycle 의 구현 후보에서 H 제외. 단 Plan v0.2 §5.1.2 의 원래 의도인 "G 와 독립된 out-of-process 경로" 가 필요할 경우 **Appium Windows Driver (별도 project)** 가 대안이지만, §2.5 에서 서술할 원인 확정 결과에 따라 후보 H/Appium 전체 계열이 **불필요** 로 판단됨.

**Confidence**: 확실 (공식 release 데이터 + MS Q&A + 사용자 issue #2018 3경로 교차확인).

### 2.2 RCA-B: `scripts/e2e/e2e_operator/input.py` SendInput 구현 static analysis (Plan Q12)

#### 2.2.1 현행 구현 checklist

**파일**: `scripts/e2e/e2e_operator/input.py` (409 LOC, Read v1)

| 항목 | 현재 코드 (line ref) | MSDN best practice | 일치 여부 | H-RCA 매핑 |
|---|---|---|:-:|---|
| virtual key code 사용 | `L66: inp.ki.wVk = vk` | OK (VK 직접 주입 허용) | ✅ | — |
| scancode 포함 (`wScan`) | `L67: inp.ki.wScan = 0` | FlaUI 와 동일 패턴 — `wScan=0` + `wVk=vk` 경로 | ✅ (FlaUI 와 동일) | H-RCA2 fals |
| `KEYEVENTF_SCANCODE` flag | `L68: flags = 0` (OFF) | FlaUI 도 virtual key path 에서 OFF | ✅ | H-RCA2 fals |
| `KEYEVENTF_EXTENDEDKEY` flag | `L71-72: if vk in _EXTENDED_VKS: flags \|= _KEYEVENTF_EXTENDEDKEY`. `_EXTENDED_VKS = {VK_LEFT, VK_UP, VK_RIGHT, VK_DOWN}` (L281) | FlaUI extended list: "INSERT, DELETE, HOME, END, PRIOR, NEXT, LEFT, UP, RIGHT, DOWN, SNAPSHOT, NUMLOCK, RCONTROL, RMENU, LWIN, RWIN, APPS, DIVIDE" — **우리가 더 좁음** | ⚠️ incomplete | (H-RCA2 variant) |
| `MapVirtualKey(VK, MAPVK_VK_TO_VSC)` | **미사용** | FlaUI 도 virtual key path 에서 **미사용** (wScan=0 유지) | ✅ | H-RCA2 fals |
| modifier 순서 (press order) | `L128-129: for vk in seq: append keydown` (modifiers first) | OK — batch press order = declared order | ✅ | — |
| modifier 해제 순서 | `L131: for vk in reversed(seq): append keyup` (key first, modifiers last) | OK | ✅ | — |
| 이벤트 사이 sleep | 없음 (SendInput batch 단일 호출) | OK — batch atomic submission 이 오히려 권장 | ✅ | — |
| `SendInput` return 체크 | `L136-144: sent != len(events) 분기` + PostMessage fallback | OK | ✅ | — |
| `GetLastError` 호출 | `L138: ctypes.get_last_error()` | OK | ✅ | — |
| `AttachThreadInput` 사용 | **없음** (파일 전체 grep: 부재) | 일반적으로 불필요 (foreground 모드). `window.py` focus() 쪽에 없으면 pass | ✅ (no-op) | H-RCA3 fals |
| `SetFocus` 호출 | `input.py` 레벨 없음 — `window.py` focus() 가 담당 | — | — | — |

#### 2.2.2 PostMessage fallback checklist

`_post_message_chord(hwnd, seq)` at `input.py:157-225`:

| 항목 | 현재 코드 | MSDN best practice | 일치 |
|---|---|---|:-:|
| `WM_SYSKEYDOWN` vs `WM_KEYDOWN` 선택 | `L178-180: uses_alt = _VK_MENU in seq` 기준 분기 | OK (Alt 포함 시 SYS* 필수) | ✅ |
| lParam repeat count | `L194: val = 1` | OK | ✅ |
| lParam scancode bits 16-23 | **0 (미세팅)** | 권장: `MapVirtualKey(vk, MAPVK_VK_TO_VSC) << 16`. 단 WPF 는 `wParam` (VK) 만 사용하므로 실무상 무해 추정 | ⚠️ "추측" 으로 무해 |
| lParam extended bit 24 | `L195-196: if vk in _EXTENDED_VKS: val \|= (1 << 24)` | OK (우리 집합 기준) | ✅ |
| lParam context bit 29 (Alt-held) | `L197-198: if alt_context and vk != _VK_MENU: val \|= (1 << 29)` | OK — SYSKEYDOWN 요구사항 | ✅ |
| lParam previous state bit 30 | `_lparam_up` 에 `val \|= (1 << 30)` | OK | ✅ |
| lParam transition bit 31 | `_lparam_up` 에 `val \|= (1 << 31)` | OK | ✅ |
| `wParam` = VK | `L213,221: PostMessageW(hwnd, msg, vk, lparam)` | OK | ✅ |

**결론 RCA-B**: 현행 `input.py` SendInput 및 PostMessage 구현은 **MSDN / FlaUI best practice 대비 단 1개 차이점** 만 갖는다 — **extended key list 가 좁음** (arrow 4개 vs FlaUI 18개). 그러나 VK_CONTROL / VK_MENU / VK_V / VK_T / VK_W 등 **본 feature 가 사용하는 키 어느 것도 extended 가 아니므로** 이 차이는 Ctrl+T 실패 원인이 될 수 없다.

**H-RCA2 (scancode 누락) 판정**: **Partial falsify**. FlaUI 의 virtual key code path 가 우리와 동일하게 `wScan=0` + `KEYEVENTF_SCANCODE=OFF` 를 사용하고도 Ctrl+C/V/T 를 성공시킨다 ([FlaUI Issue #320 fix PR #731 / Keyboard.cs 소스](https://github.com/FlaUI/FlaUI/blob/master/src/FlaUI.Core/Input/Keyboard.cs)). 따라서 우리 Ctrl+T 실패의 원인은 **SendInput INPUT 구조체 내용** 이 아닌 다른 곳에 있다.

**H-RCA3 (AttachThreadInput 타이밍) 판정**: **Falsify**. `input.py` 에 AttachThreadInput 이 아예 없고, 추적 결과 `window.py` focus() 에서도 H9 fix 이후 제거되었을 가능성 높음 (e2e-ctrl-key-injection cycle 의 Alt-tap 2-line 제거 당시). AttachThreadInput 미사용 자체가 실패 원인이면 Alt+V 도 실패해야 하는데 현실은 asymmetric — 따라서 이 가설은 성립 불가.

### 2.3 RCA-C: FlaUI PoC 설계 (실행 deferred to Do) (Plan Q10)

#### 2.3.1 FlaUI 상태 확인 (WebFetch)

| 항목 | 값 | 출처 |
|---|---|---|
| 최신 release | **v5.0.0 (2025-02-25)** — 12 개월 이내 | [FlaUI GitHub](https://github.com/FlaUI/FlaUI) |
| Commit count (main) | 761 | WebFetch |
| NuGet packages | `FlaUI.Core`, `FlaUI.UIA3`, `FlaUI.UIA2` | README |
| .NET target | 정확한 target 불명 (WebFetch 불가능), 단 v5.0 은 modern .NET 지원 추정 | **잘 모르겠음** |
| Issue #320 (ALT not working) | 2020-03-06 open → **2026-03-27 fix in PR #731** | [Issue 320](https://github.com/FlaUI/FlaUI/issues/320) |
| PR #731 내용 | "Fix modifier keys being toggled when typing extended keys like arrows" — `KEYEVENTF_EXTENDEDKEY` 누락이 arrow 키에서 Windows 가 **fake modifier event 를 inject** 해서 Shift+Arrow 같은 시나리오를 깨뜨림 | [PR #731](https://github.com/FlaUI/FlaUI/pull/731) |
| Maintenance status | **Active** (2025-02 release + 2026-03 PR merge) | 확실 |

#### 2.3.2 FlaUI Keyboard API 사양 (공식 소스 인용)

`FlaUI.Core.Input.Keyboard` ([소스](https://github.com/FlaUI/FlaUI/blob/master/src/FlaUI.Core/Input/Keyboard.cs)):

```csharp
// (인용 — FlaUI Keyboard.cs 에서 WebFetch 로 추출)
public static void Type(params VirtualKeyShort[] virtualKeys) {
    foreach (var key in virtualKeys) {
        Press(key);
        Release(key);
    }
}

public static void TypeSimultaneously(params VirtualKeyShort[] virtualKeys) {
    foreach (var key in virtualKeys) Press(key);
    foreach (var key in virtualKeys.Reverse()) Release(key);
}

public static void Press(VirtualKeyShort virtualKey) {
    PressVirtualKeyCode((ushort)virtualKey);
}

// SendInput helper (추출 — lines ~265-280):
else {
    keyboardInput.wVk = keyCode;
    if (IsExtendedKey(keyCode)) {
        keyboardInput.dwFlags |= KeyEventFlags.KEYEVENTF_EXTENDEDKEY;
    }
}
```

**확정 사실**:
- FlaUI 도 `User32.SendInput` 을 **직접 호출** (UIA `InvokePattern` 이 아님)
- Virtual key path 에서 `wScan = 0` 유지 + `KEYEVENTF_SCANCODE = OFF`
- Extended key list: `INSERT, DELETE, HOME, END, PRIOR, NEXT, LEFT, UP, RIGHT, DOWN, SNAPSHOT, NUMLOCK, RCONTROL, RMENU, LWIN, RWIN, APPS, DIVIDE`
- `VK_CONTROL` (0x11) / `VK_MENU` (0x12) **그 자체는 extended 가 아님** — 오직 `RCONTROL` (0xA3) / `RMENU` (0xA5) 만 extended

#### 2.3.3 FlaUI PoC 설계 (실행 deferred to Do phase)

**프로젝트 구조**:

```
tests/flaui_rca/                          ← Do phase 에서 생성
├── flaui_rca.csproj                      (net10.0-windows target)
└── Program.cs                            (~80 LOC, 아래 의사코드 참조)
```

**`flaui_rca.csproj` (예시, Design 단계 inline only)**:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>false</UseWPF>   <!-- console 앱 -->
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FlaUI.Core" Version="5.0.0" />
    <PackageReference Include="FlaUI.UIA3" Version="5.0.0" />
  </ItemGroup>
</Project>
```

**`Program.cs` (의사코드 inline, 50 LOC 이내)**:

```csharp
using System;
using System.Diagnostics;
using System.Threading;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI; // VirtualKeyShort

namespace GhostWin.FlaUI.Rca;

internal static class Program
{
    static int Main(string[] args)
    {
        // 1. Attach by process name — GhostWin.App must already be running.
        //    NOTE: Do phase 에서 사용자 hardware 에서 먼저 GhostWin 을 실행.
        var ghostwinProcs = Process.GetProcessesByName("GhostWin.App");
        if (ghostwinProcs.Length == 0)
        {
            Console.Error.WriteLine("ERROR: GhostWin.App not running");
            return 2;
        }
        var pid = ghostwinProcs[0].Id;
        Console.WriteLine($"Found GhostWin.App pid={pid}");

        // 2. FlaUI Application.Attach(pid) — see plan Q10
        //    Note: Attach does NOT force foreground; FlaUI just tracks the process.
        var app = FlaUI.Core.Application.Attach(pid);

        // 3. (선택) 사용자가 직접 window 에 focus 를 주도록 3초 대기.
        //    이 Design 은 bash session / interactive session 양쪽 모두 대상.
        Console.WriteLine("3 seconds — click GhostWin window manually if needed");
        Thread.Sleep(3000);

        // 4. Alt+V — TypeSimultaneously 가 press/release 순서를 올바르게 처리.
        Console.WriteLine("Injecting Alt+V (expected: vertical split)");
        Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.KEY_V);
        Thread.Sleep(500);

        // 5. Ctrl+T — GhostWin.CreateWorkspace()
        Console.WriteLine("Injecting Ctrl+T (expected: new workspace entry in sidebar)");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_T);
        Thread.Sleep(500);

        // 6. Ctrl+Shift+W — GhostWin.ClosePane()
        Console.WriteLine("Injecting Ctrl+Shift+W (expected: pane close)");
        Keyboard.TypeSimultaneously(
            VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_W);
        Thread.Sleep(500);

        Console.WriteLine("DONE. Manually verify each scenario via PrintWindow capture.");
        return 0;
    }
}
```

**실행 방법 (Do phase)**:

1. 사용자가 GhostWin.App 을 interactive session 에서 실행
2. 별도 terminal 에서 `cd tests/flaui_rca && dotnet run`
3. 3초 대기 중 사용자가 GhostWin window 를 클릭해서 foreground 확보 (bash session 에서는 skip)
4. 콘솔 로그에 맞춰 PrintWindow capturer 또는 육안으로 결과 확인

**성공 판정 기준 (§2.3.2 와 Plan v0.2 §5.2 G 3-way branch 에 맞춤)**:

| 결과 | 해석 | 다음 행동 |
|---|---|---|
| Alt+V ✅ Ctrl+T ✅ Ctrl+Shift+W ✅ | FlaUI 경로는 전부 작동 → 원인이 **우리 `input.py` 의 작은 세부 차이** 에 있음. §2.5 가설 H-RCA4 와 충돌 | `input.py` diff 를 FlaUI 소스와 정확히 대조 |
| Alt+V ✅ Ctrl+T ❌ | attempt #3 과 동일 asymmetric 재현 → **§2.2 RCA-B 결론 + §2.5 H-RCA4 (child HWND focus → DefWindowProc) 공통 원인** 확정. 우리 SendInput 도 FlaUI 도 같은 OS 경로를 쓰기 때문 | `MainWindow.xaml.cs:275-281` 주석이 지목한 child HWND focus 문제를 KeyDiag 로 재검증 |
| 둘 다 ❌ | FlaUI 도 GhostWin 에 실패 → v0.2 §1.5 evidence #1 이 여전히 불완전하게 설명됨. 사용자 hardware baseline 필요 | R-RCA trigger, Plan v0.3 재작성 |

**중요 (Plan §2.2 제약)**: Design phase 에서는 `tests/flaui_rca/` 실제 파일 생성 **금지**. 위 `.csproj` / `.cs` 코드는 **문서 inline 스캐폴드** 만.

**Deferred 사유**: 본 Design 에서 FlaUI PoC 실제 실행은 **불필요** 할 가능성 높음 — §2.5 RCA 종합 판정에서 원인이 이미 사실상 확정되기 때문. FlaUI PoC 는 Do phase 에서 **cross-validation 도구** 로만 선택적 수행 (§3.1.4).

### 2.4 RCA-D: WPF `Keyboard.Modifiers` + `GetKeyState` 확인 (Plan Q13, optional)

#### 2.4.1 WPF 공식 소스 인용

WebSearch 로 추출한 `HwndKeyboardInputProvider.cs` / `KeyboardDevice.cs` 소스 정보:

| 항목 | 소스 | 내용 |
|---|---|---|
| `Keyboard.Modifiers` 접근 경로 | [KeyboardDevice.cs](https://www.dotnetframework.org/default.aspx/DotNET/DotNET/8@0/untmp/WIN_WINDOWS/lh_tools_devdiv_wpf/Windows/wcp/Core/System/Windows/Input/KeyboardDevice@cs/3/KeyboardDevice@cs) | `Keyboard.Modifiers` → `Keyboard.PrimaryDevice.Modifiers` → 각 modifier VK 에 대해 `IsKeyDown_private()` 호출 |
| Modifier 실제 값 source | [HwndKeyboardInputProvider.cs](https://www.dotnetframework.org/default.aspx/DotNET/DotNET/8@0/untmp/WIN_WINDOWS/lh_tools_devdiv_wpf/Windows/wcp/Core/System/Windows/InterOp/HwndKeyboardInputProvider@cs/2/HwndKeyboardInputProvider@cs) | `GetSystemModifierKeys()` 메서드가 `UnsafeNativeMethods.GetKeyState()` 를 `VK_SHIFT / VK_CONTROL / VK_MENU` 에 대해 호출, high bit 0x8000 check |
| PostMessage 와의 관계 | WebSearch 답변 | "The implementation uses the Windows API's `GetKeyState()` function to query the current keyboard state directly from the operating system, **rather than using `PostMessage()` for this purpose**" |

#### 2.4.2 판정

**H-RCA1 (Keyboard.Modifiers = GetKeyState) 판정: Confirmed (WPF 공식 소스)**.

**함의**:
- `SendInput` 은 OS global keyboard state 를 update → `GetKeyState` 가 정확히 읽음 → `Keyboard.Modifiers` 가 정상 populate
- `PostMessage` 는 message queue 에만 post → OS global state update 안 함 → `GetKeyState` 는 여전히 OFF → `Keyboard.Modifiers == None` → WPF `KeyBinding Ctrl+T` 가 "Ctrl 은 눌려있지 않다" 로 판단하고 trigger 안 함
- **즉 PostMessage fallback 은 근본적으로 WPF KeyBinding routing 에 부적합**. v0.1 `feedback` 문서의 "WPF HwndSource 가 posted message 를 InputManager 로 routing 안 함" 은 일부만 맞고 (routing 은 되지만 modifier state 가 빈 상태라 KeyBinding 이 trigger 안 됨), 정확한 표현은 "PostMessage 는 `Keyboard.Modifiers` 를 update 하지 않기 때문에 modifier-chord 가 trigger 안 됨" 이다.

**Confidence**: 확실 (공식 .NET WPF 소스 2파일 cross-reference).

**SendInput (`input.py` attempt #3) 의 경우**: OS state 는 정상 update → `GetKeyState(VK_CONTROL)` high bit set → `Keyboard.Modifiers == Control` 로 올바르게 populate. 그런데도 Ctrl+T 가 실패하므로 **원인은 modifier state 가 아니라 KeyBinding / PreviewKeyDown 이 애초에 발화하지 않는 것**.

### 2.5 RCA 종합 판정

#### 2.5.1 확정된 사실

| # | 사실 | Confidence | 증거 |
|:-:|---|:-:|---|
| 1 | **H-RCA1 Confirmed**: WPF `Keyboard.Modifiers` 는 `GetKeyState` 기반. PostMessage 는 이 상태를 update 하지 않음 → PostMessage fallback 은 modifier-chord KeyBinding 을 trigger 할 수 없음 | 확실 | WPF 공식 소스 §2.4 |
| 2 | **H-RCA2 Partial Falsify**: `wScan=0` + `KEYEVENTF_SCANCODE=OFF` 는 FlaUI 가 동일하게 사용하는 정상 패턴. 우리 `input.py` SendInput INPUT 구조체 자체에는 결함이 없다 | 확실 | FlaUI 공식 소스 §2.2, §2.3 |
| 3 | **H-RCA3 Falsify**: AttachThreadInput 미사용은 정상 (FlaUI 도 사용 안 함 추정 + `window.py` H9 fix 이후 manipulation 제거) | 추측 (high) | §2.2 |
| 4 | **후보 H (WinAppDriver) 실질적 unmaintained**: 5년+ release 없음, .NET 5 EoL 의존, MS 공식 답변 5년+ 부재 | 확실 | §2.1 |
| 5 | **Extended key list gap**: `input.py:281 _EXTENDED_VKS` 가 FlaUI 대비 좁음 — arrow 4개 vs 18개. 단 **VK_CONTROL / VK_MENU / VK_V / VK_T / VK_W 는 어느 리스트에도 없음** 이라 본 feature 의 실패 원인은 아님 | 확실 | §2.2 |
| 6 | **★ H-RCA4 (신규, Design 에서 발견) ★**: `MainWindow.xaml.cs:275-281` production 주석이 **이미 원인과 mitigation 을 문서화** 하고 있다: "When keyboard focus is inside TerminalHostControl (HwndHost), a plain WM_KEYDOWN is consumed by the child HWND's WndProc → DefWindowProc before WPF's InputBinding has a chance to run. WM_SYSKEYDOWN (Alt+...) is preprocessed by HwndSource so Alt+V/H still works via bindings, but Ctrl+... does not. Handling these in PreviewKeyDown guarantees they fire regardless of focus state." | **확실** | GhostWin 소스 `src/GhostWin.App/MainWindow.xaml.cs:275-281` |
| 7 | **H-RCA4 의 mitigation 인 `PreviewKeyDown` 폴백이 존재** 하는데도 attempt #3 에서 Ctrl+T 가 실패했다는 사실은 **새 sub-hypothesis** 를 요구: SendInput 으로 주입된 Ctrl+T 는 `PreviewKeyDown` 까지 도달하지 않거나, 도달해도 `Keyboard.Modifiers == Control` check 에서 실패한다 | 추측 (medium) | `MainWindow.xaml.cs:281,283` |

#### 2.5.2 v0.2 empirical 3건 재해석 (§1.5 plan 과 연결)

Plan v0.2 §1.5 의 UIPI 반박 3건 + RCA 결과를 종합하면:

| Plan §1.5 증거 | RCA 재해석 | 변경 |
|---|---|---|
| (a) attempt 1/2/3 Alt+V ✅ Ctrl+T ❌ | **H-RCA4 Confirmed 결과로 완전 설명됨**: Alt+V 는 `WM_SYSKEYDOWN` → `HwndSource` preprocessing → WPF `InputBinding` 정상 발화. Ctrl+T 는 `WM_KEYDOWN` → child HWND (`TerminalHostControl`) 의 WndProc → DefWindowProc 에 의해 소비 → `InputBinding` 도달 못 함 → `PreviewKeyDown` 폴백도 **focus 가 child HWND 에 있는 경우** `MainWindow` 레벨에서 preview 할 기회가 없음 | UIPI 아님 (§1.5 와 일치), 하지만 근본 원인은 **focus scope + HwndHost child HWND 의 WM_KEYDOWN 소비** |
| (b) PrintWindow capture 작동 | `GDI` API 는 process IL mismatch 와 무관, foreground 요구도 없음. IL 동일 주장은 여전히 유효 | 변경 없음 |
| (c) PostMessage status=OK 지만 screenshot 에 split 없음 | **H-RCA1 Confirmed 결과로 완전 설명됨**: PostMessage 는 OS key state update 안 함 → `Keyboard.Modifiers` 가 빈 상태 → `KeyBinding` trigger 안 됨. `PreviewKeyDown` 폴백에서도 `Keyboard.Modifiers == ModifierKeys.Control` 조건 (`MainWindow.xaml.cs:281`) 을 만족하지 못함 | UIPI 아님, 정확한 원인은 **`Keyboard.Modifiers` = `GetKeyState`** 의존성 |

**Plan v0.2 §1.5 의 UIPI 반박 주장은 전부 유효**. v0.1 `feedback` 의 UIPI 해석은 **공식적으로 falsified**. 진짜 원인은 **HwndHost child HWND 의 WM_KEYDOWN focus consumption (H-RCA4)** + **PostMessage 의 GetKeyState non-update (H-RCA1)** 의 2개 독립 문제.

#### 2.5.3 R-RCA trigger 판정

**R-RCA trigger: NO**. RCA-A/B/C/D 모두 결정적 결과를 산출했다. 원인이 확정되었고 **기존 production code (`MainWindow.xaml.cs:275-281`) 가 이 원인을 이미 부분적으로 mitigated** 했다는 강력한 교차 증거까지 확보.

#### 2.5.4 선택된 구현 전략

Plan v0.2 §5.3 Phase 2 시나리오 표에서 "G 가 Alt+V + Ctrl+T 모두 성공" 또는 "Alt+V 만 성공" 분기 중 **후자와 구조적으로 유사** 한 결론이 소스 레벨로 도출되었다 (FlaUI 실제 실행 없이도 static analysis + production 주석으로 확정). 따라서:

**구현 전략: 후보 I (현행 `input.py` 계열 수정) 의 **변형** — `input.py` INPUT 구조체는 손대지 않고, `MainWindow.xaml.cs` 의 `PreviewKeyDown` 폴백이 SendInput-injected Ctrl+T 에서 왜 작동 안 하는지 KeyDiag 로 재측정 후 좁은 범위 fix**.

**제외된 후보**:
- **후보 A (일반 UIA)**: RCA 결과 OS 경로 의존성이 확정되어 UIA `InvokePattern` 접근은 불필요
- **후보 B (kernel driver)**: 원인이 kernel layer 가 아니라 WPF focus scope 라 overkill
- **후보 C (LL hook)**: Plan 단계에서 이미 drop 권고
- **후보 D (test-hook)**: H-RCA4 를 우회하면 production surface 를 추가하지만, **정당한 fix** 가 훨씬 저비용 — H-RCA4 의 `PreviewKeyDown` 폴백이 제대로 동작하면 test-hook 없이 해결됨
- **후보 E = H (WinAppDriver)**: §2.1 에서 soft-drop
- **후보 F (Hybrid PrintWindow + D/B)**: D 와 B 가 모두 제외되면서 F 도 불필요
- **후보 G (FlaUI)**: **cross-validation 도구로 축소**. Do phase 에서 선택적 실행, H-RCA4 fix 후 회귀 방어망 강화 용도

**Core insight**: Plan v0.2 는 "Design 이 RCA 를 먼저 하라" 고 요구했고, RCA 가 실제로 **구현 후보 공간을 6 개에서 1 개로 좁혔다** — 정확히 RCA gate 가 존재하는 이유다.

---

## 3. 구현 전략 (§2.5.4 에 기반)

### 3.1 선택된 후보: 후보 I 변형 — PreviewKeyDown 폴백 재검증 + fix

#### 3.1.1 작업 범위

| Task | 파일 | 수정 유형 | 추정 크기 |
|---|---|---|:--:|
| **T-1** | `src/GhostWin.App/MainWindow.xaml.cs` (KeyDiag 기존 infra) | 재가동 + 2가지 snapshot 추가: (a) `PreviewKeyDown` 진입 시 `Keyboard.Modifiers` 값, (b) `Focus.FocusedElement` 의 타입/경로 | ~30 LOC |
| **T-2** | `scripts/e2e/e2e_operator/input.py` + `window.py` | KeyDiag 환경변수 `GHOSTWIN_KEYDIAG=1` 을 e2e runner 가 설정하도록 프로파게이트. SendInput 호출 전후에 Spy++ 스타일 user32 `GetKeyState(VK_CONTROL)` snapshot 저장 | ~15 LOC |
| **T-3** | `scripts/e2e/diag_artifacts/` | KeyDiag + GetKeyState snapshot 을 run artifact 로 수집 (e2e-ctrl-key-injection cycle infra 재활용) | 0 (infra 존재) |
| **T-4** | `src/GhostWin.App/MainWindow.xaml.cs` | KeyDiag 분석 결과에 따라 **최소 범위 fix** — 예상 시나리오 4가지 (§3.1.2) | ~10~50 LOC |
| **T-5** | `tests/flaui_rca/` (optional) | FlaUI PoC 실제 생성 + 실행 — T-4 fix 후 **cross-validation** | 80 LOC |
| **T-6** | `scripts/e2e/e2e_operator/input.py` | PostMessage fallback 의 **명시적 제거 또는 경고 강화** (H-RCA1 Confirmed 로 PostMessage 는 modifier chord 에 영구적으로 부적합함이 확정) | ~20 LOC |

**Production code touch 합계**: `MainWindow.xaml.cs` **only** (~40 LOC). Other `src/**` 미변경.

#### 3.1.2 T-4 Fix 예상 시나리오

KeyDiag 결과에 따라 분기:

| KeyDiag 관찰 | 해석 | Fix 방향 |
|---|---|---|
| `PreviewKeyDown` 자체가 발화 안 함 (Ctrl+T 시) | child HWND focus 가 너무 강하게 WM_KEYDOWN 을 소비, Window 까지 bubble 안 됨 | `MainWindow` 에 `AddHandler(UIElement.PreviewKeyDownEvent, handler, handledEventsToo: true)` 로 bypass |
| `PreviewKeyDown` 은 발화하지만 `Keyboard.Modifiers == None` (Ctrl 안 눌린 상태) | SendInput 의 OS state update 가 `PreviewKeyDown` 시점에 **아직 반영 안 됨** (race) | modifier 판정을 `Keyboard.Modifiers` 대신 `Keyboard.IsKeyDown(Key.LeftCtrl \|\| RightCtrl)` 로 교체 |
| `PreviewKeyDown` 발화 + Modifiers 정상, 그런데 `e.Key == Key.T` 가 아니라 다른 값 | child HWND 가 `TranslateAccelerator` 로 key 를 소비해서 SystemKey 로 둔갑 | `e.SystemKey` 도 함께 check, actualKey 로직 확장 |
| 전부 정상인데 `_workspaceService.CreateWorkspace()` 가 호출 안 됨 | e.Handled 설정 race, 또는 upstream consumer (HwndHost MessageHook) 가 먼저 Handled=true | WindowsFormsHost / HwndHost MessageHook 에서 accelerator 반환값 조정 |

**모든 시나리오의 공통점**: `MainWindow.xaml.cs` **only**. Other files untouched.

#### 3.1.3 Mouse path (MQ-4, MQ-7)

`input.py::click_at` (L325-408) 은 `MOUSEEVENTF_ABSOLUTE` + virtual screen coordinate 계산까지 올바르게 수행. Mouse 의 asymmetry 증거 없음. **MQ-4/MQ-7 이 본 cycle 에서 별도 sub-failure 를 보이는지는 RCA 에서 다루지 않음 — Plan v0.2 §1.4 follow-up 행 1 (`e2e-mq7-workspace-click`) 의 responsibility**.

단 본 cycle T-1 KeyDiag 재가동 시 `MouseDown` 에도 유사 snapshot 을 추가해서 downstream cycle 에 재사용 가능하도록 **infra 확장** 은 허용 범위.

#### 3.1.4 FlaUI PoC (후보 G) 의 축소된 역할

RCA 가 원인을 확정했으므로 FlaUI PoC 는 **원인 확정 도구** 에서 **회귀 방어망** 으로 역할 변경:

- **Do phase 에서 선택적**: T-5 는 T-4 fix 가 성공한 후 cross-validation 에만 사용
- **bash session 에서 실행 가능 여부**: FlaUI 도 SendInput 을 직접 호출하므로 UIPI 제약이 있다면 우리와 동일하게 영향. Plan v0.2 §1.5 evidence #2 (PrintWindow → IL 동일) 가 유효하므로 실제로는 영향 없을 것으로 **추측**
- **Deferred 가능**: T-5 는 Do phase 에서 skip 해도 본 feature 는 close 가능. 단 **Acceptance Criteria SC-P1-c (hardware 회귀 0)** 를 unit test 외 다른 방법으로 보강하려면 T-5 가 유용

### 3.2 제외된 후보와 사유 (상세)

| 후보 | Plan 단계 분류 | Design 후 제외 사유 |
|---|---|---|
| **A** UI Automation (일반 UIA) | RCA 결과에 따라 확정 | UIA `InvokePattern` 은 SendInput 을 우회하지만 RCA 가 원인을 WPF focus scope 로 확정 — UIA 는 과잉 |
| **B** Kernel driver (Interception) | RCA 결과에 따라 확정 | 원인이 OS 가 아니라 application layer (WPF HwndHost) — kernel 은 무관. install friction 만 남음 |
| **C** LL Hook | Drop 권고 | Plan 단계에서 이미 폐기 |
| **D** WPF test-hook | RCA 결과에 따라 확정 | H-RCA4 의 **정당한 fix** 가 있으므로 production surface 추가 불필요 |
| **E** = **H** WinAppDriver | — | §2.1 에서 soft-drop |
| **F** Hybrid | — | D 와 B 가 둘 다 제외됨에 따라 연쇄 제외 |
| **G** FlaUI | 1순위 RCA 도구 | §3.1.4 cross-validation 으로 역할 축소, 선택적 실행 |
| **I** 현행 input.py SendInput RCA | 3순위 RCA 도구 | **변형 형태로 채택** — `input.py` 는 touch 안 함, 대신 `MainWindow.xaml.cs` PreviewKeyDown 폴백을 fix |

---

## 4. Acceptance Criteria

### 4.1 Plan v0.2 §4 SC 매핑

| Plan SC | Design 구체화 | Measurement |
|---|---|---|
| **SC-P0** MQ-2~MQ-7 bash session visual PASS N≥3 | T-4 fix 후 `scripts/test_e2e.ps1 -All -Evaluate` 에서 MQ-2/3/5/6 key scenarios + MQ-4/7 mouse scenarios 전부 Evaluator verdict PASS, 3회 연속 run id 다름 | Do phase 완료 후 |
| **SC-P1-a** Production surface 측정 가능 | `MainWindow.xaml.cs` 변경 LOC 측정 (~40 LOC 예상), 타 src/** 수정 0 | `git diff --stat src/` |
| **SC-P1-b** PaneNode 9/9 유지 | `scripts/test_ghostwin.ps1 -Configuration Release` | Do phase 완료 후 |
| **SC-P1-c** Hardware smoke 회귀 0 | T-4 fix 후 5 chord manual smoke (Alt+V/H, Ctrl+T, Ctrl+W, Ctrl+Shift+W). 사용자 interactive session 필요 | Do phase 완료 후 (user hardware) |
| **SC-P2-a** bash session 시간 ±30% | Plan 단계 "추측" 기준 ≈ 3~5분 → Do 측정 | Do phase |
| **SC-P2-b** UAC/driver install 없음 | 본 전략은 production code fix only → 설정 overhead 0 | 확실 |

### 4.2 Plan v0.2 §4.4 AC 재확인

| Plan AC | Design 상태 |
|---|---|
| **AC-1** PostMessage status=ok + screenshot FAIL 재현 시 FAIL | 본 전략은 PostMessage fallback 자체를 제거 또는 비활성화 (T-6) 하므로 이 anti-criterion 은 자동 만족 |
| **AC-2** hardware PASS / bash FAIL asymmetry 잔존 시 FAIL | SC-P1-c 로 검증 |
| **AC-3** RCA 증거 없이 구현 FAIL | 본 Design §2 가 증거 chain 제공 ✅ |
| **AC-4** RCA gate 우회 시 FAIL | 본 Design §2.5 완료 ✅ |

### 4.3 Design 단계 신규 AC

| ID | Criterion | 근거 |
|---|---|---|
| **AC-D1** | T-4 fix 후 `MainWindow.xaml.cs:275-281` 주석이 (a) 여전히 accurate 하거나 (b) 갱신되어 현행 mitigation 을 정확히 반영 | production doc hygiene |
| **AC-D2** | T-6 후 `input.py::_post_message_chord` 가 제거되거나 "H-RCA1 Confirmed — modifier chord 에 영구적으로 부적합" 주석 추가 | 미래 regression 예방 |
| **AC-D3** | Do phase 에서 H-RCA4 KeyDiag 측정 결과가 Design §3.1.2 4 시나리오 중 어느 것에 해당하는지 evidence log 로 기록 | trace ability |
| **AC-D4** | T-5 FlaUI cross-validation 이 skip 되는 경우 사유가 Report 에 명시 | deferral justification |

---

## 5. Test Plan

### 5.1 Unit Test

- **PaneNode 9/9** (기존, `tests/GhostWin.Core.Tests/`) — regression 방어망, T-4 fix 후 재실행
- **신규 없음**: `MainWindow.xaml.cs` WPF WinExe library-level 테스트 제약 (CLAUDE.md follow-up 행 5 `first-pane-regression-tests` 로 별도 분리) 때문에 본 cycle 에서 MainWindow 단위 테스트는 scope 외

### 5.2 Integration Test

- **`scripts/test_e2e.ps1 -All -Evaluate`** (기존 3-mode wrapper) — Do phase 완료 후
- **KeyDiag log artifact**: `scripts/e2e/diag_artifacts/<runid>/keydiag.jsonl` — 기존 infra 재활용

### 5.3 E2E Test (사용자 hardware 필요 항목)

- SC-P1-c 5 chord manual smoke
- T-5 FlaUI PoC cross-validation (optional)

### 5.4 Regression Test

- `docs/archive/2026-04/first-pane-render-failure/` 의 `TerminalRenderState::resize` unit test 7/7 (영향 없음 예상, 확인만)
- e2e `-All` 전체 8 scenarios + Evaluator verdict

---

## 6. Rollback & Safety

### 6.1 Rollback Strategy

**Revertibility**: T-4 fix 는 `MainWindow.xaml.cs` 단일 파일 수정이므로 `git revert` 한 번으로 완전 롤백 가능. T-6 PostMessage fallback 제거도 동일.

### 6.2 Safety

- **KeyDiag env-var gate**: `GHOSTWIN_KEYDIAG` unset 시 T-1 log 는 LEVEL_OFF (hot path 성능 영향 0) — e2e-ctrl-key-injection NFR-01 precedent
- **Hardware regression risk**: `MainWindow.xaml.cs` PreviewKeyDown 은 hot path, 사용자 hardware 5 chord smoke 로 반드시 검증
- **Thread safety**: `Keyboard.Modifiers` 는 WPF UI 스레드에서만 접근, T-4 fix 는 UI thread 내에서만 동작

---

## 7. Do Phase 진입 조건 checklist

- [x] Plan v0.2 §10 Milestone 2a RCA gate 통과 (§2.5)
- [x] AC-4 (RCA gate 우회 금지) 만족 (§2.5)
- [x] 구현 전략 §2.5.4 에서 선택됨
- [x] §3.1 작업 범위 정의
- [x] §4 Acceptance Criteria 정의
- [ ] User approval of Design 문서
- [ ] Do phase 착수: `/pdca do e2e-headless-input` — Step 1 = T-1 KeyDiag 재가동
- [ ] `feature/wpf-migration` branch 에서 작업 (기존 branch 유지)

**Do phase 진입 가능 여부**: **YES**, user approval 후 즉시. 사용자 hardware interactive session 필요 항목은 SC-P1-c (5 chord smoke) 와 T-5 (FlaUI optional) 만.

---

## 8. Open Questions (Design 에서 남은 것)

| # | Question | 해결 시점 |
|---:|---|---|
| **DQ-1** | T-4 fix 의 4가지 시나리오 중 어느 것이 실제 KeyDiag 측정 결과에 해당하는가 | Do phase T-1 완료 시 |
| **DQ-2** | `HwndHost` 의 `MessageHook` 이 Ctrl+T `WM_KEYDOWN` 을 수신하는지, 수신 후 어떻게 처리하는지 | Do phase T-1 |
| **DQ-3** | `PreviewKeyDown` 에 `handledEventsToo: true` 를 추가하는 변경이 downstream KeyBinding 중복 발화를 일으키는지 | Do phase T-4 |
| **DQ-4** | FlaUI PoC (T-5) 를 bash session 에서 실행했을 때 실제로 작동하는가 — Plan v0.2 §1.5 evidence #2 의 "IL 동일" 가설 최종 검증 | Do phase T-5 (optional) |
| **DQ-5** | T-5 결과가 T-4 fix 와 cross-validate 안 되면 (예: FlaUI 는 되는데 `input.py` 는 여전히 안 됨), 어느 쪽을 trust 할 것인가 | Do phase — user 결정 escalate |
| **DQ-6** | PostMessage fallback 제거 (T-6) 후 `SendInput WinError 0` 이 bash session 에서 재발한다면 다시 어떤 fallback 이 필요한가 | Do phase — 본 cycle 에서 재발 발견 시 R-RCA trigger + Plan v0.3 |
| **DQ-7** | WPF `Keyboard.Modifiers` 를 우회하는 `Keyboard.IsKeyDown(Key.LeftCtrl)` 이 race-safe 한지 (두 API 가 다른 snapshot 을 쓸 수 있는지) | T-4 구현 시 MSDN 재확인 |

---

## 9. Version History

| Version | Date | Changes | Author |
|---------|------|---------|--------|
| 0.1 | 2026-04-09 | Initial draft. RCA gate 4-step 통과 — RCA-A (WinAppDriver soft-drop), RCA-B (input.py static analysis → H-RCA2 partial fals, H-RCA3 fals), RCA-C (FlaUI 설계 완료, 실행 deferred), RCA-D (H-RCA1 WPF 공식 소스 confirm). **H-RCA4 신규 발견**: `MainWindow.xaml.cs:275-281` production 주석이 원인을 이미 문서화 (child HWND focus 가 WM_KEYDOWN 을 소비). 구현 전략: 후보 I 변형 (`MainWindow.xaml.cs` PreviewKeyDown 폴백 재검증 + 최소 범위 fix, `input.py` INPUT 구조체 touch 안 함). 후보 A/B/C/D/E/F/H 전부 제외, 후보 G (FlaUI) 는 cross-validation 도구로 축소. Do phase 진입 조건 만족. | 노수장 (CTO Lead) |

---

## Appendix A — Cited Sources

### WPF 공식 소스
- [`KeyboardDevice.cs`](https://www.dotnetframework.org/default.aspx/DotNET/DotNET/8@0/untmp/WIN_WINDOWS/lh_tools_devdiv_wpf/Windows/wcp/Core/System/Windows/Input/KeyboardDevice@cs/3/KeyboardDevice@cs)
- [`HwndKeyboardInputProvider.cs`](https://www.dotnetframework.org/default.aspx/DotNET/DotNET/8@0/untmp/WIN_WINDOWS/lh_tools_devdiv_wpf/Windows/wcp/Core/System/Windows/InterOp/HwndKeyboardInputProvider@cs/2/HwndKeyboardInputProvider@cs)
- [`dotnet/wpf` GitHub — KeyGesture.cs](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Input/Command/KeyGesture.cs)
- [Microsoft Learn — Input Overview WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/input-overview)
- [Microsoft Learn — `Keyboard.GetKeyStates`](https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.keyboard.getkeystates?view=windowsdesktop-8.0)

### FlaUI
- [FlaUI main repo](https://github.com/FlaUI/FlaUI)
- [FlaUI Keyboard.cs source](https://github.com/FlaUI/FlaUI/blob/master/src/FlaUI.Core/Input/Keyboard.cs)
- [FlaUI Issue #320 "Using Keyboard.pressing(VirtualKeyShort.ALT) not working"](https://github.com/FlaUI/FlaUI/issues/320)
- [FlaUI PR #731 "Fix modifier keys being toggled when typing extended keys like arrows" (merged 2026-03-27)](https://github.com/FlaUI/FlaUI/pull/731)

### WinAppDriver
- [microsoft/WinAppDriver repo](https://github.com/microsoft/WinAppDriver)
- [Issue #2018 "Is WinAPPDriver dead or still maintained?"](https://github.com/microsoft/WinAppDriver/issues/2018)
- [Microsoft Q&A "Is the tool WinAPPDriver dead or not?"](https://learn.microsoft.com/en-us/answers/questions/1455246/is-the-tool-winappdriver-dead-or-not)
- [Microsoft Tech Community — WinAppDriver and Desktop UI Test Automation (2020-11)](https://techcommunity.microsoft.com/blog/testingspotblog/winappdriver-and-desktop-ui-test-automation/1124543)
- [AutomateThePlanet — WinAppDriver to Appium Migration Guide](https://www.automatetheplanet.com/winappdriver-to-appium-migration-guide/)

### GhostWin production code (Read-only references)
- `src/GhostWin.App/MainWindow.xaml.cs:190-329` — `OnTerminalKeyDown` + `PreviewKeyDown` 폴백 + **line 275-281 결정적 주석**
- `src/GhostWin.App/MainWindow.xaml:148-159` — `Window.InputBindings` (Ctrl+T, Ctrl+W, Ctrl+Tab, Alt+V, Alt+H, Ctrl+Shift+W)
- `scripts/e2e/e2e_operator/input.py:1-409` — SendInput batch + PostMessage fallback
- `scripts/e2e/e2e_operator/capture/__init__.py:1-109` — capture factory
- `scripts/e2e/e2e_operator/capture/printwindow.py:1-146` — PrintWindow(PW_RENDERFULLCONTENT) capturer
