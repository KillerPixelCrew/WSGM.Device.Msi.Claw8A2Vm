<#
.SYNOPSIS
    Downloads and verifies the Device Lab release pinned in the lock file.

.DESCRIPTION
    `third_party/devicelab/devicelab.lock.json` names the exact release the installer's optional
    Device Lab component ships. This script reads it rather than restating it: a second copy of a
    pinned digest is a copy that can silently disagree with the reviewed one.

    The archive is verified by SHA-256 before it is expanded. A mismatch is fatal — this runs on the
    release machine, where the right answer is to stop and look rather than to ship bytes nobody
    reviewed.

    Nothing here is checked in. The destination is generated, gitignored, and safe to delete.

.PARAMETER Destination
    Where to expand the verified tree. Defaults to the staging directory the build uses.

.PARAMETER LockPath
    The lock file to read. Defaults to the repository's own.

.PARAMETER Force
    Re-download even when the destination already holds the pinned version.
#>
[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Destination = (Join-Path $PSScriptRoot '..\third_party\devicelab\staging'),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$LockPath = (Join-Path $PSScriptRoot '..\third_party\devicelab\devicelab.lock.json'),

    [Parameter()]
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lockFull = [System.IO.Path]::GetFullPath($LockPath)
$lock = Get-Content -LiteralPath $lockFull -Raw | ConvertFrom-Json
$entry = $lock.component
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$destinationRootPath = [System.IO.Path]::GetPathRoot($destinationRoot)
if ([string]::Equals(
        $destinationRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar),
        $destinationRootPath.TrimEnd([System.IO.Path]::DirectorySeparatorChar),
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Device Lab cache destination may not be a filesystem root.'
}

$assetName = [string]$entry.asset
$executableName = [string]$entry.executable
$expectedHash = [string]$entry.assetSha256
$expectedBytes = [long]$entry.assetBytes
if ([int]$lock.schemaVersion -ne 1 -or
    [string]::IsNullOrWhiteSpace($assetName) -or
    [System.IO.Path]::GetFileName($assetName) -cne $assetName -or
    [string]::IsNullOrWhiteSpace($executableName) -or
    [System.IO.Path]::GetFileName($executableName) -cne $executableName -or
    $expectedHash -notmatch '^[0-9A-F]{64}$' -or
    $expectedBytes -le 0 -or
    -not ([Uri]$entry.assetUrl).IsAbsoluteUri -or
    ([Uri]$entry.assetUrl).Scheme -cne 'https') {
    throw "$lockFull contains an unsupported or unsafe Device Lab pin."
}

$stampPath = Join-Path $destinationRoot '.pinned-version'
$cacheMarkerValue = 'wsgm-devicelab-cache-v1'
$executablePath = Join-Path $destinationRoot $executableName

function Test-GeneratedCache {
    param([Parameter(Mandatory = $true)][string]$Path)

    $marker = Join-Path $Path '.wsgm-generated-cache'
    return (Test-Path -LiteralPath $marker -PathType Leaf) -and
        (Get-Content -LiteralPath $marker -Raw).Trim() -ceq $cacheMarkerValue
}

if ((Test-Path -LiteralPath $destinationRoot) -and
    -not (Test-GeneratedCache -Path $destinationRoot)) {
    throw "Refusing to replace an unmarked Device Lab cache directory: $destinationRoot"
}

# The stamp records which pin produced this tree. Without it, a stale staging directory from an
# earlier pin would be reused silently and the installer would ship the previous version.
if (-not $Force -and
    (Test-GeneratedCache -Path $destinationRoot) -and
    (Test-Path -LiteralPath $stampPath -PathType Leaf) -and
    (Test-Path -LiteralPath $executablePath -PathType Leaf) -and
    (Get-Content -LiteralPath $stampPath -Raw).Trim() -ceq $expectedHash) {
    Write-Information "Device Lab $($entry.version) is already staged." -InformationAction Continue
    return $destinationRoot
}

$destinationParent = Split-Path -Parent $destinationRoot
$destinationName = Split-Path -Leaf $destinationRoot
$stagingRoot = Join-Path $destinationParent (
    '.{0}.wsgm-stage-{1}' -f $destinationName, [Guid]::NewGuid().ToString('N'))
$stagingMarker = Join-Path $stagingRoot '.wsgm-generated-cache'
$stagingStamp = Join-Path $stagingRoot '.pinned-version'
$stagingExecutable = Join-Path $stagingRoot $executableName
$backupRoot = Join-Path $destinationParent (
    '.{0}.wsgm-backup-{1}' -f $destinationName, [Guid]::NewGuid().ToString('N'))
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) (
    'WSGM-DeviceLab-{0}.zip' -f [Guid]::NewGuid().ToString('N'))

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
Set-Content -LiteralPath $stagingMarker -Value $cacheMarkerValue -NoNewline

Write-Information "Acquiring Device Lab $($entry.version)" -InformationAction Continue
try {
    # The default progress renderer costs more than the download on an asset this size.
    $previousProgress = $ProgressPreference
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $entry.assetUrl -OutFile $archivePath -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }

    $actualBytes = (Get-Item -LiteralPath $archivePath).Length
    if ($actualBytes -ne $expectedBytes) {
        throw "Size mismatch for $assetName`: expected $expectedBytes, got $actualBytes."
    }

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -cne $expectedHash) {
        throw "Hash mismatch for $assetName`: expected $expectedHash, got $actualHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingRoot -Force
    Set-Content -LiteralPath $stagingMarker -Value $cacheMarkerValue -NoNewline

    if (-not (Test-Path -LiteralPath $stagingExecutable -PathType Leaf)) {
        throw "The verified Device Lab archive did not contain $executableName."
    }

    Set-Content -LiteralPath $stagingStamp -Value $expectedHash -NoNewline

    if (Test-Path -LiteralPath $destinationRoot) {
        Move-Item -LiteralPath $destinationRoot -Destination $backupRoot
    }

    try {
        Move-Item -LiteralPath $stagingRoot -Destination $destinationRoot
    }
    catch {
        if (Test-Path -LiteralPath $backupRoot) {
            Move-Item -LiteralPath $backupRoot -Destination $destinationRoot
        }
        throw
    }

    if (Test-Path -LiteralPath $backupRoot) {
        if (-not (Test-GeneratedCache -Path $backupRoot)) {
            throw "Refusing to delete an unmarked Device Lab cache backup: $backupRoot"
        }
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }

    Write-Information "  verified and staged $assetName ($actualHash)" -InformationAction Continue
}
finally {
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $stagingRoot) {
        if (Test-GeneratedCache -Path $stagingRoot) {
            Remove-Item -LiteralPath $stagingRoot -Recurse -Force
        }
        else {
            Write-Warning "Refusing to delete an unmarked Device Lab staging directory: $stagingRoot"
        }
    }
}

return $destinationRoot
