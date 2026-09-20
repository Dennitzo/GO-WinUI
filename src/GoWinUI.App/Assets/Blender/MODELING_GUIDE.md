# In kleinen Etappen zu einem belastbaren 3D-Modell

Lies Auftrag, `design.json` und betroffene Skripte. Wähle genau **eine nächste
Etappe**. Baue, prüfe und korrigiere sie vor abhängigen Details. Einfache Prompts
erlauben eigene Designentscheidungen; ausdrücklich genannte Maße, Merkmale und
spätere Nutzeränderungen haben Vorrang.

## 1. Aus dem Wunsch einen prüfbaren Entwurf machen

Schreibe einen kurzen Designbrief mit vier getrennten Listen:

- **Anforderungen:** Anzahl, Funktion, Maße, Anordnung und sichtbare Hauptmerkmale
  als einzeln prüfbare Aussagen.
- **Referenzen:** Tatsächlich erkannte Merkmale mit Quelle und gegebenenfalls Seite.
  Abgelesene Maße von Schätzungen trennen.
- **Entscheidungen:** Selbst gewählte Proportionen, Formen und Materialien mit
  kurzer Begründung durch Funktion, Lesbarkeit oder Stil.
- **Offene Punkte:** Verdecktes, Unlesbares, Widersprüche. Für sinnvollen Fortschritt
  eine ausdrücklich bezeichnete Annahme verwenden.

Beispiel: „Forschungsstation mit Gewächshaus“ wird zu Hauptmodul, Gewächshaus,
Verbindungsgang, Zugang und Energieversorgung. Wähle Arbeitsmaße, etwa Hauptmodul
8 × 5 × 3 m, als Entwurfsentscheidung. „Vier Stützen unter den Ecken“ ist prüfbar;
„professionelle Details“ nicht. Analysiere Bildanhänge und lies Dokumentseiten
tatsächlich mit den angebotenen Werkzeugen. Verdeckte Innenkonstruktion ist eine
eigene Gestaltung, kein belegtes Bildmerkmal.

Bei gewünschter Webrecherche nutze `web.search` mit `profile=images` für konkrete
Vorder-, Seiten-, Rück- und Detailansichten. Die lokale SearXNG-Suche liefert die
Quellseite als `url` und die Bildadresse als `thumbnailUrl`. Prüfe die Quellseite
mit `web.fetch`. Lade geeignete tatsächlich gefundene Bildadressen über
`coding.command` in freie Referenzdateien im Workspace; anschließend
`image.input file` und `media.analyze` mit der zurückgegebenen Upload-ID verwenden.
Pfadregel für Downloads: `coding.command` läuft standardmäßig im Workspace-Root;
gib Zielpfade relativ zum Workspace an (etwa `projekt/references/front.png`, der
Scaffold legt `references/` an) und ein `workingDirectory` nur, wenn der Befehl
es zwingend braucht. Ein Befehl mit `workingDirectory=projekt` **und** dem Pfad
`projekt/references/…` erzeugt verschachtelte Ordner, die `image.input` nicht findet.
Prüfe nach dem Download mit `coding.list`, dass die Datei am erwarteten Pfad liegt.
Ein Suchtreffer oder Seitentext ersetzt keine Sichtprüfung. Dokumentiere Quelle,
Bildadresse, lokalen Pfad, Blickwinkel und sichtbare Merkmale. Scheitert ein
Bildabruf, verwende eine andere gefundene Quelle und halte verbleibende Lücken fest.

Bestimme den Referenztyp vor dem Ableiten von Koordinaten: Lageplan, Seitenansicht,
Perspektive oder Detail. In einem Lageplan sind Bildabstände X/Y-Abstände auf dem
Boden, keine Höhen. Zwei dort übereinander gezeichnete Paneele stehen an zwei
Y-Positionen; daraus folgt kein Stapel in Z. Übernimm ausdrücklich angegebene
Achsen und Legenden aus dem Brief. Eine widersprechende Vision-Beschreibung erst
mit Dokument und Bild abgleichen, nicht ungeprüft in Geometrie übersetzen.

## 2. Koordinaten, Maße und Hauptformen zuerst

Verwende Z nach oben, X für Breite, Y für Tiefe/Länge. Vorderseite ist negatives Y:
`front` schaut von dort, `right` von positivem X, `top` von oben mit +X rechts und
+Y oben im Bild. Ursprung ist etwa
die Grundflächenmitte bei Z=0. Setze metrische Einheiten und `scale_length = 1.0`
in der ersten Etappe. Erhalte die Einheiten bestehender Szenen. Berichtsgrenzen
sind Blender-Einheiten; `units.scaleLength` rechnet Längen in Meter um.

Alle `size`-Werte des Helfers sind **volle Abmessungen**, Positionen die Objektmitte,
Rotationen im Bogenmaß. Ein Quader mit Höhe h auf Z=0 erhält Z=h/2, auf einer
Sockeloberkante s entsprechend Z=s+h/2. Leite Anschlüsse aus gemeinsamen Parametern
ab. Bei gedrehten Teilen prüfe Weltkoordinaten und evaluierte Bounds.

Blockout: Hauptvolumen, Raum für Öffnungen und Baugruppenpositionen. Noch keine
Schrauben oder Kabel. Prüfe Silhouette, Breite/Tiefe/Höhe, Abstände und Zugang
in Perspektive sowie Vorder- und Seitenansicht.

## 3. Bauteile so ordnen, dass Änderungen klein bleiben

Eine Collection pro Baugruppe, etwa `Structure`, `Cabin`, `Glazing`, `Access`.
Stabile Objektnamen verbinden Funktion und Position: `Structure_Post_FL`,
`Cabin_Wall_Right`. Vermeide unbeabsichtigte Duplikate mit `.001`-Namen.

`g.collection(name, parent=None)` findet/erzeugt eine Collection und erhält deren
Hierarchie; ein widersprechender Parent wird abgelehnt. Collections bewegen ihre
Objekte nicht automatisch gemeinsam. Verwende dafür bewusst Objekt-Parenting oder
gemeinsame Positionsparameter. `g.tag_component(obj, component, role=None)` ordnet
ein Objekt der Collection zu und entfernt seine anderen Collection-Zuordnungen.

Bei Wiederholungen erst **ein** korrektes Exemplar, dann eine kleine Reihe, danach
die volle Anzahl. Instanzen helfen bei identischen Modulen; prüfe auch deren
Sichtbarkeit. Gemeinsame Mesh-/Materialdaten ändern alle Nutzer dieser Daten.
Kopiere sie vor einer beabsichtigten Einzeländerung.

## 4. Jede Etappe ist ein kleines Änderungsskript

Beginne mit Scaffold und `steps/01_blockout.py`. `stage` lädt die `baseScene`,
führt dein Skript aus, speichert eine **neue** Revision, inspiziert sie und
aktualisiert Blender. Die erste Etappe beginnt leer. Folgeskripte ändern nur
die betreffende Baugruppe. Kein `clear_scene`, Laden/Speichern oder Rendern darin.

Höchstens 12.000 Zeichen pro Skript; meist genügen deutlich weniger. Kein kompletter
Generator in importierten Modulen als Umgehung. Wiederverwendbare Funktionen für
Einzelteile sind sinnvoll. Benenne Schritte etwa `02_structure.py`, `05_fix_joint.py`.

Übernimm die Scaffold-Importe in jedes neue Skript; frühere Skriptvariablen leben
nicht weiter. Dieses Fragment ergänzt danach nur zwei Teile:

```python
parts = g.collection("Structure")
finish = g.material("Structure_Finish", (0.28, 0.34, 0.42, 1), roughness=0.5)
g.box("Structure_Base", size=(4, 3, 0.2), location=(0, 0, 0.1),
      bevel=0.02, material=finish, collection=parts)
g.beam("Structure_Post_FL", start=(-1.8, -1.3, 0.2),
       end=(-1.8, -1.3, 2.6), width=0.14,
       material=finish, collection=parts)
```

Lies vor Dateiänderungen den aktuellen Hash. `stage` erhält Skripthash, neuen
`outputPath` und `label`, bei Fortsetzung auch `baseScene` und `baseSceneSha256`.
Den Szenenhash liefert `blender.execute` mit `operation: "info"` und Szenenpfad.
Erfinde keine Hashes. Prüfe `preview.state`: `blocked` kann ungespeicherte
Nutzeränderungen bedeuten; diese nicht verwerfen. Beachte neue Nutzeranweisungen.

## 5. Die passende Geometrie mit wenig Komplexität wählen

Verfügbare Bausteine liefern normale `bpy`-Objekte:

- `g.box(name, size=(1,1,1), location=(0,0,0), bevel=0.04, rotation=(0,0,0), material=None, collection=None)`
- `g.cylinder(name, radius=1, depth=2, location=(0,0,0), vertices=48, bevel=0.02, rotation=(0,0,0), material=None, collection=None)`; Achse zunächst Z.
- `g.sphere(name, radius=1, location=(0,0,0), scale=(1,1,1), material=None, collection=None)`
- `g.beam(name, start, end, width=0.1, depth=None, material=None, collection=None)`; rechteckiger Balken zwischen zwei Punkten.
- `g.tube_curve(name, points, radius=0.05, closed=False, material=None, collection=None)`; Rohr entlang einer **Polyline**, keine automatisch weiche Bézierkurve.
- `g.mesh(name, vertices, faces, material=None, collection=None)`; freie Geometrie mit gültigen Vertex-Indizes und konsistenter Flächenorientierung.

Für Architektur: Grundriss, Höhen, Wandstärken zuerst. Öffnungen mit Wandsegmenten
oder geprüftem Boolean herstellen; eine dunkle Platte ist kein Durchgang. Dach,
Rahmen und Stützen müssen zusammentreffen. `g.ground` ist Präsentationsumgebung
und vom Modellrahmen ausgeschlossen; verwende es nicht als erforderlichen Sockel.

Für Hard-Surface: Hauptkörper, Ausschnitte/Fugen, dann Halterungen und prägende
Details. Bevelradius passend zur kleinsten Dicke wählen: Box-Standard 0,04 m kann
zu groß sein, ausdrücklich kleiner oder 0 setzen. Glatte Normalen ändern Reflexe,
nicht die Silhouette. Runde Flächen glatt schattieren, harte Kanten erhalten.
Schattierungsfehler untersuchen, bevor du Segmente oder Subdivision erhöhst.

Beispiel Rad: Ein `g.cylinder` liegt zunächst mit seiner Achse entlang Z. Für eine
Radachse entlang X setze `rotation=(0, math.pi/2, 0)`; bei Boden Z=0 liegt die
Radmitte auf Z=Radius. Prüfe danach in Front- und Seitenansicht, ob die Scheibe
senkrecht steht, seitlich aus dem Fahrzeug ragt und den Boden berührt. Einfach
Z=Radius ohne passende Rotation ergibt ein flach liegendes, schwebendes Rad.

Für abstrahierte organische Formen: wenige Körper-/Gliedmaßenvolumen, dann Haltung
und Silhouette aus mehreren Richtungen. Rohre für Äste, Wurzeln oder Tentakel mit
bewusst geplanten Punkten. Überlappende Kugeln sind keine durchgehende Haut:
Verbindungen/Remesh/Retopologie als eigene Etappe bauen; Silhouette und dünne
Übergänge anschließend prüfen.

Figuren und Charaktere (Tiere, Maskottchen, Spielfiguren) brauchen eine
**durchgehende Silhouette** und **aufgesetzte, sichtbare Details**. Dafür gibt es
eigene Bausteine; zwei gestapelte Kugeln ergeben einen Schneemann, keinen Körper:

- `g.lathe(name, profile, segments=48, location=(0,0,0), smooth=True, material=None, collection=None)`
  erzeugt aus einem Profil `[(radius, z), ...]` von unten nach oben eine
  Rotationsfläche: Birnenkörper mit Kopf in einem Stück, Hals, Bauch und Scheitel
  aus einer Kurve. Beispiel: `[(0.0, 0.0), (0.16, 0.03), (0.19, 0.16), (0.15, 0.30), (0.17, 0.36), (0.16, 0.46), (0.0, 0.52)]`.
- `g.cone_between(name, start, end, start_radius, end_radius=0.0, ...)` baut Kegel
  und Kegelstümpfe **zwischen zwei Weltpunkten**. Ohren, Hörner, Stacheln und
  Schwanzansätze mit farbiger Spitze: einen Ansatz- und einen Spitzenpunkt wählen,
  mit `g.split_point(start, end, 0.7)` teilen und beide Segmente auf derselben
  Linie bauen. So bleiben Spitzen in jeder Ansicht fluchtend; keine geratenen
  Rotationen für getrennte Teilobjekte.
- `g.point_on_sphere(center, radius, azimuth_grad, elevation_grad)` liefert Punkt
  und Normale auf einem Kopf oder Rumpf; `g.surface_patch(name, point, normal, radius, thickness=0.01, scale=(1,1), sink=0.35, ...)`
  setzt darauf flache Wangen, Augenweiß, Flecken oder Rückenstreifen
  (`scale=(2.2, 0.5)` für längliche Streifen). Der Patch steht bewusst aus der
  Oberfläche heraus und kann nicht im Körper verschwinden.
- `g.flat_polygon(name, outline, thickness, plane="xz", ...)` extrudiert einen
  geschlossenen 2D-Umriss zu einer flachen Platte: Blitzschwanz als Zickzack
  `[(0,0),(0.06,0.05),(0.03,0.09),(0.10,0.15),(0.06,0.17),(0.14,0.28),(0.16,0.27),(0.11,0.16),(0.15,0.13),(0.07,0.06)]`
  in der XZ-Ebene, Flossen, Blätter, Kämme. Ein Rohr (`tube_curve`) ist für
  einen flachen Blitzschwanz die falsche Form.
- `g.mirror_x(obj, name)` spiegelt ein fertiges Bauteil an X=0 für Links/Rechts.

Arbeitsfolge für eine Figur: Referenzbilder analysieren und Proportionen als
Verhältnisse notieren (Kopfhöhe zu Gesamthöhe, Ohrlänge zu Kopfbreite,
Schwanzspannweite zu Körperhöhe). Zuerst Rumpf und Kopf als eine Lathe-Silhouette,
dann Gliedmaßen mit `cone_between`/`sphere`, danach Ohren, Schwanz und
Gesichtsdetails als Patches. Nach jeder Etappe Front, Seite und Perspektive gegen
die Referenz vergleichen (Abschnitt 8) und nur die benannten Abweichungen ändern.

## 6. Topologie, Modifier und Verbindungen kontrollieren

Keine gleichen Flächeneckpunkte, Nullflächen oder nicht endlichen Koordinaten.
Verwende konsistente Flächenorientierung. Kritische nicht ebene N-Gons gezielt
zerlegen; unbeabsichtigte lose Geometrie entfernen. Offene Ränder sind bei offenen
Flächen zulässig, bei geschlossenen Gehäusen zu erklären oder zu beheben.
`valid=true` beweist weder Wasserdichtigkeit noch Fertigungstauglichkeit.

Prüfe `issues`, Größen und Scale. Nicht uniforme Scale beeinflusst Modifier;
gezielt anwenden oder Maße in Meshdaten einbauen, wie `box` und `sphere` es tun.
Bei `bpy.ops` aktive Objekte, Auswahl und Modus beachten; direkte Datenänderungen
vermeiden oft versehentliche Änderungen an weiteren Teilen.

Booleans, Geometry Nodes und weitere Blender-Funktionen bleiben möglich. Prüfe die
**evaluierte** Ausgabe. Array, Bevel und Subdivision multiplizieren Aufwand: klein
starten, nur bei sichtbarem Bedarf erhöhen. Render-/Viewport-Einstellungen können
abweichen. Übersprungene Evaluation ist „nicht geprüft“: Baugruppe vereinfachen
oder Schritt zerlegen, nicht blind Grenzwerte erhöhen.

Pro Anschluss zwei Partner und eine Bedingung: Stütze endet an Dachunterkante;
Rohrende erreicht Flansch; Treppe erreicht Plattform. Gemeinsame Ebenen/Endpunkte
berechnen, Bilder auf Spalten, schwebende Teile und Durchdringungen prüfen.
Bounds-Überlappung beweist keinen Kontakt. Kleine bewusste Überlappungen nur für
passende visuelle Konstruktionen verwenden und entsprechend benennen.

Beispiel gerader Gang entlang X: Modul A endet bei `a_x + a_width/2`, Modul B
beginnt bei `b_x - b_width/2`. Die Ganglänge ist die Differenz dieser Anschluss-
ebenen, seine X-Mitte deren Mittelwert. Ist die Länge nicht positiv, liegt ein
Planungswiderspruch vor. Berechne Z aus derselben Boden-/Plattformhöhe. Der Name
„A_B_Connector“ beweist nicht, dass das Objekt tatsächlich beide Partner erreicht.

## 7. Materialien unterstützen die Form

Wenige Rollen: Hauptmaterial, Tragwerk, Akzent, Verglasung.
`g.material(name, color=(r,g,b,a), metallic=0.0, roughness=0.45)` erzeugt/aktualisiert
benannte Materialien mit linearen RGBA-Werten von 0 bis 1. Wichtige Nachbarteile
durch Kontrast trennen. Beleuchtung repariert keine falsche Form.

Alpha kleiner 1 ist keine vollständige Glaskonfiguration. Für echte Durchsicht
Principled-Shader und verfügbare Renderoptionen konfigurieren, Ergebnisbild prüfen.
Eine deckende getönte Fläche nur als bewusste stilisierte Darstellung verwenden.
Glas braucht lesbare Rahmen, begrenzte Reflexe und erkennbare Formen dahinter.
Ersetze einen massiven Blockout-Körper durch Wände, Rahmen und Verglasung, sobald
Durchsicht erforderlich ist. Eine transparente Außenhülle um einen unveränderten
massiven Innenblock bleibt optisch geschlossen.

## 8. Nach jeder Etappe Struktur UND drei Ansichten prüfen

1. Werte den Stage-Bericht aus: `success`, `valid`, `inspectionCompleted`, `issues`,
   `counts`, `bounds`. Eine gespeicherte ungültige Revision bleibt ein reparierbarer
   Zwischenstand. Lies bei gekürzter Antwort den vollständigen `reportPath`.
2. Rendere die gespeicherte Revision in einen neuen Ausgabeordner. Wähle mindestens
   `perspective`, `front`, `right`; bei Grundriss-/Dachfragen zusätzlich `top`.
   Beginne etwa mit 512–768 px. Mehr Samples ersetzen keine andere Blickrichtung.
3. Lade die tatsächlich erzeugten Bilder über `image.input` und untersuche sie mit
   `media.analyze`. Dateiexistenz, erfolgreicher Render oder Vorschau sind keine
   durchgeführte Vision-Prüfung. `stage` mit `views` rendert die neue Revision
   sofort in `<Revision>_renders/` und gibt die Bildpfade in `images` zurück; ein
   getrennter `render`-Aufruf ist dann nicht nötig.
4. Vergleiche **Referenz und Render im selben Aufruf**: Übergib das Renderbild als
   `uploadId` und die passenden Referenzbilder (gleiche oder ähnliche Ansicht) als
   `referenceUploadIds`. Vision beschreibt dann Bauteil für Bauteil Referenz,
   Render und Unterschied und formuliert jede Abweichung als Änderungsanweisung
   mit Richtung, Achse und Betrag relativ zu einem Bezugsmaß. Stelle zusätzlich
   konkrete Fragen: „Sind die Ohrspitzen in der Seitenansicht auf einer Linie mit
   dem Ohransatz?“, „Sind beide Wangen vollständig sichtbar?“, „Ist der Schwanz
   eine flache Zickzackplatte oder ein rundes Rohr?“, „Liegt die Tür frei?“
   Halte die Upload-IDs der Referenzen in `design.json` fest und lade sie bei Bedarf
   erneut über `image.input file`.
5. Erfasse pro Kriterium `erfüllt`, `verletzt` oder `nicht beurteilbar`, zusammen
   mit Revision, Bildpfad und beobachtetem Befund. Ein Kriterium ist erst erfüllt,
   wenn die Vision-Antwort für dieses Merkmal **keinen Unterschied und keinen
   Vorschlag** mehr nennt; „grob vorhanden“ ist verletzt. Begründe Sichtbares,
   erfinde keine exakten Maße aus perspektivischen Bildern. Maße kommen aus Szenendaten.
6. Leite aus der priorisierten Änderungsliste der Vision-Antwort die nächste kleine
   Etappe ab: Bauteil, Ursache im Skript (Position, Achse, Radius, Profilpunkt),
   konkrete Zahlenänderung, erwartetes Bild. Rendere erneut mit denselben Ansichten
   und vergleiche mit derselben Referenz, bis kein Unterschied mehr benannt wird.

Verdecktes ist nicht bestätigt: weitere Ansicht oder Detailprüfung wählen.
Geänderte Kriterien brauchen Bilder der **neuen** Revision. Vision kann irren;
Widersprüche mit Objektinformationen und weiteren Ansichten untersuchen.

## 9. Reparieren, fortsetzen und eine Etappe akzeptieren

Repariere den kleinsten belegten Defekt: Objekt, Ursache, Änderung und Prüfkriterium
benennen. Beispiel: „Stütze FL endet 0,15 m unter dem Dach; nur bis zur berechneten
Unterkante verlängern.“ Nicht zugleich Material, Kamera und Dachform ändern.
Nutze die fehlerhafte Revision als Reparaturbasis, erhalte gute Teile. Ein Rückgang
zu einer früheren akzeptierten Revision wird protokolliert; sie bleibt erhalten.

Nach zwei gleichen Reparaturen ohne Fortschritt vollständigen Befund erneut lesen,
Ursachenhypothese ändern oder Teil vereinfachen. Offenen Befund ehrlich benennen;
produktive Etappen bleiben möglich, der Fehler gilt nicht als behoben.

| Etappe | Eingabe | Änderung | Prüfung und Akzeptanz |
|---|---|---|---|
| Brief | Auftrag, Anhänge | Maße, Annahmen, Kriterien | Jede Pflichtfunktion hat ein prüfbares Kriterium. |
| Blockout | Brief | Hauptformen, Anordnung | Proportionen und Freiräume in drei Ansichten plausibel. |
| Baugruppe | Geprüfte Basis | Eine funktionale Gruppe | Anzahl, Maße, Position und Anschlüsse stimmen. |
| Verbindung | Nachbarbauteile | Fugen, Halter, Übergänge | Keine unbeabsichtigten Lücken oder schwebenden Teile. |
| Oberfläche | Stabile Formen | Bevel, Normalen, Materialien | Silhouette erhalten, Bauteile und Glas lesbar. |
| Reparatur | Konkreter Befund | Kleinste wirksame Korrektur | Derselbe Befund erneut geprüft; Nachbarteile erhalten. |
| Abschluss | Alle Revisionen/Kriterien | Letzte offene Pflichtpunkte | Aktuelle Struktur- und Bildnachweise vorhanden. |

Aktualisiere `design.json`: `requirements`, `decisions`, `references`, `stagePlan`,
`revisions`, `visualChecks`, `stageHistory`. `currentScene`/`currentStage` verweisen
auf den aktuellen Stand; halte Prüfstatus und offene Befunde in der Historie fest.
`acceptedFeatures` enthält nur bestätigte Merkmale. Neue ausdrückliche Wünsche
dürfen sie gezielt ändern. Vor Fortsetzung Manifest und aktuelle Anweisung lesen,
Priorität prüfen und passende Basisrevision mit echtem Hash wählen.

Abschluss: Anforderungen erfüllt oder offen benannt, aktuelle Szene gespeichert,
Bilder geprüft, Pfade verfügbar. Berichte Ergebnis, Prüfung und Einschränkungen.
Keine Kollisionsfreiheit, Statik oder Fertigungsqualität ohne gesonderten Nachweis.
