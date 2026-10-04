using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Codev.Avalonia.Tests.TestAppBuilder))]

namespace Codev.Avalonia.Tests;

public sealed class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Codev.Avalonia.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
