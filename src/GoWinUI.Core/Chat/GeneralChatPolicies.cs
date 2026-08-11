using System.Text;

namespace GoWinUI.Core.Chat;

public static class GeneralChatPolicies
{
    public const string EnvelopeSchema = "barebone.general.markdown.request.v1";
    public const string RouterSchema = "barebone.agent.router.v1";
    public const string GeneralRoute = "general_chat";
    public const string DocumentRoute = "document_qa";
    public const string ExpectedResponse = "visible-markdown";

    private const string GeneralPolicy = """
        Du bist der allgemeine AI-Assistent für GO. Antworte direkt, hilfreich und auf Deutsch als normale sichtbare Chatantwort.

        Verwende Markdown-Überschriften, Tabellen und echte Aufzählungen, wenn sie die Lesbarkeit verbessern. Tabellen müssen gültige Markdown-Pipe-Tabellen mit Header, einer Trennzeile wie |---|---| und gleich vielen Zellen pro Datenzeile sein. Verwende keine tabulatorgetrennten oder nur durch Leerzeichen ausgerichteten Klartexttabellen. Aufzählungen müssen echte Markdown-Listen mit genau einem Punkt pro Zeile sein. Verwende keine HTML-Tags wie <br>, sondern echte Zeilenumbrüche.

        Formeln werden als LaTeX geschrieben: inline mit \(...\), abgesetzt mit \[...\]. Setze Einheiten mit \mathrm aufrecht, gruppiere mehrbuchstabige Indizes, verwende \cdot für Multiplikation und schreibe ein Dezimalkomma in LaTeX beispielsweise als 0{,}9. Mathematische Werte in Tabellenzellen bleiben Inline-Math.

        Bei Berechnungen zeigst du zuerst die Grundgleichung und erklärst danach kurz alle darin verwendeten Symbole. Zeige notwendige Umrechnungen in SI-Einheiten vor der eigentlichen Berechnung. Führe die Einheit an jedem eingesetzten Wert, jedem Summanden und jedem Zwischenergebnis mit, sodass die Einheitendurchrechnung nachvollziehbar bleibt.

        Rufe im Allgemeinen Modus keine CAD-/DOTNET-Funktionen auf, schlage keine Zeichnungsaktionen vor und erfinde keine Tools oder Ausführungsergebnisse.
        """;

    private const string DocumentPolicy = """
        Dokument-Policy:
        Wenn Dokumentkontext vorhanden ist und die Nutzeranfrage PDF-, Word- oder Textinhalte betrifft, antworte ausschließlich aus den im Request enthaltenen Auszügen. Erfinde keine Inhalte außerhalb dieses Dokumentkontexts. Verweise auf Dokumentnamen und Seiten, wenn sie im Kontext vorhanden sind. Wenn der benötigte Bereich nicht enthalten ist, sage das klar und frage nach einem engeren oder anderen Bereich. Dokumentfragen verwenden keine CAD-Tools.
        """;

    public static string Compose(string applicationInstruction, bool hasDocumentContext)
    {
        var result = new StringBuilder(GeneralPolicy.Trim());
        if (hasDocumentContext)
        {
            result.AppendLine().AppendLine().Append(DocumentPolicy.Trim());
        }

        if (!string.IsNullOrWhiteSpace(applicationInstruction))
        {
            result.AppendLine().AppendLine()
                .AppendLine("Vertrauenswürdiger Anwendungshinweis:")
                .Append(applicationInstruction.Trim());
        }

        return result.ToString();
    }

    public static IReadOnlyList<string> References(bool hasDocumentContext) =>
        hasDocumentContext ? ["general", "documents"] : ["general"];
}
