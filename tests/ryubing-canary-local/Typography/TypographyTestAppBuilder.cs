using Avalonia;
using Avalonia.Headless;
using Ryujinx.Ava.UI;

[assembly: AvaloniaTestApplication(typeof(Ryubing.Canary.Local.Tests.Typography.TypographyTestAppBuilder))]

namespace Ryubing.Canary.Local.Tests.Typography;

// No RyujinxApp/Program, real windows, app data, accounts or host preference initialization.
// Disable the headless drawing stub so assertions inspect actual Skia font tables/shaped glyphs.
public static class TypographyTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .With(UiFontPolicy.CreateFontManagerOptions());
}
