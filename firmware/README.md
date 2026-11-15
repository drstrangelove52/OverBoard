# OverBoard Firmware

Touch-Display als Mischung aus Stream Deck und DAKBoard: Live-Werte und Schaltflaechen fuer Home Assistant,
Banner mit Ton ueber MQTT, Ruhemodus mit Uhr.

Hardware: Elecrow CrowPanel Advance 9" (ESP32-P4 + ESP32-C6, Hardware V1.2, SKU DHE042090).
Planung und Entscheidungen stehen im Obsidian-Vault unter `02 Projekte/OverBoard.md`.

## Projekte

- `overboard/` die Firmware (ESP-IDF 5.4.4, LVGL 9.3)


## Bauen

1. ESP-IDF 5.4.x einrichten, Projekt auf einem **lokalen** Laufwerk (nicht Netzlaufwerk).
2. `overboard/sdkconfig.secrets.example` nach `overboard/sdkconfig.secrets` kopieren und ausfuellen.
3. `idf.py set-target esp32p4`, dann `idf.py -p COMx build flash`.

Aendert sich `sdkconfig.secrets` oder `sdkconfig.defaults`, die erzeugte `sdkconfig` loeschen (Vollbuild, ca. 10 Minuten).

## Wichtig zur Hardware

- Der C6 haengt per SDIO am P4, **1-Bit-Modus mit D0=GPIO17, D1=GPIO16** (rueckwaerts zum ESP-IDF-Standard), CLK 18, CMD 19,
  Reset GPIO32. Mit den Standardpins stuerzt `esp_hosted` mit `0x107` ab.
- `swap_bytes` im LVGL-Display muss `false` sein, sonst sind die Farben falsch.
- LVGL muss mindestens 9.3 sein (esp_lvgl_port 2.9).
- Der Verstaerker (Lautsprecher) wird ueber GPIO30 mit Low eingeschaltet.

## MQTT-Nachrichten an das Geraet

Topic `overboard/notify`, JSON:

```json
{ "title": "Geschirrspueler", "text": "Programm beendet", "level": "ok", "sound": "ok", "duration": 8, "volume": 40 }
```

`level`: `info`, `ok`, `warn`, `alarm` (alarm bleibt bis zum Antippen). Umlaute und Latin-1-Zeichen werden angezeigt, andere Zeichen werden zu ?.
Status des Geraets: `overboard/status` (`online` / `offline`).
