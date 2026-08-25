# GO AI Docker-Stack

GO AI wird ausschließlich als Docker-Compose-Stack auf dem GPU-Server betrieben. Es gibt keine Windows-Server-App,
keinen Gateway-Windows-Dienst und keine LM-Studio-Laufzeitabhängigkeit.

## Architektur

```text
GO-WinUI -> http://GPU-SERVER:8080 -> Caddy -> GO Gateway
                                               |- llama.cpp Router (CUDA)
                                               |- Speech Worker
                                               |- Media Worker
                                               |- Image Worker
                                               `- SearXNG
```

Nur Caddy veröffentlicht Port `8080`. Gateway, Modellrouter, Worker und SearXNG liegen in privaten Docker-Netzen und
besitzen keine Hostports. Der Windows-Firewall-Eintrag erlaubt TCP 8080 ausschließlich im Profil `Private` und für
`LocalSubnet`.

Es gibt keine GO-API-Schlüssel, Worker-Schlüssel, Zertifikate, Fingerprints oder Verbindungspakete. Eine zufällige
Client-ID im Header `X-GO-Client-ID` dient nur Rate-Limit-Fairness und Diagnose. Der Stack darf deshalb nicht direkt
ins Internet veröffentlicht werden.

## Persistente Daten

Standardwurzel auf dem GPU-Server:

```text
C:\ProgramData\GO-AI-Stack
|- Data\database
|- Data\uploads
|- Data\artifacts
|- Data\logs
|- Models
`- Config
```

Im Container werden diese Verzeichnisse als `/data/...` und `/models` eingebunden. Die serverseitige SQLite-Datenbank
speichert AI-Läufe, Warteschlange, Tool-Checkpoints und Artefaktmetadaten. Chats, Sitzungen, Projekte, Workflows und
UI-Zustände bleiben in der lokalen SQLite-Datenbank des jeweiligen GO-WinUI-Clients.

SQLite verwendet WAL, Integritätsprüfungen und sichere Checkpoints. Ein unterbrochener Lauf kann nur ab einem bereits
persistierten Checkpoint wieder aufgenommen werden; mutierende Tools werden durch Wiederholungen nicht doppelt ausgeführt.

## Modelle

- General verwendet `gpt-oss-120b MXFP4` mit seinem maximalen Kontextfenster von 131.072 Token.
- Coding verwendet standardmäßig `Qwen3-Coder-Next Q8_0` mit seinem nativen maximalen Kontextfenster von
  262.144 Token. Alternativ kann `gpt-oss-120b` für Coding ausgewählt werden.
- CUDA verteilt das Modell über GPU 0 und GPU 1 mit Tensor-Split `1,1`.
- Flash Attention, Harmony/Jinja-Template, native Function Calls und ein serieller Inferenzslot sind aktiv.
- `gpt-oss-120b` wird beim Start des LLM-Containers vorgewärmt. Der explizit deaktivierte Idle-Sleep
  (`--sleep-idle-seconds -1`) hält das jeweils aktive Modell bis zum nächsten Modellwechsel oder Docker-Stopp resident.
  Ein General-Lauf lädt General AI, ein Coding-Lauf das im Client ausgewählte Coding-Modell. Bereits aktive identische
  Modelle werden nicht erneut geladen.
- Qwen3-Coder-Next ist ein reines Non-Thinking-Modell. GO bietet dafür keine Reasoning-Stufe an und sendet auch keinen
  `reasoning_effort`-Parameter.
- Vision und BGE-M3 sind eigene bedarfsgesteuerte llama.cpp-Profile.
- Whisper, ECAPA und Supertonic laufen im Speech-Worker. Supertonic bleibt auf GPU 1 resident, solange der Stack läuft.
- Ein Modell- oder Containerfehler erhält genau einen kontrollierten Wiederholungsversuch. Es gibt keinen Modell- oder
  LM-Studio-Fallback.

Revisionen, Dateigrößen und SHA-256-Prüfsummen stehen in `deploy/go-ai/models.manifest.json`. Zur Laufzeit werden keine
Modelle heruntergeladen.

## Installation und Betrieb

Docker Desktop beziehungsweise Docker Engine mit NVIDIA Container Toolkit muss auf dem GPU-Server verfügbar sein.
Das initiale Deployment sollte einmal in einer administrativen PowerShell erfolgen, damit die Firewallregel angelegt
werden kann:

```powershell
Set-Location C:\Users\AMD\Documents\GitHub\GO-WinUI
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\deploy-ai-stack.ps1
```

Optional kann eine frühere Serverdatenbank einmalig übernommen werden:

```powershell
.\windows\deploy-ai-stack.ps1 -LegacyDatabasePath 'C:\Pfad\GO-AI.db'
```

Lifecycle:

```powershell
.\windows\build-ai-stack.ps1
.\windows\start-ai-stack.ps1
.\windows\smoke-ai-stack.ps1
.\windows\update-ai-stack.ps1
.\windows\stop-ai-stack.ps1
```

Direkte Docker-Diagnose:

```powershell
docker compose -f .\deploy\go-ai\compose.yaml ps
docker compose -f .\deploy\go-ai\compose.yaml logs --tail 200
```

Alle Container verwenden `restart: unless-stopped`. Nach einem Rechnerneustart startet ein zuvor aktiver Stack wieder,
sobald Docker verfügbar ist. `stop-ai-stack.ps1` führt `docker compose down` aus und gibt den gesamten von GO belegten
VRAM frei.

## Clientverbindung

GO-WinUI benötigt nur die Serveradresse, standardmäßig:

```text
http://192.168.0.67:8080
```

Die Einstellungen bieten ausschließlich Server-URL, Verbindungstest und das Aktualisieren von Fähigkeiten und
Dienststatus. Die Readiness unterscheidet Gateway erreichbar, Modell nicht geladen, Modell wird geladen, Modell bereit
und Dienstfehler.

Während eines Modellturns fragt das Gateway die privaten llama.cpp-Slots ab. Der Denkstatus zeigt daraus genau einen
laufend steigenden Zähler der im aktuellen Modell-Run erzeugten Tokens. Beim nächsten Modell-Run beginnt dieser wieder
bei null; Werte vorheriger Runs werden nicht summiert. Prompttoken und Generierungsrate bleiben ausschließlich interne
Diagnosedaten; die Erfassung benötigt weder Modell-Streaming noch einen zusätzlichen AI-Lauf.

## Tool- und Kontextmodell

Das Modell erhält zunächst einen kompakten Katalog aus Toolnamen und Beschreibungen. Nach der Auswahl wird nur das
vollständige Schema des gewählten Tools gesendet. Pro Modellturn ist höchstens ein Toolaufruf erlaubt. General kann
lesende Recherche-, Dokument-, Mathematik- und Medienwerkzeuge autonom auswählen; Coding erhält zusätzlich Workspace-,
Datei-, Prozess-, Git- und Lean-Werkzeuge. Mutationen bleiben auf den vom Client freigegebenen Workspace begrenzt.

`document.read` und `document.create` stehen General und Coding gemeinsam zur Verfügung. `document.read` liefert
Dokumentlisten, Gliederungen, Suchtreffer oder begrenzte Abschnittsfenster mit Fortsetzungsposition; ein Toolergebnis
enthält höchstens 30 Einheiten und 40.000 Zeichen. `document.create` erstellt oder ändert genau einen stabil benannten
Abschnitt und verlangt bei Änderungen den zuletzt gelesenen SHA-256. General speichert Markdown-, Text-, DOCX- und
KaTeX-gerenderte PDF-Ausgaben versioniert als Chat-Artefakte in der Client-SQLite. Coding hält dafür eine diffbare
Markdown-Quelle im freigegebenen Workspace und erzeugt die gewünschte Ausgabe deterministisch daraus.

General verwendet den persistent vorbereiteten Sitzungsverlauf. Coding verwendet den aktuellen Prompt, Workspace-Index
und laufenden Toolverlauf. Ab 75 Prozent Kontextbelegung verdichtet das jeweils aktive, ausgewählte Modell den
Arbeitskontext; die sichere Verdichtung wird mit dem Laufcheckpoint gespeichert.

## Abnahme

```powershell
dotnet test .\tests\GoAi.Server.Tests\GoAi.Server.Tests.csproj -c Release
dotnet test .\tests\GoWinUI.Tests\GoWinUI.Tests.csproj -c Release
.\windows\build-ai-stack.ps1
.\windows\smoke-ai-stack.ps1
.\windows\build.ps1
```

Der vollständige LAN-Test erfolgt von einem zweiten PC nur über `http://<GPU-Server>:8080`. Auf dem Host darf außer
Port 8080 kein GO-AI-Containerport veröffentlicht sein.
