"""
Precise Backspace step-by-step capture.
Each keystroke followed by screenshot to trace exactly when residual appears.
"""
import subprocess, time, sys, os
import pyautogui
import pygetwindow as gw
import ctypes

user32 = ctypes.windll.user32
VK_HANGUL = 0x15
VK_BACK = 0x08
VK_RETURN = 0x0D
KEYEVENTF_KEYUP = 0x0002

def press(vk):
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.01)
    user32.keybd_event(vk, 0, KEYEVENTF_KEYUP, 0)

def cap(name):
    time.sleep(0.3)  # 렌더 프레임 안정화 대기
    img = pyautogui.screenshot()
    path = os.path.join(os.path.dirname(__file__), '..', 'test_results', f'bs_{name}.png')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    img.save(path)
    print(f"  [{name}] captured")
    return path

project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
exe = os.path.join(project_dir, 'build', 'ghostwin_winui.exe')
proc = subprocess.Popen([exe])
time.sleep(4)

wins = gw.getWindowsWithTitle('GhostWin Terminal')
if not wins:
    print("FAIL: window not found"); proc.kill(); sys.exit(1)
win = wins[0]
win.activate()
time.sleep(0.5)

# 패널 클릭
pyautogui.click(win.left + win.width//2 + 100, win.top + win.height//2)
time.sleep(1)

print("=== Step-by-step BS test ===")

# Enter로 프롬프트 새 줄
press(VK_RETURN)
time.sleep(1)
cap("00_fresh_prompt")

# 한/영 전환
press(VK_HANGUL)
time.sleep(0.5)
cap("01_hangul_on")

# ㅎ (G)
press(ord('G'))
cap("02_after_G_hieut")

# ㅏ (K) → 하
press(ord('K'))
cap("03_after_K_ha")

# ㄴ (S) → 한
press(ord('S'))
cap("04_after_S_han")

# BS 1 → 하
press(VK_BACK)
cap("05_after_BS1_ha")

# BS 2 → ㅎ
press(VK_BACK)
cap("06_after_BS2_hieut")

# BS 3 → 빈 (조합 종료)
press(VK_BACK)
cap("07_after_BS3_empty")

# 추가 대기 후 재캡처 (지연 렌더링 확인)
time.sleep(1)
cap("08_after_1sec_wait")

print("\n=== Done ===")
print("Compare bs_06_after_BS2_hieut.png vs bs_07_after_BS3_empty.png")
print("If 07 still shows a character → rendering bug confirmed")

proc.kill()
