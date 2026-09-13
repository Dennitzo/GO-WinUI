# Coding-Effizienz: Einzelagent, Belegarchiv und Prüfstand

Stand: 12. September 2026. GO verwendet einen einzelnen Coding-Agenten mit dauerhaft aktivem Arbeitsstand und der höchsten vom Modell unterstützten Denkleistung. Die beiden experimentellen Einstellungen und die adaptive Umschaltung wurden entfernt. Parallele Spezialisten bleiben entfernt; die native Konfiguration bleibt bei einem Slot und 262.144 Kontexttoken.

Der aktuelle Funktionsstand wurde mit 427 Server-Tests und 389 App-/Core-Tests ohne Workflow geprüft. Eine zusätzliche gezielte Serie mit 30 Arbeitsstand-Integrationstests enthält zwei neue Wiederaufnahmefälle. Ein echter isolierter Modelllauf dauerte 164 Sekunden: sechs Runden ausschließlich mit `xhigh`, tatsächliche Dateiänderung und Testausführung, fünf bestandene unabhängige Prüffälle und unveränderte Schutzdateien. Arbeitsstand und Planupdates wurden während des Laufs gespeichert. Dies ist ein Funktionsnachweis, kein allgemeiner Geschwindigkeitsvergleich. Belege: `artifacts/working-state-always-live/results.json` und `observed-working-state.json`.

## Dauerhafter Arbeitsstand

`CodingWorkingState` ergänzt das Gespräch um den ursprünglichen Auftrag, Plan und Akzeptanzkriterien, belegte Erkenntnisse, verworfene Hypothesen, Werkzeugbelege, Prozesse, aktive Dateiausschnitte und Fehlersignaturen. Der Arbeitsstand wird mit offenen Werkzeugaufrufen im `AgentRunCheckpoint` gespeichert. Er bleibt auf höchstens 224 Werkzeugbelege, zwölf aktive Dateien und 64 Fehlersignaturen begrenzt. Referenzierte Plan- und Erkenntnisbelege bleiben bei der Auswahl erhalten.

`coding.updatePlan` übernimmt Teilupdates anhand bestehender IDs. Nicht genannte Punkte und Felder bleiben erhalten; neue Punkte benötigen Titel und Status. Ein neuer Abschlussstatus benötigt einen vorhandenen erfolgreichen Werkzeugbeleg. Ein Planupdate kann sich nicht selbst als Ausführungsbeleg verwenden. Die Rückgabe enthält nur betroffene IDs und Status, gegebenenfalls Phase, nächsten Schritt und Anzahl empfangener Befunde. Diese Prüfung bestätigt die Existenz und den Ausführungserfolg der referenzierten Belege; sie beweist nicht automatisch jede frei formulierte Schlussfolgerung.

Die `ev-…`-Kennung eines lokalen Belegs wird einem eindeutigen ursprünglichen Werkzeugbeleg zugeordnet. Erfolgreiches Nachlesen einer fehlgeschlagenen Befehlsausgabe macht den ursprünglichen Befehl nicht nachträglich erfolgreich. Große Argumente abgeschlossener Aufrufe werden im Modellkontext durch Belege mit Aufruf-ID, Argumenthash und relevanten Parametern ersetzt. Das verbessert den vorhandenen Vorfilter; ursprünglicher Auftrag und Journal bleiben getrennt erhalten.

Dateiausschnitte werden nach Änderungen oder möglicherweise ausgeführten Prozessen als erneut zu lesen markiert. Auch ein unbekannter Prozessausgang kann den Cache veralten lassen. Dabei entsteht kein erfundener aktueller Hash. Ein erfolgreicher, sachlich anderer Test löscht frühere Fehlersignaturen nicht pauschal. Explizit nicht ausgeführte Vorschläge sind von der Aktualitätsinvalidierung ausgenommen.

Quellen: [CodingWorkingState.cs](../src/GoAi.Server.Core/Coding/CodingWorkingState.cs), [CodingWorkingStateTools.cs](../src/GoAi.Server.Core/Coding/CodingWorkingStateTools.cs), [CodingEvidenceContext.cs](../src/GoAi.Server.Core/Coding/CodingEvidenceContext.cs), [CodingContextCompactor.cs](../src/GoAi.Server.Core/Coding/CodingContextCompactor.cs), [AgentRunCheckpoint.cs](../src/GoAi.Server.Core/Runs/AgentRunCheckpoint.cs).

## Lokales Belegarchiv

Die tatsächlich erfassten Werkzeugdaten liegen außerhalb des bearbeiteten Projekts:

```text
<Settings.DataDirectory>/Cache/CodingRuns/<rootRunId>/
```

Die Grenze beträgt **8 MiB je Werkzeugschritt und 256 MiB je Lauf**. Eingabe, Ergebnis, stdout und stderr teilen sich die Schrittgrenze; Metadatenreserve wird innerhalb der Grenzen berücksichtigt. stdout und stderr bleiben getrennt. Die begrenzte Liveanzeige zeigt weiterhin Fortschritt, während das Archiv auch das gezielte Nachlesen mittlerer Abschnitte langer Ausgaben erlaubt. Überschreitungen und Speicherprobleme erhalten ausdrückliche Hinweise; nicht erfasste Daten werden nicht als vorhanden behauptet.

| Werkzeug | Vertrag |
| --- | --- |
| `coding.readOutput` | Opaque `evidenceId`, Kanal `stdout`, `stderr`, `input` oder `result`, UTF-16-Offset; standardmäßig 4.000, maximal 8.000 Zeichen je Seite, mit Folgeoffset und Kürzungshinweis. |
| `coding.searchRunEvidence` | Wörtliche Suchphrase bis 512 Zeichen im aktuellen Lauf; standardmäßig acht, maximal 20 Treffer mit Beleg-ID, Kanal, Offset und Ausschnitt. |

Der Host bindet das Archiv an Sitzung und Lauf. Modellargumente können weder einen Dateipfad noch eine fremde Sitzung auswählen. Das Nachlesen startet keinen ursprünglichen Befehl erneut. Das Archiv verfolgt keine Verknüpfungen. Ein Speicherfehler nach einer bereits angewendeten Mutation darf deren Erfolg nicht in eine automatisch wiederholbare Aktion verwandeln.

Bereinigung erfolgt nach tatsächlich abgeschlossener Sitzungslöschung. Nach Sammellöschung wird gegen noch vorhandene Sitzungen geprüft; angepinnte oder gleichzeitig erhalten gebliebene Sitzungen behalten ihre Daten. Historische Pilotartefakte werden durch die Entfernung der Spezialistenfunktion nicht gelöscht.

Quellen: [CodingRunEvidenceStore.cs](../src/GoWinUI.Core/Coding/CodingRunEvidenceStore.cs), [LocalCodingToolExecutor.cs](../src/GoWinUI.Core/Coding/LocalCodingToolExecutor.cs), [LocalToolBroker.cs](../src/GoWinUI.App/Services/LocalToolBroker.cs), [AssistantCoordinator.cs](../src/GoWinUI.App/Services/AssistantCoordinator.cs).

## Feste Policy und native Denkleistung

Arbeitsstand und `coding.updatePlan` sind für Coding immer aktiv. Alte JSON-Einstellungen werden ignoriert und beim Speichern entfernt. Der Server normalisiert auch alte Clientanfragen mit deaktiviertem Arbeitsstand oder `adaptive` auf die feste Policy. Beim Wiederaufnehmen eines alten Checkpoints ohne Arbeitsstand wird dieser aus dem ursprünglichen Auftrag erzeugt; vorhandene Arbeitsstände bleiben erhalten.

Jede Modellrunde einschließlich Kontextverdichtung verwendet die höchste modellseitig unterstützte Denkstufe; beim installierten Qwen3.8 ist das `xhigh`. Phasenwechsel reduzieren weder Denkleistung noch verfügbare Werkzeuge. Optionale alte Vertragsfelder bleiben ausschließlich für kompatible Deserialisierung erhalten. Die produktive adaptive Policy wurde gelöscht.

Die maximale Kontextkapazität und die bestehende Berechnung des Antwortbudgets bleiben erhalten. Arbeitsstand begrenzt weder Gesamtlaufzeit noch die Zahl der Runden. Die höchste Denkstufe ist keine Garantie für fehlerfreie Ergebnisse; belegte Abschlusskriterien und Tests bleiben erforderlich.

Quellen: [RunProcessor.cs](../src/GoAi.Server.Core/Runs/RunProcessor.cs), [AgentToolCatalog.cs](../src/GoAi.Server.Core/Runs/AgentToolCatalog.cs), [SettingsPage.xaml](../src/GoWinUI.App/Pages/SettingsPage.xaml), [CodingWorkingStateIntegrationTests.cs](../tests/GoAi.Server.Tests/CodingWorkingStateIntegrationTests.cs).

## Ereignisse, Wiederaufnahme und Messdaten

`AssistantRunEventPump` trennt SSE-Empfang von lokalen Werkzeugen. Ein geordneter Consumer führt die UI-Änderungen zusammen; der Kanal ist auf 256 Signale begrenzt und verwendet Rückstau. Ein gewöhnlicher Reconnect startet eine laufende lokale Aktion nicht erneut. Bei terminalen Serverereignissen wird die Worker-Cancellation vor der abschließenden UI- und Datenbankverarbeitung angestoßen.

Vor lokaler Ausführung wird ein dauerhaftes Tooljournal angelegt. Gespeicherte Ergebnisse können erneut übermittelt werden. Nach Neustart werden passende Einträge im Zustand `executing` ohne Ergebnis anhand von lokalem Lauf und Serverlauf als unbekannter Ausgang abgeschlossen und nicht erneut ausgeführt. Der Empfangscursor wird dafür nicht auf frühere Werkzeugaufrufe zurückgesetzt. Ein unbekannter Ausgang bleibt ausdrücklich unbekannt; dies ist keine verteilte Exactly-once-Garantie über beliebige Abstürze hinweg.

`coding.metrics` liefert pro Modellrunde Phase, Denkstufe, Queuezeit und vorhandene native Messgrößen: Tokenzählung, Prompt- und Generationszeit, erste Ausgabe, Gesamtzeit, ausgewertete und gecachte Prompttoken sowie Ein-/Ausgabetoken. Fehlende Reasoning- oder Providerwerte bleiben `null`. Zeichen und Streamingfragmente werden nicht als gemessene Token ausgegeben. Sichtbarer Fortschritt und Performancemessung sind getrennt zu bewerten.

Die verbliebenen Coding-Optionen werden anhand der Serverfähigkeiten verhandelt. Ein älterer Server erhält keinen unbekannten Optionsblock. `coding.evidence` erlaubt Nachlesewerkzeuge unabhängig von `UseWorkingState`; dieselben notwendigen Werkzeuge stehen dadurch auch der Vergleichsvariante ohne Arbeitsstand zur Verfügung.

Quellen: [AssistantRunEventPump.cs](../src/GoWinUI.App/Services/AssistantRunEventPump.cs), [GoAiAssistantService.CodingOptions.cs](../src/GoWinUI.App/Services/GoAiAssistantService.CodingOptions.cs), [SqliteAiClientRepositories.cs](../src/GoWinUI.Infrastructure/Repositories/SqliteAiClientRepositories.cs), [ModelTurnMeasurement.cs](../src/GoAi.Server.Core/Models/ModelTurnMeasurement.cs), [AgentToolCatalog.cs](../src/GoAi.Server.Core/Runs/AgentToolCatalog.cs).

## Historische Messung und bewusste Entfernung

Pilot 004 prüfte denselben Zweimodulfall `03-clamp` in je einer Wiederholung. Alle drei Varianten führten tatsächliche Änderungen und Tests aus, bestanden das unabhängige Oracle mit 15 Fällen und erhielten die geschützten Dateien. Die Ergebnisse bleiben als historischer Entscheidungsbeleg erhalten:

| Damalige Variante | Dauer | Modellrunden | Lokale Werkzeugaufrufe |
| --- | ---: | ---: | ---: |
| Einzelagent mit Arbeitsstand | 344,823 s | 8 | 15 |
| Ein zusätzlicher Spezialist, inzwischen entfernt | 765,256 s | 16 | 24 |
| Zwei zusätzliche Spezialisten, inzwischen entfernt | 818,648 s | 20 | 23 |

Im Zweikindlauf waren zwei echte Berichte, genau ein erfolgreiches Warten und elf auf reale Datei-Reads zurückführbare Belege vorhanden. Von 818,648 Sekunden entfielen 814,411 Sekunden auf Modellanfragen. Die reine Werkzeugübermittlung betrug insgesamt 2,210 Sekunden; der Unit-Test-Prozess dauerte 102 Millisekunden. Die zusätzlichen Agenten erhöhten in diesem Fall vor allem Modellarbeit und mehrfache Einarbeitung. Die Funktionsprüfung allein begründete keinen praktischen Vorteil. Auf ausdrücklichen Nutzerwunsch wurden diese Varianten deshalb wieder entfernt; sie sind keine auswählbaren Experimente mehr.

Das sind drei Einzelmessungen, keine allgemeine Aussage zur Qualität oder Geschwindigkeit anderer Modelle und Projekte. Die damaligen langen Berichte wurden ausdrücklich gekürzt. Die geplante Weiterentwicklung ihrer Anfang-/Ende-Darstellung entfällt mit der entfernten Funktion. Die Entfernung verändert Quell- und Buildfingerprints; diese alten Läufe dürfen daher nicht mit neuen Läufen zu einer Qualifikation des aktuellen Builds zusammengezählt werden. Parallelitäts- und Zwei-Slot-Versuche werden nicht als ausstehende Produktfeatures geführt.

Belege: [results.json](../artifacts/coding-efficiency/live-pilot-004/results.json), [timings.json](../artifacts/coding-efficiency/live-pilot-004/jobs/03-clamp--r1--two-specialists/timings.json), [events.jsonl](../artifacts/coding-efficiency/live-pilot-004/jobs/03-clamp--r1--two-specialists/events.jsonl).

Der separate Slot-Versuch 001 wurde beim Entladen der Produktionsmodelle abgebrochen, bevor isolierte Inferenz startete. Wegen einer verspätet abgeschlossenen Entladung musste der ursprüngliche Modellzustand anschließend ausdrücklich wiederhergestellt werden. Der gesicherte Wiederherstellungsbeleg meldet `modelRestored=true`, einen Slot, 262.144 Kontexttoken pro Slot und `processing=false`. Das Experiment und seine aktiven Hilfsskripte wurden nicht weitergeführt. Belege: [native-slots-live-001.log](../artifacts/coding-efficiency/native-slots-live-001.log), [recovery.json](../artifacts/coding-efficiency/native-slots-live-001/recovery.json).

Der frühere Pilot 003 bestand den einfachen Additionsfall mit Arbeitsstand in 258,027 Sekunden: acht Modellrunden, sieben lokale Aufrufe, zwei öffentliche Tests und fünf Oracle-Fälle. Sein anschließender adaptiver Lauf scheiterte an einer Windows-Dateisperre beim Speichern des Benchmarkzustands. Dieser Harnessfehler liefert keine belastbare Aussage zur adaptiven Modellqualität. Die Korrektur wurde separat geprüft. Belege: [result.json](../artifacts/coding-efficiency/live-pilot-003/jobs/01-add--r1--working-state/result.json), [live-pilot-003.log](../artifacts/coding-efficiency/live-pilot-003.log).

## Vorhandene Prüfbelege

Die abschließenden Einzelagent-Suites nach der Entfernung ergeben **429 Server-, 374 App- und 82 Webtests: insgesamt 885 bestandene Tests**. Frühere gezielte Wiederholungen und historische Suitezahlen werden nicht hinzuaddiert. Diese technische Prüfung ersetzt keine Leistungsqualifikation.

| Prüfung | Abgeschlossener Prüfstand | Log |
| --- | --- | --- |
| Server nach Entfernung | 429 bestanden, 0 fehlgeschlagen | [server-single-agent-final.log](../artifacts/coding-efficiency/server-single-agent-final.log) |
| App/Core/Infrastructure nach Entfernung, ohne Workflowtests | 374 bestanden, 0 fehlgeschlagen | [app-single-agent-final.log](../artifacts/coding-efficiency/app-single-agent-final.log) |
| Web nach Entfernung | 82 bestanden, 0 fehlgeschlagen | [web-single-agent-final.log](../artifacts/coding-efficiency/web-single-agent-final.log) |
| Benchmark Release-Build nach Entfernung | 0 Warnungen und 0 Fehler | [benchmark-single-agent-final.log](../artifacts/coding-efficiency/benchmark-single-agent-final.log) |
| Portable-Publish und isolierte Startprüfung nach Entfernung | Bestanden, gebündelte native Laufzeitdateien geprüft | [portable-single-agent-final.log](../artifacts/coding-efficiency/portable-single-agent-final.log) |
| Linux-Gateway-Image nach Entfernung | Build bestanden; Container anschließend neu erstellt und gestartet | [gateway-single-agent-final.log](../artifacts/coding-efficiency/gateway-single-agent-final.log), [gateway-single-agent-deploy.log](../artifacts/coding-efficiency/gateway-single-agent-deploy.log) |

Die erneut ausgeführte Einzelagent-Fixtureprüfung akzeptierte zwölf Referenzprogramme und wies alle zwölf defekten Ausgangsprogramme ab. Die separate Python-Vorprüfung wies auch jede Mischung mit einzeln zurückgesetzter notwendiger Reparaturdatei ab. Fünf Windows-Speicherprüfungen bestätigten atomischen Ersatz nach Lesefreigabe, Erhaltung des vollständigen alten Checkpoints bei Sperre, Cancellation, Diagnose dauerhafter Fehler und Schutz fremder temporärer Dateien. Diese Prüfungen nutzten kein Modell. Belege: [fixture-checks.json](../artifacts/coding-session-review/fixture-preflight-20260912-v3/fixture-checks.json), [dotnet-fixtures-single-agent.log](../artifacts/coding-efficiency/dotnet-fixtures-single-agent.log), [storage-unit-checks.json](../artifacts/coding-efficiency/dotnet-fixtures-single-agent/storage-unit-checks.json).

## Nachprüfung der Portable-Chips am 12. September

Die gemeldete Rücksetzung des Vorlesen-Chips und das Fehlen des Dateiänderungschips hatten eine bestätigte Auslieferungsursache: Der Desktop-Link `GO.App.lnk` startete `artifacts/portable/win-x64/GO.exe` aus dem Stand von 11:10 Uhr. Die neuere Ausgabe lag unter `artifacts/portable-coding-agent/win-x64/GO.exe`. Der Standardpfad wurde inzwischen mit dem vollständigen Quellstand einschließlich der folgenden Korrekturen neu veröffentlicht; die bestehende Desktop-Verknüpfung bleibt verwendbar. Der finale Publish vom 12. September um 17:37 Uhr prüfte zusätzlich alle 35 tatsächlich entpackten Webdateien gegen ihre Quell-Hashes. Beleg: [publish-final.log](../artifacts/coding-efficiency/portable-regression-review/publish-final.log).

Die acht betroffenen Dateien in `bin/x64/Portable/.../Assets/Web` waren bytegleich mit dem Quellstand. Das HTML lädt die aktuellen Skripte und Styles unter derselben Revision `20260912-7`; die Bridge erlaubt `coding.changes`. 35 gezielte Webtests für Chips, Bridge und Coding-Ansicht wurden erneut erfolgreich ausgeführt. Sie sind eine gezielte Teilprüfung, keine zusätzlichen 35 unabhängigen Tests neben der 885er-Gesamtzahl.

Ein zusätzlicher Headless-Edge-Test öffnete das tatsächliche `Assets/Web/index.html` und seine produktiven Assets. Er simulierte ausschließlich versionierte native Hostereignisse über den echten `bridge.js`-Empfang: keine native TTS-Anfrage, keine Modellinferenz, keine Mikrofonaufnahme. Geprüft wurden die Wartestatusanzeige mit sichtbar nur „Vorlesen“, Spinner und Pause-/Stopbuttons, zugänglich erhaltene und visuell geclippte Details, Pause/Fortsetzen/Stop-Nachrichten sowie ein weiter inaktives Mikrofon. Der Dateiänderungschip zeigte zwei Dateien und korrekte Nettozeilenzahlen, öffnete die vollständigen Diffs, verwarf ältere Revisionen und verschwand bei vollständigem Revert. Helle, dunkle und schmale Ansichten zeigten keinen horizontalen Überlauf; es traten keine JavaScript-Seitenfehler auf.

Diese Browserprüfung belegt Rendering und Ereignisverarbeitung mit isolierten simulierten Hostdaten. Sie ist kein Nachweis tatsächlicher Audioausgabe oder der vom Desktop-Link gestarteten nativen Anwendung. Artefakte: [verification.json](../artifacts/coding-efficiency/portable-regression-review/web-ui/verification.json), [Prüfskript](../artifacts/coding-efficiency/portable-regression-review/web-ui/verify-chips-ui.cjs), [helle Wartestatusansicht](../artifacts/coding-efficiency/portable-regression-review/web-ui/chips-waiting-light.png), [pausierte Ansicht](../artifacts/coding-efficiency/portable-regression-review/web-ui/chips-paused-light.png), [Diffübersicht](../artifacts/coding-efficiency/portable-regression-review/web-ui/changes-dialog.png), [schmale Ansicht](../artifacts/coding-efficiency/portable-regression-review/web-ui/changes-chip-narrow.png).

### Behobene Laufzeitfehler und endgültiger Stand

Die native WebView-Allowlist ließ `coding.changes` nicht passieren. Dieser Hostfehler ist korrigiert; der Monitor→Coordinator-Test prüft nun auch die native Freigabe. Atomare Änderungssummaries erlauben Delete-Sharing beim Lesen und wiederholen kurzzeitige Windows-Ersetzungssperren begrenzt und abbrechbar. Große Werkzeugreceipts behalten tatsächliche Status-, Hash-, Paging- und Archivinformationen. `coding.command` akzeptiert auch absolute Arbeitsverzeichnisse innerhalb des gewählten Workspaces und prüft sie weiterhin auf Verzeichnisgrenzen und Reparse-Points. Ungültige Plan-Beleg-IDs werden konkret benannt, ohne die Belegprüfung abzuschwächen.

Vorlesen besitzt jetzt einen gemeinsamen Pausenzustand für Vorbereitung, Textwarteschlange und Audiowiedergabe. Der Pause-Button bleibt auch zwischen Segmenten verfügbar. Neue Segmente und automatisches Nachpuffern respektieren die Pause; Fortsetzen verarbeitet wartenden Text einmal, Stop/Abbruch löst wartende Tasks auf. Die bestehenden kompakten Webassets bleiben unverändert. Fünf neue Host-/Queue-Regressionen sowie eine Web-Ereignisregression decken diese Zustände ab. Der zusätzlich vorgesehene native UI-Test dieser letzten Pausekorrektur wurde auf ausdrücklichen Nutzerwunsch ausgelassen; es wurde kein weiterer AI-Lauf gestartet.

**905 automatisierte Tests bestanden:** [390 App-/Core-Tests ohne Workflow](../artifacts/coding-efficiency/portable-regression-review/app-tests-speech-final.log), [432 Server-Tests ohne Live-Kategorie](../artifacts/coding-efficiency/portable-regression-review/server-tests-final.log) und [83 Webtests ohne Workflow](../artifacts/coding-efficiency/portable-regression-review/web-tests-final.log). Der Gateway wurde nach den Serverkorrekturen aktualisiert: [Deployment](../artifacts/coding-efficiency/portable-regression-review/deployment-final.json). Dieser historische Prüfstand hatte noch optionale Profile; die oben beschriebene feste Policy ersetzt diese inzwischen. Spezialisten sind weiterhin entfernt.

Ein echter, isolierter Coding-Lauf über die Standard-Portable-Version wurde vor den letzten Pause-/Eingabediagnosekorrekturen vollständig abgeschlossen: 452,6 Sekunden, zehn Modellrunden mit `xhigh`, 13 Clientaufrufe, 262.144 Tokens Kontextkapazität und aktivierter Arbeitsstand. Das Diagnoseprogramm wurde genau einmal ausgeführt; der Agent las einen 300-Zeichen-Ausschnitt aus der Mitte der 57.358 Zeichen langen archivierten Ausgabe nach, änderte `calculator.py` mit Hashprüfung und führte drei erfolgreiche Unittests aus. Fünf unabhängige Rechenfälle und unveränderte Diagnose-/Testdateien wurden zusätzlich geprüft. Der native Chip erschien während des Edits mit +1/−1; der geöffnete Diff zeigte die richtige Änderung. Zwei vom Modell selbst korrigierte Eingabefehler motivierten die oben genannten Verbesserungen. Dieser Funktionslauf qualifiziert keine Leistungssteigerung: [Prüfbeleg](../artifacts/coding-efficiency/portable-regression-review/live-verification.json).

## Aktueller Funktionsprüfstand und historische Vergleiche

Der Live-Runner akzeptiert ausschließlich `working-state`: Arbeitsstand aktiv und maximale Denkleistung. Ohne Aufgaben- und Wiederholungsfilter laufen zwölf Aufgaben mit je drei Wiederholungen. Historische Vergleichsauswertungen bleiben lesbar; neue Referenz-/Adaptive-Läufe sind gesperrt und es gibt keine automatische Profilaktivierung.

```powershell
$env:GO_AI_CODING_BENCHMARK_LIVE = '1'
dotnet run --project tools/GoAi.CodingBenchmarks -- --python 'C:\Python314\python.exe' --model 'coding/EXAKTE-LOKALE-MODELL-ID' --output 'C:\GO-Benchmark-Artefakte\arbeitsstand-001' --variants working-state --tasks 03-clamp --repetitions 1
```

Der isolierte Harness hat standardmäßig 900 Sekunden pro Lauf und eine Grenze von 512 lokalen Werkzeugaufrufen. Das sind Versuchsgrenzen, keine produktiven Coding-Grenzen. Die kleinen Python-Fixtures belegen keine allgemeine Qualität für große Repositories oder mehrtägige Arbeit. Details, Unterbrechungsverhalten und historische Messungen stehen im [Benchmark-README](../tools/GoAi.CodingBenchmarks/README.md).
