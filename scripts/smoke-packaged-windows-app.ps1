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

function Wait-ForTopLevelWindow([string]$Name, [int]$TimeoutSeconds = 5) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name))
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $candidate = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $candidate) { return $candidate }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Test-NewConversationShortcut($Window, [string]$DataRoot) {
    $activeConversationPath = Join-Path $DataRoot 'Codev\avalonia-active-conversation.json'
    if (-not (Test-Path -LiteralPath $activeConversationPath -PathType Leaf)) {
        throw 'The isolated profile did not persist an active conversation before the Ctrl+N check.'
    }
    $previousConversationId = Get-Content -LiteralPath $activeConversationPath -Raw | ConvertFrom-Json
    $composerCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Message Codev'))
    $composer = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $composerCondition)
    if ($null -eq $composer) { throw 'The named composer is missing for the Ctrl+N check.' }
    $composer.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^n')
    $newConversationDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $currentConversationId = Get-Content -LiteralPath $activeConversationPath -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($null -ne $currentConversationId -and $currentConversationId -ne $previousConversationId) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $newConversationDeadline)
    throw 'Ctrl+N did not create and activate a new conversation.'
}

$script:AgentProfileSmokeConversationId = $null

function Select-AgentProfileInPackagedApp($Window, [string]$DataRoot, [string]$ConversationId) {
    $pickerCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ComboBox),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Primary agent profile'))
    $picker = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $pickerCondition)
    if ($null -eq $picker -or -not $picker.Current.IsEnabled) {
        throw 'The primary agent profile picker is missing or disabled in Code task mode.'
    }
    $expandPattern = $null
    if (-not $picker.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expandPattern)) {
        throw 'The primary agent profile picker does not expose its popup to UI Automation.'
    }
    $expandPattern.Expand()
    Start-Sleep -Milliseconds 200

    $candidate = $null
    $elements = $null
    $pickerDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        for ($index = 0; $index -lt $elements.Count; $index++) {
            $element = $elements.Item($index)
            $name = [string]$element.Current.Name
            if ($name -notin @('Smoke QA', 'Smoke QA agent') -and -not $name.StartsWith('Smoke QA · ', [StringComparison]::Ordinal)) { continue }
            $selectionItem = $null
            if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selectionItem)) {
                $candidate = $selectionItem
                break
            }
        }
        if ($null -eq $candidate) { Start-Sleep -Milliseconds 100 }
    } while ($null -eq $candidate -and [DateTime]::UtcNow -lt $pickerDeadline)
    if ($null -eq $candidate) {
        $visibleItems = @()
        for ($index = 0; $index -lt $elements.Count; $index++) {
            $element = $elements.Item($index)
            if ($element.Current.ControlType -in @([System.Windows.Automation.ControlType]::ListItem, [System.Windows.Automation.ControlType]::DataItem)) {
                $visibleItems += [string]$element.Current.Name
            }
        }
        throw "The expanded agent profile picker did not expose the Smoke QA selection. Visible profile items: $($visibleItems -join ', ')"
    }
    $candidate.Select()

    $conversationPath = Join-Path $DataRoot 'Codev\avalonia-conversations.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        try {
            $saved = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $conversation = @($saved | Where-Object { [string]$_.Id -eq $ConversationId })
            if ($conversation.Count -eq 1 -and [string]$conversation[0].AgentProfileName -eq 'Smoke QA') {
                $script:AgentProfileSmokeConversationId = $ConversationId
                return
            }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Selecting Smoke QA in the packaged primary-agent picker did not persist to the active conversation.'
}

function Test-ConversationModeShortcut($Window, [string]$DataRoot) {
    $conversationPath = Join-Path $DataRoot 'Codev\avalonia-conversations.json'
    $activePath = Join-Path $DataRoot 'Codev\avalonia-active-conversation.json'
    $conversationId = [string](Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json)
    $readActiveConversation = {
        $saved = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
        return @($saved | Where-Object { [string]$_.Id -eq $conversationId })[0]
    }
    $conversation = & $readActiveConversation
    if ($null -eq $conversation -or $conversation.IsPlanMode -or $conversation.IsCodeTask) {
        throw 'The mode-cycle shortcut smoke requires the active conversation to start in Chat mode.'
    }

    $planButtonCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Chat mode'))
    $chatButton = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $planButtonCondition)
    if ($null -eq $chatButton) { throw 'The visible conversation mode button did not identify Chat mode.' }
    $composer = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'ComposerTextBox'))
    if ($null -eq $composer) { throw 'The named composer is missing for the mode-cycle shortcut check.' }
    $composer.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^+m')

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $conversation = & $readActiveConversation
        if ($null -ne $conversation -and $conversation.IsPlanMode -and -not $conversation.IsCodeTask) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $conversation -or -not $conversation.IsPlanMode -or $conversation.IsCodeTask) {
        throw 'Ctrl+Shift+M did not switch the active conversation from Chat to Plan.'
    }

    $planButtonCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Plan mode'))
    if ($null -eq $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $planButtonCondition)) {
        throw 'The mode control did not visibly update to Plan mode after Ctrl+Shift+M.'
    }

    $codeTaskButton = $null
    foreach ($label in @('Code task unavailable', 'Enable Code task', 'Code task on')) {
        $codeTaskButton = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button),
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::NameProperty,
                    $label)))
        if ($null -ne $codeTaskButton) { break }
    }
    if ($null -eq $codeTaskButton) { throw 'The Code task mode control is missing during mode-cycle smoke.' }
    if ($codeTaskButton.Current.Name -eq 'Code task unavailable') {
        $composer.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('^+m')
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $conversation = & $readActiveConversation
            if ($null -ne $conversation -and -not $conversation.IsPlanMode -and -not $conversation.IsCodeTask) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $conversation -or $conversation.IsPlanMode -or $conversation.IsCodeTask) {
            throw 'When Code task is unavailable, Ctrl+Shift+M did not cycle Plan back to Chat.'
        }
        return 'Chat → Plan → Chat (Code task unavailable)'
    }

    if ([string]$conversation.Provider -eq 'ollama') {
        $composer.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('^+m')
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        do {
            $conversation = & $readActiveConversation
            if ($null -ne $conversation -and $conversation.IsCodeTask -and -not $conversation.IsPlanMode) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $conversation -or -not $conversation.IsCodeTask -or $conversation.IsPlanMode) {
            throw 'Ctrl+Shift+M did not switch Plan into an eligible local Ollama Code task.'
        }
        Select-AgentProfileInPackagedApp $Window $DataRoot $conversationId
        $composer.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('^+m')
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $conversation = & $readActiveConversation
            if ($null -ne $conversation -and -not $conversation.IsPlanMode -and -not $conversation.IsCodeTask) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $conversation -or $conversation.IsPlanMode -or $conversation.IsCodeTask) {
            throw 'Ctrl+Shift+M did not cycle an eligible local Code task back to Chat.'
        }
        return 'Chat → Plan → local Code task → Chat'
    }

    # Entering an eligible hosted Code task can request consent and is not suitable
    # for an unattended smoke. Restore Chat through the visible Plan control.
    $planModeButton = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $planButtonCondition)
    $planInvoke = $null
    if ($null -eq $planModeButton -or -not $planModeButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$planInvoke)) {
        throw 'Could not restore Chat mode after checking the eligible Code task branch.'
    }
    $planInvoke.Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $conversation = & $readActiveConversation
        if ($null -ne $conversation -and -not $conversation.IsPlanMode -and -not $conversation.IsCodeTask) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $conversation -or $conversation.IsPlanMode -or $conversation.IsCodeTask) {
        throw 'Could not restore Chat mode after the mode-cycle shortcut smoke.'
    }
    return 'Chat → Plan; Code task eligible (hosted-consent branch intentionally not invoked)'
}

function Test-ConversationRename($Window, [string]$DataRoot) {
    $activeConversationPath = Join-Path $DataRoot 'Codev\avalonia-active-conversation.json'
    $conversationPath = Join-Path $DataRoot 'Codev\avalonia-conversations.json'
    $conversationId = [string](Get-Content -LiteralPath $activeConversationPath -Raw | ConvertFrom-Json)
    $rowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'New conversation'))
    $row = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
    if ($null -eq $row) { throw 'The active disposable conversation is missing from the sidebar.' }
    $row.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')

    $renameCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Rename…'))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $renameItem = $null
    do {
        $renameItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $renameCondition)
        if ($null -ne $renameItem) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $renameItem) { throw 'The conversation context menu did not expose Rename….' }
    $renameItem.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')

    $dialog = Wait-ForTopLevelWindow 'Rename conversation'
    if ($null -eq $dialog) { throw 'Choosing Rename… did not open its dialog.' }
    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edit = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    if ($null -eq $edit) { throw 'The Rename conversation dialog has no editable name field.' }
    $valuePattern = $null
    if (-not $edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        throw 'The rename field does not expose its editable value to UI Automation.'
    }
    $renamedTitle = 'Codev UI smoke renamed'
    $valuePattern.SetValue($renamedTitle)

    $saveCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Save name'))
    $save = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $saveCondition)
    if ($null -eq $save) { throw 'The Rename conversation dialog has no Save name action.' }
    $saveInvoke = $null
    if (-not $save.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$saveInvoke)) {
        throw 'The Save name action is not invokable through UI Automation.'
    }
    $saveInvoke.Invoke()

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try {
            $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $renamed = @($conversations | Where-Object { [string]$_.Id -eq $conversationId -and [string]$_.Title -eq $renamedTitle })
            if ($renamed.Count -eq 1) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The renamed title was not persisted for the active conversation.'
}

function Test-ConversationArchiveRestore($Window, [string]$DataRoot) {
    $conversationId = [string](Get-Content -LiteralPath (Join-Path $DataRoot 'Codev\avalonia-active-conversation.json') -Raw | ConvertFrom-Json)
    $conversationPath = Join-Path $DataRoot 'Codev\avalonia-conversations.json'
    $title = 'New conversation'
    $rowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $title))
    $row = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
    if ($null -eq $row) { throw 'The renamed conversation is missing from the sidebar before archive.' }
    $row.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    $archiveCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Archive / restore'))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $archiveItem = $null
    do {
        $archiveItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $archiveCondition)
        if ($null -ne $archiveItem) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $archiveItem) { throw 'The conversation context menu did not expose Archive / restore.' }
    $archiveInvoke = $null
    if ($archiveItem.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$archiveInvoke)) {
        $archiveInvoke.Invoke()
    }
    else {
        $archiveItem.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $archived = $false
    do {
        try {
            $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $target = @($conversations | Where-Object { [string]$_.Id -eq $conversationId })
            $archived = $target.Count -eq 1 -and [bool]$target[0].IsArchived
        }
        catch { }
        if ($archived) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $archived) { throw 'Archiving the disposable conversation did not persist its archived state.' }
    $activeAfterArchive = [string](Get-Content -LiteralPath (Join-Path $DataRoot 'Codev\avalonia-active-conversation.json') -Raw | ConvertFrom-Json)
    if ($activeAfterArchive -eq $conversationId) { throw 'Archiving the active conversation did not switch to another available conversation.' }

    $buttonsCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
    $archiveView = $null
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        if ($buttons.Item($index).Current.Name -like '*Show archived*') { $archiveView = $buttons.Item($index); break }
    }
    if ($null -eq $archiveView) { throw 'The sidebar does not expose its Show archived action after archiving.' }
    $archiveViewInvoke = $null
    if (-not $archiveView.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$archiveViewInvoke)) {
        throw 'The Show archived action cannot be opened through UI Automation.'
    }
    $archiveViewInvoke.Invoke()

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $row = $null
    do {
        $row = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
        if ($null -ne $row) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $row) { throw 'The archived conversation did not appear in the archived sidebar view.' }
    $row.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $archiveItem = $null
    do {
        $archiveItem = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $archiveCondition)
        if ($null -ne $archiveItem) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $archiveItem) { throw 'The archived conversation context menu did not expose Archive / restore.' }
    $archiveInvoke = $null
    if ($archiveItem.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$archiveInvoke)) {
        $archiveInvoke.Invoke()
    }
    else {
        $archiveItem.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try {
            $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $target = @($conversations | Where-Object { [string]$_.Id -eq $conversationId })
            $activeId = [string](Get-Content -LiteralPath (Join-Path $DataRoot 'Codev\avalonia-active-conversation.json') -Raw | ConvertFrom-Json)
            $active = @($conversations | Where-Object { [string]$_.Id -eq $activeId })
            if ($target.Count -eq 1 -and -not [bool]$target[0].IsArchived -and
                $active.Count -eq 1 -and -not [bool]$active[0].IsArchived) { return }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Restoring the archived conversation did not persist its active state.'
}

function Find-ConversationMenuItem($Window, [string]$Title, [string]$MenuName) {
    $rowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Title))
    $row = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
    if ($null -eq $row) { throw "Conversation '$Title' is missing from the sidebar." }
    $row.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('+{F10}')
    $menuCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::MenuItem),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $MenuName))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $item = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $menuCondition)
        if ($null -ne $item) { return $item }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The context menu for '$Title' did not expose '$MenuName'."
}

function Invoke-AccessibleMenuItem($Item) {
    $invoke = $null
    if ($Item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invoke)) {
        $invoke.Invoke()
    }
    else {
        $Item.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    }
}

function Test-PinnedConversationSearchArchiveRestore($Window, [string]$DataRoot) {
    $activePath = Join-Path $DataRoot 'Codev\avalonia-active-conversation.json'
    $conversationPath = Join-Path $DataRoot 'Codev\avalonia-conversations.json'
    $conversationId = [string](Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json)
    $title = 'New conversation'
    $moreButton = Find-ByAutomationId $Window 'MoreButton'
    if ($null -eq $moreButton) { throw 'The conversation More menu is missing.' }
    $moreInvoke = $null
    if (-not $moreButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$moreInvoke)) {
        throw 'The conversation More menu cannot be opened through UI Automation.'
    }
    $moreInvoke.Invoke()
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $pinItem = $null
    do {
        $menuItems = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::MenuItem))
        for ($index = 0; $index -lt $menuItems.Count; $index++) {
            $name = $menuItems.Item($index).Current.Name
            if ($name -like '*Pin*' -and $name -notlike '*Pinned*') { $pinItem = $menuItems.Item($index); break }
        }
        if ($null -ne $pinItem) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $pinItem) { throw 'The conversation More menu did not expose Pin.' }
    Invoke-AccessibleMenuItem $pinItem

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $pinned = $false
    do {
        try {
            $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $target = @($conversations | Where-Object { [string]$_.Id -eq $conversationId })
            $pinned = $target.Count -eq 1 -and [bool]$target[0].IsPinned
        }
        catch { }
        if ($pinned) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $pinned) { throw 'Pin did not persist for the active conversation.' }

    $searchCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Search conversations'))
    $search = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $searchCondition)
    if ($null -eq $search) { throw 'The named conversation search field is missing for the pinned-chat check.' }
    $searchValue = $null
    if (-not $search.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$searchValue)) {
        throw 'Conversation search does not expose an editable value to UI Automation.'
    }
    $searchValue.SetValue($title)
    $rowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $title))
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $row = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
        if ($null -ne $row) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $row) { throw 'Searching did not find the pinned conversation.' }

    $archiveItem = Find-ConversationMenuItem $Window $title 'Archive / restore'
    Invoke-AccessibleMenuItem $archiveItem
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    $archived = $false
    do {
        try {
            $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $target = @($conversations | Where-Object { [string]$_.Id -eq $conversationId })
            $archived = $target.Count -eq 1 -and [bool]$target[0].IsArchived -and [bool]$target[0].IsPinned
        }
        catch { }
        if ($archived) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $archived) { throw 'Archiving the searched pinned conversation did not persist both states.' }
    $activeAfterArchive = [string](Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json)
    if ($activeAfterArchive -eq $conversationId) { throw 'Archiving the searched active conversation did not select another chat.' }

    $buttonsCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
    $archiveView = $null
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        if ($buttons.Item($index).Current.Name -like '*Show archived*') { $archiveView = $buttons.Item($index); break }
    }
    if ($null -eq $archiveView) { throw 'The sidebar does not expose Show archived after archiving a pinned conversation.' }
    $archiveViewInvoke = $null
    if (-not $archiveView.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$archiveViewInvoke)) {
        throw 'Show archived cannot be opened through UI Automation.'
    }
    $archiveViewInvoke.Invoke()
    $archivedRowDeadline = [DateTime]::UtcNow.AddSeconds(5)
    $archivedRow = $null
    do {
        $archivedRow = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
        if ($null -ne $archivedRow) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $archivedRowDeadline)
    if ($null -eq $archivedRow) { throw 'The pinned conversation did not appear in the archived view before its restore check.' }
    $archiveItem = Find-ConversationMenuItem $Window $title 'Archive / restore'
    Invoke-AccessibleMenuItem $archiveItem

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        try {
            $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $target = @($conversations | Where-Object { [string]$_.Id -eq $conversationId })
            if ($target.Count -eq 1 -and [bool]$target[0].IsPinned -and -not [bool]$target[0].IsArchived) { break }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $target -or $target.Count -ne 1 -or -not [bool]$target[0].IsPinned -or [bool]$target[0].IsArchived) {
        throw 'Restoring the searched pinned conversation did not persist its pin and active state.'
    }

    $buttons = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
    $archiveView = $null
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        if ($buttons.Item($index).Current.Name -like '*Show recent*') { $archiveView = $buttons.Item($index); break }
    }
    if ($null -eq $archiveView) { throw 'The sidebar does not expose Show recent after restoring the pinned conversation.' }
    $archiveViewInvoke = $null
    if (-not $archiveView.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$archiveViewInvoke)) {
        throw 'Show recent cannot be opened through UI Automation.'
    }
    $archiveViewInvoke.Invoke()
    $searchValue.SetValue('')
}

function Test-PermanentConversationDelete($Window, [string]$DataRoot) {
    $activePath = Join-Path $DataRoot 'Codev\avalonia-active-conversation.json'
    $conversationPath = Join-Path $DataRoot 'Codev\avalonia-conversations.json'
    $archiveView = $null
    $buttonsCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonsCondition)
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        if ($buttons.Item($index).Current.Name -like '*Show recent*') { $archiveView = $buttons.Item($index); break }
    }
    if ($null -ne $archiveView) {
        $archiveViewInvoke = $null
        if (-not $archiveView.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$archiveViewInvoke)) {
            throw 'Show recent cannot be opened before deletion.'
        }
        $archiveViewInvoke.Invoke()
    }
    $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
    $target = @($conversations | Where-Object { [string]$_.Title -eq 'New conversation' })
    if ($target.Count -ne 1) {
        throw "Expected one disposable 'New conversation' before permanent deletion; found $($target.Count)."
    }
    $conversationId = [string]$target[0].Id
    $title = [string]$target[0].Title
    $rowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $title))
    $row = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
    if ($null -eq $row) { throw 'The disposable conversation to delete is missing from Recents.' }
    $rowInvoke = $null
    if (-not $row.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$rowInvoke)) {
        throw 'The disposable conversation row cannot be selected through UI Automation.'
    }
    $rowInvoke.Invoke()
    $selectDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $activeId = [string](Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json)
        if ($activeId -eq $conversationId) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $selectDeadline)
    if ($activeId -ne $conversationId) { throw 'Selecting the disposable chat did not activate it before deletion.' }
    $deleteItem = Find-ConversationMenuItem $Window $title 'Delete permanently…'
    Invoke-AccessibleMenuItem $deleteItem
    $dialog = Wait-ForTopLevelWindow 'Delete conversation?'
    if ($null -eq $dialog) { throw 'Choosing Delete permanently… did not show its confirmation.' }
    $confirmCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Delete conversation'))
    $confirm = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $confirmCondition)
    if ($null -eq $confirm) { throw 'The permanent-delete confirmation has no Delete conversation action.' }
    $confirmInvoke = $null
    if (-not $confirm.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$confirmInvoke)) {
        throw 'The permanent-delete confirmation cannot be activated through UI Automation.'
    }
    $confirmInvoke.Invoke()

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $conversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
        $remaining = @($conversations | Where-Object { [string]$_.Id -eq $conversationId })
        $activeAfterDelete = [string](Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json)
        if ($remaining.Count -eq 0 -and $activeAfterDelete -ne $conversationId) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($remaining.Count -ne 0 -or $activeAfterDelete -eq $conversationId) {
        throw 'Permanent deletion did not remove the conversation and activate another chat.'
    }
    $searchCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Search conversations'))
    $search = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $searchCondition)
    if ($null -eq $search) { throw 'The named conversation search field is missing after deletion.' }
    $searchValue = $null
    if (-not $search.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$searchValue)) {
        throw 'Conversation search does not expose an editable value after deletion.'
    }
    $searchValue.SetValue($title)
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $row = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $rowCondition)
        if ($null -eq $row) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -ne $row) { throw 'The permanently deleted conversation still appears in search results.' }
    $searchValue.SetValue('')
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
    $agentProfileDirectory = Join-Path $dataRoot 'Codev\agents'
    $null = New-Item -ItemType Directory -Path $agentProfileDirectory -Force
    [System.IO.File]::WriteAllText(
        (Join-Path $agentProfileDirectory 'smoke-qa.md'),
        "---`nname: Smoke QA`ndescription: Inspect the disposable UI smoke conversation.`n---`nUse only the isolated smoke data; report observations without editing files.`n",
        [System.Text.UTF8Encoding]::new($false))
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
    $workingArea = [System.Windows.Forms.Screen]::FromHandle($windowHandle).WorkingArea
    $layoutDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $windowBounds = $window.Current.BoundingRectangle
        if ($windowBounds.Left -ge $workingArea.Left -and $windowBounds.Top -ge $workingArea.Top -and
            $windowBounds.Right -le $workingArea.Right -and $windowBounds.Bottom -le $workingArea.Bottom) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $layoutDeadline)
    if ($windowBounds.Left -lt $workingArea.Left -or $windowBounds.Top -lt $workingArea.Top -or
        $windowBounds.Right -gt $workingArea.Right -or $windowBounds.Bottom -gt $workingArea.Bottom) {
        throw "The packaged main window extends outside the monitor work area (window=$([int]$windowBounds.Left),$([int]$windowBounds.Top),$([int]$windowBounds.Width),$([int]$windowBounds.Height); workArea=$($workingArea.X),$($workingArea.Y),$($workingArea.Width),$($workingArea.Height))."
    }

    $editCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCondition)
    if ($edits.Count -lt 1) { throw 'The packaged main window exposes no editable text control.' }
    foreach ($edit in $edits) {
        if ([string]::IsNullOrWhiteSpace($edit.Current.Name)) {
            throw "A packaged-app text field has no accessible name (automation id '$($edit.Current.AutomationId)')."
        }
    }
    $comboBoxCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)
    $comboBoxes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $comboBoxCondition)
    foreach ($comboBox in $comboBoxes) {
        if ([string]::IsNullOrWhiteSpace($comboBox.Current.Name)) {
            throw "A packaged-app selector has no accessible name (automation id '$($comboBox.Current.AutomationId)')."
        }
    }

    $composerCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Message Codev'))
    $composer = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $composerCondition)
    if ($null -eq $composer) { throw 'The named message composer is missing from the keyboard focus order.' }
    # SetForegroundWindow can report failure when Windows' foreground lock
    # prevents an automation host from activating a newly launched process.
    # Verify the actual focused element below instead of treating that return
    # value as proof that focus failed.
    [void][CodevCommonDialog]::ActivateWindow($windowHandle)
    $composer.SetFocus()
    $focusDeadline = [DateTime]::UtcNow.AddSeconds(2)
    $focused = $null
    do {
        $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -ne $focused -and $focused.Current.ProcessId -eq $app.Id) { break }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $focusDeadline)
    if ($null -eq $focused -or $focused.Current.ProcessId -ne $app.Id) {
        $focusName = if ($null -eq $focused) { '<none>' } else { $focused.Current.Name }
        $focusProcessId = if ($null -eq $focused) { 0 } else { $focused.Current.ProcessId }
        throw "The composer did not take keyboard focus in the packaged Codev process (focused='$focusName', process=$focusProcessId)."
    }
    $visitedTabNames = @{}
    for ($tab = 0; $tab -lt 40; $tab++) {
        [System.Windows.Forms.SendKeys]::SendWait('{TAB}')
        $focusDeadline = [DateTime]::UtcNow.AddMilliseconds(500)
        do {
            Start-Sleep -Milliseconds 25
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            if ($null -ne $focused -and $focused.Current.ProcessId -eq $app.Id) { break }
        } while ([DateTime]::UtcNow -lt $focusDeadline)
        if ($null -eq $focused -or $focused.Current.ProcessId -ne $app.Id) {
            $focusName = if ($null -eq $focused) { '<none>' } else { $focused.Current.Name }
            $focusProcessId = if ($null -eq $focused) { 0 } else { $focused.Current.ProcessId }
            throw "Tab traversal left the packaged Codev process (focused='$focusName', process=$focusProcessId, tab=$($tab + 1))."
        }
        if (-not [string]::IsNullOrWhiteSpace($focused.Current.Name)) {
            $visitedTabNames[$focused.Current.Name] = $true
        }
    }
    foreach ($requiredTabName in @('Search conversations', 'Message Codev', 'Response style')) {
        if (-not $visitedTabNames.ContainsKey($requiredTabName)) {
            throw "Tab traversal did not reach the accessible control '$requiredTabName'."
        }
    }

    # Exercise the shortcuts against the packaged native window, not only the
    # Avalonia headless event handler. The app runs against an isolated profile.
    [void][CodevCommonDialog]::ActivateWindow($windowHandle)
    $composer.SetFocus()
    Start-Sleep -Milliseconds 100
    [System.Windows.Forms.SendKeys]::SendWait('{F1}')
    $shortcutsWindow = Wait-ForTopLevelWindow 'Keyboard shortcuts'
    if ($null -eq $shortcutsWindow) { throw 'F1 did not open the keyboard shortcuts reference.' }
    $shortcutText = $shortcutsWindow.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $shortcutNames = @($shortcutsWindow.Current.Name)
    for ($index = 0; $index -lt $shortcutText.Count; $index++) {
        $shortcutNames += $shortcutText.Item($index).Current.Name
    }
    foreach ($requiredShortcut in @('Ctrl+N', 'Ctrl+F', 'Ctrl+L', 'Ctrl+Shift+M', '/status')) {
        if (-not ($shortcutNames -contains $requiredShortcut) -and
            -not (($shortcutNames -join "`n").Contains($requiredShortcut, [StringComparison]::Ordinal))) {
            throw "The keyboard shortcuts reference does not list '$requiredShortcut'."
        }
    }
    $shortcutsHandle = [IntPtr]$shortcutsWindow.Current.NativeWindowHandle
    if ($shortcutsHandle -eq [IntPtr]::Zero -or -not [CodevCommonDialog]::ActivateWindow($shortcutsHandle)) {
        throw 'The keyboard shortcuts reference could not be activated for its Escape check.'
    }
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    $closeDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $shortcutsWindow = Wait-ForTopLevelWindow 'Keyboard shortcuts' 1
        if ($null -eq $shortcutsWindow) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $closeDeadline)
    if ($null -ne $shortcutsWindow) { throw 'Escape did not close the keyboard shortcuts reference.' }

    $searchCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Search conversations'))
    $search = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $searchCondition)
    if ($null -eq $search) { throw 'The named conversation search field is missing.' }
    $composer.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^f')
    Start-Sleep -Milliseconds 100
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($focused.Current.Name -ne 'Search conversations') { throw 'Ctrl+F did not focus conversation search.' }
    [System.Windows.Forms.SendKeys]::SendWait('^l')
    Start-Sleep -Milliseconds 100
    $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
    if ($focused.Current.Name -ne 'Message Codev') { throw 'Ctrl+L did not focus the composer.' }
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

    $composerValue = $null
    if (-not $composer.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$composerValue)) {
        throw 'The composer does not expose its editable value to UI Automation.'
    }
    $composer.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('codev-shift-enter-first')
    [System.Windows.Forms.SendKeys]::SendWait('+{ENTER}')
    [System.Windows.Forms.SendKeys]::SendWait('codev-shift-enter-second')
    $newlineDraft = ([string]$composerValue.Current.Value).Replace("`r`n", "`n")
    if (-not $newlineDraft.Contains("codev-shift-enter-first`ncodev-shift-enter-second", [StringComparison]::Ordinal)) {
        throw 'Shift+Enter did not preserve both draft lines in the composer.'
    }
    [System.Windows.Forms.SendKeys]::SendWait('^a')
    [System.Windows.Forms.SendKeys]::SendWait('{BACKSPACE}')
    if (-not [string]::IsNullOrEmpty([string]$composerValue.Current.Value)) {
        throw 'The disposable keyboard-smoke draft did not clear before the slash command check.'
    }

    # /status is an in-app command and must not depend on a reachable model.
    # Exercise it through the real composer and verify its saved transcript.
    $conversationPath = Join-Path $dataRoot 'Codev\avalonia-conversations.json'
    $activePath = Join-Path $dataRoot 'Codev\avalonia-active-conversation.json'
    $statusActiveId = [string](Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json)
    $savedConversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
    $statusConversation = @($savedConversations | Where-Object { [string]$_.Id -eq $statusActiveId })
    if ($statusConversation.Count -ne 1) { throw 'The active conversation is missing before the /status smoke.' }
    $statusBeforeCount = @($statusConversation[0].Messages).Count
    $composer.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('/status')
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    $statusDeadline = [DateTime]::UtcNow.AddSeconds(10)
    $statusReport = $null
    do {
        try {
            $savedConversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
            $statusConversation = @($savedConversations | Where-Object { [string]$_.Id -eq $statusActiveId })
            $statusMessages = @($statusConversation[0].Messages)
            if ($statusMessages.Count -ge $statusBeforeCount + 2 -and
                [string]$statusMessages[-2].Content -eq '/status' -and
                [string]$statusMessages[-1].Role -eq 'assistant') {
                $statusReport = [string]$statusMessages[-1].Content
                break
            }
        }
        catch { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $statusDeadline)
    if ([string]::IsNullOrWhiteSpace($statusReport) -or
        -not $statusReport.Contains('Model: Ollama (local)', [StringComparison]::Ordinal) -or
        -not $statusReport.Contains('Project command permissions:', [StringComparison]::Ordinal)) {
        throw 'Sending /status through the packaged composer did not save the expected local status report.'
    }

    $allButtonCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $allButtonCondition)
    for ($index = 0; $index -lt $buttons.Count; $index++) {
        $button = $buttons.Item($index)
        if ([string]::IsNullOrWhiteSpace($button.Current.Name)) {
            throw "A packaged-app button has no accessible name (automation id '$($button.Current.AutomationId)')."
        }
        if (-not $button.Current.IsKeyboardFocusable) {
            throw "A packaged-app button is not keyboard-focusable ('$($button.Current.Name)')."
        }
    }

    Test-ConversationRename $window $dataRoot

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
    $renamedRowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Codev UI smoke renamed'))
    if ($null -eq $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $renamedRowCondition)) {
        throw 'The renamed conversation was not visible in the sidebar after the Auto-mode restart.'
    }
    $modeCycleResult = Test-ConversationModeShortcut $window $dataRoot
    if ($env:GITHUB_ACTIONS -eq 'true' -and $env:RUNNER_ENVIRONMENT -eq 'github-hosted') {
        Test-NewConversationShortcut $window $dataRoot
        Test-PinnedConversationSearchArchiveRestore $window $dataRoot
        Test-PermanentConversationDelete $window $dataRoot
        $smokeSucceeded = $true
        Write-Host "Packaged app smoke passed with a window inside the monitor work area, $($buttons.Count) named, keyboard-focusable buttons, $($edits.Count) named text fields, $($comboBoxes.Count) named selectors, successful Tab traversal, F1/Escape shortcut-reference use, Enter/Shift+Enter composer behavior, Ctrl+N conversation creation, Ctrl+Shift+M mode cycle ($modeCycleResult), Ctrl+F/Ctrl+L focus, /status without a model request, sidebar rename/pin/search/archive/restore/permanent-delete, and Auto mode-change/restart persistence. Native Save/Open dialogs are skipped on GitHub-hosted runners because their desktop does not expose them as an activatable foreground window; the native round-trip passed locally on Windows 11."
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

    Test-NewConversationShortcut $window $dataRoot
    Test-PinnedConversationSearchArchiveRestore $window $dataRoot
    Test-PermanentConversationDelete $window $dataRoot

    if (-not $app.CloseMainWindow() -or -not $app.WaitForExit(10000)) {
        throw 'The packaged app did not close cleanly after the conversation-management smoke.'
    }
    $app = Start-Process -FilePath $appPath -WorkingDirectory (Split-Path $appPath) -PassThru `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $conversationRestartDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $windowHandle = [IntPtr]::Zero
    while ([DateTime]::UtcNow -lt $conversationRestartDeadline) {
        $app.Refresh()
        if ($app.HasExited) { throw "Avalonia exited before restoring the renamed conversation (exit $($app.ExitCode))." }
        $windowHandle = $app.MainWindowHandle
        if ($windowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($windowHandle -eq [IntPtr]::Zero) { throw 'Avalonia did not reopen after the conversation-management smoke.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    $renamedRow = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $renamedRowCondition)
    if ($null -eq $renamedRow) { throw 'The renamed conversation was not visible in the sidebar after restart.' }
    if ($null -ne $script:AgentProfileSmokeConversationId) {
        $conversationPath = Join-Path $dataRoot 'Codev\avalonia-conversations.json'
        $savedConversations = @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
        $profileConversation = @($savedConversations | Where-Object { [string]$_.Id -eq $script:AgentProfileSmokeConversationId })
        if ($profileConversation.Count -ne 1 -or [string]$profileConversation[0].AgentProfileName -ne 'Smoke QA') {
            throw 'The user-selected Smoke QA profile did not persist across the packaged app restart.'
        }
    }
    $smokeSucceeded = $true
    Write-Host "Packaged Codev window opened inside the monitor work area with $($edits.Count) named editable text control(s), $($comboBoxes.Count) named selectors, $($buttons.Count) named keyboard-focusable buttons, successful Tab traversal, F1/Escape shortcut-reference use, Enter/Shift+Enter composer behavior, Ctrl+N conversation creation, Ctrl+Shift+M mode cycle ($modeCycleResult), Ctrl+F/Ctrl+L focus, /status without a model request, and persisted sidebar rename/pin/search/archive/restore/permanent-delete actions including rename restoration after restart. Auto/Ask changes survived restart; native Save/Open dialogs round-tripped the conversation backup in an isolated profile."
    if ($null -ne $script:AgentProfileSmokeConversationId) { Write-Host 'The Smoke QA profile was selected through the native Code task picker and persisted across app restarts.' }
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
