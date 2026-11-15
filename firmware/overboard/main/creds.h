#pragma once
#include <stdbool.h>
#include <stddef.h>

#include "esp_err.h"

// Zugangsdaten (WLAN, Home Assistant, MQTT) im Flash (NVS, Namensraum "ob_cred"). Sie kommen vom PC-Agent und
// ersetzen die beim Bauen eingestellten Werte, damit ein fertiges Programm keine Zugangsdaten enthalten muss.

// Liest einen Wert. true, wenn vorhanden und nicht leer.
bool creds_get(const char *key, char *out, size_t n);
// Speichert einen Wert (sofort festgeschrieben).
esp_err_t creds_put(const char *key, const char *val);
