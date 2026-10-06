param(
    [Parameter(Mandatory)] [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$seedProfile = Join-Path $OutputRoot 'seed-profile'
$appData = Join-Path $seedProfile 'Codev'
$project = Join-Path $OutputRoot 'synthetic-large-project'
New-Item -ItemType Directory -Path $appData, $project -Force | Out-Null

# Match the established Q3 synthetic workload exactly: 1,000 files / 4,966,000
# bytes and a 240-message transcript / 537,276 characters.
$fileContent = [byte[]]::new(4966)
$pattern = [System.Text.Encoding]::ASCII.GetBytes('// Synthetic Q3 benchmark source file. No real project content. ')
for ($i = 0; $i -lt $fileContent.Length; $i++) { $fileContent[$i] = $pattern[$i % $pattern.Length] }
$relativeFiles = [System.Collections.Generic.List[string]]::new()
for ($i = 1; $i -le 1000; $i++) {
    $relativePath = 'src/file-{0:D4}.cs' -f $i
    $fullPath = Join-Path $project $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($fullPath, $fileContent)
    if ($i -le 100) { $relativeFiles.Add($relativePath) }
}

$messages = [System.Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt 240; $i++) {
    $length = if ($i -lt 156) { 2239 } else { 2238 }
    $role = if ($i % 2 -eq 0) { 'user' } else { 'assistant' }
    $prefix = '{0} synthetic history message {1:D3}: ' -f $role, $i
    $content = $prefix + ('x' * ($length - $prefix.Length))
    $messages.Add(@{ Role = $role; Content = $content; Thinking = '' })
}

$conversation = @{
    Id = 'fd28d79c-fd27-4ee0-a301-5c9c5c1a8657'
    Title = 'Synthetic Q3 large transcript and project'
    Model = 'qwen3.6:35b-a3b'
    Provider = 'ollama'
    NumCtx = 32768
    ProjectPath = $project
    ContextFiles = $relativeFiles
    Messages = $messages
    UpdatedAt = [DateTimeOffset]::UtcNow.ToString('o')
}
$json = ConvertTo-Json -InputObject @($conversation) -Depth 10
[System.IO.File]::WriteAllText((Join-Path $appData 'avalonia-conversations.json'), $json, [System.Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    SeedProfile = $seedProfile
    Project = $project
    ProjectFiles = 1000
    ProjectBytes = (Get-ChildItem -LiteralPath $project -Recurse -File | Measure-Object -Property Length -Sum).Sum
    SelectedContextFiles = $relativeFiles.Count
    Messages = $messages.Count
    TranscriptCharacters = ($messages | Measure-Object -Property { $_.Content.Length } -Sum).Sum
} | ConvertTo-Json -Compress
