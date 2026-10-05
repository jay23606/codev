param(
    [Parameter(Mandatory = $true)]
    [string]$CurrentAppPath
)

$ErrorActionPreference = 'Stop'

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'The package-upgrade smoke must run on a disposable GitHub-hosted runner.'
}
if (-not [string]::IsNullOrWhiteSpace($env:CODEV_DATA_ROOT)) {
    throw 'CODEV_DATA_ROOT must be unset so both packages use the runner profile being tested.'
}
if (-not (Test-Path -LiteralPath $CurrentAppPath -PathType Leaf)) {
    throw "Current packaged app was not found at $CurrentAppPath."
}

$localDataRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$codevProfile = Join-Path $localDataRoot 'Codev'
if (Get-Process -Name 'Codev.Avalonia' -ErrorAction SilentlyContinue) {
    throw 'A Codev.Avalonia process is already running on the disposable runner.'
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$smokeRoot = Join-Path $env:RUNNER_TEMP ("Codev-upgrade-smoke-" + [guid]::NewGuid().ToString('N'))
$previousArchive = Join-Path $smokeRoot 'previous-release.zip'
$previousDirectory = Join-Path $smokeRoot 'previous-release'
$stdoutPath = Join-Path $smokeRoot 'codev.stdout.log'
$stderrPath = Join-Path $smokeRoot 'codev.stderr.log'
$previousReleaseUri = 'https://github.com/jay23606/codev/releases/latest/download/Codev-Avalonia-preview-win-x64.zip'
$profileBackupPath = Join-Path $localDataRoot ('.Codev-pre-upgrade-' + [guid]::NewGuid().ToString('N'))
$testProfilePath = Join-Path $localDataRoot ('.Codev-upgrade-test-' + [guid]::NewGuid().ToString('N'))
$profileStaged = $false
$process = $null

function Start-CodevPackage([string]$ExecutablePath) {
    $script:process = Start-Process -FilePath $ExecutablePath -WorkingDirectory (Split-Path $ExecutablePath -Parent) -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $script:process.Refresh()
        if ($script:process.HasExited) {
            Get-Content $stdoutPath, $stderrPath -ErrorAction SilentlyContinue
            throw "Codev exited before creating its window (exit $($script:process.ExitCode))."
        }

        $windowHandle = $script:process.MainWindowHandle
    } while ($windowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

    if ($windowHandle -eq [IntPtr]::Zero) {
        throw 'Codev remained running but did not expose its main window within 30 seconds.'
    }

    $window = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    if ($null -eq $window -or $window.Current.Name -ne 'Codev') {
        throw 'The packaged process did not expose the expected Codev main window.'
    }
    return $window
}

function Stop-CodevPackage {
    if ($null -eq $script:process) { return }
    $script:process.Refresh()
    if (-not $script:process.HasExited) {
        $null = $script:process.CloseMainWindow()
        if (-not $script:process.WaitForExit(15000)) {
            $script:process.Kill($true)
            $script:process.WaitForExit(5000)
            throw 'Codev did not finish its orderly shutdown on the disposable runner.'
        }
    }
    $script:process = $null
}

function Find-Composer($Window) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'ComposerTextBox')
    $composer = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $composer) { throw 'The packaged app does not expose its composer to UI Automation.' }
    return $composer
}

New-Item -ItemType Directory -Path $smokeRoot | Out-Null
try {
    Invoke-WebRequest -Uri $previousReleaseUri -OutFile $previousArchive -MaximumRedirection 10
    Expand-Archive -LiteralPath $previousArchive -DestinationPath $previousDirectory
    $previousAppPath = Join-Path $previousDirectory 'Codev.Avalonia.exe'
    if (-not (Test-Path -LiteralPath $previousAppPath -PathType Leaf)) {
        throw 'The latest public Windows release archive did not contain Codev.Avalonia.exe.'
    }

    if (Test-Path -LiteralPath $codevProfile) {
        $existingProfile = Get-Item -LiteralPath $codevProfile -Force
        if (-not $existingProfile.PSIsContainer -or
            ($existingProfile.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to move an unexpected Codev profile entry at $codevProfile."
        }
        Move-Item -LiteralPath $codevProfile -Destination $profileBackupPath
        $profileStaged = $true
    }
    if (Test-Path -LiteralPath $codevProfile) {
        throw 'Could not clear the disposable runner profile for the upgrade smoke.'
    }

    $draft = 'Codev package upgrade smoke · ' + [guid]::NewGuid().ToString('N')
    $previousWindow = Start-CodevPackage $previousAppPath
    $previousComposer = Find-Composer $previousWindow
    $valuePattern = $null
    if (-not $previousComposer.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        throw 'The previous release composer does not expose editable text through UI Automation.'
    }
    $valuePattern.SetValue($draft)

    $conversationsPath = Join-Path $codevProfile 'avalonia-conversations.json'
    $persistDeadline = [DateTime]::UtcNow.AddSeconds(15)
    $persistedConversationCount = 0
    do {
        Start-Sleep -Milliseconds 250
        try {
            if (Test-Path -LiteralPath $conversationsPath -PathType Leaf) {
                $conversations = @(Get-Content -LiteralPath $conversationsPath -Raw | ConvertFrom-Json -AsHashtable)
                $persistedConversationCount = @($conversations | Where-Object { $_.Draft -ceq $draft }).Count
            }
        }
        catch { }
    } while ($persistedConversationCount -ne 1 -and [DateTime]::UtcNow -lt $persistDeadline)
    if ($persistedConversationCount -ne 1) {
        throw 'The previous release did not persist the sentinel draft before shutdown.'
    }

    Stop-CodevPackage

    $currentWindow = Start-CodevPackage $CurrentAppPath
    $currentComposer = Find-Composer $currentWindow
    $currentValuePattern = $null
    if (-not $currentComposer.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$currentValuePattern)) {
        throw 'The current release composer does not expose editable text through UI Automation.'
    }

    $restoreDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 250
        $restoredDraft = $currentValuePattern.Current.Value
    } while ($restoredDraft -cne $draft -and [DateTime]::UtcNow -lt $restoreDeadline)
    if ($restoredDraft -cne $draft) {
        throw 'The current package did not restore the previous release draft.'
    }

    Stop-CodevPackage
    Write-Host 'Upgrade smoke passed: a draft saved by the latest public Windows release survived an update to the current package. No prompt was sent.'
}
finally {
    try { Stop-CodevPackage } catch { Write-Warning $_ }
    if (Test-Path -LiteralPath $codevProfile) {
        if (Test-Path -LiteralPath $testProfilePath) {
            throw "Refusing to overwrite the retained upgrade-test profile at $testProfilePath."
        }
        Move-Item -LiteralPath $codevProfile -Destination $testProfilePath
    }
    if ($profileStaged) {
        if (Test-Path -LiteralPath $codevProfile) {
            throw 'Could not restore the pre-existing disposable runner profile after the upgrade smoke.'
        }
        Move-Item -LiteralPath $profileBackupPath -Destination $codevProfile
    }
}
