#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Index2SP as a self-contained win-x64 build and compiles the Inno Setup installer.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version 1.2.0
    .\build.ps1 -SkipInstaller        # just the publish output
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$Version       = '1.0.0',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root       = $PSScriptRoot
$project    = Join-Path $root 'src\Index2SP\Index2SP.csproj'
$publishDir = Join-Path $root "src\Index2SP\bin\$Configuration\net8.0-windows\$Runtime\publish"
$distDir    = Join-Path $root 'dist'

Write-Host "==> dotnet publish ($Configuration / $Runtime / v$Version)" -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    "/p:Version=$Version" `
    "/p:PublishSingleFile=true"

if (-not (Test-Path (Join-Path $publishDir 'Index2SP.exe'))) {
    throw "Publish did not produce Index2SP.exe at $publishDir"
}
Write-Host "    published -> $publishDir" -ForegroundColor Green

if ($SkipInstaller) { return }

# Locate ISCC (Inno Setup 6 command-line compiler)
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    'ISCC.exe'
) | Where-Object { $_ -and (Get-Command $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isdl.php " +
          "or run with -SkipInstaller."
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "==> ISCC installer/Index2SP.iss" -ForegroundColor Cyan
& $iscc `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDir" `
    (Join-Path $root 'installer\Index2SP.iss')

Write-Host ""
Write-Host "Done. Installer:" -ForegroundColor Green
Get-ChildItem $distDir -Filter 'Index2SP-Setup-*.exe' | ForEach-Object { "  $($_.FullName)" }
