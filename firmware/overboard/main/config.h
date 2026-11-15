#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"
#include "i18n.h"

// Aufbau der Konfiguration (Seiten, Kacheln, Einstellungen). Die Felder entsprechen den JSON-Schluesseln
// (siehe docs/config.md). Es gibt eine eingebaute Standardkonfiguration; eine per USB uebertragene Konfiguration
// wird im Flash gespeichert und ersetzt sie.

typedef enum { TILE_VALUE, TILE_BUTTON } tile_type_t;           // Anzeige-Kachel oder Button-Kachel
typedef enum { FMT_POWER, FMT_KWH, FMT_STATUS, FMT_ONOFF, FMT_TEXT, FMT_PERCENT, FMT_TIME } fmt_t;
typedef enum { TRIG_NONE, TRIG_TAP, TRIG_HOLD, TRIG_CONFIRM } trigger_t;   // Tippen, Halten, Tippen mit Rueckfrage

typedef struct {
    tile_type_t type;
    uint8_t x, y;            // Position im Raster (Spalte, Reihe, ab 0)
    uint8_t w, h;            // Groesse in Rasterzellen
    const char *entity;      // Zustandsquelle: HA-Entitaet ("light.x") oder PC-Funktion ("pc:volume"), "" = keine
    ltext_t title;           // Titel in beiden Sprachen
    const char *icon;        // Symbolname, z. B. "mdi:lightbulb" (optional, NULL = keins)
    fmt_t fmt;               // Darstellung des Zustands
    float max;               // Obergrenze fuer plausible Leistungswerte (0 = Standard 20 kW)
    trigger_t trigger;       // nur bei Button-Kacheln
    uint16_t hold_ms;        // Haltedauer bei TRIG_HOLD in ms, 0 = globaler Wert
    const char *action;      // Aktion: NULL = HA-Entitaet umschalten, "pc:<id>" = Aktion auf dem PC (z. B. "pc:volume.up")
} tile_cfg_t;

typedef struct {
    ltext_t title;
    const char *icon;
    const tile_cfg_t *tiles;
    int n_tiles;
    uint8_t rows;            // Reihen im Raster (1..6), 0 = Standard (4)
} page_cfg_t;

typedef struct {
    const char *language;    // "de" oder "en"
    const char *theme;       // "dark" oder "light"
    int brightness;          // Helligkeit aktiv, Prozent
    int idle_s;              // Sekunden bis zum Ruhemodus
    int idle_brightness;     // Helligkeit im Ruhemodus, Prozent
    int idle_move_s;         // Uhr im Ruhemodus alle n Sekunden verschieben
    tile_cfg_t idle_items[2];// bis zu zwei Werte im Ruhemodus (verwendet: Quelle, Titel, Format, Obergrenze)
    int n_idle_items;
    int hold_ms;             // Standard-Haltedauer fuer Buttons mit Halten (ms)
    int sound_volume;        // Lautstaerke der Hinweistoene, Prozent (0 = aus)
    bool click;              // Tastenklick beim Beruehren von Tasten
    int click_volume;        // Lautstaerke des Tastenklicks, Prozent
    const page_cfg_t *pages;
    int n_pages;
    void *arena;             // nur bei dynamischen Konfigurationen: gesammelter Speicher, gehoert config_free()
} ob_config_t;

// Raster: 8 Spalten, Standard 4 Reihen pro Seite (einstellbar 1..6).
#define GRID_COLS 8
#define GRID_ROWS 4
#define MAX_PAGES 8
#define MAX_TILES_PER_PAGE 48
#define MAX_ROWS 6

// Aktive Konfiguration (nie NULL).
const ob_config_t *ob_config(void);
// Eingebaute Standardkonfiguration.
const ob_config_t *config_default(void);

// Aus JSON-Text lesen und pruefen. Bei Erfolg gehoert das Ergebnis dem Aufrufer (config_free). Bei Fehler NULL
// und ein deutscher Fehlertext in err.
ob_config_t *config_parse(const char *json, size_t len, char *err, size_t err_len);
// Konfiguration als JSON-Text (kompakt). Der Aufrufer gibt ihn mit free() frei.
char *config_serialize(const ob_config_t *cfg);
// Gibt eine dynamische Konfiguration frei (die eingebaute Standardkonfiguration wird ignoriert).
void config_free(ob_config_t *cfg);
// Macht cfg zur aktiven Konfiguration (NULL = Standard). Gibt die bisher aktive dynamische Konfiguration zurueck;
// sie wird erst freigegeben, wenn die Oberflaeche neu aufgebaut ist.
ob_config_t *config_activate(ob_config_t *cfg);

// CRC32 (IEEE, wie zlib) fuer die Pruefung uebertragener und gespeicherter Konfigurationen.
uint32_t config_crc32(const uint8_t *data, size_t len);

// Beim Start: gespeicherte Konfiguration laden (sonst Standard).
void config_init(void);
// Konfigurationstext im Flash speichern bzw. die gespeicherte Konfiguration loeschen.
esp_err_t config_store(const char *json, size_t len);
esp_err_t config_erase(void);
