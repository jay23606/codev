param(
    [string]$AppPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish/win-x64/Codev.Avalonia.exe')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
    throw "Packaged Avalonia app was not found at $appPath."
}

$smokeRoot = Join-Path $env:TEMP ("Codev-Windows-UI-Smoke-" + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $smokeRoot
$dataRoot = Join-Path $smokeRoot 'data'
$stdoutPath = Join-Path $smokeRoot 'stdout.log'
$stderrPath = Join-Path $smokeRoot 'stderr.log'
$previousDataRoot = $env:CODEV_DATA_ROOT
$smokeSucceeded = $false
$app = $null

function Find-ByAutomationId($Element, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-BackupMenuItem([string]$AutomationId) {
    $item = Find-ByAutomationId ([System.Windows.Automation.AutomationElement]::RootElement) $AutomationId
    if ($null -eq $item) { throw "The backup menu item '$AutomationId' is missing." }
    # Avalonia's native MenuFlyout menu items expose scrolling but no InvokePattern.
    # Focus plus Enter exercises the same keyboard activation a user can use.
    $item.SetFocus()
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
}

function Wait-ForBackupDialog([string]$Title) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Title))
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $dialog = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $dialog) { return $dialog }
        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The native file picker '$Title' did not open."
}

function Set-BackupDialogPath($Dialog, [string]$Path) {
    if ($Dialog.Current.Name -eq 'Import Codev conversation backup') {
        $fileNameCondition = [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
                '1148'),
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ClassNameProperty,
                'Edit'))
        $fileName = $Dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $fileNameCondition)
    }
    else {
        $fileName = Find-ByAutomationId $Dialog '1001'
    }
    if ($null -eq $fileName) { throw 'The native file picker filename control is missing.' }
    $bounds = $fileName.Current.BoundingRectangle
    if ($bounds.IsEmpty) { throw 'The native file picker filename control is not visible.' }
    $dialogHandle = [IntPtr]$Dialog.Current.NativeWindowHandle
    [void][CodevCommonDialog]::SetForegroundWindow($dialogHandle)
    [CodevCommonDialog]::ClickAt([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait($Path)
    if ($env:CODEV_UI_SMOKE_DIAGNOSTICS -eq '1') {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        Write-Host "Filename after typing: '$($fileName.Current.Name)' bounds=$($bounds.Left),$($bounds.Top),$($bounds.Width),$($bounds.Height); focused='$($focused.Current.Name)' id='$($focused.Current.AutomationId)' class='$($focused.Current.ClassName)'"
    }
}

function Invoke-BackupDialogButton($Dialog, [string]$Name) {
    if ($Name -notin @('Save', 'Open')) { throw "Unsupported native file picker action '$Name'." }
    $buttonCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            '1'),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty,
            'Button'))
    $button = $Dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
    if ($null -eq $button) { throw "The native file picker '$Name' button is missing." }
    $bounds = $button.Current.BoundingRectangle
    if ($bounds.IsEmpty) { throw "The native file picker '$Name' button is not visible." }
    if ($env:CODEV_UI_SMOKE_DIAGNOSTICS -eq '1') { Write-Host "Picker $Name button name='$($button.Current.Name)' id='$($button.Current.AutomationId)' class='$($button.Current.ClassName)' bounds=$($bounds.Left),$($bounds.Top),$($bounds.Width),$($bounds.Height)" }
    [CodevCommonDialog]::ClickAt([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
}

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
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class CodevCommonDialog
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static void ClickAt(int x, int y)
    {
        if (!SetCursorPos(x, y)) throw new InvalidOperationException("Could not focus the native filename field.");
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
'@
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

    $modeCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'ProjectCommandModeButton')
    $modeButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $modeCondition)
    if ($null -eq $modeButton) { throw 'The footer permission mode selector is missing.' }
    if (-not $modeButton.Current.IsEnabled) { throw 'The footer permission mode selector is disabled for a fresh profile.' }
    if ($modeButton.Current.Name -ne 'Auto ▾') {
        throw "A fresh profile should start in Auto mode; UI Automation reported '$($modeButton.Current.Name)'."
    }

    $invokePattern = $null
    if (-not $modeButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
        throw 'The footer permission mode selector cannot be opened through UI Automation.'
    }
    $invokePattern.Invoke()
    Start-Sleep -Milliseconds 250

    $menuItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::MenuItem)
    $menuItems = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuItemCondition)
    $menuItemNames = @()
    for ($index = 0; $index -lt $menuItems.Count; $index++) {
        $menuItemNames += $menuItems.Item($index).Current.Name
    }

    $expectedModes = @(
        'Auto · approve unless denied',
        'Allowlist · run saved exact commands',
        'Read-only · allow recognized inspections',
        'Ask every time')
    foreach ($expectedMode in $expectedModes) {
        if ($expectedMode -notin $menuItemNames) {
            throw "The footer permission menu is missing '$expectedMode'. Found: $($menuItemNames -join ', ')"
        }
    }
    $invokePattern.Invoke()
    Start-Sleep -Milliseconds 150

    $backupPath = Join-Path $smokeRoot ('Codev-native-picker-' + [guid]::NewGuid().ToString('N') + '.codev.json')
    $backupButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'ChatBackupsButton'))
    if ($null -eq $backupButton) { throw 'The Chat backups menu button is missing.' }
    $backupButtonInvoke = $null
    if (-not $backupButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$backupButtonInvoke)) {
        throw 'The Chat backups menu button cannot be opened through UI Automation.'
    }

    $backupButtonInvoke.Invoke()
    Start-Sleep -Milliseconds 200
    Invoke-BackupMenuItem 'ExportAllChatsMenuItem'

    $saveDialog = Wait-ForBackupDialog 'Export all Codev conversations'
    Set-BackupDialogPath $saveDialog $backupPath
    Invoke-BackupDialogButton $saveDialog 'Save'
    $fileDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $backupPath -PathType Leaf) -and [DateTime]::UtcNow -lt $fileDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
        throw 'The native Save dialog did not write the selected Codev backup file.'
    }

    $backup = @(Get-Content -LiteralPath $backupPath -Raw | ConvertFrom-Json -AsHashtable)
    if ($backup.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$backup[0].Id)) {
        throw 'The native Save dialog output did not contain the expected conversation backup.'
    }
    $originalConversationId = [string]$backup[0].Id

    $backupButtonInvoke.Invoke()
    Start-Sleep -Milliseconds 200
    Invoke-BackupMenuItem 'ImportChatsMenuItem'

    $openDialog = Wait-ForBackupDialog 'Import Codev conversation backup'
    Set-BackupDialogPath $openDialog $backupPath
    Invoke-BackupDialogButton $openDialog 'Open'

    $conversationPath = Join-Path $dataRoot 'Codev\avalonia-conversations.json'
    $importDeadline = [DateTime]::UtcNow.AddSeconds(10)
    $importedConversations = @()
    do {
        Start-Sleep -Milliseconds 100
        try {
            if (Test-Path -LiteralPath $conversationPath -PathType Leaf) {
                $importedConversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json -AsHashtable)
            }
        }
        catch { }
    } while ($importedConversations.Count -lt 2 -and [DateTime]::UtcNow -lt $importDeadline)
    if ($importedConversations.Count -ne 2) {
        throw "The native Open dialog did not import a second conversation; persisted count was $($importedConversations.Count)."
    }
    $originalMatches = @($importedConversations | Where-Object { [string]$_.Id -eq $originalConversationId })
    $newMatches = @($importedConversations | Where-Object { [string]$_.Id -ne $originalConversationId })
    if ($originalMatches.Count -ne 1 -or $newMatches.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$newMatches[0].Id)) {
        throw 'Importing the selected file did not preserve the original conversation and add one with a fresh ID.'
    }

    $smokeSucceeded = $true
    Write-Host "Packaged Codev window opened with $($edits.Count) editable text control(s), an accessible send button, and the Auto footer selector. Native Save/Open dialogs round-tripped the conversation backup in an isolated profile."
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

    if ($smokeSucceeded) {
        $resolvedSmokeRoot = [System.IO.Path]::GetFullPath($smokeRoot)
        $resolvedTempRoot = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
        if (-not $resolvedSmokeRoot.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($resolvedSmokeRoot) -notlike 'Codev-Windows-UI-Smoke-*') {
            throw 'Refusing to remove an unexpected packaged-app smoke directory.'
        }
        Remove-Item -LiteralPath $resolvedSmokeRoot -Recurse -Force
    }
}
