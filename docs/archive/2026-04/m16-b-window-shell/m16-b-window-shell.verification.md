---
title: "M-16-B 윈도우 셸 — 사용자 PC 시각 검증 체크리스트"
type: verification
feature: m16-b-window-shell
date: 2026-04-29
author: 노수장
status: Pending (사용자 PC 진행 대기)
plan_reference: docs/01-plan/features/m16-b-window-shell.plan.md
design_reference: docs/02-design/features/m16-b-window-shell.design.md
---

# M-16-B 윈도우 셸 — 사용자 PC 시각 검증 체크리스트

> **한 줄 요약**: M-16-B 마라톤 (Day 1-7 코드 + 8 commit, 0 warning) 종료 후 사용자 PC 에서 진행하는 시각 검증 가이드. 8 step + 5 핵심 결함, 약 30-45분 소요. 각 단계의 결과를 직접 채우는 형식.
>
> **선행 marathon**: Day 1-7 commit 7건 (`ff618e1` ~ `81d3234`) + 분석/보고 commit 1건
> **PC 의존 작업**: NFR-03 (M-15 회귀 측정) + NFR-05 (DPI 5단계) + NFR-06 (LightMode + Mica) + R1/R2/R3/R6 검증
> **다음 단계**: 본 문서 결과 채워진 후 `/pdca analyze m16-b-window-shell`

---

## 진행 방법

1. PC 가 Win11 22H2+ 환경인지 확인 (Mica 백드롭 동작 조건)
2. 위에서 아래 순서로 step 진행 — 각 step 안의 동작은 모두 통과해야 ✅
3. 실패 (`❌`) 항목 발견 시 즉시 멈출 필요는 없음 — 끝까지 진행 후 일괄 보고
4. 각 step 의 **"실제 결과"** 칸에 짧게 메모 (예: "통과", "최대화 시 좌측만 4px 검은 갭" 등)
5. 마지막 "발견된 이슈" 섹션에 종합 정리

---

## 사전 준비 (5분)

### A. 빌드

| 항목 | 절차                                         | 통과 기준                     | 실제 결과 |
| ---- | -------------------------------------------- | ----------------------------- | --------- |
| A-1  | VS GUI 에서 `GhostWin.sln` 열기              | 솔루션 정상 로드              | OK        |
| A-2  | **Ctrl+Shift+B** (Build Solution, Debug+x64) | 0 error, 0 warning, 빌드 성공 | OK        |

### B. 첫 실행

| 항목 | 절차                                                | 통과 기준                        | 실제 결과 |
| ---- | --------------------------------------------------- | -------------------------------- | --------- |
| B-1  | **F5** (Debug 실행)                                 | 창이 정상 띄워짐                 | OK        |
| B-2  | 터미널 영역에 bash/PowerShell 프롬프트 보임         | M-A 와 동일하게 일반 터미널 동작 | OK        |
| B-3  | 사용자 명령 (예:`ls`, `Get-ChildItem`) 입력 + Enter | 정상 출력                        | OK        |

> ❌ B 실패 시: Day 1 FluentWindow 교체 호환성 문제 (R1) 또는 빌드 환경 문제. **즉시 중단 + 보고**.

---

## Step 1 — Day 1: FluentWindow Caption row hit-test (R1 검증)

### 무엇을 보나

일반 `<Window>` → `<ui:FluentWindow>` 교체 후 caption row 의 7개 zero-size E2E button + Min/Max/Close 의 hit-test 가 정상인지.

### 검증

|  #  | 동작                                  | 기대 결과                 | ✅/❌ | 실제 결과 |
| :-: | ------------------------------------- | ------------------------- | :---: | --------- |
| 1-1 | Min button (─) 클릭                   | 창이 작업 표시줄로 최소화 |  ✅   |           |
| 1-2 | Max button (□) 클릭                   | 일반 → 최대화             |  ✅   |           |
| 1-3 | Restore button (□ → 겹친 사각형) 클릭 | 최대화 → 일반             |  ✅   |           |
| 1-4 | Close button (×) 클릭                 | 창 닫힘 (앱 종료)         |  ✅   |           |
| 1-5 | Title bar 영역 더블클릭               | 최대화 ↔ 일반 토글        |  ❌   |           |
| 1-6 | Title bar 드래그                      | 창이 마우스 따라 이동     |  ❌   |           |
| 1-7 | **Ctrl+T**                            | 새 워크스페이스 생성      |  ✅   |           |
| 1-8 | **Ctrl+W**                            | 워크스페이스 닫기         |  ✅   |           |

### Step 1 종합 결과

```
[ ] 통과 (8/8)
[ ] 실패 — 항목: ____________________
```

---

## Step 2 — Day 2: Mica 토글 + LightMode 합성 (R3, R6 검증)

### 무엇을 보나

- Settings UseMica 토글이 실제 `DwmSetWindowAttribute` 호출까지 도달해서 시각 변화 발생 (false-advertising 해소)
- "(restart required)" 라벨 제거 확인
- LightMode + Mica 합성 시 Sidebar 텍스트 컨트라스트 유지 (R6)
- 터미널 영역에서 Mica 차단 (R3)

### 검증

|  #  | 동작                                                  | 기대 결과                                                                                          | ✅/❌ | 실제 결과                   |
| :-: | ----------------------------------------------------- | -------------------------------------------------------------------------------------------------- | :---: | --------------------------- |
| 2-1 | **Ctrl+,** (Settings 열기)                            | 설정 페이지 표시                                                                                   |  ✅   |                             |
| 2-2 | APPEARANCE 섹션의**Mica backdrop checkbox 켜기**      | 사이드바 / NotificationPanel / TitleBar 영역에 즉시 Mica 텍스처 합성 (반투명 배경에 OS 색이 비침)  |  ❌   | 변화없음                    |
| 2-3 | 체크박스 다시 끄기                                    | Mica off, 단색 background 로 즉시 복귀                                                             | block | 2-2 결과에 따른 테스트 불가 |
| 2-4 | "(restart required)" 라벨 확인                        | **사라짐** (M-B Day 2 에서 제거)                                                                   |  ✅   |                             |
| 2-5 | Settings → Theme 을**Light** 로 전환 + Mica 켜진 상태 | LightMode + Mica 합성 정상. Sidebar 의 모든 텍스트가 또렷하게 읽힘 (컨트라스트 4.5:1 이상)         |  ❌   |                             |
| 2-6 | Mica 켜진 상태에서 터미널 영역 확인                   | 터미널 background 가**불투명** (Terminal.Background.Brush 가 Mica 차단). DX11 child HWND 영역 정상 |  ❌   |                             |
| 2-7 | Theme 을 다시**Dark** + Mica 켜진 상태                | DarkMode + Mica 합성 정상                                                                          |  ❌   |                             |

### Step 2 종합 결과

```
[ ] 통과 (7/7)
[ ] 실패 — 항목: ____________________
```

> ❌ 2-2/2-3 실패: wpfui FluentWindow `WindowBackdropType` swap 미동작 → R3 폴백
> ❌ 2-5 실패: LightMode 토큰 Sidebar.Background opacity 보정 필요 → R6 폴백
> ❌ 2-6 실패: DX11 child HWND Background 명시 필요 (시간 검증)

---

## Step 3 — Day 3: 최대화 / DPI 검은 갭 (R2 검증)

### 무엇을 보나

M-A 까지의 `BorderThickness=8` 수동 보정 코드가 제거됨. FluentWindow + ClientAreaBorder template 이 자동 처리해서 검은 갭 0px 인지.

### 검증

|  #  | 동작                                                  | 기대 결과                                                  | ✅/❌ | 실제 결과            |
| :-: | ----------------------------------------------------- | ---------------------------------------------------------- | :---: | -------------------- |
| 3-1 | 창 최대화 (Win+↑ 또는 Max button)                     | 사방 가장자리에**검은 갭 0px**                             |  ✅   |                      |
| 3-2 | 최대화 → 일반 → 최대화 반복 5회                       | 매번 검은 갭 0px                                           |  ✅   |                      |
| 3-3 | 창 가장자리 hover (resize edge)                       | cursor 가 SizeWE/SizeNS/SizeNESW 변경 (Win11 표준 8px hit) |  ✅   |                      |
| 3-4 | **DPI 100% 모니터** 에서 최대화                       | 검은 갭 0px                                                |       | 원격이라 테스트 불가 |
| 3-5 | **DPI 125%** 모니터로 드래그 또는 설정 변경 후 최대화 | 검은 갭 0px                                                |       | 원격이라 테스트 불가 |
| 3-6 | **DPI 150%**                                          | 검은 갭 0px                                                |       | 원격이라 테스트 불가 |
| 3-7 | **DPI 175%**                                          | 검은 갭 0px                                                |       | 원격이라 테스트 불가 |
| 3-8 | **DPI 200%**                                          | 검은 갭 0px                                                |       | 원격이라 테스트 불가 |

> **DPI 변경 방법**: 외부 모니터 사용 시 GhostWin 창을 다른 배율 모니터로 드래그. 단일 모니터 사용 시 Windows 설정 → 시스템 → 디스플레이 → 배율 조정 (배율 변경 후 GhostWin 의 OnDpiChanged 가 즉시 처리해야 함).

### Step 3 종합 결과

```
[ ] 통과 (8/8 — DPI 5단계 모두 통과)
[ ] 부분 통과 — 통과 DPI: ____________________
[ ] 실패 — 항목: ____________________
```

> ❌ 3-4~3-8 일부 실패 시: R2 폴백 — `MainWindow.xaml.cs:OnWindowStateChanged` 의 maximized 분기에 `BorderThickness = new Thickness(8);` 복원 follow-up commit 작성

---

## Step 4 — Day 4: GridSplitter 양방향 sync (R4 검증)

### 무엇을 보나

사이드바 / 알림 패널 가장자리에 GridSplitter (outer 8px transparent + inner 1px hairline). 마우스 드래그로 폭 조절 + Settings slider 자동 반영. 그 반대도. infinite-loop 0건.

### 검증

|  #   | 동작                                                          | 기대 결과                                                  | ✅/❌ | 실제 결과 |
| :--: | ------------------------------------------------------------- | ---------------------------------------------------------- | :---: | --------- |
| 4-1  | 사이드바 우측 가장자리 hover                                  | cursor 가**SizeWE (좌우 화살표)** 로 변경                  |  ✅   |           |
| 4-2  | 드래그로 사이드바 폭 줄이기 (예: 200 → 150)                   | 사이드바 폭 즉시 반응. workspace list 가 좁아짐            |  ✅   |           |
| 4-3  | 드래그 놓기 (DragCompleted)                                   | 폭이 그 위치에서 멈춤                                      |  ✅   |           |
| 4-4  | Ctrl+, → SIDEBAR → Width slider 확인                          | Slider 가**드래그한 폭 (예: 150)** 을 반영                 |  ✅   |           |
| 4-5  | Settings 의 Width slider 를**230 으로 이동**                  | MainWindow 사이드바가**즉시 230px 로 변경**                |  ❌   |           |
| 4-6  | 사이드바 폭을 MinWidth=120 미만으로 드래그 시도               | 120px 에서 멈춤 (더 줄어들지 않음)                         |  ✅   |           |
| 4-7  | 사이드바 폭을 MaxWidth=400 초과로 드래그 시도                 | 400px 에서 멈춤                                            |  ✅   |           |
| 4-8  | **Ctrl+Shift+I** 또는 벨 아이콘 클릭 — NotificationPanel 열기 | NotificationPanel 표시 + 우측 가장자리에 GridSplitter 보임 |  ✅   |           |
| 4-9  | NotificationPanel 가장자리 hover + 드래그                     | 폭 조절 가능 (SizeWE cursor + 드래그 반응)                 |  ❌   |           |
| 4-10 | 양방향 sync 시 창 깜박임 / freeze                             | infinite-loop 0건                                          |  ✅   |           |

### Step 4 종합 결과

```
[ ] 통과 (10/10)
[ ] 실패 — 항목: ____________________
```

> ❌ 4-4 ~ 4-5 실패: suppressWatcher 100ms 패턴 동작 안 함 → R4 폴백
> ❌ 4-10 실패 (infinite-loop): Output window 에서 PropertyChanged 폭주 메시지 확인 필요

---

## Step 5 — Day 5: NotificationPanel 200ms transition

### 무엇을 보나

NotificationPanel 토글이 즉시 점프가 아닌 **200ms CubicEase EaseOut 슬라이드** 인지. cmux 패리티.

### 검증

|  #  | 동작                                                                        | 기대 결과                                                      | ✅/❌ | 실제 결과              |
| :-: | --------------------------------------------------------------------------- | -------------------------------------------------------------- | :---: | ---------------------- |
| 5-1 | 벨 아이콘 클릭 또는**Ctrl+Shift+I** (NotificationPanel 열기)                | 패널이 부드럽게 슬라이드 인 (오른쪽에서 280px 까지, 약 200ms)  |  ✅   |                        |
| 5-2 | 다시 클릭 (닫기)                                                            | 패널이 부드럽게 슬라이드 아웃 (280 → 0, 약 200ms)              |  ✅   |                        |
| 5-3 | 빠르게 토글 5회 연속                                                        | 매번 정상 transition. freeze 0                                 |  ✅   |                        |
| 5-4 | 사이드바 GridSplitter 로 NotificationPanel 폭 변경 (예: 350px) 후 다시 토글 | 다시 열 때**350px 로 슬라이드 인** (사용자 customised 폭 보존) |  ❌   | 4-9에 의해 테스트 불가 |

### Step 5 종합 결과

```
[ ] 통과 (4/4)
[ ] 실패 — 항목: ____________________
```

> ❌ 즉시 점프 시: `MainWindow.AnimateNotificationPanel` 미동작 또는 PropertyChanged 구독 실패

---

## Step 6 — Day 6: 부가 결함 7건 polish

### 검증

|  #  | 위치                                                 | 기대 결과                                                      | ✅/❌ | 실제 결과                   |
| :-: | ---------------------------------------------------- | -------------------------------------------------------------- | :---: | --------------------------- |
| 6-1 | 사이드바 워크스페이스 10개 이상 만들기 (Ctrl+T 반복) | 가장 아래 ⚙ Settings gear 가 잘리지 않음 (ScrollViewer 동작)   |  ✅   |                             |
| 6-2 | Settings 페이지 (Ctrl+,) — 큰 창 (Maximized) 에서    | 컨텐츠가 창 가운데에 정렬 (좌측 몰림 X)                        |  ✅   |                             |
| 6-3 | Command Palette (**Ctrl+Shift+P**) — 작은 창         | 팔레트 폭이 창 폭에 따라 적응 (MinWidth=400)                   |  ❌   | 고정 Width로 변화 하지 않음 |
| 6-4 | Command Palette — 큰 창                              | 폭이 700px 초과 안 됨                                          |  ❌   | 고정 Width로 변화 하지 않음 |
| 6-5 | 사이드바 ＋ 버튼 클릭 영역                           | 32×32 (Fitts 표준), 이전 28×28 보다 클릭 쉬움                  |  ✅   |                             |
| 6-6 | 사이드바 상단 "GHOSTWIN" 헤더                        | Opacity 흐림 사라짐. Text.Tertiary 색조 (또렷하지만 secondary) |  ✅   |                             |
| 6-7 | 워크스페이스 active indicator (좌측 3px 파란 막대)   | 음수 margin 부작용 없음. workspace 의 실제 위치 안에서 표시    |  ✅   |                             |

### Step 6 종합 결과

```
[ ] 통과 (7/7)
[ ] 실패 — 항목: ____________________
```

---

## Step 7 — Day 7: 키보드 + 스크린리더 a11y

### 무엇을 보나

MainWindow Sidebar 영역의 키보드 결정적 순회 + FocusVisualStyle 글로벌 적용 + 스크린리더 announce.

### 검증

|  #  | 동작                                            | 기대 결과                                                                                  | ✅/❌ | 실제 결과 |
| :-: | ----------------------------------------------- | ------------------------------------------------------------------------------------------ | :---: | --------- |
| 7-1 | Tab 키 5회 연속                                 | 결정적 순서로 포커스 이동 (caption row → ＋ button → ⚙ Settings → workspace list → 터미널) |  ❌   |           |
| 7-2 | Tab 으로**＋ button** 도달 시                   | 파란색 outline (FocusVisualStyle Accent.Primary) 명확히 보임                               |  ❌   |           |
| 7-3 | Tab 으로**⚙ Settings gear** 도달 시             | 동일 outline.**Enter** 누르면 Settings 열림                                                |  ❌   |           |
| 7-4 | Tab 으로**workspace list** 도달 시              | 첫 ListBoxItem 에 outline.**↓↑ 화살표**로 순회 가능                                        |  ❌   |           |
| 7-5 | NVDA 또는 내레이터 켠 상태에서 Sidebar Tab 순회 | "New workspace 버튼" / "Open settings 버튼" / "Workspaces 목록" 각 영역 의미 있게 announce |  ❌   |           |
| 7-6 | Settings 페이지 (Ctrl+,) 에서 Tab 0~15 순회     | M-A 의 Settings 폼 결정성 보존 (회귀 0건)                                                  |  ❌   |           |
| 7-7 | workspace 활성 상태에서**Esc** 키               | 터미널 포커스 복원 (M-A 보존)                                                              |  ✅   |           |

### Step 7 종합 결과

```
[ ] 통과 (7/7)
[ ] 실패 — 항목: ____________________
```

> ❌ 7-7 실패: ListBox `Focusable=False` 제거가 터미널 포커스 회수 회귀. 폴백 — Esc handler 보강 또는 ListBox FocusManager 명시

---

## Step 8 — M-15 측정 (NFR-03 회귀 검증)

### 무엇을 보나

M-A 기준선 idle p95 7.79 ms 대비 **±5% 이내** (성능 회귀 0).

### 절차

PowerShell 에서:

```powershell
.\scripts\measure_render_baseline.ps1 -Scenario idle,resize-4pane,load -ResetSession
```

### 결과 기록

| 시나리오     |                  M-A 기준선                   | M-B 허용 범위 (±5%) |    측정값     |       ✅/❌        |
| ------------ | :-------------------------------------------: | :-----------------: | :-----------: | :----------------: |
| idle p95     |                    7.79 ms                    | **7.40 ~ 8.18 ms**  | **\_\_\_** ms | 스크립트 실행 실패 |
| resize-4pane | **\_\_\_** ms (M-A archive 의 baselines 참조) |         ±5%         | **\_\_\_** ms | 스크립트 실행 실패 |
| load         |                 **\_\_\_** ms                 |         ±5%         | **\_\_\_** ms | 스크립트 실행 실패 |

### 통과 기준

✅ 모든 시나리오 ±5% 이내

> 측정 raw 데이터: `src/GhostWin.App/bin/.../baselines/` 또는 측정 스크립트 출력 폴더 — 결과 csv 첨부 가능

### Step 8 종합 결과

```
[ ] 통과 (3/3 시나리오 모두 ±5%)
[ ] 부분 회귀 — 시나리오: ____________________
[ ] 실패 — 측정 자체 실패 또는 +5% 초과
```

---

## 5 핵심 결함 최종 종합 (사용자 직접 보고)

PRD 의 사용자 직접 체감 5결함 — 각 결함을 별도 step 결과로 매핑:

|    #    | 결함                          | 검증 step   |                                                            결과                                                            |
| :-----: | ----------------------------- | ----------- | :------------------------------------------------------------------------------------------------------------------------: |
| **#4**  | Mica 토글 시각 변화           | Step 2 #2-3 |                                                      실패. 변화 없음                                                       |
| **#5**  | 사이드바 폭 마우스 조절       | Step 4 #1-3 |                                                             OK                                                             |
| **#6**  | 알림 패널 부드러운 transition | Step 5 #1-2 |                                                             OK                                                             |
| **#13** | 최대화 검은 갭                | Step 3 #1-2 | 검은 갭이 뭘 의미하는지 모르겠음. 지금 터미널 host 배경이 검은색이고 별도 지정한 배경색이 계속 혼재되어 있어서 보기 안좋음 |
| **#14** | DPI 변경 시 잔여 갭           | Step 3 #4-8 |                                                        테스트 불가                                                         |

```
[ ] 5/5 통과 — /pdca analyze m16-b-window-shell 진행 가능
[ ] 4/5 — 실패 결함: ___________________________
[ ] 3/5 이하 — 종합 실패, follow-up commit 필요
```

---

## 발견된 이슈 (자유 기록)

> 각 step 의 실패 항목 + 추가 발견 + 스크린샷 링크 + 의문사항을 자유롭게 기록.

### 이슈 1

- **위치**: (예: Step 3 #3-5)
- **기대**:
- **실제**:
- **DPI / 모니터**:
- **스크린샷**:

### 이슈 2

- **위치**:
- **기대**:
- **실제**:

(필요 시 추가)

---

## 빌드 환경 확인 메모

마라톤 종료 시점 (2026-04-29) 기준 빌드 환경 진단:

| 항목                              | 상태                                                          |
| --------------------------------- | ------------------------------------------------------------- |
| .NET 10 SDK 10.0.203              | ✅ 정상 (`C:\Program Files\dotnet\sdk\10.0.203\`)             |
| C#`dotnet build`                  | ✅ 0 warning 통과 (Day 1-7 매 commit)                         |
| C++ Engine.dll                    | ✅ M-A 빌드 결과물 재사용 (M-B 변경 0)                        |
| C++ props 14.50.18.0              | ✅ 존재 (`v145/Microsoft.VCToolsVersion.VC.14.50.18.0.props`) |
| PowerShell `msbuild GhostWin.sln` | ❌ vswhere PATH 미등록 → 환경 부분 초기화 (별도 mini-fix)     |
| **VS GUI Ctrl+Shift+B**           | ✅ 정상 (사용자 환경) — 이 가이드 권장 빌드                   |

---

## 검증 후 다음 단계

1. **모든 step 통과 (8/8 + 5/5 핵심)** → `/pdca analyze m16-b-window-shell` 진행 → gap-detector → Match Rate 계산
2. **부분 통과** → 실패 항목 follow-up commit (R1-R6 폴백 패턴 중 적용) → 재검증 → analyze
3. **Match Rate ≥ 95%** → `/pdca report` → `/pdca archive --summary`

---

## Version History

| Version | Date       | Changes                                                                                           | Author |
| ------- | ---------- | ------------------------------------------------------------------------------------------------- | ------ |
| 0.1     | 2026-04-29 | Initial — Day 8 사용자 PC 시각 검증 8 step + 5 핵심 결함 + M-15 측정 + 발견된 이슈 자유 기록 형식 | 노수장 |
