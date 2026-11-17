#pragma once
#include <stdbool.h>
#include <stdint.h>

// Entitaeten, die das Geraet von Home Assistant abonniert (ergibt sich aus den Kacheln der Konfiguration).
int ui_entity_count(void);
const char *ui_entity_id(int index);

void ui_init(void);
// Oberflaeche nach einer Konfigurationsaenderung neu aufbauen (Zustaende gehen bis zur naechsten Meldung verloren).
void ui_reload(void);

// Neuen Zustand einer HA-Entitaet anzeigen (thread-safe, darf aus jedem Task aufgerufen werden)
void ui_set_entity(const char *entity_id, const char *state);
void ui_press_tile(int page, int idx);   // Vorfuehrmodus des PC-Editors: Kachel wie bei einer Beruehrung ausloesen
void ui_show_page(int page, bool click);   // click: dabei den Tastenklick abspielen (Vorfuehrmodus)      // Seite anzeigen (der PC-Editor haelt das Geraet auf der Seite, die er gerade bearbeitet)

// Kalender-Kacheln: die naechsten Termine vom PC-Programm (Datum "YYYY-MM-DD", Uhrzeit "HH:MM" oder "" = ganztaegig, Titel).
#define CAL_MAX 8
typedef struct { char date[11]; char time[6]; char title[100]; } cal_event_t;
void ui_set_calendar(const cal_event_t *ev, int n);

// Radio-Kachel: Sender, laufender Titel, ob der Player spielt; Senderlogo als RGB565-Bild in Stuecken (128 x 128).
void ui_set_radio(const char *station, const char *title, bool on);
void ui_radio_image(int w, int h, int off, const uint8_t *data, int len);
// Verbindungsstatus als Symbole oben rechts.
void ui_set_status(bool wifi, bool ha);
void ui_set_mqtt(bool connected);
void ui_set_pc(bool connected);   // Verbindung zur PC-App (USB)
void ui_set_pc_away(bool away);   // PC weg (schlaeft, gesperrt, aus, Programm beendet) oder wieder da; steuert "Display aus"

typedef enum { BANNER_INFO, BANNER_OK, BANNER_WARN, BANNER_ALARM } banner_level_t;
// Banner ueber allen Seiten einblenden (thread-safe). duration_s = 0: bleibt bis zum Antippen.
// Ein neues Banner ersetzt das aktuelle.
void ui_show_banner(const char *title, const char *text, banner_level_t level, int duration_s);
