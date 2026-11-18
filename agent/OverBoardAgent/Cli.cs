using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OverBoardAgent.Core;
using OverBoardAgent.Editor;

namespace OverBoardAgent;

/// <summary>
/// Kommandozeile des Programms, z. B. für Claude Code beim Gestalten der Seiten: Konfiguration prüfen, Vorschau zeichnen (--export-preview),
/// auf das Display schicken (--send) und von dort holen (--get), dazu Listen mit Symbolen, Aktionen, PC-Werten und Home-Assistant-Entitäten.
/// Alles läuft lokal; --send und --get sprechen über eine Pipe mit der laufenden Instanz (nur derselbe Windows-Benutzer).
/// </summary>
internal static class Cli
{
    private static readonly string[] Commands =
        ["--validate", "--send", "--get", "--list-icons", "--list-actions", "--list-sources", "--list-entities", "--help-cli"];

    public static bool IsCli(string[] args) => args.Any(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase));

    /// <summary>Name der Pipe der laufenden Instanz (je Windows-Benutzer eine).</summary>
    public static string PipeName => "OverBoardAgent.Cli." + Environment.UserName;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    public static async Task<int> RunAsync(string[] args)
    {
        // Das Programm hat kein eigenes Konsolenfenster: an die des Aufrufers anhängen. Bei umgeleiteter Ausgabe (Datei oder Pipe,
        // z. B. durch Claude Code) nicht: sonst ginge sie an die Konsole statt in die Datei.
        try
        {
            var handle = GetStdHandle(-11);                                   // STD_OUTPUT_HANDLE
            var redirected = handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != 0;
            if (!redirected) AttachConsole(-1);
        }
        catch { /* egal */ }
        Console.OutputEncoding = new UTF8Encoding(false);
        Settings.Load();
        try { Program.BuildAvaloniaApp().SetupWithoutStarting(); } catch { /* Symbole stehen dann nicht zur Verfügung */ }

        try
        {
            var cmd = args.First(a => Commands.Contains(a, StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();
            var rest = args.SkipWhile(a => !string.Equals(a, cmd, StringComparison.OrdinalIgnoreCase)).Skip(1).ToArray();
            return cmd switch
            {
                "--validate" => Validate(rest),
                "--list-icons" => ListIcons(rest),
                "--list-actions" => ListActions(),
                "--list-sources" => ListSources(),
                "--list-entities" => await ListEntitiesAsync(rest),
                "--send" => await SendAsync(rest),
                "--get" => await GetAsync(rest),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fehler: " + ex.Message);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            OverBoardAgent.exe --validate <datei.json>            Konfiguration prüfen (Fehler im Klartext)
            OverBoardAgent.exe --export-preview <datei.json> <bild.png> [Seite]   Display als Bild zeichnen (Seite ab 0)
            OverBoardAgent.exe --send <datei.json>                Konfiguration auf das Display schicken (Programm muss laufen)
            OverBoardAgent.exe --get <datei.json>                 Konfiguration vom Display holen
            OverBoardAgent.exe --list-icons [Filter]              Symbolnamen
            OverBoardAgent.exe --list-actions                     Aktionen für Tasten (pc:<Kennung>), eingebaute und eigene
            OverBoardAgent.exe --list-sources                     PC-Werte für Anzeige-Kacheln (pc:<Kennung>)
            OverBoardAgent.exe --list-entities [Filter]           Home-Assistant-Entitäten mit Namen und aktuellem Wert
            """);
        return 0;
    }

    private static string Arg(string[] rest, string what)
    {
        if (rest.Length == 0) throw new ArgumentException($"Es fehlt {what}.");
        return rest[0];
    }

    // ---- Prüfen ------------------------------------------------------------------------------

    private static int Validate(string[] rest)
    {
        var file = Arg(rest, "die Datei");
        var json = File.ReadAllText(file);
        ConfigModel config;
        try { config = ConfigModel.Parse(json); }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"FEHLER: Kein gültiges JSON ({ex.Message})");
            return 1;
        }

        var errors = ConfigValidator.Validate(config);
        var warnings = new List<string>();

        // Hinweise: Aktionen, die es nicht gibt (das Gerät lässt sie sonst einfach ins Leere laufen)
        var actions = new ActionStore();
        var known = BuiltinActions.All.Select(a => "pc:" + a.Id).Concat(actions.Items.Select(a => "pc:" + a.Id)).ToHashSet();
        for (var p = 0; p < config.Pages.Count; p++)
            for (var t = 0; t < config.Pages[p].Tiles.Count; t++)
            {
                var tile = config.Pages[p].Tiles[t];
                var where = $"Seite {p + 1}, Kachel {t + 1}";
                if (tile.Action != null && !known.Contains(tile.Action)) warnings.Add($"{where}: Aktion '{tile.Action}' ist unbekannt (siehe --list-actions)");
                if (tile.Source is { } s && s.StartsWith("pc:", StringComparison.Ordinal) && s is not ("pc:volume" or "pc:mute" or "pc:mic_mute" or "pc:cpu" or "pc:ram" or "pc:net_down" or "pc:net_up"))
                    warnings.Add($"{where}: PC-Wert '{s}' ist unbekannt (siehe --list-sources)");
            }

        foreach (var e in errors) Console.WriteLine("FEHLER: " + e);
        foreach (var w in warnings) Console.WriteLine("HINWEIS: " + w);
        var tiles = config.Pages.Sum(pg => pg.Tiles.Count);
        if (errors.Count == 0) Console.WriteLine($"OK: {config.Pages.Count} Seiten, {tiles} Kacheln" + (warnings.Count > 0 ? $" ({warnings.Count} Hinweise)" : ""));
        return errors.Count == 0 ? 0 : 1;
    }

    // ---- Listen ------------------------------------------------------------------------------

    private static int ListIcons(string[] rest)
    {
        var filter = rest.Length > 0 ? rest[0] : "";
        foreach (var name in MdiIcons.Names.Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine(name);
        return 0;
    }

    private static int ListActions()
    {
        Console.WriteLine("# Kennung\tName\tHinweis");
        foreach (var a in BuiltinActions.All)
            Console.WriteLine($"pc:{a.Id}\t{a.De}\t{(a.Confirm ? "gefährlich: mit Rückfrage (Bedienung confirm)" : "eingebaut")}");
        foreach (var a in new ActionStore().Items)
            Console.WriteLine($"pc:{a.Id}\t{a.Name}\teigene Aktion");
        return 0;
    }

    private static int ListSources()
    {
        Console.WriteLine("# Wert\tBeschreibung\tFormat");
        Console.WriteLine("pc:volume\tLautstärke des PCs in Prozent\tpercent");
        Console.WriteLine("pc:mute\tPC stumm (on/off)\tonoff");
        Console.WriteLine("pc:mic_mute\tMikrofon stumm (on/off)\tonoff");
        Console.WriteLine("pc:cpu\tCPU-Auslastung in Prozent\tpercent");
        Console.WriteLine("pc:ram\tBelegter Arbeitsspeicher in Prozent\tpercent");
        Console.WriteLine("pc:net_down\tNetzwerk: Download in Mbit/s\tmbit");
        Console.WriteLine("pc:net_up\tNetzwerk: Upload in Mbit/s\tmbit");
        Console.WriteLine();
        Console.WriteLine("# Formate: power, kwh, status, onoff, text, percent, time, mbit");
        return 0;
    }

    private static async Task<int> ListEntitiesAsync(string[] rest)
    {
        if (string.IsNullOrWhiteSpace(Settings.HaUrl) || string.IsNullOrWhiteSpace(Settings.HaToken))
        {
            Console.Error.WriteLine("Home Assistant ist im Programm nicht eingerichtet (Seite Gerät, Einrichtung).");
            return 1;
        }
        var (entities, error) = await HaClient.FetchAsync(Settings.HaUrl, Settings.HaToken);
        if (entities == null)
        {
            Console.Error.WriteLine("Home Assistant: " + error);
            return 1;
        }
        var filter = rest.Length > 0 ? rest[0] : "";
        Console.WriteLine("# Entität\tName\tWert\tEinheit");
        foreach (var e in entities.Where(e => filter.Length == 0 || e.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"{e.Id}\t{e.Name}\t{e.State}\t{e.Unit}");
        return 0;
    }

    // ---- Senden und Holen (über die laufende Instanz) ------------------------------------------

    private static async Task<int> SendAsync(string[] rest)
    {
        var file = Arg(rest, "die Datei");
        var json = File.ReadAllText(file);
        var reply = await AskRunningAsync(new { cmd = "send", config = json });
        if (reply.GetProperty("ok").GetBoolean())
        {
            Console.WriteLine("Auf das Display gesendet.");
            return 0;
        }
        Console.Error.WriteLine("Nicht gesendet: " + (reply.TryGetProperty("error", out var e) ? e.GetString() : "?"));
        return 1;
    }

    private static async Task<int> GetAsync(string[] rest)
    {
        var file = Arg(rest, "die Datei");
        var reply = await AskRunningAsync(new { cmd = "get" });
        if (!reply.GetProperty("ok").GetBoolean())
        {
            Console.Error.WriteLine("Nicht geholt: " + (reply.TryGetProperty("error", out var e) ? e.GetString() : "?"));
            return 1;
        }
        var config = ConfigModel.Parse(reply.GetProperty("config").GetString() ?? "");
        File.WriteAllText(file, config.ToJson(pretty: true), new UTF8Encoding(false));
        Console.WriteLine($"Gespeichert: {file}");
        return 0;
    }

    private static async Task<JsonElement> AskRunningAsync(object request)
    {
        await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);        // einfache, blockierende Ein-/Ausgabe genügt hier
        var debug = Environment.GetEnvironmentVariable("OVERBOARD_CLI_DEBUG") == "1";
        if (debug) Console.Error.WriteLine($"verbinde mit {PipeName} …");
        try { client.Connect(3000); }
        catch (TimeoutException) { throw new InvalidOperationException("OverBoard läuft nicht. Bitte das Programm starten (und das Display verbinden)."); }
        if (debug) Console.Error.WriteLine("verbunden");
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        writer.WriteLine(JsonSerializer.Serialize(request));
        if (debug) Console.Error.WriteLine("Anfrage gesendet, warte auf Antwort …");
        var line = reader.ReadLine() ?? throw new IOException("Keine Antwort vom Programm.");
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    // ---- Server in der laufenden Instanz -----------------------------------------------------

    /// <summary>Nimmt in der laufenden Instanz Befehle von --send und --get an. Läuft auf einem Hintergrund-Thread.</summary>
    public static void StartServer(AgentCore core)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    // Nur der eigene Windows-Benutzer darf sich verbinden
                    var security = new System.IO.Pipes.PipeSecurity();
                    security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.IO.Pipes.PipeAccessRights.ReadWrite, System.Security.AccessControl.AccessControlType.Allow));
                    await using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, security);
                    await server.WaitForConnectionAsync();
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var reader = new StreamReader(server, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(server, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(cts.Token);
                    var response = line == null ? Fail("leere Anfrage") : await HandleAsync(core, line, cts.Token);
                    await writer.WriteLineAsync(response);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Befehlsschnittstelle: {ex.Message}");
                    await Task.Delay(500);
                }
            }
        });
    }

    private static string Fail(string error) => JsonSerializer.Serialize(new { ok = false, error });

    private static async Task<string> HandleAsync(AgentCore core, string line, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var cmd = doc.RootElement.GetProperty("cmd").GetString();
            if (!core.Link.IsConnected) return Fail("Das Display ist nicht verbunden.");

            if (cmd == "get")
            {
                var json = await core.Link.GetConfigAsync(ct);
                return JsonSerializer.Serialize(new { ok = true, config = json });
            }
            if (cmd == "send")
            {
                var text = doc.RootElement.GetProperty("config").GetString() ?? "";
                ConfigModel config;
                try { config = ConfigModel.Parse(text); }
                catch (JsonException ex) { return Fail("Kein gültiges JSON: " + ex.Message); }
                var errors = ConfigValidator.Validate(config);
                if (errors.Count > 0) return Fail(string.Join("; ", errors.Take(5)));
                var (ok, error) = await core.Link.SetConfigAsync(config.ToJson(), ct);
                Log.Info(ok ? "Konfiguration über die Kommandozeile gesendet" : $"Konfiguration (Kommandozeile) nicht gesendet: {error}");
                return ok ? JsonSerializer.Serialize(new { ok = true }) : Fail(error ?? "unbekannt");
            }
            return Fail("unbekannter Befehl");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }
}
