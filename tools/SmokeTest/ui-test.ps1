# End-to-end UI test of the real RecBox.exe (Windows UI Automation).
# Backs up and restores %AppData%\RecBox\settings.json; recordings go to %TEMP%\RecBoxE2E.
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\..\src\RecBox\bin\Release\net8.0-windows\RecBox.exe")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W32 {
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@

$Exe = (Resolve-Path $Exe).Path
$settingsDir = Join-Path $env:APPDATA "RecBox"
$settingsPath = Join-Path $settingsDir "settings.json"
$backup = "$settingsPath.e2e-backup"
$out = Join-Path $env:TEMP "RecBoxE2E"
$failures = New-Object System.Collections.Generic.List[string]
$passes = New-Object System.Collections.Generic.List[string]

function Check($name, $ok, $detail) {
    if ($ok) { $passes.Add($name); Write-Host "  PASS $name" }
    else { $failures.Add("$name : $detail"); Write-Host "  FAIL $name : $detail" }
}

function Get-RecBoxProcs { @(Get-Process RecBox -ErrorAction SilentlyContinue) }
function Get-OurFfmpeg { @(Get-Process ffmpeg -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*\RecBox\tools\ffmpeg\*" }) }

function Find-Element($root, $automationId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-MainWindow($proc, $timeoutSec = 20) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) {
            return [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
        }
        Start-Sleep -Milliseconds 300
    }
    throw "RecBox window did not appear"
}

function Get-Text($win, $id) {
    $el = Find-Element $win $id
    if ($null -eq $el) { return "" }
    return $el.Current.Name
}

function Wait-Status($win, $pattern, $timeoutSec) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $t = Get-Text $win "StatusText"
        if ($t -match $pattern) { return $t }
        Start-Sleep -Milliseconds 300
    }
    return (Get-Text $win "StatusText")
}

function Click($win, $id) {
    $el = Find-Element $win $id
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Start-RecBox {
    $p = Start-Process -FilePath $Exe -PassThru
    $w = Get-MainWindow $p
    $s = Wait-Status $w "Ready" 30
    return @{ Proc = $p; Win = $w; Status = $s }
}

Get-RecBoxProcs | Stop-Process -Force
Start-Sleep 1
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -File | Remove-Item -Force
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }

try {
    $json = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    $json | Add-Member -NotePropertyName outputFolder -NotePropertyValue $out -Force
    $json | Add-Member -NotePropertyName recordMicrophone -NotePropertyValue $true -Force
    $json | Add-Member -NotePropertyName recordSystemAudio -NotePropertyValue $true -Force
    $json | ConvertTo-Json | Set-Content $settingsPath -Encoding UTF8

    Write-Host "--- launch ---"
    $app = Start-RecBox
    Check "app starts and is ready" ($app.Status -match "Ready") "status='$($app.Status)'"
    $hwnd = $app.Proc.MainWindowHandle

    Write-Host "--- record ---"
    Click $app.Win "RecordButton"
    $s = Wait-Status $app.Win "^Recording" 20
    Check "recording starts from the UI" ($s -match "^Recording") "status='$s' error='$(Get-Text $app.Win 'ErrorText')'"
    Start-Sleep 2

    Write-Host "--- second instance ---"
    $second = Start-Process -FilePath $Exe -PassThru
    $exited = $second.WaitForExit(8000)
    Check "second launch exits (single instance)" ($exited -and (Get-RecBoxProcs).Count -eq 1) "exited=$exited count=$((Get-RecBoxProcs).Count)"

    Write-Host "--- minimize to tray while recording ---"
    $wp = $app.Win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $wp.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    Start-Sleep 2
    Check "window hidden from taskbar/screen" (-not [W32]::IsWindowVisible($hwnd)) "still visible"
    Check "still recording while hidden" (@(Get-OurFfmpeg).Count -ge 1) "no ffmpeg running"
    Start-Sleep 4

    Write-Host "--- restore via relaunch ---"
    $third = Start-Process -FilePath $Exe -PassThru
    $null = $third.WaitForExit(8000)
    Start-Sleep 2
    Check "relaunch brings the window back" ([W32]::IsWindowVisible($hwnd)) "window not visible"
    $app.Win = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
    Check "state still Recording after restore" ((Get-Text $app.Win "StatusText") -match "^Recording") "status='$(Get-Text $app.Win 'StatusText')'"
    Start-Sleep 2

    Write-Host "--- stop ---"
    Click $app.Win "RecordButton"
    $s = Wait-Status $app.Win "Ready" 60
    $last = Get-Text $app.Win "LastFileText"
    $warn = Get-Text $app.Win "WarningText"
    $err = Get-Text $app.Win "ErrorText"
    Write-Host "  last='$last' warning='$warn' error='$err'"
    Check "stop returns to Ready" ($s -match "Ready") "status='$s'"
    $mp4 = Get-ChildItem $out -Filter *.mp4 | Select-Object -First 1
    Check "MP4 saved" ($null -ne $mp4) "no mp4 in $out"
    Check "no MKV left" (@(Get-ChildItem $out -Filter *.mkv).Count -eq 0) "mkv present"
    if ($mp4) {
        $ff = (Resolve-Path (Join-Path $PSScriptRoot "..\ffmpeg\ffmpeg.exe")).Path
        $info = cmd /c "`"$ff`" -hide_banner -i `"$($mp4.FullName)`" 2>&1" | Out-String
        $dur = [regex]::Match($info, "Duration: (\S+),").Groups[1].Value
        Write-Host "  $($mp4.Name) duration=$dur"
        Check "MP4 has video+audio" (($info -match "Video:") -and ($info -match "Audio:")) $info
        $secs = [TimeSpan]::Parse($dur).TotalSeconds
        Check "MP4 covers the whole session (>= 14 s)" ($secs -ge 14) "duration $dur"
        cmd /c "`"$ff`" -hide_banner -loglevel error -y -ss 1 -i `"$($mp4.FullName)`" -frames:v 1 `"$out\e2e_frame_window_open.png`""
        cmd /c "`"$ff`" -hide_banner -loglevel error -y -sseof -2 -i `"$($mp4.FullName)`" -frames:v 1 `"$out\e2e_frame_after_restore.png`""
    }

    Write-Host "--- crash while recording (RecBox killed) ---"
    Click $app.Win "RecordButton"
    $s = Wait-Status $app.Win "^Recording" 20
    Start-Sleep 5
    Stop-Process -Id $app.Proc.Id -Force
    Start-Sleep 2
    Check "FFmpeg dies with RecBox (job object)" (@(Get-OurFfmpeg).Count -eq 0) "orphan ffmpeg still running"
    $mkv = @(Get-ChildItem $out -Filter *.mkv)
    Check "crash left a playable MKV segment" ($mkv.Count -ge 1) "no mkv"

    Write-Host "--- restart recovers the recording ---"
    $before = @(Get-ChildItem $out -Filter *.mp4).Count
    $app = Start-RecBox
    Start-Sleep 6
    $after = @(Get-ChildItem $out -Filter *.mp4).Count
    $last = Get-Text $app.Win "LastFileText"
    Write-Host "  last='$last'"
    Check "recovered MP4 created on next start" ($after -eq $before + 1) "mp4 before=$before after=$after"
    Check "UI reports the recovery" ($last -match "Recovered") "last='$last'"
    Check "recovered MKV cleaned up" (@(Get-ChildItem $out -Filter *.mkv).Count -eq 0) "mkv still present"

    Stop-Process -Id $app.Proc.Id -Force
}
finally {
    Get-RecBoxProcs | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path $backup) { Move-Item $backup $settingsPath -Force }
    Write-Host ""
    Write-Host "=== UI test summary ==="
    foreach ($p in $passes) { Write-Host "PASS  $p" }
    foreach ($f in $failures) { Write-Host "FAIL  $f" }
    Write-Host ("{0} passed, {1} failed. Output: {2}" -f $passes.Count, $failures.Count, $out)
}
if ($failures.Count -gt 0) { exit 1 }
