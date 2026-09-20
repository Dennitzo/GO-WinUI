# Blender-Modul: Übergabe an die lokale AI

> Weiterentwicklung vom 20.09.2026: Der aktuelle Vertrag mit Projektbaukasten,
> gespeicherten Designentscheidungen, Szenendiagnose, mehreren Renderansichten
> und der komplexen Mehrturn-Abnahme steht in
> [BLENDER-COMPLEX-WORKFLOW.md](BLENDER-COMPLEX-WORKFLOW.md).
> Die folgenden Ergebnisse beschreiben den früheren Stand vom 19.09.2026.

Stand: 19.09.2026. Repository/Workspace: `C:\Users\AMD\Documents\GitHub\GO-WinUI`.
Die Implementierung ist vorhanden und der General-Blender-Modelltest ist bestanden.
Weitere Modelltests wurden auf Nutzerwunsch beendet bzw. nicht mehr gestartet.
Bestehende Änderungen gehören zu diesem Arbeitsauftrag; nicht zurücksetzen.
Der abschließende Portable-Build liegt unter
`artifacts/portable/workspace-tools-win-x64/GO.exe`. Er wird separat veröffentlicht,
damit eine bereits geöffnete GO-Anwendung nicht beendet werden muss.

## Direkt verwendbarer Weiterarbeits-Prompt

```text
Arbeite im Workspace C:\Users\AMD\Documents\GitHub\GO-WinUI am vorhandenen
Blender-Werkzeug weiter. Lies zuerst AGENTS.md, soweit vorhanden,
docs/BLENDER-TOOL-HANDOFF.md und docs/WORKSPACE-TOOLS-VALIDATION.md sowie die dort
genannten Implementierungsdateien. Nutze die vorhandene Anbindung; führe keine
zweite konkurrierende Blender-Integration ein.

Beginne mit der noch offenen echten Coding-Abnahme für Blender. Die ausführbare
Blender-Anwendung ist bereits installiert. Erstelle über die angebotenen
Werkzeuge eine Szene, speichere scene.blend und render.png im Test-Workspace,
analysiere das Renderbild tatsächlich mit image.input und media.analyze und
öffne die fertige Szene mit blender.execute open. Arbeite anschließend an einer
gezielten Änderung der gespeicherten Szene und prüfe sie erneut. Als Vorlage
kannst du examples/blender/scene_setup.py lesen und in den Test-Workspace kopieren.
Die Vorlage wurde bereits erfolgreich ausgeführt und visuell geprüft.

Behebe dabei konkret nachgewiesene Fehler. Behalte die Freigabe in General und
Coding sowie die vorhandenen Konfliktprüfungen bei.
Szenen, Skripte und Render gehören ausschließlich in den gewählten Workspace.
Vor run den aktuellen Skript-SHA-256 lesen; keine Hashes erfinden. Nutze echte
Werkzeugergebnisse als Belege. Ein gestarteter Prozess oder ein Bild-Upload ist
noch kein erfolgreiches Render bzw. keine Bildanalyse.

Führe Builds und Tests nacheinander aus. Verändere keine vorhandenen
Benutzersitzungen und beende keine fremden Modellläufe. Dokumentiere bestandene,
fehlgeschlagene und nicht ausgeführte Prüfungen getrennt. Antworte auf Deutsch.
```

## Einstiegspunkte im Code

| Datei | Zuständigkeit |
|---|---|
| `src/GoAi.Contracts/WorkspaceTools.cs` | Namen, erlaubte Operationen, Argumente, Hash- und Zeitlimitprüfung |
| `src/GoAi.Server.Core/Runs/AgentToolCatalog.Workspace.cs` | Modellseitige Beschreibung und vollständiges JSON-Schema |
| `src/GoWinUI.App/Services/WorkspaceToolService.cs` | Installationssuche, Skriptausführung, sichtbares Öffnen, Bildaufnahme/-Upload |
| `src/GoWinUI.App/Services/LocalToolBroker.cs` | Lokale Ausführung und Risikoklasse |
| `src/GoWinUI.Core/Coding/WorkspaceFilePath.cs` | Relative Workspace-Pfade und Reparse-Point-Prüfung |
| `src/GoWinUI.App/Services/GoAiAssistantService.cs` | Werkzeugfreigaben für General und Coding |
| `src/GoWinUI.App/Assets/Web/index.html` / `app.js` | Sichtbarer Blender-Menüeintrag und Beschriftung |
| `tests/GoWinUI.Tests/WorkspaceToolsLiveTests.cs` | Echte Modellabnahmen mit separater Testdatenbank |
| `tests/GoAi.Server.Tests/WorkspaceToolAvailabilityTests.cs` | Auswahl/Validierung in beiden Modi |

Alle relativen Pfade beziehen sich auf den oben genannten Repository-Workspace.
Das Blender-Laufzeitmodul befindet sich derzeit gemeinsam mit den Bild-/Startwerkzeugen
in `WorkspaceToolService.cs`; eine spätere Aufteilung kann dessen Vertrag beibehalten.

## Werkzeugvertrag und Arbeitsfolge

1. `blender.execute {"operation":"info"}` prüft die Installation.
2. Mit `coding.write`/`coding.edit` ein `bpy`-Skript im Workspace erstellen.
3. Mit `coding.read` den aktuellen SHA-256 des Skripts ermitteln.
4. `blender.execute {"operation":"run","path":"scene_setup.py","expectedSha256":"<64 tatsächliche Hexzeichen>","timeoutSeconds":300}`.
5. `image.input {"operation":"file","path":"render.png"}` liefert eine `uploadId`.
6. `media.analyze` mit dieser ID und einer konkreten Prüffrage verwenden.
7. Bei Bedarf Skript/Szene ändern und Render erneut prüfen.
8. `blender.execute {"operation":"open","path":"scene.blend"}` öffnet die Szene.

Die Installation liegt hier unter
`C:\Program Files\Blender Foundation\Blender 5.2\blender.exe` (5.2.2 LTS).
`GO_BLENDER_EXECUTABLE` erlaubt einen expliziten anderen Installationspfad.
Die Hintergrundausführung verwendet `--background --factory-startup
--disable-autoexec --python-exit-code 1 --python <Skript>`; Prozessausgaben und
Fehler werden über den bestehenden Coding-Executor zurückgeliefert.
Das sichtbare Öffnen ist vom Ausgabekanal des aufrufenden Prozesses entkoppelt.

Das sichtbare Öffnen übernimmt der Hauptagent. Eine solche Arbeitskopie ist keine
Betriebssystem-Sandbox für beliebige
Python-Skripte. Bestehende Szenen deshalb nicht ungeprüft mit eingebetteten Skripten
starten; automatische Skriptausführung bleibt deaktiviert.

## Prüfstand und nächste sinnvolle Schritte

- **Bestanden, echter General-Lauf:** eigener Skriptentwurf, Blender-Ausführung,
  `scene.blend`, `render.png`, erfolgreiche Vision-Analyse des blauen Würfels auf
  grauem Boden und Öffnen in Blender. Lauf:
  `run-4f754291edbb4d15a2c2ed0a43b034ec`.
  Belege: `artifacts/validation/workspace-live/General-blender-GOd9da811c8f/acceptance.json`.
- **Bestanden, automatisiert:** Werkzeugauswahl für General/Coding, Schemaprüfung,
  Pfadgrenzen und Risikoklasse.
- **Noch offen:** echter Coding-Blender-Lauf; bestehende Szene gezielt bearbeiten
  und erneut rendern; deterministische
  Tests für fehlende Installation, Prozessfehler, Abbruch und abweichenden Skripthash.
- **Noch nicht erneut live geprüft:** die nach dem General-Lauf korrigierte
  Entkopplung sichtbarer GUI-Prozesse vom Test-Ausgabekanal. Das frühere Blender-Fenster
  hielt den bereits beendeten Testprozess offen; der folgende Coding-Test soll auch
  sicherstellen, dass der Test bei weiterhin geöffnetem Blender korrekt endet.

Für die nächste echte Abnahme bei gestarteter GO-Modelllaufzeit:

```powershell
powershell -NoProfile -File windows/test-workspace-tools.ps1 -Mode Coding -Scenario blender
```

Der Test startet reale lokale Modell- und Blender-Prozesse, erhält Ausgabedateien
und verwendet keine bestehenden Chats. Das Zeitlimit pro Fall beträgt 45 Minuten,
da Wechsel zwischen großen Text-/Vision-Modellen hier mehrere Minuten benötigen.
Nicht gleichzeitig mit anderen GO-Builds oder Modellabnahmen starten.

Für die Regression anschließend:

```powershell
powershell -NoProfile -File windows/test-agent-context.ps1 -Configuration Release
dotnet build GO.slnx -c Release --nologo
```

Die vollständige Werkzeug-Prüfmatrix und die SearXNG-Reparatur sind in
`docs/WORKSPACE-TOOLS-VALIDATION.md` dokumentiert. Der dort aufgeführte visuelle
General-Test wurde nach 25 Minuten abgebrochen und gilt ausdrücklich nicht als
bestanden, obwohl erste Bildanalyse, Änderung und zweite Aufnahme erfolgt sind.

Als fachliche Referenz wurde die offizielle Beschreibung
[Architectural visualization with Astra](https://developers.openai.com/blog/architectural-visualization-with-astra)
gelesen: `bpy`, Hintergrundausführung und iterative Prüfung tatsächlicher Render.
