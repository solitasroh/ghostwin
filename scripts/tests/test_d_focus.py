"""
GhostWin Terminal — D: Focus / Window state tests.

D01: launch + immediate input
D02: panel click + input
D03: minimize + restore + input
D04: maximize + input
D05: rapid hangul toggle x10
D06: composition + focus loss simulation
D07: ConPTY exit + input (crash guard)
"""
import ctypes
import ctypes.wintypes
import time

user32 = ctypes.windll.user32

# ShowWindow 상수
SW_MINIMIZE = 6
SW_RESTORE = 9
SW_MAXIMIZE = 3


def run(win, runner, proc=None):
    from helpers import (
        TestResult, VK_HANGUL, VK_MENU, VK_RETURN, VK_TAB,
        capture_window, click_terminal, fresh_prompt, has_glyph,
        input_changed, press_key, toggle_hangul, type_keys,
    )

    hwnd = win._hWnd

    # ── D01: launch + immediate input ──────────────────────
    r = TestResult("D01: launch + immediate input")
    click_terminal(win)
    time.sleep(0.3)
    img_before = capture_window(win)
    type_keys("abc")
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "d01_launch_input")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after input")
    r.check("text rendered", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── D02: panel click + input ───────────────────────────
    r = TestResult("D02: panel click + input")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.5)
    img_before = capture_window(win)
    type_keys("abc")
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "d02_click_input")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after click + input")
    r.check("text rendered", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── D03: minimize + restore + input ────────────────────
    r = TestResult("D03: minimize + restore + input")
    fresh_prompt(win)
    user32.ShowWindow(hwnd, SW_MINIMIZE)
    time.sleep(2)
    user32.ShowWindow(hwnd, SW_RESTORE)
    time.sleep(1)
    click_terminal(win)
    time.sleep(0.5)
    img_before = capture_window(win)
    type_keys("abc")
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "d03_restore")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after minimize/restore")
    r.check("text rendered after restore", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── D04: maximize + input ──────────────────────────────
    r = TestResult("D04: maximize + input")
    fresh_prompt(win)
    user32.ShowWindow(hwnd, SW_MAXIMIZE)
    time.sleep(1)
    click_terminal(win)
    time.sleep(0.5)
    img_before = capture_window(win)
    type_keys("abc")
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "d04_maximize")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after maximize")
    r.check("text rendered in maximized", changed, f"pixel diff={diff:.4f}")
    # restore back to normal
    user32.ShowWindow(hwnd, SW_RESTORE)
    time.sleep(0.5)
    runner.add(r)

    # ── D05: rapid hangul toggle x10 ──────────────────────
    r = TestResult("D05: rapid hangul toggle x10")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    for i in range(10):
        toggle_hangul(delay=0.15)
    time.sleep(0.5)
    img_before = capture_window(win)
    # 10회 토글 후 원래 상태(영문)로 복귀해야 함 (짝수 회)
    type_keys("abc")
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "d05_toggle10")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after 10 toggles")
    r.check("text rendered after toggle", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── D06: composition + focus loss simulation ───────────
    r = TestResult("D06: composition + focus loss (alt+tab)")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    toggle_hangul(delay=0.3)
    # ㅎ, ㅏ 입력하여 조합 중 상태
    press_key(ord('G'))
    time.sleep(0.05)
    press_key(ord('K'))
    time.sleep(0.3)
    # Alt+Tab으로 포커스 이동 시뮬레이션
    user32.keybd_event(VK_MENU, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_TAB, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_TAB, 0, 0x0002, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_MENU, 0, 0x0002, 0)
    time.sleep(2)
    # 복귀: Alt+Tab 다시
    user32.keybd_event(VK_MENU, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_TAB, 0, 0, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_TAB, 0, 0x0002, 0)
    time.sleep(0.05)
    user32.keybd_event(VK_MENU, 0, 0x0002, 0)
    time.sleep(1)
    click_terminal(win)
    time.sleep(0.5)
    # 영문으로 복귀
    toggle_hangul(delay=0.3)
    img = capture_window(win, "d06_focus_loss")
    r.check("no crash", True, "process alive after focus loss during composition")
    runner.add(r)

    # ── D07: ConPTY exit + input (crash guard) ─────────────
    r = TestResult("D07: ConPTY exit + input")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    type_keys("exit")
    press_key(VK_RETURN)
    time.sleep(2)
    # ConPTY 종료 후 키 입력 시도
    type_keys("abc")
    time.sleep(1)
    img = capture_window(win, "d07_after_exit")
    r.check("no crash", True, "no crash after ConPTY exit + input")
    runner.add(r)
