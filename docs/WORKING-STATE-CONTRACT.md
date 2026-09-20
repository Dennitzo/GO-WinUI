# Arbeitsstand: geprüfter Vertrag

`coding.updatePlan` speichert Daten des aktuellen Agentenlaufs. Es führt keine
Projektarbeit aus und ist kein Erfolgsbeleg für Dateiänderungen oder Tests.

## Anlegen und ändern

- `steps` und `acceptanceCriteria` haben getrennte ID-Bereiche.
- Neue IDs benötigen `id`, `title` (1–400 nichtleere Zeichen) und `status`.
- Nach erfolgreichem Anlegen reichen ID und geänderte Felder. Auslassen erhält
  den bisherigen Wert; `null`, leere oder nur aus Leerzeichen bestehende Titel
  sind ungültig. Ein unverändert mitgesendeter Titel ist zulässig.
- Je Liste bleiben höchstens 16 Einträge gespeichert, davon höchstens einer
  `in_progress`. Die Prüfung erfolgt gegen den zusammengeführten Zustand.
- Bestehende Akzeptanzkriterien dürfen weder entfernt noch umbenannt werden.
- Jeder Fehler verwirft das gesamte Update. Ein gescheiterter Aufruf legt auch
  seine ansonsten gültigen Einträge nicht an.

Beispiel für einen neuen Schritt:
`{"steps":[{"id":"tests","title":"Regression prüfen","status":"pending"}]}`

Danach ist zulässig:
`{"steps":[{"id":"tests","status":"in_progress"}]}`

## Laufgrenzen und Kontext

Der bestehende Sitzungsvertrag übernimmt Nachrichten und belegte Befunde in
einen Folgelauf, beginnt dessen Planlisten aber leer. Historische Plan-Receipts
sind deshalb kein Nachweis einer aktuell registrierten ID. Die neue
`GO_CODING_RUN_PLAN_SCOPE`-Nachricht erklärt diesen Zustand vor dem neuen
Nutzerauftrag; der bisherige Gesprächspräfix bleibt unverändert.

Erfolgreiche Planänderungen melden die tatsächlich gespeicherten IDs beider
Listen. Fehler wegen unbekannter IDs nennen Liste, ID und vorhandene IDs.
Bei einem Wiederanlauf desselben Laufs bleibt dagegen dessen Checkpoint gültig.

## Belege und weitere Felder

- Ein neuer `completed`-Status benötigt 1–3 tatsächliche erfolgreiche
  Werkzeugbelege. Plan-Receipts sind keine Ausführungsbelege. Bereits bestätigte
  Abschlüsse dürfen ihre gültigen Belege behalten.
- `facts` und `rejectedHypotheses` benötigen tatsächliche Belege; Fehlerbelege
  sind hier zulässig. Je Liste bleiben die letzten 12 Befunde aktiv. Das Journal
  bewahrt ältere Einträge. Leere Listen löschen keine bestehenden Befunde.
- `explanation` steht im Aufrufjournal, wird aber nicht als eigenes Feld des
  Arbeitsstands übernommen. `nextStep` und `phase` werden übernommen.
- `phase=final` setzt weder Schritte noch Akzeptanzkriterien auf `completed`.

## Prüfung vom 19.09.2026

Der gemeldete Fehler wurde im gespeicherten Lauf nachvollzogen: Die alten IDs
standen im übernommenen Verlauf, waren in den neuen Planlisten aber unbekannt.
Es handelte sich nicht um eine generelle Titelpflicht bei vorhandenen IDs.

Regressionstests decken Laufgrenzen, unbekannte IDs, getrennte Listen, atomare
Ablehnung, ausgelassene/ungültige Titel und bestätigte IDs ab. Schema- und
Reducer-Validierung laufen nun über dieselbe Formprüfung. Bestehende Tests
prüfen weiterhin Belege, Teilupdates, Kompaktierung und Sitzungsfortsetzung.
Alle 678 Server-Tests bestanden; Protokoll:
`artifacts/validation/working-state-consistency-tests.log`.
