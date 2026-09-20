# DeepSeek: integrierte Vision für Medien- und Blender-Werkzeuge

## Verhalten

Ein ausgewähltes DeepSeek-Modell mit passendem lokalem Vision-Projektor erhält
den Projektor bereits in seinem normalen `coding/...`-Preset. Der Modellkatalog
meldet dafür `supportsVision: true`, ohne die General-/Coding-Rollen zu entfernen.
Bild- und Videoanalyse verwenden diese genaue Instanz; eine Umwandlung in eine
andere `vision/...`-ID oder das Laden eines Qwen-Vision-Modells findet nicht statt.
Das gilt auch für die Bildanalyse von Blender-Rendern.

Die Modellwahl wird an allen Einstiegspunkten weitergereicht:

- Medien-Schaltflächen: `MediaJobRequest.PreferredModelId` aus der App-Auswahl.
- General/Coding: tatsächlich ausgewählte Modellinstanz des laufenden Agenten.
- Video mit Transkript: integriertes Modell auch für die abschließende Zusammenfassung.

Bei einem reinen Textmodell ohne Vision-Fähigkeit bleibt die bisherige separate
Vision-/General-Pipeline erhalten. Die Auswahl basiert auf Katalogfähigkeiten,
nicht allein auf dem Wort „Vision“ im Dateinamen. Ohne erreichbaren Katalog wird
ein Fehler gemeldet, statt stillschweigend ein anderes Modell zu wählen.

Native Preset-Erweiterung: nur DeepSeek-Architekturen mit tatsächlich gefundenem
Projektor. Andere Text-Presets bleiben unverändert. Beim erstmaligen Upgrade muss
die native Laufzeit neu starten, damit DeepSeek den Projektor mitlädt; danach
verwenden Text- und Bildaufrufe dieselbe Modellinstanz.

## Code und Regression

- `workers/coding/catalog.py`: Projektor im DeepSeek-Textpreset, Capability-Tag
  und Projektor-Gerätezuordnung.
- `ModelRuntimeClient.Vision.cs`: Auswahl ausschließlich des ausgewählten
  Modells, wenn dessen Katalogeintrag Vision-Unterstützung meldet.
- `AgentToolExecutor.cs`: Bild-/Videoanalyse und Fusion mit ausgewähltem Modell.
- `RunProcessor.cs`: Modellweitergabe aus Agentenläufen.
- `Research.cs`, `GatewayEndpoints.cs`, `GoAiAssistantService.cs`: direkter Medienpfad.
- `IntegratedVisionTests.cs`, `workers/coding/test_catalog.py`: integrierte Vision,
  exakte IDs und unveränderter Textmodell-Fallback.

## Reproduzierbarer Praxistest

```powershell
python workers/coding/verify_integrated_vision.py `
  --model 'coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0' `
  --image artifacts/validation/workspace-live/General-blender-GOd9da811c8f/render.png `
  --video artifacts/validation/deepseek-vision/render.mp4 `
  --agent-tools `
  --output artifacts/validation/deepseek-vision/acceptance-with-agents.json
```

Der Test erfordert die gestartete lokale Laufzeit und führt nur DeepSeek aus.
Er prüft Blender-Renderbild, wiederholte Bildanalyse, einen kurzen stummen Clip
sowie echte Medien-Werkzeugaufrufe aus General und Coding. Er verlangt richtige
Farb-/Formerkennung, die exakte DeepSeek-ID im Werkzeugergebnis und ausschließlich
DeepSeek unter den geladenen Modellen. Fehlerhafte eigene Läufe werden abgebrochen.
Der Clip prüft Video-Frames, nicht Spracherkennung oder Audio-/Video-Fusion.

Build: `artifacts/portable/deepseek-vision-win-x64/GO.exe`.
Die alten Fallback-Modelle werden in den Regressionstests simuliert und für diese
Abnahme ausdrücklich nicht live geladen.

## Blender-Rückkopplung mit Vision

Für Blender-Renderings läuft der vollständige Ablauf über das ausgewählte DeepSeek-Modell:
Auftrag verstehen, bpy-Skript im Workspace erstellen, Szene mit Objekten, Materialien, Licht und
Kamera einrichten, rendern, das tatsächliche Renderbild mit image.input und media.analyze prüfen,
konkrete Abweichungen erkennen, Szene korrigieren, erneut rendern und prüfen. Automatische
Korrekturschleifen sind auf höchstens zwei begrenzt. Vorhandene Projekte und .blend-Dateien werden
nicht ungefragt überschrieben. Eine erfolgreiche Skriptausführung allein gilt nicht als visuelle
Prüfung; die Analyse erhält das tatsächliche Renderbild. Fehlendes Blender, ungültige Skripte,
Prozessfehler, Zeitüberschreitungen, Abbruch, fehlende Renderbilder und Fehler der Bildanalyse werden
verständlich gemeldet. Das Werkzeugergebnis protokolliert visionModelId und modelId.

Der Praxistest vom 19.09.2026 mit blauem Würfel, grauer Bodenfläche und roter Kugel wurde real
ausgeführt: Ein absichtlich fehlerhaftes erstes Rendering platzierte die Kugel außerhalb des
Sichtfelds; die Vision-Analyse erkannte die fehlende Kugel und benannte die Abweichung. Die
korrigierte Szene zeigte beide Objekte vollständig und nicht abgeschnitten. Beleg:
`artifacts/validation/blender-feedback/TESTBERICHT.md`.

## Historisches Ergebnis vom 19.09.2026

Die folgenden Belege stammen vom genannten Datum. Nachfolgende Änderungen,
einschließlich Entfernung der Subagent- und Parallelmodell-Infrastruktur, benötigen
eine eigene Prüfung; die damaligen Testzahlen sind kein Nachweis des aktuellen Builds.

Alle fünf genannten Praxistests bestanden, einschließlich korrekt erkanntem
blauen Würfel und grauem Boden. Beleg:
`artifacts/validation/deepseek-vision/acceptance-with-agents.json` (`passed: true`).
Der native DeepSeek-Prozess blieb über die wiederholten Direktaufrufe und beide
Agentenläufe identisch (PID 38500, Startzeit unverändert); kein Qwen-Modell wurde
geladen. Die Prozessaufnahmen liegen im selben Belegordner.

Regression: 663 Server-, 668 Client- und 57 native Python-Tests bestanden.
Release-Build ohne Warnungen/Fehler; Portable-Startprüfung einschließlich aller
43 Web-Dateien bestanden. Der Gateway und die native Laufzeit wurden aktualisiert.
Für die Modellweitergabe aus den Medien-Schaltflächen ist der oben genannte neue
Client-Build zu starten; die alte GO-Anwendung vorher schließen.

## DeepSeek-Dateibearbeitung: DSML-Schemakompatibilität

Der native DeepSeek-Parser liest Werkzeugparameter aus dem Wurzelobjekt des
Schemas. Das `oneOf` von `coding.edit` ersetzt dieses Objekt im nativen Schema-AST;
`foreach_parameter` findet dann keine Parameter. Die generierte Grammatik erlaubt
dadurch nur leere Aufrufe (`{}`), auch wenn das Modell die richtigen Argumente kennt.
Dies wurde mit dem installierten DeepSeek-Vision-Modell vor und nach der Anpassung
reproduziert: `artifacts/validation/deepseek-edit-schema-live.json`.

`ModelRuntimeClient.ToolSchema.cs` entfernt ausschließlich für DeepSeek und
`coding.edit` das oberste `oneOf` aus dem übertragenen Schema. Eigenschaften,
Pflichtfelder, Typen und Hash-Format bleiben erhalten. Der ursprüngliche Katalog
und der lokale Executor prüfen weiterhin Einzel-/Batch-Exklusivität, aktuellen
Datei-Hash und eindeutige Fundstellen. Andere Modelle und Werkzeuge bleiben gleich.
Die Anpassung liegt im gemeinsamen Modellclient und gilt für General und Coding.

Regression: `DeepSeekToolSchemaTests` und `CodingBatchEditSchemaTests`.
Der echte Dateiablauf lässt sich gezielt prüfen (temporärer Test-Workspace):

```powershell
$env:GO_AI_CODING_FILE_LIVE = '1'
$env:GO_AI_LIVE_CODING_MODEL = 'coding/DeepSeek-V4-Flash-Vision-Exp-UD-IQ1_S~31b467a216d0'
dotnet test tests/GoWinUI.Tests/GoWinUI.Tests.csproj -c Release --filter 'FullyQualifiedName~LocalModelCreatesFileThenAppliesSeparateHashGuardedEditAndVerifiesContents' --logger 'console;verbosity=normal'
Remove-Item Env:GO_AI_CODING_FILE_LIVE
Remove-Item Env:GO_AI_LIVE_CODING_MODEL
```

Gateway und native Laufzeit müssen laufen; nach Änderungen am Modellclient muss
das Gateway-Image gebaut und der Gateway aktualisiert werden. Ein reiner Windows-
Client-Build ersetzt die laufende Gateway-Implementation nicht.

Die reale Datei-Abnahme mit DeepSeek Vision und aktiviertem Reasoning bestand
am 19.09.2026: Lauf `run-f469dfcb6af547a2a3b12aa4069a8882`, Dauer 2:59 Minuten.
Reihenfolge: Projekt auflisten, suchen, Auftragsnotiz lesen, Datei neu schreiben,
Datei samt Hash lesen, gezielt bearbeiten, Endinhalt erneut lesen. Der Test prüft
den tatsächlichen Dateiinhalt und dass die vorhandenen Dateien unverändert bleiben.
Beleg: `artifacts/validation/deepseek-edit/deepseek-file-live.trx`.
