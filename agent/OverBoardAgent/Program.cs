using Avalonia;
using Avalonia.Controls;

namespace OverBoardAgent;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Nur eine Instanz: ein zweiter Start beendet sich sofort
        // (Der Vorschau-Export für die Entwicklung darf neben einem laufenden Agent laufen)
        var export = args.Contains("--export-preview");
        using var mutex = new Mutex(true, @"Local\OverBoardAgent", out var isFirst);
        if (!isFirst && !export) return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
