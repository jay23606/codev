namespace Codev.Tests;

public sealed class PersonalAgentInstructionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public void Empty_preferences_add_no_context(string? value) => Assert.Equal("", PersonalAgentInstructions.Build(value));

    [Fact]
    public void Labels_and_trims_preferences()
    {
        var result = PersonalAgentInstructions.Build("\nPrefer concise answers.\n");

        Assert.Contains("Personal user preferences", result);
        Assert.EndsWith("Prefer concise answers.", result);
    }

    [Fact]
    public void Truncates_long_preferences_to_the_documented_limit()
    {
        var normalized = PersonalAgentInstructions.Normalize(new string('x', PersonalAgentInstructions.MaxCharacters + 1));

        Assert.Equal(PersonalAgentInstructions.MaxCharacters, normalized.Length);
    }
}
