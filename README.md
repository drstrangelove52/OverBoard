# OverBoard

Ein Touch-Display für deinen Schreibtisch, halb Stream Deck, halb Info-Tafel. Du gestaltest es selbst: Tasten für den PC,
Anzeigen für dein Smart Home und Hinweise mit Ton.

- **PC steuern:** Programme starten, Lautstärke, Stummschaltung, Mikrofon, Medientasten, Tastenkombinationen, Sperren,
  Ruhezustand und mehr.
- **Home Assistant anzeigen und schalten:** Solarleistung, Verbrauch, Gerätezustände, Lichter schalten.
- **Kalender:** die nächsten Termine, zum Beispiel vom Familienkalender (Google Kalender, Nextcloud und andere).
- **Hinweise mit Ton:** Banner und Signaltöne, zum Beispiel von Home Assistant über MQTT.
- **Bildschirmschoner und Energiesparen:** Uhr mit Werten, Display aus, wenn du nicht da bist.
- **Deutsch und Englisch,** helles und dunkles Design.

Der PC-Teil funktioniert auch ganz ohne Home Assistant, wie ein Stream Deck.

## Was du brauchst

- **Display:** Elecrow CrowPanel Advance 9" (ESP32-P4, 1024 × 600, Hardware V1.2)
- **PC:** Windows 10 oder 11 (macOS folgt)
- **Optional:** Home Assistant (für Smart-Home-Werte), ein MQTT-Broker (für Hinweise mit Ton)

## Installation

1. Lade auf der Seite [Releases](../../releases/latest) die Datei `OverBoard-Setup-<Version>.exe` und führe sie aus.
   Windows zeigt beim ersten Mal eventuell eine SmartScreen-Warnung, weil das Programm nicht signiert ist. Wähle
   *Weitere Informationen* und dann *Trotzdem ausführen*.
2. Verbinde das Display mit der Buchse **UART** und öffne OverBoard. Auf der Seite **Gerät** installierst du mit
   *Firmware installieren* die mitgelieferte Firmware.
3. Verbinde das Display zusätzlich mit der Buchse **USB 2.0**. Das ist die normale Verbindung zum Programm.
4. Trage auf der Seite **Gerät** unter *Einrichtung* WLAN, Home Assistant und (wenn du Hinweise willst) MQTT ein und klicke
   auf *Speichern und ans Gerät übertragen*.

Alle Zugangsdaten verwaltest du nur im Programm. Sie werden verschlüsselt auf deinem PC gespeichert und per USB ans Gerät
übertragen, die Firmware selbst enthält keine.

## Das Display einrichten

Auf der Seite **Display** siehst du das Display so, wie es aussieht, und gestaltest es direkt. Änderungen gehen sofort aufs
Gerät.

- **Kacheln** legst du neu an, verschiebst sie mit der Maus und ziehst sie am Eckgriff grösser. Das Raster hat 8 Spalten
  und bis zu 6 Reihen, und du kannst mehrere Seiten anlegen.
- **Anzeige-Kacheln** zeigen einen Wert aus Home Assistant oder vom PC (zum Beispiel Leistung, Status, Prozent, Zeit).
- **Tasten** lösen eine Aktion aus: ein Tippen, langes Halten oder Tippen mit Rückfrage. Aktionen sind zum Beispiel
  Lautstärke, Medientasten, ein Programm starten oder Tasten senden. Unter **Aktionen** legst du eigene an.
- **Kalender-Kacheln** zeigen die nächsten Termine. Siehe unten.
- **Symbole, Texte und Design:** Symbole wählst du aus einer Liste, Titel schreibst du frei. Du kannst zwischen dem hellen
  und dem dunklen Design wählen.
- **Echte Werte:** Wenn Home Assistant verbunden ist, zeigt die Vorschau die aktuellen Werte.

Mit *Laden …* und *Speichern unter …* kannst du die ganze Anordnung als Datei sichern und wiederherstellen.

## Kalender

1. Öffne Google Kalender am Computer, dann *Einstellungen*.
2. Wähle links den Kalender, dann **Kalender integrieren**.
3. Kopiere die **Geheime Adresse im iCal-Format**.
4. Füge sie im Programm auf der Seite **Gerät** unter *Einrichtung* im Feld *Kalender* ein und klicke auf *Speichern und
   ans Gerät übertragen*.
5. Wähle bei einer Kachel die Art **Kalender (Termine)**.

Die Adresse gilt wie ein Passwort und wird verschlüsselt gespeichert. Der Kalender wird nur gelesen. Das Programm lädt ihn
alle 10 Minuten. Wiederkehrende Termine werden berücksichtigt. Zum Entfernen des Kalenders trägst du im Feld ein
Minuszeichen (`-`) ein.

## Bildschirmschoner und Energiesparen

Auf der Seite **Gerät** stellst du ein:

- **Bildschirmschoner:** startet nach einer einstellbaren Zeit ohne Berührung, zeigt Uhr und bis zu zwei Werte und bewegt
  sich gegen das Einbrennen.
- **Display ausschalten:** nach weiteren Minuten geht die Hintergrundbeleuchtung ganz aus. Sinnvoll, wenn das Gerät an einem
  USB-Anschluss mit dauerndem Strom hängt.
- **Früher ausschalten, wenn der PC schläft, gesperrt oder aus ist.**

Berühren des Displays und Hinweise (Banner, Alarm) wecken es wieder auf.

## Töne

Hinweise kommen mit Signalton, und die Tasten klicken beim Berühren. Beides lässt sich ein- und ausschalten, die Lautstärken
stellst du auf der Seite **Gerät** ein.

## Updates

Das Programm sucht höchstens einmal täglich nach einer neuen Version. Das lässt sich auf der Seite **Programm** abschalten.
Ein Update bringt die passende Firmware mit und wird erst nach einem Klick installiert.

## Wenn etwas nicht klappt

- **Das Gerät verbindet sich nicht:** Prüfe, dass das Kabel in der Buchse **USB 2.0** steckt (die Buchse **UART** ist nur zum
  Installieren der Firmware). Auf der Seite **Gerät** steht der Verbindungszustand.
- **Home Assistant zeigt "-":** Prüfe Adresse und Token unter *Einrichtung*. Die Adresse beginnt mit `http://`, nicht mit
  `https://`.
- **Die Firmware lässt sich nicht installieren:** Das Gerät muss an der Buchse **UART** hängen, ein anderes Programm darf
  den Anschluss nicht belegen.
- **Protokoll:** Das Programm schreibt eine Logdatei nach `%LOCALAPPDATA%\OverBoardAgent\agent.log`. Sie hilft, wenn du ein
  Problem meldest.

Fragen, Fehler oder Ideen kannst du als [Issue auf GitHub](../../issues) melden.

## Das Projekt unterstützen

OverBoard entsteht in der Freizeit. Wenn es dir nützt, freue ich mich über eine kleine
[Ko-fi-Spende](https://ko-fi.com/pobli) ☕

## Lizenz

[MIT](LICENSE) © 2026 Martin Nigg

OverBoard verwendet unter anderem LVGL, ESP-IDF, Avalonia, NAudio, die Schrift Montserrat und die Material Design Icons,
jeweils unter deren eigener Lizenz.
