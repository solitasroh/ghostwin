# ConPTY 한글 간격 테스트 - echo로 출력하여 렌더링 비교
param([string]$ExePath = "")
$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $ExePath) { $ExePath = Join-Path $ProjectDir 'build\ghostwin_winui.exe' }

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class K {
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT {
        public uint type; public KEYBDINPUT ki; public long pad;
    }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] i, int s);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public static void Key(ushort vk) {
        var i = new INPUT[2];
        i[0].type=1; i[0].ki.wVk=vk;
        i[1].type=1; i[1].ki.wVk=vk; i[1].ki.dwFlags=2;
        SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Type(string s, int d) { foreach(char c in s){Key((ushort)char.ToUpper(c));System.Threading.Thread.Sleep(d);} }
}
'@ -Language CSharp

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class M {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint b, UIntPtr e);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public static void Click(IntPtr h) { RECT r; GetWindowRect(h, out r); SetCursorPos((r.L+r.R)/2,(r.T+r.B)/2); mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero); }
}
'@ -Language CSharp

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function SS($n) {
    $p = Join-Path $ProjectDir "test_results\$n.png"
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width,$b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location,[System.Drawing.Point]::Empty,$b.Size)
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose(); return $p
}

$proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
Start-Sleep 3
[K]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
[M]::Click($proc.MainWindowHandle)
Start-Sleep -Milliseconds 500

# Test A: echo outputs Korean (ConPTY → VT → render, no IME involved)
Write-Host "[A] echo output test"
[K]::Type("echo ", 50)
# Type Korean string via clipboard to bypass IME
[System.Windows.Forms.Clipboard]::SetText("한글테스트 spacing")
Start-Sleep 100
# Ctrl+V to paste
[K]::Key(0x11)  # Ctrl down... actually SendInput doesn't hold keys well
# Use SendKeys instead for paste
[System.Windows.Forms.SendKeys]::SendWait("^v")
Start-Sleep 200
[K]::Key(0x0D)  # Enter
Start-Sleep 500
SS "spacing_A_echo" | Out-Null

# Test B: Korean IME slow typing (100ms per key)
Write-Host "[B] Slow Korean IME (100ms)"
[K]::Key(0x15)  # Hangul toggle
Start-Sleep 300
[K]::Type("gks", 100)  # 한
Start-Sleep 500  # long pause between syllables
[K]::Type("rmf", 100)  # 글
Start-Sleep 500
[K]::Key(0x15)  # back to English
Start-Sleep 200
[K]::Key(0x0D)
Start-Sleep 500
SS "spacing_B_slow" | Out-Null

# Test C: Korean IME fast typing (30ms per key)
Write-Host "[C] Fast Korean IME (30ms)"
[K]::Key(0x15)
Start-Sleep 300
[K]::Type("gksrmf", 30)  # 한글 fast
[K]::Key(0x15)
Start-Sleep 200
[K]::Key(0x0D)
Start-Sleep 500
SS "spacing_C_fast" | Out-Null

# Test D: Backspace test
Write-Host "[D] Backspace test"
[K]::Key(0x15)
Start-Sleep 300
[K]::Type("gksrmf", 60)  # 한글
Start-Sleep 200
[K]::Key(0x08); Start-Sleep 50  # BS
[K]::Key(0x08); Start-Sleep 50  # BS
[K]::Key(0x08); Start-Sleep 50  # BS
[K]::Key(0x08); Start-Sleep 50  # BS
Start-Sleep 200
[K]::Type("xptmxm", 60)  # 테스트
[K]::Key(0x15)
Start-Sleep 200
[K]::Key(0x0D)
Start-Sleep 500
SS "spacing_D_backspace" | Out-Null

Write-Host "`nDone. Check test_results/spacing_*.png"
Stop-Process $proc -Force -ErrorAction SilentlyContinue
