$ErrorActionPreference = 'Stop'

$appPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish/win-x64/Codev.Avalonia.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
    throw "Packaged Avalonia app was not found at $appPath."
}

$smokeRoot = Join-Path $env:TEMP ("Codev-Windows-UI-Smoke-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $smokeRoot
$dataRoot = Join-Path $smokeRoot 'data'
$stdoutPath = Join-Path $smokeRoot 'stdout.log'
$stderrPath = Join-Path $smokeRoot 'stderr.log'
$previousDataRoot = $env:CODEV_DATA_ROOT
$app = $null

try {
    $env:CODEV_DATA_ROOT = $dataRoot
    $app = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $windowHandle = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $deadline) {
        $app.Refresh()
        if ($app.HasExited) {
            Get-Content $stdoutPath, $stderrPath -ErrorAction SilentlyContinue
            throw "Avalonia exited before creating its main window (exit $($app.ExitCode))."
        }

        $windowHandle = $app.MainWindowHandle
        if ($windowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    }

    if ($windowHandle -eq [IntPtr]::Zero) {
        throw 'Avalonia remained running but did not expose a main-window handle within 30 seconds.'
    }

    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    if ($null -eq $window) { throw 'Windows UI Automation could not read the packaged main window.' }
    if ($window.Current.Name -ne 'Codev') {
        throw "Expected the Codev main window; UI Automation reported '$($window.Current.Name)'."
    }

    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    if ($edits.Count -lt 1) { throw 'The packaged main window exposes no editable text control.' }

    $sendName = 'Send or queue prompt; stop when the composer is empty'
    $buttonCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $sendName))
    $sendButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    if ($null -eq $sendButton) { throw "The composer send button is missing its accessible name '$sendName'." }

    Write-Host "Packaged Codev window opened with $($edits.Count) editable text control(s) and an accessible send button."
}
finally {
    if ($null -ne $app) {
        $app.Refresh()
        if (-not $app.HasExited) {
            $app.Kill($true)
            $app.WaitForExit(5000)
        }
    }

    if ($null -eq $previousDataRoot) {
        Remove-Item Env:CODEV_DATA_ROOT -ErrorAction SilentlyContinue
    }
    else {
        $env:CODEV_DATA_ROOT = $previousDataRoot
    }
}
