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
}
