using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.v3;

[assembly: AvaloniaTestApplication(typeof(Codev.Avalonia.Tests.TestAppBuilder))]

namespace Codev.Avalonia.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<Codev.Avalonia.App>();
        if (string.Equals(Environment.GetEnvironmentVariable("CODEV_TEST_REAL_RENDERING"), "1", StringComparison.Ordinal))
        {
            return builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = true
            });
        }
        return builder.UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

[XunitTestCaseDiscoverer(typeof(AvaloniaFactDiscoverer))]
public sealed class LiveOllamaFactAttribute : FactAttribute
{
    public LiveOllamaFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEV_OLLAMA_LIVE_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set CODEV_OLLAMA_LIVE_TESTS=1 to run the live local-model test.";
    }
}
