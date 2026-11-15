#include "theme.h"

#include <string.h>

static const theme_t k_themes[] = {
    {
        .name = "dark",
        .bg = 0x0B0F14, .tile = 0x161C24, .tile_on = 0x4A3B00,
        .title = 0x8A97A6, .value = 0xF2F5F8,
        .accent = 0xFFC53D, .on_accent = 0x000000,
        .ok = 0x3DDC84, .bad = 0xE5484D, .nav = 0x1F2733,
        .idle_bg = 0x000000, .idle_fg = 0xF2F5F8,
        .info = 0x1E5AA8, .success = 0x1F8A4C, .warn = 0xC27A00, .alarm = 0xC0282D,
    },
    {
        .name = "light",
        .bg = 0xF2F4F7, .tile = 0xFFFFFF, .tile_on = 0xFFE9A6,
        .title = 0x5B6672, .value = 0x111418,
        .accent = 0xC98A00, .on_accent = 0x000000,
        .ok = 0x1F9D55, .bad = 0xD13438, .nav = 0xE3E8EE,
        .idle_bg = 0x000000, .idle_fg = 0xF2F5F8,
        .info = 0x1E5AA8, .success = 0x1F8A4C, .warn = 0xC27A00, .alarm = 0xC0282D,
    },
};
#define N_THEMES ((int)(sizeof(k_themes) / sizeof(k_themes[0])))

static const theme_t *s_current = &k_themes[0];

const theme_t *theme(void) { return s_current; }

bool theme_set(const char *name)
{
    if (!name) return false;
    for (int i = 0; i < N_THEMES; i++) {
        if (!strcmp(k_themes[i].name, name)) {
            s_current = &k_themes[i];
            return true;
        }
    }
    return false;
}

bool theme_exists(const char *name)
{
    if (!name) return false;
    for (int i = 0; i < N_THEMES; i++)
        if (!strcmp(k_themes[i].name, name)) return true;
    return false;
}
