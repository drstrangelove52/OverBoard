using System.Globalization;

namespace OverBoardAgent.Editor;

/// <summary>Formatiert einen Zustand für eine Anzeige-Kachel genau wie die Firmware (overboard/main/ui.c, format_value).</summary>
public static class ValueFormatter
{
    private static readonly Dictionary<string, (string De, string En)> Status = new()
    {
        ["ready"] = ("Bereit", "Ready"),
        ["inactive"] = ("Bereit", "Ready"),
        ["run"] = ("Läuft", "Running"),
        ["finished"] = ("Fertig", "Finished"),
        ["delayedstart"] = ("Wartet", "Waiting"),
        ["pause"] = ("Pause", "Paused"),
        ["error"] = ("Achtung", "Attention"),
        ["actionrequired"] = ("Achtung", "Attention"),
        ["aborting"] = ("Abbruch", "Aborting"),
    };

    private static float Num(string s) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    public static string Format(TileModel t, string? state, string language)
    {
        if (state == null || state is "unavailable" or "unknown") return "-";
        var en = language == "en";
        switch (t.EffectiveFormat)
        {
            case "power":
            {
                var w = Num(state);
                var lim = t.Max is > 0 ? (float)t.Max.Value : 20000f;
                if (w > lim || w < -lim) return "-";                      // unplausible Ausreisser
                if (w >= 1000f || w <= -1000f) return (w / 1000f).ToString("0.00", CultureInfo.InvariantCulture) + " kW";
                return ((int)(w + (w >= 0 ? 0.5f : -0.5f))).ToString(CultureInfo.InvariantCulture) + " W";
            }
            case "kwh":
                return Num(state).ToString("0.0", CultureInfo.InvariantCulture) + " kWh";
            case "percent":
                return ((int)(Num(state) + 0.5f)).ToString(CultureInfo.InvariantCulture) + " %";
            case "status":
                return Status.TryGetValue(state, out var s) ? (en ? s.En : s.De) : state;
            case "onoff":
                return state == "on" ? (en ? "On" : "An") : state == "off" ? (en ? "Off" : "Aus") : state;
            case "time":
                return TimeText(state);
            default:
                return state;
        }
    }

    /// <summary>ISO-Zeitstempel als Ortszeit: "21:39", an anderen Tagen mit Datum ("16.11. 06:30").</summary>
    private static string TimeText(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return iso;
        var local = t.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : $"{local.Day}.{local.Month}. {local:HH:mm}";
    }
}
