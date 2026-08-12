using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GoWinUI.Core.Contracts;
using GoWinUI.Core.Models;

namespace GoWinUI.Core.Chat;

public sealed class ContextAssembler : IContextAssembler
{
    private const int MaxDocumentCharacters = 65_000;
    private const int MaxPageCharacters = 7_000;
    private const int MaximumHistoryTokens = 16_384;
    private const int MaximumSingleHistoryMessageTokens = 4_096;
    private const int MinimumOutputTokens = 1_024;
    private const int MaximumOutputTokens = 8_192;
    private const string PromptTruncationMarker =
        "\n\n[... Eingabe für das Modellfenster gekürzt ...]\n\n";
    private const string ContextTruncationMarker =
        "\n[... Dokument- oder Workflowkontext automatisch gekürzt ...]\n";

    private static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public ContextBuildResult Build(ContextBuildRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SystemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserPrompt);

        var contextLength = Math.Max(2_048, request.ContextLength);
        var maximumOutputTokens = Math.Clamp(contextLength / 8, MinimumOutputTokens, MaximumOutputTokens);
        var inputBudget = contextLength - maximumOutputTokens;
        var pageSelection = FindRequestedPageRange(request.UserPrompt);
        var (documentText, documentWasTruncated) = BuildDocumentContext(request.DocumentPages, pageSelection);
        var hasDocumentContext = documentText.Length > 0;
        var policyReferences = GeneralChatPolicies.References(hasDocumentContext);
        var systemText = GeneralChatPolicies.Compose(request.SystemPrompt, hasDocumentContext);
        var systemTokens = EstimateTokens(systemText);
        var wasTruncated = documentWasTruncated;

        var maximumUserTokens = Math.Max(1, inputBudget - systemTokens);
        var maximumPromptTokens = Math.Max(32, maximumUserTokens - 256);
        var effectivePrompt = TruncatePreservingEnds(
            request.UserPrompt.Trim(),
            maximumPromptTokens * 4,
            ref wasTruncated);
        var selectedPages = (pageSelection is null
                ? request.DocumentPages
                : request.DocumentPages.Where(page =>
                    page.PageNumber >= pageSelection.Value.Start
                    && page.PageNumber <= pageSelection.Value.End))
            .ToArray();
        var workflowContent = request.Workflow?.ContentJson ?? string.Empty;
        var envelopeJson = BuildEnvelopeJson(
            effectivePrompt,
            request.Workflow,
            workflowContent,
            selectedPages,
            documentText,
            hasDocumentContext,
            wasTruncated,
            policyReferences);
        for (var attempt = 0;
             attempt < 32 && EstimateTokens(envelopeJson) > maximumUserTokens;
             attempt++)
        {
            var excessCharacters = Math.Max(
                64,
                (EstimateTokens(envelopeJson) - maximumUserTokens) * 4 + 16);
            wasTruncated = true;
            if (documentText.Length > 0)
            {
                documentText = TruncateContext(
                    documentText,
                    Math.Max(0, documentText.Length - excessCharacters));
            }
            else if (workflowContent.Length > 0)
            {
                workflowContent = TruncateContext(
                    workflowContent,
                    Math.Max(0, workflowContent.Length - excessCharacters));
            }
            else if (effectivePrompt.Length > 1)
            {
                effectivePrompt = TruncatePreservingEnds(
                    effectivePrompt,
                    Math.Max(1, effectivePrompt.Length - excessCharacters),
                    ref wasTruncated);
            }
            else
            {
                break;
            }

            envelopeJson = BuildEnvelopeJson(
                effectivePrompt,
                request.Workflow,
                workflowContent,
                selectedPages,
                documentText,
                hasDocumentContext,
                wasTruncated,
                policyReferences);
        }

        var retained = new Stack<LmChatMessage>();
        var baseTokens = systemTokens + EstimateTokens(envelopeJson);
        var historyBudget = Math.Min(MaximumHistoryTokens, Math.Max(0, inputBudget - baseTokens));
        var retainedHistoryTokens = 0;
        foreach (var history in request.History.Reverse())
        {
            if (history.Role == ChatRole.System
                || history.Status is MessageStatus.Failed or MessageStatus.Pending)
            {
                continue;
            }

            var content = history.Content.Trim();
            if (content.Length == 0)
            {
                continue;
            }

            var contentWasTruncated = false;
            content = TruncatePreservingEnds(
                content,
                MaximumSingleHistoryMessageTokens * 4,
                ref contentWasTruncated);
            wasTruncated |= contentWasTruncated;
            var tokens = EstimateTokens(content);
            if (retainedHistoryTokens + tokens > historyBudget)
            {
                wasTruncated = true;
                break;
            }

            retained.Push(new(history.Role, content));
            retainedHistoryTokens += tokens;
        }

        envelopeJson = BuildEnvelopeJson(
            effectivePrompt,
            request.Workflow,
            workflowContent,
            selectedPages,
            documentText,
            hasDocumentContext,
            wasTruncated,
            policyReferences);

        var messages = new List<LmChatMessage>(retained.Count + 2)
        {
            new(ChatRole.System, systemText),
        };
        messages.AddRange(retained);
        messages.Add(new(ChatRole.User, envelopeJson));
        var estimated = messages.Sum(static message => EstimateTokens(message.Content));

        return new(
            messages,
            estimated,
            wasTruncated,
            wasTruncated
                ? "Der Kontext wurde an das Modellfenster angepasst; gekürzte Dokumentseiten, Historie oder Eingaben sind nicht vollständig enthalten."
                : null,
            envelopeJson,
            policyReferences,
            maximumOutputTokens);
    }

    private static string BuildEnvelopeJson(
        string prompt,
        WorkflowDefinition? workflow,
        string workflowContent,
        IReadOnlyList<DocumentPage> pages,
        string documentText,
        bool hasDocuments,
        bool wasTruncated,
        IReadOnlyList<string> policyReferences)
    {
        var routeName = hasDocuments ? GeneralChatPolicies.DocumentRoute : GeneralChatPolicies.GeneralRoute;
        var capabilityProfile = hasDocuments ? "tga-document" : "tga-general";
        var route = new Dictionary<string, object?>
        {
            ["schema"] = GeneralChatPolicies.RouterSchema,
            ["route"] = routeName,
            ["capabilityProfile"] = capabilityProfile,
            ["reason"] = hasDocuments
                ? "TGA-Fachplanung mit Dokumentkontext"
                : "Allgemeine TGA-Fachplanung",
            ["mode"] = "general",
        };
        var modePolicy = new Dictionary<string, object?>
        {
            ["mode"] = "general",
            ["allowedRoutes"] = new[] { GeneralChatPolicies.GeneralRoute, GeneralChatPolicies.DocumentRoute },
            ["cadContextAllowed"] = false,
            ["cadToolsAllowed"] = false,
            ["cadActionsRequireDotNet"] = false,
            ["toolProposalAllowed"] = false,
            ["generalChatAllowed"] = true,
            ["documentContextAllowed"] = hasDocuments,
            ["route"] = routeName,
            ["capabilityProfile"] = capabilityProfile,
        };

        object selectedWorkflow = workflow is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>
            {
                ["id"] = workflow.Id,
                ["title"] = workflow.Title,
                ["description"] = workflow.Description,
                ["domain"] = workflow.Domain,
                ["contextSummary"] = workflow.ContextSummary,
                ["tags"] = workflow.EffectiveTags,
            };
        object[] workflowCapsules = workflow is null
            ? []
            :
            [
                new Dictionary<string, object?>
                {
                    ["id"] = workflow.Id,
                    ["title"] = workflow.Title,
                    ["description"] = workflow.Description,
                    ["domain"] = workflow.Domain,
                    ["contextSummary"] = workflow.ContextSummary,
                    ["contentJson"] = workflowContent,
                    ["manuallySelected"] = true,
                },
            ];

        var envelope = new Dictionary<string, object?>
        {
            ["schema"] = GeneralChatPolicies.EnvelopeSchema,
            ["originalUserPrompt"] = prompt,
            ["route"] = route,
            ["modePolicy"] = modePolicy,
            ["domainProfile"] = new Dictionary<string, object?>
            {
                ["name"] = "TGA-Fachplanung",
                ["application"] = "GO",
                ["applicationNameIsNotProgrammingLanguage"] = true,
                ["focus"] = new[]
                {
                    "Heizung",
                    "Kälte",
                    "Lüftung",
                    "Sanitär",
                    "Elektro",
                    "Gebäudeautomation/MSR",
                    "Energie",
                    "Brandschutzschnittstellen",
                    "Planungskoordination",
                },
            },
            ["policyRefs"] = policyReferences,
            ["expectedResponse"] = GeneralChatPolicies.ExpectedResponse,
            ["responseContract"] = new Dictionary<string, object?>
            {
                ["schema"] = GeneralAgentResponseParser.ResponseSchema,
                ["type"] = "message",
                ["required"] = new[] { "schema", "type", "message", "sessionTitle" },
                ["messageFormat"] = "visible-markdown",
                ["sessionTitle"] = new Dictionary<string, object?>
                {
                    ["language"] = "de",
                    ["maximumWords"] = 6,
                    ["mustBeSpecific"] = true,
                    ["refreshOnEveryRun"] = true,
                },
            },
            ["includeConversationHistory"] = true,
            ["selectedWorkflow"] = selectedWorkflow,
            ["workflowCapsules"] = workflowCapsules,
            ["contextWasTruncated"] = wasTruncated,
        };
        if (hasDocuments)
        {
            envelope["documentContext"] = new Dictionary<string, object?>
            {
                ["selectedText"] = documentText,
                ["pages"] = pages
                    .OrderBy(static page => page.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static page => page.PageNumber)
                    .Select(static page => new
                    {
                        documentId = page.DocumentId,
                        pageNumber = page.PageNumber,
                        fileName = page.FileName,
                    })
                    .ToArray(),
                ["truncated"] = wasTruncated,
            };
        }

        return JsonSerializer.Serialize(envelope, EnvelopeJsonOptions);
    }

    private static (string Text, bool WasTruncated) BuildDocumentContext(
        IReadOnlyList<DocumentPage> pages,
        (int Start, int End)? selection)
    {
        var selected = selection is null
            ? pages.OrderBy(static page => page.PageNumber)
            : pages.Where(page => page.PageNumber >= selection.Value.Start
                    && page.PageNumber <= selection.Value.End)
                .OrderBy(static page => page.PageNumber);

        var result = new StringBuilder();
        var wasTruncated = false;
        foreach (var page in selected)
        {
            var source = string.IsNullOrWhiteSpace(page.FileName)
                ? page.DocumentId.ToString("D")
                : page.FileName;
            var header = FormattableString.Invariant(
                $"\n--- Dokument {source}, Seite {page.PageNumber} ---\n");
            if (result.Length + header.Length >= MaxDocumentCharacters)
            {
                wasTruncated = true;
                break;
            }

            result.Append(header);
            if (page.Text.Length > MaxPageCharacters)
            {
                wasTruncated = true;
            }

            var pageLength = Math.Min(page.Text.Length, MaxPageCharacters);
            var remaining = MaxDocumentCharacters - result.Length;
            var included = Math.Min(pageLength, remaining);
            result.Append(page.Text.AsSpan(0, included));
            if (included < page.Text.Length)
            {
                wasTruncated = true;
            }
        }

        return (result.ToString(), wasTruncated);
    }

    private static string TruncateContext(string value, int maximumCharacters)
    {
        if (maximumCharacters <= 0)
        {
            return string.Empty;
        }
        if (value.Length <= maximumCharacters)
        {
            return value;
        }
        if (maximumCharacters <= ContextTruncationMarker.Length)
        {
            return value[..maximumCharacters];
        }

        var available = maximumCharacters - ContextTruncationMarker.Length;
        var headLength = available * 2 / 3;
        var tailLength = available - headLength;
        return string.Concat(
            value.AsSpan(0, headLength),
            ContextTruncationMarker,
            value.AsSpan(value.Length - tailLength, tailLength));
    }

    private static string TruncatePreservingEnds(
        string value,
        int maximumCharacters,
        ref bool wasTruncated)
    {
        if (value.Length <= maximumCharacters)
        {
            return value;
        }

        if (maximumCharacters <= PromptTruncationMarker.Length)
        {
            wasTruncated = true;
            return value[..Math.Max(0, maximumCharacters)];
        }

        var available = maximumCharacters - PromptTruncationMarker.Length;
        var headLength = available * 3 / 4;
        var tailLength = available - headLength;
        wasTruncated = true;
        return string.Concat(
            value.AsSpan(0, headLength),
            PromptTruncationMarker,
            value.AsSpan(value.Length - tailLength, tailLength));
    }

    private static (int Start, int End)? FindRequestedPageRange(string prompt)
    {
        var match = Regex.Match(
            prompt,
            @"\b(?:seite|seiten|page|pages)\s+(?<start>\d{1,5})(?:\s*(?:-|\u2013|bis|to)\s*(?<end>\d{1,5}))?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["start"].Value, out var start))
        {
            return null;
        }

        var end = int.TryParse(match.Groups["end"].Value, out var parsed)
            ? parsed
            : start;
        return start > 0 && end >= start ? (start, end) : null;
    }

    private static int EstimateTokens(string text) => Math.Max(1, (text.Length + 3) / 4);
}
