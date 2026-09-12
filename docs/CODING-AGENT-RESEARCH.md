# Coding Agent: Unsloth-Analyse und Umsetzungskriterien

Stand: 11.09.2026. Die Websuche lief ausschließlich über die lokale GO-Schnittstelle
`POST http://localhost:8080/v1/research/web`. Alle drei Suchantworten lieferten
`provider: searxng`, `isFallback: false` (18:50:07–18:50:43 Europe/Berlin).
Primärquellen wurden über `/v1/research/fetch` abgerufen; Such-Snippets allein
wurden nicht als technische Belege verwendet. Die installierten Unsloth-Dateien
wurden ausschließlich gelesen.

## Was Unsloth als Vorbild liefert

Unsloth Studio verbindet lokale Modellverwaltung und llama.cpp-Inferenz mit
Werkzeugaufrufen, Python-/Bash-Ausführung, Dateierzeugung, einer Vorschau und
Reparatur fehlerhafter Tool-Aufrufe. Das ist eine passende funktionale Referenz für
den GO-Coding-Modus. Training, Medienmodelle und allgemeine Studio-Funktionen
gehören nicht zur Coding-Agent-Laufzeit. Die Produktseite beschreibt selbst,
dass zusätzliche Suche, Code-Ausführung und Tool-Reparatur Laufzeit kosten.
Eine beworbene prozentuale Genauigkeitsverbesserung ist kein Nachweis für GO.
[Unsloth Studio](https://unsloth.ai/docs/new/studio)

Der offizielle Tool-Calling-Leitfaden verwendet strukturierte Funktionsschemas,
eine wiederholte Modell-/Tool-Schleife und echte lokale Ausführung. GO benötigt
dazu klar getrennte Zustände für Planung, Modellinferenz, laufendes Werkzeug,
Werkzeugergebnis und Abschluss. Tool-Ausgaben müssen mit der zugehörigen
Aufruf-ID in den nächsten Modellkontext gelangen.
[Unsloth Tool Calling](https://unsloth.ai/docs/basics/tool-calling-guide-for-local-llms)

## Konkrete Ursache für minutenlanges Warten

[Unsloth-Issue #10698](https://github.com/unslothai/unsloth/issues/10698) vom
10.09.2026 dokumentiert vollständige erneute Prompt-Verarbeitung nach jedem
Tool-Aufruf und nennt einen früheren Commit als schnelleren Vergleich. Der
später präzisierte Titel bezieht sich auf einen gesetzten Seed. Das ist eine
konkrete Regression, keine Erklärung für jede langsame Unsloth-Anfrage.

Die am Recherchezeitpunkt noch offene
[PR #10707](https://github.com/unslothai/unsloth/pull/10707) beschreibt die
Ursache: Eine Hilfsfunktion setzt bei festem Seed für jede Runde
`cache_prompt=false`. Ein Maintainer bestätigt diesen Codepfad. Laut späterem
PR-Kommentar bestätigte der Reporter Seed 1. Die vorgeschlagene Änderung lässt
die erste Runde kalt und erlaubt Cache-Nutzung für Fortsetzungen nach Tools.
Dabei wird bewusst auf bitidentische Reproduzierbarkeit über unterschiedliche
Batch-/Cache-Zustände verzichtet. Die PR war bei der Prüfung nicht gemergt;
ihr Vorschlag wird hier nicht als veröffentlichter Fix dargestellt.

Der lokale Installationsstand bestätigt den betroffenen Pfad:

- Paketmetadaten: `unsloth 2026.9.4`, `unsloth_zoo 2026.9.3`.
- Installationswurzel:
  `C:\Users\AMD\.unsloth\studio\unsloth_studio\Lib\site-packages\studio`.
- `backend/core/inference/llama_cpp.py:634–641`: festes Seed setzt
  `cache_prompt` auf `False`.
- Dieselbe Hilfsfunktion wird im Tool-Loop bei Zeile 30460 und im abschließenden
  Stream bei Zeile 32931 erneut aufgerufen.
- SHA-256 dieser Datei zum Prüfzeitpunkt:
  `04FC8D04AA43EA077B515A3F891FA8BDC7E63E64EE8110EDB83894B9296E064F`.

Die lokale Datei fordert bereits `return_progress=True` an (u. a. Zeile 30429),
führt Tool-Start-/Ergebnisereignisse und begrenzt wiederholte identische
Tool-Ergebnisse. `tool_loop_controller.py` enthält getrennte Entscheidung und
Abschluss sowie schemagestützte Argumentbehandlung. `studio_tool_loop.py`
begrenzt erfolglose Runden und verwendet Tool-Zeitlimits/Heartbeats. Daher wäre
die Aussage, Unsloth besitze überhaupt keine Fortschritts- oder Schleifenkontrolle,
unrichtig. Ob die konkrete Nutzeranfrage mit festem Seed lief, wurde nicht anhand
eines Unsloth-UI-Laufs reproduziert.

[Issue #6329](https://github.com/unslothai/unsloth/issues/6329) betrifft Fragen
zur Weiterleitung und Speicherung von Websuchanfragen. Es ist keine Quelle für
die Generating-Latenz. Für GO folgt daraus eine nachvollziehbare Suchroute über
den vorhandenen lokalen SearXNG-Dienst und eine sichtbare Herkunft der Ergebnisse.

## Ableitung für GO

Die offizielle [llama-server-Dokumentation](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
beschreibt die Wiederverwendung gemeinsamer Prompt-Präfixe mit `cache_prompt`,
Jinja-basierte Tool-Schemas und Streaming. `return_progress` liefert
`prompt_progress` mit Gesamt-, Cache- und verarbeiteten Tokenzahlen sowie
Zeitmessung. SSE-Pings halten stille Verbindungen beobachtbar. Diese Merkmale
müssen gegen die tatsächlich eingesetzte Serverversion geprüft werden; ein
Heartbeat belegt allein weder Tokenfortschritt noch erfolgreichen Abschluss.

Für GO ergeben sich folgende technische Kriterien:

1. **Ein Agenten-Loop:** GO führt die Werkzeuge aus; llama-server liefert
   strukturierte Aufrufe und Text. Kein zusätzlicher Studio-Tool-Loop dazwischen.
   Unsloth warnt bei externen Agenten ausdrücklich vor verschluckten Aufrufen,
   wenn Studio selbst die Tools ausführt.
   [Integrationshinweis](https://unsloth.ai/docs/basics/claude-code)
2. **Cache stabil halten:** Systemtext und Tool-Katalog bleiben innerhalb eines
   Laufs stabil. Neue Assistant-Aufrufe und Ergebnisse werden angehängt.
   Keine wechselnden Uhrzeiten oder zufälligen Kennungen am Prompt-Anfang und
   kein erzwungener Kaltcache nach jedem Werkzeug.
3. **Begrenzte Arbeitsschritte:** Gezieltes Dateilisting, Textsuche und Lesen
   begrenzter Ausschnitte, nachvollziehbare Dateiänderungen und explizite
   Prüfbefehle. Kontext-/Ausgabelimits, Schrittbudget und Abbruch unterbinden
   endlose Wiederholungen. Ein Toolfehler liefert eine korrigierbare Rückmeldung.
4. **Sofort sichtbarer Zustand:** Schon vor dem ersten Token erscheinen
   Modellvorbereitung und verstrichene Wartezeit. Modellfortschritt, Toolstart,
   laufender Prozess, Ergebnis, Fehler und Abbruch sind unterscheidbar.
   Lang laufende Befehle benötigen begrenzte Wartezeiten und Prozessbeendigung
   bei Abbruch; eine abschließende Antwort ersetzt keine reale Prüfung.
5. **Lokale Modellquelle:** Das Coding-Dropdown besitzt eine eigene Auswahl.
   GGUF-Dateien aus dem vorhandenen Unsloth-Modellverzeichnis werden für den
   nativen Windows-llama-server direkt verfügbar gemacht. Das GO-Gateway
   bleibt in Docker und erreicht Windows über `host.docker.internal:8081`. Es werden
   keine Modelldateien nach Linux kopiert. Geteilte GGUF-Dateien sind als ein
   Modell zu behandeln, Projektionsdateien und unvollständige Downloads nicht
   als eigenständige Textmodelle. Dateierkennung ist noch kein Lade-/Tool-Test.
6. **Nachprüfbare Grenzen:** Dateizugriffe bleiben im gewählten Workspace;
   Tool-Risikoklassen werden aus dem bekannten Katalog überprüft. Eine vom
   Modell gelieferte Behauptung einer niedrigeren Risikoklasse gilt nicht als
   Freigabe für einen Schreibzugriff oder Prozessstart.

## Aktueller Betriebsvertrag

Auf Wunsch des Nutzers läuft die Coding-Inferenz nativ auf Windows.
`windows/manage-coding-llama.ps1` verwaltet mit `Start`, `Status` und `Stop` den
Supervisor und das vorhandene Unsloth-Binary
`%USERPROFILE%\.unsloth\llama.cpp\build\bin\Release\llama-server.exe`.
Standardmäßig liest der Scanner `%USERPROFILE%\.cache\huggingface\hub`;
`-ModelRoot`, `-BinaryPath` und `-PythonPath` erlauben vorhandene abweichende
Installationspfade. Zustandsdateien und Logs liegen unter
`%LOCALAPPDATA%\GO-CodingLlama`. Es gibt keinen Coding-llama-Docker-Dienst.
Nach der erweiterten Nutzeranforderung verwenden auch General, Vision und
Embeddings denselben nativen Windows-Router. Recherche und andere Docker-Worker
bleiben Bestandteil des bestehenden Stacks. Die frühere separate Modellruntime
ist keine GO-Abhängigkeit mehr.

Der Gateway-Endpunkt `GET /v1/models/coding` liefert `models`, `modelRoot`,
`runtimeReachable`, optional `message` und `updatedAt`. Der Client sendet
`mode: coding`, die Capability `coding` und die ausgewählte
`preferredCodingModelId`. Der autorisierte Projektordner wird pro Sitzung in
der lokalen Datenbank gespeichert; ein zuletzt global verwendeter Ordner ist
keine Ausführungsfreigabe für andere Sitzungen. Die API-Verträge stehen in
`openapi/go-ai-v1.yaml` und `openapi/run-events-v1.schema.json`.

Die Ereignisse `model.generation` tragen unter anderem `codingLoading`,
`codingWaiting` und `elapsedSeconds`. Echte Prompt-/Tokenmetriken bleiben von
diesen Wartezeit-Heartbeats unterscheidbar. Die native Architektur und diese
Vertragsfelder belegen für sich noch keine Laufzeitqualität; dafür gelten die
nachfolgenden Prüfungen.

## Automatische Recherche und Arbeitsansicht

Im Coding-Modus stehen `web.search`, `web.fetch` und `web.deepResearch` dem
lokalen Modell ohne zusätzlichen Startknopf zur Verfügung. Das Modell entscheidet
anhand des Auftrags zwischen direkter Antwort, Dateiarbeit, kurzer Websuche und
mehrstufiger Recherche. Reine öffentliche Fragen verlangen keine Projektdateien.
Der vorhandene SearXNG-Dienst übernimmt die Suche; ein stiller Providerwechsel ist
nicht erlaubt. Abgerufene Seiten gelten als nicht vertrauenswürdige Quelldaten.

`web.deepResearch` plant Teilfragen, sucht, liest bestätigte Fundstellen und
synthetisiert eine belegte Antwort. Quelle-IDs und wörtliche Belege werden gegen
die tatsächlich gelesenen Texte geprüft. Innere Modell- und Werkzeugaufrufe
zählen gegen das Budget des Coding-Laufs; zusätzlich gelten eigene Zeit- und
Umfangsgrenzen. Planung, Suche, Lesen und Auswertung senden Fortschritt, und der
Lauf bleibt abbrechbar. Der äußere Agent erhält das Rechercheergebnis als
Werkzeugantwort und kann anschließend mit der Projektarbeit fortfahren.

Coding und General verwenden denselben Chatheader „AI Assistent“ ohne Modellangaben.
Die Coding-Ansicht zeigt den Workspace im Promptfenster sowie aufklappbare Werkzeugschritte
mit Eingaben, tatsächlichem Ergebnis und Status. Dateiänderungen erscheinen als
Vorschlag mit Diff und dem anschließend bestätigten Ausführungsstatus.
Die Historie bleibt mit der Nachricht gespeichert; allgemeine Chatansicht und
Nachrichtenfooter behalten ihre Funktionen. Das ist eine an Coding-Agenten
orientierte Arbeitsansicht, keine Behauptung vollständiger Codex-Funktionsgleichheit.

Die Standardanfragen verwenden das höchste unterstützte Reasoning: GPT-OSS `high`,
die installierten Qwen3.8-Modelle `xhigh`. Die Werte sind gegen deren lokale
GGUF-Chatvorlagen geprüft; Modelle ohne Thinking-Funktion erhalten keine erfundene
Reasoning-Stufe. Der native Prozess hat kein separates knappes Thinking-Budget.
Text- und Vision-Kontext beginnen beim GGUF-Maximum; llama kann den Kontext nur
bei Speichermangel reduzieren (`--fit`, 2 GiB Reserve je GPU). GO liest anschließend
das tatsächlich geladene Fenster aus `/props` und zählt den vollständigen Prompt
einschließlich Tooldefinitionen und Bildern über den nativen `input_tokens`-Pfad.
Ohne explizit kleinere API-Vorgabe nutzt die Ausgabe das verbleibende Fenster,
einschließlich Reasoning; feste 4.096-/8.192-Ausgabetoken-Grenzen entfallen.
Die Trennung zwischen Kontext, Ausgabetoken und Thinking-Budget folgt den
[llama.cpp-Serverparametern](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md).

Der Chip **Workspace** steht im Promptfenster direkt rechts neben **Workflows**.
Die native Ordnerauswahl bindet den ausgewählten Ordner an die erfasste Sitzung
und aktiviert Coding. Abbrechen verändert weder Ordner noch Modus. Andere
Sitzungen behalten ihre eigene Bindung; eine globale Einstellung ersetzt keine
Workspace-Auswahl. Ein laufender Auftrag muss vor einem Ordnerwechsel beendet
werden.

Aufgeklappte Werkzeugschritte nutzen die volle Inhaltshöhe. Bereits gelieferte
Eingaben, Diffs und Ergebnisse werden für die Anzeige nicht nochmals gekürzt.
Die Werkzeug-Ausführungsbudgets bleiben davon unabhängig: Sehr große
Prozessausgaben sind begrenzt und ausdrücklich als gekürzt gekennzeichnet.
Der ruhende Coding-Modus zeigt keinen zusätzlichen Bereitschaftsstatus über
dem Prompt.

GO verwendet allgemeine Systemanweisungen, Medienanalysen, Dokumentaufbereitung,
Begrüßungen und Themenprofile. Das aktuelle Nutzeranliegen bestimmt das Thema.
Diese eingebauten Anweisungen werden aus dem Anwendungscode erstellt und nicht
als veraltete Standardprompts in den Einstellungen oder Chatdaten gespeichert.
Bestehende Nutzernachrichten und Dokumentinhalte werden nicht umgeschrieben.

## Erforderliche Validierung

- Persistenz: General- und Coding-Modellauswahl unabhängig speichern und nach
  Neustart wiederherstellen; Coding-Zustand und vorhandene Chats erhalten.
- Workflow-Migration: neue Installation leer; nur die zwei bekannten
  mitgelieferten Workflow-IDs entfernen; eigene Kopien, Pins und Nachrichten
  erhalten, Suchindex und alte Auswahlreferenzen konsistent halten.
- Laufzeit: reales verfügbares Modell laden, strukturierten Tool-Aufruf
  ausführen, in einem temporären Workspace eine Datei ändern, Prüfbefehl
  ausführen und das Ergebnis vom Datenträger verifizieren.
- Fehlerwege: ungültige Argumente, unbekannte Tools, Pfadüberschreitung,
  fehlgeschlagener Prüfbefehl, Zeitlimit und Abbruch sichtbar prüfen.
- Latenz: Zeit bis erstem Status, erstem Token und Toolstart getrennt erfassen.
  Bei mehreren Tool-Runden verarbeitete und wiederverwendete Prompt-Token
  vergleichen. Erst dieses Experiment belegt Cache-Nutzung und eine
  Geschwindigkeitsverbesserung auf dieser Maschine.

Dieses Dokument trennt Recherche und Umsetzungskriterien von Testergebnissen.
Es behauptet weder bestandene Laufzeittests noch eine gemessene Beschleunigung.
