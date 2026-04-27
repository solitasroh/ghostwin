$root = 'C:\Users\Solit\Rootech\works\ghostwin'
$cutoff = (Get-Date).AddHours(-3)
Get-ChildItem -Recurse $root -Filter '*.log' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -gt $cutoff } |
    Select-Object FullName, Length, LastWriteTime |
    Format-Table -AutoSize
