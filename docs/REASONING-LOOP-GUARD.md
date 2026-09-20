# Schutz gegen endlose Reasoning-Schleifen

## Nachgewiesener Fehler

Im Lauf `run-a6c2a17b371245be889379046e339c01` vom 19./20.09.2026
lief das lokale DeepSeek-Modell im Coding-Modus mit aktiviertem Reasoning und
ohne Laufzeitlimit. Nach dem letzten Werkzeugschritt um 03:57 Uhr waren alle
fünf Planschritte als abgeschlossen markiert. Trotzdem folgten in Runde 133
bis 06:43 Uhr ausschließlich Denktext und Tokenfortschritt, ohne neue sichtbare
Antwort oder Werkzeuge. Der ganze Lauf endete erst durch einen Client-Abbruch
um 06:49 Uhr. Die Uhrzeiten beziehen sich auf Europe/Berlin.

Der letzte Reasoning-Abschnitt dauerte 2 Stunden 45 Minuten und umfasste
22.381 Ereignisse mit 212.661 Zeichen. Der Satz
„Eigentlich: Ich sollte den Bericht erstellen. Die Kernarbeit ist abgeschlossen.“
erschien 367-mal. Die Diagnose erfolgte lesend anhand der persistierten
Serverereignisse. Der vorhandene Inaktivitätswächter verlängerte seine Frist mit
jedem Fragment; Tokenproduktion allein konnte deshalb die Schleife unbegrenzt
aufrechterhalten.

## Verhalten

Der gemeinsame Wächter sitzt im Modell-Streamingpfad. Er gilt unabhängig vom
General- oder Coding-Modus sowie für Bildanalysen. Mehrfach fast unveränderte
längere Denkpassagen lösen einen kontrollierten Fehler aus. Kurze Wiederholungen,
einzelne Zitate und längere fortschreitende Überlegungen reichen dafür nicht aus.
Die Erkennung verarbeitet Wortfolgen unabhängig von den Fragmentgrenzen des
Providers und hält ihren Speicher begrenzt.

Zusätzlich begrenzt ein Zeitwächter einen ununterbrochenen Abschnitt ausschließlich
mit Denktext auf standardmäßig 30 Minuten. `GO_AI_REASONING_ONLY_TIMEOUT_MINUTES`
konfiguriert im Gateway einschließlich Docker einen Wert von 1 bis 1440 Minuten.
Neuer Antworttext oder tatsächlich fortschreitende Werkzeugargumente
beginnen einen neuen Abschnitt. Es handelt sich um eine Grenze innerhalb einer
Modellrunde, nicht um eine allgemeine Zeitgrenze für komplexe Aufgaben mit vielen
produktiven Schritten.

Der betroffene HTTP-Stream wird beendet; der native Modellprozess bleibt bestehen.
Der Lauf erhält einen verständlichen Fehlergrund, und bereits gespeicherte
Werkzeugergebnisse sowie Zwischenstände bleiben erhalten. Ein erkannter Loop
startet keine automatische identische Wiederholung. Eine spätere Fortsetzung ist
eine bewusste neue Anfrage. Das System meldet keine erfolgreiche Fertigstellung,
wenn nur der Denkprozess gestoppt wurde.

Bildanalysen verwenden denselben Streamingpfad und übernehmen unterstützte
Reasoning-Auswahlen des Laufs. Beim gleichen steuerbaren Modell bleibt insbesondere
`none` erhalten. Ein anderes Fallbackmodell erhält nur Stufen, die es tatsächlich
unterstützt: ohne abschaltbares Reasoning die niedrigste Stufe für `none`, sonst
seinen nativen Standard. Instruct-Modelle ohne Reasoning-Steuerung erhalten keine
erfundene Stufe. Die tatsächliche Stufe steht im Medienergebnis. Das gilt auch für
Transkriptanalyse und Video-/Audio-Zusammenführung.

## Aussagegrenze und Prüfung

Die Erkennung beweist keinen semantischen Fortschritt. Stark umformulierte
Wiederholungen können erst am Zeitwächter auffallen. Die Schutzschicht soll
beobachtete Endlosschleifen begrenzen, ohne jede längere Überlegung als Fehler
einzustufen. Das allgemeine Ausgabetokenbudget und die verfügbare Kontextlänge
werden dadurch nicht pauschal verkleinert.

Die Prüfungen umfassen Wiederholungen über Fragmentgrenzen, fortschreitenden
Denktext, sichtbaren Fortschritt, Zeitablauf auch bei blockiertem Stream,
Provider-Antworten mit SSE und JSON, Vision sowie die nicht wiederholbaren
Client-Fehlerpfade. Im Originalstream-Replay wurde die nächtliche Schleife nach
524,09 Sekunden (8 Minuten 44 Sekunden), 1706 Ereignissen und 2816 Wörtern erkannt.
Das ist ein Wiederabspielen real aufgezeichneter Ereignisse, keine neue Inferenz.
Quelle und Hash stehen im [Replaybericht](../artifacts/validation/reasoning-night-guard-replay.json).

Die abschließende Server-Regression bestand inzwischen mit **785 von 785 Tests**;
[Protokoll](../artifacts/validation/blender-final-server-tests.log). Der zugehörige
[Solution-Build](../artifacts/validation/blender-final-build-02.log) bestand ohne
Warnungen und Fehler. Die abschließende Client-Prüfung bestand mit **725 von
725 Tests** bei ausgeschaltetem Live-Opt-in; [Protokoll](../artifacts/validation/blender-final-client-tests.log).
Sie umfasst keine neue AI-Inferenz oder Blender-GUI-Abnahme. Die zuvor bestandenen **780 Server-, 725 Client-
und 166 Webtests** bleiben historische Ergebnisse vor den letzten Kamera- und
Kontextänderungen: [Server](../artifacts/validation/blender-guided-server-final.log),
[Client](../artifacts/validation/blender-guided-client.log),
[Web](../artifacts/validation/blender-stages-final-web.log). Die frühere
Portable-Startprüfung und der
[Gateway-Deploymentbeleg](../artifacts/validation/blender-guided-deployment.json)
gelten für ihren damaligen Stand. Das abschließende Paket
`artifacts/portable/blender-final-win-x64/GO.exe` ist veröffentlicht; seine
Startprüfung und alle 42 WebView-Dateien bestanden;
[Portable-Protokoll](../artifacts/validation/blender-final-portable.log).
Auch das [abschließende Gateway-Image](../artifacts/validation/blender-final-gateway-build.log)
ist erfolgreich gebaut. Gemäß Nutzerauftrag folgten kein Containerneustart und
kein neuer Modelllauf. Das [Buildmanifest](../artifacts/validation/blender-final-build-manifest.json)
verzeichnet `deployedFinalImage=false`: Image `6203605e…` wurde gebaut, während
Containerimage `83407d58…` mit dem bereits bereitgestellten Reasoning-Wächter
weiterläuft. Die eigenen GO-/Blender-Testfenster sind geschlossen; der
[Stopstatus](../artifacts/validation/blender-final-stop-state.json) enthält keine
aktiven AI-Aufträge.

## Kontrollierter Nutzerabbruch der Blender-Abnahme

Der spätere Blender-Lauf `run-ea095220bb9e405abe10d7dfb42732e6` wurde am
20.09.2026 um **11:26:30 Europe/Berlin** nach rund **42 Minuten** ausdrücklich auf
Nutzerwunsch beendet. Der Nutzer verlangte sichtbare Korrekturen, anschließend
Stop, geschlossene Fenster und Builds. Das ist **kein nachgewiesener Eingriff des
Reasoning-Wächters** und kein erfolgreich abgeschlossener Modellierungsauftrag.
[Nutzerabbruch](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/user-requested-stop.json)
und [acceptance.json](../artifacts/validation/blender-complex-staged-guided/20260920-084424-b3a6bd7f/acceptance.json)
dokumentieren `run.cancelled` und `passed=false`.

Belegt sind Dokument- und Referenzbildanalyse, vollständig gelesene Anleitung,
Designplan, eine kleine sichtbare Stage und vier Render. Weather-Steering wurde
angenommen und angewandt. Die Renderdatei wurde hochgeladen; eine abgeschlossene
Render-Vision, weitere geprüfte Etappen und der Folgeprompt fehlen. Der numerisch
belegte [Top-Kamera-Fix](../artifacts/validation/top-camera-probe-20260920/after.json)
und die [separate Codex-Reparatur](../artifacts/validation/blender-visible-repair-20260920/provenance.json)
werden deshalb nicht als autonome lokale AI-Abnahme gezählt.

Die anschließende Kontextreduktion ist von der Schleifenerkennung unabhängig:
kurze `info`-Antworten, öffentliche Helper-API statt vollständiger Runtime-Lektüre
und höchstens 12.000 Zeichen für große Blender-Belege im General-Modellkontext.
Ungekürzte Berichte bleiben erhalten; übernommene Pfade und Hashes bleiben exakt.
Messwerte, Quellen und Aussagegrenzen stehen in
[Blender: Kontext gezielt klein halten](BLENDER-COMPLEX-WORKFLOW.md#kontext-gezielt-klein-halten).
Diese Änderung belegt noch keine beschleunigte oder erfolgreich abgeschlossene
autonome Modellierung.

Nachfolgende Nutzeranweisung, 20.09.2026 um 11:38 Uhr: Portable erneut gebaut und
startgeprüft; den aktuellen Gateway diesmal tatsächlich neu bereitgestellt.
Image-Identität, Docker `healthy`, HTTP `ready`, erreichbare Modellruntime und
`GO_AI_REASONING_ONLY_TIMEOUT_MINUTES=30` sind im
[Bereitstellungsbeleg](../artifacts/validation/portable-gateway-renewed-20260920.json)
bestätigt. Damit ist auch die letzte Kontextoptimierung aktiv. Andere Dienste und
native Modellprozesse blieben bestehen; kein neuer AI-Modelllauf wurde gestartet.
