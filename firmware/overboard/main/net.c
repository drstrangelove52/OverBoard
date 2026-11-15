#include "net.h"

#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "esp_check.h"
#include "esp_event.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_netif_sntp.h"
#include "esp_timer.h"
#include "esp_websocket_client.h"
#include "esp_wifi.h"
#include "creds.h"
#include "notify.h"
#include "nvs_flash.h"
#include "ui.h"

static const char *TAG = "net";

// Zugangsdaten: zuerst aus dem Flash (vom PC-Agent uebertragen), sonst die beim Bauen eingestellten
static char s_ha_uri[128];
static char s_ha_token[512];
static char s_wifi_ssid[33];
static char s_wifi_pass[65];
static bool s_wifi_owned;      // WLAN-Zugang stammt vom Agent (sonst verbindet der Funkchip mit seinen gespeicherten Daten)

static void creds_load(void)
{
    snprintf(s_ha_uri, sizeof(s_ha_uri), "%s", CONFIG_OB_HA_WS_URI);
    snprintf(s_ha_token, sizeof(s_ha_token), "%s", CONFIG_OB_HA_TOKEN);
    char u[sizeof(s_ha_uri)], t[sizeof(s_ha_token)];
    if (creds_get("ha_uri", u, sizeof(u)) && creds_get("ha_tok", t, sizeof(t))) {
        memcpy(s_ha_uri, u, sizeof(u));
        memcpy(s_ha_token, t, sizeof(t));
        ESP_LOGI(TAG, "HA-Zugangsdaten aus dem Flash");
    }
    s_wifi_owned = creds_get("wifi_ssid", s_wifi_ssid, sizeof(s_wifi_ssid));
    if (s_wifi_owned) {
        if (!creds_get("wifi_pass", s_wifi_pass, sizeof(s_wifi_pass))) s_wifi_pass[0] = 0;   // offenes WLAN
        ESP_LOGI(TAG, "WLAN-Zugangsdaten aus dem Flash");
    }
}

static esp_websocket_client_handle_t s_ws;
static char *s_rx;
static size_t s_rx_cap;
static bool s_wifi_up, s_ha_up;
static bool s_sntp_started;

static void report_status(void) { ui_set_status(s_wifi_up, s_ha_up); }

// Waechter: bestaetigt HA die Anmeldung nicht innerhalb von 10 s nach dem Verbinden, wird neu verbunden.
// (Beobachtet am 2026-11-15: einmal blieb die Anmeldung beim Start haengen.)
#define AUTH_TIMEOUT_US (10LL * 1000000LL)
static esp_timer_handle_t s_auth_timer;

static void reconnect_task(void *arg)
{
    ESP_LOGW(TAG, "HA: Anmeldung dauert zu lange, verbinde neu");
    esp_websocket_client_stop(s_ws);
    vTaskDelay(pdMS_TO_TICKS(500));
    esp_websocket_client_start(s_ws);
    vTaskDelete(NULL);
}

static void auth_timeout(void *arg)
{
    if (s_ha_up || !s_ws) return;
    xTaskCreate(reconnect_task, "ha_reconn", 4096, NULL, 5, NULL);   // nicht im Timer-Task blockieren
}

// ---- Home Assistant WebSocket ---------------------------------------------------------------

static void ws_send_json(cJSON *obj)
{
    char *txt = cJSON_PrintUnformatted(obj);
    if (txt) {
        esp_websocket_client_send_text(s_ws, txt, strlen(txt), pdMS_TO_TICKS(2000));
        cJSON_free(txt);
    }
}

static bool s_auth_sent;   // pro Verbindung nur einmal anmelden

static void ha_send_auth(void)
{
    s_auth_sent = true;
    cJSON *o = cJSON_CreateObject();
    cJSON_AddStringToObject(o, "type", "auth");
    cJSON_AddStringToObject(o, "access_token", s_ha_token);
    ws_send_json(o);
    cJSON_Delete(o);
}

static int s_next_id = 10;      // laufende Nummer fuer alle Befehle an HA
static int s_sub_id;            // Nummer des aktuellen Entitaeten-Abos (0 = keins)

// Entitaeten abonnieren. Ein bestehendes Abo wird vorher abbestellt (nach einem Konfigurationswechsel aendert sich die Liste).
static void ha_subscribe(void)
{
    if (s_sub_id) {
        cJSON *u = cJSON_CreateObject();
        cJSON_AddNumberToObject(u, "id", s_next_id++);
        cJSON_AddStringToObject(u, "type", "unsubscribe_events");
        cJSON_AddNumberToObject(u, "subscription", s_sub_id);
        ws_send_json(u);
        cJSON_Delete(u);
    }
    s_sub_id = s_next_id++;
    cJSON *o = cJSON_CreateObject();
    cJSON_AddNumberToObject(o, "id", s_sub_id);
    cJSON_AddStringToObject(o, "type", "subscribe_entities");
    cJSON *ids = cJSON_AddArrayToObject(o, "entity_ids");
    for (int i = 0; i < ui_entity_count(); i++) cJSON_AddItemToArray(ids, cJSON_CreateString(ui_entity_id(i)));
    ws_send_json(o);
    cJSON_Delete(o);
}

// Kompaktes subscribe_entities-Format: "a" = hinzugefuegt/Anfangszustand, "c" = Aenderungen.
// a: { entity: { "s": state, "a": {attrs}, ... } }   c: { entity: { "+": { "s": state, ... } } }
static void ha_handle_event(const cJSON *event)
{
    const cJSON *add = cJSON_GetObjectItem(event, "a");
    const cJSON *chg = cJSON_GetObjectItem(event, "c");
    const cJSON *it;
    cJSON_ArrayForEach(it, add) {
        const cJSON *s = cJSON_GetObjectItem(it, "s");
        if (cJSON_IsString(s) && it->string) ui_set_entity(it->string, s->valuestring);
    }
    cJSON_ArrayForEach(it, chg) {
        const cJSON *plus = cJSON_GetObjectItem(it, "+");
        const cJSON *s = plus ? cJSON_GetObjectItem(plus, "s") : NULL;
        if (cJSON_IsString(s) && it->string) ui_set_entity(it->string, s->valuestring);
    }
}

static void ha_handle_message(const char *txt)
{
    cJSON *root = cJSON_Parse(txt);
    if (!root) {
        ESP_LOGW(TAG, "HA: JSON nicht lesbar (%d Bytes)", (int)strlen(txt));
        return;
    }
    const cJSON *type = cJSON_GetObjectItem(root, "type");
    const char *t = cJSON_IsString(type) ? type->valuestring : "";
    ESP_LOGD(TAG, "HA rx: %s", t);
    if (!strcmp(t, "auth_required")) {
        if (!s_auth_sent) ha_send_auth();
    } else if (!strcmp(t, "auth_ok")) {
        ESP_LOGI(TAG, "HA: angemeldet");
        esp_timer_stop(s_auth_timer);
        s_ha_up = true;
        report_status();
        ha_subscribe();
    } else if (!strcmp(t, "auth_invalid")) {
        ESP_LOGE(TAG, "HA: Token abgelehnt");
    } else if (!strcmp(t, "event")) {
        ha_handle_event(cJSON_GetObjectItem(root, "event"));
    } else if (!strcmp(t, "result")) {
        const cJSON *ok = cJSON_GetObjectItem(root, "success");
        const cJSON *id = cJSON_GetObjectItem(root, "id");
        if (cJSON_IsFalse(ok)) {
            const cJSON *err = cJSON_GetObjectItem(root, "error");
            const cJSON *msg = err ? cJSON_GetObjectItem(err, "message") : NULL;
            ESP_LOGW(TAG, "HA: Befehl %d fehlgeschlagen: %s", cJSON_IsNumber(id) ? id->valueint : -1,
                     cJSON_IsString(msg) ? msg->valuestring : "?");
        } else if (cJSON_IsNumber(id) && id->valueint != s_sub_id && id->valueint >= 10) {
            ESP_LOGI(TAG, "HA: Befehl %d ausgefuehrt", id->valueint);
        }
    }
    cJSON_Delete(root);
}

static void on_ws(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    esp_websocket_event_data_t *e = data;
    switch (id) {
    case WEBSOCKET_EVENT_CONNECTED:
        ESP_LOGI(TAG, "HA-WebSocket verbunden");
        esp_timer_stop(s_auth_timer);
        esp_timer_start_once(s_auth_timer, AUTH_TIMEOUT_US);
        // Sofort anmelden, ohne auf "auth_required" zu warten: HA schickt es oft im selben Paket wie die
        // Handshake-Antwort, und der WebSocket-Client liefert es dann manchmal erst verspaetet aus.
        s_auth_sent = false;
        s_sub_id = 0;                          // Abos ueberleben keine neue Verbindung
        ha_send_auth();
        break;
    case WEBSOCKET_EVENT_DISCONNECTED:
        ESP_LOGW(TAG, "HA-WebSocket getrennt");
        esp_timer_stop(s_auth_timer);
        s_sub_id = 0;
        s_ha_up = false;
        report_status();
        break;
    case WEBSOCKET_EVENT_DATA:
        ESP_LOGD(TAG, "WS Frame op=0x%02x off=%d len=%d/%d", e->op_code, e->payload_offset, e->data_len, e->payload_len);
        if (e->op_code != 0x01 && e->op_code != 0x00) break;   // nur Text-Frames
        if (e->payload_offset == 0) {
            size_t need = e->payload_len + 1;
            if (need > s_rx_cap) {
                free(s_rx);
                s_rx = heap_caps_malloc(need, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
                s_rx_cap = s_rx ? need : 0;
            }
        }
        if (!s_rx) break;
        if (e->payload_offset + e->data_len <= s_rx_cap - 1) memcpy(s_rx + e->payload_offset, e->data_ptr, e->data_len);
        if (e->payload_offset + e->data_len >= e->payload_len) {
            s_rx[e->payload_len] = 0;
            ha_handle_message(s_rx);
        }
        break;
    default:
        break;
    }
}

bool net_ha_ready(void) { return s_ws && s_ha_up; }

void net_ha_resubscribe(void)
{
    if (s_ws && s_ha_up) ha_subscribe();
}

bool net_ha_call_service(const char *domain, const char *service, const char *entity_id)
{
    if (!s_ws || !s_ha_up) {
        ESP_LOGW(TAG, "HA nicht bereit, %s.%s verworfen", domain, service);
        return false;
    }
    cJSON *o = cJSON_CreateObject();
    cJSON_AddNumberToObject(o, "id", s_next_id++);
    cJSON_AddStringToObject(o, "type", "call_service");
    cJSON_AddStringToObject(o, "domain", domain);
    cJSON_AddStringToObject(o, "service", service);
    cJSON *target = cJSON_AddObjectToObject(o, "target");
    cJSON_AddStringToObject(target, "entity_id", entity_id);
    char *txt = cJSON_PrintUnformatted(o);
    int sent = txt ? esp_websocket_client_send_text(s_ws, txt, strlen(txt), pdMS_TO_TICKS(300)) : -1;
    ESP_LOGI(TAG, "HA: %s.%s -> %s (%s)", domain, service, entity_id, sent >= 0 ? "gesendet" : "FEHLER");
    cJSON_free(txt);
    cJSON_Delete(o);
    return sent >= 0;
}

static void ha_start(void)
{
    if (s_ws || !s_ha_uri[0] || !s_ha_token[0]) return;
    esp_websocket_client_config_t cfg = {
        .uri = s_ha_uri,
        .buffer_size = 4096,
        .reconnect_timeout_ms = 5000,
        .network_timeout_ms = 10000,
        .ping_interval_sec = 20,
    };
    const esp_timer_create_args_t ta = { .callback = auth_timeout, .name = "ha_auth" };
    esp_timer_create(&ta, &s_auth_timer);
    s_ws = esp_websocket_client_init(&cfg);
    esp_websocket_register_events(s_ws, WEBSOCKET_EVENT_ANY, on_ws, NULL);
    esp_websocket_client_start(s_ws);
    ESP_LOGI(TAG, "HA-WebSocket-Client gestartet");
}

esp_err_t net_ha_set_credentials(const char *uri, const char *token)
{
    if (!uri || !token || strlen(uri) < 8 || strlen(uri) >= sizeof(s_ha_uri) || strlen(token) < 20 ||
        strlen(token) >= sizeof(s_ha_token) || strncmp(uri, "ws://", 5))   // wss:// braucht Zertifikate: noch nicht unterstuetzt
        return ESP_ERR_INVALID_ARG;
    if (!strcmp(uri, s_ha_uri) && !strcmp(token, s_ha_token)) return ESP_OK;     // unveraendert

    esp_err_t r = creds_put("ha_uri", uri);
    if (r == ESP_OK) r = creds_put("ha_tok", token);
    if (r != ESP_OK) return r;

    snprintf(s_ha_uri, sizeof(s_ha_uri), "%s", uri);
    snprintf(s_ha_token, sizeof(s_ha_token), "%s", token);
    ESP_LOGI(TAG, "HA-Zugangsdaten gespeichert, verbinde neu");
    if (s_ws) {
        esp_websocket_client_stop(s_ws);
        esp_websocket_client_set_uri(s_ws, s_ha_uri);
        s_sub_id = 0;
        s_auth_sent = false;
        esp_websocket_client_start(s_ws);
    } else if (s_wifi_up) {
        ha_start();
    }
    return ESP_OK;
}

// WLAN mit den Zugangsdaten vom Agent verbinden
static esp_timer_handle_t s_wifi_retry;

static void wifi_retry_cb(void *arg) { esp_wifi_connect(); }

static void wifi_apply(void)
{
    wifi_config_t wc = { 0 };
    memcpy(wc.sta.ssid, s_wifi_ssid, strlen(s_wifi_ssid));       // hoechstens 32 Zeichen, geprueft beim Speichern
    memcpy(wc.sta.password, s_wifi_pass, strlen(s_wifi_pass));
    wc.sta.threshold.authmode = s_wifi_pass[0] ? WIFI_AUTH_WPA_PSK : WIFI_AUTH_OPEN;
    wc.sta.pmf_cfg.capable = true;
    esp_wifi_set_config(WIFI_IF_STA, &wc);
    esp_wifi_connect();
}

esp_err_t net_wifi_set_credentials(const char *ssid, const char *pass)
{
    size_t sl = ssid ? strlen(ssid) : 0, pl = pass ? strlen(pass) : 0;
    if (sl < 1 || sl > 32 || pl > 64 || (pl > 0 && pl < 8)) return ESP_ERR_INVALID_ARG;
    if (s_wifi_owned && !strcmp(ssid, s_wifi_ssid) && !strcmp(pass, s_wifi_pass)) return ESP_OK;   // unveraendert
    esp_err_t r = creds_put("wifi_ssid", ssid);
    if (r == ESP_OK) r = creds_put("wifi_pass", pass);
    if (r != ESP_OK) return r;
    snprintf(s_wifi_ssid, sizeof(s_wifi_ssid), "%s", ssid);
    snprintf(s_wifi_pass, sizeof(s_wifi_pass), "%s", pass);
    s_wifi_owned = true;
    ESP_LOGI(TAG, "WLAN-Zugangsdaten gespeichert, verbinde neu");
    esp_wifi_disconnect();
    wifi_apply();
    return ESP_OK;
}

// ---- WLAN / Zeit ----------------------------------------------------------------------------

static void on_wifi_ip(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        s_wifi_up = false;
        report_status();
        if (s_wifi_owned && s_wifi_retry) esp_timer_start_once(s_wifi_retry, 3000000);   // nach 3 s erneut versuchen
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *e = data;
        ESP_LOGI(TAG, "IP erhalten: " IPSTR, IP2STR(&e->ip_info.ip));
        s_wifi_up = true;
        report_status();
        if (!s_sntp_started) {
            s_sntp_started = true;
            setenv("TZ", "CET-1CEST,M3.5.0,M10.5.0/3", 1);   // Europe/Zurich
            tzset();
            esp_sntp_config_t sc = ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
            esp_netif_sntp_init(&sc);
        }
        ha_start();
        notify_start();
    }
}

esp_err_t net_start(void)
{
    esp_err_t r = nvs_flash_init();
    if (r == ESP_ERR_NVS_NO_FREE_PAGES || r == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_RETURN_ON_ERROR(nvs_flash_erase(), TAG, "nvs erase");
        r = nvs_flash_init();
    }
    ESP_RETURN_ON_ERROR(r, TAG, "nvs");
    creds_load();
    ESP_RETURN_ON_ERROR(esp_netif_init(), TAG, "netif");
    ESP_RETURN_ON_ERROR(esp_event_loop_create_default(), TAG, "event loop");
    esp_netif_create_default_wifi_sta();
    ESP_RETURN_ON_ERROR(esp_event_handler_register(WIFI_EVENT, WIFI_EVENT_STA_DISCONNECTED, on_wifi_ip, NULL), TAG, "ev");
    ESP_RETURN_ON_ERROR(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, on_wifi_ip, NULL), TAG, "ev");

    wifi_init_config_t cfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_RETURN_ON_ERROR(esp_wifi_init(&cfg), TAG, "wifi init");   // startet esp-hosted ueber SDIO
    ESP_RETURN_ON_ERROR(esp_wifi_set_mode(WIFI_MODE_STA), TAG, "mode");
    ESP_RETURN_ON_ERROR(esp_wifi_start(), TAG, "wifi start");
    const esp_timer_create_args_t rt = { .callback = wifi_retry_cb, .name = "wifi_retry" };
    esp_timer_create(&rt, &s_wifi_retry);
    if (s_wifi_owned) wifi_apply();
    // Zugangsdaten stammen vorerst aus dem Speicher des C6 (aus der Werks-Demo). Provisionierung folgt spaeter.
    return ESP_OK;
}
