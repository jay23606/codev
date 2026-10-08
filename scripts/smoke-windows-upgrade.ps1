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
$currentInstallDirectory = Join-Path $smokeRoot 'current-install'
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

    $currentPackageSource = Split-Path -Parent (Resolve-Path -LiteralPath $CurrentAppPath).Path
    New-Item -ItemType Directory -Path $currentInstallDirectory | Out-Null
    Get-ChildItem -LiteralPath $currentPackageSource -Force | Copy-Item -Destination $currentInstallDirectory -Recurse -Force
    $currentInstallAppPath = Join-Path $currentInstallDirectory 'Codev.Avalonia.exe'
    if (-not (Test-Path -LiteralPath $currentInstallAppPath -PathType Leaf)) {
        throw 'The isolated current-package copy did not contain Codev.Avalonia.exe.'
    }

    $currentWindow = Start-CodevPackage $currentInstallAppPath
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

    $persistedAfterUpgrade = @(Get-Content -LiteralPath $conversationsPath -Raw | ConvertFrom-Json -AsHashtable |
        Where-Object { $_.Draft -ceq $draft })
    if ($persistedAfterUpgrade.Count -ne 1) {
        throw 'The upgraded profile did not retain the sentinel draft before package removal.'
    }

    $resolvedSmokeRoot = [System.IO.Path]::GetFullPath($smokeRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedInstallDirectory = [System.IO.Path]::GetFullPath($currentInstallDirectory)
    if (-not $resolvedInstallDirectory.StartsWith($resolvedSmokeRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a package directory outside the isolated upgrade-smoke folder.'
    }
    $installEntry = Get-Item -LiteralPath $resolvedInstallDirectory -Force
    if (-not $installEntry.PSIsContainer -or ($installEntry.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to remove an unexpected or linked current-package directory.'
    }

    Remove-Item -LiteralPath $resolvedInstallDirectory -Recurse -Force
    if (Test-Path -LiteralPath $resolvedInstallDirectory) {
        throw 'The isolated current-package directory remained after simulated portable-package uninstall.'
    }
    if (-not (Test-Path -LiteralPath $conversationsPath -PathType Leaf)) {
        throw 'Removing the portable package also removed the user conversation store.'
    }
    $persistedAfterRemoval = @(Get-Content -LiteralPath $conversationsPath -Raw | ConvertFrom-Json -AsHashtable |
        Where-Object { $_.Draft -ceq $draft })
    if ($persistedAfterRemoval.Count -ne 1) {
        throw 'User conversation data did not remain readable after simulated portable-package uninstall.'
    }

    Write-Host 'Upgrade and portable-package removal smoke passed: the latest public Windows release draft survived upgrade, and removing the isolated app directory left the user profile readable with that draft. No prompt was sent.'
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
