# Seiten für OverBoard mit Claude Code gestalten

Diese Anleitung ist für Claude Code (oder einen anderen Assistenten mit Dateizugriff) und für dich. Sie beschreibt, wie du dir die
Seiten auf dem Display von einem Assistenten bauen lässt. Die Konfiguration ist eine JSON-Datei. Der Assistent bearbeitet sie, prüft
sie, zeichnet eine Vorschau, schaut sie sich an und schickt sie, wenn du es freigibst, aufs Display.

## Voraussetzungen

- OverBoard (Windows-Programm) ist installiert und **läuft**, das Display ist per USB 2.0 verbunden. Für `--send` und `--get` muss das
  Programm laufen, die anderen Befehle gehen auch ohne.
- Die Befehle sind Teile der Programmdatei. In diesem Text steht `overboard` für `OverBoardAgent.exe` (Standard: `%LOCALAPPDATA%\Programs\OverBoard\OverBoardAgent.exe`).

## Der Arbeitszyklus

1. **Aktuellen Stand holen:** `overboard --get layout.json` schreibt die Konfiguration des Displays in eine Datei.
2. **Bearbeiten:** Die Datei ändern (Seiten, Kacheln, Einstellungen).
3. **Prüfen:** `overboard --validate layout.json` meldet Fehler im Klartext (dieselbe Prüfung wie im Editor und im Gerät) und Hinweise zu
   unbekannten Aktionen.
4. **Ansehen:** `overboard --export-preview layout.json seite1.png 0` zeichnet das Display als Bild (1024 × 600). Die letzte Zahl ist die
   Seite (ab 0). Das Bild ansehen und prüfen: Passt alles? Ist ein Titel abgeschnitten? Sind Kacheln zu klein? Gibt es Lücken?
5. **Nachbessern**, bis es passt.
6. **Senden:** `overboard --send layout.json`. **Erst nach Rückfrage bei der Person**, denn das ersetzt die Konfiguration auf dem Display.
   Ist der Editor im Programm gerade offen, dort danach **Vom Gerät laden** drücken, sonst überschreibt der nächste Edit im Editor die
   Änderung.

## Nachschlagen (nicht raten!)

| Befehl | Zweck |
|---|---|
| `overboard --list-icons [Filter]` | Alle Symbolnamen (Material Design Icons), z. B. `--list-icons volume` |
| `overboard --list-actions` | Aktionen für Tasten (`pc:<Kennung>`), eingebaute und eigene |
| `overboard --list-sources` | PC-Werte für Anzeige- und Verlaufs-Kacheln (`pc:volume`, `pc:mute`, `pc:mic_mute`, `pc:cpu`, `pc:ram`, `pc:net_down`, `pc:net_up`) |
| `overboard --list-entities [Filter]` | Home-Assistant-Entitäten mit Namen, aktuellem Wert und Einheit, z. B. `--list-entities power` |

Symbole, Aktionen und Entitäten **nie erfinden**: Nur das verwenden, was die Listen ausgeben. Unbekannte Symbole und Quellen lehnt die
Prüfung ab, unbekannte Aktionen meldet sie als Hinweis.

## Aufbau der Konfiguration

```json
{
  "language": "de",
  "theme": "dark",
  "brightness": 80,
  "hold_ms": 600,
  "idle": { "timeout_s": 60, "brightness": 8, "move_s": 60, "off_s": 0, "values": [] },
  "sound": { "volume": 60, "click": true, "click_volume": 30 },
  "power": { "pc_away": false, "pc_away_delay_s": 30 },
  "pages": [ { "title": "Haus", "icon": "home", "rows": 4, "tiles": [ … ] } ]
}
```

- Die Seiten (1 bis 8) stehen in `pages`. Die Reihenfolge ist die der Navigationstasten unten auf dem Display.
- `language` (`de`/`en`) und `theme` (`dark`/`light`) gelten fürs ganze Display.
- Titel sind **einfacher Text** (oder `{"de": "...", "en": "..."}`, in diesem Fall gilt die passende Sprache). Ein Zeilenumbruch im Titel ist `\n`.
- Einstellungen zu Ruhemodus, Töne und Energiesparen stehen auf der Seite **Gerät** im Programm. Der Assistent ändert sie nur, wenn die Person es verlangt.

### Seite

| Feld | Bedeutung |
|---|---|
| `title` | Titel der Navigationstaste |
| `icon` | Symbol der Navigationstaste |
| `rows` | Reihen im Raster, 1 bis 6 (Standard 4). Je mehr Reihen, desto niedriger die Kacheln. |
| `tiles` | Kacheln, höchstens 48 |

### Raster

- 8 Spalten, `x` von 0 bis 7, `y` ab 0 bis `rows - 1`. `w` und `h` sind die Breite und Höhe in Zellen.
- Kacheln dürfen **nicht überlappen** und nicht über den Rand ragen (`x + w ≤ 8`, `y + h ≤ rows`).
- Die Zellgrösse ergibt sich aus dem Display (1024 × 600): bei 4 Reihen und mehreren Seiten sind die Zellen etwa 118 × 100 Pixel.

### Kachel

| Feld | Bedeutung |
|---|---|
| `type` | `value` (Anzeige), `button` (Taste), `calendar` (Termine), `radio` (Radio-Player), `chart` (Verlauf als Linie) |
| `x`, `y`, `w`, `h` | Lage und Grösse im Raster |
| `title` | Beschriftung |
| `icon` | Symbol (siehe `--list-icons`) |
| `source` | Datenquelle: Home-Assistant-Entität (`sensor.xyz`) oder PC-Wert (`pc:volume`) |
| `format` | Darstellung bei `value` und `chart`: `power`, `kwh`, `status`, `onoff`, `text`, `percent`, `time`, `mbit` (Netzwerk in Mbit/s) |
| `span` | Nur `chart`: Zeitraum der Linie in Sekunden (10 bis 86400, Standard 120). Die Linie hat 60 Punkte. |
| `max` | Obergrenze für Leistungswerte (blendet Ausreisser aus), nur bei `power`, `kwh`, `percent` |
| `trigger` | Bedienung bei `button`: `none`, `tap` (Antippen), `hold` (halten), `confirm` (Antippen mit Rückfrage), `repeat` (gedrückt halten wiederholt) |
| `hold_ms` | Haltedauer bei `trigger: hold` |
| `action` | Aktion auf dem PC (`pc:<Kennung>`, siehe `--list-actions`) |

### Die fünf Arten

**Anzeige (`value`)**: zeigt einen Wert. Beispiel:
`{ "type": "value", "x": 0, "y": 0, "w": 2, "h": 2, "source": "sensor.inverter_active_power", "title": "Solar", "icon": "solar-power", "format": "power" }`
Eine 1-Reihen-Kachel passt für eine Zahl mit kurzem Titel. Für Zahlen mit Einheit besser `w` 2.

**Taste (`button`)**: löst etwas aus. Zwei Fälle, **nie beide gleichzeitig**:
- *PC-Aktion:* `action` setzen, `trigger` meist `tap`. Für Lauter und Leiser `trigger: repeat`.
  `{ "type": "button", "x": 4, "y": 1, "w": 1, "h": 1, "title": "Stumm", "icon": "volume-mute", "trigger": "tap", "action": "pc:mute.toggle" }`
  Stumm-Tasten leuchten automatisch nach dem Zustand (`source` wird von selbst gesetzt).
- *Home-Assistant-Schalter:* `source` auf die Entität, **keine** `action`. Die Taste schaltet sie um und leuchtet, wenn sie an ist.
  `{ "type": "button", "x": 5, "y": 1, "w": 1, "h": 1, "title": "Büro", "icon": "lightbulb", "trigger": "tap", "source": "light.burotisch" }`
Gefährliche Aktionen (Herunterfahren, Neustart, Ruhezustand, Fenster schliessen) bekommen `trigger: confirm`. Die Liste markiert sie.

**Verlauf (`chart`)**: zeigt einen Zahlenwert als Linie, mit dem aktuellen Wert oben rechts. Für jeden Zahlenwert: PC-Werte (`pc:cpu`, `pc:ram`, `pc:net_down`, `pc:net_up`) oder Home-Assistant-Entitäten (Solarleistung, Temperatur ...). Bei `percent` reicht die Skala immer von 0 bis 100, sonst passt sie sich den Werten an. Das Display kennt nur, was es seit dem Einschalten gesehen hat: nach einem Neustart beginnt die Linie leer.
`{ "type": "chart", "x": 0, "y": 0, "w": 4, "h": 2, "title": "CPU", "source": "pc:cpu", "format": "percent", "span": 120 }`
Gut ab 2 × 1, besser 2 × 2 und grösser. Zwei Kacheln nebeneinander (CPU und Arbeitsspeicher) lesen sich gut.

**Kalender (`calendar`)**: zeigt die nächsten Termine. Die Adresse des Kalenders steht im Programm (Seite Gerät, Einrichtung), nicht in der Datei.
Gut ab `w: 4`, `h: 2`. Jede Zeile braucht etwa 30 Pixel Höhe plus die Titelzeile.
`{ "type": "calendar", "x": 0, "y": 0, "w": 4, "h": 3, "title": "Familienkalender", "icon": "calendar" }`

**Radio (`radio`)**: zeigt Senderlogo, Sender und laufenden Titel, Tippen startet oder stoppt. Die Senderliste pflegt die Person im Programm (Seite Radio).
Am besten `w: 4`, `h: 2`. Zum Wechseln des Senders zwei Tasten mit `pc:radio.next` und `pc:radio.prev` daneben.
`{ "type": "radio", "x": 0, "y": 0, "w": 4, "h": 2, "title": "Radio", "icon": "radio" }`

## Gestaltungsregeln, die sich bewährt haben

- **Gruppieren:** Zusammengehörendes nebeneinander (z. B. Lauter, Leiser, Stumm in einer Reihe). Wichtiges links oben.
- **Einheitliche Tastengrösse:** Tasten sind 1 × 1. Anzeigen für Zahlen 2 × 1 oder 2 × 2. Kalender und Radio 4 × 2 und grösser.
- **Titel kurz halten.** Lange Titel werden abgeschnitten oder brechen um. Für schmale Kacheln (1 Zelle) höchstens etwa 8 Zeichen.
- **Nicht alles auf eine Seite:** Lieber 2 oder 3 Seiten mit je 4 bis 6 Reihen als eine überladene.
- **Symbole passend wählen** und aus `--list-icons` nehmen. Gleiche Art, gleiches Symbol-Prinzip.
- **Leere Zellen sind in Ordnung.** Nicht jede Zelle muss belegt sein.

## Was der Assistent nicht tun soll

- Keine Aktionen erfinden, nichts ausserhalb von `--list-actions`. Eigene Aktionen (Programme, Webseiten) legt die Person im Programm an (Seite Aktionen).
- Nichts senden, ohne vorher zu fragen. Immer erst prüfen und die Vorschau ansehen.
- Zugangsdaten (WLAN, Home-Assistant-Token, MQTT, Kalender-Adresse) nicht in der Konfiguration ablegen. Sie gehören nur ins Programm.
