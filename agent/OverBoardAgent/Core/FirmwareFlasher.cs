using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverBoardAgent.Core;

/// <summary>Eine Firmware, wie ESP-IDF sie baut: Ordner mit flasher_args.json und den Dateien (Bootloader, Partitionstabelle, Programm).</summary>
public sealed record FirmwarePackage(string Dir, string? Version, string? BuiltAt, IReadOnlyList<(string Offset, string File)> Files,
    IReadOnlyList<string> FlashArgs, string Chip);

/// <summary>Installiert eine Firmware über die UART-Buchse des Geräts mit esptool (Espressif).</summary>
public static partial class FirmwareFlasher
{
    /// <summary>esptool: zuerst neben dem Agent (mitgeliefert), dann in der ESP-IDF-Umgebung (Entwicklung), dann im Suchpfad.</summary>
    public static string? FindEsptool()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "esptool", "esptool.exe"),
            Path.Combine(AppContext.BaseDirectory, "esptool.exe"),
        };
        try
        {
            var idfTools = Environment.GetEnvironmentVariable("IDF_TOOLS_PATH") ?? @"C:\Espressif\tools";
            var env = Path.Combine(idfTools, "python_env");
            if (Directory.Exists(env))
                foreach (var d in Directory.GetDirectories(env))
                    candidates.Add(Path.Combine(d, "Scripts", "esptool.exe"));
        }
        catch { /* nicht lesbar: weiter */ }
        foreach (var c in candidates) if (File.Exists(c)) return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "esptool.exe");
                if (File.Exists(p)) return p;
            }
            catch { /* ungültiger Eintrag */ }
        }
        return null;
    }

    /// <summary>Liest einen Firmware-Ordner (flasher_args.json). Liefert einen Fehlertext, wenn er nicht brauchbar ist.</summary>
    public static (FirmwarePackage? Package, string? Error) LoadPackage(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "flasher_args.json");
            if (!File.Exists(path)) return (null, Strings.Get("fw.err.noargs"));
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var files = new List<(string, string)>();
            foreach (var p in root.GetProperty("flash_files").EnumerateObject())
            {
                var rel = p.Value.GetString() ?? "";
                var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) return (null, Strings.Format("fw.err.missing", rel));
                files.Add((p.Name, rel));
            }
            if (files.Count == 0) return (null, Strings.Get("fw.err.noargs"));

            var args = new List<string>();
            if (root.TryGetProperty("write_flash_args", out var wa))
                foreach (var a in wa.EnumerateArray()) args.Add(a.GetString() ?? "");
            var chip = "esp32p4";
            if (root.TryGetProperty("extra_esptool_args", out var extra) && extra.TryGetProperty("chip", out var c)) chip = c.GetString() ?? chip;

            var (version, built) = ReadAppInfo(dir, root);
            return (new FirmwarePackage(dir, version, built, files, args, chip), null);
        }
        catch (Exception ex)
        {
            return (null, Strings.Format("fw.err.read", ex.Message));
        }
    }

    /// <summary>Version und Bauzeit aus dem Programmabbild (esp_app_desc_t ab Byte 32).</summary>
    private static (string?, string?) ReadAppInfo(string dir, JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("app", out var app) || !app.TryGetProperty("file", out var f)) return (null, null);
            var full = Path.Combine(dir, (f.GetString() ?? "").Replace('/', Path.DirectorySeparatorChar));
            using var fs = File.OpenRead(full);
            var buf = new byte[160];
            if (fs.Read(buf, 0, buf.Length) < buf.Length) return (null, null);
            if (BitConverter.ToUInt32(buf, 32) != 0xABCD5432) return (null, null);
            static string Str(byte[] b, int off, int len)
            {
                var end = Array.IndexOf(b, (byte)0, off, len);
                return Encoding.ASCII.GetString(b, off, (end < 0 ? off + len : end) - off);
            }
            return (Str(buf, 48, 32), (Str(buf, 128, 16) + " " + Str(buf, 112, 16)).Trim());
        }
        catch
        {
            return (null, null);
        }
    }

    [GeneratedRegex(@"\((\d+)\s*%\)")]
    private static partial Regex PercentPattern();

    /// <summary>
    /// Schreibt die Firmware. Das Gerät wird dafür automatisch in den Flash-Modus versetzt und danach neu gestartet.
    /// Fortschritt in Prozent (0–100) mit der Textzeile von esptool.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> FlashAsync(FirmwarePackage pkg, string port, IProgress<(int Percent, string Line)> progress, CancellationToken ct = default)
    {
        var esptool = FindEsptool();
        if (esptool == null) return (false, Strings.Get("fw.err.noesptool"));

        var psi = new ProcessStartInfo(esptool)
        {
            WorkingDirectory = pkg.Dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--chip", pkg.Chip, "-p", port, "-b", "460800", "--before", "default_reset", "--after", "hard_reset", "write_flash" })
            psi.ArgumentList.Add(a);
        foreach (var a in pkg.FlashArgs) psi.ArgumentList.Add(a);
        foreach (var (offset, file) in pkg.Files)
        {
            psi.ArgumentList.Add(offset);
            psi.ArgumentList.Add(file.Replace('/', Path.DirectorySeparatorChar));
        }

        var last = new List<string>();
        void Handle(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return;
            lock (last)
            {
                last.Add(line);
                if (last.Count > 8) last.RemoveAt(0);
            }
            var m = PercentPattern().Match(line);
            progress.Report((m.Success ? int.Parse(m.Groups[1].Value) : -1, line));
        }

        Log.Info($"Firmware installieren: {pkg.Dir} über {port}");
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return (false, Strings.Format("fw.err.start", ex.Message));
        }

        // esptool schreibt den Fortschritt mit Wagenrücklauf (\r) statt Zeilenumbruch: selbst in Zeilen teilen
        async Task Pump(StreamReader reader)
        {
            var sb = new StringBuilder();
            var buffer = new char[256];
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] is '\r' or '\n') { Handle(sb.ToString()); sb.Clear(); }
                    else sb.Append(buffer[i]);
                }
            }
            Handle(sb.ToString());
        }

        using var reg = ct.Register(() => { try { proc.Kill(true); } catch { /* schon beendet */ } });
        await Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError), proc.WaitForExitAsync(ct));
        if (proc.ExitCode == 0)
        {
            Log.Info("Firmware installiert");
            return (true, null);
        }
        string tail;
        lock (last) tail = string.Join(" ", last.TakeLast(3));
        Log.Warn($"Firmware-Installation fehlgeschlagen (Code {proc.ExitCode}): {tail}");
        return (false, Strings.Format("fw.err.failed", tail));
    }
}
