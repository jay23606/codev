$ErrorActionPreference = 'Stop'

$missingApp = Join-Path $env:TEMP ('Codev-guard-test-missing-' + [guid]::NewGuid().ToString('N') + '.exe')
$scripts = @(
    'smoke-packaged-windows-app.ps1',
    'smoke-packaged-windows-recovered-queue.ps1',
    'smoke-packaged-windows-data-survival.ps1'
) | ForEach-Object { Join-Path $PSScriptRoot $_ }
$upgradeScript = Join-Path $PSScriptRoot 'smoke-windows-upgrade.ps1'
$previousGitHubActions = $env:GITHUB_ACTIONS
$previousRunnerEnvironment = $env:RUNNER_ENVIRONMENT
$previousRunnerTemp = $env:RUNNER_TEMP

try {
    foreach ($scriptPath in $scripts) {
        foreach ($value in @($null, '', 'false', 'FALSE', 'TRUE', '0')) {
            $env:GITHUB_ACTIONS = $value
            try {
                & $scriptPath -AppPath $missingApp
                throw "The local smoke guard did not stop '$([IO.Path]::GetFileName($scriptPath))' for GITHUB_ACTIONS='$value'."
            }
            catch {
                if ($_.Exception.Message -notlike '*disabled on local desktops by default*') { throw }
            }
        }

        $env:GITHUB_ACTIONS = 'true'
        try {
            & $scriptPath -AppPath $missingApp
            throw "The missing app path did not stop '$([IO.Path]::GetFileName($scriptPath))'."
        }
        catch {
            if ($_.Exception.Message -like '*disabled on local desktops by default*') { throw }
        }
    }
}
finally {
    $env:GITHUB_ACTIONS = $previousGitHubActions
    $env:RUNNER_ENVIRONMENT = $previousRunnerEnvironment
    $env:RUNNER_TEMP = $previousRunnerTemp
}

try {
    $env:GITHUB_ACTIONS = ''
    $env:RUNNER_ENVIRONMENT = 'github-hosted'
    $env:RUNNER_TEMP = $env:TEMP
    try {
        & $upgradeScript -CurrentAppPath $missingApp
        throw 'The upgrade smoke did not reject a local invocation before touching the desktop.'
    }
    catch {
        if ($_.Exception.Message -notlike '*disposable GitHub-hosted runner*') { throw }
    }

    $env:GITHUB_ACTIONS = 'true'
    $env:RUNNER_ENVIRONMENT = ''
    $env:RUNNER_TEMP = $env:TEMP
    try {
        & $upgradeScript -CurrentAppPath $missingApp
        throw 'The upgrade smoke did not reject a local runner before touching the desktop.'
    }
    catch {
        if ($_.Exception.Message -notlike '*disposable GitHub-hosted runner*') { throw }
    }

    $env:RUNNER_ENVIRONMENT = 'github-hosted'
    $env:RUNNER_TEMP = ''
    try {
        & $upgradeScript -CurrentAppPath $missingApp
        throw 'The upgrade smoke did not reject a missing hosted-runner temp folder before touching the desktop.'
    }
    catch {
        if ($_.Exception.Message -notlike '*disposable GitHub-hosted runner*') { throw }
    }
}
finally {
    $env:GITHUB_ACTIONS = $previousGitHubActions
    $env:RUNNER_ENVIRONMENT = $previousRunnerEnvironment
    $env:RUNNER_TEMP = $previousRunnerTemp
}

Write-Output 'Packaged-app and upgrade smoke guards reject local or incomplete runner environments before launching the desktop.'
