#include "pclink.h"

#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "config.h"
#include "i18n.h"
#include "mbedtls/base64.h"
#include "net.h"
#include "notify.h"
#include "tinyusb.h"
#include "tusb_cdc_acm.h"
#include "esp_app_desc.h"
#include "audio.h"
#include "ui.h"

static const char *TAG = "pclink";

#define LINE_MAX        1024
#define LINK_TIMEOUT_US (6LL * 1000000LL)
#define MAX_PC_IDS      12

static char s_line[LINE_MAX];
static size_t s_len;
static QueueHandle_t s_queue;          // vollstaendige Zeilen (malloc), vom Task verarbeitet
static SemaphoreHandle_t s_tx_mutex;
static volatile bool s_dtr;
static volatile bool s_linked;
static int64_t s_last_rx_us;
static char s_ids[MAX_PC_IDS][24];     // bisher gemeldete Zustands-Kennungen
static int s_n_ids;

bool pclink_connected(void) { return s_linked; }

// ---- Senden ---------------------------------------------------------------------------------

static void tx_line(const char *json)
{
    if (!s_dtr) return;
    if (xSemaphoreTake(s_tx_mutex, pdMS_TO_TICKS(200)) != pdTRUE) return;
    size_t len = strlen(json);
    size_t off = 0;
    while (off < len) {                   // in Stuecken schreiben, falls die Zeile groesser als der USB-Puffer ist
        size_t n = tinyusb_cdcacm_write_queue(TINYUSB_CDC_ACM_0, (const uint8_t *)json + off, len - off);
        if (n == 0) {
            if (tinyusb_cdcacm_write_flush(TINYUSB_CDC_ACM_0, pdMS_TO_TICKS(100)) != ESP_OK) break;
            continue;
        }
        off += n;
    }
    tinyusb_cdcacm_write_queue(TINYUSB_CDC_ACM_0, (const uint8_t *)"\n", 1);
    tinyusb_cdcacm_write_flush(TINYUSB_CDC_ACM_0, pdMS_TO_TICKS(100));
    xSemaphoreGive(s_tx_mutex);
}

static void send_json(cJSON *obj)
{
    char *txt = cJSON_PrintUnformatted(obj);
    if (txt) {
        tx_line(txt);
        cJSON_free(txt);
    }
}

bool pclink_action(const char *id)
{
    if (!s_linked || !id) return false;
    cJSON *o = cJSON_CreateObject();
    cJSON_AddStringToObject(o, "t", "act");
    cJSON_AddStringToObject(o, "id", id);
    send_json(o);
    cJSON_Delete(o);
    ESP_LOGI(TAG, "Aktion an PC: %s", id);
    return true;
}

// ---- Verbindungszustand ---------------------------------------------------------------------

static void set_linked(bool linked)
{
    if (s_linked == linked) return;
    s_linked = linked;
    ESP_LOGI(TAG, "PC %s", linked ? "verbunden" : "getrennt");
    ui_set_pc(linked);
    if (!linked) {                        // alle PC-Werte als nicht verfuegbar anzeigen
        for (int i = 0; i < s_n_ids; i++) {
            char key[32];
            snprintf(key, sizeof(key), "pc:%.23s", s_ids[i]);
            ui_set_entity(key, "unavailable");
        }
    }
}

static void remember_id(const char *id)
{
    for (int i = 0; i < s_n_ids; i++)
        if (!strcmp(s_ids[i], id)) return;
    if (s_n_ids < MAX_PC_IDS) snprintf(s_ids[s_n_ids++], sizeof(s_ids[0]), "%.23s", id);
}

// ---- Nachrichten vom PC ---------------------------------------------------------------------

static void send_hello(void)
{
    cJSON *o = cJSON_CreateObject();
    cJSON_AddStringToObject(o, "t", "hello");
    cJSON_AddNumberToObject(o, "v", 1);
    cJSON_AddStringToObject(o, "dev", "overboard");
    cJSON_AddStringToObject(o, "fw", esp_app_get_description()->version);   // aus dem Release-Tag (PROJECT_VER) oder git describe
    cJSON_AddNumberToObject(o, "w", 1024);
    cJSON_AddNumberToObject(o, "h", 600);
    cJSON_AddStringToObject(o, "lang", i18n_get() == LANG_EN ? "en" : "de");
    send_json(o);
    cJSON_Delete(o);
}

// ---- Konfiguration uebertragen --------------------------------------------------------------
// PC -> Geraet: cfg_begin {size, crc}, cfg_data {off, d (Base64)} je Stueck mit Bestaetigung cfg_ack, cfg_end -> cfg_result.
// Geraet -> PC auf cfg_get: cfg_begin, cfg_data (ohne Bestaetigung), cfg_end.

#define CFG_MAX    (48 * 1024)     // groesste uebertragbare Konfiguration
#define CFG_CHUNK  450             // Rohbytes je Stueck (als Base64 600 Zeichen, bleibt unter der Zeilengrenze)

static struct {
    char *buf;
    size_t size, got;
    uint32_t crc;
    bool active;
} s_cfg;

static void send_cfg_ack(size_t off)
{
    char line[64];
    snprintf(line, sizeof(line), "{\"t\":\"cfg_ack\",\"off\":%u}", (unsigned)off);
    tx_line(line);
}

static void send_cfg_result(bool ok, const char *error)
{
    cJSON *o = cJSON_CreateObject();
    cJSON_AddStringToObject(o, "t", "cfg_result");
    cJSON_AddBoolToObject(o, "ok", ok);
    if (error && error[0]) cJSON_AddStringToObject(o, "error", error);
    send_json(o);
    cJSON_Delete(o);
}

static void cfg_reset_transfer(void)
{
    free(s_cfg.buf);
    memset(&s_cfg, 0, sizeof(s_cfg));
}

// Pruefen, speichern und anwenden. Bei Fehlern bleibt die bisherige Konfiguration unveraendert.
static bool cfg_apply(const char *json, size_t len, char *err, size_t err_len)
{
    ob_config_t *cfg = config_parse(json, len, err, err_len);
    if (!cfg) return false;
    char *canon = config_serialize(cfg);
    if (!canon) {
        snprintf(err, err_len, "zu wenig Speicher");
        config_free(cfg);
        return false;
    }
    esp_err_t se = config_store(canon, strlen(canon));
    free(canon);
    if (se != ESP_OK) {
        snprintf(err, err_len, "Speichern fehlgeschlagen: %s", esp_err_to_name(se));
        config_free(cfg);
        return false;
    }
    ob_config_t *old = config_activate(cfg);
    ui_reload();                // die alte Konfiguration darf erst nach dem Neuaufbau freigegeben werden
    net_ha_resubscribe();       // Entitaeten koennen sich geaendert haben
    config_free(old);
    return true;
}

static void cfg_send_to_pc(void)
{
    char *json = config_serialize(ob_config());
    if (!json) {
        send_cfg_result(false, "zu wenig Speicher");
        return;
    }
    size_t len = strlen(json);
    char line[CFG_CHUNK / 3 * 4 + 96];
    snprintf(line, sizeof(line), "{\"t\":\"cfg_begin\",\"size\":%u,\"crc\":%u}", (unsigned)len,
             (unsigned)config_crc32((const uint8_t *)json, len));
    tx_line(line);
    for (size_t off = 0; off < len; off += CFG_CHUNK) {
        size_t n = len - off < CFG_CHUNK ? len - off : CFG_CHUNK;
        char b64[CFG_CHUNK / 3 * 4 + 8];
        size_t olen = 0;
        mbedtls_base64_encode((unsigned char *)b64, sizeof(b64) - 1, &olen, (const unsigned char *)json + off, n);
        b64[olen] = 0;
        snprintf(line, sizeof(line), "{\"t\":\"cfg_data\",\"off\":%u,\"d\":\"%s\"}", (unsigned)off, b64);
        tx_line(line);
        vTaskDelay(pdMS_TO_TICKS(4));       // dem PC Zeit zum Lesen geben
    }
    tx_line("{\"t\":\"cfg_end\"}");
    free(json);
}

static void handle_cfg_message(const char *type, const cJSON *root)
{
    char err[200] = "";
    if (!strcmp(type, "cfg_get")) {
        cfg_send_to_pc();
    } else if (!strcmp(type, "cfg_reset")) {
        config_erase();
        ob_config_t *old = config_activate(NULL);
        ui_reload();
        net_ha_resubscribe();
        config_free(old);
        ESP_LOGI(TAG, "Konfiguration auf Standard zurueckgesetzt");
        send_cfg_result(true, NULL);
    } else if (!strcmp(type, "cfg_begin")) {
        const cJSON *size = cJSON_GetObjectItem(root, "size");
        const cJSON *crc = cJSON_GetObjectItem(root, "crc");
        cfg_reset_transfer();
        if (!cJSON_IsNumber(size) || size->valuedouble < 1 || size->valuedouble > CFG_MAX || !cJSON_IsNumber(crc)) {
            send_cfg_result(false, "ungueltige Groesse (1 bis 49152 Bytes)");
            return;
        }
        s_cfg.size = (size_t)size->valuedouble;
        s_cfg.crc = (uint32_t)crc->valuedouble;
        s_cfg.buf = malloc(s_cfg.size + 1);
        if (!s_cfg.buf) {
            send_cfg_result(false, "zu wenig Speicher");
            return;
        }
        s_cfg.active = true;
        send_cfg_ack(0);
    } else if (!strcmp(type, "cfg_data")) {
        const cJSON *off = cJSON_GetObjectItem(root, "off");
        const cJSON *d = cJSON_GetObjectItem(root, "d");
        if (!s_cfg.active || !cJSON_IsNumber(off) || !cJSON_IsString(d) || (size_t)off->valuedouble != s_cfg.got) {
            cfg_reset_transfer();
            send_cfg_result(false, "Uebertragung ausser der Reihe");
            return;
        }
        size_t olen = 0;
        int rc = mbedtls_base64_decode((unsigned char *)s_cfg.buf + s_cfg.got, s_cfg.size - s_cfg.got, &olen,
                                       (const unsigned char *)d->valuestring, strlen(d->valuestring));
        if (rc != 0) {
            cfg_reset_transfer();
            send_cfg_result(false, "Base64 nicht lesbar oder mehr Daten als angekuendigt");
            return;
        }
        s_cfg.got += olen;
        send_cfg_ack(s_cfg.got);
    } else if (!strcmp(type, "cfg_end")) {
        if (!s_cfg.active || s_cfg.got != s_cfg.size) {
            cfg_reset_transfer();
            send_cfg_result(false, "Uebertragung unvollstaendig");
            return;
        }
        s_cfg.buf[s_cfg.size] = 0;
        if (config_crc32((const uint8_t *)s_cfg.buf, s_cfg.size) != s_cfg.crc) {
            cfg_reset_transfer();
            send_cfg_result(false, "Pruefsumme stimmt nicht");
            return;
        }
        bool ok = cfg_apply(s_cfg.buf, s_cfg.size, err, sizeof(err));
        ESP_LOGI(TAG, "Konfiguration %s%s%s", ok ? "angewendet" : "abgelehnt: ", ok ? "" : err, "");
        cfg_reset_transfer();
        send_cfg_result(ok, ok ? NULL : err);
    }
}

static void handle_line(const char *line)
{
    cJSON *root = cJSON_Parse(line);
    if (!root) {
        ESP_LOGW(TAG, "Zeile ist kein JSON (%d Bytes)", (int)strlen(line));
        return;
    }
    s_last_rx_us = esp_timer_get_time();
    const cJSON *t = cJSON_GetObjectItem(root, "t");
    const char *type = cJSON_IsString(t) ? t->valuestring : "";

    if (!strcmp(type, "hello")) {
        const cJSON *pc = cJSON_GetObjectItem(root, "pc");
        ESP_LOGI(TAG, "hello vom PC '%s'", cJSON_IsString(pc) ? pc->valuestring : "?");
        send_hello();
        set_linked(true);
    } else if (!s_linked) {
        ESP_LOGW(TAG, "Nachricht '%s' vor hello verworfen", type);
    } else if (!strcmp(type, "ping")) {
        cJSON *o = cJSON_CreateObject();
        cJSON_AddStringToObject(o, "t", "pong");
        send_json(o);
        cJSON_Delete(o);
    } else if (!strcmp(type, "sound")) {
        // Ton zum Ausprobieren der Lautstaerke: {"t":"sound","id":"click"|"info"|"ok"|"warn"|"alarm"}
        const cJSON *id = cJSON_GetObjectItem(root, "id");
        if (cJSON_IsString(id)) audio_play(id->valuestring, -1);
    } else if (!strcmp(type, "creds_set")) {
        // Zugangsdaten vom PC-Agent: {"wifi":{"ssid","pass"},"ha":{"uri","token"},"mqtt":{"uri","user","pass"}}.
        // Teile, die fehlen, bleiben unveraendert. Werte werden nicht protokolliert.
        char err[160] = "";
        const cJSON *w = cJSON_GetObjectItem(root, "wifi");
        const cJSON *h = cJSON_GetObjectItem(root, "ha");
        const cJSON *m = cJSON_GetObjectItem(root, "mqtt");
        if (cJSON_IsObject(w)) {
            const cJSON *a1 = cJSON_GetObjectItem(w, "ssid"), *a2 = cJSON_GetObjectItem(w, "pass");
            if (!cJSON_IsString(a1) || net_wifi_set_credentials(a1->valuestring, cJSON_IsString(a2) ? a2->valuestring : "") != ESP_OK)
                strlcat(err, "WLAN (Name 1-32 Zeichen, Passwort leer oder 8-64 Zeichen) ", sizeof(err));
        }
        if (cJSON_IsObject(h)) {
            const cJSON *a1 = cJSON_GetObjectItem(h, "uri"), *a2 = cJSON_GetObjectItem(h, "token");
            if (!cJSON_IsString(a1) || !cJSON_IsString(a2) || net_ha_set_credentials(a1->valuestring, a2->valuestring) != ESP_OK)
                strlcat(err, "Home Assistant (ws://... und Token) ", sizeof(err));
        }
        if (cJSON_IsObject(m)) {
            const cJSON *a1 = cJSON_GetObjectItem(m, "uri"), *a2 = cJSON_GetObjectItem(m, "user"), *a3 = cJSON_GetObjectItem(m, "pass");
            if (!cJSON_IsString(a1) ||
                notify_set_credentials(a1->valuestring, cJSON_IsString(a2) ? a2->valuestring : "", cJSON_IsString(a3) ? a3->valuestring : "") != ESP_OK)
                strlcat(err, "MQTT (mqtt://host:port) ", sizeof(err));
        }
        cJSON *o = cJSON_CreateObject();
        cJSON_AddStringToObject(o, "t", "creds_result");
        cJSON_AddBoolToObject(o, "ok", err[0] == 0);
        if (err[0]) {
            char msg[200];
            snprintf(msg, sizeof(msg), "ungueltig: %s", err);
            cJSON_AddStringToObject(o, "error", msg);
        }
        send_json(o);
        cJSON_Delete(o);
    } else if (!strncmp(type, "cfg_", 4)) {
        handle_cfg_message(type, root);
    } else if (!strcmp(type, "state")) {
        const cJSON *id = cJSON_GetObjectItem(root, "id");
        const cJSON *v = cJSON_GetObjectItem(root, "v");
        if (cJSON_IsString(id)) {
            char value[32] = "";
            if (cJSON_IsString(v)) snprintf(value, sizeof(value), "%.31s", v->valuestring);
            else if (cJSON_IsNumber(v)) snprintf(value, sizeof(value), "%d", v->valueint);
            else if (cJSON_IsBool(v)) snprintf(value, sizeof(value), "%s", cJSON_IsTrue(v) ? "on" : "off");
            remember_id(id->valuestring);
            char key[40];
            snprintf(key, sizeof(key), "pc:%.23s", id->valuestring);
            ui_set_entity(key, value);
        }
    }
    cJSON_Delete(root);
}

// ---- USB ------------------------------------------------------------------------------------

static void on_rx(int itf, cdcacm_event_t *event)
{
    (void)event;
    uint8_t buf[256];
    size_t n = 0;
    while (tinyusb_cdcacm_read(itf, buf, sizeof(buf), &n) == ESP_OK && n > 0) {
        for (size_t i = 0; i < n; i++) {
            uint8_t c = buf[i];
            if (c == '\n') {
                if (s_len) {
                    s_line[s_len] = 0;
                    char *copy = strdup(s_line);
                    if (copy && xQueueSend(s_queue, &copy, 0) != pdTRUE) free(copy);
                }
                s_len = 0;
            } else if (c != '\r') {
                if (s_len < LINE_MAX - 1) s_line[s_len++] = c;
                else s_len = 0;           // zu lange Zeile verwerfen
            }
        }
    }
}

static void on_line_state(int itf, cdcacm_event_t *event)
{
    (void)itf;
    s_dtr = event->line_state_changed_data.dtr;
    ESP_LOGI(TAG, "USB: DTR %s", s_dtr ? "gesetzt" : "weg");
    if (!s_dtr) set_linked(false);
}

static void link_task(void *arg)
{
    char *line;
    for (;;) {
        if (xQueueReceive(s_queue, &line, pdMS_TO_TICKS(500)) == pdTRUE) {
            handle_line(line);
            free(line);
        }
        if (s_linked && esp_timer_get_time() - s_last_rx_us > LINK_TIMEOUT_US) {
            ESP_LOGW(TAG, "Keine Nachricht vom PC seit 6 s");
            set_linked(false);
        }
    }
}

esp_err_t pclink_start(void)
{
    s_queue = xQueueCreate(8, sizeof(char *));
    s_tx_mutex = xSemaphoreCreateMutex();
    if (!s_queue || !s_tx_mutex) return ESP_ERR_NO_MEM;

    const tinyusb_config_t tusb_cfg = { .external_phy = false };   // Standard-Deskriptoren, Texte aus der Konfiguration
    esp_err_t err = tinyusb_driver_install(&tusb_cfg);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "TinyUSB: %s", esp_err_to_name(err));
        return err;
    }
    const tinyusb_config_cdcacm_t acm = {
        .usb_dev = TINYUSB_USBDEV_0,
        .cdc_port = TINYUSB_CDC_ACM_0,
        .rx_unread_buf_sz = 512,
        .callback_rx = on_rx,
        .callback_line_state_changed = on_line_state,
    };
    err = tusb_cdc_acm_init(&acm);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "CDC: %s", esp_err_to_name(err));
        return err;
    }
    if (xTaskCreate(link_task, "pclink", 16384, NULL, 5, NULL) != pdPASS) return ESP_FAIL;
    ESP_LOGI(TAG, "USB-Verbindung bereit (Buchse USB 2.0)");
    return ESP_OK;
}
