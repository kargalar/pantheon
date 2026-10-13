param(
    [ValidatePattern('^\d+\.\d+\.\d+([-.][A-Za-z0-9.]+)?$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$workspaceDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseDirectory = Join-Path $workspaceDirectory "artifacts/releases/$Version"
$publishDirectory = Join-Path $releaseDirectory 'Pantheon'
$archivePath = Join-Path $releaseDirectory 'Pantheon-win-x64.zip'

if (Test-Path -LiteralPath $publishDirectory) {
    throw "Release output already exists: $publishDirectory. Choose a new version or remove the old output explicitly."
}
foreach ($tool in @('ffsubsync.exe', 'ffmpeg.exe', 'ffprobe.exe', 'Licenses/FFmpeg-LICENSE.txt')) {
    $toolPath = Join-Path $workspaceDirectory "artifacts/SubtitleTools/$tool"
    if (-not (Test-Path -LiteralPath $toolPath -PathType Leaf)) {
        throw "Missing subtitle bundle file: $toolPath. Run tools/Build-SubtitleTools.ps1 first."
    }
}

New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$projectPath = Join-Path $workspaceDirectory 'src/VideoPlayer/VideoPlayer.csproj'
& dotnet publish $projectPath -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $publishDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# The full guide and its images keep the packaged README usable offline.
Copy-Item -LiteralPath (Join-Path $workspaceDirectory 'docs') -Destination $publishDirectory -Recurse
$assetsDirectory = Join-Path $publishDirectory 'src/VideoPlayer/Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $workspaceDirectory 'src/VideoPlayer/Assets/pantheon.png') -Destination $assetsDirectory
Copy-Item -LiteralPath (Join-Path $workspaceDirectory 'CONTRIBUTING.md') -Destination $publishDirectory

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $archivePath)
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
[System.IO.File]::WriteAllText($checksumPath, "$checksum  Pantheon-win-x64.zip`n", [System.Text.UTF8Encoding]::new($false))
Write-Output "Release ready: $archivePath"
Write-Output "SHA-256: $checksum"
