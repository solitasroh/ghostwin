"""
GhostWin IME External Verification (pyautogui)
- 앱 코드와 완전 독립된 블랙박스 테스트
- SendInput 기반 키 입력 + 화면 캡처 + 픽셀 분석
- 한글 조합, Backspace, Space 확정 검증
"""
import subprocess, time, sys, os
import pyautogui
import pygetwindow as gw
from PIL import Image
import ctypes
from ctypes import wintypes

# Win32 API
user32 = ctypes.windll.user32
imm32 = ctypes.windll.imm32

VK_HANGUL = 0x15
VK_BACK = 0x08
VK_SPACE = 0x20
VK_RETURN = 0x0D
KEYEVENTF_KEYUP = 0x0002

def press_key(vk):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)
    time.sleep(0.03)

def type_keys(keys, delay=0.08):
    for k in keys:
        vk = ord(k.upper())
        press_key(vk)
        time.sleep(delay)

def capture_region(x, y, w, h, name):
    """특정 영역 캡처 후 저장"""
    img = pyautogui.screenshot(region=(x, y, w, h))
    path = os.path.join(os.path.dirname(__file__), '..', 'test_results', f'{name}.png')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)
    return img

def count_non_bg_pixels(img, bg_threshold=30):
    """배경(어두운)이 아닌 픽셀 수 — 글자 존재 여부 판정"""
    pixels = list(img.getdata())
    count = 0
    for r, g, b in [(p[0], p[1], p[2]) for p in pixels]:
        if r + g + b > bg_threshold:
            count += 1
    return count

def has_overlay_color(img, target_r=0x44, target_g=0x33, target_b=0x44, tolerance=0x10):
    """조합 오버레이 배경색 (0xFF443344) 존재 여부"""
    pixels = list(img.getdata())
    for p in pixels:
        r, g, b = p[0], p[1], p[2]
        if (abs(r - target_r) < tolerance and
            abs(g - target_g) < tolerance and
            abs(b - target_b) < tolerance):
            return True
    return False

def get_cell_region(win, col, row, cell_w=9, cell_h=19, sidebar=220):
    """터미널 셀 영역의 화면 좌표"""
    # 윈도우 클라이언트 영역 + 사이드바 오프셋 + 타이틀바
    x = win.left + sidebar + col * cell_w + 8  # 8px border
    y = win.top + 32 + row * cell_h  # 32px title bar
    return (x, y, cell_w * 2, cell_h)  # 2셀 너비 (CJK)

# ═══ Main ═══
print("=== GhostWin IME External Test (pyautogui) ===")

# 1. 앱 실행
project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
exe_path = os.path.join(project_dir, 'build', 'ghostwin_winui.exe')
if not os.path.exists(exe_path):
    print(f"FAIL: {exe_path} not found")
    sys.exit(1)

proc = subprocess.Popen([exe_path])
print(f"PID: {proc.pid}")
time.sleep(4)

# 2. 윈도우 찾기
wins = gw.getWindowsWithTitle('GhostWin Terminal')
if not wins:
    print("FAIL: GhostWin window not found")
    proc.kill()
    sys.exit(1)

win = wins[0]
win.activate()
time.sleep(0.5)

# 패널 영역 클릭 (포커스)
cx = win.left + win.width // 2 + 100
cy = win.top + win.height // 2
pyautogui.click(cx, cy)
time.sleep(1)

results = []
pass_count = 0
fail_count = 0

# ═══ T1: English "hello" ═══
print("\n[T1] English: hello")
type_keys("hello", delay=0.06)
press_key(VK_RETURN)
time.sleep(0.5)
img_t1 = capture_region(win.left, win.top, win.width, win.height, "t1_english")
print(f"  Screenshot: test_results/t1_english.png")
results.append(("T1", "english", "CAPTURED"))

# ═══ T2: Korean toggle + "한" + Space ═══
print("\n[T2] Korean: han + Space")
press_key(VK_HANGUL)
time.sleep(0.5)

# 조합 중 캡처
type_keys("gk", delay=0.1)
time.sleep(0.3)
img_t2_comp = capture_region(win.left, win.top, win.width, win.height, "t2_composing")

type_keys("s", delay=0.1)
time.sleep(0.3)

# Space 확정
press_key(VK_SPACE)
time.sleep(0.5)
img_t2_done = capture_region(win.left, win.top, win.width, win.height, "t2_confirmed")
print(f"  Screenshots: t2_composing.png, t2_confirmed.png")
results.append(("T2", "korean_confirm", "CAPTURED"))

# ═══ T3: Korean "한" + Backspace x3 ═══
print("\n[T3] Korean: han + BS x3")
type_keys("gks", delay=0.1)
time.sleep(0.3)
img_t3_before = capture_region(win.left, win.top, win.width, win.height, "t3_before_bs")

press_key(VK_BACK); time.sleep(0.15)
press_key(VK_BACK); time.sleep(0.15)
press_key(VK_BACK); time.sleep(0.5)
img_t3_after = capture_region(win.left, win.top, win.width, win.height, "t3_after_bs")
print(f"  Screenshots: t3_before_bs.png, t3_after_bs.png")
results.append(("T3", "korean_bs_cancel", "CAPTURED"))

# ═══ T4: Fast Backspace (race condition) ═══
print("\n[T4] Fast BS race: 30ms interval")
type_keys("gks", delay=0.03)
time.sleep(0.1)
press_key(VK_BACK); time.sleep(0.03)
press_key(VK_BACK); time.sleep(0.03)
press_key(VK_BACK); time.sleep(0.3)
img_t4 = capture_region(win.left, win.top, win.width, win.height, "t4_fast_bs")
print(f"  Screenshot: t4_fast_bs.png")
results.append(("T4", "fast_bs_race", "CAPTURED"))

# ═══ T5: Korean "한글" multi-syllable ═══
print("\n[T5] Korean: hangul (2 syllables)")
type_keys("gksrmf", delay=0.1)
time.sleep(0.3)
img_t5_comp = capture_region(win.left, win.top, win.width, win.height, "t5_hangul_comp")
press_key(VK_SPACE)
time.sleep(0.5)
img_t5_done = capture_region(win.left, win.top, win.width, win.height, "t5_hangul_done")
print(f"  Screenshots: t5_hangul_comp.png, t5_hangul_done.png")
results.append(("T5", "multi_syllable", "CAPTURED"))

# ═══ Restore English ═══
press_key(VK_HANGUL)
time.sleep(0.3)

# ═══ Summary ═══
print("\n=== External Test Complete ===")
print(f"Screenshots saved to: test_results/")
print(f"Tests captured: {len(results)}")
for tid, name, status in results:
    print(f"  [{tid}] {name}: {status}")

print("\n[!] Visual verification required:")
print("  - t2_composing.png: 조합 오버레이(자주색 배경) 보이는가?")
print("  - t2_confirmed.png: '한 ' 터미널에 표시되는가?")
print("  - t3_after_bs.png: BS 3회 후 ㅎ 잔상 없는가?")
print("  - t4_fast_bs.png: 빠른 BS 후 잔상 없는가?")
print("  - t5_hangul_done.png: '한글 ' 정상 표시되는가?")

proc.kill()
print("\nApp terminated.")
