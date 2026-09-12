# Coding Agent: Validierungsnachweise

Stand: 11.09.2026, Europe/Berlin. Dieses Protokoll trennt abgeschlossene
Einzelprüfungen von der abschließenden Gesamtprüfung. Laufzeiten gelten für den
genannten lokalen Testfall; ein Geschwindigkeitsvergleich mit Unsloth wurde
nicht durchgeführt.

## Erste Coding-Abnahme vor der vollständigen Modellmigration

Der Coding-Server verwendet direkt das bereits installierte
`C:\Users\AMD\.unsloth\llama.cpp\build\bin\Release\llama-server.exe`.
`--version` meldete `0.4.0-dev`, Build `10840`, Commit `58670d128`, kompiliert
für Windows AMD64 durch Unsloth. Die Installationsmetadaten nennen das Bundle
`b10840-mix-d5c17a0`, CUDA13 für ältere GPUs, einschließlich SM75-Unterstützung.
Die vorhandenen Router- und GPU-Fitting-Optionen wurden mit `--help` geprüft.

Die erste Coding-Abnahme erfolgte vor dem ergänzenden Auftrag zur vollständigen
Ablösung von LM Studio. Die folgenden Lauf-IDs dokumentieren diesen Zwischenstand;
die abschließende Prüfung der vollständigen Umstellung folgt weiter unten.

Modellzugriffe erfolgen unmittelbar auf
`C:\Users\AMD\.cache\huggingface\hub`. Modellfiles wurden weder kopiert noch
heruntergeladen oder konvertiert. Der Gateway erreicht den nativen Router über
`http://host.docker.internal:8081`; localhost liefert unter `/health` einen
erfolgreichen Status und unter `/v1/models?reload=1` die lokalen Presets.

Die tatsächliche Discovery lieferte genau diese beiden vollständigen Textmodelle:

| Modell | Stabile Coding-ID | Dateigröße, dezimale GB | Nachweis |
| --- | --- | ---: | --- |
| Qwen3.8-27B-UD-Q8_K_XL | `coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896` | ca. 31,46 | Erkannt und im unten beschriebenen Coding-Lauf verwendet |
| Qwen3.8-Flash-Next-UD-Q2_K_XL | `coding/Qwen3.8-Flash-Next-UD-Q2_K_XL~c3df87cb03cc` | ca. 78,87 über drei Shards | Vollständige Discovery; keine Inferenzvalidierung dokumentiert |

Die vorhandene Flash-Next-Q6-Variante mit nur zwei von sechs Shards wurde
ausgeschlossen. ASR-Modelle, Projektoren und andere nicht geeignete GGUFs wurden
nicht angeboten. Der Scanner berücksichtigt Unsloths Sonderfall, bei dem der
erste Shard ausschließlich Metadaten und keine Tensoren enthält. Eine erfolgreiche
Discovery bestätigt Metadaten und Shard-Vollständigkeit; die native Runtime
validiert Tensorinhalte erst beim Laden.

## Scanner und Prozessverwaltung

`python -m unittest discover -s workers/coding -p 'test_*.py' -v` war mit
**4/4 erfolgreichen Tests** abgeschlossen. Die Fälle decken die Auswahl von
Textmodellen, unvollständige Shards, stabile unterschiedliche IDs bei gleichen
Dateinamen und das Hinzufügen beziehungsweise Entfernen von Presets ab.

Die vier PowerShell-Dateien `manage-coding-llama.ps1`, `start-ai-stack.ps1`,
`stop-ai-stack.ps1` und `common.ps1` bestanden die Parserprüfung. Ein isolierter
Environment-Test bestätigte die Übernahme eines Modellpfads mit Leerzeichen und
der nativen Gateway-URL, ohne die produktive Environment-Datei zu verändern.

Die native Verwaltung wurde tatsächlich mit Start, erneutem Start, Stop und
erneutem Start geprüft. Erneutes Starten verwendete denselben Supervisor.
Stop beendete den eigenen Router samt zwei zugehörigen Kindprozessen; die
anschließende Prüfung fand **keinen verbliebenen eigenen Prozess**. Der
Neustart lieferte erneut einen gesunden Router mit zwei Modellen. Die Prüfung
betraf ausschließlich die durch dieses Skript verwalteten Prozesse.

Der Supervisor speichert den tatsächlichen Python-Prozess, auch wenn ein
virtueller Interpreter zunächst einen Launcher startet. Zur Identitätsprüfung
dienen PID, Prozessstartzeit und Kommandozeile. Ein Windows Job Object bindet
seinen nativen Router und dessen Kinder an die Supervisor-Lebensdauer.
Unsloths eigene Prozesse und das schon vorhandene `manage-llama-server.ps1`
wurden dabei nicht gestoppt oder überschrieben.

Native Logausgabe wurde nach einem Neustart in
`%LOCALAPPDATA%\GO-CodingLlama\llama.stderr.log` tatsächlich gelesen:
Routerstart, Listener auf Port 8081 und beide Preset-IDs waren enthalten.
Die Runtime erhält eigene Log-Dateihandles, damit Windows-Interpreter-Launcher
die weitergeleitete Ausgabe nicht verlieren.

## Echter Coding-Agent-Lauf

Der opt-in Test
`CodingAgentLiveTests.LocalModelFixesPythonProjectRunsTestsAndStreamsProgress`
verwendete den laufenden Gateway, das native Qwen3.8-27B-Modell und den echten
Windows-Executor. Er lief auf einem temporären Python-Projekt mit einer zunächst
fehlerhaften Additionsfunktion. Der Test prüfte zunächst das Scheitern der
unveränderten Ausgangsversion.

- Run-ID: `run-b51477c6664545c6809ff4aaa726d0c3`.
- Modell: `coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896`, Kontext 32.768 Token.
- Ablauf: `coding.read` für die Implementierung, `coding.read` für die Tests,
  `coding.edit` mit vorher gelesenem SHA-256 und anschließend `coding.command`.
- Änderung: `return a - b` wurde durch `return a + b` ersetzt. Die Testdatei
  durfte durch die Test-Harness nicht verändert werden.
- Testkommando: `C:\Python314\python.exe -m unittest -v`.
- Ergebnis: der Agent-Lauf wurde abgeschlossen; anschließend führte die
  Harness die unveränderten Tests nochmals unabhängig aus und prüfte
  erfolgreichen Exit sowie `Ran 3 tests`.

Die gespeicherte SSE-Aufzeichnung enthält `run.started` um 19:06:46,758 und
`run.completed` um 19:08:13,457, also **86,70 Sekunden**, gerundet 87 Sekunden.
Davon entfielen ungefähr **45 Sekunden auf das erstmalige Modellladen**.
Diese Messung umfasst genau diesen kalten Lauf und ist kein allgemeines
Leistungsversprechen.

Lokaler Beleg: `artifacts/coding-validation/native-live-run.sse`.
Die Datei enthält vier `client_tool.proposed`-Ereignisse, zwei
`model.loading`-Ereignisse, 141 `model.generation`-Ereignisse, 60 `text.delta`-
Ereignisse und einen erfolgreichen Abschluss. Die Aufzeichnung belegt damit
sichtbare Zwischenstände und Textausgabe; das tatsächliche Werkzeugergebnis
wurde zusätzlich durch die Assertions der Live-Test-Harness geprüft.
Der Artefaktordner ist lokal und nicht Teil der regulär versionierten Quelldateien.

## Reale WebView und bestehendes Nutzerprofil

Das damalige Portable-Paket wurde im vorhandenen GO-Profil gestartet. Vorher
wurden Datenbank und Einstellungen unter
`%LOCALAPPDATA%/GO/Backups/before-coding-native-20260911-191255` gesichert.
Die tatsächliche Migration reduzierte die Workflow-Anzahl von zwei auf null;
zwei vorhandene Sitzungen, eine angepinnte Sitzung und 20 Nachrichten blieben
erhalten. Das Overlay zeigte „Noch keine Workflows vorhanden.“
Der nachfolgende Vergleich mit der Sicherung bestätigte alle 20 ursprünglichen
Nachrichten anhand von ID, Rolle und Inhalt sowie die vorhandene Pin-Zuordnung.

Das Coding-Dropdown zeigte beide oben genannten Modelle. Qwen3.8-27B wurde
über die Oberfläche ausgewählt und gespeichert. Die bestehende General-Auswahl
`gpt-oss-120b` blieb erhalten. LM Studio war bei diesem Test nicht erreichbar;
die unabhängige Coding-Auswahl und der folgende Coding-Lauf funktionierten.
Nach regulärem Schließen und erneutem Starten desselben Portable-Pakets wurden
Coding-Modell, Coding-Chip, Projektordner und abgeschlossener Chat wiederhergestellt.

Über Tools → Coding wurde eine zusätzliche Testsitzung angelegt. Der native
Ordnerdialog band ausschließlich
`artifacts/coding-validation/ui-project` an diese Sitzung. Die Oberfläche
zeigte den Coding-Chip und „Projekt: ui-project“.

Der über die WebView gesendete Auftrag ließ `calculator.py` und
`test_calculator.py` lesen und nur die Implementierung ändern. Für diesen
separaten Oberflächentest waren Prozessaufrufe ausdrücklich ausgenommen;
der vollständige Modell-/Befehlstest ist oben dokumentiert.

- Run: `run-8900d48d0e0c44bab5a6eca41d23253f`, erfolgreich abgeschlossen.
- Beginn 19:27:55,277, Ende 19:28:44,188: **48,91 Sekunden**, davon
  **19,09 Sekunden Modellladen**. Kein Vergleich unter kontrollierten gleichen
  Bedingungen mit dem früheren Lauf oder Unsloth.
- Tatsächliche Tools: `coding.read`, `coding.read`, `coding.edit`.
- Änderung auf Datenträger: `return a + b` → `return a * b`.
- Unveränderte unabhängige Tests: zuvor **2/2 fehlgeschlagen**, danach **2/2
  bestanden** mit `C:\Python314\python.exe -m unittest -v`.
- SSE: 113 Generierungsereignisse, 48 Text-Deltas, sichtbare Ladezustände mit
  verstrichener Zeit und echte Prompt-Verarbeitung; Beleg
  `artifacts/coding-validation/native-ui-run.sse`.

Der gemeldete Prompt-Fortschritt begann in der ersten Runde bei null. In den
Folgerunden begann er bereits mit 1.847/2.034, 2.071/2.275 und 2.434/2.538
verarbeiteten Token. Diese gemeldete Wiederverwendung von Präfixen ist konsistent
mit dem aktivierten Prompt-Cache; sie ist kein gemessener Speedup gegenüber Unsloth.

## Automatisierte Prüfung des ersten Coding-Stands

- Die finale Server-Suite nach den Modellwechsel- und Latenzkorrekturen bestand
  **184/184 Tests**, ohne übersprungene Tests. Ergebnis:
  `artifacts/coding-validation/coding-server-final.trx`.
- Die App-Suite nach der Korrektur der Prozessbereinigung bestand
  **280/280 Tests**, ohne übersprungene Tests. Ergebnis:
  `artifacts/coding-validation/coding-app-final.trx`. Der separate Live-Test ist
  opt-in; seinen tatsächlichen Modelllauf belegt der oben beschriebene Abschnitt.
- Die unveränderte echte JavaScript-Funktion `applySnapshot` wurde zusätzlich
  in sechs Fällen geprüft: Initialentwurf, Coding-Auswahl vor dem Debounce,
  veralteter Host-Entwurf, nach dem Senden leer bleiben, Wechsel zu Sitzung B
  und Rückkehr zu A. Alle sechs Fälle und die Syntaxprüfung bestanden.
- Das finale Gateway-Image wurde gebaut und der Gateway-Container erneuert.
  Der laufende Dienst ist gesund und `/v1/models/coding` liefert weiterhin die
  beiden direkt unter Windows verfügbaren Modelle.

## Paketprüfung des ersten Coding-Stands

`windows/build.ps1 -Configuration Release` lief vollständig durch: App- und
Protokoll-Build, Protokoll-Smoke, **280/280 App-Tests** einschließlich des letzten
Composer-Fixes, Portable-Publish und tatsächlicher Start-Smoke mit WebView2.
Das finale Paket ist `artifacts/portable/win-x64/GO.exe`; das vollständige Log
liegt in `artifacts/coding-validation/final-build.log`.

Die Flash-Next-Inferenz wurde nicht geprüft;
ein pauschaler Speedup oder ein nachgewiesener Stand-der-Technik-Vorsprung wird
aus den vorliegenden Tests nicht abgeleitet. Die Quellenanalyse steht separat
in `docs/CODING-AGENT-RESEARCH.md`.

## Vollständige Umstellung auf native Unsloth-Modelle

Der ergänzende Auftrag wurde für General, Coding, Vision und Embeddings umgesetzt.
Alle Rollen verwenden ausschließlich `http://host.docker.internal:8081`; die
aktiven Windows-Helfer benötigen keinen LM-Studio-Prozess und keine LM-Studio-API.
Ein gemeinsames Inferenz-Gate verhindert, dass ein paralleler Rollenwechsel ein
gerade verwendetes Modell entlädt. Wiederholte Tool-Runden nutzen dasselbe geladene
Preset. Text und Vision verwenden 32.768, BGE-M3 8.192 Kontext-Token.

Sechs vorhandene Dateien mit zusammen **95.771.651.584 Bytes (95,77 GB)** wurden
von `C:\Users\AMD\.lmstudio\models` nach
`C:\Users\AMD\.cache\huggingface\hub\models--<publisher>--<repo>\snapshots\<revision>`
verschoben. Vor dem ersten Verschieben wurden sämtliche SHA-256-Prüfsummen und
Dateigrößen gegen das gepinnte Manifest geprüft. Danach stimmten die NTFS-Datei-IDs
und Größen überein: Es erfolgte ein Umbenennen auf demselben Volume, keine Kopie
auf eine Linux-Ebene. Der Quellordner enthält keine Modelldateien mehr. Die
ursprünglichen Repository-Namen `lmstudio-community/...` bleiben als Provenienz
der Gewichte erhalten; sie sind keine Laufzeitabhängigkeit.

`huggingface_hub.scan_cache_dir` meldete alle vier migrierten Repositories ohne
Warnungen. Die unveränderte installierte Unsloth-Funktion
`hub.services.models.cache_inventory.list_cached_gguf_response` bestätigte
`scan_confirmed: true` und für alle vier Pakete `active_cache: true`,
`partial: false`, `runtime: llama_cpp`. Der laufende Unsloth-Dienst wurde dafür
nicht umkonfiguriert. Belege: `model-migration.json` und `unsloth-inventory.json`
im lokalen Ordner `artifacts/coding-validation`.

Der native Router erkennt nach der Migration neun Presets: fünf Textmodelle,
drei Vision-Presets mit zugeordnetem Projektor sowie ein Embeddingmodell. Der
Gateway bietet die fünf Textmodelle jeweils für General und Coding an. Die
Katalogdatei ist `native-model-catalog-after-migration.json`.

Die neue Server-Suite bestand **182/182**, die finale App-Suite nach allen
Sitzungs- und Freigabekorrekturen **292/292 Tests**.
Belege: `native-server-final.trx`, `native-app-final.trx`. Die Tests prüfen unter
anderem alte Provider-Einstellungen, getrennte Modellauswahl, native Aliasauflösung,
Modellwiederverwendung, Embedding-Modellwechsel und den Schutz laufender Inferenz.
Der Scanner bestand **7/7**, die Dateimigration **2/2 Tests**. Zehn aktive
PowerShell-Skripte bestanden die Parserprüfung. Die nachfolgend korrigierte
Windows-Argumentquotierung bestand zehn Roundtrips einschließlich abschließender
Backslashes; der native Stop-Aufruf wurde auch bei einem Dockerfehler bestätigt.

Das neu gebaute Gateway-Image wurde tatsächlich bereitgestellt. Ein Embedding-Batch
über `/v1/context/embeddings` lieferte mit dem migrierten BGE-M3 zwei Vektoren mit
jeweils **1.024 Dimensionen**; Beleg `native-gateway-embedding.json`.

Der tatsächlich aktivierte `NativeRoleLiveTests` verwendete denselben
`ModelRuntimeClient` wie der Server: GPT-OSS-120B antwortete mit `42` und echten
Text-Deltas, Qwen3-VL-30B erkannte die rote Fläche der deterministischen PNG als
`Rot`, BGE erzeugte zwei endliche, von null verschiedene 1.024-dimensionale
Vektoren. Der gesamte Rollenwechsel-Test dauerte **73,8 Sekunden** inklusive
Modellladen; Beleg `native-roles-live.trx`.

Der zusätzliche General-Live-Test über den produktiven Gateway mit nativer
GPT-OSS-120B-ID bestand in **48 Sekunden**. Er prüft mehrere sichtbare Text-Deltas
vor dem Abschluss und ausreichenden zeitlichen Abstand zwischen erster Ausgabe
und Abschluss. Beleg `native-general-gateway-live.trx`.

Die echte GO-Oberfläche zeigte alle fünf General- und Coding-Textmodelle. Als
General wurde `coding/gpt-oss-120b-MXFP4~fd8e76ec296f` ausgewählt und gespeichert,
während Coding auf `coding/Qwen3.8-27B-UD-Q8_K_XL~86704cb9d896` blieb.
Die gespeicherten Einstellungen sind Version 18 ohne bisherige Provider-URL;
Coding-Chip, Projektordner und bestehender Verlauf wurden wiederhergestellt.

Im finalen App-Lauf war `GO_AI_CODING_LIVE=1` tatsächlich gesetzt. Run
`run-accbb9849f4a46b5b8bb9dd8ede8e7f9` las beide Python-Dateien, korrigierte
`return a - b` zu `return a + b` mit dem zuvor gelesenen Hash und führte
`C:\Python314\python.exe -m unittest -v` aus. Alle drei unveränderten Tests
bestanden mit Exit-Code 0; die Harness prüfte sie anschließend unabhängig
erneut. Der Lauf dauerte 65,97 Sekunden und endete als `Completed`, mit `codingLoading`, `codingWaiting`,
`generationStarted`, `promptProcessing`, `tokenProgress`, `toolSelected` und
sichtbaren Text-Deltas. Belege: `native-app-final.trx` und
`native-final-coding-run.sse`.

Regressionstests sichern außerdem die erneute Ablauf-/Abbruchprüfung nach einer
Prozessbestätigung sowie die Bindung von Toolwahl und Ordnerdialog an die beim
Klicken erfasste Sitzung. Ein zwischenzeitlicher Sitzungswechsel verändert keine
fremde Sitzung. Die letzte Datenbankprüfung bestätigte erneut alle 20 ursprünglichen
Nachrichten, die ursprüngliche Pin-Zuordnung und null Workflows; Beleg
`profile-preservation.json`.

Die abschließende Veröffentlichung mit `windows/build.ps1 -Configuration Release
-SkipTests` bestand nach dieser separat vollständig ausgeführten Testsuite:
Release-Build, Protokoll-Build, Portable-Publish und isolierter Start-Smoke mit
WebView2. Beleg: `native-final-build.log`. Das ausgelieferte Paket ist
`artifacts/portable/win-x64/GO.exe`. Alle sechs laufenden Compose-Dienste sind
gesund; der Gateway verwendet ausschließlich die native Modellruntime auf 8081.

Nach dem Neustart dieses Pakets zeigte das Workflow-Overlay erneut
„Noch keine Workflows vorhanden.“ Die wiederhergestellte Coding-Sitzung führte
über die echte WebView `coding.read` auf `calculator.py` aus und lieferte korrekt
`return a * b`. Run `run-bc764341e65e4279bf05a96fd30aa7ed` wurde erfolgreich
abgeschlossen; SSE und lokale Datenbank enthalten denselben Text und genau den
angeforderten Dateileseschritt. Beleg: `native-final-ui-run.sse`.

Dieser letzte Oberflächentest deckte eine vorhandene Markdown-Bereinigung auf,
die abschließende Backticks und Hervorhebungszeichen entfernte. Die gespeicherten
Modellantworten waren unverändert korrekt. Die destruktive Bereinigung wurde
entfernt; `markdown.js` blieb unverändert. Der Regressionstest
`node --test tests/web/markdown-rendering.test.cjs` führt den tatsächlichen
Sanitizer und Renderer gemeinsam aus: vor der Korrektur 1/8, danach **8/8**
bestanden. Er prüft unter anderem Python-Codezaun und Kopierinhalt, Inlinecode,
starke Hervorhebung, noch unvollständige Streaming-Codeblöcke, unveränderte
Codeeinrückung und wörtliches HTML sowie bestehende LaTeX-Ausgabe und
Titelmetadaten. Die Webasset-Version wurde aktualisiert, damit vorhandene
Profile den korrigierten Code laden.

Nach dieser ausschließlich die Anzeige betreffenden Korrektur bestanden zusätzlich
alle **134 AssistantWorkflowTests**; Beleg `native-renderer-app.trx`.

## Erweiterte Coding-Werkzeugprüfung nach dem erneuten Unsloth-Abgleich

Der erneute, dateiweise Abgleich ist in `UNSLOTH-CODING-TOOLS-AUDIT.md`
dokumentiert. Die folgenden Tests gehören zur anschließenden Erweiterung;
die oben genannten Builds beschreiben jeweils den zuvor geprüften Stand.
Seit der letzten Nutzeranweisung wurden keine zusätzlichen Tests des
Workflow-Overlays angesetzt.

- `coding-workspace-app.trx`: **307/307 App-Tests**. Der tatsächlich aktivierte
  Datei-Live-Test mit Qwen3.8-27B (`run-317d947f884e4422a51b2ef829a634ef`)
  führte list/search/read/write/read/edit/read aus, ersetzte ENTWURF durch
  FREIGEGEBEN und ließ alle drei ursprünglichen Projektdateien unverändert.
- Die echte WebView führte in `run-734a89365f82400fb9d3c23541d8baec`
  write/read/edit/read aus. `ui-review.txt` enthält nach SHA-gesicherter
  Ersetzung `Status: GEPRÜFT`. Die unveränderten Python-Projekttests bestanden
  zusätzlich. Schritte, Diff, vollständige Antwort und Nachrichtenfooter
  wurden im Chat angezeigt und in derselben Nachricht gespeichert.
- `coding-tools-expanded.trx`: **57/57 gezielte Tests** für atomare Batch-Edits,
  Pfad-/Hash-/Mehrdeutigkeitsprüfung, CRLF/BOM, echte frühe Prozessausgaben,
  Unicode, Ausgabelimits, Abbruch des Prozessbaums, Callbackfehler,
  persistierte Schritte, Verlauf-/Wissenssuche und HTML-Isolation bestanden.
- `coding-powershell-live.trx`: **echter PowerShell-Live-Test bestanden in
  73 Sekunden**, Run `run-29cfee72b5f24d4bbec04ac23fd62f9c`. Das native
  Qwen3.8-27B-Modell las zuerst `probe.ps1`, wählte danach selbst die zwei
  vorgegebenen Aufrufe mit `-File probe.ps1 -Mode Success|Failure` und erhielt
  deren tatsächliche Ergebnisse vom produktiven `LocalCodingToolExecutor`.
  Beide liefen im ausgewählten Projekt, mit Exit-Code **0** bzw. **7**.
  stdout, stderr, `Grüße € 日本語`, Arbeitsordner und unveränderte
  Eingabedateien wurden geprüft. Das Testharness gab ausschließlich diese
  beiden Aufrufe frei. Die GUI-Prozessbestätigung wurde dabei nicht umgangen
  oder automatisiert; ihre Ablauf-/Abbruchregeln sind separat getestet.
  Die echten Resultate wurden über den produktiven Repository- und
  Anzeigeformatierer im Chat **PowerShell-Werkzeugtest** gespeichert.
  Vollständiger Beleg: `coding-powershell-evidence.json` und
  `coding-powershell-live.sse`.

Laufende Terminalausgaben aktualisieren höchstens zweimal pro Sekunde den
bestehenden Werkzeugschritt. Ein endgültiges Ergebnis enthält Eingabe,
Exit-Code und getrennte stdout-/stderr-Blöcke; die begrenzte Anzeige behält
Anfang und Ende beider Streams. Ein echter JS-Regressionsfall prüft mehrere
Teilausgaben, deren Ersatz durch das Endergebnis und die Wiederherstellung
ohne doppelte Ausgaben. Die aktuellen Webtests bestanden **21/21**.

`coding-context-live.trx` bestand mit tatsächlich aktivierter lokaler Inferenz
in **115 Sekunden**. Run `run-06629d87d5e2404f9b7db1600a83a2bb` benutzte
`coding.searchHistory`, `coding.searchKnowledge` und `coding.renderHtml`.
Der produktive Broker fand die vorher gespeicherte Nutzernotiz und das wirklich
importierte TXT-Dokument, einschließlich Nachrichten-/Dokument-ID und
Seitenzitat. Zwei zufällige Prüfmarker waren im Modellprompt nicht enthalten;
sie gelangten nur über diese tatsächlichen Werkzeugresultate zum Modell.
Die erzeugte HTML-Tabelle enthält beide belegten Varianten, Werte und Marker,
Inline-JavaScript und einen Umschaltknopf, ohne externe Ressourcen. Ihr
unveränderter HTML-Code und die Werkzeugresultate sind im Chat
**Kontext- und HTML-Werkzeugtest** gespeichert. Belege:
`coding-context-evidence.json`, `coding-context-live.sse` und die am
Testprofil abgelegte `comparison.html`. Dieser Lauf validiert Erzeugung und
Persistenz; die sichtbare WebView-Interaktion wird separat bewertet.

Die anschließende **echte WebView-Kontrolle** des Portable-Builds mit
Webassets `20260911-4` bestand: Der PowerShell-Testchat zeigte genau drei
gespeicherte Werkzeugschritte (Lesen, Erfolg, erwarteter Fehler), ausführbares
Programm, Argumente, Projektordner, Exit-Code 0/7 und getrennte stdout-/stderr-
Blöcke mit unverändertem Unicode. Flüchtige Modellphasen erschienen im
abgeschlossenen Verlauf nicht mehr. Im Kontext-Testchat öffnete der Button
die tatsächlich vom Modell erzeugte HTML-Vorschau; deren JavaScript-Button
blendete die belegte Erläuterung sichtbar ein. Schließen kehrte zum Chat
zurück. Alle fünf Nachrichtenfooter-Aktionen waren weiter vorhanden; der
Sprung zum Nachrichtenanfang wurde zusätzlich per Klick geprüft.

Ein vorübergehender Fehler des Windows-Automationshelfers
(`foreground window did not report a process id`) wurde durch dessen
JavaScript-Kernel-Neustart behoben. Danach wurden die beschriebenen
Oberflächenaktionen tatsächlich ausgeführt; kein offener GUI-Blocker.

Nach der unabhängigen Codeprüfung wurden zwei zusätzliche Randfälle behoben:
Abbruch-/Callbackfehler behalten die zuletzt wirklich empfangenen
Prozessausgaben ohne einen Exit-Code zu erfinden; lange Dokument-Chunks
liefern einen Ausschnitt um die tatsächliche Fundstelle statt nur ihren
Anfang. Acht Regressionfälle prüfen diese Situationen, persistierte
Teilausgaben, echte vollständige Ergebnisse mit Exit-Code 0/7 und die
ausdrückliche Kennzeichnung semantischer Treffer ohne lexikalische Fundstelle.
Die anschließende App-Suite bestand **215/215 Tests** mit
`FullyQualifiedName!~AssistantWorkflowTests`; die bereits geprüfte
Workflow-Suite wurde entsprechend der Nutzeranweisung nicht erneut angesetzt.
Beleg: `coding-tools-app-final.trx`. Die getrennten Live-Läufe oben wurden
mit aktivierten Opt-in-Variablen durchgeführt; dieser breite Regressionslauf
startete keine weiteren Modellaufträge.

Der letzte Release-/Portable-Build einschließlich isoliertem Start-Smoke
bestand (`coding-tools-final-build.log`, Paket `artifacts/portable/win-x64`).
Nach dem Neustart dieses Pakets waren Coding-Modell, Projektbindung,
Sitzungsdokument, alle drei Kontext-Werkzeugschritte und der vollständige
Nachrichtenfooter wieder vorhanden. Alle 20 ursprünglichen Nachrichten und
der ursprüngliche Pin sind weiterhin unverändert erhalten
(`coding-profile-final.json`).

Die erweiterte Server-Suite bestand **227/227 Tests**. Sie deckt zusätzlich
das Batch-Edit-Schema, konkrete SearXNG-Engine-Ausfälle, teilweise erfolgreiche
Suchantworten, kurze technische Suchprofile und die unveränderten Grenzen
der autonomen Recherche ab. Die Suchprofile benutzen vorhandene Engines
derselben lokalen SearXNG-Instanz; es erfolgt kein Anbieter-Fallback und kein
zusätzlicher versteckter Suchaufruf außerhalb des Recherchebudgets.


## Allgemeine Assistenz, Workspace-Chip und vollständige Werkzeuganzeige

Die eingebauten Branchenvorgaben wurden aus Server- und Client-Systemprompts,
Medienanalyse, Dokumentaufbereitung, Begrüßungen, Themenprofilen und
Titelregeln entfernt. Die reguläre Repository-Suche findet die alte
Spezialisierung nur noch in expliziten negativen Regressionstests.
Das bestehende GO-Profil enthält keine gespeicherten Systemprompt-/Policy-
Einstellungen und keine entsprechenden alten Prompt-Trigger (read-only geprüft).
Bestehende Nutzernachrichten und Dokumente werden dadurch nicht umgeschrieben.

`general-workspace-tool-display-app.trx`: **238/238 App-Tests bestanden**.
Die Workflow-Overlay-Suite wurde erneut ausdrücklich ausgeschlossen.
Neue Nachweise umfassen allgemeine Themen einschließlich kurzer Programmiertitel,
Workspace-Sitzungsbindung, atomaren Pfad-/Moduswechsel, SQLite-Rollback,
Abbruch und Gleichzeitigkeit mit Chatstarts. Werkzeugdetails über 12.000 Zeichen
und gültige Unicode-Prozessargumente mit rund 1,54 Millionen Anzeigezeichen
bleiben in Repository und Snapshot exakt erhalten. Pfad-/Hash-Metadaten,
Git-Diagnosen und Ausgabe mit eigenen Codefences sind abgedeckt.

`general-workspace-web-tests.log`: **27/27 Webtests bestanden**. Der Chip
Workspace steht direkt rechts neben Workflows. Der ruhende Coding-Modus
hat keinen Bereitschaftsstatus über dem Prompt. Aufgeklappte Schritte
besitzen keine inneren vertikalen Scrollbereiche; 400 stdout-, 200 stderr-
und 500 Diffzeilen sowie Backticks und wörtliches `<br>` bleiben beim
Rendern und Kopieren vollständig erhalten. Die fünf Footeraktionen bleiben
funktionsfähig. Aktuelle Webasset-Version: `20260911-6`.

Die separate Anzeige-/Speichergrenze beträgt 2 Mi Zeichen pro Schritt;
Werkzeug-Ausführungs- und Modellkontextbudgets wurden nicht erhöht.
Bereits früher gekürzte gespeicherte Details lassen sich daraus nicht
rückwirkend rekonstruieren. Neue Live-Harnesses verwenden dieselben
vollständigen Formatierer und prüfen die exakte Wiederherstellung.


Der anschließende Release-/Portable-Build samt isoliertem Start-Smoke bestand
(`general-workspace-tool-display-build.log`; GO.exe gebaut 21:52:41).
Die echte Oberfläche zeigt die neutrale Begrüßung, den Workspace-Chip in der
richtigen Reihenfolge und keinen Idle-Status. Die gespeicherten PowerShell-
Schritte wurden aufgeklappt: Dateiinhalt, vollständige Argumentlisten,
Exit-Code 0/7, stdout/stderr und Unicode sind über den gemeinsamen
Chat-Scrollbereich lesbar, ohne einzelne vertikale Scrollkästen.

Der native Ordnerdialog wurde geöffnet und per Escape abgebrochen; Modus
und Ordner blieben leer. Beim zweiten Öffnen wurde der vorhandene
`artifacts/coding-validation/ui-project` eingetragen. Der Benutzer bestätigte
„Ordner auswählen“, weil der Automationshelfer Mausklicks auf den separat
gehosteten PickerHost-Prozess verweigerte. Anschließend wechselte GO sichtbar
zu Coding und speicherte Pfad und Modus gemeinsam in Sitzung
`90d714b6-5a1e-43be-aee6-3b003ef2854c` (`workspace-ui-binding.json`).

`general-neutral-native-live.trx`: echter General-Chat mit dem verschobenen
GPT-OSS-120B-Modell **bestanden in 46 Sekunden**. Die Antwort zur Gestaltung
von Softwaretests enthält mehrere tatsächlich gestreamte Textdeltas,
keine frühere Branchenspezialisierung und ein erfolgreiches Laufende.


Nach einem erneuten tatsächlichen GO-Neustart waren Workspace `ui-project`,
Coding-Modus und ausgewähltes Qwen-Modell unverändert wiederhergestellt.
Der abschließende Profilvergleich bestätigt weiterhin alle **20 ursprünglichen
Nachrichten unverändert** und den ursprünglichen Pin (`coding-profile-final.json`).

`research-evidence-final-server.trx`: **258/258 Server-Tests bestanden**.
SearXNG-Fehler bleiben konkret sichtbar, ohne anderen Suchanbieter. Die
HTML-Extraktion erhält durch Inline-Markup getrennte API-Namen; gezielte
Fetch-Ergebnisse priorisieren verschiedene angeforderte Phrasen und berechnen
Abdeckung am wirklich ausgegebenen Text. Die Synthese wählt ausschließlich
aktuelle Beleg-IDs. Der Host ordnet diesen die tatsächlichen Originalexzerpte
zu und prüft deren exakte Herkunft; er verknüpft keine getrennten Trefferfenster
zu einem künstlichen Zitat. Diese Prüfung sichert die Zitatzuordnung, nicht
vollständig die semantische Richtigkeit jeder Modellaussage.

Das aktualisierte Gateway-Image `f4b90daf1432e1187e367c81bfc4c2d6501d86cfabdeb29b0d4e0a98c59233a1`
wurde gestartet und meldete healthy/live. Die sechs GO-Dienste sind healthy.
Native llama hört auf Windows-Port 8081, LM-Studio-Port 1234 ist nicht belegt.
General/Coding/Vision/Embedding-Rollen zeigen auf die vorhandenen Unsloth-
Cache-Dateien. Aktuelle Größen und NTFS-Datei-IDs der sechs migrierten Dateien
stimmen mit dem abgeschlossenen Migrationsmanifest überein; die großen Dateien
wurden für diesen Schlussvergleich nicht nochmals vollständig gehasht.


## Abschließende automatische Deep-Research-Live-Abnahme

`coding-deep-research-evidence-live.trx`: **bestanden in 7 Minuten 22 Sekunden**,
Run `run-0bc50598108949df9c95baa22e3c09b9`. Der natürliche Auftrag zur
Abbruch-/Timeout-Semantik von Python-APIs erzwang kein Werkzeug und enthielt
keinen manuellen Deep-Research-Start. Das native Qwen3.8-27B wählte
`web.deepResearch` selbst und schloss anschließend den gesamten Coding-Lauf ab.
Die unveränderten Live-Abnahmebedingungen für mehrere Suchen, Originalquellen,
Fortschrittsphasen, eindeutige Werkzeugoperationen und tatsächliches Laufende
wurden erfüllt.

Belegt sind fünf erfolgreiche SearXNG-Suchen (`provider: searxng`,
`isFallback: false`), sieben erfolgreiche gezielte Abrufe auf insgesamt vier
verschiedenen Original-URLs, acht quellengebundene Synthesebefunde und eine
vollständig gestreamte Endantwort. Nach der Deep-Synthese untersuchte der
äußere Agent weitere offene Punkte mit denselben Webwerkzeugen. Alle vier
Links der Endantwort entsprechen tatsächlich abgerufenen Originalquellen;
kein ungeprüfter Quellenlink wurde gefunden. Nicht abrufbare oder in den
Ausschnitten nicht belegte Aspekte bleiben als Einschränkungen sichtbar.
Die Prüfung der Zitatherkunft ist kein Beweis für die semantische Richtigkeit
jeder vom Modell gezogenen Schlussfolgerung.

Vollständige Belege: `coding-deep-research-evidence-live.sse`,
`coding-deep-research-result.json`, `coding-deep-research-final-answer.md`,
`coding-deep-research-acceptance.json` und `coding-deep-research-timing.json`.
Die früheren fehlgeschlagenen Live-Läufe bleiben als Diagnosebelege erhalten;
der oben genannte Lauf prüft den zuletzt deployten Stand. Die gemessene
Gesamtdauer ist kein Beschleunigungsbenchmark gegen Unsloth. Während der
Arbeit wurden tatsächliche Modell-/Recherche-/Werkzeugphasen und Textdeltas
übertragen, statt ausschließlich einen unveränderten Generating-Status anzuzeigen.

## Einheitlicher Header und maximale Modellvorgaben, 11.09.2026

Der Coding-Header verwendet dasselbe Markup und dieselben Desktop-/Mobilregeln
wie General: „AI Assistent“, ohne Modellzeile. Die Modellwahl bleibt in den
Einstellungen. Im neu veröffentlichten GO wurde Coding → General → Tools/Coding
mit nativen Screenshots und Accessibility geprüft; Überschrift, Schriftgröße,
Position und Aktionen bleiben gleich. Workspace und Coding-Chip sind nach dem
Wechsel erhalten. Nachweis: `unified-header-ui.json`.

Die Webtests bestehen 27/27, die App-Tests ohne Workflow-Overlay- und Live-Suite
243/243. Release/Portable-Build und isolierter Starttest bestehen. Artefakte:
`unified-header-web-tests.log`, `maximum-defaults-app.log/.trx`,
`unified-header-build.log`. Die veröffentlichte GO.exe hat SHA-256
`938B80D8E2BCEFB2F39C7AFB7F2285BA22997B1496DB08A4408372A2F32649C1`.

Die lokalen GGUF-Dateien bestätigen GPT-OSS 131.072, Qwen 262.144 und BGE-M3
8.192 Kontexttoken. GPT-OSS unterstützt als höchste Stufe `high`; beide
installierten Qwen3.8-Vorlagen unterstützen `xhigh`. 13 Python-Tests und der
PowerShell-Parser prüfen die native Konfiguration. Der native Windows-Prozess
wurde mit neun Modell-Presets, `max-fit` und unbegrenztem Thinking-Budget neu
gestartet. Metadatennachweis: `native-maximum-metadata.json`.

Die finale Server-Suite besteht 289/289 (`native-status-transition-server.log/.trx`).
Sie prüft unter anderem exakte Promptzählung, große Ausgabebudgets, kleinere
geladene Kontextfenster, Vision mit eigenem Modellmaximum und konkurrierende
Statusabfragen beim Laden/Entladen. Der Coding-Sonderabbruch nach 180 Sekunden
entfällt; die allgemeinen Modell-/Auftragszeitlimits bleiben wirksam.

Die echten Gateway-Anfragen enthalten keine Reasoning-, Kontext- oder
Ausgabetoken-Overrides. SSE-Abschluss, korrekte Antwort „42“, Gateway-Requestlog
und `/props` des nativen Prozesses bestätigen:

| Modus / Modell | Reasoning | Geladenes Fenster | Prompttoken | Erlaubte Ausgabetoken |
|---|---|---:|---:|---:|
| General / GPT-OSS 120B | high | 131.072 | 1.024 | 130.047 |
| Coding / Qwen3.8 27B | xhigh | 262.144 | 2.987 | 259.156 |

Runs: `run-2c4291b861724ba8a84214ec8482c57e` (40,995 s) und
`run-56f90e776b594a2db2e3bf3cfa5c5ef2` (39,648 s).
Die Nachweise liegen in `maximum-defaults-final/maximum-defaults-live.json`,
den beiden SSE-Dateien und `maximum-defaults-request-budget.log` in demselben
Ordner. Gemessen wurden die tatsächlich geladenen Fenster und Request-Limits;
der Test fordert keine künstliche Dauergeneration bis zum letzten Token.

Auch der native Produktionsclient wurde mit allen drei Rollen geprüft:
General liefert „42“, Vision erkennt das rote Testquadrat als „Rot“, BGE-M3
liefert zwei endliche, nichtleere Vektoren mit jeweils 1.024 Dimensionen.
Gesamtdauer 75,7 s; `maximum-defaults-native-roles.log/.trx`. Dieser separate
General-Aufruf nutzt absichtlich ein kleineres explizites Testbudget; die
Standardvorgaben sind durch die obigen Gateway-Läufe belegt.

Die abschließende Statuskorrektur ist im Gateway-Image
`sha256:a46ac14164bbb92874e2d0132e6fca31f56e358edc4c170433e5a76f4f06f7b5`
deployt. Der echte Wechsel von Qwen zu GPT-OSS 20B wurde über einen neuen
Gateway-Auftrag ausgelöst (`run-7d7abc9da1a540a79a4e50b75fb7a2df`). Alle 45
parallelen Statusproben blieben erreichbar, der Lauf endete korrekt mit „42“.
Der Request bestätigt auch für GPT-OSS 20B `high`, 131.072 Kontexttoken und
130.055 verfügbare Ausgabetoken nach 1.016 Prompttoken. Das Gateway meldete dabei
keine irrtümliche Runtime-Unerreichbarkeit. Nachweise:
`native-model-switch-live.json/.log`, `native-model-switch-gateway.log` und
`native-status-gateway-build.log`. Alle sechs Docker-Dienste sind gesund;
die aktualisierte portable GO-Anwendung läuft.
