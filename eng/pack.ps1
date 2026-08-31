<#
.SYNOPSIS
    Assembles the device package, validates it offline, and packs the distributable archive.

.DESCRIPTION
    This produces the exact bytes a release ships and WSGM pins. The order matters: the package is
    assembled first, then validated by a pinned Device Lab, then packed from the validated tree.
    Validating after packing would prove nothing about what was packed.

    Validation is offline and never loads plugin code — it checks the manifest, the package layout
    and that the entry assembly is a managed x64 image. Nothing here touches hardware.

    The plugin publishes framework-dependent: WSGM loads it into its own process, which already
    carries the runtime. A self-contained package would ship a second copy of .NET that can never
    be used.
#>
[CmdletBinding()]
param(
    [string]$OutputRoot = "publish",

    [string]$Configuration = "Release",

    [string]$RuntimeIdentifier = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "src\WSGM.Device.Msi.Claw8A2Vm"
$project = Join-Path $source "WSGM.Device.Msi.Claw8A2Vm.csproj"
$outputFull = [IO.Path]::GetFullPath((Join-Path $root $OutputRoot))

if (-not (Test-Path -LiteralPath (Join-Path $root "external\WSGM.Device.Sdk\src") -PathType Container)) {
    throw "external\WSGM.Device.Sdk is empty. Clone with --recursive, or run: git submodule update --init"
}

# The manifest is the authority on id, version and entry assembly. Reading them from it rather than
# restating them here keeps the archive name and the package contents from disagreeing.
$manifestPath = Join-Path $source "plugin.wsgm.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 32
$packageId = [string]$manifest.id
$packageVersion = [string]$manifest.version
$entryAssembly = [string]$manifest.entryAssembly
if ($packageId -notmatch '^[A-Za-z0-9._-]+$' -or $packageVersion -notmatch '^[0-9]+(?:\.[0-9]+){1,3}$') {
    throw "$manifestPath has an unsafe package id or version."
}

if (Test-Path -LiteralPath $outputFull) {
    Remove-Item -LiteralPath $outputFull -Recurse -Force
}
$packageDirectory = Join-Path $outputFull $packageId
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

& dotnet publish $project `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained false `
    --output $packageDirectory `
    /p:Version=$packageVersion `
    /p:PlatformTarget=x64 `
    /p:PublishSingleFile=false `
    /p:TreatWarningsAsErrors=true `
    -m:1
if ($LASTEXITCODE -ne 0) {
    throw "Publishing the plugin failed."
}

# Debug symbols are not package content and would only inflate the installed slot.
Get-ChildItem -LiteralPath $packageDirectory -Filter "*.pdb" -File -Recurse | Remove-Item -Force

if (-not (Test-Path -LiteralPath (Join-Path $packageDirectory $entryAssembly) -PathType Leaf)) {
    throw "The publish did not produce the manifest's entry assembly: $entryAssembly"
}

$deviceLab = & (Join-Path $PSScriptRoot "acquire-devicelab.ps1")
$validator = Join-Path $deviceLab "wsgm-device.exe"

$validation = @(& $validator validate $packageDirectory 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Offline package validation failed:`n$($validation -join [Environment]::NewLine)"
}
Write-Host "Validated $packageId $packageVersion"

$archive = Join-Path $outputFull "$packageId-$packageVersion.wsgmpkg"
& $validator pack $packageDirectory --out $archive
if ($LASTEXITCODE -ne 0) {
    throw "Packing the plugin package failed."
}

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
Write-Host "Packed $archive"
Write-Host "SHA-256 $hash"
