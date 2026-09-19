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
- Subagent: dessen eigene Instanz, einschließlich `~secondary`.
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

- `workers/coding/catalog.py`: Projektor im DeepSeek-Textpreset, Capability-Tag,
  Projektor-Gerätezuordnung auch bei parallelen Instanzen.
- `ModelRuntimeClient.Vision.cs`: exakte Auswahl und Wiederverwendung einer
  bereits geladenen `~main`-Instanz bei Medienaufrufen mit gespeicherter Basis-ID.
- `AgentToolExecutor.cs`: Bild-/Videoanalyse und Fusion mit ausgewähltem Modell.
- `RunProcessor.cs`, `CodingSubagentService.cs`: Modellweitergabe aus Agentenläufen.
- `Research.cs`, `GatewayEndpoints.cs`, `GoAiAssistantService.cs`: direkter Medienpfad.
- `IntegratedVisionTests.cs`, `workers/coding/test_catalog.py`: integrierte Vision,
  exakte Haupt-/Subagent-IDs und unveränderter Textmodell-Fallback.

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

## Ergebnis vom 19.09.2026

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
