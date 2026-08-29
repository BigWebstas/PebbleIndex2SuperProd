#requires -Version 5.1
<#
.SYNOPSIS
    Publishes Index2SP and compiles the Inno Setup installer(s).

    Two variants:
      self-contained      ~80 MB, bundles the .NET runtime, no prerequisites
      framework-dependent  ~3 MB, needs the .NET 8 Desktop Runtime + ASP.NET Core 8 Runtime

.EXAMPLE
    .\build.ps1                              # both variants + installers
    .\build.ps1 -Version 1.2.0
    .\build.ps1 -Mode self-contained
    .\build.ps1 -SkipInstaller               # just the publish output + zips
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64',
    [string]$Version       = '1.0.0',
    [ValidateSet('self-contained', 'framework-dependent', 'both')]
    [string]$Mode          = 'both',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$project = Join-Path $root 'src\Index2SP\Index2SP.csproj'
$distDir = Join-Path $root 'dist'
$pubRoot = Join-Path $root 'artifacts\publish'

$targets = switch ($Mode) {
    'self-contained'      { @('self-contained') }
    'framework-dependent' { @('framework-dependent') }
    default               { @('self-contained', 'framework-dependent') }
}

# Locate ISCC (Inno Setup 6 command-line compiler)
$iscc = $null
if (-not $SkipInstaller) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        'ISCC.exe'
    ) | Where-Object { $_ -and (Get-Command $_ -ErrorAction SilentlyContinue) } | Select-Object -First 1

    if (-not $iscc) {
        throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isdl.php or pass -SkipInstaller."
    }
}

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

foreach ($variant in $targets) {
    $selfContained = ($variant -eq 'self-contained')
    $suffix        = if ($selfContained) { '' } else { '-fd' }
    $publishDir    = Join-Path $pubRoot $variant

    $scArg = if ($selfContained) { 'true' } else { 'false' }

    Write-Host ""
    Write-Host "==> publish: $variant  (v$Version / $Runtime / self-contained=$scArg)" -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

    dotnet publish $project `
        -c $Configuration `
        -r $Runtime `
        --self-contained $scArg `
        -o $publishDir `
        "/p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $variant (exit $LASTEXITCODE)" }

    $exe = Join-Path $publishDir 'Index2SP.exe'
    if (-not (Test-Path $exe)) { throw "publish did not produce $exe" }

    $zip = Join-Path $distDir "Index2SP-portable$suffix-$Version.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip
    Write-Host "    portable -> $zip" -ForegroundColor Green

    if (-not $SkipInstaller) {
        Write-Host "==> installer: $variant" -ForegroundColor Cyan
        $isccArgs = @("/DAppVersion=$Version", "/DPublishDir=$publishDir")
        if (-not $selfContained) { $isccArgs += '/DFrameworkDependent' }
        & $iscc @isccArgs (Join-Path $root 'installer\Index2SP.iss')
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $variant (exit $LASTEXITCODE)" }
    }
}

Write-Host ""
Write-Host "Outputs in $distDir :" -ForegroundColor Green
Get-ChildItem $distDir | Sort-Object Name | ForEach-Object { "  {0,-40} {1,10:N0} bytes" -f $_.Name, $_.Length }
