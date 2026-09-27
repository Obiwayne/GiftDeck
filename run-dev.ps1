# Runs the development build with its own data folder (%APPDATA%\GiftDeck-v2), seeded once from your
# real GiftDeck data, so testing never changes what the installed GiftDeck uses.
# Only one GiftDeck can run at a time: close the installed one first.
# Run:  powershell -ExecutionPolicy Bypass -File run-dev.ps1
$ErrorActionPreference = "Stop"
$dev = Join-Path $env:APPDATA "GiftDeck-v2"
$real = Join-Path $env:APPDATA "GiftDeck"
if (-not (Test-Path $dev) -and (Test-Path $real)) {
    Copy-Item $real $dev -Recurse
    Remove-Item (Join-Path $dev "log.txt") -ErrorAction SilentlyContinue
    Write-Host "Seeded $dev from your GiftDeck data."
}
if (Get-Process GiftDeck -ErrorAction SilentlyContinue) { throw "GiftDeck is already running. Close it first (only one can run at a time)." }
dotnet build (Join-Path $PSScriptRoot "GiftDeck.csproj") -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$env:GIFTDECK_DATA = $dev
Start-Process (Join-Path $PSScriptRoot "bin\Debug\net8.0-windows\GiftDeck.exe")
