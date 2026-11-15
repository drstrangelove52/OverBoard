#include "audio.h"
#include "board.h"
#include "config.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "net.h"
#include "pclink.h"
#include "ui.h"

static const char *TAG = "overboard";

void app_main(void)
{
    ESP_LOGI(TAG, "OverBoard startet");
    config_init();             // gespeicherte Konfiguration laden (sonst Standard), vor dem Aufbau der Oberflaeche
    ESP_ERROR_CHECK(board_init());
    ui_init();
    if (audio_init() != ESP_OK) ESP_LOGW(TAG, "Audio nicht verfuegbar");
    board_set_backlight(ob_config()->brightness);   // erst nach dem ersten Zeichnen einschalten
    if (pclink_start() != ESP_OK) ESP_LOGW(TAG, "USB-Verbindung nicht verfuegbar");
    ESP_ERROR_CHECK(net_start());
    ESP_LOGI(TAG, "bereit");
}
