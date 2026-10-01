param(
    [int] $SourceFiles = 10000,
    [int] $TextFileSizeKiB = 8,
    [int] $BinaryFiles = 8,
    [int] $BinaryFileSizeMiB = 4,
    [int] $UntrackedFiles = 100,
    [int] $IgnoredFiles = 2000,
    [int] $IgnoredFileSizeKiB = 32
)

$ErrorActionPreference = 'Stop'

function Invoke-Git([string] $Repository, [string[]] $Arguments, [switch] $AllowFailure) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = $Repository
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void] $start.ArgumentList.Add($argument) }

    $process = [Diagnostics.Process]::Start($start)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0 -and -not $AllowFailure) {
        throw "git $($Arguments -join ' ') failed ($($process.ExitCode)): $stderr"
    }
    return $stdout
}

function Get-NulSeparatedPaths([string] $Text) {
    return @($Text.Split([char] 0, [StringSplitOptions]::RemoveEmptyEntries))
}

function Get-TreeBytes([string] $Path) {
    [long] $bytes = 0
    foreach ($file in [IO.Directory]::EnumerateFiles($Path, '*', [IO.SearchOption]::AllDirectories)) {
        $bytes += ([IO.FileInfo]::new($file)).Length
    }
    return $bytes
}

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Security.Cryptography;

public static class SnapshotBenchmark
{
    public static bool FilesMatch(string sourceRoot, string snapshotRoot, string[] relativePaths)
    {
        foreach (var relativePath in relativePaths)
        {
            using var source = File.OpenRead(Path.Combine(sourceRoot, relativePath));
            using var snapshot = File.OpenRead(Path.Combine(snapshotRoot, relativePath));
            var sourceHash = SHA256.HashData(source);
            var snapshotHash = SHA256.HashData(snapshot);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, snapshotHash))
                return false;
        }
        return true;
    }
}
'@

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $tempRoot ('Codev-C7-fixture-' + [guid]::NewGuid().ToString('N'))
$snapshotRoot = Join-Path $tempRoot ('Codev-C7-snapshot-' + [guid]::NewGuid().ToString('N'))

try {
    foreach ($directory in @('src', 'assets', 'build')) {
        [void] [IO.Directory]::CreateDirectory((Join-Path $fixtureRoot $directory))
    }
    [IO.File]::WriteAllText((Join-Path $fixtureRoot '.gitignore'), "build/`n")

    $textBytes = $TextFileSizeKiB * 1KB
    for ($i = 0; $i -lt $SourceFiles; $i++) {
        $line = 'public sealed class Sample{0:D6} {{ public int Value => {1}; }}' -f $i, $i
        $content = $line.PadRight($textBytes, ' ')
        [IO.File]::WriteAllText((Join-Path (Join-Path $fixtureRoot 'src') ('Source{0:D6}.cs' -f $i)), $content, [Text.Encoding]::UTF8)
    }

    for ($i = 0; $i -lt $BinaryFiles; $i++) {
        $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes($BinaryFileSizeMiB * 1MB)
        [IO.File]::WriteAllBytes((Join-Path (Join-Path $fixtureRoot 'assets') ('Asset{0:D3}.bin' -f $i)), $bytes)
    }

    [void] (Invoke-Git $fixtureRoot @('init', '--quiet', '--initial-branch=main'))
    [void] (Invoke-Git $fixtureRoot @('config', 'user.email', 'snapshot-benchmark@example.invalid'))
    [void] (Invoke-Git $fixtureRoot @('config', 'user.name', 'Snapshot benchmark'))
    [void] (Invoke-Git $fixtureRoot @('add', '.gitignore', 'src', 'assets'))
    [void] (Invoke-Git $fixtureRoot @('commit', '--quiet', '-m', 'Synthetic snapshot fixture'))

    for ($i = 0; $i -lt $UntrackedFiles; $i++) {
        $content = ('Untracked sample {0}' -f $i).PadRight($textBytes, ' ')
        [IO.File]::WriteAllText((Join-Path (Join-Path $fixtureRoot 'src') ('Untracked{0:D5}.md' -f $i)), $content, [Text.Encoding]::UTF8)
    }

    for ($i = 0; $i -lt $IgnoredFiles; $i++) {
        $bytes = [Security.Cryptography.RandomNumberGenerator]::GetBytes($IgnoredFileSizeKiB * 1KB)
        [IO.File]::WriteAllBytes((Join-Path (Join-Path $fixtureRoot 'build') ('Cache{0:D5}.dat' -f $i)), $bytes)
    }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $included = Get-NulSeparatedPaths (Invoke-Git $fixtureRoot @('ls-files', '--cached', '--others', '--exclude-standard', '-z'))
    [void] [IO.Directory]::CreateDirectory($snapshotRoot)
    [long] $includedBytes = 0
    foreach ($relativePath in $included) {
        $sourcePath = Join-Path $fixtureRoot $relativePath
        $snapshotPath = Join-Path $snapshotRoot $relativePath
        [void] [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($snapshotPath))
        $includedBytes += ([IO.FileInfo]::new($sourcePath)).Length
        [IO.File]::Copy($sourcePath, $snapshotPath)
    }
    $timer.Stop()

    $verifyTimer = [Diagnostics.Stopwatch]::StartNew()
    $hashesMatch = $true
    $hashesMatch = [SnapshotBenchmark]::FilesMatch($fixtureRoot, $snapshotRoot, [string[]] $included)
    $verifyTimer.Stop()

    $ignored = Get-NulSeparatedPaths (Invoke-Git $fixtureRoot @('ls-files', '--others', '--ignored', '--exclude-standard', '-z'))
    [long] $ignoredBytes = 0
    foreach ($relativePath in $ignored) { $ignoredBytes += ([IO.FileInfo]::new((Join-Path $fixtureRoot $relativePath))).Length }
    $tracked = Get-NulSeparatedPaths (Invoke-Git $fixtureRoot @('ls-files', '-z'))

    [pscustomobject] @{
        Fixture = 'Synthetic source tree; only temporary directories were written'
        TrackedFiles = $tracked.Count
        IncludedUntrackedFiles = $included.Count - $tracked.Count
        IncludedFiles = $included.Count
        IncludedMiB = [Math]::Round($includedBytes / 1MB, 1)
        BinaryFiles = $BinaryFiles
        IgnoredFiles = $ignored.Count
        IgnoredMiB = [Math]::Round($ignoredBytes / 1MB, 1)
        SnapshotCopyAndDiscoverySeconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3)
        HashVerificationSeconds = [Math]::Round($verifyTimer.Elapsed.TotalSeconds, 3)
        SnapshotDiskMiB = [Math]::Round((Get-TreeBytes $snapshotRoot) / 1MB, 1)
        HashesMatch = $hashesMatch
    } | Format-List

    if (-not $hashesMatch) { throw 'Snapshot hash verification failed.' }
}
finally {
    foreach ($path in @($fixtureRoot, $snapshotRoot)) {
        $fullPath = [IO.Path]::GetFullPath($path)
        if (-not $fullPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($fullPath) -notmatch '^Codev-C7-(fixture|snapshot)-[0-9a-f]{32}$') {
            throw "Refusing cleanup outside the generated temporary benchmark paths: $fullPath"
        }
        if ([IO.Directory]::Exists($fullPath)) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
    }
}
