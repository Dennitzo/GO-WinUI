# Historischer Prüfbericht: Projektleiste und Kontextwiederverwendung

**Einordnung vom 20.09.2026:** Die Subagent-Funktion wurde entfernt. Es gibt keine
Agenten-Tabs, delegierten Arbeitskopien oder Parallelmodell-Auswahl mehr. Die hier
dokumentierten Subagent- und Paralleltests sind ausschließlich historische Belege
des damaligen Builds; sie beschreiben keine aktuelle Fähigkeit und gelten nicht als
Abnahme des aktuellen Quellstands. Bestehende Sitzungen und historische Ergebnisse
bleiben Daten. Das aktuelle Pflichtgate verwendet die heute vorhandenen Tests.

## Historischer Stand vom 19.09.2026

Das abschließende Pflichtgate bestand mit 658 Client-, 645 Server-, 162 Web- und
56 Python-Prüfungen (1.521 insgesamt). Der Portable-Publish bestand einschließlich
Startprüfung, nativer Laufzeitdateien und aller 43 eingebetteten Webdateien.

Im finalen Portable-Build wurden zusätzlich die Subagent-Chatdarstellung ohne
Promptfeld, die Reasoning-Auswahl ohne „Automatisch“ und der kombinierte
Stop-/Sendepfeil direkt geprüft. Leeres Enter ließ den aktiven Lauf bestehen;
Texteingabe zeigte nur den Pfeil. Die darüber gesendete Korrektur blieb im Lauf
`run-e445629a757740db9b97329bba628430` und lieferte „Sonne warm“. Nach dem echten
App-Neustart wurden zunächst 3.243 Kontexttokens wiederverwendet. Ein separater
Lauf wurde über denselben Button bei leerem Promptfeld erfolgreich abgebrochen.
Belege und Screenshots sind in `artifacts/validation/ui-native-acceptance.json`
aufgeführt. Die Anzeige „Umgeleitet“ für beendete Subagent-Denkrunden ist durch
Persistenz- und Renderer-Tests belegt, nicht durch diese abschließende Sichtprobe.

Das Originalprofil wurde anschließend mit dem finalen Build auf Schema 36
migriert. Beide vorhandenen Sitzungen sind sichtbar und gruppiert; der Pin blieb
erhalten. Alle 22 vollständigen Nachrichtenzeilen stimmen unverändert mit der
Sicherung überein. Titel, Entwürfe, Zeitstempel, Pins und Sitzungsrevisionen blieben
ebenfalls gleich; repariert wurde die ungültige Projektzuordnung. Eine bereits
abgebrochene alte Nachricht wurde nicht erneut gestartet. Nachweis:
`artifacts/validation/original-profile-after.json`.

Die unten dokumentierte fehlgeschlagene General-Probe mit stark prioritätsbetontem
Wortlaut bleibt eine Modellgrenze; sie wird durch diese erfolgreichen Prüfungen
nicht zu einem bestandenen Test umgedeutet.

## Wiederholbare Pflichtprüfung und historische Prüfmatrix

`windows/test-agent-context.ps1` ist das gemeinsame Pflichtgate für Client, Gateway,
Webdarstellung und native Cache-Verwaltung. Es läuft auch vor dem Publish durch
`windows/build.ps1`, sofern Tests nicht ausdrücklich deaktiviert werden. Ein Fehler
bricht den Build ab. Logs, TRX und `summary.json` liegen unter
`artifacts/validation/agent-context`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File windows/test-agent-context.ps1
```

Die folgende Matrix beschreibt den Stand vom 19.09.2026. Insbesondere die Zeilen
zu Subagenten, Agenten-Tabs und Arbeitskopien sind nach deren Entfernung nicht mehr
ausführbare Produktfunktionen. Auch damalige Testzahlen sind kein aktuelles Gate-Ergebnis.

| Historischer Fall | Damaliger Nachweis |
|---|---|
| Parallele Dateimutationen | Exklusive Datei-/Verzeichnisbereiche, Konfliktprüfung bei Rückübernahme, Tests gegen überlappende Pfade |
| Subagent-Terminal | Echter Prozess in eigener Quellkopie, nur zugewiesene erfolgreiche Änderungen zurückübernommen, Hardlink-/Junction- und Fehlerfälle geprüft |
| Gemeinsamer Kontext | Unveränderter Nachrichtenpräfix des Hauptagenten; offene Elternaufrufe werden als unbekannt gekennzeichnet; eigener persistenter Fortsetzungsverlauf |
| Folgende Prompts | Tatsächliche Modellnachrichten einschließlich Reasoning und Werkzeugbelegen bleiben erhalten |
| Sitzung A → B → A | Getrennte Sitzungsschlüssel, aktuelle sichtbare Historie maßgeblich; keine Vermischung |
| Modell A → B → A | Inhaltlicher Kontext bleibt; KV-Snapshots sind an Modell, Vorlage und Konfiguration gebunden |
| App-/Laufzeitneustart | Persistierte SQLite-Kontexte und native Slot-Snapshots; Wiederherstellung getrennt von tatsächlich gemessenen Cache-Treffern |
| General und Coding | Pipeline-Integrationstests und native Messmatrix für beide Rollen |
| Reiter-/Sitzungswechsel | Gemessener Kontext, Reasoning und Laufstatus werden sitzungsbezogen wiederhergestellt; fremde verspätete Ereignisse verworfen |
| Projekt-Sidebar | Migration 36 repariert ungültige Gruppen-IDs, erhält Nachrichten/Pins und gruppiert nach Workspace; neue Sitzungen übernehmen den Projektordner |
| Deutsch | Sprachregel und deutsche Vorlagen für Reasoning, Status und Teilaufträge; historische Texte werden nicht nachträglich umgeschrieben |
| Reasoning-Auswahl | Nur explizite unterstützte Stufen; alte `auto`-Präferenzen werden auf dieselbe konkrete Stufe für Anzeige und Anfrage aufgelöst; Modelle ohne wählbare Stufen deaktivieren den Button |
| Agent-Tabs | Hauptagent zeigt offene Start-/Ergebnis-Karten; Subagent verwendet dieselbe Chatdarstellung mit Auftrag, Antwort, Denktexten, Werkzeugen, Teilplan und getrennten Messwerten, ohne Promptfenster |
| Projekt-Auswahl | Globales `+` öffnet die Workspace-Auswahl; jedes Projekt besitzt ein Schreibsymbol für eine neue Sitzung im selben Workspace; Pins stehen je Projekt oben |
| Sidebar-Formatierung | Sitzungsnamen ohne Datum; kompakte einheitliche Typografie, globale und projektbezogene Symbole in einer gemeinsamen rechten Flucht |
| Umlenken in General/Coding | Gleiche Run-ID und Assistentennachricht; dauerhaft quittierte, idempotente Eingaben, Abbruch nur der aktuellen Modellgenerierung; begonnene Werkzeuge werden vor der neuen Richtung abgeschlossen |
| Umlenken nach Verbindungsverlust | Wiederholung derselben Eingabe-ID liefert auch nach Laufende die alte Quittung; neue Entwürfe bleiben erhalten; verspätete Antworten können keine andere Sitzung oder einen neuen Lauf umlenken |
| Umlenken und Historie | Chronologische Assistent-/Nutzersegmente, einheitliche CRLF-/Metadatenbereinigung, bestätigte Nutzereingaben bleiben auch nach abgebrochenen Antworten im Kontext |
| Kombinierter Promptbutton | Bei eigenem aktivem Lauf und leerem Text Stop; bei Text der ursprüngliche Sendepfeil, der den laufenden Auftrag umlenkt; leeres Enter beendet keinen Lauf |
| Umgeleitete Denkrunden | Terminaler Zustand samt Ursache bleibt nach Speicherung und Ereignis-Replay erhalten; Anzeige „Umgeleitet“ ohne weiterlaufenden Spinner |
| Subagent-Rückübernahme | Mehrere Dateien werden vorbereitet und auf Konflikte geprüft; Fehler lösen Rücknahme mit explizitem Änderungsbeleg aus, spätere Hauptagent-Änderungen bleiben erhalten |
| Aufbewahrung der Arbeitskopien | Inaktive eigene Kopien werden nach Alter, Anzahl und Größe begrenzt; aktive Sperren und fremde Verzeichnisse bleiben geschützt |

Die native Assistentenseite bleibt bei Reiterwechseln im Navigation-Cache.
WebView, laufende Werkzeuge und Ereignisverarbeitung bleiben aktiv. Erst das
tatsächliche Schließen des Fensters sichert den Entwurf und beendet die Seite,
auch wenn gerade ein anderer Reiter geöffnet ist.

## Historische Live-Abnahme und Diagnosewerkzeuge

Das deterministische Pflichtgate behauptet keine GPU-Messung. Die zusätzliche
native Messung verlangt echte `usage.prompt_tokens_details.cached_tokens` und
bricht bei fehlenden oder zu kleinen Werten ab. Sie läuft nur ohne aktive
Benutzergenerierung. Modellwechsel und Neustart werden ausdrücklich ausgelöst.

```powershell
python workers/coding/verify_session_cache.py --model MODEL_A --other-model MODEL_B --report artifacts/validation/cache-native.json
python workers/coding/verify_session_cache.py --model MODEL_A --stage seed-restart --state artifacts/validation/cache-restart-state.json --report artifacts/validation/cache-restart-seed.json
powershell -NoProfile -ExecutionPolicy Bypass -File windows/manage-coding-llama.ps1 -Action Stop
powershell -NoProfile -ExecutionPolicy Bypass -File windows/manage-coding-llama.ps1 -Action Start
python workers/coding/verify_session_cache.py --model MODEL_A --stage verify-restart --state artifacts/validation/cache-restart-state.json --report artifacts/validation/cache-restart.json
python workers/coding/verify_run_steering.py --model MODEL_A --mode both --scenario natural
python workers/coding/verify_run_steering.py --model MODEL_A --mode both --scenario priority
```

Die Umlenkungsprüfung startet je Modus genau einen Lauf, wartet auf tatsächlich
begonnene Generierung und sendet anschließend die neue Richtung. Sie prüft
Run-ID, dauerhafte Accepted-/Applied-Ereignisse, Antwortinhalt und wiederholte
Bestätigungen auch nach Laufende. Ein Fehler stoppt die Folgeprüfung; jeder Fall
schreibt seinen vollständigen Ereignisbeleg als JSON. Bereits abgebrochene lokale
Nachrichten werden beim App-Neustart nicht erneut aufgenommen oder verändert.

Die reale Portable-Abnahme am 19.09.2026 ergänzte diese Prüfungen:

- General wurde über den sichtbaren Umlenkungsbutton innerhalb derselben Run-ID
  umgelenkt; die neue Antwort lautete wie gefordert „Himmel blau“. Dabei wurden
  2.224 Token wiederverwendet, beim folgenden kontextabhängigen Prompt 3.003.
- Im Coding-Chat schrieben Haupt- und Subagent getrennt `ui-main.txt` und
  `ui-child.txt`. Beide Inhalte wurden mit Dateiwerkzeugen geprüft. Der Wechsel
  zu einer anderen Sitzung und zurück erhielt den laufenden Subagent-Reiter.
  Gemessene Cachewerte erreichten 11.785 Token beim Hauptagenten und 10.348 beim
  Subagenten. Belege liegen in `ui-native-acceptance.json` und den zugehörigen
  Ereignisdateien unter `artifacts/validation`.
- Der zusätzliche echte Paralleltest mit isoliertem PowerShell-Prozess bestand
  nach 3:18 Minuten. Der Kindprozess lieferte Exitcode 0 und
  `SUBAGENT-TEST-PASSED`; Haupt- und Subagent wurden erfolgreich zusammengeführt.
  Beleg: `artifacts/validation/parallel-live-final/beee306b3b2a42b08ea0df3e8c4008d2.json`.

## Technische Grenzen des damaligen Builds

Verschiedene Modelle können keine KV-Tensoren teilen. Beim ersten Wechsel werden
Nachrichten erneut verarbeitet; bei der Rückkehr kann der kompatible gespeicherte
Cache wieder genutzt werden. Haupt- und Subagent auf getrennten GPUs teilen den
Nachrichtenkontext, führen aber jeweils eigene modellgebundene KV-Caches.

Die Subagent-Projektkopie schützt vor gewöhnlichen Datei- und Buildüberschneidungen.
Sie ist keine Betriebssystem-Sandbox für beliebigen nativen Code. Explizite Pfade
zum Originalworkspace werden abgelehnt, Links nicht rückübertragen, und der
Hauptagent wartet vor eigenen breiten Terminalbefehlen auf das Ende des Subagenten.
Änderungen außerhalb der zugewiesenen Bereiche werden nicht zurückübernommen.

Ein App-Neustart erhält Sitzungsverlauf, Agent-Reiter, gespeicherte Messwerte und
kompatible Modellcaches. Das ist von der Wiederaufnahme eines noch laufenden
Auftrags zu unterscheiden: Coding kann den gespeicherten Lauf wieder aufnehmen;
General beendet beim Neustart weiterhin den vorherigen aktiven Lauf. Ein Wechsel
zwischen Seiten, Sitzungen oder Agent-Reitern beendet dagegen keinen aktiven Lauf.

## Historische native Diagnose der Umlenkungspriorität

Am 19.09.2026 wurde eine echte General-Umlenkung dauerhaft akzeptiert und vor der
nächsten Modellgenerierung angewendet, dennoch setzte Qwen den alten Auftrag fort.
Checkpoint, Native-Promptvorlage und native Tokenmessung bestätigten die neue
Nutzereingabe im zweiten Aufruf; die Antwort stammte nicht aus einem alten Stream.

Die kontrollierte Diagnose verwendete denselben abgeschlossenen Checkpoint ohne
abschließende Assistentenantwort, dasselbe bereits geladene Modell, niedrige
Reasoning-Stufe und höchstens 512 Ausgabetoken je Anfrage. Der alte Sprachhinweis,
ein ausschließlich auf Sprache begrenzter Ersatz und das Weglassen des letzten
Sprachhinweises genügten jeweils nicht: Die neue direkte Nutzereingabe wurde
fälschlich als externe Anweisung behandelt. Eine statische Systemregel, die spätere
direkte Nutzereingaben als gültige Korrekturen früherer Nutzeraufträge einordnet,
führte zur exakt verlangten Markerantwort. Eine natürliche Korrektur ergab ebenfalls
genau die geforderten drei Wörter. Quellen und Werkzeugausgaben bleiben Daten;
Systemregeln und Werkzeugrechte behalten ihren Vorrang.

Die getestete Regel gilt zentral für General, Coding und wiederaufgenommene
Checkpoints. Historische Nutzer-, Werkzeug- und Sprachnachrichten bleiben erhalten.
Alte Sitzungen erhalten einmalig eine ergänzte Systempolicy, wodurch ihr nativer
Präfixcache einmal ungültig wird; folgende Präparationen sind idempotent und behalten
den gesamten bereits aktualisierten Nachrichtenpräfix. Regressionstests prüfen
diese Grenzen und den unveränderten historischen Inhalt. Die direkten nativen
Diagnosen ersetzen nicht den anschließenden vollständigen Gateway-Umlenkungstest.

Messbelege: `artifacts/validation/steering-reminder-ab.json` und
`artifacts/validation/steering-policy-ab.json`.

Die anschließende Gateway-Vollprobe mit `--mode both --scenario natural` bestand
für General und Coding. Beide Läufe wurden während tatsächlicher Generierung
umgelenkt und lieferten nach der bestätigten Umlenkungsposition exakt den neuen
Marker. Run-ID, einmalige Start-/Accepted-/Applied-Ereignisse, unmittelbare und
abschließende idempotente Quittungen sowie der vollständige SSE-Replay wurden
geprüft. Belege: `steering-live-general-natural-6b1e43ceb553.json` und
`steering-live-coding-natural-b47d8d6be0bc.json` in `artifacts/validation/`.

Die weiterhin verfügbare Variante `--scenario priority` verwendet bewusst den
ursprünglichen, stark prioritätsbetonten Wortlaut. Coding bestand diese Variante;
General setzte in einer echten Probe trotz angewendeter Eingabe den alten Auftrag
fort. Die Fehlprobe wurde gesichert und ausschließlich ihr eigener Lauf beendet
(`steering-live-general-2e0c2570f160.json`). Direkte Wiederholungen desselben
2003-Token-Prompts lieferten den Marker; ein Transport- oder KV-Korruptionsfehler
wurde nicht nachgewiesen. Dieser modellabhängige Unterschied ist eine verbleibende
Grenze der Befolgung von Anweisungen, kein bestandener Abnahmefall. Beide Szenarien
bleiben ausführbar und ihre JSON-Belege werden nicht überschrieben. Der Probe-Trigger
verlangt echte Textausgabe oder mindestens 64 Reasoningzeichen statt bloßer
Tokenzähler; längere abweichende Antworten brechen den eigenen Lauf frühzeitig ab.
