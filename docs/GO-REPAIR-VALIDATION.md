# GO-WinUI: Reparatur- und Prüfstand vom 20.09.2026

Der vollständige Windows-Build einschließlich Portable-Startprüfung und die reale
Cache-Wiederverwendung nach Stop sowie nach normalem Laufabschluss mit anschließendem
Neustart sind für General und Coding bestanden. Auch leere unterbrochene Modellrunden
und fehlerhafte Cache-Antworten sind durch Regressionstests abgesichert.
Dokumentwerkzeuge und Deep Research sind ebenfalls in beiden Modi live geprüft.
Der abschließende Build und die Portable-Startprüfung sind bestanden.

Repository: `C:\Users\AMD\Documents\GitHub\GO-WinUI`.
Die unten genannten relativen Pfade beziehen sich auf dieses Verzeichnis.
Der Bericht enthält keine Inhalte bestehender Benutzersitzungen.

## Änderungen im aktuellen Quellstand

| Bereich | Änderung und Prüfumfang |
|---|---|
| Bildvorschauen im Chat | Artefakte erhalten die stabile `StepId` ihrer Werkzeugaktion. Darstellung und Ereignis-Replay ordnen die Vorschau dieser Aktion zu; spätere Texte und Aktionen erscheinen dahinter. Der gemeinsame Pfad gilt für General und Coding, einschließlich erneut verbundener Streams. Historische Vorschauen ohne neue Metadaten behalten den vorhandenen kompatiblen Pfad. |
| Entfernte Subagent-Funktion | Keine ausführbaren Subagent-Werkzeuge, Agenten-Tabs oder Parallelitätseinstellungen. Die veraltete Client-Capability `document-agent` wurde entfernt. Dokumente verwenden `document.read` und `document.create` direkt im normalen Lauf. Historische Benutzerdaten und ihre Anzeigenamen sind keine aktive Delegation. |
| Workspace-Auswahl | Der Workspace-Button im Promptfenster wurde entfernt. Projekte und Sitzungen in der AI-Assistent-Sidebar bestimmen weiterhin den jeweiligen Workspace. |
| Native Projekte-Seite | Der native GO-Navigationseintrag, seine Seite, das ViewModel und ausschließlich dafür benötigte Ressourcen wurden entfernt. Die alte Route `projects` führt zum AI-Assistenten. Projektdaten, gemeinsame Projektdienste und Sidebar-Projekte bleiben erhalten. |
| Deep Research | Der zusätzliche Tool-Chip überträgt `RunRequest.DeepResearch`. General und Coding erreichen den vorgesehenen Recherchepfad; Coding behält seinen Modus, sein Modell und seinen Workspace. Abwahl oder ein fehlendes Flag lösen keinen expliziten Rechercheauftrag aus. |
| Stop und neuer Prompt | Vor Beginn der Modellinferenz wird der konsistente Sitzungskontext gesichert. Bei Unterbrechung bleibt zusätzlich der exakte native Fortsetzungsrest erhalten und wird über einen gesonderten Ereignispfad dem gespeicherten Turn zugeordnet. Die Fortsetzung verwendet die bereits gespeicherte Gesprächsfolge statt eines vollständig neu aufgebauten Prompts. Unbekannte oder abgebrochene Werkzeugausgänge werden nicht als Erfolg übernommen oder blind wiederholt. |
| Native Cache-Sicherung | Sitzungsidentität bleibt unabhängig von Clientprozess und Run-ID. Der native Ausführungspfad verwendet einen einzelnen Slot 0, der mit dem gespeicherten Slot übereinstimmt; nach Modellrunden werden Snapshots gesichert. Ein Slot ist keine wieder eingeführte Subagent-Parallelität. |
| GO schließen | Der native Modellprozess wird erst nach bestätigtem Gateway-Leerlauf beendet. Damit kann die Cache-Sicherung nach dem bereits bestätigten Stop noch fertig werden. Bleibt der Gateway bis zu 15 Sekunden beschäftigt oder ist sein Zustand unbekannt, lässt GO die gemeinsame Runtime aktiv. Andere Portable-Instanzen werden dadurch nicht während eines erkannten laufenden Auftrags beendet. |
| DeepSeek-Kontextgrenzen | Die Abschätzung des tatsächlich benötigten Kontextfensters berücksichtigt DeepSeek gesondert. Die normalisierte Modellvorlage erhält den historischen Promptpräfix beim nächsten Nutzerturn. Der reale Wiederherstellungstest nach Stop ist bestanden; der leere Fortsetzungsrest direkt nach Promptverarbeitung ist durch vier zusätzliche Testfälle abgesichert. |

## Abschließend nachgewiesene Prüfungen

Der vollständige Aufruf von `windows/build.ps1` in
`artifacts/validation/GO-WinUI-final-build.log` bestand mit folgendem Ergebnis:

| Prüfung | Ergebnis |
|---|---|
| Server | 673 Tests bestanden |
| Client | 679 Tests bestanden |
| Webdarstellung | 160 Tests bestanden |
| Native Python-Komponenten | 74 Tests bestanden |
| Portable | Veröffentlichung und Startprüfung bestanden; 42 eingebettete Webdateien sowie native Laufzeitdateien geprüft |

Belege:

- `artifacts/validation/GO-WinUI-final-build.log`
- `artifacts/validation/agent-context/summary.json`
- `artifacts/validation/agent-context/server.log`
- `artifacts/validation/agent-context/web-state.log`
- `artifacts/validation/agent-context/native-cache.log`
- `artifacts/validation/gateway-final-accepted-build.log`
- `artifacts/validation/gateway-final-accepted-deploy.log`

Der frühere vollständige Build mit 633 Server-, 660 Client-, 160 Web- und 61
nativen Tests bleibt unter `artifacts/validation/full-repair-build.log` als
historischer Zwischenstand erhalten.

Das dabei erzeugte Paket liegt unter
`artifacts/portable/win-x64/GO.exe`; Veröffentlichung und realer Start sind im
Buildprotokoll bestätigt. Das Gateway ist ebenfalls mit dem geprüften Quellstand neu gebaut und aktiviert.
Die genannte Startprüfung ersetzt keine interaktive Sichtprüfung sämtlicher Chatabläufe.

Die neuen Client-Regressionsprüfungen umfassen unter anderem:

- `DeepResearchClientIntegrationTests`: tatsächliche Kette von WebView-Anfrage über
  Coordinator und Service bis zur serialisierten Gateway-Anfrage; Auswahl, Abwahl
  und gewöhnlicher Prompt jeweils in General und Coding. Die HTTP-Testumgebung
  beendet die Anfrage vor Inferenz und beansprucht keine Live-Rechercheprüfung.
- `ClientRestartRequestTests`: echter SQLite-/DI-Neustart desselben Testprofils,
  erneute Sitzungsauswahl und identische Folgeanfrage für General und Coding nach
  abgeschlossenem, abgebrochenem oder unterbrochenem Verlauf. Eine neue Client-ID
  darf den Modellrequest nicht verändern. Dies prüft Requeststabilität, keine GPU-KV-Treffer.
- `NativeRuntimeShutdownDrainTests`: tatsächlicher Protokoll-Serializer, verzögerte
  Cache-Sicherung, belegter Leerlauf, unvollständige Statusantworten und begrenztes
  Warten beim Schließen; ein unbekannter Zustand darf die Runtime nicht beenden.
- `RemovedSubagentCapabilityTests`: gültige direkte Dokumentfähigkeiten bleiben
  vorhanden; entfernte Delegationsfähigkeiten werden nicht mehr angekündigt.
- `MediaArtifactAnchorIntegrationTests`, `tests/web/media-artifact-anchor.test.cjs`
  und Timeline-Tests: Zuordnung der Bildvorschau zur Werkzeugaktion, erneute
  Streamverbindung und Reihenfolge nach weiteren Ereignissen.

## Reale Dokumentprüfung: General und Coding bestanden

Die Dokumentprüfung wurde mit echten Agentenläufen über das gemeinsame Gateway
ausgeführt. Beide Modi erstellten, lasen, ergänzten und prüften das Dokument mit
den direkten Dokumentwerkzeugen. Der Test las die tatsächlich exportierte
`Bericht.docx` erneut ein; ein separater Dokumenten-Agent war nicht beteiligt.

- Laufzeit des gemeinsamen Testlaufs: 7 Minuten 37 Sekunden.
- `artifacts/validation/document-live.log`: Test bestanden.
- `artifacts/validation/document-live/General-document-GO6eab928bbc/acceptance.json`:
  `passed: true`; erfolgreiche `document.create`- und `document.read`-Aufrufe.
- `artifacts/validation/document-live/Coding-document-GOc111b7bb79/acceptance.json`:
  `passed: true`; erfolgreiche `document.create`, `document.read` und `documents.list`.
- Beide Fallordner enthalten `Bericht.docx`, `run.json` und `tools.json`.

## Reale Deep-Research-Prüfung

`artifacts/validation/deep-research-chip-verified.json` enthält die bestandene
General-Abnahme: echte SearXNG-Suche (`provider: searxng`, `isFallback: false`),
Abruf beider vorgegebenen Originaldokumentationen und zwei tatsächlich abgerufene
Quellenlinks in der fertigen Antwort. Die Kandidaten aus dem Nutzerauftrag werden
getrennt von der unveränderten SearXNG-Trefferliste geführt.

`artifacts/validation/deep-research-coding-complete.json` meldet `passed: true`
für Coding: drei SearXNG-Suchen, zwei erfolgreiche Originalquellen, sechs mit
Originaltext belegte Befunde und beide Quellenlinks in der Endantwort.
Die Vergleichstabellen für vorhandene Datei, Verzeichnis und fehlenden Pfad
wurden zusätzlich gelesen und stimmen in beiden Modi mit dem Prüfauftrag überein.
Der Test bewertet keine beliebigen zusätzlichen Aussagen oder Versionsbehauptungen
außerhalb dieses begrenzten Vergleichs.

Die vorherigen Coding-Proben bleiben als Diagnose erhalten: Einmal ignorierte
Modellauswahl führte zum falschen Ersatztreffer; anschließend lieferte eine zu
lange Suchphrase keine Fundstelle. GO priorisiert nun explizite Nutzerquellen und
wiederholt eine erfolglose Textsuche höchstens einmal mit kurzen API-Begriffen,
wenn das bestehende Werkzeugbudget dies erlaubt. Keine Quelle wird ohne echten
Abruf zu einem Beleg erklärt.

`deep-research-chip-live.json` wurde wegen irrelevanter Quellen und langem
Reasoning abgebrochen. Die vorherige Coding-Abnahme unter
`deep-research-coding-accepted.json` scheiterte korrekt an nur einer Belegquelle.
Diese Zwischenstände sind ausdrücklich keine Erfolgsbelege. Ein unmittelbar nach
Containerstart aufgezeichneter 502-Versuch enthält keinen gestarteten Modelllauf.

Wiederholbarer Aufruf (benutzt ausschließlich das gemeinsame lokale Gateway):

```powershell
python .\workers\coding\verify_deep_research_chip.py `
  --model 'coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0' `
  --mode both --reasoning none --timeout 900 `
  --output .\artifacts\validation\deep-research-chip-final.json
```

## Reale Cache-Prüfung nach Stop und Neustart: bestanden

`artifacts/validation/exact-stop-restart-restored.json` meldet `passed: true` und
`nativeRestartObserved: true`. Die Wiederherstellung wurde in einem separaten
Clientprozess nach einem tatsächlichen Neustart der nativen Supervisor- und
Modellserverprozesse geprüft. Das neu gebaute Gateway wurde vor dieser Prüfung
über den regulären Compose-Start aktiviert.

| Modus | Wiederverwendete Prompttokens | Gesamte Prompttokens |
|---|---:|---:|
| General | 4.327 | 4.574 |
| Coding | 9.884 | 10.316 |

Dies sind reale Modellmessungen aus jeweils isolierten Prüfsitzungen mit dem
ausgewählten DeepSeek-Vision-Modell. Die Probe startet keine Desktop-Oberfläche
und führt keine Dateiwerkzeuge aus. Sie belegt native Cache-Wiederverwendung;
die Client-Regressionstests ergänzen den SQLite-/Sitzungsauswahlpfad.

Belege:

- `artifacts/validation/exact-stop-restart-seed.json`
- `artifacts/validation/exact-stop-restart-seed.log`
- `artifacts/validation/exact-cache-restart-state.json`
- `artifacts/validation/exact-stop-restart-restored.json`
- `artifacts/validation/exact-stop-restart-restored.log`

### Normaler Laufabschluss und anschließender Neustart: bestanden

`artifacts/validation/exact-completed-restart-restored.json` meldet ebenfalls
`passed: true` und `nativeRestartObserved: true`. Nach normalem Laufabschluss,
anschließendem nativen Neustart und neuem Clientprozess wurden folgende Werte gemessen:

| Modus | Wiederverwendete Prompttokens | Gesamte Prompttokens |
|---|---:|---:|
| General | 4.711 | 4.898 |
| Coding | 10.352 | 10.724 |

Das zugehörige Protokoll ist
`artifacts/validation/exact-completed-restart-restored.log`.

### Historische Cache-Diagnose vor der Korrektur

`artifacts/validation/stop-session-cache.json` meldet `passed: true` für die
ausgeführte Stop-/Fortsetzungsprüfung. Der vorbereitende Neustartlauf in
`artifacts/validation/restart-cache-seed.json` meldet ebenfalls `passed: true`.

Die damals anschließende Wiederherstellung in
`artifacts/validation/restart-cache-restored.json` meldet dagegen `passed: false`.
Die native Sicherung wurde wiederhergestellt, der erwartete wiederverwendete
Promptpräfix blieb jedoch nicht erhalten. Eine erfolgreiche Slot-Wiederherstellung
allein belegt daher keine tatsächlich wiederverwendeten Tokens.

Die Diagnose führte zur DeepSeek-Jinja-Vorlage: Sie veränderte beim nächsten
Nutzerturn Teile des zuvor generierten Kontextpräfixes. Eine Normalisierung dieser
Vorlage und ein gesonderter Ereignispfad für unterbrochene Modellrunden wurden
anschließend ergänzt. Der oben dokumentierte reale Neustarttest mit exaktem
Fortsetzungsrest bestätigt die korrigierte Wiederverwendung. Die frühere
fehlgeschlagene Probe bleibt als historische Diagnose dokumentiert und ist kein
aktuelles Fehlschlagergebnis der korrigierten Variante.

Weitere Belege dieses Diagnoseabschnitts:

- `artifacts/validation/stop-session-cache.log`
- `artifacts/validation/restart-cache-state.json`
- `artifacts/validation/restart-cache-seed.log`
- `artifacts/validation/restart-cache-restored.log`

Der Client-Audit fand keine zufällige Neustartkennung im Modellrequest. Die
Sitzungs-ID stammt aus SQLite; Coding überträgt zusätzlich den gespeicherten
Workspace. Die wechselnde Transport-Client-ID gehört nicht zum nativen
Sitzungscache-Schlüssel. Einige angezeigte Kontextmesswerte sind dagegen nur
prozesslokal vorhanden: Eine nach Neustart geschätzte Anzeige ist von tatsächlich
erneut berechneten Prompttokens zu unterscheiden.

## Neue Läufe nach zwischenzeitlichem Sitzungswechsel: bestanden

`artifacts/validation/final-session-followups.json` meldet `passed: true`.
Nach den getrennten Rechercheläufen wurden die bisherigen General- und Coding-
Prüfsitzungen wieder ausgewählt. Jeweils ein neuer Lauf erinnerte die Kennung korrekt
und verwendete den gespeicherten Prompt wieder:

| Modus | Wiederverwendete Prompttokens | Gesamte Prompttokens |
|---|---:|---:|
| General | 4.992 | 5.179 |
| Coding | 10.761 | 11.133 |

Dieser Durchgang enthält keinen zusätzlichen nativen Neustart und wird nicht als
solcher ausgegeben. Er ergänzt die beiden zuvor gemessenen Neustartfälle.

## Wiederholbare Befehle

Alle Builds, Tests und Modellproben nacheinander ausführen. Die nachfolgenden
Modellproben verwenden dasselbe Gateway und dieselbe native Laufzeit wie GO.
Währenddessen dürfen keine anderen AI-Aufträge laufen. Sie erzeugen eigene
Prüfsitzungen; sie übernehmen keine privaten Chatverläufe.

Vollständiger Windows-Build einschließlich Pflichtprüfungen und Portable-Publish:

```powershell
Set-Location 'C:\Users\AMD\Documents\GitHub\GO-WinUI'
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

Das Gateway separat bauen und aktivieren, nachdem aktive Aufträge beendet sind:

```powershell
. .\windows\common.ps1
$repairStackPaths = Get-GoAiStackDefaults
Invoke-GoAiCompose -Paths $repairStackPaths -Arguments @('build', 'gateway')
if ($LASTEXITCODE -ne 0) { throw 'Gateway-Build fehlgeschlagen.' }
Invoke-GoAiCompose -Paths $repairStackPaths -Arguments @('up', '-d', '--no-deps', 'gateway')
```

Diese Befehle fordern keinen expliziten `--pull` an. Ein Windows-Publish allein
aktualisiert keinen laufenden Gateway-Container. Änderungen an der nativen Vorlage
werden erst nach Aktualisierung der Laufzeitdateien und Neustart wirksam.

Stop/Fortsetzung und neuer Lauf mit dem ausgewählten DeepSeek-Modell:

```powershell
$repairVisionModel = 'coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0'
python .\workers\coding\verify_stop_session_cache.py `
  --model $repairVisionModel --mode both `
  --output .\artifacts\validation\stop-session-cache-final.json
```

Prüfung mit tatsächlichem Neustart zwischen Vorbereitung und Fortsetzung:

```powershell
python .\workers\coding\verify_stop_session_cache.py `
  --model $repairVisionModel --mode both --stage seed-restart `
  --state .\artifacts\validation\restart-cache-final-state.json `
  --output .\artifacts\validation\restart-cache-final-seed.json

powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\manage-coding-llama.ps1 -Action Stop
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\manage-coding-llama.ps1 -Action Start

python .\workers\coding\verify_stop_session_cache.py `
  --model $repairVisionModel --mode both --stage verify-restart --require-native-restart `
  --state .\artifacts\validation\restart-cache-final-state.json `
  --output .\artifacts\validation\restart-cache-final-restored.json
```

Für den zusätzlichen Fall „während Generierung stoppen, anschließend Neustart“
beim `seed-restart`-Aufruf `--restart-after-stop` ergänzen und eigene neue
State-/Reportdateien verwenden. Die Probe prüft einen wirklichen Prozesswechsel;
bereits vorhandene Belege nicht mit nachfolgenden Versuchen überschreiben.

## Grenzen und Einordnung

- Cache-Wiederverwendung verlangt denselben Modell-, Binary-, Vorlagen- und
  Konfigurationsstand sowie einen passenden Gesprächspräfix. Die korrigierte
  Slot-/Vorlagenkonfiguration macht ältere Snapshots ungültig. Nach diesem Update
  muss eine bisherige Benutzersitzung deshalb einmal neu ausgewertet werden.
- Ein Modellwechsel verwendet nur einen zu diesem Modell passenden Snapshot;
  Cachezustände verschiedener Modelle sind nicht austauschbar. Eine geänderte oder
  gekürzte Historie darf keinen unpassenden Cache erzwingen.
- Bei unvollständigem Toolprotokoll, nicht sicher dekodierbarem Rest oder einem
  internen Tool-JSON-Reparaturversuch kann eine sichere Neuberechnung erforderlich
  sein. Unbekannte Werkzeugausgänge werden nicht zu erfolgreichen Aufrufen erklärt.
- Die Neustartprüfung kombiniert echte native Prozesswechsel und getrennte
  Clientprozesse mit SQLite-/DI-Neustarttests des Windows-Clients. Sie ersetzt
  keinen vollständigen manuellen Chat-UI-Neustarttest. Die Portable-Startprüfung
  und DOM-/SSE-/Artefakt-Replaytests sind separat bestanden.
- Die Recherche-Funktionsprüfung verwendet dasselbe DeepSeek-Modell mit explizitem
  `--reasoning none`; die Cache- und Dokumentläufe verwendeten das unterstützte
  Standardreasoning. Benutzereinstellungen wurden nicht geändert.
- SearXNG-Engines können externe 429-/Blockierungsfehler liefern. Die Recherche
  darf diese nicht als gelungene Abrufe darstellen oder unbemerkt einen anderen
  Suchanbieter verwenden. Original-URLs werden erst nach echtem Abruf zu Belegen.

Messwerte wie `cachedPromptTokens`, tatsächlich neu evaluierte Prompttokens und
Prozesswechsel sind die Abnahmekriterien. Eine schnellere Antwort, ein geladenes
Snapshot oder bestandene Test-Doubles allein reichen dafür nicht.
