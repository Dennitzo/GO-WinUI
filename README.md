# GO

GO ist eine lokale WinUI-3-Anwendung mit AI-Chat, Coding-Agent, Dokumentkontext, SQLite-Workflows und Projektverwaltung. Der Client verbindet sich mit dem Docker-Gateway im privaten LAN; General-, Coding-, Vision- und Embeddingmodelle laufen im nativen Windows-llama.cpp-Server aus Unsloth. Er liest die vorhandenen Modelle direkt aus dem lokalen Hugging-Face-Cache. Cloud-Anbieter gehören nicht zur Laufzeitarchitektur.

## Funktionen

- WinUI-3-Shell mit Mica, systemakzentbasierter Gestaltung, dem Barebone-App-Icon für EXE und Taskleiste, einer bewusst iconfreien Titelleiste, Hell-/Dunkel-/High-Contrast-Theme, responsiver Navigation und zuverlässiger Fenster-/Monitor-/DPI-Wiederherstellung
- modularer, vollständig lokal gebündelter WebView2-Assistent mit Sitzungen, Markdown, Code, Tabellen, KaTeX, Anhängen, Workflows, Reasoning-Auswahl, Kontextanzeige, Stop und PDF-Export
- direktes, schlüsselloses HTTP-Protokoll zum Docker-Gateway mit Run-Events, Abbruch, Checkpoints, Kontextbudget und Crash-Recovery
- persistenter Dokumentkontext für PDF, DOCX sowie Text-, Markup- und Quellcodeformate; PDF-Seitenauswahl und verständliche Ablehnung leerer Scan-PDFs
- eine zunächst leere Workflow-Bibliothek mit anlegbaren, editierbaren, klonbaren und revisionsgesicherten Benutzerworkflows
- Coding-Modus mit eigenem Modell, Projektordner pro Sitzung, Datei-/Such-/Diff-Werkzeugen, automatischen Prüfbefehlen und gestreamtem Fortschritt
- lokale Projekte, Checklisten und Assets einschließlich Chunking, Deduplizierung, Vorschau, externer Arbeitskopie, erkanntem Reimport und Thumbnail
- flüchtige, inhaltsredigierte App-/DB-/AI-/WebView-/BricsCAD-Logs und manuelle, prüfsummengeschützte `.gobackup`-Backups
- technisch getrennte BricsCAD-V26-Bridge mit dynamischem Loopback-Port, gegenseitiger Authentifizierung und dem bestehenden 39-Fähigkeiten-Vertrag

## Architektur

| Projekt | Verantwortung |
|---|---|
| `GoWinUI.App` | WinUI-Shell, Views/ViewModels und geschlossene WebView2-Bridge |
| `GoWinUI.Core` | Domänenmodelle, Use Cases und UI-/SQLite-unabhängige Verträge |
| `GoWinUI.Infrastructure` | Client-SQLite, Dokumentparser, Einstellungen, Backup und Logging |
| `GoWinUI.BricsCad.Protocol` | Bridge-DTOs, Framing, Vertrag und Loopback-Host |
| `GoWinUI.BricsCad.Plugin` | optionales .NET-8-x64-Plugin für BricsCAD V26 |
| `GoWinUI.Tests` | Unit-, Integrations-, Persistenz- und Vertragstests |
| `GoAi.Gateway` / `GoAi.Server.Core` | Linux-Container für API, Orchestrierung, Server-SQLite und Worker-Anbindung |
| `GoAi.Contracts` / `GoAi.Client` | gemeinsame API-Verträge und .NET-Clientpaket |

Die lokale Client-SQLite ist die einzige Wahrheit für Chats, Dokumente, Workflows, Projekte und Binärobjekte. Die getrennte Docker-SQLite speichert nur serverseitige AI-Läufe und Checkpoints. Einstellungen liegen atomar in `settings.json`; WebView-Storage ist kein Anwendungsspeicher.

## Voraussetzungen

- Windows 10 Version 2004 (Build 19041) oder neuer, x64
- WebView2 Evergreen Runtime
- erreichbarer GO-AI-Hybrid-Stack im privaten LAN; Standardadresse `http://192.168.0.67:8080`
- auf dem Windows-GPU-Host Unsloths nativer `llama-server.exe`, ein Python-Interpreter und lokale GGUF-Modelle; der verwaltete Router nutzt Port `8081`
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

Gateway und Worker besitzen einen vollständig getrennten Docker-Build; die Modelle selbst werden dabei nicht in Images eingebettet:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build-ai-stack.ps1
```

Serverbetrieb, Modellpfade, Deployment und Live-Abnahme sind in [GO-AI-SERVER.md](GO-AI-SERVER.md) beschrieben.

## Nutzung

1. Auf dem GPU-Server `windows\start-ai-stack.ps1` ausführen.
2. `GO.exe` starten und unter **Einstellungen** die Serveradresse prüfen.
3. **General AI Modell** und **Coding AI Modell** unabhängig auswählen. Über **Workspace** im Promptfenster einen Projektordner zuordnen und Coding starten oder Dokumente für General im Composer anhängen.
4. Backups vor externen Änderungen über **Einstellungen** erzeugen. Ein Restore prüft Manifest, Hashes, Datenbankintegrität und Schemaversion und sichert zuerst den aktuellen Zustand.

GO startet pro Windows-Benutzer nur einmal. Weitere Starts aktivieren das bestehende Fenster. Die allgemeine Chatpipeline kennt die BricsCAD-Bridge absichtlich nicht und kann keine CAD-Aktionen auslösen.

Im Coding-Modus kann das lokale Modell selbstständig die vorhandene SearXNG-Websuche
und mehrstufige Deep Research nutzen. Beide Modi verwenden denselben Chatheader ohne
Modellangaben. Die Arbeitsansicht zeigt den Workspace und aufklappbare
Werkzeugschritte mit Datei-Diffs, Ergebnissen und dauerhaft gespeicherten
Statusdaten. Die Nachrichtenfooter bleiben erhalten. Details und nachprüfbare
Abnahmeläufe stehen in [Coding-Validierung](docs/CODING-AGENT-VALIDATION.md).

## v1-Abgrenzungen

Kein Import aus Barebone-Qt oder TwitchAI, kein PostgreSQL, kein OCR, keine verschlüsselten/passwortgeschützten PDFs, keine binären `.doc`-Dateien, keine Cloud-Modelle, kein Installer/MSIX, kein ARM64 und kein sichtbarer BricsCAD-Chatmodus.
