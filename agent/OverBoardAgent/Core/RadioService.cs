using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace OverBoardAgent.Core;

/// <summary>Ein Radiosender: Name, Adresse des Streams und (optional) Adresse des Senderlogos.</summary>
public sealed record Station(string Name, string Url, string? Logo);

/// <summary>
/// Radio-Player des Programms: spielt Streams (MP3, AAC) über die Lautsprecher des PCs ab, liest den laufenden Titel aus dem Stream
/// (ICY-Metadaten) und meldet Sender, Titel und Senderlogo ans Gerät. Die Senderliste liegt in %APPDATA%\OverBoardAgent\radio.json.
/// </summary>
public sealed class RadioService : IDisposable
{
    private readonly string _file = Path.Combine(Settings.DataDir, "radio.json");
    private readonly object _gate = new();
    private readonly List<Station> _stations = [];
    private int _current;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private string? _logoSentFor;

    /// <summary>Sendet eine Zeile ans Gerät (vom AgentCore gesetzt).</summary>
    public Action<string>? Send { get; set; }

    /// <summary>Hintergrundfarbe der Kachel für das Logo (Dunkel/Hell), damit transparente Logos passen.</summary>
    public string Theme { get; set; } = "dark";

    public bool Playing { get; private set; }
    public string Title { get; private set; } = "";
    public string? Error { get; private set; }

    /// <summary>Liste, Auswahl oder Zustand haben sich geändert (kann auf einem beliebigen Thread kommen).</summary>
    public event Action? Changed;

    public RadioService()
    {
        Load();
    }

    public IReadOnlyList<Station> Stations
    {
        get { lock (_gate) return _stations.ToList(); }
    }

    public int CurrentIndex
    {
        get { lock (_gate) return _current; }
    }

    public Station? CurrentStation
    {
        get { lock (_gate) return _current >= 0 && _current < _stations.Count ? _stations[_current] : null; }
    }

    // ---- Senderliste -------------------------------------------------------------------------

    private sealed class FileModel
    {
        public int Current { get; set; }
        public List<StationModel> Stations { get; set; } = [];
    }

    private sealed class StationModel
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string? Logo { get; set; }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_file));
            if (model == null) return;
            lock (_gate)
            {
                _stations.Clear();
                foreach (var s in model.Stations)
                    if (s.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) _stations.Add(new Station(s.Name, s.Url, s.Logo));
                _current = Math.Clamp(model.Current, 0, Math.Max(0, _stations.Count - 1));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"radio.json nicht lesbar: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            FileModel model;
            lock (_gate)
            {
                model = new FileModel { Current = _current, Stations = _stations.Select(s => new StationModel { Name = s.Name, Url = s.Url, Logo = s.Logo }).ToList() };
            }
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(_file, JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"radio.json nicht speicherbar: {ex.Message}");
        }
    }

    public void Add(Station station)
    {
        lock (_gate)
        {
            if (_stations.Any(s => string.Equals(s.Url, station.Url, StringComparison.OrdinalIgnoreCase))) return;
            _stations.Add(station);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Ändert Name, Adresse oder Logo eines Senders. Läuft er gerade und die Adresse ändert sich, startet er neu.</summary>
    public void Update(int index, Station station)
    {
        bool restart;
        lock (_gate)
        {
            if (index < 0 || index >= _stations.Count) return;
            restart = Playing && index == _current && !string.Equals(_stations[index].Url, station.Url, StringComparison.Ordinal);
            _stations[index] = station;
        }
        Save();
        Changed?.Invoke();
        if (restart) Play(index);
        else if (index == CurrentIndex) PushToDevice();
    }

    public void Remove(int index)
    {
        var wasCurrent = false;
        lock (_gate)
        {
            if (index < 0 || index >= _stations.Count) return;
            wasCurrent = index == _current;
            _stations.RemoveAt(index);
            if (_current > index || _current >= _stations.Count) _current = Math.Max(0, _current - 1);
        }
        if (wasCurrent) Stop();
        Save();
        Changed?.Invoke();
        PushToDevice();
    }

    public void Move(int index, int delta)
    {
        lock (_gate)
        {
            var to = index + delta;
            if (index < 0 || index >= _stations.Count || to < 0 || to >= _stations.Count) return;
            var s = _stations[index];
            _stations.RemoveAt(index);
            _stations.Insert(to, s);
            if (_current == index) _current = to;
            else if (_current == to) _current = index;
        }
        Save();
        Changed?.Invoke();
    }

    // ---- Wiedergabe --------------------------------------------------------------------------

    /// <summary>Spielt den Sender mit diesem Index ab.</summary>
    public void Play(int index)
    {
        Station? st;
        lock (_gate)
        {
            if (index < 0 || index >= _stations.Count) return;
            _current = index;
            st = _stations[index];
        }
        Save();
        StopInternal();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Error = null;
        Title = "";
        Playing = true;                 // gleich als "läuft" melden: das Verbinden kann einige Sekunden dauern
        Changed?.Invoke();
        PushToDevice();
        _ = Task.Run(() => PlayLoopAsync(st, cts.Token));
    }

    public void Stop()
    {
        StopInternal();
        Playing = false;
        Title = "";
        Changed?.Invoke();
        PushToDevice();
    }

    private void StopInternal()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { /* egal */ }
    }

    public void Toggle()
    {
        if (Playing) Stop();
        else Play(CurrentIndex);
    }

    public void Next() => Step(+1);
    public void Previous() => Step(-1);

    private void Step(int delta)
    {
        int count, idx;
        lock (_gate)
        {
            count = _stations.Count;
            if (count == 0) return;
            _current = ((_current + delta) % count + count) % count;
            idx = _current;
        }
        if (Playing) Play(idx);
        else
        {
            Save();
            Changed?.Invoke();
            PushToDevice();
        }
    }

    private async Task PlayLoopAsync(Station station, CancellationToken ct)
    {
        var attempts = 0;
        while (!ct.IsCancellationRequested && attempts < 4)
        {
            try
            {
                // Die Audiogeräte (COM) gehören dem Thread, der sie erzeugt: alles hier auf diesem einen Thread
                using var reader = new MediaFoundationReader(station.Url);
                using var output = new WasapiOut(AudioClientShareMode.Shared, 300);
                output.Init(reader);
                output.Play();
                Log.Info($"Radio: {station.Name} läuft");
                _ = Task.Run(() => IcyLoopAsync(station, ct), ct);
                var started = DateTime.UtcNow;
                while (!ct.IsCancellationRequested && output.PlaybackState != PlaybackState.Stopped)
                    await Task.Delay(300, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && DateTime.UtcNow - started > TimeSpan.FromSeconds(20)) attempts = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"Radio '{station.Name}': {ex.Message}");
                Error = ex.Message;
            }
            attempts++;
            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
        if (!ct.IsCancellationRequested)       // aufgegeben: als gestoppt melden
        {
            Playing = false;
            Title = "";
            Changed?.Invoke();
            PushToDevice();
        }
    }

    // ---- Titel aus dem Stream (ICY) ----------------------------------------------------------

    private static readonly Regex StreamTitle = new(@"StreamTitle='(.*?)';", RegexOptions.Singleline | RegexOptions.Compiled);

    private async Task IcyLoopAsync(Station station, CancellationToken ct)
    {
        var url = station.Url;
        try
        {
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            request.Headers.TryAddWithoutValidation("User-Agent", "OverBoard");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.Headers.TryGetValues("icy-metaint", out var values) || !int.TryParse(values.FirstOrDefault(), out var interval) || interval <= 0)
                return;                                   // der Sender liefert keine Titel
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var audio = new byte[interval];
            while (!ct.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(audio, ct).ConfigureAwait(false);
                var length = stream.ReadByte() * 16;
                if (length < 0) return;
                if (length == 0) continue;
                var meta = new byte[length];
                await stream.ReadExactlyAsync(meta, ct).ConfigureAwait(false);
                var match = StreamTitle.Match(DecodeMeta(meta));
                if (!match.Success) continue;
                var title = match.Groups[1].Value.Trim();
                // Manche Sender melden zwischen den Liedern nur ihren Namen: das ist kein Titel
                if (title.Length > 0 && (station.Name.Contains(title, StringComparison.OrdinalIgnoreCase) || title.Contains(station.Name, StringComparison.OrdinalIgnoreCase))) title = "";
                if (title != Title)
                {
                    Title = title;
                    Log.Info($"Radio: Titel '{title}'");
                    Changed?.Invoke();
                    PushToDevice();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"Radio-Titel: {ex.Message}");
        }
    }

    private static string DecodeMeta(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes).TrimEnd('\0'); }
    }

    // ---- Gerät -------------------------------------------------------------------------------

    /// <summary>Nach einer neuen Verbindung zum Gerät: Zustand und Logo neu senden.</summary>
    public void OnDeviceConnected()
    {
        _logoSentFor = null;
        PushToDevice();
    }

    /// <summary>Meldet Sender, Titel und Zustand (und bei Bedarf das Logo) ans Gerät.</summary>
    public void PushToDevice()
    {
        var send = Send;
        if (send == null) return;
        var st = CurrentStation;
        send(Protocol.Radio(st?.Name ?? "", Title, Playing));
        _ = Task.Run(async () =>
        {
            try { await PushLogoAsync(st).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"Radio-Logo: {ex.Message}"); }
        });
    }

    private async Task PushLogoAsync(Station? st)
    {
        var key = st == null ? "" : st.Name + "|" + st.Logo + "|" + Theme;
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_logoSentFor == key) return;
            var send = Send;
            if (send == null) return;
            var pixels = st?.Logo != null ? await RadioLogo.LoadAsync(st.Logo, Theme).ConfigureAwait(false) : null;
            if (pixels == null)
            {
                send(Protocol.RadioImageClear());
            }
            else
            {
                // In Stücken, mit kleinen Pausen: das Gerät nimmt nur wenige Zeilen gleichzeitig an
                var parts = 0;
                foreach (var line in Protocol.RadioImage(pixels, RadioLogo.Size))
                {
                    send(line);
                    parts++;
                    await Task.Delay(15).ConfigureAwait(false);
                }
                Log.Info($"Radio-Logo gesendet ({parts} Teile)");
            }
            _logoSentFor = key;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public void Dispose()
    {
        StopInternal();
    }
}
