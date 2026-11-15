# Builds NaxCompletion.bhm. Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File <this folder>\build.ps1
# Packages, NuGet caches, dotnet's home folder and temp files go to .build-cache inside this folder.
# Not redirected: the .NET SDK itself (wherever you installed it) and NuGet reading %APPDATA%\NuGet\NuGet.Config.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$cache = Join-Path $PSScriptRoot '.build-cache'
$tmp   = Join-Path $cache 'tmp'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

$env:TEMP                         = $tmp
$env:TMP                          = $tmp
$env:NUGET_SCRATCH                = Join-Path $cache 'nuget-scratch'
$env:NUGET_PACKAGES               = Join-Path $cache 'nuget-packages'
$env:NUGET_HTTP_CACHE_PATH        = Join-Path $cache 'nuget-http'
$env:NUGET_PLUGINS_CACHE_PATH     = Join-Path $cache 'nuget-plugins'
$env:DOTNET_CLI_HOME              = Join-Path $cache 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT  = '1'
$env:DOTNET_NOLOGO                = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'dotnet SDK not found on PATH. Install the .NET SDK (8 or newer) and run this again.' -ForegroundColor Red
    exit 1
}

Write-Host "dotnet SDK: $(dotnet --version)  at $((Get-Command dotnet).Source)"

dotnet build NaxCompletion.csproj -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host 'BUILD FAILED. The compiler output above shows the cause.' -ForegroundColor Red
    exit $LASTEXITCODE
}

$bhm = Get-ChildItem -Path (Join-Path $PSScriptRoot 'bin') -Recurse -Filter 'NaxCompletion.bhm' |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $bhm) {
    Write-Host 'Build succeeded but no NaxCompletion.bhm was produced. Check the build output above.' -ForegroundColor Red
    exit 1
}

# Sanity check: only the module itself should be packed, not Blish HUD's own libraries.
$outDir = $bhm.DirectoryName
$extraDlls = Get-ChildItem -Path $outDir -Filter '*.dll' | Where-Object { $_.Name -ne 'NaxCompletion.dll' }
if ($extraDlls) {
    Write-Host "WARNING: extra DLLs ended up in the package: $($extraDlls.Name -join ', ')" -ForegroundColor Yellow
}

$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item $bhm.FullName (Join-Path $dist 'NaxCompletion.bhm') -Force

Write-Host ''
Write-Host "OK: $(Join-Path $dist 'NaxCompletion.bhm')  ($([math]::Round($bhm.Length / 1KB)) KB)" -ForegroundColor Green
Write-Host 'Copy it into your Blish HUD modules folder and enable "Nax Completion" in Blish HUD.'
