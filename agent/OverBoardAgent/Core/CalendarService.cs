using System.Globalization;
using System.Text;

namespace OverBoardAgent.Core;

/// <summary>Ein Termin für die Anzeige: Tag, Uhrzeit (leer = ganztägig) und Titel. Alles in Ortszeit des PCs.</summary>
public sealed record CalLine(string Date, string Time, string Title);

/// <summary>
/// Liest einen Kalender aus der "geheimen Adresse im iCal-Format" (Google Kalender, Nextcloud usw.) und berechnet die nächsten Termine,
/// auch aus Wiederholungen (täglich, wöchentlich, monatlich, jährlich) mit Ausnahmen und verschobenen Einzelterminen.
/// </summary>
public static class CalendarService
{
    public const int MaxLines = 8;
    public const int TitleChars = 30;
    private const int MaxMessageBytes = 880;     // das Gerät nimmt Zeilen bis 1024 Byte an

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private sealed record Ev(string Uid, DateTime Start, DateTime End, bool AllDay, string Title);

    /// <summary>Lädt den Kalender und liefert die nächsten Termine ab jetzt.</summary>
    public static async Task<(List<CalLine>? Lines, string? Error)> FetchAsync(string url, CancellationToken ct = default)
    {
        url = url.Trim();
        if (url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[9..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return (null, Strings.Get("cal.err.url"));
        try
        {
            var text = await Http.GetStringAsync(uri, ct);
            if (!text.Contains("BEGIN:VCALENDAR", StringComparison.Ordinal)) return (null, Strings.Get("cal.err.notical"));
            return (Upcoming(text, DateTime.Now), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, Strings.Format("cal.err.fetch", ex.Message));
        }
    }

    /// <summary>Die nächsten Termine ab <paramref name="now"/> (laufende und ganztägige von heute eingeschlossen).</summary>
    public static List<CalLine> Upcoming(string ics, DateTime now)
    {
        var today = now.Date;
        var events = Parse(ics, today, today.AddDays(60), now);
        var lines = new List<(DateTime Day, bool AllDay, string Time, string Title)>();
        foreach (var e in events)
        {
            var carriesOver = e.AllDay || e.Start.Date < today;
            var day = e.Start.Date < today ? today : e.Start.Date;
            var time = carriesOver ? "" : e.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
            lines.Add((day, carriesOver, time, e.Title));
        }
        var ordered = lines.OrderBy(l => l.Day).ThenByDescending(l => l.AllDay).ThenBy(l => l.Time, StringComparer.Ordinal).Take(MaxLines);
        return ordered.Select(l => new CalLine(l.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), l.Time, Truncate(l.Title))).ToList();
    }

    private static string Truncate(string s)
    {
        s = s.Trim();
        var e = StringInfo.GetTextElementEnumerator(s);
        var sb = new StringBuilder();
        var n = 0;
        while (e.MoveNext())
        {
            if (n == TitleChars) return sb.ToString().TrimEnd() + "…";
            sb.Append(e.GetTextElement());
            n++;
        }
        return s;
    }

    /// <summary>Die Nachricht ans Gerät. Wird bei Bedarf gekürzt, damit sie in eine Zeile passt.</summary>
    public static string ToMessage(IReadOnlyList<CalLine> lines)
    {
        var options = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        for (var n = lines.Count; n >= 0; n--)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new { t = "cal", e = lines.Take(n).Select(l => new[] { l.Date, l.Time, l.Title }) }, options);
            if (Encoding.UTF8.GetByteCount(json) <= MaxMessageBytes) return json;
        }
        return "{\"t\":\"cal\",\"e\":[]}";
    }

    /// <summary>Beschriftung des Tages für die Vorschau (gleich wie auf dem Gerät): "Heute 18:30", "Morgen", "Mi 18.11. 09:00".</summary>
    public static string When(CalLine l, string language, DateTime today)
    {
        var de = language != "en";
        if (!DateTime.TryParseExact(l.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return l.Date;
        string day;
        if (d == today) day = de ? "Heute" : "Today";
        else if (d == today.AddDays(1)) day = de ? "Morgen" : "Tomorrow";
        else
        {
            string[] dede = ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"], en = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
            day = $"{(de ? dede : en)[(int)d.DayOfWeek]} {d.Day}.{d.Month}.";
        }
        return l.Time.Length > 0 ? $"{day} {l.Time}" : day;
    }

    // ---- iCal lesen ---------------------------------------------------------------------------

    private sealed class Prop
    {
        public string Name = "";
        public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
        public string Value = "";
    }

    private static List<List<Prop>> ReadEvents(string ics)
    {
        // Zeilen entfalten (Fortsetzungszeilen beginnen mit Leerzeichen oder Tab)
        var raw = ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>();
        foreach (var l in raw)
        {
            if ((l.StartsWith(' ') || l.StartsWith('\t')) && lines.Count > 0) lines[^1] += l[1..];
            else lines.Add(l);
        }

        var result = new List<List<Prop>>();
        List<Prop>? cur = null;
        foreach (var l in lines)
        {
            if (l.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = []; continue; }
            if (l.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (cur != null) result.Add(cur); cur = null; continue; }
            if (cur == null) continue;

            // Name;PARAM=wert;PARAM="wert":Wert  (Doppelpunkte in Anführungszeichen zählen nicht)
            var colon = -1;
            var quoted = false;
            for (var i = 0; i < l.Length; i++)
            {
                if (l[i] == '"') quoted = !quoted;
                else if (l[i] == ':' && !quoted) { colon = i; break; }
            }
            if (colon <= 0) continue;
            var head = l[..colon].Split(';');
            var p = new Prop { Name = head[0].ToUpperInvariant(), Value = l[(colon + 1)..] };
            foreach (var h in head.Skip(1))
            {
                var eq = h.IndexOf('=');
                if (eq > 0) p.Params[h[..eq]] = h[(eq + 1)..].Trim('"');
            }
            cur.Add(p);
        }
        return result;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    /// <summary>Datum oder Zeitpunkt aus iCal. wall = Uhrzeit in der Zeitzone des Termins, utc = ob "Z".</summary>
    private static bool TryDate(Prop p, out DateTime wall, out bool allDay, out TimeZoneInfo? tz, out bool utc)
    {
        wall = default; allDay = false; tz = null; utc = false;
        var v = p.Value.Trim();
        if (p.Params.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase) || v.Length == 8)
        {
            allDay = true;
            return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out wall);
        }
        utc = v.EndsWith('Z');
        var fmt = utc ? "yyyyMMdd'T'HHmmss'Z'" : "yyyyMMdd'T'HHmmss";
        if (!DateTime.TryParseExact(v, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out wall)) return false;
        if (!utc && p.Params.TryGetValue("TZID", out var id))
        {
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { tz = null; }        // unbekannte Zeitzone: als Ortszeit behandeln
        }
        return true;
    }

    private static DateTime ToLocal(DateTime wall, bool allDay, TimeZoneInfo? tz, bool utc)
    {
        if (allDay) return wall.Date;
        try
        {
            if (utc) return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(wall, DateTimeKind.Utc), TimeZoneInfo.Local);
            if (tz != null && !tz.Equals(TimeZoneInfo.Local))
            {
                if (tz.IsInvalidTime(wall)) wall = wall.AddHours(1);
                return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), tz, TimeZoneInfo.Local);
            }
        }
        catch { /* Wert unverändert verwenden */ }
        return wall;
    }

    private static TimeSpan? ParseDuration(string v)
    {
        // P1DT2H30M, PT45M, P2W
        var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return null;
        static int N(System.Text.RegularExpressions.Group g) => g.Success ? int.Parse(g.Value) : 0;
        var ts = TimeSpan.FromDays(N(m.Groups[2]) * 7 + N(m.Groups[3])) + TimeSpan.FromHours(N(m.Groups[4])) + TimeSpan.FromMinutes(N(m.Groups[5])) + TimeSpan.FromSeconds(N(m.Groups[6]));
        return m.Groups[1].Value == "-" ? -ts : ts;
    }

    /// <summary>Alle Vorkommen, die zwischen from und to liegen oder noch laufen (nach now).</summary>
    private static List<Ev> Parse(string ics, DateTime from, DateTime to, DateTime now)
    {
        var props = ReadEvents(ics);
        var overrides = new HashSet<(string Uid, DateTime Orig)>();
        var singles = new List<Ev>();
        var masters = new List<(List<Prop> P, string Uid)>();

        foreach (var ev in props)
        {
            string? Get(string n) => ev.FirstOrDefault(p => p.Name == n)?.Value;
            if (string.Equals(Get("STATUS"), "CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            var uid = Get("UID") ?? Guid.NewGuid().ToString();
            var rid = ev.FirstOrDefault(p => p.Name == "RECURRENCE-ID");
            if (rid != null && TryDate(rid, out var rw, out var rad, out var rtz, out var ru))
                overrides.Add((uid, ToLocal(rw, rad, rtz, ru)));       // dieses Vorkommen der Serie wurde verschoben oder geändert
            if (ev.Any(p => p.Name == "RRULE") && rid == null) masters.Add((ev, uid));
            else if (Single(ev, uid) is { } s) singles.Add(s);
        }

        var result = new List<Ev>();
        foreach (var s in singles) if (InRange(s, from, to, now)) result.Add(s);
        foreach (var (p, uid) in masters)
            foreach (var o in Expand(p, uid, from, to))
                if (!overrides.Contains((uid, o.Start)) && InRange(o, from, to, now)) result.Add(o);
        return result;
    }

    private static bool InRange(Ev e, DateTime from, DateTime to, DateTime now) =>
        e.Start < to && (e.End > now || e.Start >= now);

    private static Ev? Single(List<Prop> ev, string uid)
    {
        var ds = ev.FirstOrDefault(p => p.Name == "DTSTART");
        if (ds == null || !TryDate(ds, out var wall, out var allDay, out var tz, out var utc)) return null;
        var start = ToLocal(wall, allDay, tz, utc);
        var title = Unescape(ev.FirstOrDefault(p => p.Name == "SUMMARY")?.Value ?? "");
        return new Ev(uid, start, EndOf(ev, start, wall, allDay, tz, utc), allDay, title);
    }

    private static DateTime EndOf(List<Prop> ev, DateTime startLocal, DateTime startWall, bool allDay, TimeZoneInfo? tz, bool utc)
    {
        var de = ev.FirstOrDefault(p => p.Name == "DTEND");
        if (de != null && TryDate(de, out var ew, out var ead, out var etz, out var eu)) return ToLocal(ew, ead, etz, eu);
        var dur = ev.FirstOrDefault(p => p.Name == "DURATION");
        if (dur != null && ParseDuration(dur.Value) is { } span) return startLocal + span;
        return allDay ? startLocal.AddDays(1) : startLocal;
    }

    // ---- Wiederholungen -----------------------------------------------------------------------

    private static IEnumerable<Ev> Expand(List<Prop> ev, string uid, DateTime from, DateTime to)
    {
        var ds = ev.FirstOrDefault(p => p.Name == "DTSTART");
        if (ds == null || !TryDate(ds, out var wall, out var allDay, out var tz, out var utc)) yield break;
        var title = Unescape(ev.FirstOrDefault(p => p.Name == "SUMMARY")?.Value ?? "");
        var firstLocal = ToLocal(wall, allDay, tz, utc);
        var length = EndOf(ev, firstLocal, wall, allDay, tz, utc) - firstLocal;

        var rule = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in ev.First(p => p.Name == "RRULE").Value.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) rule[part[..eq]] = part[(eq + 1)..];
        }

        var excluded = new HashSet<DateTime>();
        foreach (var x in ev.Where(p => p.Name == "EXDATE"))
            foreach (var one in x.Value.Split(','))
                if (TryDate(new Prop { Name = "EXDATE", Params = x.Params, Value = one }, out var xw, out var xad, out var xtz, out var xu))
                    excluded.Add(ToLocal(xw, xad, xtz, xu));

        // Das Vorkommen in der Zeit des Termins (Sommerzeit bleibt so richtig), dann in Ortszeit
        var until = DateTime.MaxValue;
        if (rule.TryGetValue("UNTIL", out var u))
        {
            if (TryDate(new Prop { Name = "UNTIL", Params = [], Value = u }, out var uw, out var uad, out _, out var uu))
                until = uad ? uw.AddDays(1).AddTicks(-1) : (uu ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(uw, DateTimeKind.Utc), tz ?? TimeZoneInfo.Utc) : uw);
        }
        var count = rule.TryGetValue("COUNT", out var cs) && int.TryParse(cs, out var cn) ? cn : int.MaxValue;
        var interval = rule.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var ivn) && ivn > 0 ? ivn : 1;
        var freq = rule.GetValueOrDefault("FREQ", "").ToUpperInvariant();

        var emitted = 0;
        foreach (var w in Candidates(wall, freq, interval, rule, count, until, to.AddDays(2), from.AddDays(-2) - length))
        {
            emitted++;
            var local = ToLocal(w, allDay, tz, utc);
            if (excluded.Contains(local)) continue;
            yield return new Ev(uid, local, local + length, allDay, title);
            if (emitted > 5000) yield break;
        }
    }

    private static readonly string[] Days = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>Alle Vorkommen (Zeit des Termins), aufsteigend, ab dem Start bis <paramref name="end"/>. Frühere als <paramref name="skipBefore"/> werden nicht ausgegeben.</summary>
    private static IEnumerable<DateTime> Candidates(DateTime start, string freq, int interval, Dictionary<string, string> rule, int count, DateTime until, DateTime end, DateTime skipBefore)
    {
        var byDay = rule.TryGetValue("BYDAY", out var bd) ? bd.Split(',').Select(s => s.Trim().ToUpperInvariant()).ToList() : [];
        var byMonthDay = rule.TryGetValue("BYMONTHDAY", out var bmd) ? bmd.Split(',').Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n != 0).ToList() : [];
        var byMonth = rule.TryGetValue("BYMONTH", out var bm) ? bm.Split(',').Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n is >= 1 and <= 12).ToList() : [];
        var tod = start.TimeOfDay;
        var n = 0;

        bool Take(DateTime d, out bool stop)
        {
            stop = false;
            if (d < start) return false;
            if (d > until || d > end || n >= count) { stop = true; return false; }
            n++;
            return d >= skipBefore;
        }

        // Ohne COUNT darf man alte Zeiträume überspringen (sonst Zählung von Anfang an)
        long Skip(long periodsPerUnit, double daysPerPeriod)
        {
            if (count != int.MaxValue) return 0;
            var days = (skipBefore - start).TotalDays;
            return days <= 0 ? 0 : Math.Max(0, (long)(days / (daysPerPeriod * interval)) - 1);
        }

        switch (freq)
        {
            case "DAILY":
                for (var k = Skip(1, 1); ; k++)
                {
                    var d = start.AddDays(k * interval);
                    if (byDay.Count > 0 && !byDay.Any(x => x.EndsWith(Days[(int)d.DayOfWeek]))) { if (d > end || d > until) yield break; continue; }
                    if (Take(d, out var stop)) yield return d;
                    if (stop) yield break;
                }
            case "WEEKLY":
            {
                var wd = byDay.Count > 0 ? byDay.Select(x => Array.IndexOf(Days, x[^2..])).Where(i => i >= 0).Distinct().ToList() : [(int)start.DayOfWeek];
                var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));   // Montag
                for (var k = Skip(1, 7); ; k++)
                {
                    var monday = weekStart.AddDays(k * 7L * interval);
                    if (monday > end || monday > until) yield break;
                    foreach (var d in wd.Select(i => monday.AddDays((i + 6) % 7) + tod).OrderBy(x => x))
                    {
                        if (Take(d, out var stop)) yield return d;
                        if (stop) yield break;
                    }
                }
            }
            case "MONTHLY":
                for (var k = Skip(1, 28); ; k++)
                {
                    var first = new DateTime(start.Year, start.Month, 1).AddMonths((int)(k * interval));
                    if (first > end || first > until) yield break;
                    foreach (var d in MonthDays(first, start, byDay, byMonthDay).Select(x => x + tod).OrderBy(x => x))
                    {
                        if (Take(d, out var stop)) yield return d;
                        if (stop) yield break;
                    }
                }
            case "YEARLY":
                for (var k = Skip(1, 365); ; k++)
                {
                    var year = start.Year + (int)(k * interval);
                    if (year > end.Year + 1 || new DateTime(Math.Min(year, 9999), 1, 1) > until) yield break;
                    var months = byMonth.Count > 0 ? byMonth : [start.Month];
                    foreach (var d in months.SelectMany(m => MonthDays(new DateTime(year, m, 1), start, byDay, byMonthDay)).Select(x => x + tod).OrderBy(x => x))
                    {
                        if (Take(d, out var stop)) yield return d;
                        if (stop) yield break;
                    }
                }
            default:
                // unbekannte Wiederholung: nur der erste Termin
                if (Take(start, out _)) yield return start;
                yield break;
        }
    }

    /// <summary>Tage eines Monats nach BYMONTHDAY oder BYDAY (auch "1MO", "-1FR"); sonst der Tag des Starts.</summary>
    private static IEnumerable<DateTime> MonthDays(DateTime first, DateTime start, List<string> byDay, List<int> byMonthDay)
    {
        var dim = DateTime.DaysInMonth(first.Year, first.Month);
        var result = new SortedSet<DateTime>();
        foreach (var md in byMonthDay)
        {
            var d = md > 0 ? md : dim + md + 1;
            if (d >= 1 && d <= dim) result.Add(first.AddDays(d - 1));
        }
        foreach (var token in byDay)
        {
            var dayIdx = Array.IndexOf(Days, token[^2..]);
            if (dayIdx < 0) continue;
            var ord = token.Length > 2 && int.TryParse(token[..^2], out var o) ? o : 0;
            var all = Enumerable.Range(0, dim).Select(i => first.AddDays(i)).Where(d => (int)d.DayOfWeek == dayIdx).ToList();
            if (ord == 0) { foreach (var d in all) result.Add(d); }
            else
            {
                var idx = ord > 0 ? ord - 1 : all.Count + ord;
                if (idx >= 0 && idx < all.Count) result.Add(all[idx]);
            }
        }
        if (byMonthDay.Count == 0 && byDay.Count == 0 && start.Day <= dim) result.Add(first.AddDays(start.Day - 1));
        return result;
    }
}
