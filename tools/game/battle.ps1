param([string[]]$Setup = @(), [int]$Seconds = 240, [double]$Speed = 1, [string[]]$Teardown = @(), [string]$Grep = "", [switch]$Quit, [switch]$Drive)
# 開発用: ゲームを起動（起動していなければ）→ カスタムバトルを観戦で始める → Setup のコマンド → Seconds 秒回す
# → Teardown のコマンド → BepInEx のログから Grep に合う行を出す。Quit なら最後にゲームを終了する
# 日本語のボタン名を含むので、このファイルは BOM 付き UTF-8 で保存する（Windows PowerShell 5.1 は BOM なしを Shift-JIS で読む）
$s = $PSScriptRoot
function Send($c, $w) { & "$s\send.ps1" -Commands $c -Wait $w }
$log = Join-Path ([IO.Path]::GetFullPath("$PSScriptRoot\..\..\..\BepInEx")) "LogOutput.log"
if (-not (Get-Process Sprocket -ErrorAction SilentlyContinue)) {
    Start-Process "steam://rungameid/1674170"
    Start-Sleep -Seconds 40
}
for ($i = 0; $i -lt 20; $i++) { if (((Send @("scene") 3) -join "") -match "MainMenu") { break } }
$start = (Get-Content $log).Count
for ($i = 0; $i -lt 5; $i++) {
    Send @("click カスタムバトル") 6 | Out-Null
    if (((Send @("ui") 2) -join "`n") -match "\[Confirm\]") { break }
}
$spectate = if ($Drive) { "spectate off" } else { "spectate on" }
Send @($spectate, "click Confirm") 5 | Out-Null
for ($i = 0; $i -lt 20; $i++) { if (((Send @("scene") 3) -join "") -match "DeathmatchGameMode") { break } }
if ($Setup.Count -gt 0) { Send $Setup 1 | Out-Null }
if ($Speed -ne 1) { Send @("timescale $Speed") 1 | Out-Null }
Start-Sleep -Seconds $Seconds
Send (@("timescale 1") + $Teardown) 3 | Out-Null
if ($Grep) { Get-Content $log | Select-Object -Skip $start | Select-String $Grep | ForEach-Object { $_.Line -replace '^\[\w+\s*:\s*Sprocket Mods\]\s*', '' } }
if ($Quit) {
    Send @("quit") 3 | Out-Null
    for ($i = 0; $i -lt 30 -and (Get-Process Sprocket -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
}
