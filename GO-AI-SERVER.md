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
- Coding AI: `qwen/qwen3.8-27b` (`Qwen3.8-27B-Q4_K_M.gguf`), 262.144 Kontexttoken.
- Alternative Codingmodelle: `openai/gpt-oss-120b` und `qwen3-coder-next` Q8_0.
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

Der Client verbindet sich standardmäßig mit `http://192.168.0.67:8080`. In den Einstellungen werden General- und
Codingmodell unabhängig gewählt; Qwen3.8 27B ist die Standardauswahl für Coding. Der Denkstatus zeigt das tatsächlich
aktive Modell und die vom fertigen LM-Studio-Turn gemeldeten Tokenwerte.

General verwendet weiterhin den kompakten Toolkatalog mit nachgeliefertem Einzelschema. Coding Agent V2 erhält dagegen
in stabiler Reihenfolge genau sechs kompakte Fassaden: `workspace.inspect`, `workspace.change`, `execution.run`,
`research.query`, `artifact.process` und `task.finish`. Dadurch entfällt im Codingpfad ein eigener Modellturn nur zur
Werkzeugnamensauswahl. Pro Modellturn ist genau ein Toolaufruf erlaubt; die Fassade wird deterministisch auf eine
vorhandene GO-Operation abgebildet. Mutationen bleiben auf den vom Client freigegebenen Workspace begrenzt und verlangen
bei vorhandenen Dateien die zuletzt belegte SHA-256-Version.

Coding Agent V2 führt einen typisierten Arbeitsstand in den Phasen `Orientieren`, `Bearbeiten`, `Prüfen` und
`Fertigstellen`. SQLite speichert jede Action und genau eine Observation sowie kompakte Checkpoints. Das Modell sieht nur
den aktuellen Nutzerauftrag, einen kleinen Repositoryausschnitt, den begrenzten Task-Ledger und höchstens sechs aktuelle
Action-/Observation-Paare. Ab sechzig Prozent des sicheren Kontextbudgets beginnt ein neuer Kontextepoch aus diesen
strukturierten Daten; der vollständige Chatverlauf wird im Codingmodus nicht erneut übertragen.

Der Client hält einen inhaltsadressierten Workspacecache mit SHA-256-Dateiversionen, Zeilenindex, FTS5-Volltextsuche,
Symbolen und Import-/Referenzkanten. Ein `FileSystemWatcher` liefert schnelle Aktualisierungen; vor jedem Lauf gleicht ein
Metadatenscan verpasste Ereignisse ab. Unveränderte, bereits bekannte Dateiversionen werden nur noch über ihre Beleg-ID
referenziert. Vollständige lokale Quelltexte verbleiben im Clientcache und werden nach der Observation aus dem temporären
Gateway-Transport entfernt.

Der Codingprovider bleibt für einen Lauf fest auf dem nicht streamenden LM-Studio-Endpunkt `/v1/chat/completions`.
Providerzustand und stabiler Promptpräfix sind nur Cachemetadaten; nach einem Modellreload wird der Lauf aus Task-Ledger
und Clientbelegen rekonstruiert. Ein Wechsel auf `/v1/responses` findet nicht still innerhalb eines aktiven Laufs statt.

Bei Qwen3.8 laufen ausschließlich die kurzen, zwingend strukturierten Tool-Transport-Turns mit
`reasoning_effort: none`. Analyse- und Antwort-Turns behalten die im Client gewählte Stufe. Das umgeht den bekannten
LM-Studio-Konflikt zwischen Qwen-Reasoning und nativer Tool-Call-Ausgabe, ohne die eigentliche Coding-Analyse zu
deaktivieren.

Da das Qwen-Chattemplate Systemnachrichten nur am Gesprächsanfang akzeptiert, fasst GO anfängliche Systemblöcke
zusammen. Chronologische Retry- und Werkzeughinweise bleiben an ihrer Position und werden als gekennzeichnete interne
Laufanweisung transportiert. Dadurch kann kein später `system`-Turn den LM-Studio-Engine-Kanal beenden.

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
