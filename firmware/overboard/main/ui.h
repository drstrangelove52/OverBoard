#pragma once
#include <stdbool.h>

// Entitaeten, die das Geraet von Home Assistant abonniert (ergibt sich aus den Kacheln der Konfiguration).
int ui_entity_count(void);
const char *ui_entity_id(int index);

void ui_init(void);
// Oberflaeche nach einer Konfigurationsaenderung neu aufbauen (Zustaende gehen bis zur naechsten Meldung verloren).
void ui_reload(void);

// Neuen Zustand einer HA-Entitaet anzeigen (thread-safe, darf aus jedem Task aufgerufen werden)
void ui_set_entity(const char *entity_id, const char *state);

// Kalender-Kacheln: die naechsten Termine vom PC-Programm (Datum "YYYY-MM-DD", Uhrzeit "HH:MM" oder "" = ganztaegig, Titel).
#define CAL_MAX 8
typedef struct { char date[11]; char time[6]; char title[100]; } cal_event_t;
void ui_set_calendar(const cal_event_t *ev, int n);
// Verbindungsstatus als Symbole oben rechts.
void ui_set_status(bool wifi, bool ha);
void ui_set_mqtt(bool connected);
void ui_set_pc(bool connected);   // Verbindung zur PC-App (USB)
void ui_set_pc_away(bool away);   // PC weg (schlaeft, gesperrt, aus, Programm beendet) oder wieder da; steuert "Display aus"

typedef enum { BANNER_INFO, BANNER_OK, BANNER_WARN, BANNER_ALARM } banner_level_t;
// Banner ueber allen Seiten einblenden (thread-safe). duration_s = 0: bleibt bis zum Antippen.
// Ein neues Banner ersetzt das aktuelle.
void ui_show_banner(const char *title, const char *text, banner_level_t level, int duration_s);
