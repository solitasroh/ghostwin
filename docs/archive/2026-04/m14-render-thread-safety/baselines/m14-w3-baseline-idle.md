# M-14 W3 Baseline — Idle Release (2026-04-21, post-force_all_dirty)

> **한 줄 요약**: W3 가 `force_all_dirty()` 를 `visual_epoch` 로 치환한 직후, 1-pane idle Release 상태에서 60초 동안 실제 렌더된 프레임은 **4건**. W1 의 1643건 대비 **99.76% 감소**. render 루프는 여전히 ~1600회 iterate 하지만 그 중 1639회는 `!vt_dirty && !visual_dirty && !resize_applied` 경로로 즉시 return. **Plan W3 완료 조건 — "idle baseline W1 대비 유의미한 감소" 를 정량 충족**.

## 이 문서의 역할

- W3 커밋 (`d43abb6`) 직후 idle baseline 기록 — W1 (`476f4f2`) 과 **1:1 전후 비교**
- Design 5.2 가설 ("`force_all_dirty` 제거가 start_us 95.6% 를 없앤다") 의 **정량 확정 증거**
- W4 (clean-surface skip-present 보강) / W5 (외부 비교) 의 in-scope 판정 근거
- 남은 부채 (startup 4 frame 의 start_us 여전히 ~12ms) 의 방향 설정

## 수집 환경

W1 baseline 과 동일한 하드웨어 + 동일한 빌드 환경. Git SHA 만 다름:

| 항목 | W1 | W3 |
|------|------|------|
| Git SHA | `19db612` | `d43abb6` |
| Scenario | idle 1-pane 60s | idle 1-pane 60s |
| Configuration | Release x64 | Release x64 |
| 수집 시각 | 2026-04-21 07:49:57 | 2026-04-21 17:50:07 |

하드웨어 (Intel Core Ultra 7 155H / 31.5GB RAM / 144Hz internal panel) 는 [[m14-w1-baseline-idle]] 의 §2.1 참조.

## 핵심 수치 비교

| 지표 | W1 (pre-W3) | W3 | 변화 |
|------|---:|---:|---:|
| **render 된 frame 수** | 1,643 | **4** | **−99.76%** |
| 실질 render FPS (iter / 60s) | 27.4 | 0.067 | −99.76% |
| total_us avg | 13,544 μs | 12,678 μs | −6% (rendered frames 만 비교) |
| start_us avg | 12,924 μs | 11,676 μs | −10% (startup 포함 평균) |

> ⚠️ **해석 주의**: W3 의 avg/p95/max 는 **4개 샘플 기준** 이라 통계적 대표성 없음. 의미 있는 지표는 **sample_count 자체** (얼마나 자주 render 했는가).

## 4개 샘플 내역 (CSV raw)

| frame | vt_dirty | visual_dirty | resize | start_us | total_us | quads | 해석 |
|---:|:---:|:---:|:---:|---:|---:|---:|------|
| 1 | 1 | **1** | 0 | 11,447 | 12,393 | 2,388 | 초기 paint (startup 시 `visual_epoch` bump + VtCore 초기 prompt 내용) |
| 6 | 1 | 0 | 0 | 9,743 | 11,478 | 2,403 | VT 변화 (셸 초기화 단계 추정) |
| 45 | 1 | 0 | 0 | 15,623 | 16,384 | 2,445 | VT 변화 (프롬프트/커서 후속 업데이트) |
| 50 | 1 | 0 | 0 | 9,890 | 10,456 | 2,497 | VT 변화 (마지막 안정 상태 진입) |

frame 51 이후 60초 끝까지 **렌더 0건**. skip 경로가 정상 작동.

## skip 메커니즘 검증

W3 의 render_surface 는 다음 3가지 신호 중 하나도 true 가 아니면 즉시 return:

```cpp
const bool vt_dirty = state.start_paint(...);
const uint32_t visual_epoch = session->visual_epoch.load(acquire);
const bool visual_dirty = (surf->last_visual_epoch != visual_epoch);

if (!vt_dirty && !visual_dirty && !resize_applied) {
    return;  // no build, no draw, no Present
}
```

관찰된 패턴:

- **Frame 1**: `visual_dirty=1` (startup 시 `SessionManager::add` 의 `visual_epoch.fetch_add(1)` 때문). 이 한 번의 paint 후 `surf->last_visual_epoch` 가 `visual_epoch` 과 같아짐.
- **Frame 6/45/50**: `vt_dirty=1` — VtCore 의 `for_each_row` 가 dirty row 를 반환한 경우. 주로 shell 이 prompt/cursor 를 업데이트할 때.
- **나머지 프레임**: 3가지 모두 false → 즉시 return. render 루프는 여전히 `Sleep(16)` 를 돌리지만 `render_surface` 진입 직후 skip.

## Plan W3 완료 조건 판정

| 조건 (Plan 7.3) | 상태 |
|------|:---:|
| idle 시나리오 baseline 이 W1 대비 **유의미한 감소** (CSV 로 증명) | ✅ **1643 → 4 (99.76% 감소)** |
| selection / IME / activate 동작 회귀 없음 (수동 확인 + 기존 e2e) | ⏳ 수동 확인 필요 — [`render_state_test` 17/17 PASS](../../../build/tests/Debug) 는 전제 확보, 앱 레벨 e2e 는 별도 세션 |

**단위 테스트**: `render_state_test.exe` Debug 빌드 → 17/17 PASS (W2 stress 포함).

## Design 5.2 가설 확정

Design 5.2 는 다음을 예상했다:

> 👉 **W3 가 `force_all_dirty()` 를 제거하면 이 12.9ms 대부분이 사라질 것으로 예상**.
> W3 후 baseline 에서 `start_us` 가 수십~수백 μs 로 떨어지면 가설 확정.

**실측 결과는 예상보다 강력**:

| 가설 | 실측 |
|------|------|
| `start_us` 가 수십~수백 μs 로 감소 | start_us 자체는 render 발생 시 여전히 ~12ms (VtCore for_each_row 비용은 남음) |
| **대신, render 자체가 99.76% 감소** | idle 프레임은 render_surface 진입 직후 `return` → start_us 가 로그에 찍히지 않음 |

즉 **start_us 를 깎은 게 아니라, start_us 를 호출할 이유 자체를 제거함**. Design 의 예측보다 더 근본적인 해결.

## W1 → W3 CPU 사용량 예상 (PC/측정 부재로 정성)

- W1: 1,643 frames × 13.5ms work = **22.2초 CPU time in 60s** → 약 37% CPU (단일 코어 기준)
- W3: 4 frames × ~12ms work + 1,639 × ~skip cost (<100μs?) = **~48ms + ~164ms ≈ 0.2초 CPU time in 60s** → 약 **0.3% CPU**

🔴 **이 추정은 측정이 아님**. Task Manager / Process Explorer 로 실제 CPU 기록 필요 (Plan NFR "Idle CPU ≤ 2%" 판정 조건). 다음 세션에서 병행 권장.

## 남은 부채 (W4 / W5 범위)

이 baseline 은 **idle only**. 다음 실측이 필요:

| 시나리오 | 필요 측정 | 예상 효과 |
|----------|----------|----------|
| load (heavy output) | W3 후 재측정 | VT dirty 신호가 정확히 필요할 때만 render → throughput 유지 + idle 저하 없음 |
| 4-pane resize | W4 완료 후 | clean-surface skip-present 가 pane 별 Present 비용 누적을 해소 |
| Idle CPU 절대값 | Task Manager 병행 | 2% NFR budget 통과 검증 |
| 경쟁사 비교 | W5 | WT / WezTerm / Alacritty 대비 상대 위치 |

## 관찰된 startup 4 frame 의 start_us ~12ms

Frame 1/6/45/50 모두 start_us ~10-16ms. W1 의 start_us avg (12.9ms) 와 동일 수준이다. 의미:

- W3 가 제거한 것은 **매 프레임 강제 전체 복사** 가 아니라 **force_all_dirty 호출** 뿐
- 실제 render 가 일어나는 순간은 여전히 start_paint 의 `for_each_row` (Zig FFI) + dirty row 복사 비용 발생
- 즉 **VT 가 실제로 dirty 를 보고하는 프레임** 에서는 여전히 비싼 경로

이건 W4 에서 다룰 건 아니지만, follow-up 으로 고려할 여지:
- `start_paint` 내부의 `vt_bridge_update_render_state_no_reset` + `for_each_row` FFI 오버헤드 측정 필요
- 대안: VT 가 빈 dirty 를 보고할 때 FFI 입구에서 빠져나오는 fast path

M-14 범위 안에선 idle 문제가 해소됐으니 이 추가 최적화는 별도 milestone 으로 미룬다.

## 산출물

```
docs/04-report/features/m14-baseline/idle-20260421-175007/
├─ ghostwin.log       (raw app log, gitignore)
├─ render-perf.csv    (4 rows × 12 columns)
└─ summary.txt        (avg/p95/max — statistical meaning limited to n=4)
```

## 요약 한 줄

> **W3 가 `force_all_dirty()` 를 제거한 직후, 1-pane idle 60s 에서 실제 render 는 4 frame (startup). W1 의 1643 frame 대비 99.76% 감소. Plan W3 완료 조건 (idle baseline 유의미 감소) 정량 충족. start_us 자체는 render 가 일어나는 프레임에서 여전히 ~12ms 이므로, 추가 최적화는 별도 milestone 으로 후속 고려.**

## 변경 이력

| 버전 | 일시 | 내용 | 커밋 |
|------|------|------|------|
| 1.0 | 2026-04-21 | W3 (d43abb6) 직후 idle Release baseline 기록 + W1 대비 비교 | — |
