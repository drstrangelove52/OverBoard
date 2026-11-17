using System.Text.Json;

namespace OverBoardAgent.Core;

/// <summary>Sendersuche im freien Verzeichnis radio-browser.info (Name, Stream-Adresse, Logo).</summary>
public static class RadioSearch
{
    private static readonly string[] Servers = ["de1.api.radio-browser.info", "de2.api.radio-browser.info", "nl1.api.radio-browser.info", "at1.api.radio-browser.info"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    static RadioSearch()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "OverBoard");
    }

    public sealed record Result(string Name, string Url, string? Logo, string Details);

    /// <summary>Sucht Sender nach Name. Streams im HLS-Format (.m3u8) werden ausgelassen, die der Player nicht abspielt.</summary>
    public static async Task<(List<Result>? Results, string? Error)> SearchAsync(string name, CancellationToken ct = default)
    {
        var query = $"/json/stations/search?name={Uri.EscapeDataString(name)}&limit=40&hidebroken=true&order=clickcount&reverse=true";
        Exception? last = null;
        foreach (var server in Servers)
        {
            try
            {
                var json = await Http.GetStringAsync("https://" + server + query, ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var list = new List<Result>();
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var url = S("url_resolved").Length > 0 ? S("url_resolved") : S("url");
                    if (url.Length == 0 || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.TryGetProperty("hls", out var hls) && hls.ValueKind == JsonValueKind.Number && hls.GetInt32() == 1) continue;
                    var bitrate = e.TryGetProperty("bitrate", out var br) && br.ValueKind == JsonValueKind.Number ? br.GetInt32() : 0;
                    var details = string.Join(" · ", new[] { S("countrycode"), S("codec"), bitrate > 0 ? bitrate + " kbit/s" : "" }.Where(x => x.Length > 0));
                    var logo = S("favicon");
                    list.Add(new Result(S("name").Trim(), url, logo.Length > 0 ? logo : null, details));
                }
                return (list, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }
        return (null, last?.Message ?? "?");
    }
}
