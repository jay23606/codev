param(
    [string]$AppPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/publish/win-x64/Codev.Avalonia.exe'),
    [string]$Model = 'qwen3.6:35b-a3b',
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    throw "Packaged Avalonia app was not found at $AppPath."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$smokeRoot = Join-Path $env:TEMP ("Codev-Packaged-Queue-" + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $smokeRoot 'data'
$codevRoot = Join-Path $dataRoot 'Codev'
$stdoutPath = Join-Path $smokeRoot 'stdout.log'
$stderrPath = Join-Path $smokeRoot 'stderr.log'
$conversationPath = Join-Path $codevRoot 'avalonia-conversations.json'
$activePath = Join-Path $codevRoot 'avalonia-active-conversation.json'
$previousDataRoot = $env:CODEV_DATA_ROOT
$process = $null
$expected = 'Packaged queue resume passed.'
$conversationId = [guid]::NewGuid().ToString()
$enqueuedAt = [DateTimeOffset]::UtcNow.ToString('o')

function Get-SavedConversation([string]$Path, [string]$Id) {
    $conversations = @(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    return @($conversations | Where-Object { [string]$_.Id -eq $Id })
}

function Read-Log([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return Get-Content -LiteralPath $Path -Raw }
    return ''
}

try {
    $null = New-Item -ItemType Directory -Path $codevRoot -Force
    $conversation = @{
        Id = $conversationId
        Title = 'Packaged queue resume smoke'
        Draft = ''
        Model = $Model
        Provider = 'ollama'
        NumCtx = 8192
        NumPredict = 64
        Temperature = 0.2
        IsCodeTask = $false
        IsPlanMode = $false
        Messages = @(
            @{ Role = 'user'; Content = "Reply with exactly: $expected" }
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
    ConvertTo-Json -InputObject @($conversation) -Depth 20 | Set-Content -LiteralPath $conversationPath -Encoding utf8
    ConvertTo-Json -InputObject $conversationId | Set-Content -LiteralPath $activePath -Encoding utf8

    $env:CODEV_DATA_ROOT = $dataRoot
    $process = Start-Process -FilePath $AppPath -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Window),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Codev'))

    $window = $null
    $windowDeadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Packaged app exited at startup: $(Read-Log $stderrPath)" }
        $window = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
        if ($null -eq $window) { Start-Sleep -Milliseconds 250 }
    } while ($null -eq $window -and [DateTime]::UtcNow -lt $windowDeadline)
    if ($null -eq $window) { throw 'Packaged queue smoke could not find the Codev window.' }

    $resumeCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            'Resume saved queue'))
    $resume = $null
    $resumeDeadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $resume = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $resumeCondition)
        if ($null -eq $resume) { Start-Sleep -Milliseconds 200 }
    } while ($null -eq $resume -and [DateTime]::UtcNow -lt $resumeDeadline)
    if ($null -eq $resume -or -not $resume.Current.IsEnabled) {
        throw 'The recovered turn did not expose an enabled Resume saved queue button.'
    }

    $queueStatusCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            '1 saved · Resume to continue'))
    $queueStatus = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $queueStatusCondition)
    if ($null -eq $queueStatus -or $queueStatus.Current.IsOffscreen) {
        throw 'The packaged footer did not visibly distinguish the recovered saved turn.'
    }

    $savedConversation = Get-SavedConversation $conversationPath $conversationId
    if ($savedConversation.Count -ne 1 -or @($savedConversation[0].PendingTurns).Count -ne 1) {
        throw 'The recovered queue was not present in the isolated packaged-app profile.'
    }

    $resumePattern = $null
    if (-not $resume.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$resumePattern)) {
        throw 'Resume saved queue does not expose an accessible invoke pattern.'
    }
    $resumePattern.Invoke()

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $assistant = ''
    do {
        $process.Refresh()
        if ($process.HasExited) { throw "Packaged app exited during resume: $(Read-Log $stderrPath)" }
        $savedConversation = Get-SavedConversation $conversationPath $conversationId
        if ($savedConversation.Count -eq 1 -and @($savedConversation[0].Messages).Count -ge 2) {
            $assistant = [string]$savedConversation[0].Messages[1].Content
        }
        if ($savedConversation.Count -eq 1 -and @($savedConversation[0].PendingTurns).Count -eq 0 -and
            $assistant.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    if ($savedConversation.Count -ne 1 -or @($savedConversation[0].PendingTurns).Count -ne 0 -or
        -not $assistant.Contains($expected, [StringComparison]::OrdinalIgnoreCase)) {
        $pendingCount = if ($savedConversation.Count -eq 1) { @($savedConversation[0].PendingTurns).Count } else { -1 }
        throw "The recovered turn did not finish. Pending=$pendingCount; reply='$assistant'; stderr=$(Read-Log $stderrPath)"
    }

    Write-Host "Packaged Windows recovered-queue smoke passed: the Resume saved queue button was visible, enabled, and invoked with UI Automation; Ollama persisted the expected reply and cleared pending state. Reply: $assistant"
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
    if (Test-Path -LiteralPath $smokeRoot) {
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        $resolved = [System.IO.Path]::GetFullPath($smokeRoot)
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove a path outside the temporary directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
