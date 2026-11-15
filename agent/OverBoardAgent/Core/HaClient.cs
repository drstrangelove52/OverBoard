using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OverBoardAgent.Core;

/// <summary>Eine Home-Assistant-Entität für die Auswahlliste im Editor.</summary>
public sealed record HaEntity(string Id, string Name, string State, string Unit, string DeviceClass)
{
    public string Display => Name.Length > 0 && Name != Id ? $"{Name}  ·  {Id}" : Id;
    public string StateText => Unit.Length > 0 ? $"{State} {Unit}" : State;
}

/// <summary>Liest die Entitäten aus Home Assistant (WebSocket-Schnittstelle, Zugriffstoken). Nur lesend.</summary>
public static class HaClient
{
    private static List<HaEntity>? _cache;
    public static IReadOnlyList<HaEntity>? Cached => _cache;

    /// <summary>"homeassistant.local", "http://host:8123" oder "https://…" → WebSocket-Adresse.</summary>
    public static Uri? ToSocketUri(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim();
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        var secure = u.Scheme is "https" or "wss";
        var port = u.IsDefaultPort ? (secure ? 443 : 8123) : u.Port;   // ohne Port: Home-Assistant-Standard 8123
        return new UriBuilder(secure ? "wss" : "ws", u.Host, port, "/api/websocket").Uri;
    }

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new IOException("Verbindung von Home Assistant geschlossen");
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct);
    }

    private static Task SendAsync(ClientWebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);

    /// <summary>Holt alle Entitäten. Fehler werden als Text zurückgegeben (deutsch/englisch je nach Sprache).</summary>
    public static async Task<(List<HaEntity>? Entities, string? Error)> FetchAsync(string? url, string? token, CancellationToken ct = default)
    {
        var uri = ToSocketUri(url);
        if (uri == null) return (null, Strings.Get("ha.err.url"));
        if (string.IsNullOrWhiteSpace(token)) return (null, Strings.Get("ha.err.token"));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(uri, timeout.Token);

            using (var hello = await ReceiveAsync(ws, timeout.Token))
                if (hello.RootElement.GetProperty("type").GetString() != "auth_required")
                    return (null, Strings.Get("ha.err.protocol"));
            await SendAsync(ws, new { type = "auth", access_token = token.Trim() }, timeout.Token);
            using (var auth = await ReceiveAsync(ws, timeout.Token))
            {
                var type = auth.RootElement.GetProperty("type").GetString();
                if (type == "auth_invalid") return (null, Strings.Get("ha.err.invalid"));
                if (type != "auth_ok") return (null, Strings.Get("ha.err.protocol"));
            }

            await SendAsync(ws, new { id = 1, type = "get_states" }, timeout.Token);
            using var result = await ReceiveAsync(ws, timeout.Token);
            var root = result.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean())
                return (null, Strings.Get("ha.err.protocol"));

            var list = new List<HaEntity>();
            foreach (var e in root.GetProperty("result").EnumerateArray())
            {
                var id = e.GetProperty("entity_id").GetString() ?? "";
                var state = e.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
                string name = "", unit = "", dc = "";
                if (e.TryGetProperty("attributes", out var attr) && attr.ValueKind == JsonValueKind.Object)
                {
                    if (attr.TryGetProperty("friendly_name", out var fn)) name = fn.GetString() ?? "";
                    if (attr.TryGetProperty("unit_of_measurement", out var un)) unit = un.GetString() ?? "";
                    if (attr.TryGetProperty("device_class", out var d)) dc = d.GetString() ?? "";
                }
                if (id.Length > 0) list.Add(new HaEntity(id, name, state, unit, dc));
            }
            list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
            _cache = list;
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { /* egal */ }
            return (list, null);
        }
        catch (OperationCanceledException)
        {
            return (null, Strings.Get("ha.err.timeout"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Home Assistant: {ex.Message}");
            return (null, Strings.Format("ha.err.connect", ex.Message));
        }
    }
}
