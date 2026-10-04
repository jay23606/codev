using Avalonia;
using Avalonia.Headless;
using Xunit;
using Xunit.Sdk;

[assembly: AvaloniaTestApplication(typeof(Codev.Avalonia.Tests.TestAppBuilder))]

namespace Codev.Avalonia.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Codev.Avalonia.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

[XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
public sealed class LiveOllamaFactAttribute : FactAttribute
{
    public LiveOllamaFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CODEV_OLLAMA_LIVE_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set CODEV_OLLAMA_LIVE_TESTS=1 to run the live local-model test.";
    }
}
