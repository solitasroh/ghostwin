# M-13 IME 진단 로그 추출 — 마지막 SESSION 만 표시
# - native LOG_I (engine-api): C:\Users\Solit\Rootech\works\ghostwin\ghostwin-diag.log
# - C# ImeDiag (WPF):           %LocalAppData%\GhostWin\diagnostics\imediag.log

$root = 'C:\Users\Solit\Rootech\works\ghostwin'
$nativeLog = Join-Path $root 'ghostwin-diag.log'
$imeLog    = Join-Path $env:LOCALAPPDATA 'GhostWin\diagnostics\imediag.log'

Write-Host "=== File status ==="
foreach ($file in @($nativeLog, $imeLog)) {
    if (Test-Path $file) {
        $info = Get-Item $file
        Write-Host ("OK   {0,-70}  {1,8} bytes  {2}" -f $info.FullName, $info.Length, $info.LastWriteTime)
    } else {
        Write-Host ("MISS $file")
    }
}

Write-Host ""
Write-Host "=== C# ImeDiag — 마지막 SESSION만 ==="
Write-Host ""
if (Test-Path $imeLog) {
    $lines = Get-Content $imeLog
    # 마지막 'SESSION START' 마커 위치 찾기
    $lastStart = -1
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match 'SESSION START') { $lastStart = $i; break }
    }
    if ($lastStart -ge 0) {
        $lines | Select-Object -Skip $lastStart
    } else {
        Write-Host "(SESSION START 마커 없음 — 이전 빌드 데이터일 수 있음. 전체 표시)"
        $lines | Select-Object -Last 80
    }
} else {
    Write-Host "(no file — 사용자 재현 필요)"
}

Write-Host ""
Write-Host "=== Native engine-api IME composition (마지막 30 줄) ==="
Write-Host ""
if (Test-Path $nativeLog) {
    Select-String -Path $nativeLog -Pattern 'IME composition|HandleCompositionUpdate' |
        Select-Object -Last 30 |
        ForEach-Object { Write-Host $_.Line }
} else {
    Write-Host "(no file)"
}
