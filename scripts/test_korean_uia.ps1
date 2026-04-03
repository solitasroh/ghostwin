# Korean IME Test via UI Automation
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

public class GWU {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError=true)]
    public static extern uint SendInput(uint n, INPUT[] inp, int size);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }

    public static IntPtr FindGW() {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len+1);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf("Ghost", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static void Key(ushort vk) {
        var inp = new INPUT[2];
        inp[0].type = 1; inp[0].u.ki.wVk = vk; inp[0].u.ki.dwFlags = 0;
        inp[1].type = 1; inp[1].u.ki.wVk = vk; inp[1].u.ki.dwFlags = 2;
        SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(200);
    }

    public static void TypeKey(ushort vk, bool shift) {
        int count = shift ? 4 : 2;
        var inp = new INPUT[count];
        int i = 0;
        if (shift) {
            inp[i].type = 1; inp[i].u.ki.wVk = 0x10; inp[i].u.ki.dwFlags = 0; i++;
        }
        inp[i].type = 1; inp[i].u.ki.wVk = vk; inp[i].u.ki.dwFlags = 0; i++;
        inp[i].type = 1; inp[i].u.ki.wVk = vk; inp[i].u.ki.dwFlags = 2; i++;
        if (shift) {
            inp[i].type = 1; inp[i].u.ki.wVk = 0x10; inp[i].u.ki.dwFlags = 2; i++;
        }
        SendInput((uint)count, inp, Marshal.SizeOf(typeof(INPUT)));
        Thread.Sleep(200);
    }
}
"@

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient

$hwnd = [GWU]::FindGW()
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "ERROR: not found"; exit 1 }
Write-Host "GhostWin: $hwnd"

[GWU]::ShowWindow($hwnd, 9)
Start-Sleep -Milliseconds 300
[GWU]::SetForegroundWindow($hwnd)
Start-Sleep -Milliseconds 500

# Try to find the TextBox via UIAutomation
$auto = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
Write-Host "Root element: $($auto.Current.Name)"

# Find all children
$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
$child = $walker.GetFirstChild($auto)
$depth = 0
while ($child -ne $null -and $depth -lt 20) {
    $name = $child.Current.Name
    $type = $child.Current.ControlType.ProgrammaticName
    $cls = $child.Current.ClassName
    Write-Host "  [$depth] $type cls=$cls name=$name"

    # Look deeper
    $grandchild = $walker.GetFirstChild($child)
    $d2 = 0
    while ($grandchild -ne $null -and $d2 -lt 10) {
        $gname = $grandchild.Current.Name
        $gtype = $grandchild.Current.ControlType.ProgrammaticName
        $gcls = $grandchild.Current.ClassName
        Write-Host "    [$d2] $gtype cls=$gcls name=$gname"

        # One more level
        $ggchild = $walker.GetFirstChild($grandchild)
        $d3 = 0
        while ($ggchild -ne $null -and $d3 -lt 10) {
            $ggname = $ggchild.Current.Name
            $ggtype = $ggchild.Current.ControlType.ProgrammaticName
            $ggcls = $ggchild.Current.ClassName
            Write-Host "      [$d3] $ggtype cls=$ggcls name=$ggname"
            $ggchild = $walker.GetNextSibling($ggchild)
            $d3++
        }

        $grandchild = $walker.GetNextSibling($grandchild)
        $d2++
    }

    $child = $walker.GetNextSibling($child)
    $depth++
}

# Try to find Edit/TextBox control
$editCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Edit
)
$editBox = $auto.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
if ($editBox) {
    Write-Host ""
    Write-Host "Found Edit control: $($editBox.Current.Name) cls=$($editBox.Current.ClassName)"

    # Try to set focus
    try {
        $editBox.SetFocus()
        Write-Host "Focus set to Edit control"
        Start-Sleep -Milliseconds 300
    } catch {
        Write-Host "SetFocus failed: $_"
    }

    # Try ValuePattern
    try {
        $vp = $editBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $vp.SetValue("echo hello")
        Write-Host "ValuePattern set: echo hello"
    } catch {
        Write-Host "ValuePattern failed: $_"
    }
} else {
    Write-Host "No Edit control found"
}

Write-Host "Done"
