using Avalonia;
using Avalonia.Controls;

namespace OverBoardAgent;

internal static class Program
{
    /// <summary>Wird von einem zweiten Start gesetzt: die laufende Instanz zeigt dann ihr Hauptfenster.</summary>
    public static EventWaitHandle? ShowEvent { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Befehle für die Kommandozeile (prüfen, senden, Listen ...): ohne Fenster, laufen neben dem Programm
        if (Cli.IsCli(args)) return Task.Run(() => Cli.RunAsync(args)).GetAwaiter().GetResult();   // auf einem Pool-Thread: der Aufruf auf dem UI-Thread würde bei await hängen

        // Nur eine Instanz: ein zweiter Start beendet sich sofort
        // (Der Vorschau-Export für die Entwicklung darf neben einem laufenden Agent laufen)
        var export = args.Contains("--export-preview");
        using var mutex = new Mutex(true, @"Local\OverBoardAgent", out var isFirst);
        ShowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\OverBoardAgent.Show");
        if (!isFirst && !export)
        {
            // Ein zweiter Start (Doppelklick auf das Symbol) holt das Fenster der laufenden Instanz nach vorn
            try { ShowEvent.Set(); } catch { /* egal */ }
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
