<#
.SYNOPSIS
    Downloads the MediaMTX binary into tools/bin.

.DESCRIPTION
    MediaMTX is the RTSP/WebRTC edge server ThePlayer supervises as a child process. It is not
    committed to the repository, so this script fetches the right build for this machine.

    The binary is deliberately downloaded by a script rather than by the application at startup:
    a network fetch during boot is fragile, hard to diagnose, and surprising in an air-gapped or
    container environment. MediaMtxSupervisor only ever looks for a binary that is already there.

.PARAMETER Version
    A specific release tag, e.g. "v1.9.3". Defaults to the latest release.

.EXAMPLE
    ./tools/fetch-mediamtx.ps1
#>
[CmdletBinding()]
param(
    [string]$Version = 'latest'
)

$ErrorActionPreference = 'Stop'

$repository = 'bluenviron/mediamtx'
$toolsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$binDirectory = Join-Path $toolsRoot 'bin'

# MediaMTX names its assets by GOARCH, which is not what .NET or PowerShell call these.
$architecture = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64'   { 'amd64' }
    'Arm64' { 'arm64' }
    default { throw "Unsupported architecture: $_" }
}

if ($Version -eq 'latest') {
    Write-Host 'Looking up the latest MediaMTX release...'
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repository/releases/latest" `
        -Headers @{ 'User-Agent' = 'ThePlayer-setup' }
    $Version = $release.tag_name
}

$assetName = "mediamtx_${Version}_windows_${architecture}.zip"
$downloadUrl = "https://github.com/$repository/releases/download/$Version/$assetName"

Write-Host "Downloading $assetName..."

New-Item -ItemType Directory -Force -Path $binDirectory | Out-Null
$archivePath = Join-Path ([System.IO.Path]::GetTempPath()) $assetName

try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing

    # -Force so re-running the script upgrades in place rather than failing on an existing binary.
    Expand-Archive -Path $archivePath -DestinationPath $binDirectory -Force

    $binaryPath = Join-Path $binDirectory 'mediamtx.exe'
    if (-not (Test-Path $binaryPath)) {
        throw "The archive did not contain mediamtx.exe."
    }

    # The bundled config is unused - ThePlayer generates its own at startup so that stream paths,
    # and the camera credentials in them, are never written to a file on disk.
    Remove-Item (Join-Path $binDirectory 'mediamtx.yml') -ErrorAction SilentlyContinue

    Write-Host "MediaMTX $Version installed at $binaryPath" -ForegroundColor Green
}
finally {
    Remove-Item $archivePath -ErrorAction SilentlyContinue
}
