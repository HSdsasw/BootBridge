using Avalonia;
using IsoWriter.Platform;
using IsoWriter.UI;

namespace IsoWriter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--helper")) { Environment.ExitCode = HelperHost.RunAsync().GetAwaiter().GetResult(); return; }
        if (args.Contains("--diagnose")) { Environment.ExitCode = Diagnostics.RunAsync().GetAwaiter().GetResult(); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<BridgeApp>().UsePlatformDetect().WithInterFont().LogToTrace();
}
