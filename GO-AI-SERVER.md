# GO AI Server

GO AI Server ist als eigenstaendiger Build im GO-WinUI-Repository enthalten. Die
WinUI-3-Serveroberflaeche, das loopbackgebundene Gateway, die Docker-Worker, die
API-Vertraege und der .NET-Client werden gemeinsam versioniert, aber getrennt von
der portablen GO-Clientanwendung gebaut und betrieben.

## Build und Artefakte

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build-ai-server.ps1
```

Das portable Serverpaket wird unter
`artifacts\go-ai-server\portable\win-x64` erzeugt. Es enthaelt:

- `GO-AI-Server.exe` als WinUI-3-Dashboard
- `gateway\GoAi.Gateway.exe` fuer den Windows-Dienst
- Docker-Deployment und Workerquellen
- OpenAPI- und SSE-Schemas
- den Smoke-Client und das versionierte `GoAi.Client`-NuGet-Paket
- alle erforderlichen Betriebs- und Deploymentskripte

## Daten- und Modellpfade

Programmdaten, temporaere Runs, Uploads, Artefakte, Worker-Modelle und Secrets
liegen ausserhalb des Repositorys unter `C:\ProgramData\GO-AI-Server`.

Modelle, die LM Studio ausfuehrt, liegen dagegen direkt in dessen verwaltetem
Modellverzeichnis:

```text
C:\Users\AMD\.lmstudio\models\<Publisher>\<Repository>\...
```

Die Publisher-/Repository-Struktur wird absichtlich beibehalten. Dadurch erhalten
die Modelle stabile LM-Studio-Modell-IDs und erscheinen unter **My Models** im
LM-Studio-Overlay. Beispielsweise werden Qwen-Modelle unter `Qwen\...` und BGE-M3
unter `ggml-org\...` einsortiert; bestehende Community-Konvertierungen koennen
weiterhin unter `lmstudio-community\...` liegen.

```powershell
# Gepinnte Dateien laden, Hashes pruefen und in LM Studio registrieren
.\windows\download-ai-models.ps1

# Vorhandene Dateien erneut einlesen und den My-Models-Katalog pruefen
.\windows\refresh-lmstudio-model-catalog.ps1 -PassThru
```

Whisper, Qwen3-TTS und Z-Image sind Worker-Modelle. Sie verbleiben unter
`C:\ProgramData\GO-AI-Server\Models`, weil sie nicht von LM Studio ausgefuehrt
werden und deshalb nicht in dessen Overlay gehoeren.

Der allgemeine Koordinator verwendet `openai/gpt-oss-20b` mit 131.072 Token.
Dieses Modell darf parallel zum Speech-Worker resident bleiben, damit
Sprachsteuerung, Live-Untertitel und Whisper-Live-Uebersetzung waehrend eines
allgemeinen Chats weiterlaufen. Laguna verwendet exklusiv 262.144 Token und
entlaedt fuer einen Code-Lauf alle anderen GPU-Modelle. Vision, Media und
Bildgenerierung bleiben ebenfalls exklusive Schwerlastprofile.

## Live-Untertitel fuer Client-Systemaudio

Der Speech-Worker stellt zusaetzlich einen explizit startbaren Live-Caption-Dienst
bereit. GO nimmt spaeter per WASAPI-Loopback ausschliesslich nach einem bewussten
Nutzerstart den Windows-Systemton auf, resampelt ihn auf PCM16/16 kHz/Mono und
sendet kurze WAV-Fenster an:

```text
POST /v1/audio/live-captions/sessions
PUT  /v1/audio/live-captions/sessions/{sessionId}/chunks/{sequence}
GET  /v1/audio/live-captions/sessions/{sessionId}
POST /v1/audio/live-captions/sessions/{sessionId}/stop
```

Whisper liefert deduplizierte bestaetigte Untertitel und einen fortlaufenden
Gesamttext. Standard sind 4-Sekunden-Fenster mit 0,5 Sekunden Ueberlappung; als
optionaler Modus ist eine Whisper-Uebersetzung nach Englisch vorgesehen. Es gibt
keine Hintergrundaufnahme. Inaktive Sitzungen enden automatisch und geben das
Speech-Modell wieder frei.

## Deployment und Abnahme

Das Deployment benoetigt einmalig eine administrative PowerShell fuer Dienst,
Firewall, ACLs und Caddy-Vertrauen:

```powershell
powershell -ExecutionPolicy Bypass -File .\windows\run-deploy-ai-server-elevated.ps1 `
  -PortableSource .\artifacts\go-ai-server\portable\win-x64
```

Danach prueft der Live-Smoke-Test HTTPS, Authentifizierung, SSE, Uploads,
Recherche, alle LM-Studio-Profile und die Medienworker:

```powershell
.\windows\live-smoke-ai-server.ps1
```

Die regulaere LAN-Adresse ist `https://192.168.0.67:8443`. Alle internen Ports
bleiben auf Loopback beschraenkt. Eine abweichende DHCP-Adresse setzt Readiness
bewusst auf nicht bereit.

Die offizielle YouTube Data API kann optional ueber die Dienstumgebung
`GO_AI_YOUTUBE_API_KEY` aktiviert werden. Ohne Key wird der gekennzeichnete
SearXNG-YouTube-Fallback verwendet.

GO-WinUI wird erst nach einem vollstaendig bestandenen Server-Live-Smoke auf
`GoAi.Client` und Server-SSE umgestellt. Der direkte LM-Studio-Anbieter bleibt
anschliessend nur als manuell aktivierbarer Diagnosemodus erhalten.
