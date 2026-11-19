param([int]$Seconds = 90, [string]$Design = "BBR-02M", [int]$Count = 10, [string[]]$Setup = @("hitcam all on"), [string]$Out = "", [switch]$KeepRunning, [switch]$WaitHits)
# 開発用のベンチマーク: ゲームを起動し直す → メインメニューで計測を始める（perf on）→ カスタムバトル（観戦、鏡写しの編成）を読み込む
# → 読み込みの時間を測る → Seconds 秒の対戦 → 計測の表（sprocketmods_perf.txt）を Out に写す。編成は最後に元へ戻す
# 日本語のボタン名を含むので、このファイルは BOM 付き UTF-8 で保存する
$s = $PSScriptRoot
function Send($c, $w) { & "$s\send.ps1" -Commands $c -Wait $w }
$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\..\..\BepInEx")
$log = "$root\LogOutput.log"

if (Get-Process Sprocket -ErrorAction SilentlyContinue) {
    Send @("quit") 3 | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-Process Sprocket -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
}
python "$s\setup.py" mirror $Design $Count | Out-Null
$launch = Get-Date
Start-Process "steam://rungameid/1674170"
Start-Sleep -Seconds 25
for ($i = 0; $i -lt 40; $i++) { if (((Send @("scene") 2) -join "") -match "MainMenu") { break } }
$menu = Get-Date
"startup to main menu: {0:0.0} s" -f ($menu - $launch).TotalSeconds
Send @("perf on") 1 | Out-Null

for ($i = 0; $i -lt 5; $i++) {
    Send @("click カスタムバトル") 6 | Out-Null
    if (((Send @("ui") 2) -join "`n") -match "\[Confirm\]") { break }
}
$start = (Get-Content $log).Count
$click = Get-Date
Send @("spectate on", "click Confirm") 0.5 | Out-Null
for ($i = 0; $i -lt 240; $i++) {
    Start-Sleep -Milliseconds 500
    if ((Get-Content $log | Select-Object -Skip $start | Select-String "Kill feed: match running").Count -gt 0) { break }
}
"battle load (Confirm to match running): {0:0.0} s" -f ((Get-Date) - $click).TotalSeconds

if ($Setup.Count -gt 0) { Send $Setup 1 | Out-Null }
if ($WaitHits) {
    # 撃ち合いが始まる（最初の命中の貫通計算）まで早送りしてから、計測をやり直す
    Send @("timescale 3") 1 | Out-Null
    for ($i = 0; $i -lt 120; $i++) {
        Start-Sleep 2
        if ((Get-Content $log | Select-Object -Skip $start | Select-String "Hit cam: fragment size").Count -gt 0) { break }
    }
    Send @("timescale 1", "perf reset") 1 | Out-Null
    "engagement after {0:0} s of fast-forward" -f ($i * 2)
}
Start-Sleep -Seconds $Seconds
Send @("perf report") 3 | Out-Null
Send @("hitcam all off", "perf off") 1 | Out-Null
$report = Get-Content "$root\sprocketmods_perf.txt"
$report
if ($Out) { $report | Out-File -Encoding utf8 $Out }
if (-not $KeepRunning) {
    Send @("quit") 3 | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-Process Sprocket -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
}
python "$s\setup.py" restore | Out-Null
