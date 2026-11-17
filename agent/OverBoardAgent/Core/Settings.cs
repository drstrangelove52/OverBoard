using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverBoardAgent.Core;

/// <summary>Einstellungen in %APPDATA%\OverBoardAgent\settings.json. Passwörter und Token werden mit Windows verschlüsselt (nur dieser Benutzer kann sie lesen).</summary>
public static class Settings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OverBoardAgent");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static string DataDir => Dir;

    /// <summary>"de" oder "en". Ohne Eintrag gilt die Sprache des Systems (Deutsch, sonst Englisch).</summary>
    public static string Language { get; set; } = "de";

    /// <summary>Adresse von Home Assistant, z. B. http://homeassistant.local:8123 (leer = nicht eingerichtet).</summary>
    public static string HaUrl { get; set; } = "";

    /// <summary>Langlebiges Zugriffstoken von Home Assistant.</summary>
    public static string HaToken { get; set; } = "";

    /// <summary>WLAN, mit dem sich das Gerät verbindet (leer = das Gerät behält seine bisherigen Zugangsdaten).</summary>
    public static string WifiSsid { get; set; } = "";
    public static string WifiPassword { get; set; } = "";

    /// <summary>MQTT-Broker für Benachrichtigungen: "host" oder "host:port" (leer = nicht verwendet).</summary>
    public static string MqttHost { get; set; } = "";
    public static string MqttUser { get; set; } = "";
    public static string MqttPassword { get; set; } = "";

    /// <summary>Wie viele Prozentpunkte die Lautstärke pro Tastendruck (Lauter/Leiser) ändert.</summary>
    public static int VolumeStep { get; set; } = 5;

    /// <summary>Täglich (höchstens einmal) auf GitHub nach einer neuen Version suchen.</summary>
    public static bool UpdateCheck { get; set; } = true;
    public static long LastUpdateCheckUtc { get; set; }

    /// <summary>Letzte Grösse des Hauptfensters (0 = noch nicht gespeichert).</summary>
    public static double WindowWidth { get; set; }
    public static double WindowHeight { get; set; }

    /// <summary>Geheime Adresse eines Kalenders im iCal-Format (Google Kalender: Einstellungen, "Kalender integrieren"). Gilt wie ein Passwort.</summary>
    public static string CalendarUrl { get; set; } = "";

    /// <summary>Noch nichts eingerichtet (erster Start): die Einstellungen öffnen sich dann von selbst.</summary>
    public static bool IsEmpty => HaUrl.Length == 0 && HaToken.Length == 0 && WifiSsid.Length == 0 && MqttHost.Length == 0;

    private static string Protect(string plain) =>
        plain.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string stored)
    {
        if (stored.Length == 0) return "";
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
        catch (Exception ex) { Log.Warn($"Gespeichertes Geheimnis nicht lesbar: {ex.Message}"); return ""; }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static void Load()
    {
        Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var r = doc.RootElement;
            if (Str(r, "language") is "de" or "en") Language = Str(r, "language");
            HaUrl = Str(r, "ha_url");
            HaToken = Unprotect(Str(r, "ha_token"));
            WifiSsid = Str(r, "wifi_ssid");
            WifiPassword = Unprotect(Str(r, "wifi_pass"));
            MqttHost = Str(r, "mqtt_host");
            MqttUser = Str(r, "mqtt_user");
            MqttPassword = Unprotect(Str(r, "mqtt_pass"));
            CalendarUrl = Unprotect(Str(r, "calendar_url"));
            if (r.TryGetProperty("volume_step", out var vs) && vs.ValueKind == JsonValueKind.Number) VolumeStep = Math.Clamp(vs.GetInt32(), 1, 20);
            if (r.TryGetProperty("window_w", out var ww) && ww.ValueKind == JsonValueKind.Number) WindowWidth = ww.GetDouble();
            if (r.TryGetProperty("window_h", out var wh) && wh.ValueKind == JsonValueKind.Number) WindowHeight = wh.GetDouble();
            if (r.TryGetProperty("update_check", out var uc) && uc.ValueKind is JsonValueKind.True or JsonValueKind.False) UpdateCheck = uc.GetBoolean();
            if (r.TryGetProperty("last_update_check", out var lu) && lu.ValueKind == JsonValueKind.Number) LastUpdateCheckUtc = lu.GetInt64();
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json nicht lesbar: {ex.Message}");
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new
            {
                language = Language,
                ha_url = HaUrl,
                ha_token = Protect(HaToken),
                wifi_ssid = WifiSsid,
                wifi_pass = Protect(WifiPassword),
                mqtt_host = MqttHost,
                mqtt_user = MqttUser,
                mqtt_pass = Protect(MqttPassword),
                calendar_url = Protect(CalendarUrl),
                volume_step = VolumeStep,
                window_w = WindowWidth,
                window_h = WindowHeight,
                update_check = UpdateCheck,
                last_update_check = LastUpdateCheckUtc,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn($"settings.json nicht speicherbar: {ex.Message}");
        }
    }
}
