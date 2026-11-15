using System.IO.Ports;
using System.Text;
using System.Text.Json;

namespace OverBoardAgent.Core;

/// <summary>
/// Verbindung zum OverBoard-Gerät über USB (serielle Schnittstelle). Sucht das Gerät selbst, baut die Verbindung
/// auf (hello), hält sie mit ping/pong am Leben und verbindet bei Unterbrechung automatisch neu.
/// </summary>
public sealed class DeviceLink : IDisposable
{
    private const int PingIntervalMs = 2000;
    private const int TimeoutMs = 6000;

    private readonly string _pcName = Environment.MachineName;
    private readonly string _appVersion;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _txLock = new();
    private SerialPort? _port;

    public DeviceLink(string appVersion) => _appVersion = appVersion;

    public bool IsConnected => _port != null;
    public DeviceInfo? Device { get; private set; }

    /// <summary>Verbindung steht (true, mit Geräteinfo) oder ist weg (false).</summary>
    public event Action<bool, DeviceInfo?>? ConnectionChanged;
    /// <summary>Das Gerät hat eine Aktion ausgelöst (nur die Kennung).</summary>
    public event Action<string>? ActionReceived;

    public void Start() => Task.Run(() => RunAsync(_cts.Token));

    public void Send(string line)
    {
        var port = _port;
        if (port == null) return;
        try
        {
            lock (_txLock) port.Write(line + "\n");
        }
        catch (Exception ex)
        {
            Log.Warn($"Senden fehlgeschlagen: {ex.Message}");
        }
    }

    public void SendState(string id, string value) => Send(Protocol.State(id, value));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var (port, info) = TryConnect();
            if (port == null)
            {
                await SafeDelay(2000, ct);
                continue;
            }

            _port = port;
            Device = info;
            Log.Info($"Verbunden mit {port.PortName}: {info?.Device} Firmware {info?.Firmware}");
            ConnectionChanged?.Invoke(true, info);
            try
            {
                await SessionAsync(port, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"Verbindung unterbrochen: {ex.Message}");
            }
            finally
            {
                _port = null;
                Device = null;
                try { port.Close(); } catch { /* schon weg */ }
                port.Dispose();
                Log.Info("Verbindung beendet");
                ConnectionChanged?.Invoke(false, null);
            }
        }
    }

    private (SerialPort?, DeviceInfo?) TryConnect()
    {
        foreach (var name in PortFinder.Find())
        {
            SerialPort? port = null;
            try
            {
                port = new SerialPort(name, 115200)
                {
                    DtrEnable = true,          // ohne DTR sendet das Gerät nichts
                    NewLine = "\n",
                    Encoding = new UTF8Encoding(false),
                    ReadTimeout = 500,
                    WriteTimeout = 1000,
                };
                port.Open();
                port.DiscardInBuffer();
                port.Write(Protocol.Hello(_pcName, _appVersion) + "\n");

                var deadline = DateTime.UtcNow.AddMilliseconds(2500);
                while (DateTime.UtcNow < deadline)
                {
                    string line;
                    try { line = port.ReadLine(); }
                    catch (TimeoutException) { continue; }
                    var info = ParseHello(line);
                    if (info != null) return (port, info);
                }
                Log.Info($"{name}: keine hello-Antwort, ist es ein anderes Gerät?");
            }
            catch (Exception ex)
            {
                Log.Info($"{name}: {ex.Message}");
            }
            try { port?.Dispose(); } catch { /* ignorieren */ }
        }
        return (null, null);
    }

    private static DeviceInfo? ParseHello(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("t").GetString() != "hello") return null;
            if (root.GetProperty("dev").GetString() != "overboard") return null;
            return new DeviceInfo("overboard",
                root.TryGetProperty("fw", out var fw) ? fw.GetString() ?? "" : "",
                root.TryGetProperty("w", out var w) ? w.GetInt32() : 0,
                root.TryGetProperty("h", out var h) ? h.GetInt32() : 0,
                root.TryGetProperty("lang", out var l) ? l.GetString() ?? "" : "",
                root.TryGetProperty("v", out var pv) && pv.ValueKind == JsonValueKind.Number ? pv.GetInt32() : 1);
        }
        catch
        {
            return null;
        }
    }

    private async Task SessionAsync(SerialPort port, CancellationToken ct)
    {
        var lastRx = DateTime.UtcNow;
        var lastPing = DateTime.MinValue;
        var reader = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested && port.IsOpen)
            {
                string line;
                try { line = port.ReadLine(); }
                catch (TimeoutException) { continue; }
                lastRx = DateTime.UtcNow;
                HandleLine(line);
            }
        }, ct);

        while (!ct.IsCancellationRequested)
        {
            if (reader.IsFaulted || reader.IsCompleted) throw new IOException("Lesen beendet");
            if ((DateTime.UtcNow - lastPing).TotalMilliseconds >= PingIntervalMs)
            {
                Send(Protocol.Ping());
                lastPing = DateTime.UtcNow;
            }
            if ((DateTime.UtcNow - lastRx).TotalMilliseconds > TimeoutMs)
                throw new TimeoutException("keine Antwort vom Gerät");
            await SafeDelay(250, ct);
        }
    }

    private void HandleLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "act":
                {
                    var id = root.GetProperty("id").GetString();
                    if (!string.IsNullOrEmpty(id)) ActionReceived?.Invoke(id);
                    break;
                }
                // Konfiguration vom Gerät (auf cfg_get)
                case "cfg_begin":
                    _getBuf = new byte[root.GetProperty("size").GetInt32()];
                    _getGot = 0;
                    _getCrc = root.GetProperty("crc").GetUInt32();
                    break;
                case "cfg_data":
                {
                    var off = root.GetProperty("off").GetInt32();
                    var data = Convert.FromBase64String(root.GetProperty("d").GetString() ?? "");
                    if (_getBuf != null && off == _getGot && off + data.Length <= _getBuf.Length)
                    {
                        data.CopyTo(_getBuf, off);
                        _getGot += data.Length;
                    }
                    break;
                }
                case "cfg_end":
                    if (_getBuf != null && _getGot == _getBuf.Length && Crc32.Compute(_getBuf) == _getCrc)
                        _cfgGet?.TrySetResult(Encoding.UTF8.GetString(_getBuf));
                    else
                        _cfgGet?.TrySetException(new IOException("Übertragung vom Gerät unvollständig oder beschädigt"));
                    _getBuf = null;
                    break;
                // Antworten auf eine Konfiguration, die wir senden
                case "cfg_ack":
                    _cfgAck?.TrySetResult(root.GetProperty("off").GetInt32());
                    break;
                case "cfg_result":
                {
                    var ok = root.GetProperty("ok").GetBoolean();
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                    _cfgResult?.TrySetResult((ok, error));
                    if (!ok) _cfgAck?.TrySetException(new IOException(error ?? "Das Gerät hat die Konfiguration abgelehnt"));
                    break;
                }
                case "creds_result":
                {
                    var ok = root.GetProperty("ok").GetBoolean();
                    var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
                    _haResult?.TrySetResult((ok, error));
                    break;
                }
                // "pong" und "hello" brauchen nichts weiter: jede Nachricht zählt als Lebenszeichen
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Nachricht nicht lesbar: {ex.Message}");
        }
    }

    // ---- Konfiguration holen, senden, zurücksetzen --------------------------------------------

    private const int ChunkBytes = 450;                 // Rohbytes je Stück (Base64 ergibt 600 Zeichen)
    private const int MaxConfigBytes = 48 * 1024;

    private TaskCompletionSource<string>? _cfgGet;
    private byte[]? _getBuf;
    private int _getGot;
    private uint _getCrc;
    private TaskCompletionSource<int>? _cfgAck;
    private TaskCompletionSource<(bool Ok, string? Error)>? _cfgResult;

    private TaskCompletionSource<(bool Ok, string? Error)>? _haResult;

    /// <summary>Überträgt Zugangsdaten (WLAN, Home Assistant, MQTT) ans Gerät, das sie im Flash speichert. <paramref name="line"/> ist die creds_set-Nachricht.</summary>
    public async Task<(bool Ok, string? Error)> SendCredentialsAsync(string line, CancellationToken ct = default)
    {
        if (!IsConnected) return (false, "Kein Gerät verbunden");
        if (Encoding.UTF8.GetByteCount(line) > 1000) return (false, "Zugangsdaten zu lang");
        var tcs = new TaskCompletionSource<(bool Ok, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _haResult = tcs;
        Send(line);
        try { return await WaitAsync(tcs.Task, 10000, ct); }
        catch (TimeoutException ex) { return (false, ex.Message); }
    }

    private static async Task<T> WaitAsync<T>(Task<T> task, int timeoutMs, CancellationToken ct)
    {
        var winner = await Task.WhenAny(task, Task.Delay(timeoutMs, ct));
        if (winner != task) throw new TimeoutException("keine Antwort vom Gerät");
        return await task;
    }

    /// <summary>Holt die aktuelle Konfiguration (JSON) vom Gerät.</summary>
    public async Task<string> GetConfigAsync(CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Kein Gerät verbunden");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cfgGet = tcs;
        _getBuf = null;
        Send("{\"t\":\"cfg_get\"}");
        return await WaitAsync(tcs.Task, 8000, ct);
    }

    private async Task SendWaitAckAsync(string line, int expectedOffset, CancellationToken ct)
    {
        var ack = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cfgAck = ack;
        Send(line);
        var offset = await WaitAsync(ack.Task, 3000, ct);
        if (offset != expectedOffset) throw new IOException($"Unerwartete Bestätigung ({offset} statt {expectedOffset})");
    }

    /// <summary>
    /// Sendet eine Konfiguration (JSON) in Stücken mit Bestätigung je Stück. Das Gerät prüft sie, speichert sie
    /// und wendet sie an. Gibt Ok=false mit dem Fehlertext des Geräts zurück, wenn es sie ablehnt.
    /// </summary>
    public async Task<(bool Ok, string? Error)> SetConfigAsync(string json, CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Kein Gerät verbunden");
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxConfigBytes) return (false, $"Konfiguration zu gross ({bytes.Length} Bytes, höchstens {MaxConfigBytes})");

        var result = new TaskCompletionSource<(bool Ok, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cfgResult = result;
        try
        {
            await SendWaitAckAsync($"{{\"t\":\"cfg_begin\",\"size\":{bytes.Length},\"crc\":{Crc32.Compute(bytes)}}}", 0, ct);
            for (var offset = 0; offset < bytes.Length; offset += ChunkBytes)
            {
                var n = Math.Min(ChunkBytes, bytes.Length - offset);
                var b64 = Convert.ToBase64String(bytes, offset, n);
                await SendWaitAckAsync($"{{\"t\":\"cfg_data\",\"off\":{offset},\"d\":\"{b64}\"}}", offset + n, ct);
            }
            Send("{\"t\":\"cfg_end\"}");
            return await WaitAsync(result.Task, 15000, ct);
        }
        catch (IOException ex)
        {
            return (false, ex.Message);
        }
        catch (TimeoutException ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Setzt die Konfiguration des Geräts auf die eingebaute Standardkonfiguration zurück.</summary>
    public async Task<(bool Ok, string? Error)> ResetConfigAsync(CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("Kein Gerät verbunden");
        var result = new TaskCompletionSource<(bool Ok, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cfgResult = result;
        Send("{\"t\":\"cfg_reset\"}");
        try { return await WaitAsync(result.Task, 15000, ct); }
        catch (TimeoutException ex) { return (false, ex.Message); }
    }

    private static async Task SafeDelay(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct); }
        catch (OperationCanceledException) { /* beim Beenden */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _port?.Close(); } catch { /* ignorieren */ }
        _cts.Dispose();
    }
}
