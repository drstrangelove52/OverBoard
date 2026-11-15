# OverBoard Agent

PC-Begleiter für das OverBoard-Display (Firmware in `../firmware`). Läuft als Tray-Symbol, verbindet sich
über USB direkt mit dem Gerät (ohne Home Assistant, wie ein Stream Deck), führt Aktionen aus und meldet Zustände.

Protokoll: `firmware/docs/protocol.md`.

## Technik

- .NET 10, Avalonia 12 (Tray), NAudio (Windows Core Audio), System.IO.Ports
- Windows zuerst. Plattformcode hinter `Platform/IPlatform.cs`, macOS folgt erst, wenn Windows vollständig ist.

## Bauen und starten

```
cd OverBoardAgent
dotnet build
dotnet run
```

Ausliefern (eine framework-abhängige Datei, Runtime: `winget install Microsoft.DotNet.DesktopRuntime.10`):

```
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

## Dateien auf dem PC

- Protokoll: `%LOCALAPPDATA%\OverBoardAgent\agent.log`
- Einstellungen: `%APPDATA%\OverBoardAgent\settings.json` (z. B. `{ "language": "de" }`)
