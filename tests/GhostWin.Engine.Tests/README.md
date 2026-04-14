# GhostWin Engine Tests

VS 솔루션 내에서 C++ 엔진 테스트 실행.

## 사용 가능한 테스트

| 이름 | 설명 |
|------|------|
| `vt_core_test` | VtCore C++ 래퍼 (기본, 10 tests) |
| `vt_bridge_cell_test` | Cell iterator API |
| `vt_minimal_test` | 최소 API sanity check |
| `conpty_integration_test` | ConPTY + VtCore integration (10 tests) |
| `conpty_benchmark` | ConPTY 성능 벤치마크 |
| `dx11_render_test` | DX11 렌더링 |
| `render_state_test` | RenderState dirty-row |
| `quad_korean_test` | 한글 glyph 렌더링 |
| `tsf_init_test` | TSF COM 초기화 |
| `ghostty_raw_test` | ghostty C API 직접 호출 |

## 실행 방법

### VS GUI
프로젝트 Properties → Configuration Properties → User Macros → `GhostWinTestName` 추가 (값: 테스트 이름).

### MSBuild CLI
```
msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:GhostWinTestName=vt_core_test /p:Configuration=Debug
```

빌드 결과: `build\tests\Debug\{testname}.exe`

### 모든 테스트 실행
```powershell
$tests = "vt_core_test","conpty_integration_test","vt_bridge_cell_test"
foreach ($t in $tests) {
    msbuild tests\GhostWin.Engine.Tests\GhostWin.Engine.Tests.vcxproj /p:GhostWinTestName=$t /p:Configuration=Debug
    & "build\tests\Debug\$t.exe"
}
```
