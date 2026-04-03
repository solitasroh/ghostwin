"""Send identical text to all 4 terminals via clipboard paste."""
import ctypes
import ctypes.wintypes
import time
import subprocess

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32

def set_clipboard(text):
    """Set clipboard text via PowerShell."""
    subprocess.run(['powershell', '-Command',
                    f'Set-Clipboard -Value "{text}"'],
                   capture_output=True)

def send_ctrl_v(hwnd):
    """Send Ctrl+V keystroke to a window."""
    user32.SetForegroundWindow(hwnd)
    time.sleep(0.3)

    VK_CONTROL = 0x11
    VK_V = 0x56
    KEYEVENTF_KEYUP = 0x0002

    INPUT_KEYBOARD = 1
    class KEYBDINPUT(ctypes.Structure):
        _fields_ = [('wVk', ctypes.wintypes.WORD), ('wScan', ctypes.wintypes.WORD),
                     ('dwFlags', ctypes.wintypes.DWORD), ('time', ctypes.wintypes.DWORD),
                     ('dwExtraInfo', ctypes.POINTER(ctypes.c_ulong))]
    class INPUT(ctypes.Structure):
        class _INPUT(ctypes.Union):
            _fields_ = [('ki', KEYBDINPUT)]
        _fields_ = [('type', ctypes.wintypes.DWORD), ('_input', _INPUT)]

    def key_down(vk):
        inp = INPUT(type=INPUT_KEYBOARD)
        inp._input.ki = KEYBDINPUT(wVk=vk)
        user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))

    def key_up(vk):
        inp = INPUT(type=INPUT_KEYBOARD)
        inp._input.ki = KEYBDINPUT(wVk=vk, dwFlags=KEYEVENTF_KEYUP)
        user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))

    key_down(VK_CONTROL)
    time.sleep(0.05)
    key_down(VK_V)
    time.sleep(0.05)
    key_up(VK_V)
    time.sleep(0.05)
    key_up(VK_CONTROL)
    time.sleep(0.3)

    # Send Enter
    VK_RETURN = 0x0D
    key_down(VK_RETURN)
    time.sleep(0.05)
    key_up(VK_RETURN)
    time.sleep(0.5)

def get_hwnd(process_name, title_filter=None):
    """Get main window handle of a process."""
    cmd = f"(Get-Process {process_name}"
    if title_filter:
        cmd += f" | Where-Object {{ $_.MainWindowTitle -like '*{title_filter}*' }}"
    cmd += ")[0].MainWindowHandle"
    result = subprocess.run(['powershell', '-Command', cmd],
                          capture_output=True, text=True)
    try:
        return int(result.stdout.strip())
    except:
        return None

# Get all terminal HWNDs
terminals = {
    'WezTerm': get_hwnd('wezterm-gui', 'pwsh'),
    'Alacritty': get_hwnd('alacritty'),
    'WT': get_hwnd('WindowsTerminal'),
    'GhostWin': get_hwnd('ghostwin_winui'),
}

for name, hwnd in terminals.items():
    print(f"{name}: HWND={hwnd}")
    if not hwnd:
        print(f"  WARNING: {name} not found!")

# Set clipboard to the test command
test_cmd = "echo 'abcdefg 한글 1234'"
set_clipboard(test_cmd)
print(f"\nClipboard set: {test_cmd}")

# Paste into each terminal
for name, hwnd in terminals.items():
    if hwnd:
        print(f"Pasting into {name}...")
        send_ctrl_v(hwnd)
        time.sleep(1.0)

print("\nAll terminals should now show the same text.")
