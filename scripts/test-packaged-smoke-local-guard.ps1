$ErrorActionPreference = 'Stop'

$missingApp = Join-Path $env:TEMP ('Codev-guard-test-missing-' + [guid]::NewGuid().ToString('N') + '.exe')
$scripts = @(
    'smoke-packaged-windows-app.ps1',
    'smoke-packaged-windows-recovered-queue.ps1',
    'smoke-packaged-windows-data-survival.ps1'
) | ForEach-Object { Join-Path $PSScriptRoot $_ }
$previousGitHubActions = $env:GITHUB_ACTIONS

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
}

Write-Output 'Packaged-app smoke guards reject unset, false, and noncanonical GITHUB_ACTIONS values before touching the desktop.'
