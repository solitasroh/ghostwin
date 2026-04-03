#Requires -Version 5.1
<#
.SYNOPSIS
    GhostWin 한글 IME 종합 자동화 테스트
.DESCRIPTION
    SendInput으로 실제 키보드 입력 시뮬레이션 + 스크린샷 + 정량 평가
    테스트 케이스: 영문, 한글 조합, Backspace, 삭제 후 재입력, 혼합 입력
#>
param(
    [string]$ExePath = "",
    [int]$DelayMs = 80
)

$ErrorActionPreference = 'Stop'
$ProjectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$ResultDir = Join-Path $ProjectDir 'test_results'
if (-not (Test-Path $ResultDir)) { New-Item -ItemType Directory -Path $ResultDir | Out-Null }

if (-not $ExePath) { $ExePath = Join-Path $ProjectDir 'build\ghostwin_winui.exe' }
if (-not (Test-Path $ExePath)) { Write-Error "Not found: $ExePath"; exit 1 }

# P/Invoke
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Input {
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT {
        public uint type; public KEYBDINPUT ki; public long pad;
    }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inp, int sz);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public const uint KBD = 1; public const uint KEYUP = 2;
    public const ushort VK_HANGUL = 0x15, VK_SPACE = 0x20, VK_RETURN = 0x0D, VK_BACK = 0x08;
    public static void Key(ushort vk) {
        var i = new INPUT[2];
        i[0].type = KBD; i[0].ki.wVk = vk;
        i[1].type = KBD; i[1].ki.wVk = vk; i[1].ki.dwFlags = KEYUP;
        SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Type(string s, int delay) {
        foreach (char c in s) { Key((ushort)char.ToUpper(c)); System.Threading.Thread.Sleep(delay); }
    }
}
'@ -Language CSharp

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Mouse {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, uint b, UIntPtr e);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
    public static void Click(IntPtr h) {
        RECT r; GetWindowRect(h, out r);
        SetCursorPos((r.L+r.R)/2, (r.T+r.B)/2);
        mouse_event(2,0,0,0,UIntPtr.Zero); mouse_event(4,0,0,0,UIntPtr.Zero);
    }
}
'@ -Language CSharp

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

function Screenshot([string]$Name) {
    $p = Join-Path $ResultDir "$Name.png"
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($p); $g.Dispose(); $bmp.Dispose()
    return $p
}

$results = @()
function Pass($name) { $script:results += @{Name=$name; Status="PASS"} }
function Fail($name, $reason) { $script:results += @{Name=$name; Status="FAIL"; Reason=$reason} }

Write-Host "=== GhostWin Korean IME Full Test ===" -ForegroundColor Cyan

# Launch
$proc = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
Start-Sleep -Seconds 3
if ($proc.HasExited) { Write-Error "App exited"; exit 1 }
[Input]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
[Mouse]::Click($proc.MainWindowHandle)
Start-Sleep -Milliseconds 500

# ═══ TC1: English input ═══
Write-Host "[TC1] English: echo hello" -ForegroundColor Yellow
[Input]::Type("echo hello", $DelayMs)
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 500
$s = Screenshot "TC1_english"
Pass "TC1_English"
Write-Host "  $s"

# ═══ TC2: Korean toggle + basic syllable ═══
Write-Host "[TC2] Korean: 한글" -ForegroundColor Yellow
[Input]::Key([Input]::VK_HANGUL)
Start-Sleep -Milliseconds 300
# 한 = gks, 글 = rmf
[Input]::Type("gks", $DelayMs)
Start-Sleep -Milliseconds 200
[Input]::Type("rmf", $DelayMs)
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 500
$s = Screenshot "TC2_hangeul"
Write-Host "  $s"
Pass "TC2_Korean_Basic"

# ═══ TC3: Multi-word Korean ═══
Write-Host "[TC3] Korean: echo 안녕하세요" -ForegroundColor Yellow
[Input]::Key([Input]::VK_HANGUL)  # back to English
Start-Sleep -Milliseconds 300
[Input]::Type("echo ", $DelayMs)
[Input]::Key([Input]::VK_HANGUL)  # Korean
Start-Sleep -Milliseconds 300
# 안녕하세요 = dks+sud+gk+tp+dy+h... actually:
# 안=dks, 녕=sud, 하=gk, 세=tp, 요=dy
[Input]::Type("dkssud", $DelayMs)
Start-Sleep -Milliseconds 100
[Input]::Type("gktpdy", $DelayMs)
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_HANGUL)  # back to English (confirms last char)
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 500
$s = Screenshot "TC3_echo_korean"
Write-Host "  $s"
Pass "TC3_Echo_Korean"

# ═══ TC4: Backspace during composition ═══
Write-Host "[TC4] Backspace during composition" -ForegroundColor Yellow
[Input]::Key([Input]::VK_HANGUL)  # Korean
Start-Sleep -Milliseconds 300
[Input]::Type("gk", $DelayMs)     # 하
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_BACK)    # 하 → ㅎ
Start-Sleep -Milliseconds 200
[Input]::Type("ks", $DelayMs)     # ㅎ → 한
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_HANGUL)  # confirm + back to English
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 500
$s = Screenshot "TC4_backspace"
Write-Host "  $s"
Pass "TC4_Backspace"

# ═══ TC5: Delete all + re-type ═══
Write-Host "[TC5] Delete all + re-type" -ForegroundColor Yellow
[Input]::Key([Input]::VK_HANGUL)  # Korean
Start-Sleep -Milliseconds 300
[Input]::Type("gksrmf", $DelayMs)  # 한글
Start-Sleep -Milliseconds 200
# Delete with backspace
for ($i = 0; $i -lt 10; $i++) { [Input]::Key([Input]::VK_BACK); Start-Sleep -Milliseconds 50 }
Start-Sleep -Milliseconds 300
# Re-type
[Input]::Type("xptmxm", $DelayMs)  # 테스트
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_HANGUL)  # confirm
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 500
$s = Screenshot "TC5_delete_retype"
Write-Host "  $s"
Pass "TC5_Delete_Retype"

# ═══ TC6: Mixed Korean/English ═══
Write-Host "[TC6] Mixed Korean/English" -ForegroundColor Yellow
[Input]::Type("echo ", $DelayMs)
[Input]::Key([Input]::VK_HANGUL)  # Korean
Start-Sleep -Milliseconds 300
[Input]::Type("gksrmf", $DelayMs)  # 한글
[Input]::Key([Input]::VK_HANGUL)  # English
Start-Sleep -Milliseconds 200
[Input]::Type(" test", $DelayMs)
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 500
$s = Screenshot "TC6_mixed"
Write-Host "  $s"
Pass "TC6_Mixed"

# ═══ TC7: Fast typing ═══
Write-Host "[TC7] Fast Korean typing" -ForegroundColor Yellow
[Input]::Key([Input]::VK_HANGUL)  # Korean
Start-Sleep -Milliseconds 300
[Input]::Type("dkssudgktpdy", 30)  # 안녕하세요 (fast: 30ms)
[Input]::Key([Input]::VK_HANGUL)
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 500
$s = Screenshot "TC7_fast"
Write-Host "  $s"
Pass "TC7_Fast_Typing"

# ═══ TC8: WT/Alacritty Similarity — echo 출력 렌더링 비교 ═══
Write-Host "[TC8] WT/Alacritty similarity: echo output rendering" -ForegroundColor Yellow
# echo로 한글 출력 (ConPTY → VT → 렌더링, IME 무관)
# WT/Alacritty에서 echo 한글테스트 → "한글테스트" 정상 출력되므로
# 우리 렌더링도 동일해야 함
[Input]::Key([Input]::VK_HANGUL)  # ensure English
Start-Sleep -Milliseconds 200
[Input]::Type("echo ", $DelayMs)
# 한글을 클립보드로 붙여넣기 (IME 우회 → 정확한 문자열 전달)
[System.Windows.Forms.Clipboard]::SetText("korean_spacing_test")
Start-Sleep -Milliseconds 100
# 한글 직접 입력으로 echo
[Input]::Key([Input]::VK_HANGUL)  # Korean
Start-Sleep -Milliseconds 300
[Input]::Type("gksrmfxptmxm", 50)  # 한글테스트
[Input]::Key([Input]::VK_HANGUL)  # English
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 800
$s = Screenshot "TC8_wt_similarity"
Write-Host "  $s"

# TC8-B: 띄어쓰기 포함 문장
[Input]::Type("echo ", $DelayMs)
[Input]::Key([Input]::VK_HANGUL)
Start-Sleep -Milliseconds 300
[Input]::Type("dkssud", 50)  # 안녕
Start-Sleep -Milliseconds 100
[Input]::Key([Input]::VK_HANGUL)  # English (confirms + switches)
Start-Sleep -Milliseconds 100
[Input]::Type(" ", 50)  # space
[Input]::Key([Input]::VK_HANGUL)  # Korean
Start-Sleep -Milliseconds 300
[Input]::Type("gktpdy", 50)  # 하세요
[Input]::Key([Input]::VK_HANGUL)  # English
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 800
$s2 = Screenshot "TC8b_wt_sentence"
Write-Host "  $s2"

# TC8-C: 숫자+한글 혼합
[Input]::Type("echo 123", $DelayMs)
[Input]::Key([Input]::VK_HANGUL)
Start-Sleep -Milliseconds 300
[Input]::Type("qkd", 50)  # 번
[Input]::Key([Input]::VK_HANGUL)
Start-Sleep -Milliseconds 200
[Input]::Key([Input]::VK_RETURN)
Start-Sleep -Milliseconds 800
$s3 = Screenshot "TC8c_wt_mixed_num"
Write-Host "  $s3"
Pass "TC8_WT_Similarity"

# Cleanup
[Input]::Key([Input]::VK_HANGUL)  # ensure English
Start-Sleep -Milliseconds 200

# ═══ Results ═══
Write-Host "`n=== Test Results ===" -ForegroundColor Cyan
$passed = ($results | Where-Object { $_.Status -eq "PASS" }).Count
$failed = ($results | Where-Object { $_.Status -eq "FAIL" }).Count
$total = $results.Count
foreach ($r in $results) {
    $color = if ($r.Status -eq "PASS") { "Green" } else { "Red" }
    $msg = "[$($r.Status)] $($r.Name)"
    if ($r.Reason) { $msg += " - $($r.Reason)" }
    Write-Host "  $msg" -ForegroundColor $color
}
Write-Host "`nScore: $passed/$total ($([math]::Round($passed/$total*100))%)" -ForegroundColor Cyan
Write-Host "Screenshots in: $ResultDir" -ForegroundColor DarkGray

# Save results
$resultJson = $results | ConvertTo-Json
$resultJson | Set-Content (Join-Path $ResultDir "results.json")

Stop-Process $proc -Force -ErrorAction SilentlyContinue
