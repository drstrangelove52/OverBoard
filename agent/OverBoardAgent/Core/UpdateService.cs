using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OverBoardAgent.Core;

/// <summary>Eine auf GitHub veröffentlichte Version (Release). Der Installer bringt auch die passende Firmware mit.</summary>
public sealed record UpdateInfo(string Tag, Version Version, string Notes, string InstallerName, string InstallerUrl, string? SumsUrl, string PageUrl);

/// <summary>
/// Sucht auf GitHub nach einer neueren Version (höchstens einmal täglich, abschaltbar) und installiert sie auf Wunsch.
/// Es wird nichts ohne Klick installiert. Der Installer wird gegen die Prüfsumme (SHA256SUMS.txt) des Releases geprüft.
/// </summary>
public static class UpdateService
{
    /// <summary>GitHub-Repository mit den Releases (Eigentümer/Name).</summary>
    public const string Repo = "drstrangelove52/OverBoard";

    private static readonly HttpClient Http = CreateClient();

    public static UpdateInfo? Latest { get; private set; }
    public static string? LastError { get; private set; }
    public static event Action? Changed;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OverBoardAgent", "1"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Version dieses Programms (drei Stellen).</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public static bool UpdateAvailable => Latest != null && Latest.Version > Current;

    /// <summary>"v1.2.3" → 1.2.3. Null, wenn der Tag keine Version ist.</summary>
    public static Version? ParseTag(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        var dash = t.IndexOfAny(['-', '+']);
        if (dash >= 0) t = t[..dash];
        return Version.TryParse(t, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>Automatische Prüfung fällig? (eingeschaltet und die letzte ist über 24 Stunden her)</summary>
    public static bool DueForAutoCheck()
    {
        if (!Settings.UpdateCheck) return false;
        if (Settings.LastUpdateCheckUtc == 0) return true;
        var last = new DateTime(Settings.LastUpdateCheckUtc, DateTimeKind.Utc);
        return DateTime.UtcNow - last >= TimeSpan.FromHours(24);
    }

    /// <summary>Fragt GitHub nach der neuesten Veröffentlichung. Merkt sich den Zeitpunkt (für die Begrenzung auf einmal täglich).</summary>
    public static async Task<(UpdateInfo? Info, string? Error)> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
            Settings.LastUpdateCheckUtc = DateTime.UtcNow.Ticks;
            Settings.Save();
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Fail(Strings.Get("upd.err.notfound"));
            if (!resp.IsSuccessStatusCode)
                return Fail(Strings.Format("upd.err.http", (int)resp.StatusCode));

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseTag(tag);
            if (version == null) return Fail(Strings.Format("upd.err.tag", tag));

            string? installerName = null, installerUrl = null, sumsUrl = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.StartsWith("OverBoard-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    (installerName, installerUrl) = (name, url);
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    sumsUrl = url;
            }
            if (installerUrl == null) return Fail(Strings.Get("upd.err.noinstaller"));

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            var info = new UpdateInfo(tag, version, notes, installerName!, installerUrl, sumsUrl, page);
            Latest = info;
            LastError = null;
            Changed?.Invoke();
            return (info, null);
        }
        catch (OperationCanceledException)
        {
            return Fail(Strings.Get("upd.err.timeout"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Update-Prüfung: {ex.Message}");
            return Fail(Strings.Format("upd.err.net", ex.Message));
        }
    }

    private static (UpdateInfo?, string?) Fail(string error)
    {
        LastError = error;
        Changed?.Invoke();
        return (null, error);
    }

    /// <summary>Lädt den Installer, prüft die Prüfsumme und startet ihn (still, schliesst und startet das Programm neu).</summary>
    public static async Task<(bool Ok, string? Error)> InstallAsync(UpdateInfo info, IProgress<int> progress, CancellationToken ct = default)
    {
        try
        {
            if (info.SumsUrl == null) return (false, Strings.Get("upd.err.nosums"));

            var dir = Path.Combine(Path.GetTempPath(), "OverBoard-Update");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, info.InstallerName);

            using (var resp = await Http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(file);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress.Report((int)(done * 100 / total));
                }
            }

            var sums = await Http.GetStringAsync(info.SumsUrl, ct);
            string? expected = null;
            foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split([' ', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[^1].Equals(info.InstallerName, StringComparison.OrdinalIgnoreCase)) expected = parts[0];
            }
            if (expected == null) return (false, Strings.Get("upd.err.nosums"));

            string actual;
            await using (var fs = File.OpenRead(file))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
                return (false, Strings.Get("upd.err.checksum"));
            }

            Log.Info($"Update {info.Tag} wird installiert");
            Process.Start(new ProcessStartInfo(file, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS") { UseShellExecute = true });
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            return (false, Strings.Get("upd.err.timeout"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Update-Installation: {ex.Message}");
            return (false, Strings.Format("upd.err.net", ex.Message));
        }
    }
}
