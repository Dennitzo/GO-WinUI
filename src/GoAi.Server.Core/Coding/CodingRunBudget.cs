using GoAi.Server.Core.Configuration;

namespace GoAi.Server.Core.Coding;

internal sealed record CodingRunBudget(int ModelRounds, int ToolCalls)
{
    internal const string PromptMarker = "GO_CODING_WORK_BUDGET";
    internal const int WarningRounds = 8;
    // This bounds one native response, never the number of calls in a run.
    internal const int MaximumNativeCallsPerTurn = 120;

    internal static CodingRunBudget FromOptions(GoAiServerOptions options) => new(
        Math.Max(0, options.CodingMaximumModelRounds),
        Math.Max(0, options.CodingMaximumToolCalls));

    internal bool MustSummarize(long rounds, long tools) => ModelRounds > 0 && rounds >= ModelRounds - 1 || ToolCalls > 0 && tools >= ToolCalls;

    internal bool ShouldWarn(long rounds, long tools) => ModelRounds > 0 && ModelRounds - rounds <= WarningRounds || ToolCalls > 0 && ToolCalls - tools <= WarningRounds;

    internal static int Remaining(int limit, long used) => limit == 0 ? int.MaxValue : (int)Math.Clamp(limit - used, 0, int.MaxValue);

    internal string Instruction(long rounds, long tools) => $"{PromptMarker}\n"
        + $"Arbeitsbudget: {(ModelRounds == 0 ? "unbegrenzte" : Remaining(ModelRounds, rounds).ToString(System.Globalization.CultureInfo.InvariantCulture))} Modellrunden und {(ToolCalls == 0 ? "unbegrenzte" : Remaining(ToolCalls, tools).ToString(System.Globalization.CultureInfo.InvariantCulture))} Werkzeugaufrufe. "
        + (MustSummarize(rounds, tools)
            ? "Dies ist der reservierte Abschluss. Rufe keine weiteren Werkzeuge auf. Schreibe einen ehrlichen Zwischenstand mit belegten Befunden, tatsächlich angewendeten Änderungen, tatsächlich ausgeführten Tests und noch offenen Aufgaben. Behaupte keine Erledigung offener Arbeiten. Nenne den konkreten nächsten Schritt für eine Fortsetzung."
            : ShouldWarn(rounds, tools)
                ? "Das Arbeitsbudget nähert sich dem Ende. Priorisiere die aktuelle Änderung und ihre Prüfung; beginne keine breite weitere Bestandsaufnahme. Reserviere die letzte Modellrunde für einen ehrlichen Zwischenstand."
                : "Arbeite auf die vollständige Nutzeraufgabe hin. Bündele unabhängige Lesezugriffe in einem Modellturn; führe abhängige Änderungen erst nach deren Ergebnissen aus.");

    internal string FailureMessage(long rounds, long tools) =>
        $"Arbeitsbudget erreicht ({rounds}/{ModelRounds} Modellrunden, {tools}/{ToolCalls} Werkzeugaufrufe). Der bisherige Zwischenstand und alle Werkzeugergebnisse bleiben gespeichert. Mit einer weiteren Nachricht ist die Fortsetzung möglich; der Auftrag wurde nicht als vollständig erledigt markiert.";
}
