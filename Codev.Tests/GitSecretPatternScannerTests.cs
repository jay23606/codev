namespace Codev.Tests;

public sealed class GitSecretPatternScannerTests
{
    [Fact]
    public void Scan_finds_added_common_tokens_without_exposing_values()
    {
        const string github = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
        var review = new GitWorkingTreeReview("main", ["config.cs"],
            "diff --git a/config.cs b/config.cs\n+++ b/config.cs\n@@ -0,0 +1,3 @@\n+var key = \"" + github + "\";\n+password = \"very-long-secret-value-123\";\n+safe = true;", false);

        var findings = GitSecretPatternScanner.Scan(review);

        Assert.Contains(findings, finding => finding.Kind == "GitHub token" && finding.Line == 1);
        Assert.Contains(findings, finding => finding.Kind == "credential-like assignment" && finding.Line == 2);
        Assert.DoesNotContain(findings, finding => finding.Kind == "credential-like assignment" && finding.Line == 3);
        Assert.DoesNotContain(findings, finding => finding.ToString().Contains(github, StringComparison.Ordinal));
    }

    [Fact]
    public void Scan_ignores_removed_lines()
    {
        var findings = GitSecretPatternScanner.Scan(new GitWorkingTreeReview("main", ["config.cs"],
            "diff --git a/config.cs b/config.cs\n--- a/config.cs\n+++ b/config.cs\n@@ -1 +1 @@\n-password = \"very-long-secret-value-123\";\n+password = \"short\";", false));

        Assert.Empty(findings);
    }
}
