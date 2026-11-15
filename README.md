# OverBoard

Touch-Display als Mischung aus Stream Deck und DAKBoard: frei gestaltbare Tasten und Anzeigen für den PC (Programme,
Lautstärke, Medien, Fenster), für Home Assistant (Solar, Verbrauch, Geräte) und Hinweise mit Ton. Der PC-Teil funktioniert
ohne Home Assistant, wie ein Stream Deck.

- **Display:** Elecrow CrowPanel Advance 9" (ESP32-P4, 1024×600, Hardware V1.2)
- **PC:** Windows (macOS folgt, wenn Windows vollständig ist)

## Aufbau des Repositories

| Ordner | Inhalt |
|---|---|
| `firmware/` | Firmware für das Display (ESP-IDF 5.4, LVGL 9.3). Protokoll und Konfigurationsformat: `firmware/docs/` |
| `agent/` | OverBoard-Programm für Windows (.NET 10, Avalonia): Editor, Aktionen, Einrichtung, Firmware-Installation, Updates |
| `installer/` | Inno-Setup-Skript für den Windows-Installer |
| `.github/workflows/` | Release-Ablauf |

## Für Anwender

1. Installer `OverBoard-Setup-<Version>.exe` von der Seite *Releases* laden und ausführen.
2. Display mit dem **UART**-Anschluss verbinden, im Programm auf der Seite **Gerät** die mitgelieferte Firmware installieren.
3. Display zusätzlich mit der Buchse **USB 2.0** verbinden (normale Verbindung zum Programm).
4. Auf der Seite **Gerät** unter *Einrichtung* WLAN, Home Assistant und (optional) MQTT eintragen.
5. Auf der Seite **Display** Kacheln anlegen, verschieben und beschriften. Änderungen gehen sofort aufs Gerät.

Das Programm sucht höchstens einmal täglich nach Updates (abschaltbar auf der Seite **Programm**). Ein Update bringt die
passende Firmware mit; installiert wird nur nach einem Klick.

## Für Entwickler

Firmware:

```
cd firmware/overboard
cp sdkconfig.secrets.example sdkconfig.secrets    # optional: Zugangsdaten für Entwicklungs-Builds
idf.py set-target esp32p4
idf.py -p COMx build flash
```

Programm:

```
cd agent
dotnet run --project OverBoardAgent
```

Weitere Einzelheiten: `firmware/README.md`, `agent/README.md`.

## Veröffentlichen

Ein Tag der Form `v1.2.3` auf `main` löst den Release-Ablauf aus (`.github/workflows/release.yml`): Firmware bauen, Programm
veröffentlichen, Firmware in den Installer packen, Prüfsummen erzeugen und alles als Release hochladen. Die Version der
Firmware und des Programms kommt aus dem Tag.

```
git tag v0.2.0
git push origin v0.2.0
```

Das Programm sucht Updates im Repository, das in `agent/OverBoardAgent/Core/UpdateService.cs` (`Repo`) eingetragen ist.
Solange das Repository privat ist, findet es nichts (die Releases sind dann ohne Anmeldung nicht abrufbar).

## Lizenz

Noch nicht festgelegt.
