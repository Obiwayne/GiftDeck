# Builds GiftDeck-Setup-<version>.exe: GiftDeck with .NET included, plus the TikTok bridge with its own
# Node.js, so people who download it don't need to install anything else.
# Needs: .NET 8 SDK, Node.js/npm (only to fetch the bridge's packages), Inno Setup 6.
# Run:   powershell -ExecutionPolicy Bypass -File build-installer.ps1 -Version 1.0.0
param([string]$Version = "1.0.0", [string]$NodeVersion = "22.19.0")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "build"
$app = Join-Path $out "app"
$cache = Join-Path $root ".buildcache"

Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $app, $cache | Out-Null

Write-Host "1/4  Publishing GiftDeck (self-contained, .NET included)..."
dotnet publish (Join-Path $root "GiftDeck.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:Version=$Version -o $app --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Get-ChildItem $app -Filter *.xml | Remove-Item   # API docs that come with the WebView2 package; not needed to run

Write-Host "2/4  Adding the TikTok bridge and its packages..."
$bridge = Join-Path $app "bridge"
New-Item -ItemType Directory -Force $bridge | Out-Null
Copy-Item (Join-Path $root "bridge\bridge.js"), (Join-Path $root "bridge\package.json"), (Join-Path $root "bridge\package-lock.json") $bridge
Push-Location $bridge
npm ci --omit=dev --no-audit --no-fund --loglevel=error
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "npm ci failed" }
Pop-Location

Write-Host "3/4  Adding Node.js $NodeVersion for the bridge..."
$zip = Join-Path $cache "node-v$NodeVersion-win-x64.zip"
if (-not (Test-Path $zip)) {
    Invoke-WebRequest "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-win-x64.zip" -OutFile $zip -UseBasicParsing
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($name in @("node.exe", "LICENSE")) {
        $entry = $z.Entries | Where-Object { $_.FullName -eq "node-v$NodeVersion-win-x64/$name" }
        $target = if ($name -eq "LICENSE") { Join-Path $bridge "NODE-LICENSE.txt" } else { Join-Path $bridge $name }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
} finally { $z.Dispose() }

Write-Host "4/4  Building the installer..."
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }
& $iscc /Qp "/DMyAppVersion=$Version" (Join-Path $root "installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }

$setup = Join-Path $out "GiftDeck-Setup-$Version.exe"
"Built {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)
