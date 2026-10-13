param(
    [string]$Python = 'python',
    [string]$FfmpegDirectory = ''
)

$ErrorActionPreference = 'Stop'
$workspaceDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildDirectory = Join-Path $workspaceDirectory 'artifacts/subtitle-tools'
$outputDirectory = Join-Path $workspaceDirectory 'artifacts/SubtitleTools'
New-Item -ItemType Directory -Path $buildDirectory, $outputDirectory -Force | Out-Null

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell otherwise treats redirected native progress on stderr as a terminating error.
        $ErrorActionPreference = 'Continue'
        & $Executable @Arguments
    } finally { $ErrorActionPreference = $previousErrorActionPreference }
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

if ([string]::IsNullOrWhiteSpace($FfmpegDirectory)) {
    $ffmpegCommand = Get-Command 'ffmpeg.exe' -ErrorAction Stop
    $ffmpegFile = Get-Item -LiteralPath $ffmpegCommand.Source
    $ffmpegPath = if ($ffmpegFile.LinkType -eq 'SymbolicLink') { $ffmpegFile.Target[0] } else { $ffmpegFile.FullName }
    $FfmpegDirectory = Split-Path -Path $ffmpegPath -Parent
}
foreach ($file in @('ffmpeg.exe', 'ffprobe.exe')) {
    $source = Join-Path $FfmpegDirectory $file
    if (-not (Test-Path -LiteralPath $source)) { throw "Required FFmpeg tool is missing: $source" }
    Copy-Item -LiteralPath $source -Destination $outputDirectory -Force
}

Invoke-Checked $Python @('-m', 'venv', $buildDirectory)
$buildPython = Join-Path $buildDirectory 'Scripts/python.exe'
Invoke-Checked $buildPython @('-m', 'pip', 'install', 'ffsubsync==0.4.31', 'pyinstaller==6.22.3')
$entryPoint = Join-Path $buildDirectory 'Lib/site-packages/ffsubsync/ffsubsync.py'
$hookDirectory = Join-Path $buildDirectory 'hooks'
New-Item -ItemType Directory -Path $hookDirectory -Force | Out-Null
# webrtcvad-wheels supplies the webrtcvad module; the stock hook expects the old distribution name.
Set-Content -LiteralPath (Join-Path $hookDirectory 'hook-webrtcvad.py') -Encoding UTF8 -Value @(
    'from PyInstaller.utils.hooks import copy_metadata',
    "datas = copy_metadata('webrtcvad-wheels')"
)
Invoke-Checked $buildPython @('-m', 'PyInstaller', '--noconfirm', '--clean', '--onedir',
    '--name', 'ffsubsync', '--collect-all', 'webrtcvad', '--collect-all', 'ffsubsync',
    '--additional-hooks-dir', $hookDirectory,
    '--distpath', (Join-Path $buildDirectory 'bundle'), '--workpath', (Join-Path $buildDirectory 'build'),
    '--specpath', $buildDirectory, $entryPoint)
Copy-Item -Path (Join-Path $buildDirectory 'bundle/ffsubsync/*') -Destination $outputDirectory -Recurse -Force

$licenseDirectory = Join-Path $outputDirectory 'Licenses'
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
Get-ChildItem -Path (Join-Path $buildDirectory 'Lib/site-packages') -Directory -Filter '*.dist-info' | ForEach-Object {
    $packageDirectory = Join-Path $licenseDirectory $_.Name
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    Get-ChildItem -LiteralPath $_.FullName -File | Where-Object { $_.Name -match 'LICENSE|COPYING|NOTICE|METADATA' } |
        Copy-Item -Destination $packageDirectory -Force
    $licenses = Join-Path $_.FullName 'licenses'
    if (Test-Path -LiteralPath $licenses) { Copy-Item -LiteralPath $licenses -Destination $packageDirectory -Recurse -Force }
}
$ffmpegLicense = Join-Path (Split-Path $FfmpegDirectory -Parent) 'LICENSE'
if (Test-Path -LiteralPath $ffmpegLicense) { Copy-Item -LiteralPath $ffmpegLicense -Destination (Join-Path $licenseDirectory 'FFmpeg-LICENSE.txt') -Force }
Invoke-Checked (Join-Path $outputDirectory 'ffsubsync.exe') @('--version')
Write-Output "Subtitle tools ready: $outputDirectory. Rebuild or publish Pantheon to include them."
