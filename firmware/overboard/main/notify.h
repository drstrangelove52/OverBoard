#pragma once
#include "esp_err.h"

// MQTT-Client starten (Broker aus Kconfig). Abonniert "overboard/notify" und meldet sich retained auf
// "overboard/status" an (Last Will: "offline"). Wird nach Erhalt der IP-Adresse aufgerufen.
//
// Nachricht auf overboard/notify (JSON, alle Felder optional ausser title oder text):
//   { "title": "Waschmaschine", "text": "Fertig", "level": "info|ok|warn|alarm",
//     "sound": "info|ok|warn|alarm|none", "duration": 8, "volume": 40 }
// duration in Sekunden, 0 = bleibt bis zum Antippen (Standard fuer level "alarm").
void notify_start(void);
// MQTT-Zugang (mqtt://host:port, Benutzer, Passwort) speichern und neu verbinden.
esp_err_t notify_set_credentials(const char *uri, const char *user, const char *pass);
