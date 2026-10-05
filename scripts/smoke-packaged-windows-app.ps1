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

function Set-PermissionMode($Window, $ModeButton, [string]$MenuItemName, [string]$ExpectedLabel, [string]$ExpectedSetting) {
    $buttonInvoke = $null
    if (-not $ModeButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$buttonInvoke)) {
        throw 'The footer permission mode selector cannot be opened through UI Automation.'
    }
    $buttonInvoke.Invoke()
    Start-Sleep -Milliseconds 150

    $menuItemCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $MenuItemName))
    $menuItem = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $menuItemCondition)
    if ($null -eq $menuItem) { throw "The footer permission menu item '$MenuItemName' is missing." }
    # Avalonia MenuFlyout items are exposed in UI Automation without InvokePattern.
    # Focus plus Enter exercises their normal keyboard activation path.
    $menuItem.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

    $settingsPath = Join-Path $dataRoot 'Codev\avalonia-settings.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $persistedMode = $null
        if (Test-Path -LiteralPath $settingsPath -PathType Leaf) {
            try {
                $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
                $persistedMode = [string]$settings.DefaultProjectCommandPermissionMode
            }
            catch { }
        }
        if ($ModeButton.Current.Name -eq $ExpectedLabel -and $persistedMode -eq $ExpectedSetting) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Selecting '$MenuItemName' did not persist '$ExpectedSetting' or update the footer to '$ExpectedLabel' (label='$($ModeButton.Current.Name)', setting='$persistedMode')."
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
    if ($env:GITHUB_ACTIONS -eq 'true') {
        Write-Host "Native picker '$($Dialog.Current.Name)' filename: id='$($fileName.Current.AutomationId)' class='$($fileName.Current.ClassName)' type='$($fileName.Current.ControlType.ProgrammaticName)' bounds=$([int]$bounds.Left),$([int]$bounds.Top),$([int]$bounds.Width),$([int]$bounds.Height)"
    }
    $dialogHandle = [CodevCommonDialog]::FindWindowByTitle($null, $Dialog.Current.Name)
    if ($dialogHandle -eq [IntPtr]::Zero) {
        $dialogHandle = [CodevCommonDialog]::FindWindowByTitle('#32770', $null)
    }
    $automationWindowHandle = [IntPtr]$Dialog.Current.NativeWindowHandle
    if ($dialogHandle -eq [IntPtr]::Zero) { $dialogHandle = $automationWindowHandle }
    if ($dialogHandle -eq [IntPtr]::Zero) { throw 'The native file picker window handle is missing.' }
    $activated = [CodevCommonDialog]::ActivateWindow($dialogHandle)
    Start-Sleep -Milliseconds 150
    [CodevCommonDialog]::ClickAt([int]($bounds.Left + $bounds.Width / 2), [int]($bounds.Top + $bounds.Height / 2))
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait($Path)
    if ($env:GITHUB_ACTIONS -eq 'true') {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        $foregroundHandle = [CodevCommonDialog]::GetForegroundWindow()
        Write-Host "Native picker activation: success=$activated target-hwnd=$($dialogHandle.ToInt64()) ui-hwnd=$($automationWindowHandle.ToInt64()) dialog=$([CodevCommonDialog]::GetWindowTextValue($dialogHandle))/$([CodevCommonDialog]::GetClassNameValue($dialogHandle)) foreground-hwnd=$($foregroundHandle.ToInt64()) foreground=$([CodevCommonDialog]::GetWindowTextValue($foregroundHandle))/$([CodevCommonDialog]::GetClassNameValue($foregroundHandle)); filename-hwnd=$($fileName.Current.NativeWindowHandle) focused=$($focused.Current.AutomationId -eq $fileName.Current.AutomationId)"
        $controls = $Dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($control in $controls) {
            if ($control.Current.ClassName -notin @('Edit', 'Button', 'ComboBox', 'ComboBoxEx32')) { continue }
            $controlBounds = $control.Current.BoundingRectangle
            if ($controlBounds.IsEmpty) { continue }
            $safeName = if ($control.Current.Name -in @('Save', 'Open', 'Cancel', 'File name:')) { $control.Current.Name } else { '' }
            Write-Host "Native picker control: name='$safeName' id='$($control.Current.AutomationId)' class='$($control.Current.ClassName)' type='$($control.Current.ControlType.ProgrammaticName)' hwnd=$($control.Current.NativeWindowHandle) bounds=$([int]$controlBounds.Left),$([int]$controlBounds.Top),$([int]$controlBounds.Width),$([int]$controlBounds.Height)"
        }
    }
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($focused.Current.AutomationId -ne $fileName.Current.AutomationId) {
        throw 'Keyboard focus did not move to the native file picker filename field.'
    }
}

function Invoke-BackupDialogButton($Dialog, [string]$Name) {
    if ($Name -notin @('Save', 'Open')) { throw "Unsupported native file picker action '$Name'." }
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
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
using System.Text;

public static class CodevCommonDialog
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    public static extern IntPtr FindWindowByTitle(string className, string windowTitle);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

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

    public static bool ActivateWindow(IntPtr hWnd)
    {
        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(hWnd, IntPtr.Zero);
        if (targetThread == 0) return false;
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        if (foregroundThread != 0 && foregroundThread != currentThread)
            AttachThreadInput(currentThread, foregroundThread, true);
        var attachedTarget = targetThread != currentThread && targetThread != foregroundThread &&
            AttachThreadInput(currentThread, targetThread, true);
        try
        {
            ShowWindow(hWnd, 9);
            BringWindowToTop(hWnd);
            SetActiveWindow(hWnd);
            SetForegroundWindow(hWnd);
            SetFocus(hWnd);
            return GetForegroundWindow() == hWnd;
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            if (foregroundThread != 0 && foregroundThread != currentThread)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    public static string GetWindowTextValue(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }

    public static string GetClassNameValue(IntPtr hWnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hWnd, text, text.Capacity);
        return text.ToString();
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
    Set-PermissionMode $window $modeButton 'Ask every time' 'Ask every time ▾' 'AskEveryTime'
    Set-PermissionMode $window $modeButton 'Auto · approve unless denied' 'Auto ▾' 'Auto'

    if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(10000)) {
        throw 'The packaged app did not close cleanly after changing the permission mode.'
    }
    $app = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $restartDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $windowHandle = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $restartDeadline) {
        $app.Refresh()
        if ($app.HasExited) {
            Get-Content $stdoutPath, $stderrPath -ErrorAction SilentlyContinue
            throw "Avalonia exited before restoring its saved mode (exit $($app.ExitCode))."
        }
        $windowHandle = $app.MainWindowHandle
        if ($windowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($windowHandle -eq [IntPtr]::Zero) { throw 'Avalonia did not reopen after the saved-mode check.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    $modeButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $modeCondition)
    $settingsPath = Join-Path $dataRoot 'Codev\avalonia-settings.json'
    $savedSettings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    if ($null -eq $modeButton -or $modeButton.Current.Name -ne 'Auto ▾' -or
        [string]$savedSettings.DefaultProjectCommandPermissionMode -ne 'Auto') {
        throw "The fresh app process did not restore Auto mode (label='$($modeButton.Current.Name)', setting='$($savedSettings.DefaultProjectCommandPermissionMode)')."
    }

    if ($env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_ENVIRONMENT -eq 'github-hosted') {
        $smokeSucceeded = $true
        Write-Host 'Packaged app and Auto mode-change/restart persistence smoke passed. Native Save/Open dialogs are skipped on GitHub-hosted runners because their desktop does not expose them as an activatable foreground window; the native round-trip passed locally on Windows 11.'
        return
    }

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
    Write-Host "Packaged Codev window opened with $($edits.Count) editable text control(s), an accessible send button, and the Auto footer selector. Auto/Ask changes survived restart; native Save/Open dialogs round-tripped the conversation backup in an isolated profile."
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
