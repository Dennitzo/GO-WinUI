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

- `GO-AI-Server.exe` als WinUI-3-Dashboard und verpflichtender Dienstcontroller
- `gateway\GoAi.Gateway.exe` nur als Diagnose- und Smoke-Test-Artefakt
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

Das volle `faster-whisper-large-v3`, ECAPA-Sprechererkennung, Piper MLS Medium (weibliches Profil) und
Z-Image ist ein Worker-Modell. Es verbleibt unter
`C:\ProgramData\GO-AI-Server\Models`, weil LM Studio keine STT-,
Sprechererkennungs-, TTS- oder Diffusions-API bereitstellt. Qwen3-VL, BGE-M3,
gpt-oss-20b und Laguna werden dagegen ausschliesslich ueber LM Studio verwaltet
und ausgefuehrt.

Whisper wird ausschliesslich durch den Docker-Speech-Worker ausgefuehrt. Eine
zusaetzliche Whisper-GGUF-Registrierung in LM Studio ist weder lauffaehig noch
erforderlich und gehoert deshalb nicht zum installierten Modellbestand.

Fuer Vision wird ausschliesslich `Qwen3-VL-30B-A3B-Instruct` verwendet. Es gibt
keinen stillen oder kleineren Vision-Modellfallback; ein Fehler des primaeren
Modells wird sichtbar an den Client zurueckgegeben.

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

Das volle Whisper-large-v3 liefert deduplizierte bestaetigte Untertitel und einen
fortlaufenden Gesamttext. ECAPA ordnet erkannte Sprecher innerhalb der Sitzung
stabil als `Person 1`, `Person 2` usw. zu. Standard sind 4-Sekunden-Fenster mit
0,5 Sekunden Ueberlappung. Rein englische Segmente werden ueber gpt-oss-20b ins
Deutsche uebersetzt; gemischtes Deutsch/Englisch bleibt erhalten. Es gibt keine
Hintergrundaufnahme. Inaktive Sitzungen enden automatisch und geben die
Speech-Ressourcen wieder frei.

## Deployment und Abnahme

Das Deployment benoetigt einmalig eine administrative PowerShell fuer Firewall,
ACLs und Caddy-Vertrauen:

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

## Verbindlicher Anwendungslebenszyklus

`GO-AI-Server.exe` ist der einzige regulaere Startpunkt. Beim Start der WinUI-App
werden das Gateway im App-Prozess, der authentifizierte LM-Studio-API-Server und
das Docker-Compose-Projekt gestartet. Beim normalen Schliessen stoppt die App
zuerst das Gateway und danach alle GO-AI-Container sowie den LM-Studio-API-Server.

Es gibt deshalb keinen automatisch gestarteten Gateway-Windows-Dienst und keine
separate LM-Studio-Aufgabe mehr. Die Container verwenden `restart: no` und werden
nicht allein durch einen Docker-Desktop-Neustart aktiviert. Docker Desktop selbst
darf weiterhin mit Windows starten; ohne die sichtbare GO-AI-Server-App ist aber
kein GO-AI-Endpunkt betriebsbereit. Das Deployment aktualisiert die vorhandene
Autostart-Verknuepfung ohne Zusatzparameter, sodass genau diese App-Steuerung nach
der Anmeldung aktiv wird. Der explizite Parameter `--dashboard-only`
bleibt ausschliesslich fuer eine manuelle Diagnose eines separat gestarteten
Gateways erhalten und wird von keiner installierten Verknuepfung verwendet.

Die offizielle YouTube Data API kann im Server-Dashboard unter **Sicherheit**
konfiguriert werden. Der Key wird DPAPI-geschuetzt abgelegt und hat Vorrang vor
der optionalen Dienstumgebung `GO_AI_YOUTUBE_API_KEY`. Ohne Key wird der
gekennzeichnete SearXNG-YouTube-Fallback verwendet.

GO-WinUI wird erst nach einem vollstaendig bestandenen Server-Live-Smoke auf
`GoAi.Client` und Server-SSE umgestellt. Der direkte LM-Studio-Anbieter bleibt
anschliessend nur als manuell aktivierbarer Diagnosemodus erhalten.
