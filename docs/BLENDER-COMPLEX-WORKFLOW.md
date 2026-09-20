# Komplexe Blender-Modelle mit der lokalen AI

Der Blender-Menüeintrag startet den vorhandenen Coding-Ablauf im ausgewählten
Workspace und verwendet das eingestellte Coding-Modell. Folgeprompts bleiben in
derselben Coding-Sitzung. Der vollständige Werkzeugkatalog einschließlich
Web-/Bildsuche, Dokumentzugriff, Workspace-Dateien und Vision wird dem Modell in
jeder Runde angeboten. Bildanhänge bleiben Uploads; Dokumentanhänge werden mit
IDs und Metadaten angegeben und bei Bedarf über Dokumentwerkzeuge gelesen.
Die zugrunde liegende Blender-Capability bleibt auch für General verfügbar.
Es verbindet kleine `bpy`-Etappenskripte mit einem lokalen Baukasten,
versionierten Szenen, sichtbarer Vorschau, strukturellen Berichten und echter Bildanalyse. Ein kurzer
Prompt darf die Gestaltung offenlassen: Die AI trifft dann eigene Entscheidungen
über Proportionen, Details und Materialien und hält diese im Projekt fest.
Explizite Maße, Bildmerkmale und spätere Nutzeränderungen bleiben verbindlich.

Anlass der Integration war der reale Lauf
`run-89b0b6f3c0f54c93b5f14e6d27110421` vom 20.09.2026: Obwohl Webwerkzeuge erlaubt
waren, sah das General-Modell nach der Werkzeugauswahl nur das Blender-Schema
und meldete fälschlich fehlende Recherchewerkzeuge. Der Coding-Ablauf bietet alle
erlaubten Schemas direkt an, auch nach lokalen Werkzeugaufrufen. Dafür wird keine
zweite Agentenschleife eingeführt. Ein vorhandener Projekt-Workspace und ein
ausgewähltes Coding-Modell sind erforderlich; fehlt eines davon, zeigt GO einen
konkreten Fehler. Die sichtbaren Blender-Etappen, Umlenkung, Prüfschritte und der
Reasoning-Schleifenwächter gelten weiterhin.

Die gezielte opt-in-Regression `BlenderCodingResearchLiveTests` führt mit den
realen Coding-Einstellungen eine eigene Testsitzung aus: Blender initialisieren,
Pikachu-Bildreferenzen über SearXNG suchen und eine gefundene Quellseite prüfen.
Sie baut ausdrücklich kein Modell. Aktivierung:
`GO_BLENDER_CODING_RESEARCH_LIVE=1`; der Testfilter lautet
`FullyQualifiedName~BlenderCodingResearchLiveTests`.
`GO_BLENDER_CODING_RESEARCH_EVIDENCE` bestimmt den Belegordner,
`GO_BLENDER_CODING_RESEARCH_SETTINGS` optional die nur lesend verwendete
`settings.json`. Run-ID, Werkzeugereignisse, SearXNG-Anbieter/Fallback-Kennzeichen,
Quellen und Endantwort werden gespeichert. Der Lauf belegt Werkzeugzugriff und
Recherche im Coding-Modus; die komplexe Modellierungsabnahme bleibt separat.

Eine konkrete Modellierungsanleitung unterstützt die lokale AI in zwei Ebenen:
Die Methode steht direkt im General-/Coding-Systemprompt; jedes neue Projekt
enthält zusätzlich `MODELING_GUIDE.md`, dessen Pfad `scaffold` als `modelingGuidePath`
zurückgibt. Vor dem ersten Skript soll die AI diese vollständige Anleitung lesen.
Sie erklärt Maßplanung, Hauptformen, Bauteile, Verbindungen, Topologie, Materialien,
Prüfansichten und gezielte Reparaturen. `stagePlan` im Designbrief hält die geplanten
Etappenziele und Akzeptanzkriterien fest. Die Live-Abnahme prüft auch den tatsächlichen
Leseaufruf der Anleitung vor der ersten Modellierungsetappe.

## Werkzeugvertrag

Alle Pfade sind relativ zum Workspace. Unbekannte Felder und ungültige Werte
werden abgelehnt. `timeoutSeconds` begrenzt eine einzelne Operation, ist optional,
Standard 300, Bereich 1–3600. Es ist kein Gesamtzeitbudget für das Modellprojekt.

| Operation | Erforderlich | Wirkung |
|---|---|---|
| `info` | nichts | Installation prüfen; mit optionalem `path` aktuellen SHA-256 und Größe einer `.py`-/`.blend`-Datei lesen. Mit Workspace die dedizierte Vorschau automatisch öffnen. |
| `scaffold` | `path` | Neuen Projektordner mit `go_blender.py`, `steps/01_blockout.py`, `design.json`, `README.md`, `MODELING_GUIDE.md` anlegen und Vorschau öffnen; optional `brief` mit 1–8000 Zeichen. `scriptPath` ist die erste Etappe; `legacyScriptPath` verweist auf das ältere eigenständige `scene.py`. |
| `stage` | `path`, `expectedSha256`, `outputPath`, `label` | Kleines `.py`-Skript ausführen, neue `.blend` automatisch speichern, strukturell prüfen und automatisch sichtbar anzeigen. |
| `preview` | `path`, `expectedSha256` | Vorhandene `.blend` im dedizierten Blender-Vorschaufenster erneut anzeigen; optional `label`. |
| `run` | `path`, `expectedSha256` | Vorhandene Python-Skripte ausführen. Neue komplexe Modelle entstehen über `stage`. |
| `inspect` | `path`, `expectedSha256`, `outputDirectory` | Gespeicherte `.blend` strukturell prüfen und JSON-Bericht erzeugen. |
| `render` | wie `inspect` | Dieselbe Szene prüfen und mehrere Ansichten rendern. |
| `open` | `path` | Gespeicherte `.blend` im selben dedizierten Blender-Fenster öffnen. |

`render` akzeptiert zusätzlich `views` mit ein bis sechs unterschiedlichen Werten
aus `perspective`, `front`, `right`, `top`, `back`, `left`; Standard sind die ersten
vier. `resolution` ist die quadratische Bildkante, 128–2048 Pixel, Standard 768.
`samples` liegt bei 1–128, Standard 32. Zwischenprüfungen sollten kleine Bilder
verwenden; höhere Qualität folgt nach stabiler Geometrie.

`stage.path` muss eine `.py`-Datei mit höchstens **12.000 Zeichen** sein. Die
allgemeine Grenze von `coding.write` ändert diese Etappengrenze nicht. `label`
benennt den Schritt mit 1–200 Zeichen. `outputPath` muss eine neue `.blend` sein.
Die erste Etappe beginnt leer. Folgeschritte geben die geprüfte `baseScene` und
ihren `baseSceneSha256` gemeinsam an. Der vertrauenswürdige Wrapper lädt diese
Basis und speichert die neue Revision; das Änderungsskript lädt und speichert
keine Szene selbst und setzt das Projekt nicht vollständig zurück.

Die Vorschau ist Teil des normalen Ablaufs: Blender öffnet automatisch, und jede
erfolgreiche Etappe aktualisiert das dedizierte Fenster. `preview.state` macht
Anzeigeprobleme unterscheidbar von einer erfolgreich gespeicherten Datei.
Ungespeicherte manuelle Änderungen im Fenster blockieren den Szenenwechsel und
werden nicht verworfen. Speichere sie zuerst; soll die AI darauf aufbauen, gib ihr
die gespeicherte Datei über „Umlenken“ als neue Basis vor.
`preview` ist kein zusätzlicher Pflichtaufruf nach jedem Schritt; es zeigt bei
Bedarf eine bestehende Revision erneut. Unabhängige Blender-Projekte bleiben
unberührt. Kleine Etappen bieten Grenzen, an denen neue Nutzerhinweise den
weiteren Aufbau umlenken können.

`scaffold.path`, `stage.outputPath` und `inspect/render.outputDirectory` müssen neu sein. Vorhandene
Verzeichnisse werden nicht überschrieben. `inspect` und `render` ändern die
Quelldatei nicht. Der bestätigte Dateistand wird vor Prozessstart geprüft und
während der Ausführung gegen Ersetzen gesperrt. Eingebettete Blender-Skripte
werden beim Laden nicht automatisch ausgeführt. Der Workspace ist dennoch keine
Betriebssystem-Sandbox für frei verfassten Python-Code.

## Arbeitsfolge und Fortsetzungen

1. `blender.execute {"operation":"info"}` und vorhandene Projektdateien prüfen.
   Neue Projekte beispielsweise mit `{"operation":"scaffold","path":"station","brief":"Modulare Marsstation mit Gewächshaus und Rover"}` beginnen.
2. Zuerst `modelingGuidePath` vollständig lesen und die Modellierungsmethode anwenden,
   danach die anderen zurückgegebenen Einstiegspfade lesen. Bildanhänge anhand ihrer wirklichen
   `uploadId` mit `media.analyze` analysieren; lokale Bilder zuvor mit `image.input`
   laden. Dokumentanhänge mit `document.read` beziehungsweise
   `documents.search/readPages` lesen. Quellen, erkennbare Merkmale und Annahmen
   getrennt festhalten; unlesbare Maße oder verdeckte Rückseiten nicht erfinden.
3. `design.json` fortschreiben: `requirements`, `decisions`, `references`,
   `acceptedFeatures`, `revisions`, `visualChecks`, `stageHistory`, `currentStage` und `currentScene`
   erhalten Designbrief, Quellen, Etappenfolge, aktuelle Revision und überprüfbare Kriterien.
4. `steps/01_blockout.py` enthält ausschließlich Hauptformen und Anordnung. `coding.read`
   liefert den aktuellen Skripthash für `stage`; `outputPath` benennt die neue Szene,
   `label` etwa „01 Hauptformen“. Der Wrapper speichert, prüft und zeigt die Etappe.
   Strukturbericht und `preview.state` auswerten. Noch keine späteren Details in
   dasselbe große Skript packen oder den Komplettaufbau in ein importiertes Modul verlagern.
5. Mit `info` und dem Szenenpfad den binären Dateihash lesen. Beispiel:
   `{"operation":"info","path":"station/scene_v001.blend"}`.
   Für `inspect` und `render` diesen zurückgegebenen Wert als `expectedSha256`
   übernehmen und jeweils einen neuen Ausgabeordner wählen. Keine Hashes erfinden;
   `coding.read` ist kein Leser für binäre `.blend`-Dateien.
6. Nach **jeder** Etappe `valid`, `issues`, `counts`, `objects` und Bildpfade auswerten. `reportPath`
   enthält den vollständigen Bericht; die Werkzeugantwort begrenzt Objekt- und
   Befundlisten. Mindestens Gesamtansicht und geeignete orthogonale Ansichten
   prüfen. Das tatsächliche Bild mit `image.input file` laden und anschließend
   mit `media.analyze` gegen konkrete Kriterien untersuchen.
7. Pro etappenbezogenem Kriterium „erfüllt“, „verletzt“ oder „nicht beurteilbar“ mit
   sichtbarem Befund erfassen. Nach bestätigten Hauptformen folgt `02_baugruppen.py`
   auf der vorherigen Szene; weitere Baugruppen, Verbindungen, Details und Materialien
   erhalten eigene kleine Skripte. Fehler ebenso gezielt mit einer neuen `stage`
   korrigieren und dieselben Kriterien erneut prüfen, bevor abhängige Details folgen.
   Noch ungebaute spätere Details bleiben offene Anforderungen. Ein verdecktes
   bereits benötigtes Bauteil ist kein bestandenes Kriterium.
   Bei zweimal unveränderter Fehlersignatur ohne Fortschritt den offenen Befund
   benennen; weitere fachliche Etappen bleiben möglich.
8. Neue Hinweise zwischen Etappen und Folgeprompts beginnen beim vorhandenen
   Manifest und der aktuellen Szene. `stage` erhält `baseScene` und deren echten
   Hash; das Skript ändert nur die betroffenen Komponenten. Kein eigenes
   `bpy.ops.wm.open_mainfile`, `save_as_mainfile`, `save_revision` oder vollständiges
   Leeren der bestehenden Szene im Etappenskript. Bereits akzeptierte Merkmale und
   ältere Revisionen erhalten. Neue Wünsche ergänzen den Designbrief.

`stageHistory` protokolliert Ziel, Skript, Basis-/Ausgabeszene, Prüfberichte,
Renderbilder, akzeptierte Befunde und offene Punkte. Bei weiterem fachlichem
Bedarf folgen weitere Etappen. Qualität hat Vorrang vor Zeit; die Begrenzung
identischer erfolgloser Reparaturen ist kein Gesamtlimit für produktive Schritte.

Der Baukasten umfasst Collections, Materialien, Quader, Zylinder, Kugeln,
gerichtete Balken, Rohre und frei definierte Meshes sowie Kamera-/Lichthilfen.
Er liefert gewöhnliche `bpy`-Objekte; andere Blender-Funktionen bleiben nutzbar.
Er ist weder ein fertiger Generator für jede Modellklasse noch ein Ersatz für
fachliche Entwurfsentscheidungen. Es gelten Meter, Z nach oben, Rotation im
Bogenmaß und vollständige Bauteilabmessungen. `save_revision` verweigert das
Überschreiben vorhandener Szenen, ist aber nur für eigenständige ältere Skripte
gedacht; `stage` verwaltet das Speichern selbst.

General erhält bei tatsächlich angebotenem Blender-Werkzeug und Workspace
standardmäßig bis zu 96 Werkzeugaufrufe und 192 Modellrunden, einschließlich der
gesonderten Werkzeugauswahl. Größere konfigurierte Grenzen bleiben erhalten.
General ohne diese Werkzeug-/Workspace-Kombination behält seine bisherigen
Budgets; Coding behält seine eigene Konfiguration.

## Kontext gezielt klein halten

Die Modellierungsmethode und der vollständige `MODELING_GUIDE.md` bleiben
verbindlich. Wiederholte Laufzeitdetails werden dagegen gezielt reduziert:

- `info` liefert einen kurzen Einstiegshinweis beziehungsweise einen Hinweis zum
  Dateihash. Im abgebrochenen Livefall enthielt allein `info.instruction` noch
  **10.422 Zeichen**. Der neue Quellstand enthält **326 Zeichen** ohne Dateipfad
  beziehungsweise **213 Zeichen** mit Dateipfad; siehe
  [BlenderToolService](../src/GoWinUI.App/Services/BlenderToolService.cs).
- Guide und README dokumentieren die öffentliche Helper-API. Bei unklaren
  Parametern wird die betreffende Funktion gezielt gelesen. Die komplette
  Runtime muss nicht vor jedem Projekt in den Modellkontext gelangen. Im
  Livefall lieferte das vollständige `coding.read` von `go_blender.py` allein
  **22.558 Zeichen `content`** einschließlich Leseformatierung. Der Guide wurde
  ebenfalls vollständig gelesen: 211 Zeilen, `truncated=false`, `nextLine=null`.
  Die ursprünglichen Antworten stehen unverändert in
  [tools-1.json](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/tools-1.json).
- Auch in General begrenzt der Server große **Blender-Werkzeugantworten** vor der
  nächsten Modellanfrage auf **12.000 serialisierte Zeichen**. Das ist eine
  Kontextgrenze, unabhängig von der gleich großen Etappenskriptgrenze. Erhaltene
  Pfade, Hashes, Prüfstatus, Bildmetadaten und Befunde werden unverändert kopiert;
  ausgelassene Daten sind als gekürzt markiert. Vollständige Berichte bleiben
  unter `reportPath` und ursprüngliche Werkzeugbelege im Journal erhalten.
  Die Umsetzung steht in [CodingLoopGuard](../src/GoAi.Server.Core/Coding/CodingLoopGuard.cs)
  und [RunProcessor](../src/GoAi.Server.Core/Runs/RunProcessor.cs). Der gezielte
  Regressionstest `GeneralBoundsOnlyBlenderRenderBeforeTheNextNativeRequestAndPreservesTheOriginalReceipt`
  prüft außerdem, dass andere General-Werkzeugantworten unverändert bleiben;
  siehe [CodingWorkingStateIntegrationTests](../tests/GoAi.Server.Tests/CodingWorkingStateIntegrationTests.cs).

Diese Änderungen kürzen keine Anforderungen und ersetzen keine Bildprüfung.
Ein Geschwindigkeitsgewinn des neuen Gesamtstands ist durch den abgebrochenen
Livefall nicht gemessen.

Die ausdrücklich gewählte Blender-Aktion mit Workspace sendet `TimeoutSeconds=0`:
kein Gesamtzeitlimit für produktive Etappen. Normale General-Anfragen behalten
ihre bisherige Frist. Einzelne Werkzeugzeitlimits und der gemeinsame
Reasoning-Schleifenwächter bleiben wirksam; standardmäßig darf eine Modellrunde
nicht 30 Minuten am Stück ausschließlich Denktext ohne neuen Antwort- oder
Werkzeugfortschritt produzieren.

## Prüfung und Aussagegrenzen

Verfügbarkeit bedeutet nur, dass eine Blender-Installation gefunden wurde.
Prozess- und Vertragstests belegen technische Abläufe. Geometrieberichte prüfen
beispielsweise endliche Koordinaten, Topologie, Abmessungen, Materialien und
Sichtbarkeit; Warnungen müssen zum Modellzweck passen. Offene Flächen sind nicht
bei jedem visuellen Modell Fehler. Evaluierte Geometrie unterliegt Speicher- und
Komplexitätsgrenzen; übersprungene Prüfungen sind keine bestandenen Prüfungen.

Ein erfolgreiches Render und `valid=true` belegen weder eine gute Gestaltung noch
die vollständige Erfüllung des Nutzerauftrags. Dafür müssen tatsächliche Ansichten
analysiert und mit den Anforderungen verglichen werden. Vision-Befunde bleiben
beurteilbare Modellantworten. Kollisionsfreiheit, Wandstärken, Fertigungstoleranzen,
Statik und Fertigungstauglichkeit benötigen eigene fachliche Prüfverfahren.

## Nachgewiesener Zwischenstand am 20.09.2026

Die komplexe lokale AI-Abnahme ist **nicht bestanden**. Der Nutzer verlangte,
die sichtbaren Korrekturen abzuschließen, den Lauf und die Fenster zu beenden,
anschließend zu bauen und dann aufzuhören. Der folgende Livefall wurde deshalb
kontrolliert beendet; er wird nicht weiter als laufende Abnahme geführt.

`run-ea095220bb9e405abe10d7dfb42732e6` lief am 20.09.2026 von **10:44:25 bis
11:26:30 Europe/Berlin**, rund **42 Minuten**. Das abschließende
[acceptance.json](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/acceptance.json)
enthält `passed=false` und `run.cancelled` mit Grund `client`. Die ausdrückliche
Nutzeranweisung und die Sicherung der Vorschau stehen in
[user-requested-stop.json](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/user-requested-stop.json).
Der spätere [Stopstatus](../artifacts/validation/blender-final-stop-state.json)
bestätigt den beendeten Lauf ohne aktive AI-Aufträge. Das abschließende
[Buildmanifest](../artifacts/validation/blender-final-build-manifest.json)
bestätigt zusätzlich, dass die eigenen Testfenster geschlossen wurden.

Belegt sind DOCX-Lesen, tatsächliche Vision-Analyse des Lageplans, das vollständige
Lesen der Modellierungsanleitung, ein Designplan und eine sichtbare kleine Etappe
mit **4.735 Skriptzeichen, 23 Meshes und acht Materialien**. Ihre strukturelle
Prüfung war gültig. Vier echte Ansichten wurden gerendert. Die zusätzliche
Weather-Anweisung wurde im selben Lauf angenommen und angewandt;
[steering-1.json](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/steering-1.json)
enthält beide Ereignisse. Das belegt die Umlenkung, noch kein gebautes Weather-Bauteil.

Vor dem Abbruch wurde außerdem das Perspektiv-Renderbild erfolgreich hochgeladen.
Eine abgeschlossene Vision-Analyse dieses Renderbildes liegt **nicht** vor; nur
die Referenzbildanalyse ist abgeschlossen. Weitere geprüfte Etappen, die fertige
Station und der zweite Prompt mit Reparatur und Geometrieerhaltung wurden nicht
abgenommen. Alle Werkzeuge und Ereignisse stehen in den ursprünglichen
[Laufbelegen](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/acceptance.json).

Der Lauf zeigte außerdem einen Fehler des Renderwerkzeugs: Die alte `top`-Kamera
drehte die Draufsicht um 180 Grad, sodass +X links und +Y unten lagen. Der Fix ist
numerisch mit Blender belegt: [vorher](../artifacts/validation/top-camera-probe-20260920/before.json)
und [nachher](../artifacts/validation/top-camera-probe-20260920/after.json) zeigen
jetzt +X rechts und +Y oben. Diese Probe hat `rendered=false` und belegt die
Kameraprojektion, keine neue autonome Bildanalyse. Ein
[zusätzlicher technischer Umlenkungshinweis](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/camera-orientation-steering.json)
informierte den aktiven Lauf über den Fehler, ohne Geometrieänderungen vorzugeben.

Die anschließend separat fertiggestellte Reparatur unter
`artifacts/validation/blender-visible-repair-20260920` stammt von **Codex**.
[Provenienz und Abschlussprüfung](../artifacts/validation/blender-visible-repair-20260920/provenance.json)
und [Reparaturprüfungen](../artifacts/validation/blender-visible-repair-20260920/repair_checks.json)
kennzeichnen sie ausdrücklich mit `autonomousLocalAiAcceptance=false`. Ihre
Szenen und Render dürfen keine fehlende autonome AI-Abnahme ersetzen. Die
Reparatur enthält 65 Meshes und zwölf Materialien; Struktur- und Renderprüfung
sind gültig und melden keine Issues. Vier Ansichten wurden mit 1024 × 1024 Pixeln
gerendert und die sichtbaren Korrekturen separat durch Codex beurteilt. Die
Ausgangskopie bleibt laut SHA-256 unverändert. Das ist eine gezielte Korrektur
des Blockouts, keine fertig abgenommene komplexe Station.

Für den abschließenden Quellstand sind inzwischen gesondert belegt:

| Prüfung | Aktueller Nachweis |
|---|---|
| Solution-Build | Erfolgreich, null Warnungen und null Fehler; [blender-final-build-02.log](../artifacts/validation/blender-final-build-02.log). |
| Server-Regression | 785 von 785 bestanden; [blender-final-server-tests.log](../artifacts/validation/blender-final-server-tests.log). |
| Client-Abschlussprüfung | 725 von 725 bestanden; [blender-final-client-tests.log](../artifacts/validation/blender-final-client-tests.log). Live-Opt-in war ausgeschaltet: keine neue AI-Inferenz und keine neue Blender-GUI-Abnahme. |
| Neues Portable-Paket | `artifacts/portable/blender-final-win-x64/GO.exe` veröffentlicht; Startprüfung und alle 42 WebView-Dateien bestanden; [blender-final-portable.log](../artifacts/validation/blender-final-portable.log). |
| Gateway-Image | Abschlussimage erfolgreich gebaut; [blender-final-gateway-build.log](../artifacts/validation/blender-final-gateway-build.log). Auf Nutzerwunsch kein anschließender Containerneustart und kein neuer Modelllauf. |

Das [abschließende Manifest](../artifacts/validation/blender-final-build-manifest.json)
unterscheidet gebautes Image `sha256:6203605e16a279295ce398333f5b6c01ea7e430639560edd9665f877ddcafef4`
und weiterhin laufendes Image `sha256:83407d586780462b925b502fd3c924bdec0ecc7c886b202ec2a386e2de247140`.
`deployedFinalImage=false`: Der laufende Gateway bleibt auf dem zuvor
bereitgestellten Stand mit Reasoning-Wächter. Die letzten Änderungen des neuen
Images sind damit gebaut, aber nicht als laufender Gateway bereitgestellt.

Die folgenden Testzahlen und Paketangaben sind der **historisch bestandene Stand
vor den letzten Kamera- und Kontextänderungen**. Sie bestätigen nicht automatisch
den abschließenden Quellstand. Sie bleiben mit ihren ursprünglichen Protokollen
erhalten, insbesondere die damals aktivierten echten Blender-Laufzeitprüfungen.

| Prüfung | Nachweis und Ergebnis |
|---|---|
| Server-Regression | 780 Tests bestanden, einschließlich großer Blender-Berichte im Coding-Kontext; [Protokoll](../artifacts/validation/blender-guided-server-final.log). |
| Client-Regression | 725 Tests bestanden, einschließlich aktivierter echter Blender-Abnahmen; [Protokoll](../artifacts/validation/blender-guided-client.log). |
| Web-Regression | 166 Tests bestanden; [Protokoll](../artifacts/validation/blender-stages-final-web.log). |
| Python-Smoke 08 | Geometrie, Render, Etappen, Reparaturen und Schutz der Vorschau bestanden; [validation.json](../artifacts/validation/blender-toolkit-check-08/validation.json). |
| .NET mit echtem Blender | Laufzeitprüfung bestanden; [runtime-acceptance.json](../artifacts/validation/blender-guided-runtime/runtime-fde3c4d909ff4f96b86e7ec109e573a8/runtime-acceptance.json). |
| Sichtbare Etappen | Dasselbe echte Blender-Fenster, unveränderte Basisrevisionen, Fehler- und Vorschau-Wiederaufnahme bestanden; [staging-acceptance.json](../artifacts/validation/blender-guided-runtime/staging-4cf62f08b0e9440998365083e274d2e8/staging-acceptance.json). |
| Solution-Build | Ohne Warnungen/Fehler; [Protokoll](../artifacts/validation/blender-guided-build.log). |
| Portable | Veröffentlicht und Startprüfung samt 42 WebView-Dateien bestanden; [Protokoll](../artifacts/validation/blender-guided-portable.log). Paket: `artifacts/portable/blender-staged-win-x64/GO.exe`. |
| Laufender Gateway | Neues Image bereitgestellt, Status `ready`, Reasoning-Wächter auf 30 Minuten; [Deploymentbeleg](../artifacts/validation/blender-guided-deployment.json). |
| Komplexe lokale AI mit Anhängen und Folgeprompt | Der oben bezeichnete geführte Livefall wurde auf Nutzerwunsch nach rund 42 Minuten beendet; `passed=false`. Eine Stage und vier Render sind belegt, weitere Render-Vision, Etappen und Folgeprompt bleiben ungeprüft. |

Frühere fehlgeschlagene oder kontrolliert abgebrochene Fälle bleiben mit ihren
ursprünglichen Belegen erhalten. Sie werden durch spätere erfolgreiche Prüfungen
nicht nachträglich als bestanden umgedeutet.

Die echte .NET-Abnahme bestätigt unter anderem native Ausführung, Metadaten,
mehrere Renderbilder, Fehlerberichte, Zeitlimit und Abbruch. Sie verwendet einen
festen Testentwurf und belegt keine autonome Entwurfsqualität der lokalen AI.

## Prüfungen wiederholen

Alle folgenden Aufrufe sind Prüfanleitungen, keine Erfolgsmeldungen. Builds,
Tests und lokale Modellläufe nacheinander ausführen. Für aktuelle Ergebnisse sind
die gespeicherten Logs, Berichte und `acceptance.json` des konkreten Laufs maßgeblich.

```powershell
dotnet test tests/GoAi.Server.Tests/GoAi.Server.Tests.csproj -c Release --filter 'FullyQualifiedName~Blender'
dotnet test tests/GoWinUI.Tests/GoWinUI.Tests.csproj -c Release --filter 'FullyQualifiedName~BlenderToolServiceTests'
node --test tests/web/coding-timeline.test.cjs
```

Der Python-Smoke-Test verwendet echtes Blender und einen deterministischen,
komplexen Rover. Er prüft den Baukasten, mehrere PNG-Ansichten, bekannte
Geometriedefekte, Dateigrenzen und Erhalt der Quelle. Er verwendet keine lokale
Text-/Vision-AI und belegt deshalb keine Modellqualität. Ein neuer Ausgabeordner
ist pro Lauf erforderlich; den Installationspfad gegebenenfalls anpassen.

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' `
  --background --factory-startup --disable-autoexec --python-exit-code 1 `
  --python examples/blender/validate_toolkit.py -- `
  --output artifacts/blender-toolkit-check-01
```

Die opt-in-Abnahme `BlenderComplexLiveTests` verwendet den echten lokalen
Modellserver, Client-Broker, DOCX-Import, Bild-Upload und Blender in einer eigenen
Testsitzung. Der erste Prompt verlangt eine Marsstation aus Dokument und Lageplan.
Ein zweiter Prompt ergänzt Bauteile und repariert einen absichtlich in einer
separaten Szenenkopie eingebauten Sichtbarkeitsfehler des Rovers. Dieser Testfehler
wird nicht der AI zugeschrieben. Beide Versionen, echte Vision-Aufrufe und der
Erhalt bestehender Geometrie werden als getrennte Belege gespeichert.
Nach der ersten gespeicherten Etappe fordert der Harness über die echte
Steering-API zusätzlich einen Weather-Sensor an. Er verlangt sowohl
`run.steering.accepted`/`applied` als auch dessen tatsächliche Geometrie im
gespeicherten Endmodell. Er fordert mindestens drei geprüfte Etappen im ersten
und zwei im zweiten Prompt. Eine technische Vierstundenfrist begrenzt den
Testprozess; die Modellanfrage selbst verwendet `TimeoutSeconds=0`.

```powershell
$previousOptIn = $env:GO_BLENDER_COMPLEX_LIVE
$previousEvidence = $env:GO_BLENDER_LIVE_EVIDENCE
try {
  $env:GO_BLENDER_COMPLEX_LIVE = '1'
  $env:GO_BLENDER_LIVE_EVIDENCE = Join-Path $PWD 'artifacts/validation/blender-complex'
  dotnet test tests/GoWinUI.Tests/GoWinUI.Tests.csproj -c Release `
    --filter 'FullyQualifiedName~BlenderComplexLiveTests' --logger 'console;verbosity=normal'
} finally {
  $env:GO_BLENDER_COMPLEX_LIVE = $previousOptIn
  $env:GO_BLENDER_LIVE_EVIDENCE = $previousEvidence
}
```

Optional setzen `GO_AI_SERVER_URL`, `GO_AI_BLENDER_MODEL` und
`GO_BLENDER_EXECUTABLE` Gateway, Modell-ID und Blender-Pfad. Client und laufender
Server müssen die neuen Operationen und Budgets enthalten. Ohne Opt-in kehrt der
Test vor jeglicher Inferenz zurück; ein grüner Testlauf ohne Opt-in ist keine
Live-Abnahme. Ein bestandenes Live-Szenario belegt dieses konkrete Beispiel,
keine allgemeine Zuverlässigkeit für beliebige komplexe Modelle.

## Blender in Coding: Prüfung und Bereitstellung am 20.09.2026, 12:10 Uhr

Der Blender-Menüeintrag verwendet jetzt Coding einschließlich Modellwahl,
Werkzeugkatalog, Anhängen, Fortschritt, Dateiänderungen und Folgeprompts.
787 Servertests, 729 Clienttests und 170 Webtests bestanden; der Solution-Build
war ohne Warnungen und Fehler. Der echte Coding-Recherchelauf
`run-59366764b99540dab5443dc7b7e69e7c` schloss nach 3:55 Minuten ab: Blender
initialisiert, zwölf Bildtreffer über SearXNG ohne Fallback, zugehörige Quelle
gelesen. Das ursprüngliche Testprotokoll meldet einen Fixture-Fehler, weil die
interne leere Preview-Szene als Modellbau gezählt wurde. Die korrigierte getrennte
[Prüfung der unveränderten Belege](../artifacts/validation/blender-coding-research/20260920-100112-19800666/posthoc-acceptance.json)
bestand 25 Kriterien. Dieser Recherchelauf enthält weder Modellbau noch Vision-Abnahme.

Der reale Blender-Etappentest mit zwei Revisionen, Render, absichtlichem Fehler
und Wiederherstellung der Vorschau bestand. Der störende Quick-Setup-Dialog wird
nur in der GO-Vorschauinstanz unterdrückt; die sichtbare Oberfläche wurde geprüft.
Portable wurde neu gebaut und mit Startprüfung sowie allen 42 WebView-Dateien
geprüft. Der laufende Gateway entspricht dem neuen Image und meldet healthy/ready.
Testfenster sind geschlossen, keine AI-Läufe aktiv. Aktueller
[Bereitstellungsbeleg](../artifacts/validation/blender-coding-release.json).

## Nachfolgende Bereitstellung am 20.09.2026, 11:38 Uhr

Auf die anschließende Nutzeranweisung wurde der aktuelle Portable-Client erneut
nach `artifacts/portable/win-x64/GO.exe` gebaut; Startprüfung und alle 42 WebView-Dateien
bestanden. Der Gateway wurde neu gebaut und diesmal tatsächlich bereitgestellt.
Container und gebautes Image stimmen überein; Docker meldet `healthy`, der
Bereitschaftsendpunkt `ready`, die Modellruntime ist erreichbar und der
Reasoning-Zeitwächter steht auf 30 Minuten. Andere Container und native
Modellprozesse wurden nicht neu gestartet. Es wurde kein weiterer AI-Test begonnen.
Der [Bereitstellungsbeleg](../artifacts/validation/portable-gateway-renewed-20260920.json)
ersetzt die frühere Aussage „finales Image noch nicht aktiviert“ für den aktuellen
Betriebszustand; die dokumentierten Grenzen der autonomen Modellabnahme bleiben bestehen.
