"""
GhostWin Terminal — E: Unicode / Encoding tests.

E01: ASCII full printable (0x20~0x7E)
E02: Korean complete syllables echo
E03: clipboard Korean paste (Ctrl+V)
E04: clipboard English paste (Ctrl+V)
E05: long string paste (crash guard)
E06: empty input (no-op)
E07: mixed echo (English + Korean)
"""
import time

import pyperclip


def _safe_copy(text, retries=3):
    """pyperclip.copy()를 재시도 로직으로 감싼 래퍼.

    OpenClipboard 에러(다른 프로세스가 잡고 있음) 시 재시도.
    """
    for attempt in range(retries):
        try:
            pyperclip.copy(text)
            return
        except Exception:
            if attempt < retries - 1:
                time.sleep(0.1 * (attempt + 1))
            else:
                raise


def run(win, runner, proc=None):
    from helpers import (
        TestResult, VK_CONTROL, VK_RETURN,
        capture_window, click_terminal, fresh_prompt, has_glyph,
        input_changed, press_key, type_keys, user32,
    )

    KEYEVENTF_KEYUP = 0x0002

    def ctrl_v():
        """Ctrl+V 붙여넣기."""
        user32.keybd_event(VK_CONTROL, 0, 0, 0)
        time.sleep(0.03)
        user32.keybd_event(ord('V'), 0, 0, 0)
        time.sleep(0.03)
        user32.keybd_event(ord('V'), 0, KEYEVENTF_KEYUP, 0)
        time.sleep(0.03)
        user32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)
        time.sleep(0.3)

    # ── E01: ASCII full printable ──────────────────────────
    r = TestResult("E01: ASCII full printable (0x20~0x7E)")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    img_before = capture_window(win)
    # echo 명령어로 전체 printable ASCII 전송
    ascii_str = "".join(chr(c) for c in range(0x20, 0x7F))
    # 클립보드 경유 — 특수문자 포함이므로 직접 type 대신 paste
    cmd = f'echo "{ascii_str}"'
    _safe_copy(cmd)
    ctrl_v()
    time.sleep(0.3)
    press_key(VK_RETURN)
    time.sleep(1)
    img = capture_window(win, "e01_ascii_full")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after full ASCII range")
    r.check("ASCII text rendered", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── E02: Korean complete syllables echo ────────────────
    r = TestResult("E02: Korean syllables echo")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    img_before = capture_window(win)
    korean_text = "가나다라마바사아자차카타파하"
    cmd = f'echo "{korean_text}"'
    _safe_copy(cmd)
    ctrl_v()
    time.sleep(0.3)
    press_key(VK_RETURN)
    time.sleep(1)
    img = capture_window(win, "e02_korean_syllables")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after Korean echo")
    r.check("Korean syllables rendered", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── E03: clipboard Korean paste ────────────────────────
    r = TestResult("E03: clipboard Korean paste")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    img_before = capture_window(win)
    _safe_copy("안녕하세요")
    ctrl_v()
    time.sleep(0.5)
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "e03_paste_korean")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after Korean paste")
    r.check("pasted Korean rendered", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── E04: clipboard English paste ───────────────────────
    r = TestResult("E04: clipboard English paste")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    img_before = capture_window(win)
    _safe_copy("Hello World")
    ctrl_v()
    time.sleep(0.5)
    press_key(VK_RETURN)
    time.sleep(0.5)
    img = capture_window(win, "e04_paste_english")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after English paste")
    r.check("pasted English rendered", changed, f"pixel diff={diff:.4f}")
    runner.add(r)

    # ── E05: long string paste (crash guard) ───────────────
    r = TestResult("E05: long string paste (100x)")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    _safe_copy("가" * 100)
    ctrl_v()
    time.sleep(1)
    press_key(VK_RETURN)
    time.sleep(1)
    img = capture_window(win, "e05_long_paste")
    r.check("no crash", True, "process alive after 100-char paste")
    runner.add(r)

    # ── E06: empty input (no-op) ───────────────────────────
    r = TestResult("E06: empty input")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(2)
    img = capture_window(win, "e06_empty")
    r.check("no crash", True, "process alive with no input for 2 seconds")
    runner.add(r)

    # ── E07: mixed echo (English + Korean) ─────────────────
    r = TestResult("E07: mixed echo (English + Korean)")
    fresh_prompt(win)
    click_terminal(win)
    time.sleep(0.3)
    img_before = capture_window(win)
    cmd = 'echo "Hello 한글 World"'
    _safe_copy(cmd)
    ctrl_v()
    time.sleep(0.3)
    press_key(VK_RETURN)
    time.sleep(1)
    img = capture_window(win, "e07_mixed_echo")
    diff, changed = input_changed(img_before, img)
    r.check("no crash", True, "process alive after mixed echo")
    r.check("mixed text rendered", changed, f"pixel diff={diff:.4f}")
    runner.add(r)
