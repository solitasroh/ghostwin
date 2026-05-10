# GhostWin Engine Tests

VS 솔루션 내에서 C++ 엔진 테스트 실행.

## 사용 가능한 테스트

| 이름 | 설명 |
|------|------|
| `vt_core_test` | VtCore C++ wrapper 기본 동작, 한글 cell, mouse shape callback |
| `vt_bridge_cell_test` | C bridge row/cell iterator, style/color/cursor contract |
| `conpty_integration_test` | ConPTY + VtCore integration, 입력/resize/한글 roundtrip |
| `dx11_render_test` | DX11 renderer smoke, swapchain, glyph atlas |
| `render_state_test` | RenderState dirty-row, resize/content 보존, reader snapshot stress |
| `surface_manager_state_test` | RenderSurface surface-local visual invalidation + resize request contract |
| `session_manager_thread_safety_test` | SessionManager registry lookup/mutation lock contract |
| `session_visual_state_test` | selection/IME visual snapshot value-copy contract |
| `tsf_init_test` | TSF COM 초기화 |
| `quad_korean_test` | Headless WARP 기반 한글 glyph quad 생성 |

`vt_minimal_test`는 `vt_core_test`와 중복되어 제거했다. `conpty_benchmark`는 테스트가 아니라 수동 성능 벤치마크였으므로 테스트 체계에서 제거했다.

## 실행 방법

### VS GUI
프로젝트 Properties → Configuration Properties → User Macros → `GhostWinTestName` 추가 (값: 테스트 이름).

### MSBuild CLI
```
msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:GhostWinTestName=vt_core_test /p:Configuration=Debug
```

프로젝트 파일이 자체적으로 repo root 를 계산하므로, 이제 `SolutionDir` 을 따로 넘기지 않아도 된다.

빌드 결과: `build\tests\Debug\{testname}.exe`

### 모든 테스트 실행

권장 실행은 루트의 단일 entrypoint를 사용한다.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\test_automation.ps1 `
  -Suite Native `
  -Configuration Debug
```

내부적으로 위 표의 10개 테스트를 하나씩 빌드하고 `build\tests\Debug\{testname}.exe`를 실행한다.

### 수동 반복 실행

```powershell
$tests = "vt_core_test","vt_bridge_cell_test","conpty_integration_test",
         "dx11_render_test","render_state_test","surface_manager_state_test",
         "session_manager_thread_safety_test","session_visual_state_test",
         "tsf_init_test","quad_korean_test"
foreach ($t in $tests) {
    msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:GhostWinTestName=$t /p:Configuration=Debug
    & "build\tests\Debug\$t.exe"
}
```
