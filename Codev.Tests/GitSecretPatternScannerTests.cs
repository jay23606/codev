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

    [Fact]
    public void Scan_finds_provider_specific_formats_and_ignores_obvious_placeholders()
    {
        var stripe = "sk_live_" + new string('1', 24);
        var google = "AIza" + new string('2', 35);
        var npm = "npm_" + new string('3', 36);
        var diff = "diff --git a/.env b/.env\n+++ b/.env\n@@ -0,0 +1,5 @@\n+stripe = " + stripe + "\n+google = " + google + "\n+npm = " + npm + "\n+api_key = \"your_api_key_here\"\n+password = \"example-password-value\"";

        var findings = GitSecretPatternScanner.Scan(new GitWorkingTreeReview("main", [".env"], diff, false));

        Assert.Contains(findings, finding => finding.Kind == "Stripe secret key");
        Assert.Contains(findings, finding => finding.Kind == "Google API key");
        Assert.Contains(findings, finding => finding.Kind == "npm token");
        Assert.DoesNotContain(findings, finding => finding.Kind == "credential-like assignment");
    }

    [Fact]
    public void Scan_finds_hugging_face_user_and_oauth_tokens_without_returning_the_value()
    {
        const string userToken = "hf_0123456789abcdefghijklmnopqrstuvwxyz";
        const string oauthToken = "hf_oauth_0123456789abcdefghijklmnopqrstuvwxyz";
        var diff = "diff --git a/model.py b/model.py\n+++ b/model.py\n@@ -0,0 +1,2 @@\n+token = \"" + userToken + "\"\n+token = \"" + oauthToken + "\"";

        var findings = GitSecretPatternScanner.Scan(new GitWorkingTreeReview("main", ["model.py"], diff, false));

        Assert.Contains(findings, finding => finding.Kind == "Hugging Face token" && finding.Line == 1);
        Assert.Contains(findings, finding => finding.Kind == "Hugging Face token" && finding.Line == 2);
        Assert.DoesNotContain(findings, finding => finding.ToString().Contains(userToken, StringComparison.Ordinal) ||
            finding.ToString().Contains(oauthToken, StringComparison.Ordinal));
    }
}
