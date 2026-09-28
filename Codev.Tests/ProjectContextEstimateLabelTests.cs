using Codev;

namespace Codev.Tests;

public sealed class ProjectContextEstimateLabelTests
{
    [Fact]
    public void Missing_project_reports_that_no_files_will_be_sent()
    {
        Assert.Equal("No project files will be included.", ProjectContextEstimateLabel.Format(false, false, false, 120));
    }

    [Fact]
    public void Hosted_chat_without_opt_in_says_project_files_stay_local()
    {
        Assert.Equal("Project files stay local; hosted context is off.", ProjectContextEstimateLabel.Format(true, true, false, 120));
    }

    [Fact]
    public void Included_context_is_labeled_as_a_source_only_estimate()
    {
        var label = ProjectContextEstimateLabel.Format(true, true, true, 1200);

        Assert.Contains("Project files:", label);
        Assert.Contains(1200.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), label);
        Assert.Contains("excludes conversation history", label);
    }

    [Fact]
    public void Project_estimate_uses_the_safe_bounded_file_service()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-context-estimate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "safe.cs"), new string('x', 20));
            File.WriteAllText(Path.Combine(root, ".env"), "SECRET=do-not-estimate");

            var selected = ProjectContextEstimateLabel.ForProject(root, "ollama", false, true, ["safe.cs"]);
            var secret = ProjectContextEstimateLabel.ForProject(root, "ollama", false, true, [".env"]);

            Assert.Contains("≈11 tokens", selected);
            Assert.Contains("≈0 tokens", secret);
            Assert.DoesNotContain("do-not-estimate", selected + secret, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Hosted_opt_out_skips_project_estimation()
    {
        var label = ProjectContextEstimateLabel.ForProject(Path.GetTempPath(), CloudModelProviders.Anthropic, false, false, null);

        Assert.Equal("Project files stay local; hosted context is off.", label);
    }

    [Fact]
    public void Untrusted_folder_does_not_automatically_add_project_files_but_explicit_files_are_estimable()
    {
        var root = Path.Combine(Path.GetTempPath(), "codev-untrusted-estimate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "safe.cs"), new string('x', 20));

            var automatic = ProjectContextEstimateLabel.ForProject(root, "ollama", false, false, null);
            var explicitFile = ProjectContextEstimateLabel.ForProject(root, "ollama", false, false, ["safe.cs"]);

            Assert.Contains("automatic project context is off", automatic);
            Assert.Contains("≈11 tokens", explicitFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
