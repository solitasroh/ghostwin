"""
GhostWin Hangul IME Composition Tests (A01-A20)
- pyautogui + SendInput 기반 블랙박스 테스트
- 한글 조합/확정/취소/혼합 시나리오 20개
"""
import time


def run(win, runner, proc=None):
    from helpers import (
        press_key, press_key_down, press_key_up,
        toggle_hangul, capture_cell, capture_window,
        has_glyph, has_overlay_color, is_empty_cell, pixel_diff,
        input_changed, fresh_prompt,
        TestResult, VK_SPACE, VK_BACK, VK_RETURN, VK_RIGHT,
        VK_SHIFT,
    )

    # ---------------------------------------------------------------
    # A01: basic "han" + Space confirm
    # ---------------------------------------------------------------
    r = TestResult("A01: basic han + Space")
    fresh_prompt(win)
    time.sleep(0.3)
    img_before = capture_window(win, "a01_before")
    toggle_hangul()

    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.3)
    img_comp = capture_window(win, "a01_composing")
    diff_comp = pixel_diff(img_before, img_comp)
    r.check("composing visible", diff_comp > 0.0001,
            f"pixel diff={diff_comp:.4f} (composing vs blank)")

    press_key(VK_SPACE)
    time.sleep(0.5)
    img_done = capture_window(win, "a01_confirmed")
    diff_confirm = pixel_diff(img_comp, img_done)
    r.check("confirmed visible", diff_confirm > 0.0001,
            f"pixel diff={diff_confirm:.4f}")
    r.check("overlay gone", diff_confirm > 0.0001,
            f"pixel diff={diff_confirm:.4f} (composing vs confirmed)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A02: BS x3 cancels composition
    # ---------------------------------------------------------------
    r = TestResult("A02: BS x3 cancels composition")
    fresh_prompt(win)
    time.sleep(0.3)
    img_before = capture_window(win, "a02_before")
    toggle_hangul()

    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.15)
    press_key(VK_BACK); time.sleep(0.05)
    press_key(VK_BACK); time.sleep(0.05)
    press_key(VK_BACK)
    time.sleep(0.3)
    img_after = capture_window(win, "a02_bs_cancel")
    diff = pixel_diff(img_before, img_after)
    r.check("cell empty after BS x3", diff < 0.01,
            f"pixel diff={diff:.4f} (< 0.01 means reverted to blank)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A03: fast BS burst (30ms) - race condition test
    # ---------------------------------------------------------------
    r = TestResult("A03: fast BS burst 30ms")
    fresh_prompt(win)
    time.sleep(0.3)
    img_before = capture_window(win, "a03_before")
    toggle_hangul()

    for k in [ord('G'), ord('K'), ord('S')]:
        press_key(k)
        time.sleep(0.03)
    for _ in range(3):
        press_key(VK_BACK)
        time.sleep(0.03)
    time.sleep(0.3)
    img_after = capture_window(win, "a03_fast_bs")
    diff = pixel_diff(img_before, img_after)
    r.check("cell empty after fast BS", diff < 0.01,
            f"pixel diff={diff:.4f} (< 0.01 means reverted to blank)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A04: two syllables "hangul" + Space
    # ---------------------------------------------------------------
    r = TestResult("A04: two syllables hangul + Space")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    for k in [ord('G'), ord('K'), ord('S'), ord('R'), ord('M'), ord('F')]:
        press_key(k)
        time.sleep(0.08)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a04_hangul")
    diff, changed = input_changed(img_before, img)
    r.check("hangul input rendered", changed,
            f"pixel diff={diff:.4f} (>0.001 = input visible)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A05: Enter confirms composition
    # ---------------------------------------------------------------
    r = TestResult("A05: Enter confirms composition")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.15)
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "a05_enter")
    diff, changed = input_changed(img_before, img)
    r.check("han confirmed by Enter", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A06: double consonant "kka" (Shift+R, K)
    # ---------------------------------------------------------------
    r = TestResult("A06: double consonant kka")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key_down(VK_SHIFT)
    press_key(ord('R'))
    press_key_up(VK_SHIFT)
    press_key(ord('K'))
    time.sleep(0.15)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a06_kka")
    diff, changed = input_changed(img_before, img)
    r.check("kka glyph rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A07: compound vowel "wae" (D,H,K,L)
    # ---------------------------------------------------------------
    r = TestResult("A07: compound vowel wae")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    for k in [ord('D'), ord('H'), ord('K'), ord('L')]:
        press_key(k)
        time.sleep(0.08)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a07_wae")
    diff, changed = input_changed(img_before, img)
    r.check("wae glyph rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A08: jongseong split "hana" (G,K,S,K)
    # ---------------------------------------------------------------
    r = TestResult("A08: jongseong split hana")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    for k in [ord('G'), ord('K'), ord('S'), ord('K')]:
        press_key(k)
        time.sleep(0.08)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a08_hana")
    diff, changed = input_changed(img_before, img)
    # 종성분리로 2글자(하+나) 생성 → 단일 글자보다 큰 변화
    r.check("jongseong split rendered", changed,
            f"pixel diff={diff:.4f} (2 wide chars expected)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A09: partial BS then recompose (G,K,S -> BS -> S)
    # ---------------------------------------------------------------
    r = TestResult("A09: partial BS recompose")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.1)
    press_key(VK_BACK)
    time.sleep(0.1)
    press_key(ord('S'))
    time.sleep(0.15)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a09_recompose")
    diff, changed = input_changed(img_before, img)
    r.check("recomposed glyph rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A10: single jamo hieut + Space
    # ---------------------------------------------------------------
    r = TestResult("A10: single jamo hieut + Space")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key(ord('G'))
    time.sleep(0.1)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a10_hieut")
    diff, changed = input_changed(img_before, img)
    r.check("hieut glyph rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A11: single vowel a + Space
    # ---------------------------------------------------------------
    r = TestResult("A11: single vowel a + Space")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key(ord('K'))
    time.sleep(0.1)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a11_vowel_a")
    diff, changed = input_changed(img_before, img)
    r.check("vowel glyph rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A12: han + double Space
    # ---------------------------------------------------------------
    r = TestResult("A12: han + double Space")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.1)
    press_key(VK_SPACE)
    time.sleep(0.1)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a12_double_space")
    diff, changed = input_changed(img_before, img)
    r.check("han glyph rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A13: fast "annyeonghaseyo" (50ms)
    # ---------------------------------------------------------------
    r = TestResult("A13: fast annyeonghaseyo 50ms")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    # 안(D,K,S) 녕(S,U,D) 하(G,K) 세(T,P) 요(D,Y)
    keys = [
        ord('D'), ord('K'), ord('S'),   # 안
        ord('S'), ord('U'), ord('D'),   # 녕
        ord('G'), ord('K'),             # 하
        ord('T'), ord('P'),             # 세
        ord('D'), ord('Y'),             # 요
    ]
    for k in keys:
        press_key(k)
        time.sleep(0.05)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a13_annyeonghaseyo")
    diff, changed = input_changed(img_before, img)
    r.check("annyeonghaseyo rendered", changed,
            f"pixel diff={diff:.4f} (5 wide chars expected)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A14: long input 14 syllables "ganadalamabasa ajacha katapaha"
    # ---------------------------------------------------------------
    r = TestResult("A14: long 14 syllables")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    # 가(R,K) 나(S,K) 다(E,K) 라(F,K) 마(A,K) 바(Q,K) 사(T,K)
    # 아(D,K) 자(W,K) 차(C,K) 카(Z,K) 타(X,K) 파(V,K) 하(G,K)
    syllables = [
        (ord('R'), ord('K')),  # 가
        (ord('S'), ord('K')),  # 나
        (ord('E'), ord('K')),  # 다
        (ord('F'), ord('K')),  # 라
        (ord('A'), ord('K')),  # 마
        (ord('Q'), ord('K')),  # 바
        (ord('T'), ord('K')),  # 사
        (ord('D'), ord('K')),  # 아
        (ord('W'), ord('K')),  # 자
        (ord('C'), ord('K')),  # 차
        (ord('Z'), ord('K')),  # 카
        (ord('X'), ord('K')),  # 타
        (ord('V'), ord('K')),  # 파
        (ord('G'), ord('K')),  # 하
    ]
    for cho, jung in syllables:
        press_key(cho)
        time.sleep(0.05)
        press_key(jung)
        time.sleep(0.05)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a14_long_14")
    diff, changed = input_changed(img_before, img)
    r.check("14 syllables rendered", changed,
            f"pixel diff={diff:.4f} (14 wide chars expected)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A15: consecutive single jamo kkkkk (Z x5)
    # ---------------------------------------------------------------
    r = TestResult("A15: consecutive single jamo kkkkk")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    for _ in range(5):
        press_key(ord('Z'))
        time.sleep(0.08)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a15_kkkkk")
    diff, changed = input_changed(img_before, img)
    r.check("jamo glyphs rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A16: korean + english mixed
    # ---------------------------------------------------------------
    r = TestResult("A16: korean + english mixed")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    # 한
    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.1)
    press_key(VK_SPACE)
    time.sleep(0.2)

    # switch to english
    toggle_hangul()
    time.sleep(0.3)

    press_key(ord('A')); press_key(ord('B')); press_key(ord('C'))
    time.sleep(0.2)

    # switch back to korean
    toggle_hangul()
    time.sleep(0.3)

    # 글
    press_key(ord('R')); press_key(ord('M')); press_key(ord('F'))
    time.sleep(0.1)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a16_mixed")
    diff, changed = input_changed(img_before, img)
    r.check("mixed input rendered", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A17: arrow key confirms composition
    # ---------------------------------------------------------------
    r = TestResult("A17: arrow key confirms composition")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.15)
    press_key(VK_RIGHT)
    time.sleep(0.5)
    img = capture_window(win, "a17_arrow_confirm")
    diff, changed = input_changed(img_before, img)
    r.check("han confirmed by arrow", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A18: compound jongseong split "dak-go" (E,K,F,R -> H)
    # ---------------------------------------------------------------
    r = TestResult("A18: compound jongseong split dakgo")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    # 닭(E,K,F,R) + 고(H) -> "닭" splits to "달" + "고"
    for k in [ord('E'), ord('K'), ord('F'), ord('R')]:
        press_key(k)
        time.sleep(0.08)
    press_key(ord('H'))
    time.sleep(0.15)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a18_dakgo")
    diff, changed = input_changed(img_before, img)
    r.check("compound jongseong split rendered", changed,
            f"pixel diff={diff:.4f} (2 wide chars expected)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A19: partial BS then Space confirms jamo
    # ---------------------------------------------------------------
    r = TestResult("A19: partial BS then Space confirms jamo")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    press_key(ord('G')); press_key(ord('K')); press_key(ord('S'))
    time.sleep(0.1)
    press_key(VK_BACK)
    time.sleep(0.05)
    press_key(VK_BACK)
    time.sleep(0.1)
    press_key(VK_SPACE)
    time.sleep(0.5)
    img = capture_window(win, "a19_jamo_confirm")
    diff, changed = input_changed(img_before, img)
    r.check("hieut jamo confirmed by Space", changed,
            f"pixel diff={diff:.4f}")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A20: long sentence 15 syllables + Enter
    # "daehanmingugui sudoneun seoul teukbyeolsi ibnida"
    # ---------------------------------------------------------------
    r = TestResult("A20: long sentence 15 syllables")
    fresh_prompt(win)
    img_before = capture_window(win)
    toggle_hangul()

    # 대(E,O) 한(G,K,S) 민(A,L,S) 국(R,D,R) 의(M,L)
    # 수(T,N) 도(E,H) 는(S,M,S) 서(T,J) 울(D,N,F)
    # 특(X,M,R) 별(Q,U,D,F) 시(T,L) 입(D,L,Q) 니(S,L)
    # 다(E,K)
    keys = [
        ord('E'), ord('O'),                     # 대
        ord('G'), ord('K'), ord('S'),            # 한
        ord('A'), ord('L'), ord('S'),            # 민
        ord('R'), ord('D'), ord('R'),            # 국
        ord('M'), ord('L'),                      # 의
        ord('T'), ord('N'),                      # 수
        ord('E'), ord('H'),                      # 도
        ord('S'), ord('M'), ord('S'),            # 는
        ord('T'), ord('J'),                      # 서
        ord('D'), ord('N'), ord('F'),            # 울
        ord('X'), ord('M'), ord('R'),            # 특
        ord('Q'), ord('U'), ord('D'), ord('F'),  # 별
        ord('T'), ord('L'),                      # 시
        ord('D'), ord('L'), ord('Q'),            # 입
        ord('S'), ord('L'),                      # 니
        ord('E'), ord('K'),                      # 다
    ]
    for k in keys:
        press_key(k)
        time.sleep(0.05)
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "a20_long_sentence")
    diff, changed = input_changed(img_before, img)
    r.check("long sentence rendered", changed,
            f"pixel diff={diff:.4f} (16 syllables expected)")

    toggle_hangul()
    runner.add(r)

    # ---------------------------------------------------------------
    # A21: BS step-by-step "한→하→ㅎ→empty" intermediate verification
    # ---------------------------------------------------------------
    r = TestResult("A21: BS step-by-step han->ha->hieut->empty")
    fresh_prompt(win)
    time.sleep(0.3)
    # 전체 윈도우 기준 baseline (step4 복원 확인용)
    img_baseline = capture_window(win, "a21_baseline")
    # 하단 3행만 크롭 — 중간 단계 미세 변화 감지용 (입력은 항상 하단)
    from helpers import capture_bottom_rows
    crop_before = capture_bottom_rows(win, 3)
    toggle_hangul()

    # Step 1: G,K,S → "한" (composing)
    press_key(ord('G')); time.sleep(0.05)
    press_key(ord('K')); time.sleep(0.05)
    press_key(ord('S'))
    time.sleep(0.3)
    capture_window(win, "a21_step1_han")
    crop_han = capture_bottom_rows(win, 3)
    diff_han = pixel_diff(crop_before, crop_han)
    r.check("step1: han composing visible", diff_han > 0.001,
            f"baseline->han: diff={diff_han:.4f}")

    # Step 2: BS → "하" (종성 ㄴ 제거)
    press_key(VK_BACK)
    time.sleep(0.3)
    capture_window(win, "a21_step2_ha")
    crop_ha = capture_bottom_rows(win, 3)
    diff_step2 = pixel_diff(crop_han, crop_ha)
    r.check("step2: BS changed han->ha", diff_step2 > 0.001,
            f"han->ha: diff={diff_step2:.4f} (bottom 3 rows)")

    # Step 3: BS → "ㅎ" (중성 제거)
    press_key(VK_BACK)
    time.sleep(0.3)
    capture_window(win, "a21_step3_hieut")
    crop_hieut = capture_bottom_rows(win, 3)
    diff_step3 = pixel_diff(crop_ha, crop_hieut)
    r.check("step3: BS changed ha->hieut", diff_step3 > 0.001,
            f"ha->hieut: diff={diff_step3:.4f} (bottom 3 rows)")

    # Step 4: BS → empty (초성 제거) — 전체 윈도우 기준 복원 확인
    press_key(VK_BACK)
    time.sleep(0.3)
    img_empty = capture_window(win, "a21_step4_empty")
    diff_empty = pixel_diff(img_baseline, img_empty)
    r.check("step4: returned to blank", diff_empty < 0.01,
            f"baseline->empty: diff={diff_empty:.4f} (<0.01)")

    # Step 5: 1초 대기 — 잔상 없음 확인
    time.sleep(1.0)
    img_late = capture_window(win, "a21_step5_late")
    diff_late = pixel_diff(img_empty, img_late)
    r.check("step5: no ghost after 1s", diff_late < 0.005,
            f"empty->1s: diff={diff_late:.4f} (<0.005)")

    toggle_hangul()
    runner.add(r)
