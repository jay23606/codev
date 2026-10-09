param(
    [string] $AppPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish/win-x64/Codev.Avalonia.exe'),
    [switch] $AllowLocalDesktop
)

$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -cne 'true' -and -not $AllowLocalDesktop) {
    throw 'This packaged data-survival smoke starts the app process. It is disabled on local desktops by default; use -AllowLocalDesktop only for an announced release-signoff check.'
}

$sourceApp = (Resolve-Path -LiteralPath $AppPath).Path
$sourceDirectory = Split-Path -Parent $sourceApp
$testRoot = Join-Path $env:TEMP ("Codev-Data-Survival-" + [guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $testRoot 'portable-app'
$profileRoot = Join-Path $testRoot 'user-data'
$appData = Join-Path $profileRoot 'Codev'
$process = $null
$smokeSucceeded = $false

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null

try {
    Copy-Item -Path (Join-Path $sourceDirectory '*') -Destination $packageDirectory -Recurse -Force
    $portableApp = Join-Path $packageDirectory (Split-Path -Leaf $sourceApp)
    if (-not (Test-Path -LiteralPath $portableApp -PathType Leaf)) {
        throw 'The isolated portable app copy is missing its executable.'
    }

    $packageFullPath = [IO.Path]::GetFullPath($packageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $profileFullPath = [IO.Path]::GetFullPath($profileRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($profileFullPath.StartsWith($packageFullPath, [StringComparison]::OrdinalIgnoreCase) -or
        $packageFullPath.StartsWith($profileFullPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The isolated profile and portable app must be separate sibling directories.'
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new($portableApp)
    $startInfo.WorkingDirectory = $packageDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.Environment['CODEV_DATA_ROOT'] = $profileRoot
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw 'The packaged app process did not start.' }

    $startupDeadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited -and [DateTime]::UtcNow -lt $startupDeadline)

    if ($process.HasExited) { throw "The packaged app exited during startup with code $($process.ExitCode)." }
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The packaged app did not expose a main window within 30 seconds.' }

    $requiredFiles = @(
        (Join-Path $appData 'avalonia-active-conversation.json'),
        (Join-Path $appData 'avalonia-conversations.json')
    )
    $persistenceDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $persisted = @($requiredFiles | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        if ($persisted.Count -eq $requiredFiles.Count) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $persistenceDeadline)
    if ($persisted.Count -ne $requiredFiles.Count) {
        throw 'The app did not create its active-conversation and conversation-store files under CODEV_DATA_ROOT.'
    }

    $beforeRemoval = @{}
    foreach ($file in $requiredFiles) {
        $beforeRemoval[$file] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }

    if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(15000)) {
        throw 'The packaged app did not close cleanly before portable uninstall simulation.'
    }
    $process.Dispose()
    $process = $null

    $resolvedPackage = (Resolve-Path -LiteralPath $packageDirectory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedTestRoot = (Resolve-Path -LiteralPath $testRoot).Path.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not ($resolvedPackage + [IO.Path]::DirectorySeparatorChar).StartsWith($resolvedTestRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a package directory outside the isolated test root.'
    }
    Remove-Item -LiteralPath $resolvedPackage -Recurse -Force
    if (Test-Path -LiteralPath $packageDirectory) { throw 'The isolated portable app directory was not removed.' }

    foreach ($file in $requiredFiles) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "User data disappeared when the portable app directory was removed: $(Split-Path -Leaf $file)."
        }
        $afterRemoval = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        if ($afterRemoval -ne $beforeRemoval[$file]) {
            throw "User data changed when the portable app directory was removed: $(Split-Path -Leaf $file)."
        }
    }

    Write-Output 'Packaged Windows data-survival smoke passed: the app closed, its extracted directory was removed, and both isolated conversation files remained byte-for-byte unchanged outside it.'
    $smokeSucceeded = $true
}
finally {
    if ($null -ne $process) {
        try {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        }
        finally { $process.Dispose() }
    }
    if ($smokeSucceeded -and (Test-Path -LiteralPath $testRoot -PathType Container)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $testRoot -PathType Container) {
        Write-Warning "Smoke failed; isolated diagnostics were preserved at $testRoot."
    }
}
