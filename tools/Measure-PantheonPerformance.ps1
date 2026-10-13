param(
    [Parameter(Mandatory = $true)]
    [string]$VideoPath,
    [ValidateRange(1, 120)]
    [int]$SampleSeconds = 8,
    [ValidateRange(0, 30)]
    [int]$WarmupSeconds = 2
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = Split-Path $PSScriptRoot -Parent
$applicationPath = Join-Path $workspaceRoot 'src\VideoPlayer\bin\Release\net10.0\publish\Pantheon.exe'
$resolvedVideoPath = (Resolve-Path -LiteralPath $VideoPath).Path
if (-not (Test-Path -LiteralPath $applicationPath -PathType Leaf)) {
    throw "Release app not found. Run dotnet publish first: $applicationPath"
}

$profilePath = Join-Path $env:TEMP ('pantheon-app-perf-' + [guid]::NewGuid().ToString('N'))
$previousDataDirectory = $env:PANTHEON_DATA_DIRECTORY
$appProcess = $null
try {
    $env:PANTHEON_DATA_DIRECTORY = $profilePath
    $appProcess = Start-Process -FilePath $applicationPath `
        -ArgumentList ('"' + $resolvedVideoPath + '"') `
        -WorkingDirectory (Split-Path $applicationPath) `
        -WindowStyle Hidden -PassThru
} finally {
    $env:PANTHEON_DATA_DIRECTORY = $previousDataDirectory
}

try {
    if ($WarmupSeconds -gt 0) { Start-Sleep -Seconds $WarmupSeconds }
    $process = Get-Process -Id $appProcess.Id -ErrorAction Stop
    $cpuBefore = $process.CPU
    $workingSetBefore = $process.WorkingSet64
    $privateBytesBefore = $process.PrivateMemorySize64
    $sampleTimer = [System.Diagnostics.Stopwatch]::StartNew()

    $gpuCounterPaths = @(
        '\GPU Engine(*)\Utilization Percentage',
        '\GPU Process Memory(*)\Dedicated Usage',
        '\GPU Process Memory(*)\Shared Usage'
    )
    try {
        $gpuSamples = Get-Counter -Counter $gpuCounterPaths -SampleInterval 1 -MaxSamples $SampleSeconds
    } catch {
        Write-Warning "GPU performance counters are unavailable: $($_.Exception.Message)"
        Start-Sleep -Seconds $SampleSeconds
        $gpuSamples = $null
    }
    $sampleTimer.Stop()
    $process.Refresh()
    $cpuSeconds = $process.CPU - $cpuBefore

    [pscustomobject]@{
        ProcessId = $appProcess.Id
        VideoPath = $resolvedVideoPath
        CpuSeconds = [math]::Round($cpuSeconds, 2)
        CpuPercentOfOneCore = [math]::Round($cpuSeconds / $sampleTimer.Elapsed.TotalSeconds * 100, 1)
        WorkingSetBeforeMiB = [math]::Round($workingSetBefore / 1MB, 1)
        WorkingSetAfterMiB = [math]::Round($process.WorkingSet64 / 1MB, 1)
        PrivateBytesBeforeMiB = [math]::Round($privateBytesBefore / 1MB, 1)
        PrivateBytesAfterMiB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        SampleSeconds = [math]::Round($sampleTimer.Elapsed.TotalSeconds, 2)
        IsolatedProfile = $profilePath
    }

    if ($null -ne $gpuSamples) {
        $gpuSamples.CounterSamples |
            Where-Object { $_.Path -match "pid_$($appProcess.Id)_" -and $_.CookedValue -gt 0 } |
            Group-Object Path |
            ForEach-Object {
                $path = $_.Name
                $average = ($_.Group | Measure-Object CookedValue -Average).Average
                if ($path -match 'gpu process memory') {
                    [pscustomobject]@{
                        Metric = ($path -split '\\')[-1]
                        Average = [math]::Round($average / 1MB, 1)
                        Unit = 'MiB'
                    }
                } else {
                    $engine = if ($path -match 'engtype_([^\)]+)') { $matches[1] } else { 'GPU engine' }
                    [pscustomobject]@{
                        Metric = $engine
                        Average = [math]::Round($average, 1)
                        Unit = '%'
                    }
                }
            } |
            Sort-Object Metric, Unit |
            Format-Table -AutoSize
    }

    $logFile = Get-ChildItem (Join-Path $profilePath 'Logs') -Filter 'Pantheon-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $logFile) {
        Select-String -Path $logFile.FullName -Pattern 'Active video decoder|Media playback failed'
    }
} finally {
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        if (-not $appProcess.CloseMainWindow()) { Stop-Process -Id $appProcess.Id -Force }
        $appProcess.WaitForExit(5000) | Out-Null
    }
}
