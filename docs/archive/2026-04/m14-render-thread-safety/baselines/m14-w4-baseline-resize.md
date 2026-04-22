# M-14 W4 Baseline — 1-pane Automated Resize Release (2026-04-21)

> **한 줄 요약**: W3 skip 로직 + 자동화된 Win32 SetWindowPos 루프 (500ms 간격 2개 크기 왕복, 60s) 로 1-pane resize 시나리오 실측. 119 resize 이벤트 → 122 render 발생, 나머지 ~1261 iteration 은 모두 skip. **p95 total_us = 34.3ms — NFR 33ms budget 을 1.3ms 초과** (가설 budget). 4-pane 선형 증가 여부는 수동 GUI 테스트 필요 — 본 baseline 은 그 follow-up 의 1-pane reference.

## 이 문서의 역할

- W3 skip 로직이 resize 경로와 통합되어 정상 작동함을 정량 확인
- `measure_render_baseline.ps1` 의 자동 resize loop (W4 에서 추가) 의 첫 산출물
- Plan W4 완료 조건 중 "4-pane 스트레스 재현 절차 고정" 항목의 1-pane 기준선
- 1-pane resize 가 이미 NFR 경계에 있으므로 **4-pane 은 예상 초과** — follow-up 조건 기록

## W4 script 개선 사항

`scripts/measure_render_baseline.ps1` 가 3가지 확장:

1. **`-Panes N` informational param**: 출력 폴더명 + summary 에 pane 수 라벨
2. **Win32 `SetWindowPos` 자동화**: `-Scenario resize` 시 user drag 대신 `SetWindowPos` 로 프로그래밍 가능한 윈도우 리사이즈 루프 (500ms 간격 2 사이즈 왕복)
3. **summary 에 `observed max panes` 표시**: CSV 의 `panes` 컬럼 max 와 declared `-Panes` 교차 확인

## 수집 환경

| 항목 | 값 |
|------|------|
| Git SHA | `d43abb6` (W3-b: force_all_dirty 제거 + skip 로직) |
| Scenario | `resize` 자동화 (Win32 SetWindowPos, 500ms 간격) |
| Panes | 1 |
| Configuration | Release x64 |
| 자동 resize 사이즈 | `1024×768` ↔ `1400×900` (cap_cols 경계 교차) |
| Duration | 60s (119 resize transitions) |
| 수집 시각 | 2026-04-21 18:04:03 |

하드웨어는 [[m14-w1-baseline-idle]] §2.1 참조 (Core Ultra 7 155H 등).

## 수치 결과

| 지표 | avg (μs) | p95 (μs) | max (μs) |
|------|---------:|---------:|---------:|
| `start_us` | 20,263 | 32,326 | 37,330 |
| `build_us` | 69 | 83 | 666 |
| `draw_us` | 191 | 1,089 | 2,155 |
| `present_us` | 531 | 1,631 | 9,176 |
| **`total_us`** | **21,061** | **34,290** | **37,751** |

### 파생 지표

| 지표 | 값 |
|------|------|
| 자동 resize transitions | 119 |
| 실제 render 된 frame 수 | **122** (119 resize + 1 startup visual_dirty + 2 기타 VT dirty) |
| skip 된 iteration 수 | ~1,261 (render loop 의 iteration count 약 1,383 중 resize 외 모두 skip) |
| observed pane count (CSV) | 1 (선언과 일치) |

## skip/render 분포 — W3 로직이 resize 에서도 정상 작동

```
iteration count      ~1,383 (60s × ~23 iter/sec)
├─ render 된 frame     122 (8.8%)
│  ├─ resize=1        119
│  ├─ visual=1          1 (startup)
│  └─ vt=1 only         2 (shell 초기 응답)
└─ skipped iteration  ~1,261 (91.2%)
```

즉 W4 로 자동 resize 반복해도 **resize 가 없는 idle 구간은 여전히 완전 skip**. W3 skip 로직과 resize 경로의 통합이 정상.

## Plan NFR 대비 (가설 budget)

| NFR | 가설 Budget | 1-pane 실측 p95 | 평가 |
|-----|-------------|------------------|------|
| Resize p95 frame time (4-pane) | **≤ 33,000 μs** | 34,290 μs (1-pane) | 🟡 **1-pane 에서 이미 1.3ms 초과** |

> ⚠️ NFR 은 4-pane 기준인데 이 baseline 은 1-pane. **4-pane 은 선형 증가 시 ~130ms 로 예상** — 명확한 초과 가능성.

## 34.3ms p95 내부 구성 이해

| 구성 요소 | 비중 (avg 기준) |
|-----------|:---------------:|
| `start_us` (render_surface entry → start_paint 완료) | 96% (20.3ms / 21.1ms) |
| `build_us` (QuadBuilder) | 0.3% |
| `draw_us` (upload + draw) | 0.9% |
| `present_us` (Present 블록) | 2.5% |

**`start_us` 지배는 idle 과 동일한 구조적 원인** — 다만 origin 이 다름:

- [[m14-w1-baseline-idle|W1 idle]]: `force_all_dirty()` → 매 프레임 전체 row 복사 → 이걸 W3 가 제거
- W4 resize: `TerminalRenderState::resize()` 가 `_api.reshape()` + 모든 row 를 dirty 로 마킹 → 다음 start_paint 에서 전체 row 복사 (정합성상 필요)

즉 **resize 경로는 force_all_dirty 제거와 무관하게 여전히 "모든 row dirty"** 를 필요로 한다. 이건 correctness 요구이지 최적화 대상이 아님. 그러나 4-pane 에서 각 surface resize 가 같은 비용을 발생시키므로 선형 증가가 문제.

## 4-pane 추정 — 수동 테스트 필요

이 baseline 만으로는 4-pane 을 판정할 수 없음. 가능한 시나리오:

| 시나리오 | 예상 |
|---|---|
| **4-pane all dirty during resize** | ~130ms per cycle (1-pane × 4, 선형) — 7.4fps 체감 stutter |
| **4-pane + W3 skip** | resize 중에도 4 × 34ms = 136ms — skip 무효 (모두 resize_applied=true) |
| **Follow-up DXGI tearing mode** | `Present(0, ALLOW_TEARING)` 로 VBlank 대기 제거 — 133ms 중 Present 부분만 감소 (작음) |

**판정**: 4-pane 의 **start_paint 자체 비용 선형 증가** 는 DXGI tearing 으로 해결 안 됨. VtCore FFI 비용 + reshape 전체 복사 비용이 근본 원인. M-14 범위 밖 follow-up 의 실제 표적은 **start_paint 경로 최적화** 이지 Present 정책이 아님.

## Plan W4 완료 조건 판정

| 조건 (Plan 7.4) | 상태 |
|------|:---:|
| 4-pane 스트레스 재현 절차 (measure_render_baseline.ps1 -Scenario resize 확장) | ✅ Win32 자동 resize 루프 추가 완료 (현재 1-pane 자동, 4-pane 은 pre-split 수동 준비 후 같은 script 사용) |
| W4 baseline CSV — pane 수 별 p95 frame time + present_ms | 🟡 **1-pane 확보** / 4-pane 수동 세션 필요 |
| p95 가 pane 수에 선형 증가 여부 판정 | ⏳ 4-pane 수집 후 |
| 선형 증가 시 follow-up milestone 기록 | ✅ **위 섹션에 pre-기록** — 원인은 Present 정책이 아니라 start_paint FFI + reshape 비용 |

## 산출물

```
docs/04-report/features/m14-baseline/resize-20260421-180403/
├─ ghostwin.log       (gitignored)
├─ render-perf.csv    (122 rows × 12 columns)
└─ summary.txt        (avg/p95/max + observed pane count)
```

## 남은 W4 작업 — 수동 GUI 세션에서 실행 가능

```powershell
# 1. GhostWin 런치 후 수동으로 Alt+V 등으로 4-pane 생성
# 2. 이미 생성된 상태로 script 재실행 (launch-from-script 는 빈 상태)
# 대안 A: GhostWin 에 pre-split 을 지원하는 CLI flag 가 있다면 그것 사용
# 대안 B: AutoHotkey / SendInput 으로 Alt+V 4회 자동화

# 4-pane resize baseline 수집 예시
.\scripts\measure_render_baseline.ps1 `
    -Scenario resize -DurationSec 60 `
    -Configuration Release -Panes 4
```

4-pane pre-split automation 은 범위 밖 (M-14 W4). 수동 세션 권장.

## 요약 한 줄

> **W3 의 skip 로직은 resize 경로에서도 정상 작동 — 자동 resize 60s 동안 1,261 iteration 중 ~1,261 을 skip, 122 건만 render. 그러나 1-pane resize p95 = 34.3ms 로 NFR 33ms budget 을 1.3ms 초과. 원인은 reshape 시 전체 row dirty + VtCore FFI + row 복사 (force_all_dirty 와 무관). 4-pane 선형 증가 가능성 높음 — follow-up milestone 의 표적은 Present 정책이 아니라 start_paint 경로 자체.**

## 변경 이력

| 버전 | 일시 | 내용 | 커밋 |
|------|------|------|------|
| 1.0 | 2026-04-21 | W4 1-pane 자동 resize baseline + 4-pane follow-up 조건 기록 | — |
