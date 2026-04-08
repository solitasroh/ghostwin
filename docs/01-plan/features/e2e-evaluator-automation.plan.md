# E2E Evaluator Automation — Planning Document

> **Summary**: e2e-test-harness 의 D19/D20 분리 원칙(Operator vs Evaluator) 중 **Evaluator side**를 자동화한다. Operator는 이미 8/8 OK + KeyDiag 9 entries로 신뢰 가능한 결과(`scripts/e2e/artifacts/diag_all_h9_fix/`)를 생산하지만, pass/fail/Match Rate 판정은 사람이 Claude Code Task tool을 수동 호출해야 하는 상태. 본 cycle은 `scripts/e2e/evaluator_prompt.md`(미작성) + 호출 wrapper + Evaluator summary 스키마 + subagent binding을 완성해서 `scripts/test_e2e.ps1 -All -Evaluate` 한 줄로 8/8 PASS 여부까지 자동 산출하게 만든다.
>
> **Project**: GhostWin Terminal
> **Phase**: 5-E.5 부채 청산 — e2e-test-harness Step 11.1 잔여 항목 (Evaluator side)
> **Author**: 노수장 (CTO Lead, leader pattern)
> **Date**: 2026-04-08
> **Status**: Draft
> **Previous**:
> - `docs/02-design/features/e2e-test-harness.design.md` §2.3 D19/D20, §3.x evaluator_prompt 위치 spec, §11.1 Step 11 evaluator_prompt.md 미완 항목, §13 Future improvements #5
> - `docs/archive/2026-04/e2e-ctrl-key-injection/` (Operator side 신뢰 회복, R4 closed)
> - `scripts/e2e/runner.py` summary.json notes 필드: "intentionally omits passed/failed/match_rate"

---

## Executive Summary

| Perspective | Content |
|-------------|---------|
| **Problem** | e2e-test-harness 가 D19/D20 으로 Operator(외부 python)와 Evaluator(Claude Code subagent)를 분리하도록 설계됐지만 Evaluator side는 미완 상태. `scripts/test_e2e.ps1 -All` 실행 시 Operator는 8/8 OK + screenshot + metadata.json 까지 산출하지만, 사용자가 그 후 Claude Code 안에서 Task tool을 수동으로 호출해 시각 검증을 해야 한다. `scripts/e2e/evaluator_prompt.md` 자체가 e2e-test-harness Step 11.1 에 "will be added in Step 8" 으로 표시된 채 작성된 적이 없다. 결과적으로 e2e-ctrl-key-injection cycle Acceptance G3 도 pending 으로 남았고, 향후 모든 e2e cycle 의 closeout 이 같은 manual visual review 부담을 안게 된다. |
| **Solution** | 4-component minimal automation: (1) `scripts/e2e/evaluator_prompt.md` 작성 — D20 8-field JSON 스키마 + 8 MQ 시나리오별 expected behavior + screenshot 검증 체크리스트. (2) `scripts/e2e/evaluator_helper.ps1` 또는 `test_e2e.ps1` 의 `-Evaluate` switch — Operator 완료 후 evaluator subagent 호출 명령 + artifact paths 안내 + 결과 수집. (3) Evaluator 결과 schema `evaluator_summary.json` — D19/D20 분리 유지 위해 operator summary와 별도 파일. 8 entries × {pass, confidence, observations, issues, failure_class, evidence} + 집계 (`match_rate`, `passed`, `failed`, `unclear`). (4) Subagent binding 결정 — 기존 `rkit:gap-detector` 재사용 vs `Task(general-purpose)` + prompt 파일 직접 주입. 두 경로 모두 Plan 단계에서 trade-off 비교, Design 단계에서 1개 lock-in. |
| **Function/UX Effect** | 사용자 가시 동작 변경 0 (production 코드 미수정). 개발자/QA 관점: (a) `scripts/test_e2e.ps1 -All -Evaluate` 단일 명령으로 Operator + Evaluator + 종합 verdict 출력, (b) Match Rate 산출이 Claude Code session 안에서 deterministic 하게 이루어져 cycle closeout 시 G3 Acceptance gate 즉시 충족, (c) e2e-ctrl-key-injection v0.2 §11.4 G3 pending 항목 retroactive close (run id `diag_all_h9_fix` 기반), (d) 향후 모든 PDCA feature 의 manual QA 부담 감소, (e) Evaluator 결과 JSON 이 스크립트 자동화 input 으로 사용 가능 — CI/CD 후속 가능성 열림 (out of scope, 별도 cycle). |
| **Core Value** | "**프레임워크가 자기 자신의 분리 원칙을 끝까지 닫는다**" — D19/D20 은 설계 시점에 분리됐지만 실제 자동화 wiring 이 미완이라 manual 의존이 잔존했다. 본 cycle 은 그 final mile 을 닫아 e2e-test-harness 가 self-contained closed loop 이 되도록 한다. 동시에 e2e-ctrl-key-injection cycle 의 G3 retroactive closeout 도 동반 — 즉 evaluator 자동화는 미래뿐 아니라 직전 cycle 의 미완 항목까지 회수한다. behavior.md "정석 우선, 우회 금지" 원칙을 e2e-test-harness 자체에 적용한 사례. |

---

## 1. Overview

### 1.1 Purpose

e2e-test-harness Design v0.1.1 §2.3 D19/D20 에서 명시된 Operator/Evaluator 분리 원칙의 **Evaluator side 미완 항목**을 완성해서, `scripts/test_e2e.ps1 -All` 한 명령으로 Operator 실행 + Evaluator 자동 호출 + 8 MQ 시나리오 시각 검증 + Match Rate 산출까지 closed loop 로 만든다.

### 1.2 Why a Separate Cycle

본 작업을 e2e-test-harness Step 11.1 안으로 흡수하지 않는 이유:

- e2e-test-harness 는 이미 archive 가능한 deliverable 완성 상태 (Operator framework 4-layer + 8 MQ scenarios + capture/normalize/inject + WGC PoC + commit `35f7d24`)
- Step 11.1 의 evaluator_prompt.md 항목은 명시적으로 "will be added in Step 8" 으로 deferred 됐고, e2e-ctrl-key-injection cycle 도 Plan 단계에서 G3 를 separate step 으로 분류
- 본 cycle 은 **prompt engineering + subagent binding + JSON schema** 라는 별개 책임 영역이며, e2e-test-harness Operator 책임 (capture/inject/normalize) 과 분리해서 추적해야 향후 회귀/iteration 측정 가능
- e2e-ctrl-key-injection 종료 시점에 G3 가 pending 으로 남았으므로, 본 cycle 의 첫 deliverable 은 **그 G3 를 retroactive 로 close** 하는 것

### 1.3 Empirical Facts (재조사 금지)

e2e-test-harness Design v0.1.1 + 직전 e2e-ctrl-key-injection cycle 결과:

| 항목 | 상태 | 출처 |
|---|---|---|
| Operator 8/8 OK | ✅ | `scripts/e2e/artifacts/diag_all_h9_fix/summary.json` |
| Operator artifact 구조 | ✅ | `MQ-N/metadata.json` (scenario, status, screenshots[], operator_notes, started_at, finished_at) + `MQ-N/*.png` |
| summary.json 분리 원칙 | ✅ | notes: "intentionally omits 'passed'/'failed'/'match_rate'" (D19/D20) |
| Evaluator schema spec | ✅ | D20 — 8-field per scenario (scenario/pass/confidence/observations/issues/failure_class/evidence) |
| evaluator_prompt.md | ❌ **미작성** | Step 11.1 missing, runner.py 출력 메시지에 "will be added in Step 8" |
| Evaluator subagent 선택 | ❌ **미결정** | gap-detector 재사용 vs 신규 e2e-evaluator |
| Evaluator 호출 wrapper | ❌ **미작성** | test_e2e.ps1 `-Evaluate` switch 또는 별도 helper |
| Evaluator summary 파일 | ❌ **미작성** | evaluator_summary.json schema |

### 1.4 Related Source / Docs

| 파일 | 역할 |
|---|---|
| `docs/02-design/features/e2e-test-harness.design.md` §2.3 D19/D20 | 분리 원칙 spec, schema field 정의 |
| `docs/02-design/features/e2e-test-harness.design.md` §3.x | evaluator_prompt 위치 (`scripts/e2e/evaluator_prompt.md`) |
| `docs/02-design/features/e2e-test-harness.design.md` §11.1 | "Step 11.1 evaluator_prompt.md 작성 — Do phase" 미완 |
| `docs/02-design/features/e2e-test-harness.design.md` §13 future #5 | "Evaluator 호출 자동화 — MCP 도구 또는 claude-code CLI" |
| `scripts/e2e/runner.py` | Operator entry, summary.json 생성, evaluator hint 출력 |
| `scripts/test_e2e.ps1` | Orchestrator 진입점, `-Scenario` `-All` `-Bootstrap` switch |
| `scripts/e2e/artifacts/diag_all_h9_fix/` | Latest operator output (Acceptance G3 input) |
| `docs/archive/2026-04/e2e-ctrl-key-injection/e2e-ctrl-key-injection.design.md` v0.2 §11.4 G3 | retroactive 종료 대상 |

---

## 2. Scope

### 2.1 In Scope

**Evaluator prompt template**

- [ ] `scripts/e2e/evaluator_prompt.md` 작성
- [ ] D20 8-field JSON schema 명시 (scenario / pass / confidence / observations / issues / failure_class / evidence + extra)
- [ ] 8 MQ 시나리오별 expected behavior + screenshot 검증 체크리스트
- [ ] Failure class taxonomy (capture-blank / wrong-pane-count / wrong-workspace / partial-render / unrelated-noise)
- [ ] False positive 가이드 (font hinting 차이, focus indicator pixel jitter, mouse cursor presence)
- [ ] Hardware vs SendInput 결과 동등성 명시 (operator 가 SendInput 으로 결과 도달했는지에 대한 추가 검증 불필요 — H9 fix 후)
- [ ] 다국어 issue 한국어 + 영어 혼용 허용 정책
- [ ] Output format: single JSON file or stdout fenced block — Design 단계에서 lock-in

**Evaluator invocation wrapper**

- [ ] `scripts/test_e2e.ps1` 에 `-Evaluate` switch 추가 (또는 `scripts/e2e/evaluate_run.ps1` helper) — Design 단계 1개 lock-in
- [ ] Operator 실행 후 가장 최근 `artifacts/{run_id}/` 경로를 자동 식별
- [ ] Evaluator subagent 호출 명령을 사용자에게 안내 (D19 수동 호출 유지) 또는 가능하면 자동 호출 시도
- [ ] Evaluator 결과 JSON 을 `scripts/e2e/artifacts/{run_id}/evaluator_summary.json` 에 저장
- [ ] Operator 의 summary.json 과 분리 (D19/D20 정합)

**Evaluator subagent binding**

- [ ] Option A — 기존 `rkit:gap-detector` 재사용 (design vs implementation 비교 patten 을 visual evaluation 에 응용)
- [ ] Option B — `Task(general-purpose, prompt=evaluator_prompt.md)` 호출
- [ ] Option C — 신규 `e2e-evaluator` 전용 subagent 정의 (project-local agent)
- [ ] Trade-off 비교 + Design 단계 1개 lock-in
- [ ] Plan 단계 prior: A 30% / B 50% / C 20%

**Evaluator summary schema**

- [ ] `evaluator_summary.json` 8 entries × 8 fields + 집계 (`match_rate`, `passed`, `failed`, `unclear`, `evaluator_id`, `prompt_version`)
- [ ] `summary.json` (Operator) 와 cross-reference 가능한 `run_id` 키 필수
- [ ] Operator 의 `MQ-N/metadata.json` 도 input 으로 사용 (scenario name, screenshot paths, operator_notes)

**Retroactive closeout**

- [ ] 본 cycle 의 첫 e2e run = `diag_all_h9_fix` (이미 존재하는 8/8 operator output)
- [ ] 본 cycle Do phase 에서 evaluator 가 그 run 을 평가 → e2e-ctrl-key-injection design v0.2 §11.4 G3 항목을 ✅ 로 update
- [ ] bisect-mode-termination design v0.2 §10.1 retroactive QA evidence table 에 evaluator screenshot judgment 항목 추가

**Regression verification**

- [ ] `scripts/test_e2e.ps1 -All -Evaluate` 1 회 실행 → Operator 8/8 OK + Evaluator 8/8 PASS + Match Rate 100%
- [ ] PaneNode 단위 9/9 회귀 0
- [ ] Hardware manual smoke 5/5 회귀 0 (Operator side 변경 없으므로 회귀 0 기대)
- [ ] Evaluator 결과 JSON 의 schema validation (jq 또는 PowerShell ConvertFrom-Json)

### 2.2 Out of Scope

| 항목 | YAGNI 근거 |
|---|---|
| 새 MQ 시나리오 추가 (MQ-9~) | 본 cycle 은 기존 8 시나리오 자동화에 한정. 새 시나리오는 별도 feature |
| Vision model upgrade (GPT-4V, Gemini Vision) | gap-detector / Task subagent 가 이미 multimodal. 다른 model 도입은 별도 cycle |
| Operator 측 변경 (capture/inject/normalize) | e2e-test-harness scope. 본 cycle 은 Evaluator side 한정 |
| MCP server 로 Evaluator 노출 | e2e-test-harness §13 future improvements #5 (MCP 도구) — 별도 cycle |
| `claude-code` CLI 간접 호출 | 현재 CC version 에서 미지원. D19 manual invoke 유지가 정석 |
| CI/CD 통합 (GitHub Actions, GitLab CI) | Evaluator 가 Claude Code session 에 의존하므로 CI 환경 제한. 별도 architecture |
| Evaluator 결과 사람이 검증하는 meta-evaluator | self-referential, YAGNI. 만약 Evaluator 가 명백히 틀리면 prompt 개선이 정답 |
| `evaluator_prompt.md` versioning system | v1.0 lock-in, 향후 회귀 시 manual increment |

---

## 3. Requirements

### 3.1 Functional Requirements

| ID | Requirement | 검증 방법 |
|---|---|---|
| **FR-01** | `scripts/e2e/evaluator_prompt.md` 작성 — D20 8-field schema + 8 MQ 시나리오별 expected behavior + screenshot 체크리스트 + failure class taxonomy + false positive 가이드 | 파일 존재 + Design 단계 lock 된 schema 와 일치 |
| **FR-02** | Evaluator 호출 wrapper — `scripts/test_e2e.ps1 -Evaluate` switch 또는 `scripts/e2e/evaluate_run.ps1` helper | 단일 명령 실행 + 가장 최근 artifact 경로 자동 식별 + 사용자 호출 안내 출력 |
| **FR-03** | Evaluator 결과 JSON 저장 — `scripts/e2e/artifacts/{run_id}/evaluator_summary.json`, D20 schema | 파일 생성 + jq/ConvertFrom-Json 검증 |
| **FR-04** | Evaluator subagent binding — Option A/B/C 중 1개 lock-in (Design 단계) | Design 문서 §Decision Lock-in 표 |
| **FR-05** | Retroactive closeout — `diag_all_h9_fix` run 을 본 cycle Do 단계에서 평가 → e2e-ctrl-key-injection v0.2 §11.4 G3 ✅ 업데이트 | design.md 변경 + commit hash 기록 |
| **FR-06** | Regression — `scripts/test_e2e.ps1 -All -Evaluate` 8/8 PASS, Match Rate 100% | summary.json + evaluator_summary.json 둘 다 confirm |
| **FR-07** | Operator/Evaluator 분리 유지 — Evaluator 결과는 Operator summary.json 에 leak 되지 않음 | git diff summary.json 에 pass/fail/match_rate 키 없음 검증 |

### 3.2 Non-Functional Requirements

| ID | Requirement | 검증 방법 |
|---|---|---|
| **NFR-01** | Evaluator 호출 latency 한 cycle 당 < 5분 (8 시나리오, 각 시나리오 < 30s) | wall-clock 측정, Design 단계 budget 확정 |
| **NFR-02** | Evaluator 결과 deterministic — 동일 input 에 대해 동일 verdict 가 high probability 로 반복 (subagent stochasticity 인지) | 동일 run id 로 evaluator 2 회 호출 → diff 비교 |
| **NFR-03** | Evaluator 결과가 false positive 일 경우 사용자가 단일 명령으로 retry 가능 | wrapper 에 `-Force` 또는 `-RunId` 인자 |
| **NFR-04** | evaluator_prompt.md 는 production 코드와 무관 — 변경 시 Operator 회귀 0 | Operator 단독 실행으로 회귀 검증 |
| **NFR-05** | 진단 가능한 logging — wrapper 가 evaluator 호출 시간 / artifact path / subagent name 을 stdout 에 기록 | 사용자 디버깅 가능 |

---

## 4. Hypotheses (open questions for Design)

> 본 절은 추측이며 Design 단계에서 evidence 또는 council synthesis 후 lock-in.

### H1 — gap-detector 재사용이 visual evaluation 에 fit

**Claim**: rkit:gap-detector 는 design vs implementation gap 검증 도구지만, 8 MQ 시나리오의 expected behavior (design intent) vs screenshot (actual implementation) 비교에 재사용 가능. agent definition 의 multimodal 지원 여부에 의존.

**Evidence to collect (Design)**:
- gap-detector agent definition 확인 — image input 지원하는가
- gap-detector 가 prompt-template-driven mode 가 있는가
- 기존 PDCA cycle 에서 gap-detector 의 input format

**Falsified if**: gap-detector 가 image 를 input 으로 받지 못함 → Option A 폐기, Option B/C 진행

### H2 — `Task(general-purpose, prompt=evaluator_prompt.md)` 호출이 가장 단순

**Claim**: Claude Code Task tool 의 general-purpose subagent 에 evaluator_prompt.md 내용을 prompt 로 전달 + artifact_dir 경로를 컨텍스트로 주면, multimodal 지원 + Read tool 로 screenshot 자동 로드 가능.

**Evidence to collect (Design)**:
- Task tool 의 prompt 길이 제한 (evaluator_prompt 가 ~3000 token 예상)
- subagent 가 Read tool 로 PNG file 을 image input 으로 인식하는지
- subagent 의 multimodal 지원 모델 (Sonnet/Opus)

**Falsified if**: Task tool 이 image input 미지원 → Option A 또는 C

### H3 — 신규 e2e-evaluator subagent 정의가 long-term 정답

**Claim**: project-local `.claude/agents/e2e-evaluator.md` 정의 + 전용 prompt + Read/Bash tool 만 grant. tool surface 최소화로 의도치 않은 부작용 차단. 향후 새 MQ 추가 시 agent definition 에 기록.

**Evidence to collect (Design)**:
- project-local agent definition spec
- 다른 rkit 프로젝트의 sample
- 본 프로젝트에 이미 .claude/agents/ 가 있는가

**Falsified if**: project-local agent 정의가 복잡도 대비 가치 부족 (Option B 가 충분)

### Hypothesis Decision Matrix

| ID | Prior (확실하지 않음) | Falsification cost |
|---|:---:|---|
| H1 gap-detector 재사용 | 30% | 낮음 (agent definition 확인) |
| H2 Task general-purpose 직접 호출 | 50% | 낮음 (Task tool API 확인) |
| H3 신규 e2e-evaluator 정의 | 20% | 중간 (sample 검색 + spec 작성) |

> H2 가 prior 가장 높음 — Design 단계 1순위 검증.

---

## 5. Diagnosis / Verification Plan

본 cycle 은 진단 cycle 이 아니라 spec-driven implementation cycle 이지만, evaluator 자동화 자체에 verification 단계가 필수.

| Step | 작업 | 산출 | Owner |
|:---:|---|---|---|
| 1 | gap-detector / Task tool / project-local agent 3 옵션 trade-off 비교 (Design 단계) | Design §Decision Lock-in | CTO Lead (council with code-analyzer) |
| 2 | `evaluator_prompt.md` 초안 작성 | 파일 (~300 lines) | CTO Lead leader |
| 3 | `evaluate_run.ps1` 또는 `test_e2e.ps1 -Evaluate` 구현 | shell script | qa-strategist + dotnet-expert advisory |
| 4 | `evaluator_summary.json` schema 작성 + jq validator | JSON spec + sample | qa-strategist |
| 5 | 첫 호출 — `diag_all_h9_fix` run 을 평가 | evaluator_summary.json (8 entries) | manual 1회 |
| 6 | False positive / unclear 발견 시 prompt 개선 → re-evaluate | prompt v0.2+ | iterative |
| 7 | NFR-02 deterministic check — 동일 run 2회 평가 → diff | comparison report | manual |
| 8 | e2e-ctrl-key-injection design v0.2 §11.4 G3 update + commit | design diff + commit hash | leader |
| 9 | bisect-mode-termination design v0.2 §10.1 evaluator evidence 추가 | design diff | leader |
| 10 | 새 e2e run 으로 end-to-end 자동화 검증 (`scripts/test_e2e.ps1 -All -Evaluate`) | summary.json + evaluator_summary.json | regression |
| 11 | Acceptance G1-G5 confirm → Report | report.md | leader |

---

## 6. Risks

| ID | Risk | 영향 | Likelihood | Mitigation |
|---|---|:---:|:---:|---|
| **R-A** | gap-detector 가 image input 미지원 → Option A 폐기 | scope reduction | Med | H1 falsification cost 낮음, Design 단계에서 즉시 결정 |
| **R-B** | Task general-purpose subagent 가 prompt 과 image 를 동시 처리 못함 | 본 cycle 핵심 가설 깨짐 | Low | Option C (project-local agent) 폴백 가능. PoC 필요 시 Design 단계 PoC step 추가 |
| **R-C** | Evaluator 가 stochastic — 동일 run 2회 호출 시 verdict 불일치 | NFR-02 위반 | Med | 결과 불일치 시 prompt 강화 (rubric 명확화) + temperature 명시 (Task tool 옵션 있다면) |
| **R-D** | False positive — Evaluator 가 정상 screenshot 을 fail 로 판정 (font hinting, cursor) | 사용자 신뢰 하락 | Med | False positive 가이드 prompt 명시 + retry switch (NFR-03) |
| **R-E** | False negative — Evaluator 가 실패 screenshot 을 pass 로 판정 | 회귀 미감지 | **Critical** | Hardware smoke + PaneNode 단위 + manual spot check 로 fallback. Evaluator 가 단일 신뢰원 아님 |
| **R-F** | `diag_all_h9_fix` run 의 8 screenshot 이 expected behavior 와 시각적으로 불일치 | retroactive G3 closeout 실패 | Low | 8 screenshot 모두 Operator OK 이고 H9 fix 후 정상 동작이라 시각적 fail 가능성 낮음. 발생 시 Operator side 회귀 분리 cycle |
| **R-G** | Evaluator 호출 wrapper 가 다른 PDCA cycle 의 e2e run 을 잘못 식별 | 잘못된 verdict | Low | run id 인자 강제 + 가장 최근 자동 식별은 default fallback only |
| **R-H** | `evaluator_prompt.md` 변경이 production 코드 회귀 트리거 | NFR-04 위반 | Very Low | scripts/e2e/ 만 수정, src/ 변경 없음. CI 회귀 0 |
| **R-I** | Evaluator latency > 5분 (NFR-01) | UX 저하 | Low | 8 시나리오 병렬 호출 가능 여부 Design 단계 검토 |

---

## 7. Dependencies

| 항목 | 상태 | 비고 |
|---|:---:|---|
| e2e-test-harness Operator framework | Done | Step 11.1 Operator 11 step 모두 완료 |
| e2e-ctrl-key-injection R4 fix (H9) | Done | `efe1950` Operator 8/8 OK 도달 |
| `scripts/e2e/artifacts/diag_all_h9_fix/` | Available | 본 cycle 첫 evaluator input |
| Claude Code Task tool | Available | general-purpose / gap-detector / project-local 모두 invokable |
| rkit:gap-detector agent | Available | Option A 후보 |
| PowerShell ≥ 5.1 | Available | wrapper script |
| jq (선택) | Optional | JSON validation. PowerShell `ConvertFrom-Json` 으로 대체 가능 |

---

## 8. Acceptance Criteria

본 cycle 이 Done 으로 close 되려면 모두 PASS:

1. **FR-01~FR-07 100% 충족** — evaluator_prompt.md, wrapper, summary JSON, subagent binding, retroactive closeout, regression, 분리 유지
2. **NFR-01~NFR-05 100% 충족** — latency, deterministic, retry, 회귀 0, logging
3. **Evaluator first run** — `diag_all_h9_fix` 8/8 PASS, Match Rate 100%, false positive 0
4. **Retroactive closeout** — e2e-ctrl-key-injection design v0.2 §11.4 G3 ⏳ → ✅ 업데이트 + commit
5. **bisect-mode-termination** design v0.2 §10.1 retroactive QA evidence table 에 evaluator judgment 추가
6. **PaneNode 단위 9/9** 회귀 0
7. **Hardware manual smoke 5/5** 회귀 0 (Operator 변경 없음, 회귀 기대 0)
8. **End-to-end 검증** — `scripts/test_e2e.ps1 -All -Evaluate` 단일 명령으로 8/8 PASS 확인
9. **Design / Report PDCA cycle** 정상 closeout

---

## 9. Out of Plan (Design / Do 단계 의사결정 필요)

다음 항목은 본 cycle 진행 중 Design 또는 Do 단계에서 사용자 결정 필요:

- **Q1**: gap-detector / Task general-purpose / project-local e2e-evaluator 중 어느 binding 채택 (FR-04)
- **Q2**: `scripts/test_e2e.ps1 -Evaluate` switch 추가 vs `scripts/e2e/evaluate_run.ps1` 분리 helper (FR-02)
- **Q3**: Evaluator 호출 자동화 수준 — D19 manual invoke 유지 vs subagent 호출까지 wrapper 가 자동 (현재 CC 제약 검증 필요)
- **Q4**: `evaluator_summary.json` location — `artifacts/{run_id}/` 안에 같이 vs `artifacts/{run_id}/evaluator/` 별도 폴더
- **Q5**: False positive retry 정책 — 자동 1회 retry vs 수동 only
- **Q6**: 시나리오별 expected behavior 를 Plan 에 인라인 vs Design 에서 separate spec 으로 분리 (Plan 의 §Scope 가 vague 해질 우려)
- **Q7**: 향후 새 MQ 시나리오 추가 시 evaluator_prompt.md 갱신 process — manual 추가 vs auto-discover 메커니즘
- **Q8**: Evaluator 가 사용하는 model — Sonnet 4.6 vs Opus 4.6 (latency vs accuracy trade-off)

---

## 10. Cross-cycle context (PDCA neighborhood)

본 cycle 은 다음 cycles 와 직접 연결:

| Cycle | Relationship |
|---|---|
| `e2e-test-harness` (parent, archived) | Step 11.1 의 evaluator_prompt.md 미완 항목 회수 |
| `e2e-ctrl-key-injection` (sibling, archived) | design v0.2 §11.4 G3 retroactive closeout 대상. 본 cycle 의 Do 단계에서 첫 evaluator 호출이 그 cycle 의 미완 acceptance gate 를 닫음 |
| `bisect-mode-termination` (sibling, archived) | design v0.2 §10.1 retroactive QA evidence table 에 evaluator judgment 항목 추가 |
| Phase 5-F session-restore (downstream) | 본 cycle 완료 후 Phase 5-F 의 PDCA closeout 시 evaluator 자동 호출이 default 가 됨 |
| P0-3 close-path-unification (sibling) | 무관 |
| P0-4 PropertyChanged detach (sibling) | 무관 |

---

## 11. Risks Summary (Plan-level)

- **Critical**: R-E (false negative) — Evaluator 가 회귀를 못 잡으면 자동화 의미 없음. Hardware smoke + unit test 와 함께 사용해서 single source of truth 회피.
- **High**: 없음
- **Medium**: R-A, R-C, R-D — Design 단계에서 lock-in 또는 fallback 명시
- **Low**: R-B, R-F, R-G, R-H, R-I

---

## 12. Estimated Cost (확실하지 않음)

| Phase | Estimate |
|---|---|
| Design (council with code-analyzer + qa-strategist) | 1 session, ~1-2시간 |
| Do (Steps 1-11) | 1-2 session, ~3-5시간 |
| Check (acceptance gates G1-G7) | 30분 |
| Report + Archive | 30분 |
| **Total** | **~5-8시간** |

작은 cycle (~600 LOC 변경 예상) 이지만 prompt engineering iteration 과 false positive tuning 이 가장 큰 변수.

---

## Version History

| Version | Date | Author | Changes |
|:---:|---|---|---|
| **v0.1** | 2026-04-08 | 노수장 (CTO Lead) | Initial plan. e2e-test-harness D19/D20 + Step 11.1 미완 항목 + e2e-ctrl-key-injection design v0.2 §11.4 G3 pending 을 동시 closeout 하는 separate cycle 정의. 4-component scope (evaluator_prompt + wrapper + JSON schema + subagent binding) + H1-H3 가설 + 9 risks (R-A~R-I) + 8 open question + 5 hour cost estimate |
