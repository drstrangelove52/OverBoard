#pragma once
#include <stdbool.h>

typedef enum { LANG_DE, LANG_EN } lang_t;

// Text in zwei Sprachen (aus der Konfiguration oder dem Code). Fehlt eine Sprache, gilt die andere.
typedef struct { const char *de; const char *en; } ltext_t;

void i18n_set(lang_t lang);
// "de" oder "en"; Unbekanntes aendert nichts (false).
bool i18n_set_code(const char *code);
lang_t i18n_get(void);

// Oberflaechentext zu einem Schluessel, z. B. "status.run". Unbekannter Schluessel: der Schluessel selbst.
const char *tr(const char *key);
// Wie tr, aber NULL bei unbekanntem Schluessel (fuer optionale Uebersetzungen).
const char *tr_opt(const char *key);
// Passende Sprache aus einem zweisprachigen Text.
const char *lt(ltext_t t);
