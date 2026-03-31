"""
GhostWin Terminal — E2E test helpers.

pyautogui + keybd_event 기반 공통 유틸리티.
  - 키 입력 (영문, 한글 2벌식)
  - 화면/셀 캡처 및 픽셀 분석
  - 앱 실행/종료
  - 테스트 결과 수집
"""
import ctypes
import ctypes.wintypes
import json
import os
import subprocess
import sys
import time
import unicodedata

import pyautogui
import pygetwindow as gw
from PIL import Image

pyautogui.FAILSAFE = False

user32 = ctypes.windll.user32

# ── Win32 상수 ──────────────────────────────────────────────
VK_BACK    = 0x08
VK_TAB     = 0x09
VK_RETURN  = 0x0D
VK_SHIFT   = 0x10
VK_CONTROL = 0x11
VK_MENU    = 0x12  # Alt
VK_HANGUL  = 0x15
VK_ESCAPE  = 0x1B
VK_SPACE   = 0x20
VK_PRIOR   = 0x21  # PageUp
VK_NEXT    = 0x22  # PageDown
VK_END     = 0x23
VK_HOME    = 0x24
VK_LEFT    = 0x25
VK_UP      = 0x26
VK_RIGHT   = 0x27
VK_DOWN    = 0x28
VK_INSERT  = 0x2D
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

KEYEVENTF_KEYUP = 0x0002

# ── 프로젝트 경로 ──────────────────────────────────────────
PROJECT_DIR = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DEFAULT_EXE = os.path.join(PROJECT_DIR, "build", "ghostwin_winui.exe")
RESULTS_DIR = os.path.join(PROJECT_DIR, "test_results")
WINDOW_TITLE = "GhostWin Terminal"

# ── 스크린샷 디렉토리 자동 생성 ────────────────────────────
os.makedirs(RESULTS_DIR, exist_ok=True)


# ═══════════════════════════════════════════════════════════
#  키 입력
# ═══════════════════════════════════════════════════════════

def press_key(vk, delay=0.03):
    """단일 키 누름 (keybd_event)."""
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(delay)


def press_key_down(vk):
    """키 누르기만 (up 이벤트 없음). press_key_up과 쌍으로 사용."""
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.01)


def press_key_up(vk):
    """키 떼기만. press_key_down과 쌍으로 사용."""
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)


def press_shift_key(vk, delay=0.03):
    """Shift + 키 누름."""
    user32.keybd_event(VK_SHIFT, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.01)
    user32.keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(delay)


def type_keys(keys, delay=0.08):
    """문자열의 각 문자를 VK로 변환하여 순차 입력 (영문 ASCII만)."""
    for ch in keys:
        vk = ord(ch.upper())
        press_key(vk, delay=delay)


def toggle_hangul(delay=0.5):
    """VK_HANGUL(0x15) 전송 + 대기."""
    press_key(VK_HANGUL, delay=delay)


# ── 2벌식 한글 입력 ────────────────────────────────────────

# 초성 19자 (유니코드 순)
_CHOSEONG = list("ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ")
# 중성 21자
_JUNGSEONG = list("ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ")
# 종성 28자 (첫 항목 = 종성 없음)
_JONGSEONG = [
    "", "ㄱ", "ㄲ", "ㄳ", "ㄴ", "ㄵ", "ㄶ", "ㄷ",
    "ㄹ", "ㄺ", "ㄻ", "ㄼ", "ㄽ", "ㄾ", "ㄿ", "ㅀ",
    "ㅁ", "ㅂ", "ㅄ", "ㅅ", "ㅆ", "ㅇ", "ㅈ", "ㅊ",
    "ㅋ", "ㅌ", "ㅍ", "ㅎ",
]

# 2벌식 자모 -> 키 매핑
# 값이 str 이면 단일 키, tuple(str, True)이면 Shift+키,
# tuple(str, str, ...)이면 순차 키 입력 (복합 모음)
KOREAN_2BUL = {
    # 자음 (초성/종성 공통)
    "ㄱ": "R",  "ㄲ": ("R", True),
    "ㄴ": "S",  "ㄷ": "E",   "ㄸ": ("E", True),
    "ㄹ": "F",  "ㅁ": "A",   "ㅂ": "Q",   "ㅃ": ("Q", True),
    "ㅅ": "T",  "ㅆ": ("T", True),
    "ㅇ": "D",  "ㅈ": "W",   "ㅉ": ("W", True),
    "ㅊ": "C",  "ㅋ": "Z",   "ㅌ": "X",
    "ㅍ": "V",  "ㅎ": "G",
    # 단일 모음
    "ㅏ": "K",  "ㅐ": "O",   "ㅑ": "I",   "ㅒ": ("O", True),
    "ㅓ": "J",  "ㅔ": "P",   "ㅕ": "U",   "ㅖ": ("P", True),
    "ㅗ": "H",  "ㅛ": "Y",
    "ㅜ": "N",  "ㅠ": "B",
    "ㅡ": "M",  "ㅣ": "L",
    # 복합 모음 (순차 키 시퀀스)
    "ㅘ": ("H", "K"),  "ㅙ": ("H", "O"),  "ㅚ": ("H", "L"),
    "ㅝ": ("N", "J"),  "ㅞ": ("N", "P"),  "ㅟ": ("N", "L"),
    "ㅢ": ("M", "L"),
}

# 겹받침 분해 (종성용)
_DOUBLE_JONGSEONG = {
    "ㄳ": ("ㄱ", "ㅅ"), "ㄵ": ("ㄴ", "ㅈ"), "ㄶ": ("ㄴ", "ㅎ"),
    "ㄺ": ("ㄹ", "ㄱ"), "ㄻ": ("ㄹ", "ㅁ"), "ㄼ": ("ㄹ", "ㅂ"),
    "ㄽ": ("ㄹ", "ㅅ"), "ㄾ": ("ㄹ", "ㅌ"), "ㄿ": ("ㄹ", "ㅍ"),
    "ㅀ": ("ㄹ", "ㅎ"), "ㅄ": ("ㅂ", "ㅅ"),
}


def decompose_hangul(ch):
    """한글 완성형 음절을 초성+중성+종성 자모 리스트로 분해.

    예: '한' -> ['ㅎ', 'ㅏ', 'ㄴ']
        '가' -> ['ㄱ', 'ㅏ']
    """
    code = ord(ch)
    if not (0xAC00 <= code <= 0xD7A3):
        return [ch]  # 한글 완성형이 아니면 그대로

    offset = code - 0xAC00
    cho_idx = offset // (21 * 28)
    jung_idx = (offset % (21 * 28)) // 28
    jong_idx = offset % 28

    jamos = [_CHOSEONG[cho_idx], _JUNGSEONG[jung_idx]]
    if jong_idx != 0:
        jamos.append(_JONGSEONG[jong_idx])
    return jamos


def _send_jamo(jamo, delay):
    """단일 자모에 해당하는 키를 전송."""
    mapping = KOREAN_2BUL.get(jamo)
    if mapping is None:
        return

    if isinstance(mapping, str):
        # 단일 키
        press_key(ord(mapping), delay=delay)
    elif isinstance(mapping, tuple) and len(mapping) == 2 and mapping[1] is True:
        # Shift + 키 (쌍자음/쌍모음)
        press_shift_key(ord(mapping[0]), delay=delay)
    elif isinstance(mapping, tuple):
        # 복합 모음: 순차 키 입력
        for k in mapping:
            if k is True:
                continue
            press_key(ord(k), delay=delay)


def type_korean_2bul(text, delay=0.1):
    """한글 문자열을 2벌식 키 시퀀스로 변환하여 입력.

    예: "한글" -> G,K,S,R,M,F
    """
    for ch in text:
        jamos = decompose_hangul(ch)
        for jamo in jamos:
            if jamo in _DOUBLE_JONGSEONG:
                # 겹받침: 두 자모로 분해하여 전송
                for sub in _DOUBLE_JONGSEONG[jamo]:
                    _send_jamo(sub, delay)
            else:
                _send_jamo(jamo, delay)


# ═══════════════════════════════════════════════════════════
#  화면 캡처
# ═══════════════════════════════════════════════════════════

def capture_window(win, name=None):
    """전체 윈도우 스크린샷. name을 지정하면 파일로 저장."""
    region = (win.left, win.top, win.width, win.height)
    img = pyautogui.screenshot(region=region)
    if name:
        path = os.path.join(RESULTS_DIR, f"{name}.png")
        img.save(path)
    return img


def capture_cell(win, row, col, cell_w=9, cell_h=19, sidebar=220, titlebar=32):
    """특정 터미널 셀 영역만 캡처 (크롭). (legacy — capture_cell_at 권장)

    CJK 문자의 경우 2셀 너비를 캡처하므로 wide=True 시 cell_w*2.
    반환값: PIL.Image
    """
    x = win.left + sidebar + col * cell_w + 8   # 8px window border
    y = win.top + titlebar + row * cell_h
    w = cell_w * 2  # CJK double-width
    h = cell_h
    return pyautogui.screenshot(region=(x, y, w, h))


def capture_cells(win, row, col_start, col_end, cell_w=9, cell_h=19, sidebar=220, titlebar=32):
    """연속된 셀 범위를 한 번에 캡처. (legacy — capture_row_at 권장)"""
    x = win.left + sidebar + col_start * cell_w + 8
    y = win.top + titlebar + row * cell_h
    w = (col_end - col_start) * cell_w
    h = cell_h
    return pyautogui.screenshot(region=(x, y, w, h))


# ═══════════════════════════════════════════════════════════
#  픽셀 분석
# ═══════════════════════════════════════════════════════════

def _pixel_brightness_sum(r, g, b):
    return r + g + b


def has_glyph(img, bg_threshold=30):
    """비배경 픽셀이 전체의 5% 이상이면 True (글자 존재)."""
    pixels = list(img.getdata())
    total = len(pixels)
    if total == 0:
        return False
    non_bg = sum(1 for p in pixels if _pixel_brightness_sum(p[0], p[1], p[2]) > bg_threshold)
    return (non_bg / total) >= 0.05


def has_overlay_color(img, r=0x44, g=0x33, b=0x44, tolerance=0x10):
    """조합 오버레이 배경색 (0xFF443344 계열) 존재 여부."""
    for p in img.getdata():
        if (abs(p[0] - r) < tolerance and
            abs(p[1] - g) < tolerance and
            abs(p[2] - b) < tolerance):
            return True
    return False


def is_empty_cell(img, bg_threshold=30):
    """배경만 있는 빈 셀인지 (비배경 픽셀 < 1%)."""
    pixels = list(img.getdata())
    total = len(pixels)
    if total == 0:
        return True
    non_bg = sum(1 for p in pixels if _pixel_brightness_sum(p[0], p[1], p[2]) > bg_threshold)
    return (non_bg / total) < 0.01


def pixel_diff(img1, img2):
    """두 이미지의 픽셀 차이 비율 (0.0 ~ 1.0).

    크기가 다르면 1.0 반환.
    """
    if img1.size != img2.size:
        return 1.0
    pixels1 = list(img1.getdata())
    pixels2 = list(img2.getdata())
    total = len(pixels1)
    if total == 0:
        return 0.0
    diff_count = 0
    for p1, p2 in zip(pixels1, pixels2):
        if abs(p1[0] - p2[0]) > 5 or abs(p1[1] - p2[1]) > 5 or abs(p1[2] - p2[2]) > 5:
            diff_count += 1
    return diff_count / total


# ═══════════════════════════════════════════════════════════
#  GridInfo — grid_info.json 기반 셀 단위 좌표
# ═══════════════════════════════════════════════════════════

class GridInfo:
    """Terminal grid layout loaded from grid_info.json.

    grid_x/grid_y: SwapChainPanel offset within WinUI3 client area.
    cell_w/cell_h: glyph atlas cell size in pixels.
    BORDER_X/TITLEBAR_Y: window frame constants (pygetwindow → client area).
    """

    BORDER_X = 8
    TITLEBAR_Y = 32

    def __init__(self, grid_x=220, grid_y=0, cell_w=9, cell_h=19):
        self.grid_x = grid_x
        self.grid_y = grid_y
        self.cell_w = cell_w
        self.cell_h = cell_h

    @classmethod
    def load(cls, path=None):
        """Load from grid_info.json. Returns defaults if file missing."""
        path = path or os.path.join(RESULTS_DIR, "grid_info.json")
        try:
            with open(path, "r") as f:
                info = json.load(f)
            inst = cls(info["grid_x"], info["grid_y"],
                       info["cell_w"], info["cell_h"])
            print(f"[grid] loaded: origin=({inst.grid_x},{inst.grid_y}), "
                  f"cell={inst.cell_w}x{inst.cell_h}")
            return inst
        except (FileNotFoundError, json.JSONDecodeError, KeyError):
            print("[grid] grid_info.json not found, using defaults")
            return cls()

    def cell_screen_rect(self, win, row, col, wide=False):
        """Absolute screen rect (x, y, w, h) for a terminal cell."""
        x = win.left + self.BORDER_X + self.grid_x + col * self.cell_w
        y = win.top + self.TITLEBAR_Y + self.grid_y + row * self.cell_h
        w = self.cell_w * (2 if wide else 1)
        h = self.cell_h
        return (x, y, w, h)

    def rows_count(self, win):
        """Estimate total terminal rows."""
        usable = win.height - self.TITLEBAR_Y - self.grid_y
        return max(1, usable // self.cell_h)

    def cols_count(self, win):
        """Estimate total terminal columns."""
        usable = win.width - self.BORDER_X - self.grid_x
        return max(1, usable // self.cell_w)


_grid_info = None


def get_grid():
    """Lazy-load GridInfo singleton."""
    global _grid_info
    if _grid_info is None:
        _grid_info = GridInfo.load()
    return _grid_info


def capture_cell_at(win, row, col, wide=False):
    """GridInfo 기반 셀 단위 캡처. Returns PIL.Image."""
    grid = get_grid()
    rect = grid.cell_screen_rect(win, row, col, wide)
    return pyautogui.screenshot(region=rect)


def capture_row_at(win, row, col_start=0, num_cols=None):
    """GridInfo 기반 행 단위 캡처. Returns PIL.Image."""
    grid = get_grid()
    if num_cols is None:
        num_cols = grid.cols_count(win) - col_start
    x, y, _, h = grid.cell_screen_rect(win, row, col_start)
    w = num_cols * grid.cell_w
    return pyautogui.screenshot(region=(x, y, w, h))


def capture_bottom_rows(win, num_rows=3):
    """터미널 하단 N행 캡처 (현재 입력 영역)."""
    grid = get_grid()
    total = grid.rows_count(win)
    start_row = max(0, total - num_rows)
    x, y, _, _ = grid.cell_screen_rect(win, start_row, 0)
    w = grid.cols_count(win) * grid.cell_w
    h = num_rows * grid.cell_h
    return pyautogui.screenshot(region=(x, y, w, h))


def has_glyph_at(win, row, col, wide=False, bg_threshold=30):
    """특정 셀에 글리프가 있는지 확인."""
    img = capture_cell_at(win, row, col, wide)
    return has_glyph(img, bg_threshold)


def input_changed(img_before, img_after, threshold=0.0003):
    """입력이 화면 변화를 유발했는지 검증.

    pixel_diff > threshold 이면 True.
    threshold=0.0003 (0.03%): 커서 블링크(~0.00008) 보다 높고
    단일 자모 최소 변화(~0.00033) 보다 낮은 기준.
    800x600 윈도우 기준 144 픽셀 (단일 자모 ~158px, 커서 ~38px).
    """
    diff = pixel_diff(img_before, img_after)
    return diff, diff > threshold


# ═══════════════════════════════════════════════════════════
#  앱 관리
# ═══════════════════════════════════════════════════════════

def launch_app(exe_path=None, wait=4.0):
    """ghostwin_winui.exe 실행 + 윈도우 찾기 + 포커스.

    Returns:
        (subprocess.Popen, pygetwindow.Win32Window)
    """
    exe = exe_path or DEFAULT_EXE
    if not os.path.exists(exe):
        print(f"FAIL: {exe} not found")
        sys.exit(1)

    proc = subprocess.Popen([exe])
    print(f"[launch] PID={proc.pid}, exe={exe}")
    time.sleep(wait)

    wins = gw.getWindowsWithTitle(WINDOW_TITLE)
    if not wins:
        print(f"FAIL: '{WINDOW_TITLE}' window not found")
        proc.kill()
        sys.exit(1)

    win = wins[0]
    win.activate()
    time.sleep(0.5)
    print(f"[launch] window found: {win.width}x{win.height} at ({win.left},{win.top})")
    return proc, win


def kill_app(proc=None):
    """프로세스 종료."""
    if proc is not None:
        proc.kill()
        print("[kill] app terminated via proc.kill()")
        return

    # proc 없으면 taskkill
    subprocess.run(
        ["taskkill", "/IM", "ghostwin_winui.exe", "/F"],
        capture_output=True,
    )
    print("[kill] app terminated via taskkill")


def click_terminal(win):
    """터미널 패널 영역 클릭 (포커스 획득).

    사이드바 오른쪽, 윈도우 중앙 근처를 클릭.
    """
    cx = win.left + win.width // 2 + 100
    cy = win.top + win.height // 2
    pyautogui.click(cx, cy)
    time.sleep(0.3)


def fresh_prompt(win):
    """Enter 키로 새 프롬프트 줄 시작."""
    press_key(VK_RETURN, delay=0.5)


# ═══════════════════════════════════════════════════════════
#  테스트 결과 수집
# ═══════════════════════════════════════════════════════════

class TestResult:
    """단일 테스트 케이스의 결과."""

    def __init__(self, name):
        self.name = name
        self.checks = []   # [(check_name, passed, detail)]

    def check(self, name, condition, detail=""):
        """조건 검증 추가."""
        self.checks.append((name, bool(condition), str(detail)))
        status = "OK" if condition else "FAIL"
        print(f"    [{status}] {name}: {detail}")

    @property
    def passed(self):
        return all(c[1] for c in self.checks)

    def summary(self):
        status = "PASS" if self.passed else "FAIL"
        lines = [f"[{status}] {self.name}"]
        for name, ok, detail in self.checks:
            mark = "v" if ok else "x"
            lines.append(f"  {mark} {name}: {detail}")
        return "\n".join(lines)


class TestRunner:
    """전체 테스트 실행 결과를 수집."""

    def __init__(self):
        self.results = []

    def add(self, result):
        self.results.append(result)

    @property
    def passed_count(self):
        return sum(1 for r in self.results if r.passed)

    @property
    def total_count(self):
        return len(self.results)

    def summary(self):
        header = f"\n=== {self.passed_count}/{self.total_count} PASS ==="
        body = "\n".join(r.summary() for r in self.results)
        return f"{header}\n{body}"
