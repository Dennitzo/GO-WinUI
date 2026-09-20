using GoAi.Contracts;

namespace GoAi.Server.Core.Runs;

public sealed partial class AgentToolCatalog
{
    private static IEnumerable<AgentToolSpec> WorkspaceToolSpecs() =>
    [
        Server("speech.synthesize", "Vorlesen oder Audiodatei erstellen: Synthetisiere den angegebenen Text als Audioartefakt. Verwende dies selbstständig, wenn die Aufgabe eine gesprochene Ausgabe verlangt.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"text":{"type":"string","minLength":1,"maxLength":20000}},"required":["text"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.Open,
            "Starte eine im ausgewählten Workspace erstellte Anwendung zur visuellen Prüfung: relative HTML/PDF-Datei in eigenem Browserfenster oder eine kompilierte EXE. Der Prozess bleibt für die Sichtprüfung geöffnet. Anschließend image.input windows/capture und media.analyze verwenden. Keine Shellbefehle.",
            ToolRiskClass.Process, Parse("""
            {"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.ImageInput,
            "Bild analysieren, Schritt 1: Lade ein lokales Workspace-Bild (operation=file, path) oder erfasse das zur Aufgabe gehörende Anwendungsfenster. windows liefert aktuelle windowIds; capture benötigt eine solche windowId. Anschließend zwingend media.analyze mit der erhaltenen uploadId und konkreter Prüffrage aufrufen. Erst dessen Befunde rechtfertigen visuelle Änderungen; nach Änderungen erneut erfassen und vergleichen.",
            ToolRiskClass.ReadOnly, Parse("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["file","windows","capture"]},"path":{"type":"string"},"windowId":{"type":"string"}},"required":["operation"],"additionalProperties":false}
            """)),
        Client(WorkspaceTools.Blender,
            BlenderAuthoringGuide.ToolDescription,
            ToolRiskClass.Process, Parse("""
            {
              "type":"object",
              "properties":{
                "operation":{"type":"string","enum":["info","scaffold","stage","preview","run","inspect","render","open"]},
                "path":{"type":"string","minLength":1,"maxLength":1024,"description":"Relativer Workspace-Pfad: scaffold neuer Projektordner; stage kleines .py-Etappenskript mit höchstens 12000 Zeichen; run vorhandenes Python-Skript; preview/inspect/render/open vorhandene .blend; info optional .py/.blend für aktuellen SHA-256 und Dateigröße."},
                "brief":{"type":"string","minLength":1,"maxLength":8000,"description":"Nur scaffold: knapper Designauftrag einschließlich vorhandener harter Anforderungen."},
                "expectedSha256":{"type":"string","minLength":64,"maxLength":64,"pattern":"^[0-9a-fA-F]{64}$","description":"Pflicht für stage/preview/run/inspect/render: tatsächlicher aktueller SHA-256 der path-Datei; Skripte aus coding.read, .blend-Dateien aus info path oder unverändertem sourceSha256 eines Reports."},
                "outputPath":{"type":"string","minLength":1,"maxLength":1024,"description":"Pflicht nur für stage: neuer relativer .blend-Pfad. Der Wrapper speichert und prüft automatisch, vorhandene Revisionen bleiben erhalten."},
                "label":{"type":"string","minLength":1,"maxLength":200,"description":"Pflicht für stage, optional für preview: verständlicher Etappenname, etwa 01 Hauptformen oder 03 Fahrwerk korrigiert."},
                "baseScene":{"type":"string","minLength":1,"maxLength":1024,"description":"Nur stage, bei Folgeschritten: bestätigte vorhandene .blend. Nur zusammen mit baseSceneSha256. Der Wrapper lädt sie vor dem kleinen Änderungsskript."},
                "baseSceneSha256":{"type":"string","minLength":64,"maxLength":64,"pattern":"^[0-9a-fA-F]{64}$","description":"Nur stage: tatsächlicher aktueller Hash der baseScene, zwingend zusammen mit baseScene. Niemals erfinden."},
                "outputDirectory":{"type":"string","minLength":1,"maxLength":1024,"description":"Pflicht für inspect/render: neuer relativer Ausgabeordner; vorhandene Ordner werden nicht überschrieben."},
                "views":{"type":"array","minItems":1,"maxItems":6,"uniqueItems":true,"items":{"type":"string","enum":["perspective","front","right","top","back","left"]},"description":"render und stage: gewünschte Ansichten; stage rendert die neue Revision sofort nach <Revision>_renders/. Bei komplexen Objekten perspektivisch plus geeignete orthogonale Ansichten."},
                "resolution":{"type":"integer","minimum":128,"maximum":2048,"default":768,"description":"render und stage: Kantenlänge der quadratischen Ansichten in Pixeln."},
                "samples":{"type":"integer","minimum":1,"maximum":128,"default":32,"description":"render und stage: Abtastungen je Ansicht."},
                "timeoutSeconds":{"type":"integer","minimum":1,"maximum":3600}
              },
              "required":["operation"],
              "additionalProperties":false
            }
            """)),
    ];
}
