# GO AI Hybrid-Stack

GO betreibt Gateway, Orchestrierung, SQLite, Werkzeuge, SearXNG sowie Speech-, Media- und Image-Worker in Docker.
LLM-, Vision- und Embeddingmodelle werden dagegen ausschließlich von LM Studio auf dem Windows-GPU-Host verwaltet.
Eine alte GO-AI-Server-App oder ein eigener llama.cpp-Modellcontainer wird nicht benötigt.

## Architektur

```text
GO-WinUI -> http://GPU-SERVER:8080 -> Caddy -> GO Gateway (Docker)
                                               |- LM Studio :1234 (Windows-Host)
                                               |- Speech Worker
                                               |- Media Worker
                                               |- Image Worker
                                               `- SearXNG
```

Caddy veröffentlicht den GO-Endpunkt auf Port `8080`. Die Docker-Worker besitzen keine Hostports. Das Gateway erreicht
LM Studio über `host.docker.internal:1234`; der Client kommuniziert nie direkt mit LM Studio. GO verwendet keine
API-Schlüssel, Zertifikate oder Verbindungspakete und darf deshalb nicht ins öffentliche Internet freigegeben werden.

## Modelle und Speicherorte

LM-Studio-kompatible GGUF-Modelle liegen unter:

```text
C:\Users\AMD\.lmstudio\models
```

Standardprofile:

- General AI: `openai/gpt-oss-120b`, 131.072 Kontexttoken.
- Vision: `qwen3-vl-30b-a3b-instruct`.
- Embeddings: `text-embedding-bge-m3`.

Nur Speech- und Bildgenerierungsressourcen bleiben unter `C:\ProgramData\GO-AI-Stack\Models`, weil diese von den
spezialisierten Docker-Workern direkt gelesen werden. Revisionen, Größen und SHA-256-Werte der migrierten GGUF-Dateien
stehen in `deploy/go-ai/models.manifest.json`.

Das Gateway nutzt LM Studios REST-Endpunkte für Modellkatalog, Load und Unload sowie den OpenAI-kompatiblen
`/v1/chat/completions`-Endpunkt für Antworten und native Function Calls. Ein bereits kompatibel geladenes Modell wird
wiederverwendet. Bei einem Modellwechsel entlädt GO ausschließlich die von GO verwalteten LM-Studio-Instanzen; fremde
LM-Studio-Modelle werden nicht verändert.

## Persistente Daten

```text
C:\ProgramData\GO-AI-Stack
|- data\database
|- data\uploads
|- data\artifacts
|- data\logs
|- Models\speech
`- Models\image
```

Die serverseitige SQLite-Datenbank speichert AI-Läufe, Warteschlange, Tool-Checkpoints und Artefaktmetadaten. Chats,
Sitzungen, Projekte, Workflows und UI-Zustände bleiben in der lokalen SQLite-Datenbank des GO-WinUI-Clients.

## Installation und Betrieb

Voraussetzungen sind Docker Desktop mit NVIDIA-Unterstützung, LM Studio samt `lms`-CLI und die lokal installierten
Modelldateien. Das Deployment prüft beide Modellwurzeln:

```powershell
Set-Location C:\Users\AMD\Documents\GitHub\GO-WinUI
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\deploy-ai-stack.ps1
```

Lifecycle:

```powershell
.\windows\start-lmstudio-server.ps1
.\windows\build-ai-stack.ps1
.\windows\start-ai-stack.ps1
.\windows\smoke-ai-stack.ps1
.\windows\stop-ai-stack.ps1
```

`start-ai-stack.ps1` startet beziehungsweise prüft LM Studio auf `0.0.0.0:1234` und startet danach Compose.
`stop-ai-stack.ps1` beendet die Docker-Dienste und entlädt nur die GO-verwalteten LM-Studio-Modelle; LM Studio selbst
bleibt geöffnet und sein Server bleibt verfügbar.

Die einmalige, idempotente Migration alter Docker-GGUFs erfolgt mit:

```powershell
.\windows\migrate-docker-models-to-lmstudio.ps1
```

Das Skript validiert Dateigrößen, überschreibt keine vorhandene Datei und lässt Worker-Ressourcen am Docker-Pfad.

## Client und Tools

Der Client verbindet sich standardmäßig mit `http://192.168.0.67:8080` und verwendet das in den Einstellungen gewählte
General-Modell. Der Denkstatus zeigt das tatsächlich aktive Modell und die vom fertigen LM-Studio-Turn gemeldeten
Tokenwerte.

General verwendet einen kompakten Toolkatalog mit nachgeliefertem Einzelschema. Serverseitige Recherche-, Medien- und
Berechnungswerkzeuge werden mit den freigegebenen Servertools kombiniert. Der Client kann Sitzungsdokumente lesen und
erstellen sowie optional BricsCAD-Aktionen anbieten. Workspace-Dateisystem, Prozessausführung und Lean-Prüfung gehören
nicht mehr zum Clientvertrag. Dokumentmutationen bleiben bestätigungspflichtig und verwenden begrenzte Abschnittsfenster.

Antworten und native Function Calls laufen über LM Studios OpenAI-kompatiblen `/v1/chat/completions`-Endpunkt. GO fasst
anfängliche Systemblöcke zusammen und erhält chronologische Retry- und Werkzeughinweise an ihrer Position.

## Abnahme

```powershell
dotnet test .\tests\GoAi.Server.Tests\GoAi.Server.Tests.csproj -c Release
dotnet test .\tests\GoWinUI.Tests\GoWinUI.Tests.csproj -c Release
.\windows\build-ai-stack.ps1
.\windows\smoke-ai-stack.ps1
.\windows\build.ps1
```

Direkte Diagnose:

```powershell
lms server status
lms ps
docker compose --env-file C:\ProgramData\GO-AI-Stack\stack.env -f .\deploy\go-ai\compose.yaml ps
docker compose --env-file C:\ProgramData\GO-AI-Stack\stack.env -f .\deploy\go-ai\compose.yaml logs --tail 200
```
