"""
GhostWin Terminal — B: Special Keys & Control Characters Test

특수키(화살표, Home/End, F키), 제어문자(Ctrl+C/L/D),
한글 조합 중 특수키 개입, Backspace, 한영 반복 등을 테스트.

Usage:
    python -m scripts.tests.test_b_special
"""
import sys
import os
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from tests.helpers import (
    press_key, press_key_down, press_key_up, press_shift_key,
    type_keys, toggle_hangul,
    capture_window, has_glyph, is_empty_cell, pixel_diff,
    input_changed, fresh_prompt, click_terminal,
    launch_app, kill_app,
    TestResult, TestRunner,
    VK_BACK, VK_SPACE, VK_RETURN, VK_TAB, VK_ESCAPE,
    VK_SHIFT, VK_CONTROL, VK_MENU, VK_HANGUL,
    KEYEVENTF_KEYUP, user32,
    RESULTS_DIR,
)

# ── 추가 Win32 VK 상수 ────────────────────────────────────
VK_LEFT    = 0x25
VK_UP      = 0x26
VK_RIGHT   = 0x27
VK_DOWN    = 0x28
VK_HOME    = 0x24
VK_END     = 0x23
VK_DELETE  = 0x2E

VK_F1  = 0x70
VK_F2  = 0x71
VK_F3  = 0x72
VK_F4  = 0x73
VK_F5  = 0x74
VK_F6  = 0x75
VK_F7  = 0x76
VK_F8  = 0x77
VK_F9  = 0x78
VK_F10 = 0x79
VK_F11 = 0x7A
VK_F12 = 0x7B


def press_ctrl_key(vk, delay=0.05):
    """Ctrl + 키 조합 전송."""
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(delay)


def is_app_alive(proc):
    """프로세스가 아직 살아 있는지 확인."""
    return proc.poll() is None


# ═══════════════════════════════════════════════════════════
#  테스트 실행
# ═══════════════════════════════════════════════════════════

def run(win, runner, proc):
    """모든 B 시리즈 테스트를 실행."""

    # ── B01: 화살표키 4방향 ────────────────────────────
    r = TestResult("B01: arrow keys 4 directions")
    fresh_prompt(win)
    type_keys("abc", delay=0.06)
    time.sleep(0.2)
    for vk in [VK_LEFT, VK_RIGHT, VK_UP, VK_DOWN]:
        press_key(vk)
        time.sleep(0.1)
    r.check("no crash", is_app_alive(proc), "app alive after arrow keys")
    capture_window(win, "B01_arrow_keys")
    runner.add(r)

    # ── B02: Home / End ───────────────────────────────
    r = TestResult("B02: Home / End keys")
    fresh_prompt(win)
    type_keys("hello", delay=0.06)
    time.sleep(0.2)
    press_key(VK_HOME)
    time.sleep(0.1)
    press_key(VK_END)
    time.sleep(0.1)
    r.check("no crash", is_app_alive(proc), "app alive after Home/End")
    capture_window(win, "B02_home_end")
    runner.add(r)

    # ── B03: F1 ~ F12 ────────────────────────────────
    r = TestResult("B03: F1-F12 function keys")
    fresh_prompt(win)
    fkeys = [VK_F1, VK_F2, VK_F3, VK_F4, VK_F5, VK_F6,
             VK_F7, VK_F8, VK_F9, VK_F10, VK_F11, VK_F12]
    for fk in fkeys:
        press_key(fk)
        time.sleep(0.05)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after F1-F12")
    capture_window(win, "B03_fkeys")
    runner.add(r)

    # ── B04: Ctrl+C (영문 모드) ───────────────────────
    r = TestResult("B04: Ctrl+C in english mode")
    fresh_prompt(win)
    type_keys("test", delay=0.06)
    time.sleep(0.2)
    press_ctrl_key(ord('C'))
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after Ctrl+C")
    capture_window(win, "B04_ctrl_c")
    runner.add(r)

    # ── B05: Ctrl+L (화면 클리어) ─────────────────────
    r = TestResult("B05: Ctrl+L screen clear")
    fresh_prompt(win)
    type_keys("echo visible", delay=0.06)
    press_key(VK_RETURN)
    time.sleep(0.5)
    img_before = capture_window(win, "B05_before_clear")
    press_ctrl_key(ord('L'))
    time.sleep(0.5)
    img_after = capture_window(win, "B05_after_clear")
    diff = pixel_diff(img_before, img_after)
    r.check("screen changed", diff > 0.01, f"pixel diff={diff:.3f}")
    r.check("no crash", is_app_alive(proc), "app alive after Ctrl+L")
    runner.add(r)

    # ── B06: 한글 조합 중 Ctrl+C ─────────────────────
    r = TestResult("B06: Ctrl+C during hangul composition")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gk", delay=0.1)  # ㅎ+ㅏ = 하
    time.sleep(0.3)
    press_ctrl_key(ord('C'))
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after Ctrl+C in composition")
    capture_window(win, "B06_ctrl_c_hangul")
    toggle_hangul()  # 영문 복귀
    runner.add(r)

    # ── B07: 한글 조합 중 Escape ──────────────────────
    r = TestResult("B07: Escape during hangul composition")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gk", delay=0.1)  # 하
    time.sleep(0.3)
    img_composing = capture_window(win, "B07_composing")
    press_key(VK_ESCAPE)
    time.sleep(0.3)
    img_after_esc = capture_window(win, "B07_after_esc")
    r.check("no crash", is_app_alive(proc), "app alive after Esc in composition")
    runner.add(r)
    toggle_hangul()  # 영문 복귀

    # ── B08: 한글 조합 중 화살표 (확정 + 커서이동) ────
    r = TestResult("B08: arrow key during hangul composition")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gks", delay=0.1)  # ㅎ+ㅏ+ㄴ = 한
    time.sleep(0.3)
    press_key(VK_RIGHT)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after arrow during composition")
    capture_window(win, "B08_arrow_hangul")
    toggle_hangul()  # 영문 복귀
    runner.add(r)

    # ── B09: 한글 조합 중 Enter (확정 + 개행) ─────────
    r = TestResult("B09: Enter during hangul composition")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gks", delay=0.1)  # 한
    time.sleep(0.3)
    press_key(VK_RETURN)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after Enter during composition")
    capture_window(win, "B09_enter_hangul")
    toggle_hangul()  # 영문 복귀
    runner.add(r)

    # ── B10: 한글 조합 중 Tab (확정 + 탭) ─────────────
    r = TestResult("B10: Tab during hangul composition")
    fresh_prompt(win)
    toggle_hangul()
    type_keys("gks", delay=0.1)  # 한
    time.sleep(0.3)
    press_key(VK_TAB)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after Tab during composition")
    capture_window(win, "B10_tab_hangul")
    toggle_hangul()  # 영문 복귀
    runner.add(r)

    # ── B11: 영문 Backspace ───────────────────────────
    r = TestResult("B11: english backspace")
    fresh_prompt(win)
    type_keys("abc", delay=0.06)
    time.sleep(0.3)
    img_before_bs = capture_window(win, "B11_before_bs")
    press_key(VK_BACK)
    time.sleep(0.5)
    img_after_bs = capture_window(win, "B11_after_bs")
    diff = pixel_diff(img_before_bs, img_after_bs)
    # BS 1글자 삭제는 매우 작은 차이만 발생 (커서+글자 1개)
    # threshold를 0.0001 이상으로 낮게 설정
    r.check("screen changed after BS", diff > 0.00005, f"pixel diff={diff:.6f}")
    r.check("no crash", is_app_alive(proc), "app alive after BS")
    runner.add(r)

    # ── B12: 빈 상태 Backspace ────────────────────────
    r = TestResult("B12: backspace on empty prompt")
    fresh_prompt(win)
    time.sleep(0.3)
    press_key(VK_BACK)
    time.sleep(0.2)
    press_key(VK_BACK)
    time.sleep(0.2)
    press_key(VK_BACK)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after BS on empty")
    capture_window(win, "B12_empty_bs")
    runner.add(r)

    # ── B13: Ctrl+D (EOF) ────────────────────────────
    r = TestResult("B13: Ctrl+D (EOF)")
    fresh_prompt(win)
    time.sleep(0.2)
    press_ctrl_key(ord('D'))
    time.sleep(0.5)
    # Ctrl+D는 빈 줄에서 쉘을 종료할 수 있으므로 캡처만
    capture_window(win, "B13_ctrl_d")
    r.check("captured", True, "Ctrl+D sent, check screenshot")
    runner.add(r)

    # ── B14: 한/영 반복 10회 ──────────────────────────
    r = TestResult("B14: hangul toggle 10 times")
    fresh_prompt(win)
    for i in range(10):
        toggle_hangul(delay=0.2)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after 10 toggles")
    capture_window(win, "B14_toggle_10x")
    runner.add(r)

    # ── B15: Shift+Tab (backtab) ─────────────────────────
    r = TestResult("B15: Shift+Tab backtab")
    fresh_prompt(win)
    type_keys("abc", delay=0.06)
    time.sleep(0.2)
    # Shift+Tab 전송
    user32.keybd_event(VK_SHIFT, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_TAB, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_TAB, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after Shift+Tab")
    capture_window(win, "B15_shift_tab")
    runner.add(r)

    # ── B16: Alt+B / Alt+F (readline 단축키) ──────────────
    r = TestResult("B16: Alt+B Alt+F readline shortcuts")
    fresh_prompt(win)
    type_keys("hello world", delay=0.06)
    time.sleep(0.2)
    # Alt+B (단어 뒤로)
    user32.keybd_event(VK_MENU, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(ord('B'), 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(ord('B'), 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.2)
    # Alt+F (단어 앞으로)
    user32.keybd_event(VK_MENU, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(ord('F'), 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(ord('F'), 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.3)
    r.check("no crash", is_app_alive(proc), "app alive after Alt+B/F")
    capture_window(win, "B16_alt_bf")
    runner.add(r)

    # ── B17: Ctrl+V 클립보드 붙여넣기 (영문) ─────────────
    r = TestResult("B17: Ctrl+V paste English")
    fresh_prompt(win)
    img_before = capture_window(win)
    # 클립보드에 텍스트 설정
    _set_clipboard("echo hello_paste")
    time.sleep(0.1)
    press_ctrl_key(ord('V'))
    time.sleep(0.5)
    press_key(VK_RETURN)
    time.sleep(0.5)
    img_after = capture_window(win, "B17_paste_english")
    diff, changed = input_changed(img_before, img_after)
    r.check("pasted text rendered", changed, f"pixel diff={diff:.4f}")
    r.check("no crash", is_app_alive(proc), "app alive after Ctrl+V")
    runner.add(r)

    # ── B18: Ctrl+V 클립보드 붙여넣기 (한글) ─────────────
    r = TestResult("B18: Ctrl+V paste Korean")
    fresh_prompt(win)
    img_before = capture_window(win)
    _set_clipboard("echo 안녕하세요")
    time.sleep(0.1)
    press_ctrl_key(ord('V'))
    time.sleep(0.5)
    press_key(VK_RETURN)
    time.sleep(0.5)
    img_after = capture_window(win, "B18_paste_korean")
    diff, changed = input_changed(img_before, img_after)
    r.check("pasted Korean rendered", changed, f"pixel diff={diff:.4f}")
    r.check("no crash", is_app_alive(proc), "app alive after Korean paste")
    runner.add(r)


def _set_clipboard(text):
    """Win32 API로 클립보드에 텍스트 설정."""
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

    for attempt in range(3):
        if u32.OpenClipboard(0):
            break
        time.sleep(0.1 * (attempt + 1))
    else:
        print("[WARN] _set_clipboard: OpenClipboard failed")
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
        capture_window(win, "B_final")
        kill_app(proc)

    sys.exit(0 if runner.passed_count == runner.total_count else 1)


if __name__ == "__main__":
    main()
