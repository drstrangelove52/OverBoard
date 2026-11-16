#include "ui.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "audio.h"
#include "board.h"
#include "config.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "esp_lvgl_port.h"
#include "esp_random.h"
#include "fonts.h"
#include "i18n.h"
#include "lvgl.h"
#include "mdi.h"
#include "net.h"
#include "pclink.h"
#include "theme.h"

static const char *TAG = "ui";

// ---- Raster ---------------------------------------------------------------------------------
// Bildschirm 1024 x 600: schmale Kopfzeile, Raster 8 x 4 (Zelle 114 x 106, Abstand 10), Navigationsleiste.
#define SCR_W   1024
#define GAP     10
#define CELL_W  114
#define CELL_H  106
#define GRID_W  (GRID_COLS * CELL_W + (GRID_COLS - 1) * GAP)
#define GRID_H  (GRID_ROWS * CELL_H + (GRID_ROWS - 1) * GAP)
#define GRID_X  ((SCR_W - GRID_W) / 2)
#define GRID_Y  68
#define GRID_H_FULL (600 - GRID_Y - 12)   // ohne Navigationsleiste: bis zum unteren Rand
#define NAV_Y   (GRID_Y + GRID_H + 10)
#define NAV_H   56
#define PAD     12

// Zwischenspeicher der zuletzt gemeldeten Werte: nach einem Konfigurationswechsel zeigen die neuen Kacheln sofort
// wieder ihren Wert, auch wenn die Quelle (z. B. der PC) gerade nichts Neues meldet.
#define CACHE_MAX 96
static struct { char id[64]; char state[40]; } s_cache[CACHE_MAX];
static int s_cache_n;

static void cache_put(const char *id, const char *state)
{
    int i = 0;
    while (i < s_cache_n && strcmp(s_cache[i].id, id)) i++;
    if (i == s_cache_n) {
        if (s_cache_n >= CACHE_MAX) return;
        s_cache_n++;
        snprintf(s_cache[i].id, sizeof(s_cache[i].id), "%.63s", id);
    }
    snprintf(s_cache[i].state, sizeof(s_cache[i].state), "%.39s", state ? state : "");
}

static const char *cache_get(const char *id)
{
    for (int i = 0; i < s_cache_n; i++)
        if (!strcmp(s_cache[i].id, id)) return s_cache[i].state;
    return NULL;
}

#define CAL_ROW_H 30                          // Zeilenhoehe der Kalender-Kachel (gleich wie in der Vorschau des PC-Programms)
static cal_event_t s_cal[CAL_MAX];            // zuletzt gemeldete Termine (bleiben nach einem Neuaufbau der Oberflaeche)
static int s_cal_n;

typedef struct {
    const tile_cfg_t *cfg;
    lv_obj_t *obj, *value, *icon, *title, *bar;
    lv_obj_t *cal_when[CAL_MAX], *cal_title[CAL_MAX];   // Kalender-Kachel: Zeilen mit Tag/Zeit und Titel
    int cal_rows, cal_top, cal_w;
    int avail_w, avail_h;      // Platz fuer den Wert (Anzeige-Kachel)
    lv_timer_t *flash_timer;   // Rueckmeldung nach dem Druecken (Aufleuchten, Fehlerblinken)
    bool st_on, st_na;         // zuletzt bekannter Zustand, zum Wiederherstellen nach dem Aufleuchten
    bool flash_err;            // gerade rot (Aktion nicht moeglich)
} tile_t;

static tile_t *s_tiles;
static int s_n_tiles;
static const char **s_entities;
static int s_n_entities;
static lv_obj_t *s_pages[MAX_PAGES];
static lv_obj_t *s_nav_btn[MAX_PAGES];
static int s_n_pages;

static lv_obj_t *s_clock, *s_date;
static lv_obj_t *s_st_wifi, *s_st_ha, *s_st_mqtt, *s_st_pc;
static bool s_wifi_ok, s_ha_ok, s_mqtt_ok, s_pc_ok;

static lv_obj_t *s_banner, *s_banner_title, *s_banner_text;
static lv_timer_t *s_banner_timer;

static lv_obj_t *s_idle, *s_idle_grp, *s_idle_clock, *s_idle_date, *s_idle_info;
static bool s_idle_on;
static int s_idle_ticks, s_idle_dx, s_idle_dy, s_idle_total;
static bool s_screen_off;                 // Display (Hintergrundbeleuchtung) ganz aus
static bool s_pc_away = true;             // bis der PC sich meldet, gilt er als weg
static char s_idle_val[2][40] = { "-", "-" };
static void idle_update_info(void);

static lv_obj_t *s_confirm, *s_confirm_card, *s_confirm_text, *s_confirm_yes, *s_confirm_no;
static tile_t *s_confirm_tile;

int ui_entity_count(void) { return s_n_entities; }
const char *ui_entity_id(int i) { return s_entities[i]; }

static lv_color_t col(uint32_t hex) { return lv_color_hex(hex); }

// ---- Bausteine ------------------------------------------------------------------------------

static lv_obj_t *make_label(lv_obj_t *parent, const lv_font_t *font, uint32_t color, const char *text)
{
    lv_obj_t *l = lv_label_create(parent);
    lv_obj_set_style_text_font(l, font, 0);
    lv_obj_set_style_text_color(l, col(color), 0);
    lv_label_set_text(l, text);
    return l;
}

static lv_obj_t *make_icon(lv_obj_t *parent, const char *name, int px, uint32_t color)
{
    char g[5];
    mdi_utf8(name, g);
    return make_label(parent, ob_icon_font(px), color, g);
}

static void set_icon(lv_obj_t *label, const char *name)
{
    char g[5];
    mdi_utf8(name, g);
    lv_label_set_text(label, g);
}

static lv_obj_t *make_plain(lv_obj_t *parent, int w, int h)
{
    lv_obj_t *o = lv_obj_create(parent);
    lv_obj_set_size(o, w, h);
    lv_obj_set_style_bg_opa(o, LV_OPA_TRANSP, 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_obj_set_style_pad_all(o, 0, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    return o;
}

static lv_obj_t *make_row(lv_obj_t *parent, int gap)
{
    lv_obj_t *r = make_plain(parent, LV_SIZE_CONTENT, LV_SIZE_CONTENT);
    lv_obj_set_flex_flow(r, LV_FLEX_FLOW_ROW);
    lv_obj_set_flex_align(r, LV_FLEX_ALIGN_START, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
    lv_obj_set_style_pad_column(r, gap, 0);
    return r;
}

// ---- Formatierung ---------------------------------------------------------------------------

static void fmt_power(const char *state, float max, char *out, size_t n)
{
    float w = strtof(state, NULL);
    float lim = max > 0 ? max : 20000.f;
    if (w > lim || w < -lim) {                       // unplausible Ausreisser (siehe Node-RED-Findings)
        snprintf(out, n, "-");
    } else if (w >= 1000.f || w <= -1000.f) {
        snprintf(out, n, "%.2f kW", w / 1000.f);
    } else {
        snprintf(out, n, "%d W", (int)(w + (w >= 0 ? 0.5f : -0.5f)));
    }
}

// ISO-Zeitstempel ("2026-11-15T20:39:59+00:00") als Ortszeit: "21:39", an anderen Tagen mit Datum ("16.11. 06:30")
static void fmt_time(const char *iso, char *out, size_t n)
{
    int Y, M, D, h, m, s;
    if (sscanf(iso, "%d-%d-%dT%d:%d:%d", &Y, &M, &D, &h, &m, &s) != 6) {
        snprintf(out, n, "%s", iso);
        return;
    }
    int off_min = 0;
    const char *z = strchr(iso + 10, '+');
    if (!z) z = strchr(iso + 10, '-');
    if (z) {
        int oh = 0, om = 0;
        if (sscanf(z + 1, "%d:%d", &oh, &om) >= 1) off_min = (oh * 60 + om) * (*z == '-' ? -1 : 1);
    }
    // Tage seit 1970 (Zivilkalender), dann in Sekunden
    int y = Y - (M <= 2);
    int era = (y >= 0 ? y : y - 399) / 400;
    int yoe = y - era * 400;
    int doy = (153 * (M + (M > 2 ? -3 : 9)) + 2) / 5 + D - 1;
    int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    int64_t days = (int64_t)era * 146097 + doe - 719468;
    time_t t = (time_t)(days * 86400 + h * 3600 + m * 60 + s - off_min * 60);
    time_t now = time(NULL);
    struct tm lt_tm, now_tm;
    localtime_r(&t, &lt_tm);
    localtime_r(&now, &now_tm);
    if (now > 1700000000 && (lt_tm.tm_yday != now_tm.tm_yday || lt_tm.tm_year != now_tm.tm_year))
        snprintf(out, n, "%d.%d. %02d:%02d", lt_tm.tm_mday, lt_tm.tm_mon + 1, lt_tm.tm_hour, lt_tm.tm_min);
    else
        snprintf(out, n, "%02d:%02d", lt_tm.tm_hour, lt_tm.tm_min);
}

static void format_value(const tile_cfg_t *c, const char *state, char *out, size_t n)
{
    if (!state || !strcmp(state, "unavailable") || !strcmp(state, "unknown")) {
        snprintf(out, n, "-");
        return;
    }
    switch (c->fmt) {
    case FMT_POWER:
        fmt_power(state, c->max, out, n);
        break;
    case FMT_KWH:
        snprintf(out, n, "%.1f kWh", strtof(state, NULL));
        break;
    case FMT_PERCENT:
        snprintf(out, n, "%d %%", (int)(strtof(state, NULL) + 0.5f));
        break;
    case FMT_TIME:
        fmt_time(state, out, n);
        break;
    case FMT_STATUS: {
        char key[40];
        snprintf(key, sizeof(key), "status.%s", state);
        const char *t = tr_opt(key);
        snprintf(out, n, "%s", t ? t : state);
        break;
    }
    case FMT_ONOFF:
        snprintf(out, n, "%s", !strcmp(state, "on") ? tr("state.on") : !strcmp(state, "off") ? tr("state.off") : state);
        break;
    case FMT_TEXT:
        snprintf(out, n, "%s", state);
        break;
    }
}

// Groesste Schrift, in der der Text in die Flaeche passt.
static const lv_font_t *fit_font(const char *text, int max_w, int max_h)
{
    static const int sizes[] = { 64, 48, 36, 28, 20 };
    for (size_t i = 0; i < sizeof(sizes) / sizeof(sizes[0]); i++) {
        const lv_font_t *f = ob_font(sizes[i]);
        lv_point_t sz;
        lv_text_get_size(&sz, text, f, 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
        if (sz.x <= max_w && sz.y <= max_h) return f;
    }
    return ob_font(20);
}

// ---- Bedienung ------------------------------------------------------------------------------

static void bar_exec(void *obj, int32_t v) { lv_bar_set_value((lv_obj_t *)obj, v, LV_ANIM_OFF); }

static void hold_done(lv_anim_t *a);

// Halten: der Balken laeuft in der Haltedauer von 0 auf 100, geschaltet wird, sobald er voll ist.
// Loslassen vorher bricht ab (lv_anim_delete ruft den Abschluss nicht auf).
static void bar_start(tile_t *t)
{
    int ms = t->cfg->hold_ms ? t->cfg->hold_ms : ob_config()->hold_ms;
    lv_anim_t a;
    lv_anim_init(&a);
    lv_anim_set_var(&a, t->bar);
    lv_anim_set_exec_cb(&a, bar_exec);
    lv_anim_set_values(&a, 0, 100);
    lv_anim_set_duration(&a, ms > 0 ? ms : 600);
    lv_anim_set_user_data(&a, t);
    lv_anim_set_completed_cb(&a, hold_done);
    lv_anim_start(&a);
}

static void bar_reset(tile_t *t)
{
    lv_anim_delete(t->bar, bar_exec);
    lv_bar_set_value(t->bar, 0, LV_ANIM_OFF);
}

static void flash_press(tile_t *t);
static void flash_release(tile_t *t);
static void flash_error(tile_t *t);
static bool tile_ready(const tile_t *t);

// Aktion der Kachel ausloesen. false, wenn sie nicht ausgefuehrt werden konnte (kein PC, HA nicht bereit).
static bool do_toggle(tile_t *t)
{
    const char *act = t->cfg->action;
    if (act && !strncmp(act, "pc:", 3)) {           // Aktion auf dem PC (nur die Kennung wird gesendet)
        bool ok = pclink_action(act + 3);
        if (!ok) ESP_LOGW(TAG, "Kein PC verbunden, '%s' verworfen", act);
        return ok;
    }
    char domain[16];
    const char *dot = strchr(t->cfg->entity, '.');
    if (!dot || (size_t)(dot - t->cfg->entity) >= sizeof(domain)) return false;
    memcpy(domain, t->cfg->entity, dot - t->cfg->entity);
    domain[dot - t->cfg->entity] = 0;
    return net_ha_call_service(domain, "toggle", t->cfg->entity);   // der neue Zustand kommt ueber das Abo zurueck
}

static void hold_done(lv_anim_t *a)
{
    tile_t *t = lv_anim_get_user_data(a);
    if (!t) return;
    if (!do_toggle(t)) flash_error(t);
    lv_bar_set_value(t->bar, 0, LV_ANIM_OFF);
}

static void confirm_show(tile_t *t)
{
    const theme_t *th = theme();
    s_confirm_tile = t;
    lv_obj_set_style_bg_color(s_confirm_card, col(th->tile), 0);
    lv_obj_set_style_text_color(s_confirm_text, col(th->value), 0);
    lv_obj_set_style_bg_color(s_confirm_yes, col(th->accent), 0);
    lv_obj_set_style_text_color(lv_obj_get_child(s_confirm_yes, 0), col(th->on_accent), 0);
    lv_obj_set_style_bg_color(s_confirm_no, col(th->nav), 0);
    lv_obj_set_style_text_color(lv_obj_get_child(s_confirm_no, 0), col(th->value), 0);
    lv_label_set_text_fmt(s_confirm_text, "%s\n%s", lt(t->cfg->title), tr("confirm.question"));
    lv_label_set_text(lv_obj_get_child(s_confirm_yes, 0), tr("confirm.yes"));
    lv_label_set_text(lv_obj_get_child(s_confirm_no, 0), tr("confirm.no"));
    lv_obj_remove_flag(s_confirm, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(s_confirm);
}

static void confirm_cancel(lv_event_t *e)
{
    audio_click();
    (void)e;
    s_confirm_tile = NULL;
    lv_obj_add_flag(s_confirm, LV_OBJ_FLAG_HIDDEN);
}

static void confirm_yes(lv_event_t *e)
{
    audio_click();
    (void)e;
    tile_t *t = s_confirm_tile;
    s_confirm_tile = NULL;
    lv_obj_add_flag(s_confirm, LV_OBJ_FLAG_HIDDEN);
    if (t && !do_toggle(t)) flash_error(t);
}

static void confirm_build(void)
{
    s_confirm = make_plain(lv_layer_top(), SCR_W, 600);
    lv_obj_set_pos(s_confirm, 0, 0);
    lv_obj_set_style_bg_color(s_confirm, col(0x000000), 0);
    lv_obj_set_style_bg_opa(s_confirm, LV_OPA_60, 0);
    lv_obj_add_flag(s_confirm, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_event_cb(s_confirm, confirm_cancel, LV_EVENT_CLICKED, NULL);   // Tippen daneben bricht ab

    s_confirm_card = lv_obj_create(s_confirm);
    lv_obj_set_size(s_confirm_card, 560, 250);
    lv_obj_center(s_confirm_card);
    lv_obj_set_style_radius(s_confirm_card, 20, 0);
    lv_obj_set_style_border_width(s_confirm_card, 0, 0);
    lv_obj_set_style_pad_all(s_confirm_card, 24, 0);
    lv_obj_remove_flag(s_confirm_card, LV_OBJ_FLAG_SCROLLABLE);

    s_confirm_text = make_label(s_confirm_card, ob_font(28), 0xFFFFFF, "");
    lv_obj_set_style_text_align(s_confirm_text, LV_TEXT_ALIGN_CENTER, 0);
    lv_obj_align(s_confirm_text, LV_ALIGN_TOP_MID, 0, 6);

    s_confirm_no = lv_button_create(s_confirm_card);
    lv_obj_set_size(s_confirm_no, 230, 64);
    lv_obj_align(s_confirm_no, LV_ALIGN_BOTTOM_LEFT, 0, 0);
    lv_obj_set_style_radius(s_confirm_no, 32, 0);
    lv_obj_set_style_shadow_width(s_confirm_no, 0, 0);
    lv_obj_center(make_label(s_confirm_no, ob_font(28), 0xFFFFFF, ""));
    lv_obj_add_event_cb(s_confirm_no, confirm_cancel, LV_EVENT_CLICKED, NULL);

    s_confirm_yes = lv_button_create(s_confirm_card);
    lv_obj_set_size(s_confirm_yes, 230, 64);
    lv_obj_align(s_confirm_yes, LV_ALIGN_BOTTOM_RIGHT, 0, 0);
    lv_obj_set_style_radius(s_confirm_yes, 32, 0);
    lv_obj_set_style_shadow_width(s_confirm_yes, 0, 0);
    lv_obj_center(make_label(s_confirm_yes, ob_font(28), 0xFFFFFF, ""));
    lv_obj_add_event_cb(s_confirm_yes, confirm_yes, LV_EVENT_CLICKED, NULL);
}

static void tile_event(lv_event_t *e)
{
    tile_t *t = lv_event_get_user_data(e);
    lv_event_code_t c = lv_event_get_code(e);
    if (c == LV_EVENT_PRESSED && !tile_ready(t)) {      // geht gerade gar nicht: gleich rot, ohne Aufleuchten davor
        flash_error(t);
        return;
    }
    if (c == LV_EVENT_PRESSED) audio_click();           // Tastenklick (wenn eingeschaltet)
    if (t->cfg->trigger != TRIG_HOLD) {                 // beim Halten zeigt der Balken den Druck an
        if (c == LV_EVENT_PRESSED) flash_press(t);
        else if (c == LV_EVENT_RELEASED || c == LV_EVENT_PRESS_LOST) flash_release(t);
    }
    switch (t->cfg->trigger) {
    case TRIG_TAP:
        if (c == LV_EVENT_CLICKED && !do_toggle(t)) flash_error(t);
        break;
    case TRIG_HOLD:
        if (c == LV_EVENT_PRESSED) bar_start(t);
        else if (c == LV_EVENT_RELEASED || c == LV_EVENT_PRESS_LOST) bar_reset(t);
        break;
    case TRIG_CONFIRM:
        if (c == LV_EVENT_CLICKED) {
            if (tile_ready(t)) confirm_show(t);
            else flash_error(t);
        }
        break;
    default:
        break;
    }
}

// ---- Seiten und Navigation ------------------------------------------------------------------

static void show_page(int p)
{
    const theme_t *th = theme();
    for (int i = 0; i < s_n_pages; i++) {
        bool act = (i == p);
        if (act) lv_obj_remove_flag(s_pages[i], LV_OBJ_FLAG_HIDDEN);
        else lv_obj_add_flag(s_pages[i], LV_OBJ_FLAG_HIDDEN);
        if (s_nav_btn[i]) {
            lv_obj_set_style_bg_color(s_nav_btn[i], col(act ? th->accent : th->nav), 0);
            for (int k = 0; k < 2; k++) {
                lv_obj_t *c = lv_obj_get_child(s_nav_btn[i], k);
                if (c) lv_obj_set_style_text_color(c, col(act ? th->on_accent : th->value), 0);
            }
        }
    }
}

static void nav_event(lv_event_t *e)
{
    audio_click();
    show_page((int)(intptr_t)lv_event_get_user_data(e));
}

// Zustand einer Kachel darstellen: Wert, Farben, Schriftgroesse.
static void style_onoff(tile_t *t, const char *state)
{
    const theme_t *th = theme();
    bool on = state && !strcmp(state, "on");
    bool na = !state || !strcmp(state, "unavailable") || !strcmp(state, "unknown");
    t->st_on = on;
    t->st_na = na;
    lv_obj_set_style_bg_color(t->obj, col(on ? th->tile_on : th->tile), 0);
    if (t->icon) lv_obj_set_style_text_color(t->icon, col(on ? th->accent : (na ? th->title : th->value)), 0);
    if (t->value) lv_obj_set_style_text_color(t->value, col(on ? th->accent : th->value), 0);
}

// ---- Rueckmeldung beim Druecken -------------------------------------------------------------
// Beim Druecken leuchtet die Kachel in der Akzentfarbe auf und bleibt auch bei sehr kurzem Tippen eine Moment
// sichtbar. Kann die Aktion nicht ausgefuehrt werden (kein PC, HA nicht bereit), blinkt sie kurz rot.

#define FLASH_MIN_MS 220
#define FLASH_ERR_MS 500

static void paint_normal(tile_t *t)
{
    const theme_t *th = theme();
    lv_obj_set_style_bg_color(t->obj, col(t->st_on ? th->tile_on : th->tile), 0);
    if (t->icon) lv_obj_set_style_text_color(t->icon, col(t->st_on ? th->accent : (t->st_na ? th->title : th->value)), 0);
}

static void paint_flash(tile_t *t, uint32_t bg, uint32_t fg)
{
    lv_obj_set_style_bg_color(t->obj, col(bg), 0);
    if (t->icon) lv_obj_set_style_text_color(t->icon, col(fg), 0);
}

static void flash_cancel(tile_t *t)
{
    if (t->flash_timer) {
        lv_timer_delete(t->flash_timer);
        t->flash_timer = NULL;
    }
}

static void flash_timer_cb(lv_timer_t *timer)
{
    tile_t *t = lv_timer_get_user_data(timer);
    t->flash_timer = NULL;                    // einmaliger Timer loescht sich selbst
    t->flash_err = false;
    paint_normal(t);
}

static void flash_schedule(tile_t *t, uint32_t ms)
{
    flash_cancel(t);
    t->flash_timer = lv_timer_create(flash_timer_cb, ms, t);
    lv_timer_set_repeat_count(t->flash_timer, 1);
}

// Gedrueckt sieht eine Kachel aus wie eine eingeschaltete (gleiche Farbgebung wie ein eingeschaltetes Licht)
static void flash_press(tile_t *t)
{
    flash_cancel(t);
    t->flash_err = false;
    paint_flash(t, theme()->tile_on, theme()->accent);
}

static void flash_release(tile_t *t)
{
    if (t->flash_err) return;                 // Fehleranzeige laeuft schon und beendet sich selbst
    flash_schedule(t, FLASH_MIN_MS);
}

static void flash_error(tile_t *t)
{
    t->flash_err = true;
    paint_flash(t, theme()->bad, 0xFFFFFF);
    flash_schedule(t, FLASH_ERR_MS);
}

// Ist die Aktion der Kachel im Moment ueberhaupt moeglich? (PC verbunden bzw. HA bereit)
static bool tile_ready(const tile_t *t)
{
    const char *act = t->cfg->action;
    if (act && !strncmp(act, "pc:", 3)) return pclink_connected();
    return net_ha_ready();
}

static void apply_state(tile_t *t, const char *state)
{
    char buf[40];
    if (t->value) {
        format_value(t->cfg, state, buf, sizeof(buf));
        lv_obj_set_style_text_font(t->value, fit_font(buf, t->avail_w, t->avail_h), 0);
        lv_label_set_text(t->value, buf);
        lv_obj_align(t->value, LV_ALIGN_BOTTOM_LEFT, 0, 0);
    }
    if (t->cfg->fmt == FMT_ONOFF || t->cfg->type == TILE_BUTTON) style_onoff(t, state);
}

// ---- Kalender-Kachel ------------------------------------------------------------------------

// Tage seit 1970-01-01 fuer ein Datum (Zivilkalender)
static int64_t civil_days(int Y, int M, int D)
{
    int y = Y - (M <= 2);
    int era = (y >= 0 ? y : y - 399) / 400;
    int yoe = y - era * 400;
    int doy = (153 * (M + (M > 2 ? -3 : 9)) + 2) / 5 + D - 1;
    int doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    return (int64_t)era * 146097 + doe - 719468;
}

// "Heute 18:30", "Morgen", "Mi 18.11. 09:00" (ohne gueltige Uhrzeit im Geraet nur "18.11.")
static void cal_when(const cal_event_t *e, char *out, size_t n)
{
    int Y, M, D;
    if (sscanf(e->date, "%d-%d-%d", &Y, &M, &D) != 3) {
        snprintf(out, n, "%s", e->date);
        return;
    }
    int64_t days = civil_days(Y, M, D);
    time_t now = time(NULL);
    char day[24];
    if (now > 1700000000) {
        struct tm lt_tm;
        localtime_r(&now, &lt_tm);
        int64_t diff = days - civil_days(lt_tm.tm_year + 1900, lt_tm.tm_mon + 1, lt_tm.tm_mday);
        if (diff == 0) {
            snprintf(day, sizeof(day), "%s", tr("cal.today"));
        } else if (diff == 1) {
            snprintf(day, sizeof(day), "%s", tr("cal.tomorrow"));
        } else {
            char key[12];
            snprintf(key, sizeof(key), "wds.%d", (int)((days % 7 + 11) % 7));   // 1970-01-01 war ein Donnerstag
            snprintf(day, sizeof(day), "%s %d.%d.", tr(key), D, M);
        }
    } else {
        snprintf(day, sizeof(day), "%d.%d.", D, M);
    }
    if (e->time[0]) snprintf(out, n, "%s %s", day, e->time);
    else snprintf(out, n, "%s", day);
}

static void cal_refresh(tile_t *t)
{
    if (t->cfg->type != TILE_CALENDAR) return;
    char when[64];
    int whenw = 0;
    int shown = s_cal_n < t->cal_rows ? s_cal_n : t->cal_rows;
    for (int i = 0; i < shown; i++) {                       // Breite der ersten Spalte = laengster Eintrag
        cal_when(&s_cal[i], when, sizeof(when));
        lv_point_t sz;
        lv_text_get_size(&sz, when, ob_font(20), 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
        if (sz.x > whenw) whenw = sz.x;
    }
    if (whenw > t->cal_w * 2 / 3) whenw = t->cal_w * 2 / 3;
    for (int i = 0; i < t->cal_rows; i++) {
        if (i >= shown) {
            lv_obj_add_flag(t->cal_when[i], LV_OBJ_FLAG_HIDDEN);
            lv_obj_add_flag(t->cal_title[i], LV_OBJ_FLAG_HIDDEN);
            continue;
        }
        cal_when(&s_cal[i], when, sizeof(when));
        int y = t->cal_top + i * CAL_ROW_H;
        lv_label_set_text(t->cal_when[i], when);
        lv_obj_set_pos(t->cal_when[i], 0, y);
        lv_label_set_text(t->cal_title[i], s_cal[i].title);
        lv_obj_set_width(t->cal_title[i], t->cal_w - whenw - 14);
        lv_obj_set_pos(t->cal_title[i], whenw + 14, y);
        lv_obj_remove_flag(t->cal_when[i], LV_OBJ_FLAG_HIDDEN);
        lv_obj_remove_flag(t->cal_title[i], LV_OBJ_FLAG_HIDDEN);
    }
    if (shown == 0) {                                       // keine Termine: Hinweis in der ersten Zeile
        lv_label_set_text(t->cal_title[0], tr("cal.none"));
        lv_obj_set_width(t->cal_title[0], t->cal_w);
        lv_obj_set_pos(t->cal_title[0], 0, t->cal_top);
        lv_obj_set_style_text_color(t->cal_title[0], col(theme()->title), 0);
        lv_obj_remove_flag(t->cal_title[0], LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_obj_set_style_text_color(t->cal_title[0], col(theme()->value), 0);
    }
}

static void build_tile(lv_obj_t *page, tile_t *t, int cell_h)
{
    const theme_t *th = theme();
    const tile_cfg_t *c = t->cfg;
    int pw = c->w * CELL_W + (c->w - 1) * GAP;
    int ph = c->h * cell_h + (c->h - 1) * GAP;

    lv_obj_t *o = lv_obj_create(page);
    t->obj = o;
    lv_obj_set_size(o, pw, ph);
    lv_obj_set_pos(o, c->x * (CELL_W + GAP), c->y * (cell_h + GAP));
    lv_obj_set_style_bg_color(o, col(th->tile), 0);
    lv_obj_set_style_border_width(o, 0, 0);
    lv_obj_set_style_radius(o, 16, 0);
    lv_obj_set_style_pad_all(o, PAD, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);

    if (c->type == TILE_VALUE) {
        bool narrow = pw < 150;
        t->title = make_label(o, ob_font(narrow ? 14 : 20), th->title, lt(c->title));
        lv_obj_align(t->title, LV_ALIGN_TOP_LEFT, 0, 0);
        if (c->icon) {
            t->icon = make_icon(o, c->icon, narrow ? 24 : 28, th->title);
            lv_obj_align(t->icon, LV_ALIGN_TOP_RIGHT, 0, 0);
        }
        t->value = make_label(o, ob_font(20), th->value, "-");
        t->avail_w = pw - 2 * PAD;
        // Platz fuer den Wert: die Hoehe des Titels (auch mehrzeilig) abziehen
        lv_point_t ts;
        lv_text_get_size(&ts, lt(c->title), ob_font(narrow ? 14 : 20), 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
        t->avail_h = ph - 2 * PAD - (ts.y + (narrow ? 5 : 8));
        lv_obj_align(t->value, LV_ALIGN_BOTTOM_LEFT, 0, 0);
    } else if (c->type == TILE_CALENDAR) {
        bool narrow = pw < 150;
        t->title = make_label(o, ob_font(narrow ? 14 : 20), th->title, lt(c->title));
        lv_obj_align(t->title, LV_ALIGN_TOP_LEFT, 0, 0);
        if (c->icon) {
            t->icon = make_icon(o, c->icon, narrow ? 24 : 28, th->title);
            lv_obj_align(t->icon, LV_ALIGN_TOP_RIGHT, 0, 0);
        }
        lv_point_t ts;
        lv_text_get_size(&ts, lt(c->title), ob_font(narrow ? 14 : 20), 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
        t->cal_top = ts.y + 8;
        t->cal_w = pw - 2 * PAD;
        t->cal_rows = (ph - 2 * PAD - t->cal_top) / CAL_ROW_H;
        if (t->cal_rows < 1) t->cal_rows = 1;
        if (t->cal_rows > CAL_MAX) t->cal_rows = CAL_MAX;
        for (int i = 0; i < t->cal_rows; i++) {
            t->cal_when[i] = make_label(o, ob_font(20), th->accent, "");
            t->cal_title[i] = make_label(o, ob_font(20), th->value, "");
            lv_label_set_long_mode(t->cal_title[i], LV_LABEL_LONG_DOT);
        }
        cal_refresh(t);
    } else {
        bool big = (c->w >= 2 && c->h >= 2);
        t->icon = make_icon(o, c->icon ? c->icon : "help-circle", big ? 64 : 48, th->value);
        lv_obj_align(t->icon, LV_ALIGN_CENTER, 0, big ? -18 : -14);
        t->title = make_label(o, ob_font(big ? 28 : 14), th->title, lt(c->title));
        lv_obj_set_width(t->title, pw - 2 * PAD);
        lv_obj_set_style_text_align(t->title, LV_TEXT_ALIGN_CENTER, 0);
        lv_obj_align(t->title, LV_ALIGN_BOTTOM_MID, 0, 0);
        if (c->trigger == TRIG_HOLD) {            // Fortschritt beim Halten, oben, damit der Finger ihn nicht verdeckt
            t->bar = lv_bar_create(o);
            lv_obj_set_size(t->bar, pw - 2 * PAD, 8);
            lv_obj_align(t->bar, LV_ALIGN_TOP_MID, 0, 0);
            lv_bar_set_range(t->bar, 0, 100);
            lv_obj_set_style_bg_color(t->bar, col(th->nav), LV_PART_MAIN);
            lv_obj_set_style_bg_color(t->bar, col(th->accent), LV_PART_INDICATOR);
        }
    }
    if (c->type == TILE_BUTTON && c->trigger != TRIG_NONE) lv_obj_add_event_cb(o, tile_event, LV_EVENT_ALL, t);
    apply_state(t, NULL);
}

// Jede Entitaet nur einmal abonnieren, auch wenn mehrere Kacheln sie zeigen.
static void collect_entities(void)
{
    const ob_config_t *cfg = ob_config();
    s_entities = calloc(s_n_tiles + 2, sizeof(char *));
    s_n_entities = 0;
    for (int i = 0; i < s_n_tiles + cfg->n_idle_items; i++) {
        const char *e = i < s_n_tiles ? s_tiles[i].cfg->entity : cfg->idle_items[i - s_n_tiles].entity;
        if (!e || !e[0] || !strncmp(e, "pc:", 3)) continue;     // PC-Werte kommen ueber USB, nicht von HA
        bool known = false;
        for (int k = 0; k < s_n_entities; k++)
            if (!strcmp(s_entities[k], e)) known = true;
        if (!known) s_entities[s_n_entities++] = e;
    }
}

// ---- Uhr ------------------------------------------------------------------------------------

static void clock_tick(lv_timer_t *timer)
{
    (void)timer;
    time_t now = time(NULL);
    if (now < 1700000000) {              // Zeit noch nicht per SNTP synchron
        lv_label_set_text(s_clock, "--:--");
        return;
    }
    struct tm tm;
    localtime_r(&now, &tm);
    char k1[8], k2[8], date[64];
    snprintf(k1, sizeof(k1), "wd.%d", tm.tm_wday);
    snprintf(k2, sizeof(k2), "mo.%d", tm.tm_mon);
    if (i18n_get() == LANG_EN) snprintf(date, sizeof(date), "%s, %s %d, %d", tr(k1), tr(k2), tm.tm_mday, tm.tm_year + 1900);
    else snprintf(date, sizeof(date), "%s, %d. %s %d", tr(k1), tm.tm_mday, tr(k2), tm.tm_year + 1900);
    char hm[8];
    snprintf(hm, sizeof(hm), "%02d:%02d", tm.tm_hour, tm.tm_min);
    lv_label_set_text(s_clock, hm);
    lv_label_set_text(s_date, date);
    if (s_idle_clock) {
        lv_label_set_text(s_idle_clock, hm);
        lv_label_set_text(s_idle_date, date);
    }
}

// ---- Banner ---------------------------------------------------------------------------------

static void idle_exit(void);

static void banner_hide(void)
{
    if (s_banner_timer) {
        lv_timer_delete(s_banner_timer);
        s_banner_timer = NULL;
    }
    lv_obj_add_flag(s_banner, LV_OBJ_FLAG_HIDDEN);
}

static void banner_timeout(lv_timer_t *t)
{
    (void)t;
    s_banner_timer = NULL;                    // einmaliger Timer loescht sich selbst
    lv_obj_add_flag(s_banner, LV_OBJ_FLAG_HIDDEN);
}

static void banner_click(lv_event_t *e)
{
    (void)e;
    banner_hide();
}

static void banner_build(void)
{
    s_banner = lv_obj_create(lv_layer_top());
    lv_obj_set_size(s_banner, 984, 104);
    lv_obj_align(s_banner, LV_ALIGN_TOP_MID, 0, 10);
    lv_obj_set_style_radius(s_banner, 20, 0);
    lv_obj_set_style_border_width(s_banner, 0, 0);
    lv_obj_set_style_pad_all(s_banner, 18, 0);
    lv_obj_remove_flag(s_banner, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_add_flag(s_banner, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_event_cb(s_banner, banner_click, LV_EVENT_CLICKED, NULL);
    s_banner_title = make_label(s_banner, ob_font(28), 0xFFFFFF, "");
    lv_obj_align(s_banner_title, LV_ALIGN_TOP_LEFT, 0, 0);
    s_banner_text = make_label(s_banner, ob_font(20), 0xFFFFFF, "");
    lv_obj_align(s_banner_text, LV_ALIGN_TOP_LEFT, 0, 42);
}

void ui_show_banner(const char *title, const char *text, banner_level_t level, int duration_s)
{
    const theme_t *th = theme();
    const uint32_t colors[] = { th->info, th->success, th->warn, th->alarm };
    if (!lvgl_port_lock(200)) return;
    idle_exit();                              // Banner weckt das Display
    if (s_banner_timer) {
        lv_timer_delete(s_banner_timer);
        s_banner_timer = NULL;
    }
    lv_obj_set_style_bg_color(s_banner, col(colors[level]), 0);
    lv_label_set_text(s_banner_title, title ? title : "");
    lv_label_set_text(s_banner_text, text ? text : "");
    lv_obj_remove_flag(s_banner, LV_OBJ_FLAG_HIDDEN);
    lv_obj_move_foreground(s_banner);
    if (duration_s > 0) {
        s_banner_timer = lv_timer_create(banner_timeout, (uint32_t)duration_s * 1000, NULL);
        lv_timer_set_repeat_count(s_banner_timer, 1);
    }
    lvgl_port_unlock();
}

// ---- Ruhemodus ------------------------------------------------------------------------------

#define IDLE_RANGE_X 200   // Verschiebung der Uhrgruppe +/- in Pixeln (gegen Bildretention)
#define IDLE_RANGE_Y 110

static void idle_move(void)
{
    int dx, dy;
    do {
        dx = (int)(esp_random() % (2 * IDLE_RANGE_X + 1)) - IDLE_RANGE_X;
        dy = (int)(esp_random() % (2 * IDLE_RANGE_Y + 1)) - IDLE_RANGE_Y;
    } while (abs(dx - s_idle_dx) + abs(dy - s_idle_dy) < 150);
    s_idle_dx = dx;
    s_idle_dy = dy;
    lv_obj_align(s_idle_grp, LV_ALIGN_CENTER, dx, dy);
}

static void idle_enter(void)
{
    if (s_idle_on) return;
    const theme_t *th = theme();
    s_idle_on = true;
    s_idle_ticks = 0;
    s_idle_total = 0;
    lv_obj_set_style_bg_color(s_idle, col(th->idle_bg), 0);
    lv_obj_set_style_text_color(s_idle_clock, col(th->idle_fg), 0);
    lv_obj_set_style_text_color(s_idle_date, col(th->title), 0);
    lv_obj_set_style_text_color(s_idle_info, col(th->accent), 0);
    idle_move();
    lv_obj_remove_flag(s_idle, LV_OBJ_FLAG_HIDDEN);
    board_set_backlight(ob_config()->idle_brightness);
    ESP_LOGI(TAG, "Ruhemodus an");
}

static void idle_exit(void)
{
    if (!s_idle_on) return;
    s_idle_on = false;
    s_screen_off = false;
    lv_obj_add_flag(s_idle, LV_OBJ_FLAG_HIDDEN);
    board_set_backlight(ob_config()->brightness);
    lv_display_trigger_activity(NULL);        // Inaktivitaetszaehler zuruecksetzen
    ESP_LOGI(TAG, "Ruhemodus aus");
}

static void idle_touch(lv_event_t *e)
{
    (void)e;
    idle_exit();                              // die Beruehrung weckt nur, sie loest nichts darunter aus
}

static void screen_off(void)
{
    if (s_screen_off) return;
    s_screen_off = true;
    board_set_backlight(0);
    ESP_LOGI(TAG, "Display aus");
}

static void idle_check(lv_timer_t *t)
{
    (void)t;
    const ob_config_t *cfg = ob_config();
    bool banner_visible = s_banner && !lv_obj_has_flag(s_banner, LV_OBJ_FLAG_HIDDEN);
    if (s_idle_on) {
        s_idle_total++;
        if (!s_screen_off && cfg->idle_off_s > 0 && s_idle_total >= cfg->idle_off_s) screen_off();
        if (!s_screen_off && ++s_idle_ticks >= cfg->idle_move_s) {
            s_idle_ticks = 0;
            idle_move();
        }
    } else if (!banner_visible && lv_display_get_inactive_time(NULL) > (uint32_t)cfg->idle_s * 1000) {
        idle_enter();
    }
    // PC weg und lange keine Beruehrung: Display aus (ohne auf die normale Ruhemodus-Zeit zu warten)
    if (cfg->pc_away && s_pc_away && !s_screen_off && !banner_visible &&
        lv_display_get_inactive_time(NULL) >= (uint32_t)cfg->pc_away_delay_s * 1000) {
        idle_enter();
        screen_off();
    }
}

// Text der Werte im Ruhemodus neu setzen (bis zu zwei, mit Titel)
static void idle_update_info(void)
{
    if (!s_idle_info) return;
    const ob_config_t *cfg = ob_config();
    char buf[128] = "";
    for (int i = 0; i < cfg->n_idle_items; i++) {
        const char *title = lt(cfg->idle_items[i].title);
        size_t n = strlen(buf);
        snprintf(buf + n, sizeof(buf) - n, "%s%s%s%s", i ? "      " : "", title, title[0] ? " " : "", s_idle_val[i]);
    }
    lv_label_set_text(s_idle_info, buf);
}

static void idle_build(void)
{
    s_idle = make_plain(lv_layer_top(), SCR_W, 600);
    lv_obj_set_pos(s_idle, 0, 0);
    lv_obj_set_style_bg_opa(s_idle, LV_OPA_COVER, 0);
    lv_obj_add_flag(s_idle, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_event_cb(s_idle, idle_touch, LV_EVENT_PRESSED, NULL);

    // Gruppe mit Uhr, Datum und Kurzinfo; wird im Ruhemodus regelmaessig verschoben
    s_idle_grp = make_plain(s_idle, 600, 330);
    lv_obj_remove_flag(s_idle_grp, LV_OBJ_FLAG_CLICKABLE);   // Beruehrungen gehen an die Flaeche dahinter
    lv_obj_align(s_idle_grp, LV_ALIGN_CENTER, 0, 0);

    s_idle_clock = make_label(s_idle_grp, ob_font(120), 0xFFFFFF, "--:--");
    lv_obj_align(s_idle_clock, LV_ALIGN_CENTER, 0, -55);
    s_idle_date = make_label(s_idle_grp, ob_font(28), 0xFFFFFF, "");
    lv_obj_align(s_idle_date, LV_ALIGN_CENTER, 0, 65);
    s_idle_info = make_label(s_idle_grp, ob_font(28), 0xFFFFFF, "");
    lv_obj_align(s_idle_info, LV_ALIGN_CENTER, 0, 125);

    lv_timer_create(idle_check, 1000, NULL);
}

// ---- Gesamtaufbau ---------------------------------------------------------------------------

static void build_main(void)
{
    const theme_t *th = theme();
    const ob_config_t *cfg = ob_config();
    lv_obj_t *scr = lv_screen_active();
    lv_obj_set_style_bg_color(scr, col(th->bg), 0);
    lv_obj_set_style_bg_opa(scr, LV_OPA_COVER, 0);
    lv_obj_remove_flag(scr, LV_OBJ_FLAG_SCROLLABLE);

    // Kopfzeile: Uhr und Datum links, Verbindungssymbole rechts
    lv_obj_t *head = make_plain(scr, GRID_W, 46);
    lv_obj_set_pos(head, GRID_X, 12);
    lv_obj_t *left = make_row(head, 18);
    lv_obj_align(left, LV_ALIGN_LEFT_MID, 0, 0);
    s_clock = make_label(left, ob_font(36), th->value, "--:--");
    s_date = make_label(left, ob_font(20), th->title, "");
    lv_obj_t *right = make_row(head, 20);
    lv_obj_align(right, LV_ALIGN_RIGHT_MID, 0, 0);
    s_st_pc = make_icon(right, "monitor", 28, th->bad);
    s_st_mqtt = make_icon(right, "access-point-network", 28, th->bad);
    s_st_ha = make_icon(right, "home-assistant", 28, th->bad);
    s_st_wifi = make_icon(right, "wifi-off", 28, th->bad);

    // Kacheln aller Seiten
    s_n_pages = cfg->n_pages > MAX_PAGES ? MAX_PAGES : cfg->n_pages;
    s_n_tiles = 0;
    for (int p = 0; p < s_n_pages; p++) s_n_tiles += cfg->pages[p].n_tiles;
    s_tiles = calloc(s_n_tiles ? s_n_tiles : 1, sizeof(tile_t));
    int ti = 0;
    for (int p = 0; p < s_n_pages; p++) {
        int grid_h = s_n_pages > 1 ? GRID_H : GRID_H_FULL;
        s_pages[p] = make_plain(scr, GRID_W, grid_h);
        lv_obj_set_pos(s_pages[p], GRID_X, GRID_Y);
        int rows = cfg->pages[p].rows ? cfg->pages[p].rows : GRID_ROWS;
        int cell_h = (grid_h - (rows - 1) * GAP) / rows;      // Zellhoehe ergibt sich aus der Reihenzahl
        for (int k = 0; k < cfg->pages[p].n_tiles; k++) {
            s_tiles[ti].cfg = &cfg->pages[p].tiles[k];
            build_tile(s_pages[p], &s_tiles[ti], cell_h);
            const char *known = cache_get(s_tiles[ti].cfg->entity);   // zuletzt gemeldeten Wert sofort anzeigen
            if (known) apply_state(&s_tiles[ti], known);
            ti++;
        }
    }
    collect_entities();

    // Navigationsleiste (nur bei mehreren Seiten)
    for (int i = 0; i < MAX_PAGES; i++) s_nav_btn[i] = NULL;
    if (s_n_pages > 1) {
        lv_obj_t *nav = make_plain(scr, GRID_W, NAV_H);
        lv_obj_set_pos(nav, GRID_X, NAV_Y);
        lv_obj_set_flex_flow(nav, LV_FLEX_FLOW_ROW);
        lv_obj_set_flex_align(nav, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
        lv_obj_set_style_pad_column(nav, 16, 0);
        for (int p = 0; p < s_n_pages; p++) {
            lv_obj_t *b = lv_button_create(nav);
            s_nav_btn[p] = b;
            lv_obj_set_size(b, 220, NAV_H);
            lv_obj_set_style_radius(b, 28, 0);
            lv_obj_set_style_shadow_width(b, 0, 0);
            lv_obj_set_flex_flow(b, LV_FLEX_FLOW_ROW);
            lv_obj_set_flex_align(b, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER, LV_FLEX_ALIGN_CENTER);
            lv_obj_set_style_pad_column(b, 10, 0);
            make_icon(b, cfg->pages[p].icon ? cfg->pages[p].icon : "help-circle", 28, th->value);
            make_label(b, ob_font(20), th->value, lt(cfg->pages[p].title));
            lv_obj_add_event_cb(b, nav_event, LV_EVENT_CLICKED, (void *)(intptr_t)p);
        }
    }
    show_page(0);
    clock_tick(NULL);
}

void ui_init(void)
{
    const ob_config_t *cfg = ob_config();
    lvgl_port_lock(0);
    fonts_init();
    i18n_set_code(cfg->language);
    theme_set(cfg->theme);

    idle_build();       // Reihenfolge auf der obersten Ebene: Ruhemodus < Rueckfrage < Banner
    confirm_build();
    banner_build();
    build_main();
    lv_timer_create(clock_tick, 1000, NULL);
    lvgl_port_unlock();
    ESP_LOGI(TAG, "UI aufgebaut (%d Seiten, %d Kacheln, %d Entitaeten)", s_n_pages, s_n_tiles, s_n_entities);
}

void ui_reload(void)
{
    const ob_config_t *cfg = ob_config();
    if (!lvgl_port_lock(1000)) return;
    i18n_set_code(cfg->language);
    theme_set(cfg->theme);
    for (int i = 0; i < s_n_tiles; i++) flash_cancel(&s_tiles[i]);   // offene Timer zeigen sonst auf freigegebene Kacheln
    s_confirm_tile = NULL;                                           // offene Rueckfrage gehoert zu einer alten Kachel
    if (s_confirm) lv_obj_add_flag(s_confirm, LV_OBJ_FLAG_HIDDEN);
    lv_obj_clean(lv_screen_active());
    free(s_tiles);
    free(s_entities);
    s_tiles = NULL;
    s_entities = NULL;
    build_main();
    {   // Werte im Ruhemodus: zuletzt bekannte Werte aus dem Zwischenspeicher
        const ob_config_t *c = ob_config();
        for (int i = 0; i < c->n_idle_items; i++)
            format_value(&c->idle_items[i], cache_get(c->idle_items[i].entity), s_idle_val[i], sizeof(s_idle_val[i]));
        idle_update_info();
    }
    ui_set_status(s_wifi_ok, s_ha_ok);
    ui_set_mqtt(s_mqtt_ok);
    ui_set_pc(s_pc_ok);
    if (!s_idle_on) board_set_backlight(cfg->brightness);
    lvgl_port_unlock();
    ESP_LOGI(TAG, "UI neu aufgebaut (%d Seiten, %d Kacheln, %d Entitaeten)", s_n_pages, s_n_tiles, s_n_entities);
}

// ---- Aktualisierung aus anderen Tasks -------------------------------------------------------

void ui_set_calendar(const cal_event_t *ev, int n)
{
    if (!lvgl_port_lock(200)) {
        ESP_LOGW(TAG, "LVGL-Sperre nicht erhalten, Kalender verworfen");
        return;
    }
    s_cal_n = n > CAL_MAX ? CAL_MAX : n;
    for (int i = 0; i < s_cal_n; i++) s_cal[i] = ev[i];
    for (int i = 0; i < s_n_tiles; i++) cal_refresh(&s_tiles[i]);
    lvgl_port_unlock();
    ESP_LOGI(TAG, "Kalender: %d Termine", s_cal_n);
}

void ui_set_entity(const char *entity_id, const char *state)
{
    ESP_LOGI(TAG, "HA: %s = %.40s", entity_id, state);
    if (!lvgl_port_lock(200)) {
        ESP_LOGW(TAG, "LVGL-Sperre nicht erhalten, Wert verworfen: %s", entity_id);
        return;
    }
    cache_put(entity_id, state);
    for (int i = 0; i < s_n_tiles; i++) {
        if (!strcmp(s_tiles[i].cfg->entity, entity_id)) apply_state(&s_tiles[i], state);
    }
    // Werte im Ruhemodus
    const ob_config_t *icfg = ob_config();
    bool idle_hit = false;
    for (int i = 0; i < icfg->n_idle_items; i++) {
        if (strcmp(icfg->idle_items[i].entity, entity_id)) continue;
        format_value(&icfg->idle_items[i], state, s_idle_val[i], sizeof(s_idle_val[i]));
        idle_hit = true;
    }
    if (idle_hit) idle_update_info();
    lvgl_port_unlock();
}

void ui_set_status(bool wifi, bool ha)
{
    s_wifi_ok = wifi;
    s_ha_ok = ha;
    if (!s_st_wifi || !lvgl_port_lock(100)) return;
    const theme_t *th = theme();
    set_icon(s_st_wifi, wifi ? "wifi" : "wifi-off");
    lv_obj_set_style_text_color(s_st_wifi, col(wifi ? th->ok : th->bad), 0);
    lv_obj_set_style_text_color(s_st_ha, col(ha ? th->ok : th->bad), 0);
    lvgl_port_unlock();
}

void ui_set_pc_away(bool away)
{
    if (!lvgl_port_lock(100)) return;
    if (s_pc_away != away) ESP_LOGI(TAG, "PC %s", away ? "weg" : "wieder da");
    s_pc_away = away;
    if (!away) {                              // PC wieder da (entsperrt, aufgewacht, Programm gestartet): Display an
        lv_display_trigger_activity(NULL);
        if (s_idle_on) idle_exit();
    }
    lvgl_port_unlock();
}

void ui_set_pc(bool connected)
{
    s_pc_ok = connected;
    if (!s_st_pc || !lvgl_port_lock(100)) return;
    lv_obj_set_style_text_color(s_st_pc, col(connected ? theme()->ok : theme()->bad), 0);
    lvgl_port_unlock();
}

void ui_set_mqtt(bool connected)
{
    s_mqtt_ok = connected;
    if (!s_st_mqtt || !lvgl_port_lock(100)) return;
    lv_obj_set_style_text_color(s_st_mqtt, col(connected ? theme()->ok : theme()->bad), 0);
    lvgl_port_unlock();
}
