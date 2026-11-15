# OverBoard-Konfiguration (JSON v1)

Die Konfiguration beschreibt Seiten, Kacheln und Einstellungen des Displays. Sie wird von der PC-App (OverBoard Agent)
über USB auf das Gerät übertragen (Nachrichten siehe `protocol.md`), dort im Flash gespeichert und sofort angewendet.
Fehlt sie oder ist sie ungültig, gilt die eingebaute Standardkonfiguration.

## Beispiel

```json
{
  "v": 1,
  "language": "de",
  "theme": "dark",
  "brightness": 80,
  "hold_ms": 600,
  "idle": { "timeout_s": 60, "brightness": 8, "move_s": 60 },
  "pages": [
    {
      "title": { "de": "Haus", "en": "Home" },
      "icon": "mdi:home",
      "rows": 4,
      "tiles": [
        { "type": "value", "x": 0, "y": 0, "w": 2, "h": 2,
          "source": "sensor.inverter_active_power", "title": { "de": "Solar", "en": "Solar" },
          "icon": "mdi:solar-power", "format": "power" },
        { "type": "button", "x": 4, "y": 1, "w": 1, "h": 1,
          "source": "light.burotisch", "title": "Büro", "icon": "mdi:lightbulb",
          "format": "onoff", "trigger": "tap" },
        { "type": "button", "x": 2, "y": 2, "w": 1, "h": 1,
          "title": "Leiser", "icon": "mdi:volume-minus", "trigger": "tap", "action": "pc:volume.down" }
      ]
    }
  ]
}
```

## Felder

### Oberste Ebene

| Feld | Bedeutung | Wert | Standard |
|---|---|---|---|
| `v` | Formatversion | `1` | 1 |
| `language` | Sprache der Oberfläche | `de`, `en` | `de` |
| `theme` | Farbdesign | `dark`, `light` | `dark` |
| `brightness` | Helligkeit aktiv in Prozent | 1–100 | 80 |
| `hold_ms` | Haltedauer bei `trigger: hold` in ms | 100–5000 | 600 |
| `idle.timeout_s` | Sekunden bis zum Ruhemodus | 5–3600 | 60 |
| `idle.brightness` | Helligkeit im Ruhemodus in Prozent | 1–100 | 8 |
| `idle.move_s` | Uhr im Ruhemodus alle n Sekunden verschieben | 5–3600 | 60 |
| `pages` | Seiten (1 bis 8) | Liste | – |

### Seite

| Feld | Bedeutung | Standard |
|---|---|---|
| `title` | Titel für die Navigation (Text oder `{"de": "...", "en": "..."}`) | – |
| `icon` | Symbol der Navigationstaste (`mdi:...`) | `mdi:help-circle` |
| `rows` | Anzahl Reihen im Raster (1–6). Die Zellhöhe ergibt sich aus der verfügbaren Höhe. | 4 |
| `tiles` | Kacheln (höchstens 48) | – |

Das Raster hat immer **8 Spalten**. Gibt es nur eine Seite, entfällt die Navigationsleiste.

### Kachel

| Feld | Bedeutung | Wert |
|---|---|---|
| `type` | Art | `value` (Anzeige), `button` |
| `x`, `y` | Position in Rasterzellen, ab 0 | `x + w <= 8`, `y + h <= rows` |
| `w`, `h` | Grösse in Rasterzellen | mindestens 1 |
| `source` | Zustandsquelle: HA-Entität (`light.x`) oder PC-Funktion (`pc:volume`), leer = keine | Text |
| `title` | Titel (Text oder zweisprachig) | – |
| `icon` | Symbolname, `mdi:...` oder nur der Name | optional |
| `format` | Darstellung des Zustands | `power`, `kwh`, `status`, `onoff`, `text`, `percent` |
| `max` | Obergrenze für plausible Leistungswerte in W (Werte darüber zeigt die Kachel als "-") | optional, 0 = 20000 |
| `trigger` | Bedienung (nur Buttons) | `none`, `tap`, `hold`, `confirm` |
| `hold_ms` | Haltedauer für diese Kachel, überschreibt den globalen Wert | optional |
| `action` | `pc:<Kennung>` löst eine Aktion auf dem PC aus. Ohne Angabe schaltet die Kachel die HA-Entität in `source` um. | optional |

Kacheln einer Seite dürfen sich nicht überlappen.

## Regeln der Prüfung

Das Gerät lehnt eine Konfiguration ab (und behält die bisherige), wenn
- das JSON nicht lesbar ist oder `v` nicht 1 ist,
- Werte ausserhalb der Bereiche liegen oder Felder unbekannte Werte haben,
- eine Kachel aus dem Raster ragt oder sich mit einer anderen überlappt,
- ein Symbolname unbekannt ist,
- ein Button mit `trigger` weder `source` noch `action` hat.

Die Fehlermeldung nennt die Stelle, zum Beispiel `Seite 1, Kachel 3: überlappt mit Kachel 1`.
