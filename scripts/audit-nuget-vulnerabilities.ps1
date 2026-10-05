$ErrorActionPreference = 'Stop'

$restoreRoots = @(
    'Codev.Avalonia.Tests/Codev.Avalonia.Tests.csproj',
    'Codev.Cli/Codev.Cli.csproj',
    'Codev.Tests/Codev.Tests.csproj',
    'Codev.Windows.Tests/Codev.Windows.Tests.csproj'
)

$projects = @(
    'Codev.Avalonia/Codev.Avalonia.csproj',
    'Codev.Avalonia.Tests/Codev.Avalonia.Tests.csproj',
    'Codev.Cli/Codev.Cli.csproj',
    'Codev.Core/Codev.Core.csproj',
    'Codev.Tests/Codev.Tests.csproj',
    'Codev.Windows.Tests/Codev.Windows.Tests.csproj'
)

Push-Location (Join-Path $PSScriptRoot '..')
try {
    foreach ($project in $restoreRoots) {
        & dotnet restore $project --verbosity quiet
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet restore failed for $project (exit code $LASTEXITCODE)."
        }
    }

    $findings = [System.Collections.Generic.List[string]]::new()
    foreach ($project in $projects) {
        $json = & dotnet package list --project $project --vulnerable --include-transitive --format json --output-version 1 --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet vulnerability audit failed for $project (exit code $LASTEXITCODE)."
        }

        try {
            $report = $json | ConvertFrom-Json
        }
        catch {
            throw "NuGet vulnerability audit returned invalid JSON for $project."
        }

        foreach ($reportProject in @($report.projects)) {
            foreach ($framework in @($reportProject.frameworks)) {
                $packages = @($framework.topLevelPackages) + @($framework.transitivePackages)
                foreach ($package in $packages) {
                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        if ($null -ne $vulnerability) {
                            $findings.Add("$project :: $($package.id) $($package.resolvedVersion) :: $($vulnerability.severity) $($vulnerability.advisoryurl)")
                        }
                    }
                }
            }
        }
    }

    if ($findings.Count -gt 0) {
        Write-Host 'Known NuGet vulnerabilities were found:'
        $findings | ForEach-Object { Write-Host " - $_" }
        exit 1
    }

    Write-Host "NuGet vulnerability audit passed for $($projects.Count) projects, including transitive packages."
}
finally {
    Pop-Location
}
