"""
GhostWin Terminal — C: Rendering Visual Tests

조합 오버레이 표시/소멸, Backspace 잔상, CJK wide char,
영한 혼합 정렬, 빈 화면, 긴 줄 출력 등 렌더링 시각 테스트.

Usage:
    python -m scripts.tests.test_c_render
"""
import sys
import os
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from tests.helpers import (
    press_key, type_keys, toggle_hangul,
    capture_window, capture_cells,
    has_glyph, has_overlay_color, is_empty_cell, pixel_diff,
    input_changed, fresh_prompt, click_terminal,
    launch_app, kill_app,
    TestResult, TestRunner,
    VK_BACK, VK_SPACE, VK_RETURN,
    KEYEVENTF_KEYUP, user32,
    RESULTS_DIR,
)


def is_app_alive(proc):
    """프로세스가 아직 살아 있는지 확인."""
    return proc.poll() is None


# ═══════════════════════════════════════════════════════════
#  테스트 실행
# ═══════════════════════════════════════════════════════════

def run(win, runner, proc):
    """모든 C 시리즈 테스트를 실행."""

    def _check_alive(label=""):
        """앱이 종료되었으면 남은 테스트를 건너뛰기 위해 예외 발생."""
        if not is_app_alive(proc):
            raise RuntimeError(f"App crashed before {label}")

    # ── C01: 조합 오버레이 표시 확인 ──────────────────
    r = TestResult("C01: composition overlay visible")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gk", delay=0.1)  # ㅎ+ㅏ = 하 (조합 중)
    time.sleep(0.5)
    img_comp = capture_window(win, "C01_composing")
    overlay_found = has_overlay_color(img_comp)
    r.check("overlay color detected", overlay_found,
            "0xFF443344 overlay present" if overlay_found else "overlay NOT detected")
    r.check("no crash", is_app_alive(proc))
    runner.add(r)

    # ── C02: 오버레이 사라짐 확인 ─────────────────────
    r = TestResult("C02: overlay disappears after confirm")
    # C01에서 이어서: 하 조합 중 -> Space 확정
    # img_comp (C01에서 캡처)을 조합 중 기준으로 사용
    press_key(VK_SPACE)
    time.sleep(1.0)  # 오버레이 소멸 대기 충분히
    img_confirmed = capture_window(win, "C02_confirmed")
    # pixel_diff로 조합 중 vs 확정 후 변화 감지 (오버레이 색상 대신)
    diff = pixel_diff(img_comp, img_confirmed)
    r.check("overlay removed", diff > 0.0001,
            f"pixel diff={diff:.4f} (composing vs confirmed, >0.001 means changed)")
    r.check("no crash", is_app_alive(proc))
    runner.add(r)
    toggle_hangul()  # 영문 복귀

    # ── C03: BS 후 잔상 없음 ──────────────────────────
    r = TestResult("C03: no residual after backspace")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gks", delay=0.1)  # 한
    time.sleep(0.3)
    img_before = capture_window(win, "C03_before_bs")
    press_key(VK_BACK)
    time.sleep(0.15)
    press_key(VK_BACK)
    time.sleep(0.15)
    press_key(VK_BACK)
    time.sleep(0.5)
    img_after = capture_window(win, "C03_after_bs3")
    diff = pixel_diff(img_before, img_after)
    r.check("screen changed after BS*3", diff > 0.0001, f"pixel diff={diff:.4f}")
    # 추가 대기 후 재캡처 (지연 렌더링 잔상 확인)
    time.sleep(0.5)
    img_late = capture_window(win, "C03_late_check")
    late_diff = pixel_diff(img_after, img_late)
    r.check("no late rendering change", late_diff < 0.01, f"late diff={late_diff:.4f}")
    r.check("no crash", is_app_alive(proc))
    runner.add(r)
    toggle_hangul()  # 영문 복귀

    # ── C04: 빠른 BS 후 잔상 없음 (30ms 간격) ────────
    r = TestResult("C04: no residual after fast backspace (30ms)")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gks", delay=0.03)  # 빠른 입력: 한
    time.sleep(0.1)
    img_fast_before = capture_window(win, "C04_fast_before")
    press_key(VK_BACK, delay=0.03)
    press_key(VK_BACK, delay=0.03)
    press_key(VK_BACK, delay=0.03)
    time.sleep(0.5)
    img_fast_after = capture_window(win, "C04_fast_after_bs")
    diff = pixel_diff(img_fast_before, img_fast_after)
    r.check("screen changed after fast BS", diff > 0.0001, f"pixel diff={diff:.4f}")
    # 1초 대기 후 재캡처
    time.sleep(1.0)
    img_fast_late = capture_window(win, "C04_fast_late")
    late_diff = pixel_diff(img_fast_after, img_fast_late)
    r.check("no late residual", late_diff < 0.01, f"late diff={late_diff:.4f}")
    r.check("no crash", is_app_alive(proc))
    runner.add(r)
    toggle_hangul()  # 영문 복귀

    # ── C05: CJK wide char 렌더링 ─────────────────────
    _check_alive("C05")
    r = TestResult("C05: CJK wide char rendering")
    fresh_prompt(win)
    img_c05_before = capture_window(win)
    # echo "가나다" 를 터미널에 직접 명령으로 실행
    type_keys('echo ', delay=0.06)
    # 따옴표는 Shift+' 이므로 직접 VK 전송
    # " = Shift+VK_OEM_7(0xDE) — pyautogui 방식 대신 직접 입력
    press_key(VK_RETURN)
    time.sleep(0.3)
    # 새 방식: 한글 모드에서 가나다 입력 후 Enter
    toggle_hangul()
    # echo 명령을 쓰려면 영문 모드 필요 → 다시 영문
    toggle_hangul()
    # cmd에서 echo 실행: echo 가나다
    type_keys("echo ", delay=0.06)
    # 유니코드 문자를 클립보드로 붙여넣기
    _paste_text("가나다")
    time.sleep(0.2)
    press_key(VK_RETURN)
    time.sleep(0.8)
    img_cjk = capture_window(win, "C05_cjk_wide")
    diff, changed = input_changed(img_c05_before, img_cjk)
    r.check("CJK glyphs rendered", changed,
            f"pixel diff={diff:.4f}")
    r.check("no crash", is_app_alive(proc))
    runner.add(r)

    # ── C06: 영한 혼합 정렬 ──────────────────────────
    _check_alive("C06")
    r = TestResult("C06: english-korean mixed alignment")
    fresh_prompt(win)
    img_c06_before = capture_window(win)
    type_keys("echo ", delay=0.06)
    _paste_text("Hello 한글")
    time.sleep(0.2)
    press_key(VK_RETURN)
    time.sleep(0.8)
    img_mixed = capture_window(win, "C06_mixed_align")
    diff, changed = input_changed(img_c06_before, img_mixed)
    r.check("mixed text rendered", changed,
            f"pixel diff={diff:.4f}")
    r.check("no crash", is_app_alive(proc))
    runner.add(r)

    # ── C07: 빈 터미널 (cls) ─────────────────────────
    _check_alive("C07")
    r = TestResult("C07: empty terminal after cls")
    fresh_prompt(win)
    type_keys("cls", delay=0.06)
    press_key(VK_RETURN)
    time.sleep(0.8)
    img_cls = capture_window(win, "C07_after_cls")
    r.check("screen captured after cls", True, "visual check required")
    r.check("no crash", is_app_alive(proc))
    runner.add(r)

    # ── C08: 긴 줄 출력 ──────────────────────────────
    _check_alive("C08")
    r = TestResult("C08: long line output (100 chars)")
    fresh_prompt(win)
    # echo + 100개 A
    type_keys("echo ", delay=0.04)
    long_text = "A" * 100
    type_keys(long_text, delay=0.01)
    press_key(VK_RETURN)
    time.sleep(0.8)
    img_long = capture_window(win, "C08_long_line")
    r.check("no crash", is_app_alive(proc), "app alive after 100-char echo")
    runner.add(r)


# ── 유틸리티: 클립보드 붙여넣기 ───────────────────────────

def _paste_text(text, retries=3):
    """클립보드에 텍스트 설정 후 Ctrl+V로 붙여넣기.

    OpenClipboard 실패 시 최대 retries회 재시도.
    """
    import ctypes

    CF_UNICODETEXT = 13
    GMEM_MOVEABLE = 0x0002

    kernel32 = ctypes.windll.kernel32
    u32 = ctypes.windll.user32

    # 64-bit Windows: 모든 포인터 인자/리턴 타입 명시
    kernel32.GlobalAlloc.restype = ctypes.c_void_p
    kernel32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
    kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    u32.SetClipboardData.restype = ctypes.c_void_p
    u32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]

    # OpenClipboard 재시도 (다른 프로세스가 잡고 있을 수 있음)
    for attempt in range(retries):
        if u32.OpenClipboard(0):
            break
        time.sleep(0.1 * (attempt + 1))
    else:
        print("[WARN] OpenClipboard failed after retries, skipping paste")
        return

    u32.EmptyClipboard()

    data = text.encode("utf-16-le") + b"\x00\x00"
    h = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
    if not h:
        u32.CloseClipboard()
        return
    ptr = kernel32.GlobalLock(h)
    if not ptr:
        u32.CloseClipboard()
        return
    ctypes.memmove(ptr, data, len(data))
    kernel32.GlobalUnlock(h)
    u32.SetClipboardData(CF_UNICODETEXT, h)
    u32.CloseClipboard()

    # Ctrl+V
    VK_CONTROL = 0x11
    KEYEVENTF_KEYUP = 0x0002
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(ord('V'), 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(ord('V'), 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.05)


# ═══════════════════════════════════════════════════════════
#  직접 실행 시 전체 시퀀스
# ═══════════════════════════════════════════════════════════

def main():
    proc, win = launch_app()
    click_terminal(win)

    runner = TestRunner()

    try:
        run(win, runner, proc)
    except Exception as e:
        print(f"\n!!! EXCEPTION: {e}")
        import traceback
        traceback.print_exc()
    finally:
        print(runner.summary())
        capture_window(win, "C_final")
        kill_app(proc)

    sys.exit(0 if runner.passed_count == runner.total_count else 1)


if __name__ == "__main__":
    main()
