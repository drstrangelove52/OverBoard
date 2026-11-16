#include "i18n.h"

#include <string.h>

static lang_t s_lang = LANG_DE;

typedef struct { const char *key; const char *de; const char *en; } entry_t;

static const entry_t k_text[] = {
    // Zustaende (Home-Connect-Geraete u. a.)
    { "status.ready",          "Bereit",     "Ready" },
    { "status.inactive",       "Bereit",     "Ready" },
    { "status.run",            "Läuft",      "Running" },
    { "status.finished",       "Fertig",     "Finished" },
    { "status.delayedstart",   "Wartet",     "Waiting" },
    { "status.pause",          "Pause",      "Paused" },
    { "status.error",          "Achtung",    "Attention" },
    { "status.actionrequired", "Achtung",    "Attention" },
    { "status.aborting",       "Abbruch",    "Aborting" },
    { "state.on",              "An",         "On" },
    { "state.off",             "Aus",        "Off" },
    // Bedienung
    { "hint.hold",             "Halten zum Schalten", "Hold to switch" },
    { "confirm.question",      "Wirklich schalten?",  "Really switch?" },
    { "confirm.yes",           "Ja",         "Yes" },
    { "confirm.no",            "Abbrechen",  "Cancel" },
    // Ruhemodus
    { "idle.solar",            "Solar",      "Solar" },
    { "idle.house",            "Haus",       "House" },
    // Kalender
    { "cal.today",  "Heute",  "Today" },     { "cal.tomorrow", "Morgen", "Tomorrow" },
    { "cal.none",   "Keine Termine", "No events" },
    { "wds.0", "So", "Sun" }, { "wds.1", "Mo", "Mon" }, { "wds.2", "Di", "Tue" }, { "wds.3", "Mi", "Wed" },
    { "wds.4", "Do", "Thu" }, { "wds.5", "Fr", "Fri" }, { "wds.6", "Sa", "Sat" },
    // Datum
    { "wd.0", "Sonntag",    "Sunday" },    { "wd.1", "Montag",   "Monday" },    { "wd.2", "Dienstag", "Tuesday" },
    { "wd.3", "Mittwoch",   "Wednesday" }, { "wd.4", "Donnerstag", "Thursday" }, { "wd.5", "Freitag", "Friday" },
    { "wd.6", "Samstag",    "Saturday" },
    { "mo.0", "Januar",     "January" },   { "mo.1", "Februar",  "February" },  { "mo.2", "März",     "March" },
    { "mo.3", "April",      "April" },     { "mo.4", "Mai",      "May" },       { "mo.5", "Juni",     "June" },
    { "mo.6", "Juli",       "July" },      { "mo.7", "August",   "August" },    { "mo.8", "September", "September" },
    { "mo.9", "Oktober",    "October" },   { "mo.10", "November", "November" }, { "mo.11", "Dezember", "December" },
};
#define N_TEXT ((int)(sizeof(k_text) / sizeof(k_text[0])))

void i18n_set(lang_t lang) { s_lang = lang; }
lang_t i18n_get(void) { return s_lang; }

bool i18n_set_code(const char *code)
{
    if (!code) return false;
    if (!strcmp(code, "de")) { s_lang = LANG_DE; return true; }
    if (!strcmp(code, "en")) { s_lang = LANG_EN; return true; }
    return false;
}

const char *tr_opt(const char *key)
{
    for (int i = 0; i < N_TEXT; i++) {
        if (!strcmp(k_text[i].key, key)) return s_lang == LANG_EN ? k_text[i].en : k_text[i].de;
    }
    return NULL;
}

const char *tr(const char *key)
{
    const char *t = tr_opt(key);
    return t ? t : key;
}

const char *lt(ltext_t t)
{
    const char *a = s_lang == LANG_EN ? t.en : t.de;
    const char *b = s_lang == LANG_EN ? t.de : t.en;
    return a ? a : (b ? b : "");
}
