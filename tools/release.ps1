#Requires -Version 7.0
<#
.SYNOPSIS
  Prepares a DrunkDeer Control release: version bump, publish, rename, SHA-256.

.DESCRIPTION
  Replaces the manual steps in Claude.md "Cut a Release":
    1. Bumps <Version>/<FileVersion>/<AssemblyVersion> in WpfApp/WpfApp.csproj.
    2. Runs the test suite (skip with -SkipTests).
    3. Publishes the self-contained single-file exe.
    4. Copies it to dist/vX.Y.Z/DrunkDeer-Control.exe (the asset name the
       in-app updater expects; do not change it).
    5. Writes DrunkDeer-Control.exe.sha256 next to it.

  It never commits, tags, pushes or creates a GitHub release. It prints the
  commands for those steps so a person runs them deliberately.

.EXAMPLE
  pwsh tools/release.ps1 -Version 2.6.0
  pwsh tools/release.ps1 -Version 2.6.0-beta.1 -SkipBump
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$')]
    [string]$Version,

    # Leave the csproj alone (e.g. it was already bumped and committed).
    [switch]$SkipBump,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$csproj = Join-Path $repo 'WpfApp/WpfApp.csproj'
$numeric = ($Version -split '-')[0]           # 2.6.0-beta.1 -> 2.6.0
$fourPart = "$numeric.0"
$tag = "v$Version"
$dist = Join-Path $repo "dist/$tag"
$assetName = 'DrunkDeer-Control.exe'

Write-Host "== DrunkDeer Control release prep: $tag" -ForegroundColor Cyan

# --- 1. Version bump ---------------------------------------------------------
if (-not $SkipBump) {
    $text = [System.IO.File]::ReadAllText($csproj)
    $new = $text
    $new = [regex]::Replace($new, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
    $new = [regex]::Replace($new, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$fourPart</FileVersion>")
    $new = [regex]::Replace($new, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$fourPart</AssemblyVersion>")
    if ($new -eq $text) {
        Write-Host "   csproj already at $Version" -ForegroundColor DarkGray
    } else {
        [System.IO.File]::WriteAllText($csproj, $new)
        Write-Host "   bumped csproj to $Version ($fourPart)"
    }
} else {
    $current = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ($current -ne $Version) {
        throw "-SkipBump given but csproj <Version> is '$current', not '$Version'."
    }
}

# --- 2. Tests ----------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "== dotnet test" -ForegroundColor Cyan
    dotnet test (Join-Path $repo 'DrunkDeerDriver.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }
}

# --- 3. Publish --------------------------------------------------------------
$publishDir = Join-Path $repo "WpfApp/bin/Release/publish-$tag"
Write-Host "== dotnet publish -> $publishDir" -ForegroundColor Cyan
dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$built = Get-ChildItem $publishDir -Filter '*.exe' | Where-Object { $_.Name -like 'DrunkDeer*Control.exe' } | Select-Object -First 1
if (-not $built) { throw "Published exe not found in $publishDir" }

# --- 4. Rename to the asset name the updater downloads -----------------------
New-Item -ItemType Directory -Force $dist | Out-Null
$asset = Join-Path $dist $assetName
Copy-Item $built.FullName $asset -Force

$ver = (Get-Item $asset).VersionInfo.FileVersion
if ($ver -ne $fourPart) { throw "Published exe reports FileVersion '$ver', expected '$fourPart'." }

# --- 5. SHA-256 sidecar ------------------------------------------------------
$hash = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant()
$shaFile = "$asset.sha256"
# sha256sum format: "<hex>  <name>", LF line ending.
[System.IO.File]::WriteAllText($shaFile, "$hash  $assetName`n")

$sizeMb = [math]::Round((Get-Item $asset).Length / 1MB, 1)
Write-Host ""
Write-Host "== Ready: $tag" -ForegroundColor Green
Write-Host "   $asset ($sizeMb MB)"
Write-Host "   $shaFile"
Write-Host "   sha256 $hash"
Write-Host ""
Write-Host "Next steps (run them yourself, after the hardware checklist passes):" -ForegroundColor Yellow
Write-Host "   git commit -am `"chore(release): bump version to $Version`""
Write-Host "   git push origin <branch>"
Write-Host "   git tag $tag && git push origin $tag"
$pre = if ($Version -match '-') { ' --prerelease' } else { '' }
Write-Host "   gh release create $tag `"$asset`" `"$shaFile`" --title `"$tag`" --notes-file <notes.md>$pre"
