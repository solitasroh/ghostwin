# M-2 Engine Interop — Design 대비 구현 검증

> **빌드 상태**: ✅ `dotnet build GhostWin.slnx -c Release` — 경고 0, 오류 0 (1.34초)
> **검증 일시**: 2026-04-06 14:25

---

## 1. 파일 구조 검증

| Design 파일 | 구현 | 판정 |
|------------|------|:----:|
| `Interop/NativeEngine.cs` | ✅ 존재 (91줄) | ✅ |
| `Interop/NativeCallbacks.cs` | ✅ 존재 (81줄) | ✅ |
| `Interop/GwCallbacks.cs` | ❌ 별도 파일 없음 → `NativeEngine.cs`에 포함 | ⚠️ |
| `Interop/EngineService.cs` | ✅ 존재 (113줄) | ✅ |
| `Interop/TsfBridge.cs` | ✅ 존재 (80줄) | ✅ |
| `App/Controls/TerminalHostControl.cs` | ✅ 존재 (115줄) | ✅ |

> [!NOTE]
> Design은 `GwCallbacks.cs`를 별도 파일로 분리했으나, 구현은 `NativeEngine.cs` 상단에 포함. 기능적으로 동등하며 작은 구조체이므로 합리적.

---

## 2. NativeEngine.cs — 19개 API P/Invoke 검증

| # | C API (`ghostwin_engine.h`) | P/Invoke 구현 | 판정 |
|---|---------------------------|--------------|:----:|
| 1 | `gw_engine_create` | ✅ | ✅ |
| 2 | `gw_engine_destroy` | ✅ | ✅ |
| 3 | `gw_render_init` | ✅ | ✅ |
| 4 | `gw_render_resize` | ✅ | ✅ |
| 5 | `gw_render_set_clear_color` | ✅ | ✅ |
| 6 | `gw_render_start` | ✅ | ✅ |
| 7 | `gw_render_stop` | ✅ | ✅ |
| 8 | `gw_session_create` | ✅ | ✅ |
| 9 | `gw_session_close` | ✅ | ✅ |
| 10 | `gw_session_activate` | ✅ | ✅ |
| 11 | `gw_session_write` | ✅ | ✅ |
| 12 | `gw_session_resize` | ✅ | ✅ |
| 13 | `gw_tsf_attach` | ✅ | ✅ |
| 14 | `gw_tsf_focus` | ✅ | ✅ |
| 15 | `gw_tsf_unfocus` | ✅ | ✅ |
| 16 | `gw_tsf_send_pending` | ✅ | ✅ |
| 17 | `gw_session_count` | ✅ | ✅ |
| 18 | `gw_active_session_id` | ✅ | ✅ |
| 19 | `gw_poll_titles` | ✅ | ✅ |

**19/19 완전 일치** ✅

---

## 3. GwCallbacks 구조체 — 필드 순서 검증

| # | C 원본 (`ghostwin_engine.h`) | 구현 C# | 판정 |
|---|---------------------------|--------|:----:|
| 0 | `context` (void*) | `Context` (nint) | ✅ |
| 1 | `on_created` (GwSessionFn) | `OnCreated` (nint) | ✅ |
| 2 | `on_closed` (GwSessionFn) | `OnClosed` (nint) | ✅ |
| 3 | `on_activated` (GwSessionFn) | `OnActivated` (nint) | ✅ |
| 4 | `on_title_changed` (GwTitleFn) | `OnTitleChanged` (nint) | ✅ |
| 5 | `on_cwd_changed` (GwCwdFn) | `OnCwdChanged` (nint) | ✅ |
| 6 | `on_child_exit` (GwExitFn) | `OnChildExit` (nint) | ✅ |
| 7 | `on_render_done` (GwRenderDoneFn) | `OnRenderDone` (nint) | ✅ |

`LayoutKind.Sequential` + 8필드 순서 일치 ✅

---

## 4. NativeCallbacks.cs — 콜백 마셜링 검증

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| `[UnmanagedCallersOnly(CallConvCdecl)]` | ✅ | ✅ 7개 모두 적용 | ✅ |
| Dispatcher 마셜링 (6개) | `BeginInvoke` | `BeginInvoke` | ✅ |
| `on_render_done` 직접 호출 | Interlocked | `_context?.OnRenderDone?.Invoke()` | ✅ |
| `len` = wchar_t 문자 수 주석 | ✅ (`ghostwin_engine.cpp L87` 확인) | ❌ **주석 없음** | ⚠️ |
| `Cleanup()` 메서드 | ❌ Design에 없음 | ✅ **추가됨** (장점) | ✅+ |
| null 체크 패턴 | `context == null \|\| disp == null` | `c?.OnXxx == null \|\| d == null` | ✅ |

---

## 5. EngineService.cs — IEngineService 구현 검증

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| `Initialize(GwCallbackContext)` | ✅ | ✅ | ✅ |
| `Shutdown()` | ✅ | ✅ (RenderStop + Destroy + Cleanup + Free) | ✅ |
| `Dispose()` → `Shutdown()` | ✅ | ✅ + `SuppressFinalize` | ✅ |
| GCHandle 핀닝 | ✅ | ✅ `GCHandle.Alloc(this)` | ✅ |
| 함수 포인터 `&NativeCallbacks.Xxx` | 7개 | 7개 모두 등록 | ✅ |
| `RenderSetClearColor` | ✅ | ✅ | ✅ |
| `PollTitles` | ✅ | ✅ | ✅ |
| `SessionCount`, `ActiveSessionId` | 프로퍼티 | ✅ **프로퍼티** (M-1에서 메서드였던 것 수정됨) | ✅ |
| `WriteSession(ReadOnlySpan<byte>)` | ✅ | ✅ `unsafe fixed (byte* ptr)` | ✅ |

### IEngineService 인터페이스 (Core) — Design 시그니처 대비

| API | Design 시그니처 | 구현 시그니처 | 판정 |
|-----|---------------|-------------|:----:|
| `RenderStart` | `int` (return) | `void` | ⚠️ |
| `RenderStop` | `void` | `void` | ✅ |
| `GwCallbackContext` 프로퍼티 | `required Action<>` | `Action<>?` (nullable) | ⚠️ |

> [!NOTE]
> `RenderStart`의 반환형이 `void`인 것은 C API(`gw_render_start`)가 `int`를 반환하므로 에러 코드를 사라지게 합니다. 현재 EngineService 구현도 `void`로 래핑하므로 에러가 무시됩니다. M-3에서 에러 처리를 추가할 때 `int`로 변경이 필요할 수 있습니다.

---

## 6. TsfBridge.cs — Design 대비 검증

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| 네임스페이스 | `GhostWin.Interop` | `GhostWin.Interop` | ✅ |
| `NativeEngine` 직접 호출 (IEngineService 미경유) | ✅ (Review Fix) | ✅ `NativeEngine.gw_tsf_send_pending` | ✅ |
| `HwndSource` 기반 | ✅ | ✅ | ✅ |
| `WM_USER + 50` 핸들링 | ✅ | ✅ | ✅ |
| ADR-011 50ms 포커스 타이머 | ✅ | ✅ | ✅ |
| `IDisposable` | ✅ | ✅ | ✅ |

---

## 7. TerminalHostControl.cs — Design 대비 검증

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| 네임스페이스 | `GhostWin.App.Controls` | `GhostWin.App.Controls` | ✅ |
| HwndHost 상속 | ✅ | ✅ | ✅ |
| `RenderResizeRequested` 이벤트 | ✅ (M-3에서 ICommand로 전환) | `Action<uint, uint>` 이벤트 | ✅ |
| DPI 처리 (`VisualTreeHelper.GetDpi`) | ✅ | ✅ | ✅ |
| PoC 코드 이전 | ✅ | ✅ 거의 동일 | ✅ |

---

## 8. MainWindow.xaml.cs — 통합 검증

| 항목 | Design | 구현 | 판정 |
|------|--------|------|:----:|
| 엔진 DI 주입 | `Ioc.Default.GetRequiredService` | ✅ | ✅ |
| 콜백 컨텍스트 설정 | 7종 모두 와이어링 | ✅ 7종 모두 | ✅ |
| `RenderInit` → `RenderSetClearColor` → `CreateSession` → `RenderStart` | ✅ | ✅ 순서 일치 | ✅ |
| TSF 한글 입력 연결 | ✅ | ✅ TsfBridge + TsfAttach + TsfFocus | ✅ |
| 키보드 입력 VT 시퀀스 | `PreviewKeyDown` + `PreviewTextInput` | ✅ | ✅ |
| Ctrl+C 처리 | ✅ | ✅ `\x03` | ✅ |

### 🟡 GetEngineHandle() — 리플렉션 해킹

[MainWindow.xaml.cs:100-111](file:///d:/work/private/ghostwin/src/GhostWin.App/MainWindow.xaml.cs#L100-L111):
```csharp
var field = typeof(EngineService).GetField("_engine",
    BindingFlags.NonPublic | BindingFlags.Instance);
return (nint)(field?.GetValue(es) ?? IntPtr.Zero);
```

> [!WARNING]
> TsfBridge에 엔진 핸들을 전달하기 위해 **리플렉션으로 private 필드에 접근**합니다. 이는 취약하고 리팩토링에 깨지기 쉽습니다.
> **권고**: `IEngineService`에 `nint EngineHandle { get; }` 프로퍼티를 추가하거나, `EngineService`에 `internal nint Handle` 프로퍼티를 노출.

---

## 9. M-1 GAP 수정 확인

| M-1 GAP | 수정됨? | 상태 |
|---------|:------:|:----:|
| 🔴 `SessionInfo` → `ObservableObject` | ✅ `[ObservableProperty]` 포함 | ✅ |
| 🔴 `IEngineService` 3개 API 누락 | ✅ 모두 추가 (`RenderSetClearColor`, `PollTitles`, `Shutdown`) | ✅ |
| 🟡 메서드명 불일치 | ✅ `CreateSession`, `CloseSession` 등으로 통일 | ✅ |
| 🟡 `SessionCount`/`ActiveSessionId` 메서드→프로퍼티 | ✅ 프로퍼티로 변경 | ✅ |
| 🟡 `GCHandle` 핀닝 | ✅ EngineService에 구현 | ✅ |
| 🟡 `Sidebar.Width` 기본값 250→200 | ❌ **미수정** (여전히 없음 — AppSettings 파일 미변경) | ⚠️ |
| 🟡 `TitlebarSettings` 누락 | ❌ **미추가** | ⚠️ |

---

## 10. M-2 완료 기준 체크 (Design §3.2.8)

| 기준 | 빌드 검증 | 런타임 검증 필요 |
|------|:--------:|:-----------:|
| 터미널 1화면 렌더링 (DX11 + ClearType) | ✅ 코드 완비 | 🔲 실행 필요 |
| 키보드 입력 → VT → session_write | ✅ 코드 완비 | 🔲 실행 필요 |
| 한글 TSF 입력 (확정 텍스트) | ✅ TsfBridge 연결 | 🔲 실행 필요 |
| 7종 콜백 연결 확인 | ✅ 모두 와이어링 | 🔲 로그 확인 필요 |
| V3 벤치마크 < 1ms | — | 🔲 벤치 실행 필요 |

---

## 11. 판정 요약

| 카테고리 | 판정 |
|---------|:----:|
| 빌드 성공 | ✅ |
| NativeEngine 19개 API | ✅ 완전 일치 |
| GwCallbacks 필드 순서 | ✅ 완전 일치 |
| 7종 콜백 마셜링 | ✅ Design 일치 |
| EngineService 함수 포인터 등록 | ✅ Design 일치 |
| TsfBridge 직접 호출 | ✅ Design (Review Fix) 일치 |
| TerminalHostControl | ✅ PoC 이전 완료 |
| M-1 GAP 해소 | ✅ 5/7 해소, 2개 잔존 (사소) |

### 조치 필요 항목 (3건)

| # | 심각도 | 항목 | 조치 |
|---|:------:|------|------|
| 1 | 🟡 | `GetEngineHandle()` 리플렉션 해킹 | `EngineService`에 `internal nint Handle` 프로퍼티 노출 |
| 2 | 🟢 | `OnTitleChanged`/`OnCwdChanged` len 단위 주석 누락 | Design에 추가된 "wchar_t 문자 수" 주석 반영 |
| 3 | 🟢 | `Sidebar.Width` 기본값, `TitlebarSettings` (M-1 잔존) | M-5 전까지 해결 |
