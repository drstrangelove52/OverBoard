#pragma once
#include <stdbool.h>
#include "esp_err.h"

// WLAN (esp-hosted ueber den C6) starten. Nach IP-Erhalt laufen SNTP und der HA-WebSocket automatisch.
esp_err_t net_start(void);

// Home-Assistant-Service aufrufen, z. B. ("light", "toggle", "light.burotisch").
// Gibt false zurueck, wenn der WebSocket nicht angemeldet ist oder das Senden fehlschlaegt.
bool net_ha_call_service(const char *domain, const char *service, const char *entity_id);

// true, wenn der HA-WebSocket angemeldet ist (Dienste koennen aufgerufen werden).
bool net_ha_ready(void);
// Zugangsdaten fuer Home Assistant (ws://host:port/api/websocket und Token) im Flash speichern und sofort verwenden.
// Ueberschreibt die beim Bauen eingestellten Werte. ESP_ERR_INVALID_ARG bei ungueltigen Werten.
esp_err_t net_ha_set_credentials(const char *ws_uri, const char *token);
// WLAN-Zugang (SSID 1..32, Passwort leer oder 8..64 Zeichen) speichern und neu verbinden.
esp_err_t net_wifi_set_credentials(const char *ssid, const char *pass);

// Entitaeten-Abo bei HA neu aufbauen (nach einem Konfigurationswechsel, wenn sich die Entitaeten geaendert haben).
void net_ha_resubscribe(void);
