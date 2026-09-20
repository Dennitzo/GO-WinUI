# Vollständige Chatantworten vor Werkzeugaktionen

Der Hauptagent veröffentlicht seinen sichtbaren Antworttext erst nach Abschluss
des jeweiligen Modellturns. Die Veröffentlichung erfolgt vor der Verarbeitung
der zugehörigen Werkzeugaufrufe. Das gilt für General und Coding. Reasoning und
Tokenfortschritt bleiben während der Erzeugung sichtbar. Unvollständiger Text
eines fehlgeschlagenen Versuchs wird nicht veröffentlicht; bereits bestätigte
Antwortblöcke bleiben bei Wiederholung und Wiederanlauf erhalten.

Automatisches Vorlesen wartet auf die nächste Hauptagent-Werkzeugmeldung oder
den Abschluss der Antwort. Alle bis dahin abgeschlossenen Textteile werden als
ein Block an die bestehende Sprachpipeline übergeben. Damit startet nicht mehr
für jeden Satz eine getrennte Wiedergabe. Wiederholte Statusmeldungen lesen
bereits konsumierten Text nicht erneut vor. Werkzeugausgaben und Denktexte
werden weiterhin nicht vorgelesen.

Die Aktionen warten auf den vollständigen Text, nicht auf die gesamte Dauer der
Audiowiedergabe. Die Sprachpipeline darf einen vollständigen Block intern für
die Synthese segmentieren. Ein späterer Modellturn nach einem Werkzeugergebnis
ist eine neue Antwort und kann nicht vor diesem Werkzeugergebnis vorliegen.

Regression: `CodingNarrationPolicyTests`, `CodingTextReconcilerTests` und
`SpeechStreamingTests.AutomaticNarrationWaitsForCompleteBlockAndSpeaksBothSentencesTogether`.
Das Vorlesebeispiel lautet: „Die neuen Tests bestehen. Ich prüfe jetzt zusätzlich
die vorhandenen Modellrouting-Tests, um die Regression zu bestätigen.“
