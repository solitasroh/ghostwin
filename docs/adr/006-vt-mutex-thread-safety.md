# ADR-006: vt_mutex를 통한 VtCore 스레드 안전성 확보

- **상태**: 채택
- **날짜**: 2026-03-29
- **관련**: Phase 2 Design 리뷰 (C2 이슈), Alacritty/WezTerm 오픈소스 패턴

## 배경

ConPTY I/O 스레드에서 `VtCore::write()`를, 메인 스레드에서 `VtCore::resize()`를 호출한다. 두 호출이 동시에 발생할 경우 libghostty-vt 내부에서 data race가 발생할 수 있다.

## 문제

- libghostty-vt의 `ghostty_terminal_vt_write`와 `ghostty_terminal_resize`의 스레드 안전성이 **공식 문서에 명시되지 않음**
- Design 리뷰에서 3개 에이전트 모두 이 경합 조건을 Critical로 지적
- "libghostty-vt 내부 뮤텍스 보호 추측"에 의존하는 것은 위험

## 결정

`std::mutex`(`vt_mutex`)로 `write()`와 `resize()`를 상호 배제:

```cpp
// I/O thread
{
    std::lock_guard lock(impl->vt_mutex);
    impl->vt_core->write({buf.get(), bytes_read});
}

// Main thread
{
    std::lock_guard lock(impl->vt_mutex);
    impl->vt_core->resize(cols, rows);
}
```

## 근거

### 오픈소스 레퍼런스

| Project | Pattern |
|---------|---------|
| **Alacritty** | resize를 event loop 채널로 전송하여 write와 직렬화 |
| **WezTerm** | `Arc<Mutex<Inner>>`로 ConPTY 상태를 공유, resize 시 mutex 잠금 |

두 프로젝트 모두 write/resize 동시 접근을 차단하는 동기화를 사용한다.

### 성능 영향

- ReadFile이 블록 상태인 시간이 대부분이므로 뮤텍스 경합은 거의 발생하지 않음
- 경합 발생 시에도 `lock_guard`의 overhead는 마이크로초 단위
- Phase 2 벤치마크에서 성능 영향 확인 예정

## 대안 검토

| 방안 | 판정 | 이유 |
|------|:----:|------|
| libghostty-vt 소스 확인 후 불필요 시 제거 | 보류 | upstream 변경 시 재검증 필요, 방어적 설계 유지 |
| SPSC 큐 | 보류 | Phase 2에서는 과잉, Phase 3에서 3-way 경합 시 재검토 |
| resize를 I/O 스레드로 위임 | 기각 | ReadFile 블록 중 메시지 전달 불가 |
| **std::mutex** | **채택** | 단순, 검증된 패턴, 오픈소스 동일 접근 |

## Phase 3 마이그레이션 노트

D3D11 렌더 스레드 도입 시 `update_render_state()`도 `vt_mutex`에 포함시켜야 한다. 3-way 경합(write + resize + render) 시 뮤텍스 contention을 측정하고, 필요하면 SPSC 큐 전환을 검토한다.
