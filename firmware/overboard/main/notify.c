#include "notify.h"

#include <string.h>

#include "audio.h"
#include "cJSON.h"
#include "creds.h"
#include "esp_log.h"
#include "mqtt_client.h"
#include "ui.h"

static const char *TAG = "notify";

#define TOPIC_NOTIFY "overboard/notify"
#define TOPIC_STATUS "overboard/status"

static esp_mqtt_client_handle_t s_mqtt;
static char s_mq_uri[128], s_mq_user[64], s_mq_pass[128];

static void mq_load(void)
{
    snprintf(s_mq_uri, sizeof(s_mq_uri), "%s", CONFIG_OB_MQTT_URI);
    snprintf(s_mq_user, sizeof(s_mq_user), "%s", CONFIG_OB_MQTT_USER);
    snprintf(s_mq_pass, sizeof(s_mq_pass), "%s", CONFIG_OB_MQTT_PASSWORD);
    char u[sizeof(s_mq_uri)];
    if (creds_get("mqtt_uri", u, sizeof(u))) {
        memcpy(s_mq_uri, u, sizeof(u));
        if (!creds_get("mqtt_user", s_mq_user, sizeof(s_mq_user))) s_mq_user[0] = 0;
        if (!creds_get("mqtt_pass", s_mq_pass, sizeof(s_mq_pass))) s_mq_pass[0] = 0;
        ESP_LOGI(TAG, "MQTT-Zugangsdaten aus dem Flash");
    }
}

// Text fuer die Anzeige bereinigen: ASCII und Latin-1 (Umlaute, Akzente) bleiben, alles andere wird zu '?'.
// Ungueltiges UTF-8 und Zeichen ausserhalb von U+00FF kennt die Schrift nicht.
static void sanitize_text(const char *in, char *out, size_t cap)
{
    size_t o = 0;
    for (size_t i = 0; in[i] && o + 3 < cap;) {
        unsigned char c = (unsigned char)in[i];
        if (c < 0x80) {
            out[o++] = (char)c;
            i++;
        } else if ((c == 0xC2 || c == 0xC3) && ((unsigned char)in[i + 1] & 0xC0) == 0x80) {
            out[o++] = (char)c;               // U+0080..U+00FF als 2 Byte UTF-8 uebernehmen
            out[o++] = in[i + 1];
            i += 2;
        } else {
            size_t len = c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : 2;
            out[o++] = '?';
            for (size_t k = 0; k < len && in[i]; k++) i++;
        }
    }
    out[o] = 0;
}

static banner_level_t parse_level(const char *s)
{
    if (s && !strcmp(s, "ok")) return BANNER_OK;
    if (s && !strcmp(s, "warn")) return BANNER_WARN;
    if (s && !strcmp(s, "alarm")) return BANNER_ALARM;
    return BANNER_INFO;
}

static const char *default_sound(banner_level_t l)
{
    switch (l) {
    case BANNER_OK: return "ok";
    case BANNER_WARN: return "warn";
    case BANNER_ALARM: return "alarm";
    default: return "info";
    }
}

static void handle_notify(const char *data, int len)
{
    cJSON *root = cJSON_ParseWithLength(data, len);
    if (!root) {
        ESP_LOGW(TAG, "Nachricht ist kein gueltiges JSON");
        return;
    }
    const cJSON *title = cJSON_GetObjectItem(root, "title");
    const cJSON *text = cJSON_GetObjectItem(root, "text");
    const cJSON *level = cJSON_GetObjectItem(root, "level");
    const cJSON *sound = cJSON_GetObjectItem(root, "sound");
    const cJSON *dur = cJSON_GetObjectItem(root, "duration");
    const cJSON *vol = cJSON_GetObjectItem(root, "volume");

    banner_level_t lv = parse_level(cJSON_IsString(level) ? level->valuestring : NULL);
    int duration = cJSON_IsNumber(dur) ? dur->valueint : (lv == BANNER_ALARM ? 0 : 8);

    char t[96] = "", x[160] = "";
    if (cJSON_IsString(title)) sanitize_text(title->valuestring, t, sizeof(t));
    if (cJSON_IsString(text)) sanitize_text(text->valuestring, x, sizeof(x));
    if (!t[0] && !x[0]) {
        ESP_LOGW(TAG, "Nachricht ohne title und text verworfen");
        cJSON_Delete(root);
        return;
    }
    ESP_LOGI(TAG, "Banner: [%s] %s / %s", level && cJSON_IsString(level) ? level->valuestring : "info", t, x);
    ui_show_banner(t, x, lv, duration);

    const char *snd = cJSON_IsString(sound) ? sound->valuestring : default_sound(lv);
    audio_play(snd, cJSON_IsNumber(vol) ? vol->valueint : -1);
    cJSON_Delete(root);
}

static void on_mqtt(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    esp_mqtt_event_handle_t e = data;
    switch ((esp_mqtt_event_id_t)id) {
    case MQTT_EVENT_CONNECTED:
        ESP_LOGI(TAG, "MQTT verbunden");
        ui_set_mqtt(true);
        esp_mqtt_client_subscribe(s_mqtt, TOPIC_NOTIFY, 1);
        esp_mqtt_client_publish(s_mqtt, TOPIC_STATUS, "online", 0, 1, 1);
        break;
    case MQTT_EVENT_DATA:
        // Nur vollstaendige Einzelnachrichten (Benachrichtigungen sind klein)
        if (e->current_data_offset == 0 && e->data_len == e->total_data_len &&
            e->topic_len == (int)strlen(TOPIC_NOTIFY) && !strncmp(e->topic, TOPIC_NOTIFY, e->topic_len)) {
            handle_notify(e->data, e->data_len);
        }
        break;
    case MQTT_EVENT_DISCONNECTED:
        ESP_LOGW(TAG, "MQTT getrennt");
        ui_set_mqtt(false);
        break;
    case MQTT_EVENT_ERROR:
        if (e->error_handle) ESP_LOGE(TAG, "MQTT Fehler: type=%d code=%d", (int)e->error_handle->error_type,
                                      (int)e->error_handle->connect_return_code);
        break;
    default:
        break;
    }
}

esp_err_t notify_set_credentials(const char *uri, const char *user, const char *pass)
{
    size_t ul = uri ? strlen(uri) : 0;
    if (ul < 8 || ul >= sizeof(s_mq_uri) || strncmp(uri, "mqtt://", 7) || !user || strlen(user) >= sizeof(s_mq_user) ||
        !pass || strlen(pass) >= sizeof(s_mq_pass))      // mqtts:// braucht Zertifikate: noch nicht unterstuetzt
        return ESP_ERR_INVALID_ARG;
    char cu[sizeof(s_mq_uri)] = "";
    mq_load();
    if (!strcmp(uri, s_mq_uri) && !strcmp(user, s_mq_user) && !strcmp(pass, s_mq_pass)) return ESP_OK;   // unveraendert
    esp_err_t r = creds_put("mqtt_uri", uri);
    if (r == ESP_OK) r = creds_put("mqtt_user", user);
    if (r == ESP_OK) r = creds_put("mqtt_pass", pass);
    if (r != ESP_OK) return r;
    (void)cu;
    ESP_LOGI(TAG, "MQTT-Zugangsdaten gespeichert, verbinde neu");
    if (s_mqtt) {
        esp_mqtt_client_stop(s_mqtt);
        esp_mqtt_client_destroy(s_mqtt);
        s_mqtt = NULL;
        ui_set_mqtt(false);
    }
    notify_start();
    return ESP_OK;
}

void notify_start(void)
{
    if (s_mqtt) return;
    mq_load();
    if (!s_mq_uri[0]) return;
    esp_mqtt_client_config_t mc = {
        .broker.address.uri = s_mq_uri,
        .credentials.username = s_mq_user,
        .credentials.authentication.password = s_mq_pass,
        .credentials.client_id = "overboard",
        .session.last_will = { .topic = TOPIC_STATUS, .msg = "offline", .qos = 1, .retain = 1 },
    };
    s_mqtt = esp_mqtt_client_init(&mc);
    esp_mqtt_client_register_event(s_mqtt, ESP_EVENT_ANY_ID, on_mqtt, NULL);
    esp_mqtt_client_start(s_mqtt);
    ESP_LOGI(TAG, "MQTT-Client gestartet");
}
