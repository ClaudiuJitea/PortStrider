using Avalonia;
using System;

namespace PortStrider.UI;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

        if (OperatingSystem.IsLinux())
        {
            var renderEnv = Environment.GetEnvironmentVariable("PORTSTRIDER_RENDER")?.ToLowerInvariant();
            var modes = renderEnv switch
            {
                "glx" => new[] { X11RenderingMode.Glx, X11RenderingMode.Software },
                "egl" => new[] { X11RenderingMode.Egl, X11RenderingMode.Software },
                "vulkan" => new[] { X11RenderingMode.Vulkan, X11RenderingMode.Software },
                _ => new[] { X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software }
            };

            builder = builder.With(new X11PlatformOptions
            {
                RenderingMode = modes
            });
        }

        return builder
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
    }
}
