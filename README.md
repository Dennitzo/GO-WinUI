# GO

GO ist ein Greenfield-Neuaufbau von Barebone-Qt als lokale WinUI-3-Anwendung. Die App kombiniert einen allgemeinen, über LM Studio gestreamten AI-Chat mit Dokumentkontext, SQLite-Workflows und einer vollständigen lokalen Projekt- und Dateiverwaltung. PostgreSQL, Cloud-Anbieter, alte Datenimporte und ein sichtbarer BricsCAD-Modus gehören bewusst nicht zu v1.

## Funktionen

- WinUI-3-Shell mit Mica, systemakzentbasierter Gestaltung, dem Barebone-App-Icon für EXE und Taskleiste, einer bewusst iconfreien Titelleiste, Hell-/Dunkel-/High-Contrast-Theme, responsiver Navigation und zuverlässiger Fenster-/Monitor-/DPI-Wiederherstellung
- modularer, vollständig lokal gebündelter WebView2-Assistent mit Sitzungen, Markdown, Code, Tabellen, KaTeX, Anhängen, Workflows, Reasoning-Auswahl, Kontextanzeige, Stop und PDF-Export
- LM-Studio-Anbindung über die OpenAI-kompatiblen Responses- und Chat-Completions-Endpunkte mit SSE-Streaming, Abbruch, Retry, Kontextbudget und Crash-Recovery
- persistenter Dokumentkontext für PDF, DOCX sowie Text-, Markup- und Quellcodeformate; PDF-Seitenauswahl und verständliche Ablehnung leerer Scan-PDFs
- zwei eingebaute allgemeine Barebone-Workflows sowie anlegbare, editierbare, klonbare und revisionsgesicherte Benutzerworkflows
- lokale Projekte, Checklisten und Assets einschließlich Chunking, Deduplizierung, Vorschau, externer Arbeitskopie, erkanntem Reimport und Thumbnail
- flüchtige, inhaltsredigierte App-/DB-/LM-Studio-/WebView-/BricsCAD-Logs und manuelle, prüfsummengeschützte `.gobackup`-Backups
- technisch getrennte BricsCAD-V26-Bridge mit dynamischem Loopback-Port, gegenseitiger Authentifizierung und dem bestehenden 39-Fähigkeiten-Vertrag

## Architektur

| Projekt | Verantwortung |
|---|---|
| `GoWinUI.App` | WinUI-Shell, Views/ViewModels und geschlossene WebView2-Bridge |
| `GoWinUI.Core` | Domänenmodelle, Use Cases und UI-/SQLite-unabhängige Verträge |
| `GoWinUI.Infrastructure` | SQLite, LM Studio, Dokumentparser, Einstellungen, Backup und Logging |
| `GoWinUI.BricsCad.Protocol` | Bridge-DTOs, Framing, Vertrag und Loopback-Host |
| `GoWinUI.BricsCad.Plugin` | optionales .NET-8-x64-Plugin für BricsCAD V26 |
| `GoWinUI.Tests` | Unit-, Integrations-, Persistenz- und Vertragstests |
| `GoAi.Server.App` | separate WinUI-3-Oberfläche für den GO AI Server |
| `GoAi.Gateway` / `GoAi.Server.Core` | versionierte Server-API, Orchestrierung, Persistenz und Worker-Anbindung |
| `GoAi.Contracts` / `GoAi.Client` | gemeinsame API-Verträge und .NET-Clientpaket |

SQLite ist die einzige Wahrheit für Chats, Dokumente, Workflows, Projekte und Binärobjekte. Einstellungen liegen separat und atomar in `settings.json`; WebView-Storage wird nicht als Anwendungsspeicher verwendet.

## Voraussetzungen

- Windows 10 Version 2004 (Build 19041) oder neuer, x64
- WebView2 Evergreen Runtime
- LM Studio für lokale Chat-Ausführung; Standardadresse `http://127.0.0.1:1234/v1`
- .NET SDK 10.0.302 nur zum Bauen
- optional BricsCAD V26 samt Managed SDK zum Bauen und Laden des Plugins

Laufzeitdaten werden unter `%LOCALAPPDATA%\GO` gespeichert. Datenbank und Backups sind nicht verschlüsselt und können vertrauliche Chat- und Dokumentinhalte enthalten.

## Build und Tests

Der öffentliche GO-Client-Einstieg führt Restore, Release-Build, Tests, `win-x64`-Single-file-Publish und einen isolierten Laufzeit-/WebView2-Smoke-Test aus:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

Das Primärartefakt ist `artifacts\portable\win-x64\GO.exe`. Es ist unpackaged, self-contained und enthält Windows App SDK, SQLite-Native-Runtime und Webassets; WebView2 Evergreen bleibt eine Systemvoraussetzung. Neben `GO.exe` werden im Laufzeitordner keine Sidecars benötigt.

Der optionale Plugin-Build ist vom App-Build entkoppelt:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1 -IncludeBricsCadPlugin
```

Alternativ kann `windows\build-bricscad-plugin.ps1` direkt verwendet werden. Das Ergebnis ist ein eigenes NETLOAD-ZIP unter `artifacts\windows\bricscad-v26`; `GOBricsCad.dll` wird nicht in `GO.exe` eingebettet. Weitere Build-Schalter und Artefakte sind in [windows/README.md](windows/README.md) beschrieben.

Der GO AI Server besitzt einen vollständig getrennten Build und ein eigenes portables Artefakt:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build-ai-server.ps1
```

Serverbetrieb, Modellpfade, Deployment und Live-Abnahme sind in [GO-AI-SERVER.md](GO-AI-SERVER.md) beschrieben.

## Nutzung

1. LM Studio starten, ein Modell laden und den lokalen Server aktivieren.
2. `GO.exe` starten. Bei genau einem geladenen Modell wählt GO es automatisch; andernfalls das Modell unter **Einstellungen** auswählen.
3. Dokumente im Composer anhängen oder im Workflow-Overlay einen allgemeinen Workflow als Kontext wählen.
4. Backups vor externen Änderungen über **Einstellungen** erzeugen. Ein Restore prüft Manifest, Hashes, Datenbankintegrität und Schemaversion und sichert zuerst den aktuellen Zustand.

GO startet pro Windows-Benutzer nur einmal. Weitere Starts aktivieren das bestehende Fenster. Die allgemeine Chatpipeline kennt die BricsCAD-Bridge absichtlich nicht und kann keine CAD-Aktionen auslösen.

## v1-Abgrenzungen

Kein Import aus Barebone-Qt oder TwitchAI, kein PostgreSQL, kein OCR, keine verschlüsselten/passwortgeschützten PDFs, keine binären `.doc`-Dateien, keine Embedding-/RAG-Suche, keine Cloud-Modelle, kein Installer/MSIX, kein ARM64 und kein sichtbarer BricsCAD-Chatmodus.
