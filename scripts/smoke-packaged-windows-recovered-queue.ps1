param(
    [string]$AppPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish/win-x64/Codev.Avalonia.exe'),
    [string]$Model = 'qwen3.6:35b-a3b',
    [int]$TimeoutSeconds = 360,
    [switch]$AllowLocalDesktop
)

$ErrorActionPreference = 'Stop'

if (-not $env:GITHUB_ACTIONS -and -not $AllowLocalDesktop) {
    throw 'This packaged queue smoke takes focus and interacts with the desktop. It is disabled on local desktops by default; use -AllowLocalDesktop only for an announced release-signoff check.'
}

if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    throw "Packaged Avalonia app was not found at $AppPath."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$smokeRoot = Join-Path $env:TEMP ("Codev-Packaged-Queue-" + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $smokeRoot 'data'
$codevRoot = Join-Path $dataRoot 'Codev'
$stdoutPath = Join-Path $smokeRoot 'stdout.log'
$stderrPath = Join-Path $smokeRoot 'stderr.log'
$conversationPath = Join-Path $codevRoot 'avalonia-conversations.json'
$activePath = Join-Path $codevRoot 'avalonia-active-conversation.json'
$previousDataRoot = $env:CODEV_DATA_ROOT
$process = $null
$smokeSucceeded = $false
$conversationAId = [guid]::NewGuid().ToString()
$conversationATitle = 'Recovered queue A'
$expectedFirst = 'queue smoke A first done.'
$expectedFollowUp = 'queue smoke A follow-up done.'
$expectedFresh = 'queue smoke B done.'
$firstPrompt = "Reply with exactly: $expectedFirst"
$followUpPrompt = "Reply with exactly: $expectedFollowUp"
$freshPrompt = "Reply with exactly: $expectedFresh"
$enqueuedAt = [DateTimeOffset]::UtcNow.ToString('o')

function Get-SavedConversations {
    if (-not (Test-Path -LiteralPath $conversationPath -PathType Leaf)) { return @() }
    return @(Get-Content -LiteralPath $conversationPath -Raw | ConvertFrom-Json)
}

function Get-SavedConversation([string]$Id) {
    return @(Get-SavedConversations | Where-Object { [string]$_.Id -eq $Id })
}

function Get-ActiveConversationId {
    if (-not (Test-Path -LiteralPath $activePath -PathType Leaf)) { return '' }
    return [string](Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json)
}

function Read-Log([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return Get-Content -LiteralPath $Path -Raw }
    return ''
}

function Find-Element($Root, [System.Windows.Automation.ControlType]$ControlType, [string]$Name) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            $ControlType),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name))
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-ForElement($Window, [System.Windows.Automation.ControlType]$ControlType, [string]$Name, [int]$Timeout = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        $element = Find-Element $Window $ControlType $Name
        if ($null -ne $element -and -not $element.Current.IsOffscreen) { return $element }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Wait-ForConversation([string]$Id, [scriptblock]$Predicate, [string]$Failure, [int]$Timeout = $TimeoutSeconds) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        $matches = Get-SavedConversation $Id
        if ($matches.Count -eq 1 -and (& $Predicate $matches[0])) { return $matches[0] }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    $last = Get-SavedConversation $Id
    $lastJson = if ($last.Count -eq 1) { ConvertTo-Json -InputObject $last[0] -Depth 12 -Compress } else { '<conversation missing>' }
    throw "$Failure Last saved state: $lastJson`nApp stderr: $(Read-Log $stderrPath)"
}

function Get-Composer($Window) {
    $composer = Find-Element $Window ([System.Windows.Automation.ControlType]::Edit) 'Message Codev'
    if ($null -eq $composer) { throw 'The named message composer is missing.' }
    return $composer
}

function Send-ComposerPrompt($Window, [string]$ConversationId, [string]$Prompt) {
    if ((Get-ActiveConversationId) -ne $ConversationId) {
        throw "Refusing to send a prompt because conversation '$ConversationId' is not active."
    }
    $composer = Get-Composer $Window
    $composer.SetFocus()
    $valuePattern = $null
    if (-not $composer.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$valuePattern)) {
        throw 'The packaged message composer does not expose UI Automation ValuePattern for reliable smoke input.'
    }
    $valuePattern.SetValue('')
    $valuePattern.SetValue($Prompt)
    if ($valuePattern.Current.Value -ne $Prompt) { throw 'The packaged composer did not retain the complete smoke prompt.' }

    $draftDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $saved = Get-SavedConversation $ConversationId
        if ($saved.Count -eq 1 -and [string]$saved[0].Draft -ceq $Prompt) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $draftDeadline)
    if ($saved.Count -ne 1 -or [string]$saved[0].Draft -cne $Prompt) {
        throw "The complete composer prompt was not persisted to conversation '$ConversationId' before send."
    }

    $sendButton = Find-Element $Window ([System.Windows.Automation.ControlType]::Button) 'Send or queue prompt; stop when the composer is empty'
    if ($null -eq $sendButton) { throw 'The named Send or queue prompt button is missing.' }
    $invokePattern = $null
    if (-not $sendButton.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$invokePattern)) {
        throw 'The composer send button does not expose UI Automation InvokePattern.'
    }
    if ((Get-ActiveConversationId) -ne $ConversationId) {
        throw "Refusing to invoke Send because conversation '$ConversationId' stopped being active."
    }
    $invokePattern.Invoke()
}

function Select-Conversation($Window, [string]$Title, [string]$Id) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $row = Find-Element $Window ([System.Windows.Automation.ControlType]::Button) $Title
        if ($null -ne $row -and -not $row.Current.IsOffscreen) {
            $pattern = $null
            if (-not $row.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
                throw "Conversation row '$Title' does not expose UI Automation InvokePattern."
            }
            $pattern.Invoke()
            $activeDeadline = [DateTime]::UtcNow.AddSeconds(10)
            do {
                if ((Get-ActiveConversationId) -eq $Id) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $activeDeadline)
            if ((Get-ActiveConversationId) -ne $Id) { throw "Selecting conversation '$Title' did not persist its active ID." }
            return
        }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Conversation row '$Title' was not visible in the sidebar."
}

try {
    $null = New-Item -ItemType Directory -Path $codevRoot -Force
    $conversationA = @{
        Id = $conversationAId
        Title = $conversationATitle
        Draft = ''
        Model = $Model
        Provider = 'ollama'
        NumCtx = 8192
        NumPredict = 64
        Temperature = 0.2
        IsCodeTask = $false
        IsPlanMode = $false
        Messages = @(
            @{ Role = 'user'; Content = $firstPrompt }
            @{ Role = 'assistant'; Content = 'Saved locally · select Resume saved queue to run' }
        )
        PendingTurns = @(
            @{
                AssistantIndex = 1
                Model = $Model
                NumCtx = 8192
                IsCodeTask = $false
                IsPlanMode = $false
                ProjectPath = $null
                ContextFiles = @()
                ContextExclusions = @()
                EnqueuedAt = $enqueuedAt
                Temperature = 0.2
                Provider = 'ollama'
                IncludeProjectContext = $false
                IncludeRepoMap = $false
                OutputStyle = 'Balanced'
                ThinkEnabled = $false
                NumPredict = 64
                BestOfNAttempts = 1
            }
        )
    }
    ConvertTo-Json -InputObject @($conversationA) -Depth 20 | Set-Content -LiteralPath $conversationPath -Encoding utf8
    ConvertTo-Json -InputObject $conversationAId | Set-Content -LiteralPath $activePath -Encoding utf8

    $env:CODEV_DATA_ROOT = $dataRoot
    $process = Start-Process -FilePath $AppPath -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $namedCodevCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Codev'))
    $windowCondition = [System.Windows.Automation.AndCondition]::new(
        $namedCodevCondition,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            [int]$process.Id))

    $window = $null
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Packaged app PID $($process.Id) exited at startup: $(Read-Log $stderrPath)" }
        $window = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 250 }
    } while ($null -eq $window -and [DateTime]::UtcNow -lt $windowDeadline)
    if ($null -eq $window) {
        throw "Could not find a Codev window owned by launched PID $($process.Id); refusing to attach to another Codev process."
    }
    if ([int]$window.Current.ProcessId -ne [int]$process.Id) {
        throw "UI Automation returned window PID $($window.Current.ProcessId), expected launched PID $($process.Id)."
    }

    $restoreDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ((Get-ActiveConversationId) -eq $conversationAId) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $restoreDeadline)
    if ((Get-ActiveConversationId) -ne $conversationAId) {
        throw 'The packaged app did not restore conversation A containing the saved queue.'
    }
    $resume = Wait-ForElement $window ([System.Windows.Automation.ControlType]::Button) 'Resume saved queue'
    if ($null -eq $resume -or -not $resume.Current.IsEnabled) {
        throw 'Conversation A did not expose an enabled Resume saved queue button.'
    }
    $savedStatus = Wait-ForElement $window ([System.Windows.Automation.ControlType]::Text) '1 saved · Resume to continue'
    if ($null -eq $savedStatus) { throw 'The packaged footer did not visibly distinguish conversation A recovered saved turn.' }

    # Create conversation B from A, leaving the recovered A turn paused.
    $composer = Get-Composer $window
    $composer.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('^n')
    $conversationBId = ''
    $newConversationDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $conversationBId = Get-ActiveConversationId
        if (-not [string]::IsNullOrWhiteSpace($conversationBId) -and $conversationBId -ne $conversationAId) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $newConversationDeadline)
    if ([string]::IsNullOrWhiteSpace($conversationBId) -or $conversationBId -eq $conversationAId) {
        throw 'Ctrl+N did not create a separate conversation B while A had a recovered queue.'
    }

    $bHeader = Wait-ForElement $window ([System.Windows.Automation.ControlType]::Text) 'New conversation' 10
    if ($null -eq $bHeader) { throw 'The new B conversation did not become visible before prompt input.' }
    Send-ComposerPrompt $window $conversationBId $freshPrompt

    $bDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $conversationB = @()
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Packaged app PID $($process.Id) exited while B was running: $(Read-Log $stderrPath)" }
        $conversationB = Get-SavedConversation $conversationBId
        $conversationAState = Get-SavedConversation $conversationAId
        if ($conversationAState.Count -ne 1 -or @($conversationAState[0].PendingTurns).Count -ne 1 -or
            @($conversationAState[0].Messages).Count -ne 2 -or
            [string]$conversationAState[0].Messages[1].Content -ne 'Saved locally · select Resume saved queue to run') {
            throw 'Conversation A changed while the prompt in B was running; its recovered turn must stay paused and untouched.'
        }
        if ($conversationB.Count -eq 1 -and @($conversationB[0].PendingTurns).Count -eq 0 -and
            @($conversationB[0].Messages | Where-Object { $_.Role -eq 'user' -and [string]$_.Content -ceq $freshPrompt }).Count -eq 1 -and
            @($conversationB[0].Messages | Where-Object {
                $_.Role -eq 'assistant' -and ([string]$_.Content).Contains($expectedFresh, [StringComparison]::OrdinalIgnoreCase)
            }).Count -eq 1) { break }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $bDeadline)
    if ($conversationB.Count -ne 1 -or @($conversationB[0].PendingTurns).Count -ne 0 -or
        @($conversationB[0].Messages | Where-Object { $_.Role -eq 'user' -and [string]$_.Content -ceq $freshPrompt }).Count -ne 1 -or
        @($conversationB[0].Messages | Where-Object {
            $_.Role -eq 'assistant' -and ([string]$_.Content).Contains($expectedFresh, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 1) {
        throw "The fresh prompt in B did not complete independently. Last saved B: $(if($conversationB.Count -eq 1){ConvertTo-Json -InputObject $conversationB[0] -Depth 12 -Compress}else{'<conversation missing>'})`nApp stderr: $(Read-Log $stderrPath)"
    }

    # Return to A only after B's independently persisted response is complete.
    Select-Conversation $window $conversationATitle $conversationAId
    $aHeader = Wait-ForElement $window ([System.Windows.Automation.ControlType]::Text) $conversationATitle 10
    $aFirstPromptVisible = Wait-ForElement $window ([System.Windows.Automation.ControlType]::Text) $firstPrompt 10
    if ($null -eq $aHeader -or $null -eq $aFirstPromptVisible) {
        throw 'Conversation A did not finish rendering its own header and saved prompt after selection.'
    }
    Send-ComposerPrompt $window $conversationAId $followUpPrompt

    $conversationAState = Wait-ForConversation $conversationAId {
        param($saved)
        @($saved.PendingTurns).Count -eq 2 -and @($saved.Messages).Count -eq 4 -and
            [string]$saved.Messages[2].Role -eq 'user' -and [string]$saved.Messages[2].Content -ceq $followUpPrompt -and
            [string]$saved.Messages[3].Role -eq 'assistant' -and
            [string]$saved.Messages[1].Content -eq 'Saved locally · select Resume saved queue to run' -and
            [string]$saved.Messages[3].Content -eq 'Saved locally · select Resume saved queue to run'
    } 'The follow-up did not remain queued behind conversation A recovered turn.' 15
    if ([int]$conversationAState.PendingTurns[0].AssistantIndex -ne 1 -or
        [int]$conversationAState.PendingTurns[1].AssistantIndex -ne 3) {
        throw 'Conversation A pending-turn order is not the recovered turn followed by the newly queued turn.'
    }
    $savedStatus = Wait-ForElement $window ([System.Windows.Automation.ControlType]::Text) '1 saved · Resume to continue' 10
    if ($null -eq $savedStatus) { throw 'The footer no longer exposed Resume for conversation A blocked follow-up queue.' }
    $resume = Wait-ForElement $window ([System.Windows.Automation.ControlType]::Button) 'Resume saved queue'
    if ($null -eq $resume -or -not $resume.Current.IsEnabled) { throw 'Conversation A Resume saved queue action is unavailable.' }
    $resumePattern = $null
    if (-not $resume.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$resumePattern)) {
        throw 'Resume saved queue does not expose an accessible invoke pattern.'
    }
    $resumePattern.Invoke()

    $conversationAState = Wait-ForConversation $conversationAId {
        param($saved)
        @($saved.PendingTurns).Count -eq 0 -and @($saved.Messages).Count -eq 4 -and
            [string]$saved.Messages[1].Role -eq 'assistant' -and
            ([string]$saved.Messages[1].Content).Contains($expectedFirst, [StringComparison]::OrdinalIgnoreCase) -and
            [string]$saved.Messages[2].Role -eq 'user' -and [string]$saved.Messages[2].Content -ceq $followUpPrompt -and
            [string]$saved.Messages[3].Role -eq 'assistant' -and
            ([string]$saved.Messages[3].Content).Contains($expectedFollowUp, [StringComparison]::OrdinalIgnoreCase)
    } 'Conversation A recovered turn and follow-up did not complete in order or the persisted queue did not clear.'

    $conversationB = Get-SavedConversation $conversationBId
    if ($conversationB.Count -ne 1 -or @($conversationB[0].PendingTurns).Count -ne 0 -or
        @($conversationB[0].Messages | Where-Object {
            $_.Role -eq 'assistant' -and ([string]$_.Content).Contains($expectedFresh, [StringComparison]::OrdinalIgnoreCase)
        }).Count -ne 1) {
        throw 'Conversation B lost its completed response while A resumed.'
    }

    $evidence = [ordered]@{
        AppPath = $AppPath
        AppProcessId = [int]$process.Id
        DataRoot = $dataRoot
        Assertions = @(
            'Window matched the launched process ID.'
            'Conversation B completed while conversation A retained its original one-entry recovered queue.'
            'Conversation A queued a follow-up after its recovered turn without running either turn early.'
            'Resume completed both A replies in message order and cleared A PendingTurns.'
        )
        A = [ordered]@{ Id = $conversationAId; PendingTurns = @($conversationAState.PendingTurns).Count; Messages = @($conversationAState.Messages | ForEach-Object { [ordered]@{ Role = $_.Role; Content = $_.Content } }) }
        B = [ordered]@{ Id = $conversationBId; PendingTurns = @($conversationB[0].PendingTurns).Count; Messages = @($conversationB[0].Messages | ForEach-Object { [ordered]@{ Role = $_.Role; Content = $_.Content } }) }
    }
    Write-Output (ConvertTo-Json -InputObject $evidence -Depth 12)
    $smokeSucceeded = $true
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            $process.WaitForExit()
        }
    }
    $env:CODEV_DATA_ROOT = $previousDataRoot
    if ($smokeSucceeded -and (Test-Path -LiteralPath $smokeRoot)) {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $resolved = [System.IO.Path]::GetFullPath($smokeRoot)
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a path outside the temporary directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $smokeRoot) {
        Write-Warning "Smoke failed; profile and logs are preserved at $smokeRoot"
    }
}
