#pragma once
#include <stdbool.h>
#include "esp_err.h"

// Verbindung zum PC (OverBoard Agent) ueber USB-CDC. Protokoll siehe docs/protocol.md.
esp_err_t pclink_start(void);

// Aktion auf dem PC ausloesen (nur die Kennung, z. B. "volume.up"). false, wenn kein PC verbunden ist.
bool pclink_action(const char *id);

// true, solange eine PC-App verbunden ist.
bool pclink_connected(void);
