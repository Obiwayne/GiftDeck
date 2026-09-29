# Builds MayhemDeck as a single exe and puts shortcuts on the Desktop and Start menu.
# Run:  powershell -ExecutionPolicy Bypass -File install.ps1
$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
$dist = Join-Path $src "dist"

Write-Host "Publishing MayhemDeck..."
dotnet publish "$src\GiftDeck.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o $dist --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

# The TikTok bridge (chat, gifts, viewers) runs on Node.js.
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    Write-Host "Installing the TikTok bridge's packages..."
    Push-Location (Join-Path $src "bridge")
    npm install --omit=dev --no-audit --no-fund --loglevel=error
    Pop-Location
} else {
    Write-Warning "Node.js was not found. Install it (winget install OpenJS.NodeJS.LTS) and run this again, or MayhemDeck can't read your LIVE."
}
$wingetFfmpeg = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages") -Directory -Filter "Gyan.FFmpeg*" -ErrorAction SilentlyContinue
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue) -and -not $wingetFfmpeg) {
    Write-Warning "ffmpeg was not found. Go LIVE with the vertical canvas needs it: winget install Gyan.FFmpeg"
}

$exe = Join-Path $dist "MayhemDeck.exe"
$shell = New-Object -ComObject WScript.Shell

foreach ($dir in @([Environment]::GetFolderPath("Desktop"), (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"))) {
    $lnk = $shell.CreateShortcut((Join-Path $dir "MayhemDeck.lnk"))
    $lnk.TargetPath = $exe
    $lnk.WorkingDirectory = $dist
    $lnk.IconLocation = "$exe,0"
    $lnk.Description = "MayhemDeck - LIVE event control"
    $lnk.Save()
}

Write-Host "Installed to $exe"
Write-Host "Shortcuts added to the Desktop and Start menu."
