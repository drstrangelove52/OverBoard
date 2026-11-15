// Board-Support fuer das Elecrow CrowPanel Advance 9" (ESP32-P4, Hardware V1.2).
// Ablauf der Initialisierung folgt dem Elecrow-Beispiel (idf-code/Lesson09).
#pragma once
#include "esp_err.h"

#define OB_LCD_H 1024
#define OB_LCD_V 600

// LDO, I2C, Touch (GT911), MIPI-DSI-Panel (EK79007) und LVGL starten. Backlight bleibt aus.
esp_err_t board_init(void);

// Helligkeit 0..100 (Prozent)
esp_err_t board_set_backlight(int percent);
