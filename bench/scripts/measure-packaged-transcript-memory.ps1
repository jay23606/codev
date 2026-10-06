param(
    [Parameter(Mandatory)] [string] $Executable,
    [Parameter(Mandatory)] [string] $SeedProfile,
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $OutputRoot,
    [int] $SamplesPerRun = 20,
    [string] $Commit = '',
    [string] $Platform = '',
    [string] $NetworkEndpoint = 'http://127.0.0.1:1'
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Commit)) {
    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $Commit = (& git -C $repositoryRoot rev-parse --short HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not determine the measured source commit.' }
}
if ([string]::IsNullOrWhiteSpace($Platform)) {
    $os = Get-CimInstance Win32_OperatingSystem
    $Platform = "$($os.Caption); build $([Environment]::OSVersion.Version.Build); locally published self-contained win-x64 Avalonia single-file app"
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$runs = [System.Collections.Generic.List[object]]::new()
$scenarios = @('loaded', 'empty', 'loaded', 'empty', 'loaded', 'empty')

for ($index = 0; $index -lt $scenarios.Count; $index++) {
    $runNumber = $index + 1
    $scenario = $scenarios[$index]
    $dataRoot = Join-Path $OutputRoot ("run-{0:D2}-{1}" -f $runNumber, $scenario)
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    if ($scenario -eq 'loaded') {
        Copy-Item -LiteralPath (Join-Path $SeedProfile 'Codev') -Destination $dataRoot -Recurse
    }
    $codevData = Join-Path $dataRoot 'Codev'
    New-Item -ItemType Directory -Path $codevData -Force | Out-Null
    $isolatedSettings = ConvertTo-Json -InputObject @{ Theme = 'dark'; OllamaEndpoint = $NetworkEndpoint }
    [System.IO.File]::WriteAllText((Join-Path $codevData 'avalonia-settings.json'), $isolatedSettings,
        [System.Text.UTF8Encoding]::new($false))

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $startInfo.WorkingDirectory = Split-Path -Parent $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.Environment['CODEV_DATA_ROOT'] = $dataRoot
    $launchTimer = [System.Diagnostics.Stopwatch]::StartNew()
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        $windowDeadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
        } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited -and [DateTime]::UtcNow -lt $windowDeadline)

        if ($process.HasExited) { throw "App exited during $scenario run $runNumber with code $($process.ExitCode)." }
        if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw "No main window handle during $scenario run $runNumber." }
        $launchTimer.Stop()
        $windowHandleMs = [Math]::Round($launchTimer.Elapsed.TotalMilliseconds, 1)

        $samples = [System.Collections.Generic.List[object]]::new()
        for ($sampleIndex = 0; $sampleIndex -lt $SamplesPerRun; $sampleIndex++) {
            $process.Refresh()
            $samples.Add([pscustomobject]@{
                Sample = $sampleIndex + 1
                ElapsedMs = $sampleIndex * 500
                PrivateMiB = [Math]::Round($process.PrivateMemorySize64 / 1MB, 1)
                WorkingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 1)
            })
            Start-Sleep -Milliseconds 500
        }

        $private = @($samples.PrivateMiB | Sort-Object)
        $working = @($samples.WorkingSetMiB | Sort-Object)
        $runs.Add([pscustomobject]@{
            Run = $runNumber
            Scenario = $scenario
            WindowHandleMs = $windowHandleMs
            Samples = @($samples)
            PrivateMiBMedian = [Math]::Round(($private[[int][Math]::Floor(($private.Count - 1) / 2)] + $private[[int][Math]::Floor($private.Count / 2)]) / 2, 1)
            PrivateMiBMax = [Math]::Round(($samples.PrivateMiB | Measure-Object -Maximum).Maximum, 1)
            WorkingSetMiBMedian = [Math]::Round(($working[[int][Math]::Floor(($working.Count - 1) / 2)] + $working[[int][Math]::Floor($working.Count / 2)]) / 2, 1)
            WorkingSetMiBMax = [Math]::Round(($samples.WorkingSetMiB | Measure-Object -Maximum).Maximum, 1)
            MainWindowHandleObserved = $true
            WindowTitle = $process.MainWindowTitle
        })
        Write-Output ("Completed {0} run {1}: private median {2} MiB, peak {3} MiB" -f $scenario, $runNumber, $runs[-1].PrivateMiBMedian, $runs[-1].PrivateMiBMax)
    }
    finally {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}

$projectFiles = @(Get-ChildItem -LiteralPath $Project -Recurse -File)
$result = [ordered]@{
    Date = Get-Date -Format 'yyyy-MM-dd'
    Commit = $Commit
    Platform = $Platform
    Method = 'Three launches per scenario, alternating loaded and empty profile. Process.Start-to-main-window-handle elapsed time is recorded, then each process is sampled every 500ms for 10 seconds. Each loaded run copied the same synthetic profile into a unique CODEV_DATA_ROOT; empty runs used a fresh root. Both scenarios force the Ollama endpoint to the refused loopback address recorded below. No prompt was sent or model inference run. App processes launched without shell execution and stayed in the background.'
    Project = @{ Bytes = ($projectFiles | Measure-Object -Property Length -Sum).Sum; SelectedContextFiles = 100; Synthetic = $true; Files = $projectFiles.Count }
    Conversation = @{ Characters = 537276; Messages = 240; Synthetic = $true }
    PromptSent = $false
    NetworkEndpoint = $NetworkEndpoint
    ModelInference = $false
    Runs = @($runs)
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputRoot 'results.json') -Encoding utf8
