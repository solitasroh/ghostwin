# ADR-013: Embedded Shader Source

## Status
Accepted

## Context
DX11 렌더러가 셰이더 파일(`shader_vs.hlsl`, `shader_ps.hlsl`)을 CWD 기준 상대 경로(`src/renderer/shader_vs.hlsl`)로 로딩했음. 프로젝트 루트에서 실행하면 동작하지만, 다른 디렉토리에서 실행하면 "Shader files not found" 오류로 렌더러 생성 실패 → 화면 빈 화면.

`run_wpf.ps1`, Claude Code `!` 명령, 파일 탐색기에서 직접 실행 등 CWD가 프로젝트 루트가 아닌 경우 렌더링이 완전히 실패함.

## Decision
셰이더 소스를 C++ raw string literal (`R"hlsl(...)hlsl"`)로 `dx11_renderer.cpp`에 직접 임베드.

- 런타임 파일 I/O 제거 → CWD 무관
- 빌드 시 컴파일 → 셰이더 문법 오류를 빌드 타임에 검출 가능
- `.hlsl` 파일은 참조용으로 유지 (에디터 syntax highlighting, diff 용도)

## Alternatives Considered
1. **exe 경로 기준 상대 경로 (GetModuleFileName)** — 동작하지만 여전히 파일 배포 필요
2. **셰이더 파일을 exe 출력 디렉토리에 복사 (빌드 스텝)** — CMake 복잡도 증가, 배포 시 파일 누락 위험
3. **사전 컴파일 .cso 파일** — 바이트코드 배포로 소스 노출 방지가 필요 없는 프로젝트에서 과도

## Consequences
- 셰이더 수정 시 `.hlsl` 파일과 C++ 임베드 문자열 양쪽 동기화 필요
- 셰이더가 ~120줄 이내로 작으므로 유지보수 부담 최소
- 어떤 디렉토리에서 실행해도 렌더링 정상 동작 보장
