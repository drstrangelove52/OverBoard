#include "mdi.h"

#include <string.h>

uint32_t mdi_codepoint(const char *name)
{
    if (!name) return 0;
    if (!strncmp(name, "mdi:", 4)) name += 4;
    int lo = 0, hi = mdi_table_len - 1;
    while (lo <= hi) {                      // Tabelle ist nach Name sortiert
        int mid = (lo + hi) / 2;
        int c = strcmp(name, mdi_table[mid].name);
        if (c == 0) return mdi_table[mid].cp;
        if (c < 0) hi = mid - 1;
        else lo = mid + 1;
    }
    return 0;
}

void mdi_utf8(const char *name, char out[5])
{
    uint32_t cp = mdi_codepoint(name);
    if (!cp) cp = mdi_codepoint("help-circle");
    if (cp < 0x80) {
        out[0] = (char)cp;
        out[1] = 0;
    } else if (cp < 0x800) {
        out[0] = (char)(0xC0 | (cp >> 6));
        out[1] = (char)(0x80 | (cp & 0x3F));
        out[2] = 0;
    } else if (cp < 0x10000) {
        out[0] = (char)(0xE0 | (cp >> 12));
        out[1] = (char)(0x80 | ((cp >> 6) & 0x3F));
        out[2] = (char)(0x80 | (cp & 0x3F));
        out[3] = 0;
    } else {                                // alle MDI-Symbole liegen ab U+F0001
        out[0] = (char)(0xF0 | (cp >> 18));
        out[1] = (char)(0x80 | ((cp >> 12) & 0x3F));
        out[2] = (char)(0x80 | ((cp >> 6) & 0x3F));
        out[3] = (char)(0x80 | (cp & 0x3F));
        out[4] = 0;
    }
}
