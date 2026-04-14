# dpi-scaling-integration — Sub-cycle Plan Placeholder

> **Parent milestone**: [[Milestones/pre-m11-backlog-cleanup]] Group 4 #13
> **Status**: Placeholder (Plan 상세 미착수)
> **Triggered by**: Tech Debt #20 (Phase 5 검토) — 이전 `gw_render_init` DPI 수정 **되돌림** (cell/viewport/ConPTY 동기화 미완)
> **Date**: 2026-04-14

## 배경

- `gw_render_init` 에서 DPI 를 **96 고정** 하드코딩
- 고DPI 모니터 (125% / 150% / 200%) 에서 폰트 블러리 / 좌표 오차 발생
- 이전 수정 시도: DPI 만 수정 → cell metrics / viewport cols·rows / ConPTY 사이즈가 동기화되지 않아 되돌림
- Phase 1-4 codebase review (31a2235) 에서 "DPI fix" 언급되었으나 실제론 부분적 대응

## Executive Summary (계획 단계)

| Perspective | Content |
|-------------|---------|
| **Problem** | DPI 96 하드코딩 → 고DPI 환경에서 렌더 품질 저하 + 좌표 misalign. 단순 DPI 값만 교체하면 cell/viewport/ConPTY 비동기화로 깨짐 |
| **Solution** | 통합 파이프라인: DPI 변경 → cell 재계산 → viewport rows·cols 재계산 → ConPTY resize 동시 실행. 후보 A: per-window DPI 추적 콜백. B: monitor DPI change 이벤트 chain. Design 에서 결정 |
| **Function/UX Effect** | 고DPI 환경에서 선명한 렌더 + 마우스 hit-test 정확도 — **실사용자 체감 큼** |
| **Core Value** | WPF 네이티브 DPI aware 앱으로 정착. M-11~13 의 UI 작업 품질 기반 |

## 진입 조건

- [ ] 현재 `gw_render_init` 코드 재확인 (31a2235 이후)
- [ ] WPF `PerMonitorV2` DPI awareness manifest 상태 확인
- [ ] 이전 되돌림 시 어떤 요소가 먼저 깨졌는지 정확히 재현

## 예상 범위

- Plan: ~400 LOC (DPI 파이프라인 분석 + 후보 비교)
- Design: ~600 LOC (cell·viewport·ConPTY 동기화 시퀀스 + Mermaid)
- Do: 대 — `gw_render_init`, `DX11Renderer`, `SessionManager`, `ConptySession` 동시 변경
- Check: 다중 DPI 환경 시각 검증 (100%, 125%, 150%, 200%) + 마우스 hit-test

## 관련 문서

- [[Architecture/dx11-rendering]]
- [[Architecture/conpty-integration]]
- [[Backlog/tech-debt]] #20
- [[Milestones/codebase-review-2026-04]] — 부분 대응 commit 31a2235
