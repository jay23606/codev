using System.Diagnostics;
using System.Text;

namespace Codev;

public sealed record GitChildWorktree(Guid ParentConversationId, Guid ChildConversationId,
    string RepositoryRoot, string WorktreePath, string Branch, string StartCommit,
    IReadOnlyList<string>? DisabledFilters = null);
public sealed record GitChildWorktreeReview(string Branch, string BaseBranch, string StartCommit,
    string BaseHead, string ChildHead, IReadOnlyList<string> Files, string Diff, bool Truncated,
    bool HasUncommittedChanges);

/// <summary>Creates dedicated Git worktrees for child conversations without touching the user's checkout.</summary>
public sealed class GitChildWorktreeManager
{
    // Git shares worktree metadata under the common .git directory. Concurrent `worktree add`
    // processes can race while creating their commondir files, even for distinct branches.
    private static readonly SemaphoreSlim WorktreeAddGate = new(1, 1);
    private readonly string _codevRoot;
    private readonly string _worktreeRoot;
    private readonly string _hooksDisabledPath;

    public GitChildWorktreeManager(string localApplicationDataPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationDataPath);
        _codevRoot = Path.GetFullPath(Path.Combine(localApplicationDataPath, "Codev"));
        _worktreeRoot = Path.Combine(_codevRoot, "child-worktrees");
        _hooksDisabledPath = Path.Combine(_codevRoot, "child-worktree-hooks-disabled");
    }

    public async Task<GitChildWorktree> CreateAsync(string repositoryPath, Guid parentConversationId,
        Guid childConversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        if (parentConversationId == Guid.Empty || childConversationId == Guid.Empty || parentConversationId == childConversationId)
            throw new ArgumentException("Distinct parent and child conversation IDs are required.");

        var repository = Path.GetFullPath(repositoryPath);
        var rootResult = await RunGitAsync(repository, ["rev-parse", "--show-toplevel"], cancellationToken);
        EnsureSuccess(rootResult, "The selected folder is not inside a Git repository.");
        var repositoryRoot = Path.GetFullPath(rootResult.Output.Trim());
        var bareResult = await RunGitAsync(repositoryRoot, ["rev-parse", "--is-bare-repository"], cancellationToken);
        EnsureSuccess(bareResult, "Git could not inspect the repository.");
        if (string.Equals(bareResult.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A child worktree needs a non-bare Git repository.");

        var commitResult = await RunGitAsync(repositoryRoot, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken);
        EnsureSuccess(commitResult, "Commit the repository's initial state before creating a child worktree.");
        var commit = commitResult.Output.Trim();
        Directory.CreateDirectory(_codevRoot);
        RejectLink(_codevRoot, "The Codev data folder cannot be a link for child worktrees.");
        Directory.CreateDirectory(_worktreeRoot);
        RejectLink(_worktreeRoot, "The Codev child-worktrees folder cannot be a link.");
        EnsureHooksDisabledDirectory();

        var id = childConversationId.ToString("N");
        var branch = $"codev/child-{id}";
        var worktreePath = Path.Combine(_worktreeRoot, id);
        await WorktreeAddGate.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(worktreePath) || File.Exists(worktreePath))
                throw new IOException("A worktree folder already exists for this child conversation.");

            var addPlan = await SafeWorktreeAddArgumentsAsync(repositoryRoot,
                ["worktree", "add", "-b", branch, worktreePath, commit], cancellationToken);
            var add = await RunGitAsync(repositoryRoot, addPlan.Arguments, cancellationToken);
            EnsureSuccess(add, "Git could not create the isolated child worktree.");
            RejectLink(worktreePath, "Git created a linked worktree folder; Codev will not use it.");
            return new GitChildWorktree(parentConversationId, childConversationId, repositoryRoot, worktreePath, branch, commit,
                addPlan.DisabledFilters);
        }
        finally { WorktreeAddGate.Release(); }
    }

    public async Task<GitChildWorktree> RecoverAsync(string repositoryPath, Guid parentConversationId,
        Guid childConversationId, string branch, string startCommit, CancellationToken cancellationToken = default)
    {
        if (parentConversationId == Guid.Empty || childConversationId == Guid.Empty || parentConversationId == childConversationId)
            throw new ArgumentException("Distinct parent and child conversation IDs are required.");
        var expectedBranch = $"codev/child-{childConversationId:N}";
        if (!string.Equals(branch, expectedBranch, StringComparison.Ordinal))
            throw new ArgumentException("The saved child branch does not match this child conversation.", nameof(branch));
        ValidateCommit(startCommit);
        var repositoryRoot = await GetRepositoryRootAsync(repositoryPath, cancellationToken);
        var expectedPath = Path.Combine(_worktreeRoot, childConversationId.ToString("N"));
        Directory.CreateDirectory(_codevRoot);
        RejectLink(_codevRoot, "The Codev data folder cannot be a link for child worktrees.");
        Directory.CreateDirectory(_worktreeRoot);
        RejectLink(_worktreeRoot, "The Codev child-worktrees folder cannot be a link.");
        EnsureHooksDisabledDirectory();
        var baseResult = await RunGitAsync(repositoryRoot, ["rev-parse", "--verify", "--end-of-options", startCommit + "^{commit}"], cancellationToken);
        EnsureSuccess(baseResult, "The child's recorded starting commit is unavailable.");
        var branchResult = await RunGitAsync(repositoryRoot, ["rev-parse", "--verify", "--end-of-options", $"refs/heads/{branch}^{{commit}}"], cancellationToken);
        EnsureSuccess(branchResult, "The child branch is unavailable; Codev cannot recover a missing worktree without its branch.");
        var ancestor = await RunGitAsync(repositoryRoot, ["merge-base", "--is-ancestor", startCommit, branch], cancellationToken);
        if (ancestor.ExitCode != 0) throw new InvalidOperationException("The child branch no longer descends from its recorded starting commit.");

        await WorktreeAddGate.WaitAsync(cancellationToken);
        try
        {
            if (Directory.Exists(expectedPath) || File.Exists(expectedPath))
                throw new IOException("The child folder already exists and will not be overwritten during recovery.");
            var prune = await RunGitAsync(repositoryRoot, ["worktree", "prune", "--expire", "now"], cancellationToken);
            EnsureSuccess(prune, "Git could not clear stale metadata for missing worktrees.");
            var addPlan = await SafeWorktreeAddArgumentsAsync(repositoryRoot,
                ["worktree", "add", expectedPath, branch], cancellationToken);
            var add = await RunGitAsync(repositoryRoot, addPlan.Arguments, cancellationToken);
            EnsureSuccess(add, "Git could not restore the child worktree from its retained branch.");
            RejectLink(expectedPath, "Git created a linked child folder; Codev will not trust it.");
            return new GitChildWorktree(parentConversationId, childConversationId, repositoryRoot, expectedPath, branch, startCommit,
                addPlan.DisabledFilters);
        }
        finally { WorktreeAddGate.Release(); }
    }

    public bool IsManagedWorktreePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(full), _worktreeRoot, PathComparison) &&
                   Guid.TryParseExact(Path.GetFileName(full), "N", out _) && Directory.Exists(full) &&
                   (File.GetAttributes(_codevRoot) & FileAttributes.ReparsePoint) == 0 &&
                   (File.GetAttributes(_worktreeRoot) & FileAttributes.ReparsePoint) == 0 &&
                   (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public async Task<GitChildWorktreeReview> GetReviewAsync(string repositoryPath, string branch,
        string startCommit, CancellationToken cancellationToken = default)
    {
        var repository = await GetRepositoryRootAsync(repositoryPath, cancellationToken);
        ValidateBranch(branch);
        ValidateCommit(startCommit);
        var linkedWorktree = await FindWorktreePathAsync(repository, branch, cancellationToken);
        return await GetReviewAtRootAsync(repository, linkedWorktree, branch, startCommit, cancellationToken);
    }

    private static async Task<GitChildWorktreeReview> GetReviewAtRootAsync(string repository, string worktreePath,
        string branch, string startCommit, CancellationToken cancellationToken)
    {
        var baseResult = await RunGitAsync(repository, ["rev-parse", "--verify", "--end-of-options", startCommit + "^{commit}"], cancellationToken);
        EnsureSuccess(baseResult, "The child's recorded starting commit is unavailable; the worktree cannot be reviewed safely.");
        var branchResult = await RunGitAsync(repository, ["rev-parse", "--verify", "--end-of-options", $"refs/heads/{branch}^{{commit}}"], cancellationToken);
        EnsureSuccess(branchResult, "The child's branch is unavailable; the worktree may have been moved or deleted.");
        var ancestry = await RunGitAsync(repository, ["merge-base", "--is-ancestor", startCommit, branch], cancellationToken);
        if (ancestry.ExitCode != 0) throw new InvalidOperationException("The child branch does not descend from its recorded starting commit.");
        var baseHead = await RunGitAsync(repository, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken);
        EnsureSuccess(baseHead, "Git could not resolve the currently checked out target branch.");
        var baseBranch = await RunGitAsync(repository, ["branch", "--show-current"], cancellationToken);
        EnsureSuccess(baseBranch, "Git could not determine the target branch name.");
        var statusResult = await RunGitAsync(worktreePath, ["status", "--porcelain=v1", "--untracked-files=all", "-z"], cancellationToken);
        EnsureSuccess(statusResult, "Git could not inspect the child worktree status.");
        var hasChanges = statusResult.Output.Length > 0;
        var filesResult = await RunGitAsync(repository, ["diff", "--name-only", "-z", $"{startCommit}...{branch}", "--"], cancellationToken, 250_000);
        EnsureSuccess(filesResult, "Git could not list the committed child changes.");
        if (filesResult.Output.Length > 250_000)
            throw new InvalidOperationException("The child changed too many paths to produce a complete review. Reduce the change set and try again.");
        var allFiles = filesResult.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = allFiles.Take(GitRepositoryService.MaxReviewFiles).ToArray();
        var diffArguments = new List<string> { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--no-renames", "--unified=3", $"{startCommit}...{branch}", "--" };
        diffArguments.AddRange(files.Select(path => ":(literal)" + path));
        var diffResult = await RunGitAsync(repository, diffArguments, cancellationToken, GitRepositoryService.MaxReviewDiffCharacters + 1);
        EnsureSuccess(diffResult, "Git could not read the child's committed diff.");
        var truncated = allFiles.Length > files.Length || diffResult.Output.Length > GitRepositoryService.MaxReviewDiffCharacters;
        var diff = diffResult.Output.Length > GitRepositoryService.MaxReviewDiffCharacters
            ? diffResult.Output[..GitRepositoryService.MaxReviewDiffCharacters] : diffResult.Output;
        if (truncated) diff += "\n[Child review truncated at Codev's safety limit.]";
        return new GitChildWorktreeReview(branch, string.IsNullOrWhiteSpace(baseBranch.Output.Trim()) ? "(detached HEAD)" : baseBranch.Output.Trim(), startCommit,
            baseHead.Output.Trim(), branchResult.Output.Trim(), files, diff, truncated, hasChanges);
    }

    public async Task MergeAsync(string repositoryPath, GitChildWorktreeReview expectedReview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedReview);
        var repository = await GetRepositoryRootAsync(repositoryPath, cancellationToken);
        var branch = expectedReview.Branch;
        var startCommit = expectedReview.StartCommit;
        ValidateBranch(branch);
        ValidateCommit(startCommit);
        if (expectedReview.Truncated) throw new InvalidOperationException("The displayed child diff was truncated. Reduce the changes before merging so they can be reviewed in full.");
        if (expectedReview.HasUncommittedChanges) throw new InvalidOperationException("Commit or discard the child's uncommitted changes before merging.");
        var currentBranch = await RunGitAsync(repository, ["branch", "--show-current"], cancellationToken);
        EnsureSuccess(currentBranch, "Git could not determine the current branch.");
        var target = currentBranch.Output.Trim();
        if (target.Length == 0) throw new InvalidOperationException("Check out a local target branch before merging a child worktree.");
        if (string.Equals(target, branch, StringComparison.Ordinal))
            throw new InvalidOperationException("Switch to the parent or another target branch before merging a child.");
        var status = await RunGitAsync(repository, ["status", "--porcelain=v1", "--untracked-files=all", "-z"], cancellationToken);
        EnsureSuccess(status, "Git could not inspect the target worktree.");
        if (status.Output.Length > 0)
            throw new InvalidOperationException("The target Git worktree must be clean before merging a child branch.");
        var currentBase = await RunGitAsync(repository, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken);
        EnsureSuccess(currentBase, "Git could not read the target branch commit.");
        var mergeBase = await RunGitAsync(repository, ["merge-base", currentBase.Output.Trim(), startCommit], cancellationToken);
        EnsureSuccess(mergeBase, "The child's recorded starting commit is not in the target branch history.");
        if (!string.Equals(mergeBase.Output.Trim(), startCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The target branch no longer contains the child's starting commit; rebase or recover the child manually before merging.");
        var ancestry = await RunGitAsync(repository, ["merge-base", "--is-ancestor", startCommit, branch], cancellationToken);
        if (ancestry.ExitCode != 0) throw new InvalidOperationException("The child branch does not descend from its recorded starting commit.");
        var childWorktree = await FindWorktreePathAsync(repository, branch, cancellationToken);
        var review = await GetReviewAtRootAsync(repository, childWorktree, branch, startCommit, cancellationToken);
        if (review.HasUncommittedChanges)
            throw new InvalidOperationException("The child worktree has uncommitted changes. Commit them in the child conversation before merging.");
        if (!string.Equals(expectedReview.BaseHead, currentBase.Output.Trim(), StringComparison.Ordinal) ||
            !string.Equals(expectedReview.BaseBranch, target, StringComparison.Ordinal) ||
            !string.Equals(expectedReview.ChildHead, review.ChildHead, StringComparison.Ordinal) ||
            !string.Equals(expectedReview.Diff, review.Diff, StringComparison.Ordinal) ||
            !expectedReview.Files.SequenceEqual(review.Files, StringComparer.Ordinal))
            throw new InvalidOperationException("The parent or child changed after you reviewed it. Refresh and inspect the updated child diff before merging.");
        var childHead = await RunGitAsync(repository, ["rev-parse", "--verify", $"refs/heads/{branch}^{{commit}}"], cancellationToken);
        EnsureSuccess(childHead, "Git could not resolve the child branch head.");
        var targetAgain = await RunGitAsync(repository, ["rev-parse", "--verify", "HEAD^{commit}"], cancellationToken);
        EnsureSuccess(targetAgain, "Git could not recheck the target branch.");
        var childHeadAgain = await RunGitAsync(repository, ["rev-parse", "--verify", $"refs/heads/{branch}^{{commit}}"], cancellationToken);
        EnsureSuccess(childHeadAgain, "Git could not recheck the child branch.");
        if (!string.Equals(currentBase.Output.Trim(), targetAgain.Output.Trim(), StringComparison.Ordinal) ||
            !string.Equals(childHead.Output.Trim(), childHeadAgain.Output.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("The parent or child branch changed during review. Refresh and review the child diff again.");
        await EnsureNoConfiguredMergeDriversAsync(repository, review.Files, cancellationToken);
        await EnsureNoConfiguredCheckoutFiltersAsync(repository, review.Files, cancellationToken);
        EnsureHooksDisabledDirectory();
        var merge = await RunGitAsync(repository,
            ["-c", $"core.hooksPath={ToGitConfigPath(_hooksDisabledPath)}", "merge", "--no-ff", "--no-edit", branch], cancellationToken);
        if (merge.ExitCode != 0)
        {
            _ = await RunGitAsync(repository,
                ["-c", $"core.hooksPath={ToGitConfigPath(_hooksDisabledPath)}", "merge", "--abort"], cancellationToken);
            EnsureSuccess(merge, "Git could not merge the child branch; any partial merge was aborted.");
        }
    }

    private void EnsureHooksDisabledDirectory()
    {
        Directory.CreateDirectory(_codevRoot);
        RejectLink(_codevRoot, "The Codev data folder cannot be a link for child worktrees.");
        Directory.CreateDirectory(_hooksDisabledPath);
        RejectLink(_hooksDisabledPath, "The Codev child-worktree hook override folder cannot be a link.");
    }

    private static async Task EnsureNoConfiguredMergeDriversAsync(string repository, IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        const int maximumDriverConfigCharacters = 8192;
        const int maximumDrivers = 64;
        var configured = await RunGitAsync(repository,
            ["config", "--name-only", "--get-regexp", "^merge\\..*\\.driver$"], cancellationToken,
            maximumDriverConfigCharacters + 1).ConfigureAwait(false);
        if (configured.ExitCode is not (0 or 1) || configured.Output.Length > maximumDriverConfigCharacters)
            throw new InvalidOperationException("Git merge-driver configuration could not be inspected safely; Codev refused to merge the child worktree.");

        var driverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = configured.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (keys.Length > maximumDrivers || keys.Any(key => !key.StartsWith("merge.", StringComparison.OrdinalIgnoreCase) ||
                                                            !key.EndsWith(".driver", StringComparison.OrdinalIgnoreCase) ||
                                                            key.Length <= 12 || key.Any(char.IsControl)))
            throw new InvalidOperationException("Git merge-driver configuration is invalid or too large to inspect safely; Codev refused to merge the child worktree.");
        foreach (var key in keys) driverNames.Add(key[6..^7]);
        if (driverNames.Count == 0) return;
        if (files.Any(IsGitAttributesPath))
            throw new InvalidOperationException("Git has external merge drivers configured. Codev will not run them while applying a child worktree; disable them or merge the child branch manually.");
        var attributes = await GetChangedFileAttributesAsync(repository, files, "merge", cancellationToken).ConfigureAwait(false);
        if (attributes.Any(driverNames.Contains))
            throw new InvalidOperationException("Git has an external merge driver for a changed file. Codev will not run it while applying a child worktree; disable it or merge the child branch manually.");
    }

    private static async Task EnsureNoConfiguredCheckoutFiltersAsync(string repository, IReadOnlyList<string> files,
        CancellationToken cancellationToken)
    {
        const int maximumFilterConfigCharacters = 8192;
        const int maximumFilters = 64;
        var drivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suffix in new[] { "smudge", "process" })
        {
            var configured = await RunGitAsync(repository,
                ["config", "--name-only", "--get-regexp", $"^filter\\..*\\.{suffix}$"], cancellationToken,
                maximumFilterConfigCharacters + 1).ConfigureAwait(false);
            if (configured.ExitCode is not (0 or 1) || configured.Output.Length > maximumFilterConfigCharacters)
                throw new InvalidOperationException("Git checkout-filter configuration could not be inspected safely; Codev refused to merge the child worktree.");

            var ending = "." + suffix;
            foreach (var key in configured.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) ||
                    !key.EndsWith(ending, StringComparison.OrdinalIgnoreCase) || key.Length <= 6 + ending.Length ||
                    key.Any(char.IsControl))
                    throw new InvalidOperationException("Git checkout-filter configuration is invalid; Codev refused to merge the child worktree.");
                drivers.Add(key[7..^ending.Length]);
                if (drivers.Count > maximumFilters)
                    throw new InvalidOperationException("Too many Git checkout filters are configured for a safe child merge.");
            }
        }

        if (drivers.Count == 0) return;
        if (files.Any(IsGitAttributesPath))
            throw new InvalidOperationException("Git has checkout filters configured and the child changes .gitattributes. Codev will not run them while applying the child worktree; disable them or merge the child branch manually.");
        var attributes = await GetChangedFileAttributesAsync(repository, files, "filter", cancellationToken).ConfigureAwait(false);
        if (attributes.Any(drivers.Contains))
            throw new InvalidOperationException("Git has a checkout filter for a changed file. Codev will not run it while applying a child worktree; disable it or merge the child branch manually.");
    }

    private static bool IsGitAttributesPath(string path) =>
        string.Equals(Path.GetFileName(path), ".gitattributes", StringComparison.OrdinalIgnoreCase);

    private static async Task<HashSet<string>> GetChangedFileAttributesAsync(string repository,
        IReadOnlyList<string> files, string attribute, CancellationToken cancellationToken)
    {
        var arguments = new List<string>(files.Count + 5) { "check-attr", "--cached", "-z", attribute, "--" };
        arguments.AddRange(files);
        var result = await RunGitAsync(repository, arguments, cancellationToken, 250_000).ConfigureAwait(false);
        EnsureSuccess(result, "Git could not inspect attributes for the reviewed child changes.");
        var fields = result.Output.Split('\0');
        if (fields.Length == 0 || fields[^1].Length != 0 || (fields.Length - 1) % 3 != 0)
            throw new InvalidOperationException("Git returned malformed file attributes; Codev refused to merge the child worktree.");
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 2; i < fields.Length - 1; i += 3)
        {
            if (fields[i].Any(char.IsControl))
                throw new InvalidOperationException("Git returned invalid file attributes; Codev refused to merge the child worktree.");
            if (!string.Equals(fields[i], "unspecified", StringComparison.Ordinal)) values.Add(fields[i]);
        }
        return values;
    }

    private async Task<string> GetRepositoryRootAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(path, ["rev-parse", "--show-toplevel"], cancellationToken);
        EnsureSuccess(result, "The selected project is not inside a Git repository.");
        return Path.GetFullPath(result.Output.Trim());
    }

    private async Task<SafeWorktreeAddPlan> SafeWorktreeAddArgumentsAsync(string repositoryRoot,
        IReadOnlyList<string> worktreeArguments, CancellationToken cancellationToken)
    {
        const int maximumFilterDrivers = 64;
        const int maximumFilterConfigCharacters = 8192;
        var drivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var suffix in new[] { "smudge", "process" })
        {
            var configured = await RunGitAsync(repositoryRoot,
                ["config", "--name-only", "--get-regexp", $"^filter\\..*\\.{suffix}$"], cancellationToken,
                maximumFilterConfigCharacters + 1).ConfigureAwait(false);
            if (configured.ExitCode is not (0 or 1))
                throw new InvalidOperationException("Git filter configuration could not be inspected; Codev refused to create a child worktree.");
            if (configured.Output.Length > maximumFilterConfigCharacters)
                throw new InvalidOperationException("The Git filter configuration is too large to inspect safely; Codev refused to create a child worktree.");

            var ending = "." + suffix;
            foreach (var key in configured.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) ||
                    !key.EndsWith(ending, StringComparison.OrdinalIgnoreCase) || key.Length <= 6 + ending.Length)
                    throw new InvalidOperationException("Git returned an invalid filter configuration key; Codev refused to create a child worktree.");
                drivers.Add(key[7..^ending.Length]);
                if (drivers.Count > maximumFilterDrivers)
                    throw new InvalidOperationException("Too many Git filters are configured for a safe child worktree checkout.");
            }
        }

        var arguments = new List<string>(worktreeArguments.Count + 8 + drivers.Count * 6)
        {
            "-c", $"core.hooksPath={ToGitConfigPath(_hooksDisabledPath)}",
            "-c", "core.fsmonitor=false"
        };
        foreach (var driver in drivers)
        {
            if (driver.Length is 0 or > 128 || driver.Any(char.IsControl))
                throw new InvalidOperationException("A Git filter name is invalid for a safe child worktree checkout.");
            arguments.Add("-c");
            // An empty smudge command makes Git pass the checked-out blob through unchanged.
            // Do not use `cat` here: Git would launch a PATH-resolved executable during checkout.
            arguments.Add($"filter.{driver}.smudge=");
            arguments.Add("-c");
            arguments.Add($"filter.{driver}.process=");
            arguments.Add("-c");
            arguments.Add($"filter.{driver}.required=false");
        }
        arguments.AddRange(worktreeArguments);
        return new SafeWorktreeAddPlan(arguments, drivers.OrderBy(driver => driver, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<string> FindWorktreePathAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(repository, ["worktree", "list", "--porcelain"], cancellationToken);
        EnsureSuccess(result, "Git could not locate the child worktree.");
        string? candidatePath = null;
        string? candidateBranch = null;
        foreach (var line in result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                candidatePath = line[9..];
                candidateBranch = null;
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
                candidateBranch = line[18..];
            else if (line.Length == 0 && string.Equals(candidateBranch, branch, StringComparison.Ordinal))
                return ValidateManagedWorktree(candidatePath!);
        }
        if (string.Equals(candidateBranch, branch, StringComparison.Ordinal) && candidatePath is not null)
            return ValidateManagedWorktree(candidatePath);
        throw new InvalidOperationException("Git no longer lists the child branch as a linked worktree.");
    }

    private string ValidateManagedWorktree(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsManagedWorktreePath(fullPath))
            throw new InvalidOperationException("The child worktree is missing or is outside Codev's managed worktree folder.");
        return fullPath;
    }

    private static void ValidateBranch(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch) || branch.Length > 120 ||
            !branch.StartsWith("codev/child-", StringComparison.Ordinal) || branch.StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException("Only a Codev-managed child branch can be reviewed or merged.", nameof(branch));
    }

    private static void ValidateCommit(string commit)
    {
        if (string.IsNullOrWhiteSpace(commit) || commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
            throw new ArgumentException("The child's full starting commit is invalid.", nameof(commit));
    }

    private static async Task<GitResult> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken, int maxOutputCharacters = 65_536)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git", WorkingDirectory = workingDirectory, UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        // Even commands used only to display a review can run repository-configured helpers:
        // `git status` invokes core.fsmonitor, and `git diff` may invoke textconv drivers.
        // Disable fsmonitor for every invocation; the review diff separately opts out of textconv.
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.fsmonitor=false");
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Git could not be started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Git was not found. Install Git and ensure it is on PATH.", ex);
        }
        var outputTask = ReadBoundedAsync(process.StandardOutput, maxOutputCharacters, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, 16_384, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException("The Git worktree operation took longer than 20 seconds.");
        }
        return new GitResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxCharacters, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maxCharacters, 16_384));
        var buffer = new char[8192];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            var remaining = maxCharacters - output.Length;
            if (remaining > 0) output.Append(buffer, 0, Math.Min(count, remaining));
        }
        return output.ToString();
    }

    private static void EnsureSuccess(GitResult result, string message)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"{message}\n{result.Error.Trim()}");
    }

    private static void RejectLink(string path, string message)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException(message);
    }

    private static string ToGitConfigPath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record SafeWorktreeAddPlan(IReadOnlyList<string> Arguments, IReadOnlyList<string> DisabledFilters);
    private sealed record GitResult(int ExitCode, string Output, string Error);
}
