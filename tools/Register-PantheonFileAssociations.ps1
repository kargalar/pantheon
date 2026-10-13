param(
    [string]$PantheonPath
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PantheonPath)) {
    $publishedApp = Join-Path $PSScriptRoot "Pantheon.exe"
    $projectBuild = Join-Path $PSScriptRoot "..\dist\Pantheon\Pantheon.exe"
    $PantheonPath = if (Test-Path -LiteralPath $publishedApp) { $publishedApp } else { $projectBuild }
}

$PantheonPath = [System.IO.Path]::GetFullPath($PantheonPath)
if (-not (Test-Path -LiteralPath $PantheonPath -PathType Leaf)) {
    throw "Pantheon.exe was not found: $PantheonPath"
}

$iconDirectory = Join-Path (Split-Path -Parent $PantheonPath) "FileTypeIcons"
$formats = @(
    @{ Extension = ".mkv"; Id = "MKV"; Name = "MKV video" },
    @{ Extension = ".mp4"; Id = "MP4"; Name = "MP4 video" },
    @{ Extension = ".mov"; Id = "MOV"; Name = "MOV video" },
    @{ Extension = ".avi"; Id = "AVI"; Name = "AVI video" },
    @{ Extension = ".webm"; Id = "WEBM"; Name = "WebM video" },
    @{ Extension = ".m4v"; Id = "M4V"; Name = "M4V video" },
    @{ Extension = ".ts"; Id = "TS"; Name = "MPEG transport stream video" },
    @{ Extension = ".wmv"; Id = "WMV"; Name = "WMV video" },
    @{ Extension = ".mpg"; Id = "MPG"; Name = "MPG video" },
    @{ Extension = ".mpeg"; Id = "MPEG"; Name = "MPEG video" },
    @{ Extension = ".ogv"; Id = "OGV"; Name = "Ogg video" },
    @{ Extension = ".flv"; Id = "FLV"; Name = "Flash video" }
)

foreach ($format in $formats) {
    $iconPath = Join-Path $iconDirectory "$($format.Id.ToLowerInvariant()).ico"
    if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
        throw "File type icon was not found: $iconPath"
    }
}

$classesRoot = "HKCU:\Software\Classes"
$capabilitiesRoot = "HKCU:\Software\Pantheon\Capabilities"
$fileAssociations = Join-Path $capabilitiesRoot "FileAssociations"
$appCommand = '"{0}" "%1"' -f $PantheonPath

New-Item -Path $fileAssociations -Force | Out-Null
New-ItemProperty -Path $capabilitiesRoot -Name "ApplicationName" -Value "Pantheon" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $capabilitiesRoot -Name "ApplicationDescription" -Value "Pantheon video player" -PropertyType String -Force | Out-Null
New-Item -Path "HKCU:\Software\RegisteredApplications" -Force | Out-Null
New-ItemProperty -Path "HKCU:\Software\RegisteredApplications" -Name "Pantheon" -Value "Software\Pantheon\Capabilities" -PropertyType String -Force | Out-Null

$applicationKey = Join-Path $classesRoot "Applications\Pantheon.exe"
New-Item -Path $applicationKey -Force | Out-Null
New-ItemProperty -Path $applicationKey -Name "FriendlyAppName" -Value "Pantheon" -PropertyType String -Force | Out-Null
New-Item -Path (Join-Path $applicationKey "shell\open\command") -Force | Out-Null
Set-Item -Path (Join-Path $applicationKey "shell\open\command") -Value $appCommand
$supportedTypes = Join-Path $applicationKey "SupportedTypes"
New-Item -Path $supportedTypes -Force | Out-Null

foreach ($format in $formats) {
    $progId = "Pantheon.Video.$($format.Id)"
    $progIdKey = Join-Path $classesRoot $progId
    $iconPath = Join-Path $iconDirectory "$($format.Id.ToLowerInvariant()).ico"

    New-Item -Path $progIdKey -Force | Out-Null
    Set-Item -Path $progIdKey -Value "Pantheon $($format.Name)"
    New-Item -Path (Join-Path $progIdKey "DefaultIcon") -Force | Out-Null
    Set-Item -Path (Join-Path $progIdKey "DefaultIcon") -Value $iconPath
    New-Item -Path (Join-Path $progIdKey "shell\open\command") -Force | Out-Null
    Set-Item -Path (Join-Path $progIdKey "shell\open\command") -Value $appCommand

    $openWithProgIds = Join-Path (Join-Path $classesRoot $format.Extension) "OpenWithProgids"
    New-Item -Path $openWithProgIds -Force | Out-Null
    New-ItemProperty -Path $openWithProgIds -Name $progId -PropertyType None -Value ([byte[]]@()) -Force | Out-Null
    New-ItemProperty -Path $supportedTypes -Name $format.Extension -Value "" -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $fileAssociations -Name $format.Extension -Value $progId -PropertyType String -Force | Out-Null
}

Write-Host "Pantheon was added to the Windows app list for video file types. Choose your default player in Windows Settings."
