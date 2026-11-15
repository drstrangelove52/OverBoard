#pragma once
#include <stdbool.h>
#include <stdint.h>

// Farbdesign. Die Oberflaeche verwendet nur diese Rollen, nie feste Farben.
typedef struct {
    const char *name;
    uint32_t bg;          // Seitenhintergrund
    uint32_t tile;        // Kachel
    uint32_t tile_on;     // Kachel eingeschaltet
    uint32_t title;       // Titel, gedaempfter Text, ausgeschaltete Symbole
    uint32_t value;       // Hauptwerte, Text
    uint32_t accent;      // Akzent (an, aktive Seite, Fortschritt)
    uint32_t on_accent;   // Text auf Akzentflaeche
    uint32_t ok;          // verbunden
    uint32_t bad;         // getrennt, Fehler
    uint32_t nav;         // Navigationstaste, Balkenhintergrund
    uint32_t idle_bg;     // Ruhemodus
    uint32_t idle_fg;
    uint32_t info;        // Banner-Stufen
    uint32_t success;
    uint32_t warn;
    uint32_t alarm;
} theme_t;

const theme_t *theme(void);
// Design nach Namen waehlen ("dark", "light"). Bei unbekanntem Namen bleibt das Design unveraendert (false).
bool theme_set(const char *name);
// Gibt es ein Design mit diesem Namen?
bool theme_exists(const char *name);
