#include "config.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "esp_log.h"
#include "esp_partition.h"
#include "mdi.h"
#include "theme.h"

static const char *TAG = "config";

// ================================================================================================
// Eingebaute Standardkonfiguration
// ================================================================================================
// Felder: Typ, x, y, w, h, Quelle, Titel, Symbol, Format, Obergrenze, Bedienung, Haltedauer, Aktion
static const tile_cfg_t k_tiles_home[] = {
    // Haus (Home Assistant)
    { TILE_VALUE,  0, 0, 2, 2, "sensor.inverter_active_power",          { "Solar", "Solar" },                 "mdi:solar-power",         FMT_POWER,  0,    TRIG_NONE, 0, NULL },
    { TILE_VALUE,  2, 0, 2, 1, "sensor.inverter_daily_yield",           { "Tagesertrag", "Daily yield" },     "mdi:white-balance-sunny", FMT_KWH,    0,    TRIG_NONE, 0, NULL },
    { TILE_VALUE,  4, 0, 2, 1, "sensor.power_consumption_house",        { "Haus", "House" },                  "mdi:home-lightning-bolt", FMT_POWER,  0,    TRIG_NONE, 0, NULL },
    // Wärmepumpe: mehr als 6 kW ist technisch ausgeschlossen (Martin), alles darüber ist ein Messfehler
    { TILE_VALUE,  6, 0, 2, 1, "sensor.heat_pump_power_consumption",   { "Wärmepumpe", "Heat pump" },        "mdi:heat-pump",           FMT_POWER,  6000, TRIG_NONE, 0, NULL },
    { TILE_VALUE,  2, 1, 2, 1, "sensor.geschirrspuler_betriebszustand", { "Geschirrspüler", "Dishwasher" },   "mdi:dishwasher",          FMT_STATUS, 0,    TRIG_NONE, 0, NULL },
    { TILE_BUTTON, 4, 1, 1, 1, "light.burotisch",                       { "Bürotisch", "Desk" },              "mdi:lightbulb",           FMT_ONOFF,  0,    TRIG_TAP,  0, NULL },
    { TILE_BUTTON, 5, 1, 1, 1, "switch.bambu_lab_x1c",                  { "X1C", "X1C" },                     "mdi:printer-3d",          FMT_ONOFF,  0,    TRIG_HOLD, 0, NULL },
    // PC (OverBoard Agent über USB)
    { TILE_VALUE,  0, 2, 2, 1, "pc:volume",                             { "PC Lautstärke", "PC volume" },     "mdi:volume-high",         FMT_PERCENT, 0,   TRIG_NONE, 0, NULL },
    { TILE_BUTTON, 2, 2, 1, 1, "",                                      { "Leiser", "Quieter" },              "mdi:volume-minus",        FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:volume.down" },
    { TILE_BUTTON, 3, 2, 1, 1, "",                                      { "Lauter", "Louder" },               "mdi:volume-plus",         FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:volume.up" },
    { TILE_BUTTON, 4, 2, 1, 1, "pc:mute",                               { "Stumm", "Mute" },                  "mdi:volume-off",          FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:mute.toggle" },
    { TILE_BUTTON, 5, 2, 1, 1, "pc:mic_mute",                           { "Mikro", "Mic" },                   "mdi:microphone-off",      FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:mic.toggle" },
    { TILE_BUTTON, 6, 2, 1, 1, "",                                      { "Sperren", "Lock" },                "mdi:lock",                FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:lock" },
    // Eigene PC-Aktionen: in der PC-App unter der Kennung "calculator", "screenshot" bzw. "notepad" anlegen
    { TILE_BUTTON, 7, 2, 1, 1, "",                                      { "Rechner", "Calculator" },          "mdi:calculator",          FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:calculator" },
    { TILE_BUTTON, 0, 3, 1, 1, "",                                      { "Screenshot", "Screenshot" },       "mdi:monitor-screenshot",  FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:screenshot" },
    { TILE_BUTTON, 1, 3, 1, 1, "",                                      { "Notepad", "Notepad" },             "mdi:notebook",            FMT_ONOFF,  0,    TRIG_TAP,  0, "pc:notepad" },
};

static const page_cfg_t k_pages[] = {
    { { "Haus", "Home" }, "mdi:home", k_tiles_home, (int)(sizeof(k_tiles_home) / sizeof(k_tiles_home[0])), 4 },
};

static const ob_config_t k_default = {
    .language = "de",
    .theme = "dark",
    .brightness = 80,
    .idle_s = 60,
    .idle_brightness = 8,
    .idle_move_s = 60,
    .idle_items = {
        { TILE_VALUE, 0, 0, 1, 1, "sensor.inverter_active_power",    { "Solar", "Solar" }, NULL, FMT_POWER, 0, TRIG_NONE, 0, NULL },
        { TILE_VALUE, 0, 0, 1, 1, "sensor.power_consumption_house", { "Haus", "House" },  NULL, FMT_POWER, 0, TRIG_NONE, 0, NULL },
    },
    .n_idle_items = 2,
    .hold_ms = 600,
    .pages = k_pages,
    .n_pages = (int)(sizeof(k_pages) / sizeof(k_pages[0])),
};

static const ob_config_t *s_active = &k_default;
static ob_config_t *s_active_dyn;

const ob_config_t *ob_config(void) { return s_active; }
const ob_config_t *config_default(void) { return &k_default; }

ob_config_t *config_activate(ob_config_t *cfg)
{
    ob_config_t *old = s_active_dyn;
    s_active_dyn = cfg;
    s_active = cfg ? cfg : &k_default;
    return old;
}

// ================================================================================================
// Speicher: alle Zeichenketten und Felder einer dynamischen Konfiguration in einer Liste, die zusammen
// freigegeben wird
// ================================================================================================
typedef struct blk { struct blk *next; } blk_t;
typedef struct { blk_t *head; } arena_t;

static void *a_alloc(arena_t *a, size_t n)
{
    blk_t *b = calloc(1, sizeof(blk_t) + n);
    if (!b) return NULL;
    b->next = a->head;
    a->head = b;
    return b + 1;
}

static char *a_strdup(arena_t *a, const char *s)
{
    size_t n = strlen(s) + 1;
    char *p = a_alloc(a, n);
    if (p) memcpy(p, s, n);
    return p;
}

static void a_free(arena_t *a)
{
    while (a->head) {
        blk_t *n = a->head->next;
        free(a->head);
        a->head = n;
    }
}

void config_free(ob_config_t *cfg)
{
    if (!cfg || cfg == &k_default || !cfg->arena) return;
    arena_t *ar = cfg->arena;     // cfg selbst liegt in der Liste: zuerst den Zeiger sichern
    a_free(ar);
    free(ar);
}

// ================================================================================================
// Namen der Aufzaehlungen
// ================================================================================================
static const char *const k_type_names[] = { "value", "button" };
static const char *const k_fmt_names[] = { "power", "kwh", "status", "onoff", "text", "percent", "time" };
static const char *const k_trig_names[] = { "none", "tap", "hold", "confirm" };
#define COUNT(a) ((int)(sizeof(a) / sizeof((a)[0])))

static int find_name(const char *s, const char *const *names, int n)
{
    for (int i = 0; i < n; i++)
        if (!strcmp(s, names[i])) return i;
    return -1;
}

// ================================================================================================
// JSON lesen und pruefen
// ================================================================================================
#define FAIL(...) do { snprintf(err, err_len, __VA_ARGS__); goto fail; } while (0)

static bool read_int(const cJSON *o, const char *key, int lo, int hi, int def, int *out,
                     const char *where, char *err, size_t err_len)
{
    const cJSON *it = cJSON_GetObjectItem(o, key);
    if (!it) {
        *out = def;
        return true;
    }
    if (!cJSON_IsNumber(it)) {
        snprintf(err, err_len, "%s: '%s' muss eine Zahl sein", where, key);
        return false;
    }
    int v = (int)it->valuedouble;
    if (v < lo || v > hi) {
        snprintf(err, err_len, "%s: '%s' muss zwischen %d und %d liegen", where, key, lo, hi);
        return false;
    }
    *out = v;
    return true;
}

static bool read_ltext(arena_t *a, const cJSON *o, const char *key, ltext_t *out,
                       const char *where, char *err, size_t err_len)
{
    out->de = "";
    out->en = "";
    const cJSON *it = cJSON_GetObjectItem(o, key);
    if (!it) return true;
    const char *de = NULL, *en = NULL;
    if (cJSON_IsString(it)) {
        de = en = it->valuestring;
    } else if (cJSON_IsObject(it)) {
        const cJSON *d = cJSON_GetObjectItem(it, "de");
        const cJSON *e = cJSON_GetObjectItem(it, "en");
        if (cJSON_IsString(d)) de = d->valuestring;
        if (cJSON_IsString(e)) en = e->valuestring;
        if (!de) de = en;
        if (!en) en = de;
    } else {
        snprintf(err, err_len, "%s: '%s' muss Text oder {\"de\": ..., \"en\": ...} sein", where, key);
        return false;
    }
    if (!de) return true;
    if (strlen(de) > 63 || strlen(en) > 63) {
        snprintf(err, err_len, "%s: '%s' ist zu lang (höchstens 63 Zeichen)", where, key);
        return false;
    }
    out->de = a_strdup(a, de);
    out->en = (en == de) ? out->de : a_strdup(a, en);
    return out->de && out->en;
}

static bool id_chars_ok(const char *s, size_t max_len)
{
    size_t n = strlen(s);
    if (n == 0 || n > max_len) return false;
    for (size_t i = 0; i < n; i++) {
        char c = s[i];
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-')) return false;
    }
    return true;
}

static bool check_source(const char *s)    // "" | "pc:<id>" | "<domain>.<objekt>"
{
    if (!s[0]) return true;
    if (!strncmp(s, "pc:", 3)) return id_chars_ok(s + 3, 31);
    return id_chars_ok(s, 64) && strchr(s, '.');
}

static bool check_icon(const char *s) { return !s[0] || mdi_codepoint(s) != 0; }

ob_config_t *config_parse(const char *json, size_t len, char *err, size_t err_len)
{
    err[0] = 0;
    cJSON *root = cJSON_ParseWithLength(json, len);
    if (!root) {
        const char *ep = cJSON_GetErrorPtr();
        snprintf(err, err_len, "JSON nicht lesbar (bei Zeichen %d)", ep ? (int)(ep - json) : -1);
        return NULL;
    }
    arena_t *ar = calloc(1, sizeof(*ar));
    ob_config_t *cfg = ar ? a_alloc(ar, sizeof(*cfg)) : NULL;
    if (!cfg) {
        snprintf(err, err_len, "zu wenig Speicher");
        cJSON_Delete(root);
        free(ar);
        return NULL;
    }
    cfg->arena = ar;

    int v = 1;
    if (!read_int(root, "v", 1, 1, 1, &v, "Konfiguration", err, err_len)) goto fail;

    const cJSON *it = cJSON_GetObjectItem(root, "language");
    cfg->language = "de";
    if (it) {
        if (!cJSON_IsString(it) || (strcmp(it->valuestring, "de") && strcmp(it->valuestring, "en")))
            FAIL("Konfiguration: 'language' muss \"de\" oder \"en\" sein");
        cfg->language = !strcmp(it->valuestring, "en") ? "en" : "de";
    }
    it = cJSON_GetObjectItem(root, "theme");
    cfg->theme = "dark";
    if (it) {
        if (!cJSON_IsString(it) || !theme_exists(it->valuestring)) FAIL("Konfiguration: unbekanntes 'theme'");
        cfg->theme = a_strdup(ar, it->valuestring);
    }
    if (!read_int(root, "brightness", 1, 100, 80, &cfg->brightness, "Konfiguration", err, err_len)) goto fail;
    if (!read_int(root, "hold_ms", 100, 5000, 600, &cfg->hold_ms, "Konfiguration", err, err_len)) goto fail;
    const cJSON *idle = cJSON_GetObjectItem(root, "idle");
    cfg->idle_s = 60;
    cfg->idle_brightness = 8;
    cfg->idle_move_s = 60;
    if (idle) {
        if (!cJSON_IsObject(idle)) FAIL("Konfiguration: 'idle' muss ein Objekt sein");
        if (!read_int(idle, "timeout_s", 5, 3600, 60, &cfg->idle_s, "idle", err, err_len)) goto fail;
        if (!read_int(idle, "brightness", 1, 100, 8, &cfg->idle_brightness, "idle", err, err_len)) goto fail;
        if (!read_int(idle, "move_s", 5, 3600, 60, &cfg->idle_move_s, "idle", err, err_len)) goto fail;
    }
    // Werte im Ruhemodus: ohne Angabe die Standardwerte, [] = keine
    const cJSON *ivals = idle ? cJSON_GetObjectItem(idle, "values") : NULL;
    if (!ivals) {
        cfg->n_idle_items = k_default.n_idle_items;
        for (int i = 0; i < k_default.n_idle_items; i++) cfg->idle_items[i] = k_default.idle_items[i];
    } else {
        if (!cJSON_IsArray(ivals)) FAIL("idle: 'values' muss eine Liste sein");
        int ni = cJSON_GetArraySize(ivals);
        if (ni > 2) FAIL("idle: höchstens 2 Werte, es sind %d", ni);
        cfg->n_idle_items = ni;
        for (int i = 0; i < ni; i++) {
            const cJSON *vj = cJSON_GetArrayItem(ivals, i);
            char where[32];
            snprintf(where, sizeof(where), "idle, Wert %d", i + 1);
            if (!cJSON_IsObject(vj)) FAIL("%s: muss ein Objekt sein", where);
            tile_cfg_t *ic = &cfg->idle_items[i];
            memset(ic, 0, sizeof(*ic));
            ic->type = TILE_VALUE;
            ic->w = ic->h = 1;
            const cJSON *so = cJSON_GetObjectItem(vj, "source");
            if (!cJSON_IsString(so) || !so->valuestring[0] || !check_source(so->valuestring))
                FAIL("%s: 'source' fehlt oder ist ungültig", where);
            ic->entity = a_strdup(ar, so->valuestring);
            if (!read_ltext(ar, vj, "title", &ic->title, where, err, err_len)) goto fail;
            const cJSON *fo = cJSON_GetObjectItem(vj, "format");
            int fmt = FMT_TEXT;
            if (fo) {
                fmt = cJSON_IsString(fo) ? find_name(fo->valuestring, k_fmt_names, COUNT(k_fmt_names)) : -1;
                if (fmt < 0) FAIL("%s: unbekanntes 'format'", where);
            }
            ic->fmt = (fmt_t)fmt;
            const cJSON *mx = cJSON_GetObjectItem(vj, "max");
            if (mx) {
                if (!cJSON_IsNumber(mx) || mx->valuedouble < 0) FAIL("%s: 'max' muss eine Zahl ab 0 sein", where);
                ic->max = (float)mx->valuedouble;
            }
            if (!ic->entity || !ic->title.de) FAIL("zu wenig Speicher");
        }
    }

    const cJSON *pages = cJSON_GetObjectItem(root, "pages");
    if (!cJSON_IsArray(pages)) FAIL("Konfiguration: 'pages' fehlt oder ist keine Liste");
    int np = cJSON_GetArraySize(pages);
    if (np < 1 || np > MAX_PAGES) FAIL("Konfiguration: 1 bis %d Seiten erlaubt, es sind %d", MAX_PAGES, np);
    page_cfg_t *page_arr = a_alloc(ar, sizeof(page_cfg_t) * np);
    if (!page_arr) FAIL("zu wenig Speicher");
    cfg->pages = page_arr;
    cfg->n_pages = np;

    for (int p = 0; p < np; p++) {
        const cJSON *pj = cJSON_GetArrayItem(pages, p);
        char where[48];
        snprintf(where, sizeof(where), "Seite %d", p + 1);
        if (!cJSON_IsObject(pj)) FAIL("%s: muss ein Objekt sein", where);
        page_cfg_t *pg = &page_arr[p];
        if (!read_ltext(ar, pj, "title", &pg->title, where, err, err_len)) goto fail;
        const cJSON *ic = cJSON_GetObjectItem(pj, "icon");
        pg->icon = NULL;
        if (ic) {
            if (!cJSON_IsString(ic) || !check_icon(ic->valuestring))
                FAIL("%s: Symbol '%s' unbekannt", where, cJSON_IsString(ic) ? ic->valuestring : "?");
            pg->icon = a_strdup(ar, ic->valuestring);
        }
        int rows = GRID_ROWS;
        if (!read_int(pj, "rows", 1, MAX_ROWS, GRID_ROWS, &rows, where, err, err_len)) goto fail;
        pg->rows = (uint8_t)rows;

        const cJSON *tiles = cJSON_GetObjectItem(pj, "tiles");
        if (!cJSON_IsArray(tiles)) FAIL("%s: 'tiles' fehlt oder ist keine Liste", where);
        int nt = cJSON_GetArraySize(tiles);
        if (nt > MAX_TILES_PER_PAGE) FAIL("%s: höchstens %d Kacheln, es sind %d", where, MAX_TILES_PER_PAGE, nt);
        tile_cfg_t *tile_arr = a_alloc(ar, sizeof(tile_cfg_t) * (nt ? nt : 1));
        if (!tile_arr) FAIL("zu wenig Speicher");
        pg->tiles = tile_arr;
        pg->n_tiles = nt;

        uint8_t occ[MAX_ROWS][GRID_COLS] = { { 0 } };     // belegt von Kachel Nr. (ab 1)
        for (int t = 0; t < nt; t++) {
            const cJSON *tj = cJSON_GetArrayItem(tiles, t);
            snprintf(where, sizeof(where), "Seite %d, Kachel %d", p + 1, t + 1);
            if (!cJSON_IsObject(tj)) FAIL("%s: muss ein Objekt sein", where);
            tile_cfg_t *tc = &tile_arr[t];

            const cJSON *ty = cJSON_GetObjectItem(tj, "type");
            int type = cJSON_IsString(ty) ? find_name(ty->valuestring, k_type_names, COUNT(k_type_names)) : -1;
            if (type < 0) FAIL("%s: 'type' muss \"value\" oder \"button\" sein", where);
            tc->type = (tile_type_t)type;

            int x, y, w, h;
            if (!read_int(tj, "x", 0, GRID_COLS - 1, 0, &x, where, err, err_len)) goto fail;
            if (!read_int(tj, "y", 0, rows - 1, 0, &y, where, err, err_len)) goto fail;
            if (!read_int(tj, "w", 1, GRID_COLS, 1, &w, where, err, err_len)) goto fail;
            if (!read_int(tj, "h", 1, rows, 1, &h, where, err, err_len)) goto fail;
            if (x + w > GRID_COLS) FAIL("%s: ragt rechts aus dem Raster (x + w ist %d, höchstens %d)", where, x + w, GRID_COLS);
            if (y + h > rows) FAIL("%s: ragt unten aus dem Raster (y + h ist %d, höchstens %d Reihen)", where, y + h, rows);
            for (int yy = y; yy < y + h; yy++) {
                for (int xx = x; xx < x + w; xx++) {
                    if (occ[yy][xx]) FAIL("%s: überlappt mit Kachel %d", where, occ[yy][xx]);
                    occ[yy][xx] = (uint8_t)(t + 1);
                }
            }
            tc->x = (uint8_t)x; tc->y = (uint8_t)y; tc->w = (uint8_t)w; tc->h = (uint8_t)h;

            const cJSON *so = cJSON_GetObjectItem(tj, "source");
            const char *source = "";
            if (so) {
                if (!cJSON_IsString(so) || !check_source(so->valuestring))
                    FAIL("%s: 'source' ungültig (erwartet z. B. \"light.buero\" oder \"pc:volume\")", where);
                source = so->valuestring;
            }
            tc->entity = a_strdup(ar, source);

            if (!read_ltext(ar, tj, "title", &tc->title, where, err, err_len)) goto fail;

            const cJSON *ico = cJSON_GetObjectItem(tj, "icon");
            tc->icon = NULL;
            if (ico) {
                if (!cJSON_IsString(ico) || !check_icon(ico->valuestring))
                    FAIL("%s: Symbol '%s' unbekannt", where, cJSON_IsString(ico) ? ico->valuestring : "?");
                if (ico->valuestring[0]) tc->icon = a_strdup(ar, ico->valuestring);
            }

            const cJSON *fo = cJSON_GetObjectItem(tj, "format");
            int fmt = (tc->type == TILE_BUTTON) ? FMT_ONOFF : FMT_TEXT;
            if (fo) {
                fmt = cJSON_IsString(fo) ? find_name(fo->valuestring, k_fmt_names, COUNT(k_fmt_names)) : -1;
                if (fmt < 0) FAIL("%s: unbekanntes 'format'", where);
            }
            tc->fmt = (fmt_t)fmt;

            const cJSON *mx = cJSON_GetObjectItem(tj, "max");
            tc->max = 0;
            if (mx) {
                if (!cJSON_IsNumber(mx) || mx->valuedouble < 0) FAIL("%s: 'max' muss eine Zahl ab 0 sein", where);
                tc->max = (float)mx->valuedouble;
            }

            const cJSON *tr = cJSON_GetObjectItem(tj, "trigger");
            int trig = TRIG_NONE;
            if (tr) {
                trig = cJSON_IsString(tr) ? find_name(tr->valuestring, k_trig_names, COUNT(k_trig_names)) : -1;
                if (trig < 0) FAIL("%s: unbekannter 'trigger' (none, tap, hold, confirm)", where);
            }
            tc->trigger = (trigger_t)trig;

            int hold = 0;
            if (!read_int(tj, "hold_ms", 100, 5000, 0, &hold, where, err, err_len)) goto fail;
            tc->hold_ms = (uint16_t)hold;

            const cJSON *ac = cJSON_GetObjectItem(tj, "action");
            tc->action = NULL;
            if (ac && !(cJSON_IsString(ac) && !ac->valuestring[0])) {
                if (!cJSON_IsString(ac) || strncmp(ac->valuestring, "pc:", 3) || !id_chars_ok(ac->valuestring + 3, 31))
                    FAIL("%s: 'action' muss \"pc:<Kennung>\" sein", where);
                tc->action = a_strdup(ar, ac->valuestring);
            }

            if (tc->type == TILE_BUTTON && tc->trigger != TRIG_NONE) {
                bool pc_source = !strncmp(tc->entity, "pc:", 3);
                if (!tc->action && (!tc->entity[0] || pc_source))
                    FAIL("%s: Button braucht eine 'action' oder eine HA-Entität in 'source'", where);
            }
            if (!tc->entity || !tc->title.de) FAIL("zu wenig Speicher");
        }
    }

    cJSON_Delete(root);
    return cfg;

fail:
    cJSON_Delete(root);
    config_free(cfg);
    return NULL;
}

// ================================================================================================
// JSON schreiben
// ================================================================================================
static void add_ltext(cJSON *o, const char *key, ltext_t t)
{
    const char *de = t.de ? t.de : "";
    const char *en = t.en ? t.en : "";
    if (!strcmp(de, en)) {
        cJSON_AddStringToObject(o, key, de);
    } else {
        cJSON *l = cJSON_AddObjectToObject(o, key);
        cJSON_AddStringToObject(l, "de", de);
        cJSON_AddStringToObject(l, "en", en);
    }
}

char *config_serialize(const ob_config_t *cfg)
{
    cJSON *root = cJSON_CreateObject();
    cJSON_AddNumberToObject(root, "v", 1);
    cJSON_AddStringToObject(root, "language", cfg->language);
    cJSON_AddStringToObject(root, "theme", cfg->theme);
    cJSON_AddNumberToObject(root, "brightness", cfg->brightness);
    cJSON_AddNumberToObject(root, "hold_ms", cfg->hold_ms);
    cJSON *idle = cJSON_AddObjectToObject(root, "idle");
    cJSON_AddNumberToObject(idle, "timeout_s", cfg->idle_s);
    cJSON_AddNumberToObject(idle, "brightness", cfg->idle_brightness);
    cJSON_AddNumberToObject(idle, "move_s", cfg->idle_move_s);
    cJSON *ivals = cJSON_AddArrayToObject(idle, "values");
    for (int i = 0; i < cfg->n_idle_items; i++) {
        const tile_cfg_t *ic = &cfg->idle_items[i];
        cJSON *vj = cJSON_CreateObject();
        cJSON_AddStringToObject(vj, "source", ic->entity);
        add_ltext(vj, "title", ic->title);
        cJSON_AddStringToObject(vj, "format", k_fmt_names[ic->fmt]);
        if (ic->max > 0) cJSON_AddNumberToObject(vj, "max", ic->max);
        cJSON_AddItemToArray(ivals, vj);
    }

    cJSON *pages = cJSON_AddArrayToObject(root, "pages");
    for (int p = 0; p < cfg->n_pages; p++) {
        const page_cfg_t *pg = &cfg->pages[p];
        cJSON *pj = cJSON_CreateObject();
        add_ltext(pj, "title", pg->title);
        if (pg->icon) cJSON_AddStringToObject(pj, "icon", pg->icon);
        cJSON_AddNumberToObject(pj, "rows", pg->rows ? pg->rows : GRID_ROWS);
        cJSON *tiles = cJSON_AddArrayToObject(pj, "tiles");
        for (int t = 0; t < pg->n_tiles; t++) {
            const tile_cfg_t *tc = &pg->tiles[t];
            cJSON *tj = cJSON_CreateObject();
            cJSON_AddStringToObject(tj, "type", k_type_names[tc->type]);
            cJSON_AddNumberToObject(tj, "x", tc->x);
            cJSON_AddNumberToObject(tj, "y", tc->y);
            cJSON_AddNumberToObject(tj, "w", tc->w);
            cJSON_AddNumberToObject(tj, "h", tc->h);
            if (tc->entity && tc->entity[0]) cJSON_AddStringToObject(tj, "source", tc->entity);
            add_ltext(tj, "title", tc->title);
            if (tc->icon && tc->icon[0]) cJSON_AddStringToObject(tj, "icon", tc->icon);
            cJSON_AddStringToObject(tj, "format", k_fmt_names[tc->fmt]);
            if (tc->max > 0) cJSON_AddNumberToObject(tj, "max", tc->max);
            if (tc->type == TILE_BUTTON) cJSON_AddStringToObject(tj, "trigger", k_trig_names[tc->trigger]);
            if (tc->hold_ms) cJSON_AddNumberToObject(tj, "hold_ms", tc->hold_ms);
            if (tc->action && tc->action[0]) cJSON_AddStringToObject(tj, "action", tc->action);
            cJSON_AddItemToArray(tiles, tj);
        }
        cJSON_AddItemToArray(pages, pj);
    }
    char *txt = cJSON_PrintUnformatted(root);
    cJSON_Delete(root);
    return txt;     // mit malloc erzeugt: free() genuegt
}

// ================================================================================================
// Speicher im Flash (Partition "storage"): Kopf mit Pruefsumme, dann der JSON-Text
// ================================================================================================
#define STORE_MAGIC   0x4642424Fu    // "OBBF"
#define STORE_MAX     (64 * 1024)

typedef struct {
    uint32_t magic;
    uint32_t len;
    uint32_t crc;
    uint32_t reserved;
} store_hdr_t;

uint32_t config_crc32(const uint8_t *data, size_t len)
{
    uint32_t c = 0xFFFFFFFFu;
    for (size_t i = 0; i < len; i++) {
        c ^= data[i];
        for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint32_t)-(int32_t)(c & 1));
    }
    return ~c;
}

static const esp_partition_t *find_storage(void)
{
    return esp_partition_find_first(ESP_PARTITION_TYPE_DATA, ESP_PARTITION_SUBTYPE_ANY, "storage");
}

esp_err_t config_store(const char *json, size_t len)
{
    const esp_partition_t *p = find_storage();
    if (!p) return ESP_ERR_NOT_FOUND;
    if (len == 0 || len > STORE_MAX) return ESP_ERR_INVALID_SIZE;
    size_t total = (sizeof(store_hdr_t) + len + 4095) & ~(size_t)4095;
    esp_err_t err = esp_partition_erase_range(p, 0, total);
    if (err != ESP_OK) return err;
    err = esp_partition_write(p, sizeof(store_hdr_t), json, len);
    if (err != ESP_OK) return err;
    store_hdr_t hdr = { STORE_MAGIC, (uint32_t)len, config_crc32((const uint8_t *)json, len), 0 };
    return esp_partition_write(p, 0, &hdr, sizeof(hdr));    // Kopf zuletzt: bei Stromausfall davor bleibt der Eintrag ungueltig
}

esp_err_t config_erase(void)
{
    const esp_partition_t *p = find_storage();
    if (!p) return ESP_ERR_NOT_FOUND;
    return esp_partition_erase_range(p, 0, 4096);
}

void config_init(void)
{
    const esp_partition_t *p = find_storage();
    if (!p) {
        ESP_LOGW(TAG, "Partition 'storage' fehlt, Standardkonfiguration");
        return;
    }
    store_hdr_t hdr;
    if (esp_partition_read(p, 0, &hdr, sizeof(hdr)) != ESP_OK || hdr.magic != STORE_MAGIC ||
        hdr.len == 0 || hdr.len > STORE_MAX) {
        ESP_LOGI(TAG, "keine gespeicherte Konfiguration, Standardkonfiguration");
        return;
    }
    char *buf = malloc(hdr.len + 1);
    if (!buf) return;
    if (esp_partition_read(p, sizeof(hdr), buf, hdr.len) != ESP_OK ||
        config_crc32((const uint8_t *)buf, hdr.len) != hdr.crc) {
        ESP_LOGW(TAG, "gespeicherte Konfiguration beschaedigt, Standardkonfiguration");
        free(buf);
        return;
    }
    buf[hdr.len] = 0;
    char err[160];
    ob_config_t *cfg = config_parse(buf, hdr.len, err, sizeof(err));
    free(buf);
    if (!cfg) {
        ESP_LOGW(TAG, "gespeicherte Konfiguration ungueltig (%s), Standardkonfiguration", err);
        return;
    }
    config_activate(cfg);
    ESP_LOGI(TAG, "gespeicherte Konfiguration geladen (%u Bytes, %d Seiten)", (unsigned)hdr.len, cfg->n_pages);
}
