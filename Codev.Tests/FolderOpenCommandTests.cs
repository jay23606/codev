using Codev;

namespace Codev.Tests;

public sealed class FolderOpenCommandTests
{
    [Theory]
    [InlineData("Windows", "explorer.exe")]
    [InlineData("macOS", "open")]
    [InlineData("Linux", "xdg-open")]
    public void Selects_the_native_file_manager_and_passes_the_path_as_one_argument(string operatingSystem, string expectedExecutable)
    {
        const string path = "a folder/with spaces";

        var command = FolderOpenCommandResolver.Resolve(operatingSystem, path);
        var start = command.CreateStartInfo();

        Assert.Equal(expectedExecutable, start.FileName);
        Assert.Equal(path, Assert.Single(start.ArgumentList));
        Assert.False(start.UseShellExecute);
    }
}
