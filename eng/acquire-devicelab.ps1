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

$lock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
$entry = $lock.component
$destinationRoot = [System.IO.Path]::GetFullPath($Destination)
$stampPath = Join-Path $destinationRoot '.pinned-version'
$executablePath = Join-Path $destinationRoot $entry.executable

# The stamp records which pin produced this tree. Without it, a stale staging directory from an
# earlier pin would be reused silently and the installer would ship the previous version.
if (-not $Force -and
    (Test-Path -LiteralPath $stampPath -PathType Leaf) -and
    (Test-Path -LiteralPath $executablePath -PathType Leaf) -and
    (Get-Content -LiteralPath $stampPath -Raw).Trim() -ceq $entry.assetSha256) {
    Write-Information "Device Lab $($entry.version) is already staged." -InformationAction Continue
    return $destinationRoot
}

if (Test-Path -LiteralPath $destinationRoot) {
    Remove-Item -LiteralPath $destinationRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) (
    'WSGM-DeviceLab-{0}-{1}' -f $PID, $entry.asset)

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

    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actualHash -cne $entry.assetSha256) {
        throw "Hash mismatch for $($entry.asset): expected $($entry.assetSha256), got $actualHash."
    }

    Expand-Archive -LiteralPath $archivePath -DestinationPath $destinationRoot -Force

    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "The verified Device Lab archive did not contain $($entry.executable)."
    }

    Set-Content -LiteralPath $stampPath -Value $entry.assetSha256 -NoNewline
    Write-Information "  verified and staged $($entry.asset) ($actualHash)" -InformationAction Continue
}
catch {
    # A partial tree is worse than none: the build would stage it and ship an incomplete tool.
    if (Test-Path -LiteralPath $destinationRoot) {
        Remove-Item -LiteralPath $destinationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
}

return $destinationRoot
