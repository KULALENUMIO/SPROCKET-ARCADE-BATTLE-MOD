param([string[]]$Commands, [double]$Wait = 2)
# 開発用のコマンド（DebugConsole、[Debug] Commands = true のとき）をゲームに送り、Wait 秒後までに出た応答を返す
$root = "F:\SteamLibrary\steamapps\common\Sprocket\BepInEx"
$out = "$root\sprocketmods_debug.txt"
$before = if (Test-Path $out) { (Get-Content $out).Count } else { 0 }
[IO.File]::WriteAllLines("$root\sprocketmods_commands.txt", $Commands, (New-Object Text.UTF8Encoding $false))
Start-Sleep -Milliseconds ([int]($Wait * 1000))
if (Test-Path $out) { Get-Content $out | Select-Object -Skip $before }
