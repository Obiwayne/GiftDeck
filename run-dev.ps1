# Runs the development build with its own data folder (%APPDATA%\GiftDeck-v2), seeded once from your
# real MayhemDeck data, so testing never changes what the installed MayhemDeck uses.
# Only one MayhemDeck can run at a time, and v2 wants to start OBS itself (hidden, on the portrait setup),
# so this offers to close the installed MayhemDeck and OBS first.
# Run:  powershell -ExecutionPolicy Bypass -File run-dev.ps1   (or the "MayhemDeck (test)" shortcut)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
function Ask($text) { [System.Windows.Forms.MessageBox]::Show($text, "MayhemDeck (test)", "YesNo", "Question") -eq "Yes" }
function Tell($text) { [System.Windows.Forms.MessageBox]::Show($text, "MayhemDeck (test)", "OK", "Information") | Out-Null }

$dev = Join-Path $env:APPDATA "GiftDeck-v2"
$real = Join-Path $env:APPDATA "GiftDeck"
if (-not (Test-Path $dev) -and (Test-Path $real)) {
    Copy-Item $real $dev -Recurse
    Remove-Item (Join-Path $dev "log.txt") -ErrorAction SilentlyContinue
}

$gd = Get-Process MayhemDeck, GiftDeck -ErrorAction SilentlyContinue
if ($gd) {
    if (-not (Ask "Your normal MayhemDeck is open. Close it and open MayhemDeck (test)?`n`n(Don't do this while you're live.)")) { exit }
    $gd | ForEach-Object { $_.CloseMainWindow() | Out-Null }
    for ($i = 0; $i -lt 60 -and (Get-Process MayhemDeck, GiftDeck -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
    Get-Process MayhemDeck, GiftDeck -ErrorAction SilentlyContinue | Stop-Process -Force
}

$obs = Get-Process obs64 -ErrorAction SilentlyContinue
if ($obs -and $obs.MainWindowTitle -notmatch "GiftDeck Portrait") {
    if (Ask "OBS is open with your normal setup. Close it so MayhemDeck (test) can start OBS itself (hidden, on the portrait setup)?`n`nWhen you close MayhemDeck (test), it puts your normal OBS setup back.") {
        $obs | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        for ($i = 0; $i -lt 60 -and (Get-Process obs64 -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
        if (Get-Process obs64 -ErrorAction SilentlyContinue) { Tell "OBS didn't close (it may be asking something). Close it yourself, then open MayhemDeck (test) again."; exit }
    }
}

$exe = Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\MayhemDeck.exe"
dotnet build (Join-Path $PSScriptRoot "GiftDeck.csproj") -c Debug --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0 -and -not (Test-Path $exe)) { Tell "Building MayhemDeck (test) failed."; exit 1 }
$env:GIFTDECK_DATA = $dev
Start-Process $exe
