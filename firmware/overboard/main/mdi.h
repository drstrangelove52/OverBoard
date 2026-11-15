#pragma once
#include <stdint.h>

// Material Design Icons (Pictogrammers, Apache-2.0). Symbolnamen wie in der Konfiguration: "mdi:lightbulb" oder "lightbulb".
typedef struct { const char *name; uint32_t cp; } mdi_entry_t;
extern const mdi_entry_t mdi_table[];
extern const int mdi_table_len;

// Unicode-Codepunkt des Symbols, 0 wenn der Name unbekannt ist.
uint32_t mdi_codepoint(const char *name);
// Symbol als UTF-8-Text (4 Bytes + Ende) fuer ein Label mit der Symbolschrift. Unbekannte Namen ergeben "help-circle".
void mdi_utf8(const char *name, char out[5]);
