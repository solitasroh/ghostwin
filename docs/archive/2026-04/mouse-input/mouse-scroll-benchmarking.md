# Mouse Scroll Benchmarking: 5개 터미널 스크롤 처리 줄단위 분석

> **Author**: Claude + 노수장
> **Date**: 2026-04-11
> **Status**: Complete
> **Scope**: M-10b 스크롤 구현을 위한 참조 구현 분석

---

## 1. 크로스 분석

### 스크롤 누적 패턴

| Terminal | 누적 방식 | 단위 | 나머지 보존 | 핵심 코드 |
|----------|----------|:----:|:----------:|-----------|
| **ghostty** | `pending_scroll_y += yoff_adjusted` → `/ cell_size` → 나머지 저장 | pixel | **O** | `Surface.zig:3430` `self.mouse.pending_scroll_y = poff - (amount * cell_size)` |
| **WT** | `accumulatedDelta += delta` → `abs >= WHEEL_DELTA(120)` 임계값 | tick | **X** (리셋) | `mouseInput.cpp:310` `_mouseInputState.accumulatedDelta = 0` |
| **Alacritty** | `accumulated_scroll.y += px * multiplier` → `/ height` → `%= height` | pixel | **O** | `input/mod.rs:835` `accumulated_scroll.y %= height` |
| **WezTerm** | discrete 정규화 `*delta = 1` | tick | **X** | `mouseevent.rs:950` `*delta = 1` |
| **cmux** | **libghostty에 위임** (precision 2x, momentum 비트) | pixel | **N/A** | `GhosttyTerminalView.swift:8094` `ghostty_surface_mouse_scroll(surface, x, y, mods)` |

### 마우스 모드 분기 (3단계)

| 조건 | ghostty | WT | Alacritty | WezTerm |
|------|---------|-----|-----------|---------|
| **마우스 reporting ON** | button 4/5/6/7 → `mouseReport` | VT 시퀀스 (SGR/UTF8/Default) | `mouse_report(64/65)` | `write!` SGR/X10/UTF8 |
| **Alt screen + alternate scroll** | 커서 키 `\x1bOA`/`\x1bOB` | `_makeAlternateScrollOutput` → VK_UP/DOWN | `\x1bOA`/`\x1bOB` | `key_down(UpArrow/DownArrow)` |
| **일반 모드** | `scrollViewport(.delta)` | `_mouseScrollHandler` → `UpdateScrollbar` | `scroll(Scroll::Delta)` | GUI 레이어에서 처리 |

### Precision vs Discrete 처리

| Terminal | Precision (트랙패드) | Discrete (마우스 휠) |
|----------|---------------------|---------------------|
| **ghostty** | `yoff * precision_multiplier` → 픽셀 누적 | `max(yoff, 1) * cell_size * discrete_multiplier` |
| **WT** | `_internalScrollbarPosition` float 누적 | `WHEEL_DELTA(120)` 임계값 |
| **Alacritty** | `PixelDelta` → 각도 필터(25도) → 누적 | `LineDelta` → `* cell_height` → 누적 |
| **WezTerm** | N/A (discrete만) | `*delta = 1` 정규화 |
| **cmux** | `hasPreciseScrollingDeltas` → 2x | 그대로 전달 |

### 스크롤 방향 부호

| Terminal | Up/Right | Down/Left | 비고 |
|----------|:--------:|:---------:|------|
| **ghostty** | `yoff > 0` → `.up_right` | `yoff < 0` → `.down_left` | delta 부호 그대로 |
| **WT** | `delta > 0` → 0x40 | `delta < 0` → 0x41 | WHEEL_DELTA 단위 |
| **Alacritty** | `scroll_y > 0` → WheelUp(64) | `scroll_y < 0` → WheelDown(65) | 픽셀 부호 |
| **WezTerm** | `amount > 0` → WheelUp | `amount < 0` → WheelDown | discrete 부호 |

### VT 버튼 코드 (5개 동일)

| 방향 | 버튼 코드 | 비고 |
|------|:---------:|------|
| Y축 위 (wheel up) | **64** (0x40) | button .four |
| Y축 아래 (wheel down) | **65** (0x41) | button .five |
| X축 오른쪽 | **66** (0x42) | button .six |
| X축 왼쪽 | **67** (0x43) | button .seven |

---

## 2. GhostWin M-10b 설계 지침

### GhostWin 제약

- WM_MOUSEWHEEL의 delta = `HIWORD(wParam)`, 120 단위 (WHEEL_DELTA)
- Windows에는 precision/momentum 개념 없음 (macOS 전용)
- child HWND에 WM_MOUSEWHEEL이 직접 전달되지 않을 수 있음 → parent forwarding 필요

### 권장 구현 패턴

ghostty의 `scrollCallback` 패턴이 가장 완전하므로 이를 따르되, Windows 환경에 맞게 단순화:

```
WM_MOUSEWHEEL 수신 (delta = HIWORD(wParam))
  ↓
delta를 ghostty mouse_encode에 전달:
  - button = delta > 0 ? 4 (up) : 5 (down)
  - action = PRESS
  - 기존 gw_session_write_mouse 재사용
  ↓
ghostty encoder가 내부적으로:
  - mouse_event == none → 인코딩 안 함 (returned written=0)
  - mouse_event != none → VT 시퀀스 생성
  ↓
written == 0 (비활성 모드) → scrollback viewport 이동
  - gw_scroll_viewport(engine, sessionId, delta_rows) 추가 필요
```

### 비활성 모드 scrollback

ghostty의 `scrollViewport(.{ .delta = y.delta * -1 })` 패턴:
- delta 부호 반전 (위로 스크롤 = viewport를 과거로)
- GhostWin에서는 `gw_scroll_viewport` API 추가 또는 `gw_session_write_mouse`의 반환값으로 "인코딩 안 됨" 판별

### 스크롤 누적 (고해상도 마우스)

WT 패턴이 가장 단순하고 Windows에 적합:
```
accumulatedDelta += delta
if abs(accumulatedDelta) < WHEEL_DELTA(120) → 대기
else → accumulatedDelta = 0, 이벤트 발사
```

다만 ghostty encoder에 위임하면 encoder 내부의 `shouldReport` + cell 변환이 자동으로 처리하므로, GhostWin 측 누적이 불필요할 수 있음. **엔진 측에서 확인 필요**.

---

## 3. cmux 고유 패턴 (참고)

| 패턴 | 설명 | GhostWin 적용 |
|------|------|---------------|
| Precision 2x 증폭 | 트랙패드 delta를 2배 | Windows에 해당 없음 |
| Momentum phase 비트 인코딩 | `mods \|= momentum << 1` | Windows에 해당 없음 |
| Scroll lag 측정 (Sentry) | `tick()` 소요시간 → 평균/최대 lag → 텔레메트리 | 향후 성능 모니터링에 참고 |
| Scrollbar coalescing | NSLock + DispatchQueue.main.async | WPF Dispatcher로 동일 패턴 가능 |
| `userScrolledAwayFromBottom` | 사용자가 scrollback 중일 때 자동 스크롤 억제 | 구현 권장 |

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 0.1 | 2026-04-11 | 5개 터미널 스크롤 코드 줄단위 분석 완료 |
