using Codev;

namespace Codev.Tests;

public sealed class CodevDataPathsTests
{
    [Fact]
    public void Local_data_root_uses_default_when_no_override_is_configured()
    {
        var resolved = CodevDataPaths.ResolveLocalDataRoot(null, Path.Combine(Path.GetTempPath(), "codev-default"));

        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "codev-default")), resolved);
    }

    [Fact]
    public void Local_data_root_resolves_an_absolute_override()
    {
        var configured = Path.Combine(Path.GetTempPath(), "codev-isolated-profile");

        var resolved = CodevDataPaths.ResolveLocalDataRoot(configured, "ignored-default");

        Assert.Equal(Path.GetFullPath(configured), resolved);
        Assert.Equal(Path.Combine(resolved, "Codev"), Path.Combine(CodevDataPaths.ResolveLocalDataRoot(configured, "ignored-default"), "Codev"));
    }

    [Fact]
    public void Relative_override_is_rejected_instead_of_falling_back_to_another_profile()
    {
        Assert.Throws<InvalidOperationException>(() => CodevDataPaths.ResolveLocalDataRoot("relative-data", "default"));
    }

    [Fact]
    public void Default_credential_targets_remain_compatible_and_redirected_profiles_are_separate()
    {
        const string target = "https://codev.local/hosted-model-api";
        var first = CodevDataPaths.ScopeCredentialTarget(target, Path.Combine(Path.GetTempPath(), "profile-one"));
        var firstAgain = CodevDataPaths.ScopeCredentialTarget(target, Path.Combine(Path.GetTempPath(), ".", "profile-one"));
        var second = CodevDataPaths.ScopeCredentialTarget(target, Path.Combine(Path.GetTempPath(), "profile-two"));

        Assert.Equal(target, CodevDataPaths.ScopeCredentialTarget(target, null));
        Assert.Equal(first, firstAgain);
        Assert.NotEqual(target, first);
        Assert.NotEqual(first, second);
        Assert.Matches("^https://codev\\.local/hosted-model-api/[0-9a-f]{64}$", first);
    }
}
