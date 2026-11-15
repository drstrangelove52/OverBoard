# OverBoard-Verbindung zwischen Gerät und PC (Protokoll v1)

Das Gerät und der PC (OverBoard Agent) sprechen direkt miteinander, ohne Home Assistant. Erster Transport ist
**USB** (CDC-ACM, vom Gerät als serielle Schnittstelle angeboten, in Windows ohne eigenen Treiber). Ein Netzwerk-
Transport (WebSocket) kann später dasselbe Nachrichtenformat verwenden.

## Transport USB

- Das Gerät meldet sich über die Buchse "USB 2.0" (der ESP32-P4 selbst) mit der USB-ID `303A:4001`,
  Hersteller "OverBoard", Produkt "OverBoard Display".
- Der PC öffnet die serielle Schnittstelle (Baudrate egal) und setzt **DTR**. Ohne DTR sendet das Gerät nichts.
- Rahmung: **eine JSON-Nachricht pro Zeile**, UTF-8, Ende mit `\n`. Maximale Zeilenlänge 1024 Bytes.
- Das Gerät erkennt das Ende der Verbindung am Wegfall von DTR oder daran, dass 6 s keine Zeile ankommt.

## Ablauf

1. Der PC öffnet die Schnittstelle und sendet `hello`.
2. Das Gerät antwortet mit `hello`. Ab jetzt gilt die Verbindung als aufgebaut.
3. Der PC sendet alle 2 s ein `ping`, das Gerät antwortet mit `pong`.
4. Der PC sendet laufend `state`, wenn sich etwas ändert (und einmal komplett nach dem `hello`).
5. Das Gerät sendet `act`, wenn eine Kachel mit PC-Aktion bedient wird.

## Nachrichten

Jede Nachricht hat das Feld `t` (Typ).

| Richtung | Nachricht | Bedeutung |
|---|---|---|
| PC → Gerät | `{"t":"hello","v":1,"pc":"MARTIN-PC","app":"0.1.0"}` | Begrüssung mit Protokollversion, PC-Name, App-Version |
| Gerät → PC | `{"t":"hello","v":1,"dev":"overboard","fw":"0.1.0","w":1024,"h":600,"lang":"de"}` | Antwort mit Gerätedaten |
| PC → Gerät | `{"t":"ping"}` | Lebenszeichen |
| Gerät → PC | `{"t":"pong"}` | Antwort |
| PC → Gerät | `{"t":"state","id":"volume","v":"42"}` | Zustand einer PC-Funktion, Wert immer als Text |
| Gerät → PC | `{"t":"act","id":"volume.up"}` | Aktion ausgelöst (Kennung der Aktion, nie ein Befehlstext) |

### Zustands-Kennungen (`state`)

Auf dem Gerät erscheinen sie als Quelle `pc:<id>` in der Kachelkonfiguration.

| id | Wert | Bedeutung |
|---|---|---|
| `volume` | `0`..`100` | Hauptlautstärke in Prozent |
| `mute` | `on` / `off` | Lautsprecher stumm |
| `mic_mute` | `on` / `off` | Mikrofon stumm |

### Aktions-Kennungen (`act`)

In der Kachelkonfiguration als `pc:<id>`. Die App führt nur Aktionen aus, die sie selbst kennt.

| id | Wirkung |
|---|---|
| `volume.up`, `volume.down` | Lautstärke um 5 Prozentpunkte ändern |
| `mute.toggle` | Lautsprecher stumm umschalten |
| `mic.toggle` | Mikrofon stumm umschalten |
| `lock` | PC sperren |

Weitere Aktionen (Programme, Adressen, Tastenkombinationen) definiert der Benutzer in der App unter einer eigenen
Kennung und ruft sie auf dem Gerät mit `pc:<kennung>` auf.

## Konfiguration übertragen

Das Gerät speichert seine Konfiguration (Seiten, Kacheln, Einstellungen, Format siehe `config.md`) im Flash. Die PC-App
holt sie vom Gerät und sendet geänderte Konfigurationen zurück. Weil eine Zeile höchstens 1024 Bytes lang sein darf,
läuft die Übertragung in Stücken zu je höchstens 450 Bytes (Base64). Die Prüfsumme ist CRC32 (IEEE, wie zlib).

### PC → Gerät: Konfiguration senden (mit Bestätigung je Stück)

| Nachricht | Antwort des Geräts |
|---|---|
| `{"t":"cfg_begin","size":2816,"crc":123456}` (Gesamtgrösse in Bytes, 1 bis 49152, und CRC32) | `{"t":"cfg_ack","off":0}` |
| `{"t":"cfg_data","off":0,"d":"<Base64>"}` (ein Stück, `off` ist der Beginn im Gesamttext) | `{"t":"cfg_ack","off":<Ende des Stücks>}` |
| `{"t":"cfg_end"}` | `{"t":"cfg_result","ok":true}` oder `{"t":"cfg_result","ok":false,"error":"..."}` |

Das Gerät prüft Grösse, Prüfsumme und Inhalt. Nur eine gültige Konfiguration wird gespeichert und sofort angewendet, die
Oberfläche baut sich neu auf und die Entitäten werden bei Home Assistant neu abonniert. Bei einem Fehler an beliebiger Stelle
antwortet das Gerät mit `cfg_result` (`ok: false`) und behält die bisherige Konfiguration.

### PC → Gerät: Konfiguration lesen

`{"t":"cfg_get"}` löst aus: `cfg_begin` (`size`, `crc`), mehrere `cfg_data` (`off`, `d`) und `cfg_end`, ohne Bestätigung.
Ist keine eigene Konfiguration gespeichert, liefert das Gerät die eingebaute Standardkonfiguration. So kann der Editor immer
von einem gültigen Stand ausgehen.

### PC → Gerät: Zurücksetzen

`{"t":"cfg_reset"}` löscht die gespeicherte Konfiguration, das Gerät verwendet wieder die Standardkonfiguration.
Antwort: `{"t":"cfg_result","ok":true}`.

## Sicherheit

- Über die Verbindung kommt nur die **Kennung** einer Aktion, nie ein auszuführender Befehl.
- Die Aktionen und ihre Befehle sind ausschliesslich in der PC-App definiert.
- Die USB-Verbindung ist physisch an den PC gebunden. Ein Netzwerk-Transport bekommt zusätzlich eine Kopplung
  mit gemeinsamem Geheimnis.
