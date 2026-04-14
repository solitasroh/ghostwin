# 문서/설명 작성 규칙

## 핵심 원칙

**쉬운 한국어 + 다이어그램 + 비교표.** 기술 용어 남발해서 읽기 어려운 문서 금지.

적용 범위:
- 채팅 응답
- `docs/01-plan/features/*.plan.md`
- `docs/02-design/features/*.design.md`
- `docs/03-analysis/*.analysis.md`
- `docs/04-report/*.report.md`
- `docs/00-research/*.md`
- `C:\Users\Solit\obsidian\note\Projects\GhostWin\` 아래 문서

예외:
- 커밋 메시지 (`commit.md` 규칙, 영어)
- 코드 식별자 (파일명, 함수명, 변수명)
- 파일 경로, API 이름, 에러 메시지 원문

## 문서 구조 템플릿

복잡한 기술 주제 설명 시 다음 구조 우선 고려:

1. **맨 위 요약** — 1~2 문장으로 핵심
2. **"지금 어떻게 작동하는가"** — 현재 상태, 다이어그램 + 평문
3. **"문제 상황"** — 사용자 체감 시나리오 (예: "창 닫았는데 안 꺼짐")
4. **"이전 시도 (있다면)"** — 왜 실패했나
5. **"해결 방법"** — 핵심 아이디어 + 의사 코드
6. **"왜 안전한가"** — 근거 3~4 개
7. **"비교표"** — 현재 / 개선 후 1:1
8. **"요약 한 줄"** — 전체를 한 문장으로

## 용어 번역 기본 매핑

| 영문 | 한국어 |
|------|------|
| thread | 스레드 (그대로) |
| I/O thread | 입출력 스레드 |
| destructor | 소멸자 |
| join() | 합류 대기, 스레드 종료 대기 |
| race condition | 동시 실행 충돌, 경쟁 조건 |
| deadlock | 교착 상태 (멈춰서 서로 기다림) |
| use-after-free | 해제 후 사용 (메모리 오염) |
| undefined behavior | 정의되지 않은 동작 |
| mutex | 자물쇠 / 잠금 (처음엔 병기) |
| callback | 콜백 / 나중에 불리는 함수 |
| render thread | 렌더 스레드 / 그리기 스레드 |
| pipe | 파이프 (그대로) |
| pending | 대기 중 |
| deferred destroy | 지연 파괴, 나중에 파괴 |
| TOCTOU | 확인과 사용 사이 타이밍 문제 |

**원칙**: API 이름 / 코드 식별자 (`ReadFile`, `CancelSynchronousIo`, `std::async`)
는 그대로 두되 **처음 등장 시 한국어 설명 병기**.

## 다이어그램 우선 원칙

평문으로만 설명 어려운 건 **거의 전부** 다이어그램 사용:

- **Mermaid**: flowchart TD, graph LR, sequenceDiagram
  - `rkit:mermaid` 스킬 규칙 준수 (subgraph ID 필수, `&` 금지)
- **ASCII 박스/화살표**: 스레드 타임라인, 메모리 레이아웃
- **표**: before/after 비교, 대안 평가

## 피해야 할 패턴

- "H-RCA4 Confirmed" 같은 내부 태그만 던지고 설명 없음
- 영문 약자 폭격 (UAF, TOCTOU, UB, RCA 연속)
- `rc != GHOSTTY_SUCCESS` 같은 코드 줄 그대로 본문에 넣기
- 대안 없이 "재설계 필요" 로 끝내기
- 체감 영향 없는 순수 내부 부채만 나열

## 참고 성공 예시

- `io-thread-timeout-v2` 설명 (2026-04-15 채팅) — 본 구조의 원형
- 대화 메모리: `feedback_korean_easy_terms.md`, `feedback_plain_korean.md`

## 체크리스트 (문서/응답 내보내기 전)

- [ ] 맨 위 1~2 문장 요약 있는가?
- [ ] 다이어그램 1개 이상 있는가?
- [ ] 사용자 체감 시나리오 언급됐는가?
- [ ] 전문 용어 풀어 설명됐는가?
- [ ] 비교표 혹은 before/after 가 있는가?
- [ ] 비개발자 친구가 개념을 이해할 수 있는가?
