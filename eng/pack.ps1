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
    [ValidateNotNullOrEmpty()]
    [string]$OutputRoot = "publish",

    [ValidateNotNullOrEmpty()]
    [string]$Configuration = "Release",

    [ValidateNotNullOrEmpty()]
    [string]$RuntimeIdentifier = "win-x64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "src\WSGM.Device.Msi.Claw8A2Vm"
$project = Join-Path $source "WSGM.Device.Msi.Claw8A2Vm.csproj"
$outputFull = [IO.Path]::GetFullPath((Join-Path $root $OutputRoot))
$workRoot = Join-Path $outputFull (".wsgm-pack-{0}" -f [Guid]::NewGuid().ToString("N"))
$workMarker = Join-Path $workRoot ".wsgm-generated-output"
$workMarkerValue = "claw-package-work-v1"

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

$archiveName = "$packageId-$packageVersion.wsgmpkg"
$archive = Join-Path $outputFull $archiveName
$archiveMarker = "$archive.wsgm-generated-output"
$archiveMarkerValue = "claw-package-output-v1|$packageId|$packageVersion"
if (Test-Path -LiteralPath $archiveMarker) {
    if (-not (Test-Path -LiteralPath $archiveMarker -PathType Leaf) -or
        (Get-Content -LiteralPath $archiveMarker -Raw).Trim() -cne $archiveMarkerValue) {
        throw "Refusing to replace an unrecognized package output marker: $archiveMarker"
    }
}
if ((Test-Path -LiteralPath $archive) -and
    -not (Test-Path -LiteralPath $archiveMarker -PathType Leaf)) {
    throw "Refusing to overwrite an unmarked package archive: $archive"
}

New-Item -ItemType Directory -Path $outputFull -Force | Out-Null
try {
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
Set-Content -LiteralPath $workMarker -Value $workMarkerValue -NoNewline
$packageDirectory = Join-Path $workRoot $packageId
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

# Physical glyph artwork. MSBuild does not copy this: the importer discovers profiles purely by
# directory (glyphs/profiles/*.json, glyphs/assets/<sha256>.<ext>), so the layout is copied through
# verbatim rather than declared file by file. Package validation treats glyphs as optional, so a
# missing copy step here would silently ship a package with no physical glyphs and still pass every
# gate — which is exactly what happened once. The count is asserted for that reason.
$glyphSource = Join-Path $source "glyphs"
if (-not (Test-Path -LiteralPath $glyphSource -PathType Container)) {
    throw "The package's glyphs directory is missing: $glyphSource"
}
$glyphFiles = @(Get-ChildItem -LiteralPath $glyphSource -File -Recurse)
if ($glyphFiles.Count -eq 0) {
    throw "The package's glyphs directory is empty."
}
foreach ($glyphFile in $glyphFiles) {
    $relative = [IO.Path]::GetRelativePath($source, $glyphFile.FullName)
    $target = Join-Path $packageDirectory $relative
    $targetParent = Split-Path -Parent $target
    if (-not (Test-Path -LiteralPath $targetParent -PathType Container)) {
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
    }
    Copy-Item -LiteralPath $glyphFile.FullName -Destination $target -Force
}
$profileCount = @(Get-ChildItem -LiteralPath (Join-Path $packageDirectory "glyphs\profiles") `
    -Filter "*.json" -File -ErrorAction SilentlyContinue).Count
if ($profileCount -eq 0) {
    throw "The staged package contains no glyph profile."
}
Write-Host "Staged $($glyphFiles.Count) glyph file(s), $profileCount profile(s)"

$deviceLab = & (Join-Path $PSScriptRoot "acquire-devicelab.ps1")
$validator = Join-Path $deviceLab "wsgm-device.exe"

$validation = @(& $validator validate $packageDirectory 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "Offline package validation failed:`n$($validation -join [Environment]::NewLine)"
}
Write-Host "Validated $packageId $packageVersion"

$stagedArchive = Join-Path $workRoot $archiveName
& $validator pack $packageDirectory --out $stagedArchive
if ($LASTEXITCODE -ne 0) {
    throw "Packing the plugin package failed."
}

$hash = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Move-Item -LiteralPath $stagedArchive -Destination $archive
Set-Content -LiteralPath $archiveMarker -Value $archiveMarkerValue -NoNewline
Write-Host "Packed $archive"
Write-Host "SHA-256 $hash"
}
finally {
    if (Test-Path -LiteralPath $workRoot) {
        if (-not (Test-Path -LiteralPath $workMarker -PathType Leaf) -or
            (Get-Content -LiteralPath $workMarker -Raw).Trim() -cne $workMarkerValue) {
            Write-Warning "Refusing to delete an unmarked package work directory: $workRoot"
        }
        else {
            Remove-Item -LiteralPath $workRoot -Recurse -Force
        }
    }
}
