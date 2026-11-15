#pragma once
#include "esp_err.h"

// I2S1 + Verstaerker (NS4168, Enable ueber GPIO30, aktiv low) vorbereiten.
esp_err_t audio_init(void);

// Eingebautes Tonmuster abspielen: "info", "ok", "warn", "alarm". Unbekannt/NULL/"none" = nichts.
// volume 0..100, bei <0 gilt der Standardwert. Kehrt sofort zurueck, gespielt wird in einem eigenen Task.
void audio_play(const char *sound, int volume);
