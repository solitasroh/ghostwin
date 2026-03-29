# ADR-008: 2-Pass 렌더링 (배경 → 텍스트) 채택

- **상태**: 채택
- **날짜**: 2026-03-30
- **관련**: Phase 3 dx11-rendering, 오픈소스 터미널 리서치

## 배경

단일 패스에서 셀 순서대로 배경+텍스트를 그리면, 셀 N+1의 배경 쿼드가 셀 N의 전각(CJK) 글리프를 덮어씌워 글리프가 잘린다.

## 문제

```
단일 패스: bg(0) → text(0) → bg(1) → text(1) → ...
                                 ↑
                    bg(1)이 text(0)의 전각 글리프 우측을 덮음
```

CJK 전각 문자는 2셀 폭으로 래스터화되지만, 다음 셀의 배경이 그 위에 그려진다.

## 결정

2-pass 렌더링을 채택한다:

```
Pass 1: bg(0) → bg(1) → bg(2) → ... → bg(N)   (전체 배경)
Pass 2: text(0) → text(1) → ... → text(N)       (전체 텍스트)
Pass 3: cursor                                   (최상위)
```

## 근거

### 오픈소스 터미널 리서치 결과 (2026-03-30)

| 터미널 | 패턴 | 비고 |
|--------|------|------|
| Windows Terminal | drawBackground → drawText → drawCursor | ShadingType 순서 정렬 |
| Alacritty | clear → draw_cells → draw_rects | 2-pass + 사각형 패스 |
| WezTerm | Layer0(bg) → Layer1(text) → Layer2(overlay) | 3-레이어 쿼드 할당 |
| Ghostty | cell_bg → cell_text (별도 파이프라인) | 별도 버퍼 + 별도 Draw Call |

**4개 프로젝트 모두 배경을 먼저, 텍스트를 나중에 그린다.**

### 글리프 오버플로 허용

4개 프로젝트 모두 **글리프가 셀 경계를 넘어가는 것을 허용**한다. Scissor rect 클리핑을 사용하지 않으며, 배경이 먼저 그려지므로 오버플로된 글리프가 자연스럽게 배경 위에 표시된다.

## 대안

| 방안 | 판정 | 이유 |
|------|:----:|------|
| 단일 패스 (셀별 bg+text) | 기각 | CJK 글리프 클리핑 발생 |
| Scissor rect 셀별 클리핑 | 기각 | 4개 주요 터미널 모두 미사용, 성능 저하 |
| **2-pass (bg → text)** | **채택** | 업계 표준, 글리프 오버플로 자연 해결 |
| ShadingType 정렬 단일 DrawCall | 보류 | WT 방식, Phase 4에서 최적화 시 검토 |

## 영향

- DrawIndexedInstanced 호출 횟수: 변화 없음 (여전히 1회, 인스턴스 순서만 변경)
- 인스턴스 배열: 배경 쿼드가 앞, 텍스트 쿼드가 뒤에 배치
- CJK, 디센더, 리가처 오버플로 모두 자연스럽게 해결
