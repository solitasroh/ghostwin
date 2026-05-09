# Archive Index — 2026-05

> 2026-05 월 archive 사이클 인덱스. PDCA 5종 풀세트 (PRD / Plan / Design / Analysis / Report) 를 보관하는 closure 폴더와, superseded plan 보존 폴더를 함께 인덱싱한다.

## Closure 사이클

| Feature | 마감 | Match Rate | Status | 문서 | Commit 범위 |
|---------|:----:|:----------:|--------|------|-------------|
| **m16-f-ui-completion** | 2026-05-09 | **91%** | ✅ Complete — M-16 series 5번째 closure. 24결함 audit 중 P1 1 + P2 14 = 15건 마감 (13 Match + 1 Partial FR-02 / 0 Miss). 4 batch (a11y / Tab·Focus / 시각·메뉴 / 토큰) sequential + 수동 verify gate. 영어 단일 운영 결정 정착. 자동화 검증 인프라는 별도 트랙으로 분리. cmux 감성 도달 마무리 | PRD / Plan / Design / Analysis / Report | `0ecadba`..`61aecdb` (Plan/Design/PRD 갱신) + `423509b`/`ecc6160`/`994c814`/`f444c16`/`975dc43` (Do 5 batch) + `e8d8eab`/`61aecdb` (Analyze/Report) |

## Superseded plan (이력 보존)

> closure 사이클이 아니라, 같은 주제의 후속 plan 으로 대체된 plan 의 보존 폴더.

| Feature | superseded일 | 대체 plan | 사유 | 문서 | Commit |
|---------|:-----------:|----------|------|------|--------|
| **test-automation-reboot-superseded** | 2026-05-09 | `docs/01-plan/features/ghostwin-test-automation-consolidation.plan.md` | 같은 날 오전 11:35 commit `5d51e4a` 작성. 6시간 뒤 오후 17:39 commit `8fe4e86` 의 consolidation plan 이 본 plan 을 흡수·확장 (legacy 흡수 단계 + 단일 진입점 + artifact 통일 규칙 추가). reboot 의 코드 산출물 (Automation.Core scaffold 1370 LOC) 은 consolidation 이 "유지" 분류로 받아 그대로 살아남음 | reboot plan (291 LOC) + SUPERSEDED.md | `5d51e4a` (생성) + 본 archive 이동 |

## 누적 통계

- 사이클 수: 1 (closure)
- 평균 Match Rate: 91%
- Superseded plan: 1 건
- 영어 단일 운영 정책: 본 월 정식 결정 (2026-05-09)
- 자동화 인프라 분리 정책: 본 월 정식 체결 (2026-05-09)

## 후속 mini / 사이클 대기

- **m16-f-tooltip-followup** (Settings 17 interactive control ToolTip — FR-02 Partial closure)
- **M-14 / M-15 회귀 측정** (수동 manual run)
- **M-16-G** (P3 11건 누적 정리)
- **test-automation-consolidation** (활성 plan, `docs/01-plan/features/` — Daily/Interactive/Measurement 분리 + legacy 흡수)
