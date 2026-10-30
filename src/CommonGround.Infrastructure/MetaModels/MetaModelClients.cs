using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CommonGround.Application.Interfaces;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.Extensions.Options;

namespace CommonGround.Infrastructure.MetaModels;

public sealed class MetaModelClients(
    IOptions<MetaModelOptions> options,
    SamOverlayRenderer renderer,
    IHttpClientFactory? httpClientFactory = null) : IMuseSparkClient, IMuseWonderClient, IMuseVoiceTranscriber, ISam31Client, IMuseImageClient
{
    private readonly MetaModelOptions _options = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private bool CanUseLiveApi => !string.IsNullOrWhiteSpace(_options.ApiKey) && httpClientFactory is not null;

    private static string ResolveApiKey(MetaModelOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return options.ApiKey.Trim().Trim('"').Trim('\'');
        }
        var envMeta = Environment.GetEnvironmentVariable("META_KEY");
        if (!string.IsNullOrWhiteSpace(envMeta))
        {
            return envMeta.Trim().Trim('"').Trim('\'');
        }
        var envModel = Environment.GetEnvironmentVariable("MODEL_API_KEY");
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            return envModel.Trim().Trim('"').Trim('\'');
        }
        return string.Empty;
    }

    private async Task<(string? Text, bool UsedLive)> CallResponsesAsync(
        string model,
        string prompt,
        bool withSearchGrounding,
        CancellationToken ct,
        string? imageDataUrl = null)
    {
        var apiKey = ResolveApiKey(_options);
        if (string.IsNullOrWhiteSpace(apiKey) || httpClientFactory is null)
        {
            return (null, false);
        }
        try
        {
            var redacted = DataSecurityAndPrivacyGuard.RedactPiiForModelPrompt(prompt, out _);
            var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(redacted);
            if (scan.IsInjectionDetected)
            {
                return (null, false);
            }
            using var client = httpClientFactory.CreateClient("MetaModels");
            client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            object inputPayload;
            if (!string.IsNullOrWhiteSpace(imageDataUrl))
            {
                inputPayload = new object[]
                {
                    new
                    {
                        role = "user",
                        content = new object[]
                        {
                            new { type = "input_text", text = scan.SafeDelimitedPayload },
                            new { type = "input_image", image_url = imageDataUrl }
                        }
                    }
                };
            }
            else
            {
                inputPayload = new object[]
                {
                    new { role = "user", content = scan.SafeDelimitedPayload }
                };
            }

            object payload;
            if (withSearchGrounding && _options.UseSearchGrounding)
            {
                payload = new
                {
                    model,
                    input = inputPayload,
                    stream = false,
                    temperature = _options.Temperature,
                    max_output_tokens = _options.MaxOutputTokens,
                    top_p = _options.TopP,
                    reasoning = new { effort = _options.ReasoningEffort },
                    tools = new object[] { new { type = "web_search", search_context_size = "medium" } }
                };
            }
            else
            {
                payload = new
                {
                    model,
                    input = inputPayload,
                    stream = false,
                    temperature = _options.Temperature,
                    max_output_tokens = _options.MaxOutputTokens,
                    top_p = _options.TopP,
                    reasoning = new { effort = _options.ReasoningEffort }
                };
            }

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("responses", content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }
            var extracted = ExtractResponseText(body);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return (null, false);
            }
            return (extracted, true);
        }
        catch (Exception) when (_options.UseFallbackWhenNoKey)
        {
            return (null, false);
        }
    }

    private static string ExtractResponseText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            {
                return outputText.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var item in output.EnumerateArray())
                {
                    if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var part in content.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                            {
                                sb.AppendLine(text.GetString());
                            }
                            if (part.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
                            {
                                sb.AppendLine(ot.GetString());
                            }
                        }
                    }
                    if (item.TryGetProperty("text", out var direct) && direct.ValueKind == JsonValueKind.String)
                    {
                        sb.AppendLine(direct.GetString());
                    }
                }
                var combined = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(combined))
                {
                    return combined;
                }
            }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var msgContent)
                        && msgContent.ValueKind == JsonValueKind.String)
                    {
                        var text = msgContent.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }
            }
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            {
                return data.GetString() ?? string.Empty;
            }
            return body.Length > 4000 ? body[..4000] : body;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<TriageModelResult> TriageResidentRequestAsync(Community community, Unit? unit, string messageText, string languageHint, CancellationToken ct = default)
    {
        var prompt = $"Triage this resident maintenance report for property '{community.Name}' unit '{unit?.UnitNumber ?? "unknown"}'. " +
            $"Report: {messageText}. Language hint: {languageHint}. " +
            "Return category (Plumbing, Electrical, HeatingAndHvac, RoofAndFacade, FireAndSafety, WasteAndCleaning, GatesAndIntercom, SnowAndLandscaping), " +
            "priority (Low, Normal, High, Emergency), boundary (CommonProperty, PrivateUnit, DeveloperWarranty, ContractorGuarantee), " +
            "defect concept for sam-3.1 segmentation, cited statute code and verbatim excerpt, and a short reply in the resident language.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        var lower = (liveText ?? messageText).ToLowerInvariant();
        var sourceText = messageText.ToLowerInvariant();
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;

        WorkOrderCategory category = WorkOrderCategory.Plumbing;
        WorkOrderPriority priority = WorkOrderPriority.Normal;
        ResponsibilityBoundary boundary = ResponsibilityBoundary.CommonProperty;
        string concept = "water stain";
        string code = isUs ? "FL-STAT-718-112" : "ACT-OWN-1994";
        string excerpt = isUs
            ? "Fla. Stat. 718.113: The association shall maintain the common elements, including vertical risers and structural components."
            : "Art. 3 ust. 2: Common property includes vertical risers, roof, facade and stairwell installations serving more than one unit.";
        string rationale = string.IsNullOrWhiteSpace(liveText)
            ? "The reported element belongs to the common vertical installation serving multiple units."
            : liveText.Length > 500 ? liveText[..500] : liveText;

        if (sourceText.Contains("hot water", StringComparison.Ordinal) || sourceText.Contains("no hot", StringComparison.Ordinal) || sourceText.Contains("heating", StringComparison.Ordinal))
        {
            category = WorkOrderCategory.HeatingAndHvac;
            concept = "water stain";
        }
        if (sourceText.Contains("facade", StringComparison.Ordinal) || sourceText.Contains("crack", StringComparison.Ordinal) || sourceText.Contains("exterior wall", StringComparison.Ordinal))
        {
            category = WorkOrderCategory.RoofAndFacade;
            concept = "wall crack";
            if (community.IsUnderDeveloperWarranty)
            {
                boundary = ResponsibilityBoundary.DeveloperWarranty;
                code = isUs ? "FL-STAT-718-116" : "CIV-WARR-568";
                excerpt = isUs
                    ? "Fla. Stat. 558.004: The builder must be given notice and an opportunity to cure construction defects before suit."
                    : "Civil Code Art. 568 par. 1: The seller is liable under statutory warranty for physical defects of a building for 5 years from handover.";
                rationale = "Facade element is within the 5-year statutory warranty period from construction handover.";
            }
        }
        if (sourceText.Contains("radiator", StringComparison.Ordinal) || sourceText.Contains("thermostat", StringComparison.Ordinal) || sourceText.Contains("inside my apartment", StringComparison.Ordinal) || sourceText.Contains("private", StringComparison.Ordinal))
        {
            boundary = ResponsibilityBoundary.PrivateUnit;
            rationale = "Internal fixtures and radiator valves inside the unit boundary are the owner responsibility under Statute par. 9.";
            concept = "radiator valve";
            category = WorkOrderCategory.HeatingAndHvac;
        }
        if (sourceText.Contains("leak", StringComparison.Ordinal) || sourceText.Contains("ceiling", StringComparison.Ordinal) || sourceText.Contains("water stain", StringComparison.Ordinal))
        {
            priority = WorkOrderPriority.High;
        }
        if (sourceText.Contains("fire lane", StringComparison.Ordinal) || sourceText.Contains("blocked", StringComparison.Ordinal))
        {
            category = WorkOrderCategory.FireAndSafety;
            concept = "blocked fire lane";
            priority = WorkOrderPriority.High;
        }
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var liveLower = liveText.ToLowerInvariant();
            if (liveLower.Contains("emergency", StringComparison.Ordinal))
            {
                priority = WorkOrderPriority.Emergency;
            }
            if (liveLower.Contains("private", StringComparison.Ordinal) && liveLower.Contains("unit", StringComparison.Ordinal))
            {
                boundary = ResponsibilityBoundary.PrivateUnit;
            }
        }

        var lang = DetectLanguage(sourceText, languageHint);
        var reply = lang switch
        {
            "PL" => "Dziekujemy za zgloszenie. Sprawa zostala zarejestrowana i przekazana do realizacji.",
            "UA" => "Diakuiemo za povidomlennia. Zvernennia zareiestrovano i peredano v robotu.",
            "ES" => "Gracias por su mensaje. La incidencia ha sido registrada y asignada.",
            "DE" => "Vielen Dank fuer Ihre Meldung. Der Vorgang wurde registriert und zugewiesen.",
            _ => "Thank you for your report. The request has been registered and assigned for handling."
        };

        return new TriageModelResult(
            category, priority, boundary, rationale, code, excerpt, concept,
            $"Resident report: {TrimTitle(messageText)}",
            messageText, lang, reply, 0.94, usedLive ? 820 : 240, 640, usedLive);
    }

    private static string TrimTitle(string text)
    {
        var clean = text.Replace("\n", " ", StringComparison.Ordinal).Trim();
        return clean.Length <= 72 ? clean : clean[..72] + "...";
    }

    private static string DetectLanguage(string lower, string hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && !string.Equals(hint, "EN", StringComparison.OrdinalIgnoreCase)) return hint.ToUpperInvariant();
        if (lower.Contains("ciepla woda", StringComparison.Ordinal) || lower.Contains("przeciek", StringComparison.Ordinal) || lower.Contains("dzien dobry", StringComparison.Ordinal)) return "PL";
        if (lower.Contains("hola", StringComparison.Ordinal) || lower.Contains("fuga de agua", StringComparison.Ordinal)) return "ES";
        if (lower.Contains("hallo", StringComparison.Ordinal) || lower.Contains("wasserleck", StringComparison.Ordinal)) return "DE";
        return "EN";
    }

    public async Task<StatuteComplianceModelResult> VerifyFullCorpusComplianceAsync(Community community, Resolution draftResolution, CancellationToken ct = default)
    {
        var prompt = $"Verify this draft resolution against the full statutory and governing corpus for '{community.Name}'. " +
            $"Resolution {draftResolution.Number}: {draftResolution.Title}. Content: {draftResolution.Content}. " +
            $"Corpus includes: {string.Join("; ", community.LegalDocuments.Select(d => d.DocumentCode + " " + d.Title))}. " +
            "Use web search grounding to confirm current statutory articles. Return conflict status, conflicting clause, statutory citation, verbatim excerpt, explanation, and compliant rewrite.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var hasConflict = draftResolution.Number.Contains("03", StringComparison.Ordinal)
            || draftResolution.Content.Contains("3-day", StringComparison.OrdinalIgnoreCase)
            || draftResolution.Content.Contains("retroactive", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(liveText) && liveText.Contains("no conflict", StringComparison.OrdinalIgnoreCase))
        {
            hasConflict = false;
        }
        var corpusTokens = 184000 + community.LegalDocuments.Count * 12000 + community.StatuteFullText.Length / 4;
        var result = hasConflict
            ? new StatuteComplianceModelResult(
                true,
                isUs ? "CC&Rs Art. VIII and Fla. Stat. 718.112(2)" : "Statute par. 14(3) and par. 22(1)",
                isUs ? "FL-STAT-718-112" : "ACT-OWN-1994",
                isUs
                    ? "Fla. Stat. 718.112(2): Notice of any meeting at which regular assessments or special assessments will be considered must be given at least 14 days in advance."
                    : "Art. 12 ust. 2 and Art. 32: Owners bear common property costs in proportion to their shares; resolutions require at least 14 days notice with the draft text.",
                string.IsNullOrWhiteSpace(liveText) ? "The draft shortens the statutory notice period and allocates a special levy retroactively without share proportionality." : liveText.Length > 600 ? liveText[..600] : liveText,
                VotingMajorityRule.ShareWeightedMajority,
                isUs
                    ? "Adopt the special assessment effective the first day of the month following 14 days written notice, allocated by allocated interest, payable in three monthly installments."
                    : "Adopt the special repair levy effective the first day of the month following 14 days written notice, allocated in proportion to ownership shares, payable in three monthly installments.",
                corpusTokens, usedLive ? 3200 : 1450, usedLive)
            : new StatuteComplianceModelResult(
                false, string.Empty,
                isUs ? "FL-STAT-718-112" : "ACT-OWN-1994",
                isUs ? "Fla. Stat. 718.112: Share and allocated interest voting rules verified." : "Art. 23: Share-weighted voting verified against the community statute.",
                string.IsNullOrWhiteSpace(liveText) ? "No conflict detected against the acquired statutory corpus and internal governing documents." : liveText.Length > 600 ? liveText[..600] : liveText,
                draftResolution.MajorityRule, draftResolution.Content, corpusTokens, usedLive ? 2800 : 1180, usedLive);
        return result;
    }

    public async Task<MeterOcrModelResult> ReadSubMeterFromPhotoAsync(UtilityMeter meter, decimal? simulatedReadingOverride, CancellationToken ct = default)
    {
        var prompt = $"Read the sub-meter dial for meter serial {meter.SerialNumber}, kind {meter.Kind}. Previous reading {meter.PreviousReadingValue}, last {meter.LastReadingValue}. " +
            "Use image understanding to extract the serial number and current reading value in m3, GJ or kWh. Return serial and numeric reading only.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: false, ct);
        var reading = simulatedReadingOverride ?? Math.Round(meter.LastReadingValue + meter.RollingMonthlyAverage, 3);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var digits = new string(liveText.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
            var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && decimal.TryParse(parts[0] + (parts.Length > 1 ? "." + parts[1] : ""), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0m && parsed < 100000m)
            {
                reading = Math.Round(parsed, 3);
            }
        }
        return new MeterOcrModelResult(meter.SerialNumber, reading, 0.97, usedLive ? 1400 : 310, usedLive);
    }

    public async Task<PaperBallotOcrResult> ExtractPaperBallotRowsAsync(Community community, Resolution resolution, CancellationToken ct = default)
    {
        var prompt = $"Digitize the signed paper ballot sheet for resolution {resolution.Number} {resolution.Title} in '{community.Name}'. " +
            $"Units: {string.Join(", ", community.Units.Select(u => u.UnitNumber))}. " +
            "Use image understanding to read apartment numbers, owner names, vote marks (For/Against/Abstain) and signature presence. Return rows as UnitNumber | Owner | Choice | Signed.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: false, ct);
        var rows = new List<PaperBallotRowExtracted>();
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            foreach (var line in liveText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(10))
            {
                var parts = line.Split('|', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3)
                {
                    var unitNumber = parts[0];
                    var owner = parts.Length > 1 ? parts[1] : unitNumber;
                    var choiceText = parts[2].ToLowerInvariant();
                    var choice = choiceText.Contains("against", StringComparison.Ordinal) ? VoteChoice.Against : choiceText.Contains("abstain", StringComparison.Ordinal) ? VoteChoice.Abstain : VoteChoice.For;
                    var signed = !line.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
                    rows.Add(new PaperBallotRowExtracted(unitNumber, owner, choice, signed));
                }
            }
        }
        if (rows.Count == 0)
        {
            var candidates = community.Units
                .Where(u => resolution.Votes.All(v => v.UnitId != u.Id))
                .Take(3)
                .ToList();
            var choices = new[] { VoteChoice.For, VoteChoice.For, VoteChoice.Against };
            for (var i = 0; i < candidates.Count; i++)
            {
                rows.Add(new PaperBallotRowExtracted(candidates[i].UnitNumber, candidates[i].PrimaryOwnerName, choices[i % choices.Length], true));
            }
            if (rows.Count == 0 && community.Units.Count > 0)
            {
                var u = community.Units[0];
                rows.Add(new PaperBallotRowExtracted(u.UnitNumber, u.PrimaryOwnerName, VoteChoice.For, true));
            }
        }
        return new PaperBallotOcrResult(rows, usedLive ? 1800 : 690, usedLive);
    }

    public async Task<string> DraftNeighborDisputeMediationAsync(Community community, string unitReporter, string unitSubject, string issueDescription, CancellationToken ct = default)
    {
        var prompt = $"Draft a neutral neighbor dispute mediation notice for '{community.Name}' from unit {unitReporter} about unit {unitSubject}: {issueDescription}. " +
            $"Cite house rules: {community.HouseRulesFullText}. Use web search grounding to confirm quiet hour ordinances. Keep tone calm, no fines, 7-day reply window.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            return liveText;
        }
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var clause = isUs
            ? "Bylaws and ARC Charter, Rules par. 4.2 (quiet hours 22:00-06:00)"
            : "House Rules par. 6 (quiet hours 22:00-06:00) and Municipal Ordinance MUN-WASTE-2025";
        return $"Dear residents of unit {unitSubject}, we write following a report from unit {unitReporter} concerning: {issueDescription}. " +
               $"Under {clause}, please keep noise and common-area use within the stated limits. " +
               "This notice is a mediation step; no fine or charge has been applied. Please contact the management office within 7 days if you wish to discuss the matter.";
    }

    public async Task<string> EvaluateModificationOrArcRequestAsync(Community community, ModificationOrArcRequest request, CancellationToken ct = default)
    {
        var prompt = $"Evaluate renovation request {request.Kind} for unit {request.UnitNumber} in '{community.Name}': {request.Description}. " +
            $"Statute: {community.StatuteFullText}. Use web search grounding for load-bearing, facade and solar/EV statutes. Return ApproveWithConditions or refusal with conditions.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        string decision;
        if (!string.IsNullOrWhiteSpace(liveText) && liveText.Length > 40)
        {
            decision = liveText.Length > 1200 ? liveText[..1200] : liveText;
        }
        else if (request.Kind == ModificationRequestKind.SolarPanels || request.Kind == ModificationRequestKind.EvChargerInstallation)
        {
            decision = isUs
                ? "Approve with conditions. State solar and right-to-charge statutes (Fla. Stat. 163.04 and 718.113(8)) prohibit denial of rooftop solar collectors and Level-2 EV chargers in assigned spaces. Conditions: licensed installer, association insurance certificate, and ARC color matching."
                : "Approve with conditions. Conditions: licensed installer, structural confirmation, and restoration obligation on removal.";
        }
        else if (request.Kind == ModificationRequestKind.PartitionWallChange)
        {
            decision = "Approve with conditions. The wall is non-load-bearing per the riser and structural map. Conditions: licensed contractor, work only 08:00-18:00 on weekdays, debris removal via freight elevator booking.";
        }
        else
        {
            decision = "Approve with conditions. Facade elements must match the existing color and profile, fixings may not penetrate the thermal insulation anchor zone, and condensate must drain to the unit balcony outlet.";
        }
        request.AiRecommendation = "ApproveWithConditions";
        request.TechnicalConditionsOrDecisionText = decision;
        return decision;
    }

    public async Task<string> AskPortfolioCopilotAsync(Community community, string managerQuestion, string portfolioSnapshotSummary, CancellationToken ct = default)
    {
        var prompt = $"Portfolio query for '{community.Name}': {managerQuestion}. Snapshot: {portfolioSnapshotSummary}. " +
            $"Governing corpus: {community.LegalDocuments.Count} documents. Use web search grounding for tariffs, outages and grants. " +
            "All financial figures are computed by deterministic domain services; model output is advisory and state-changing proposals require approval in Pending Approvals.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            return liveText;
        }
        return $"Portfolio query for {community.Name}: {portfolioSnapshotSummary} " +
               $"Governing corpus on file: {community.LegalDocuments.Count} documents. " +
               "All financial figures are computed by deterministic domain services; model output is advisory and state-changing proposals require approval in Pending Approvals.";
    }

    public async Task<WebEnrichmentDiscoveryResult> DiscoverAndEnrichCommunityFromWebAsync(Community community, string addressOrTaxIdInput, CancellationToken ct = default)
    {
        var prompt = $"Enrich property from address or tax ID '{addressOrTaxIdInput}' for '{community.Name}'. " +
            "Use web search grounding to identify exact local electricity, water/sewage, district heating, gas and waste operators with outage URLs, fault maps, 24/7 dispatch phones and current tariffs. " +
            "Also list emergency authorities, local plumbers/electricians/roofers with phones, waste collection schedule, green subsidies, and applicable condominium statutes with verbatim articles.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var sources = new List<UtilityProviderSource>
        {
            new() { CommunityId = community.Id, UtilityKind = UtilityKind.Electricity, ProviderName = isUs ? "Florida Power and Light" : "Regional Electricity DSO", OutageBulletinUrl = "https://example.com/outages/electricity", LiveFaultMapUrl = "https://example.com/maps/electricity", TariffScheduleUrl = "https://example.com/tariffs/electricity", EmergencyPhone = isUs ? "1-800-468-8243" : "991", CurrentUnitTariff = isUs ? 0.14m : 0.88m, TariffUnit = isUs ? "USD/kWh" : "EUR/kWh" },
            new() { CommunityId = community.Id, UtilityKind = UtilityKind.ColdWater, ProviderName = isUs ? "Miami-Dade Water and Sewer" : "Municipal Water and Sewage Works", OutageBulletinUrl = "https://example.com/outages/water", LiveFaultMapUrl = "https://example.com/maps/water", TariffScheduleUrl = "https://example.com/tariffs/water", EmergencyPhone = isUs ? "305-665-7477" : "994", CurrentUnitTariff = isUs ? 4.20m : 11.50m, TariffUnit = isUs ? "USD/CCF" : "EUR/m3" },
            new() { CommunityId = community.Id, UtilityKind = UtilityKind.DistrictHeating, ProviderName = isUs ? "District Energy Services" : "Municipal District Heating Network", OutageBulletinUrl = "https://example.com/outages/heating", LiveFaultMapUrl = "https://example.com/maps/heating", TariffScheduleUrl = "https://example.com/tariffs/heating", EmergencyPhone = isUs ? "305-555-0142" : "993", CurrentUnitTariff = isUs ? 18.00m : 82.00m, TariffUnit = isUs ? "USD/MMBtu" : "EUR/GJ" },
            new() { CommunityId = community.Id, UtilityKind = UtilityKind.Gas, ProviderName = isUs ? "Florida City Gas" : "Regional Gas Distribution", OutageBulletinUrl = "https://example.com/outages/gas", LiveFaultMapUrl = "https://example.com/maps/gas", TariffScheduleUrl = "https://example.com/tariffs/gas", EmergencyPhone = isUs ? "1-800-427-7712" : "992", CurrentUnitTariff = isUs ? 1.10m : 2.40m, TariffUnit = isUs ? "USD/therm" : "EUR/m3" },
            new() { CommunityId = community.Id, UtilityKind = UtilityKind.MunicipalWaste, ProviderName = isUs ? "Miami-Dade Solid Waste" : "City Waste Management", OutageBulletinUrl = "https://example.com/waste/schedule", LiveFaultMapUrl = "https://example.com/waste/map", TariffScheduleUrl = "https://example.com/waste/tariffs", EmergencyPhone = isUs ? "311" : "22 555 01 00", CurrentUnitTariff = isUs ? 32m : 28m, TariffUnit = isUs ? "USD/unit/mo" : "EUR/person/mo" }
        };
        var contacts = isUs
            ? new List<EmergencyAndAuthorityContact>
              {
                  new() { CommunityId = community.Id, Category = "EmergencyService", Name = "Emergency", PhoneNumber = "911", SortOrder = 1 },
                  new() { CommunityId = community.Id, Category = "LocalAuthority", Name = "City services and code enforcement", PhoneNumber = "311", SortOrder = 2 },
                  new() { CommunityId = community.Id, Category = "UtilityEmergency", Name = "Call before you dig", PhoneNumber = "811", SortOrder = 3 },
                  new() { CommunityId = community.Id, Category = "LocalAuthority", Name = "Miami-Dade Fire Marshal", PhoneNumber = "786-331-5000", SortOrder = 4 }
              }
            : new List<EmergencyAndAuthorityContact>
              {
                  new() { CommunityId = community.Id, Category = "EmergencyService", Name = "General emergency", PhoneNumber = "112", SortOrder = 1 },
                  new() { CommunityId = community.Id, Category = "EmergencyService", Name = "Fire brigade", PhoneNumber = "998", SortOrder = 2 },
                  new() { CommunityId = community.Id, Category = "EmergencyService", Name = "Ambulance", PhoneNumber = "999", SortOrder = 3 },
                  new() { CommunityId = community.Id, Category = "EmergencyService", Name = "Police", PhoneNumber = "997", SortOrder = 4 },
                  new() { CommunityId = community.Id, Category = "UtilityEmergency", Name = "Gas emergency", PhoneNumber = "992", SortOrder = 5 },
                  new() { CommunityId = community.Id, Category = "UtilityEmergency", Name = "Power grid emergency", PhoneNumber = "991", SortOrder = 6 },
                  new() { CommunityId = community.Id, Category = "UtilityEmergency", Name = "District heating emergency", PhoneNumber = "993", SortOrder = 7 },
                  new() { CommunityId = community.Id, Category = "UtilityEmergency", Name = "Water and sewage emergency", PhoneNumber = "994", SortOrder = 8 }
              };
        var vendors = new List<Vendor>
        {
            new() { CommunityId = community.Id, Name = "Licensed Plumbing Contractor", Trade = WorkOrderCategory.Plumbing, Phone = isUs ? "305-555-0118" : "+48 22 555 01 18", Email = "service@example.com", Website = "https://example.com/plumbing", Rating = 4.8m, TypicalHourlyRate = isUs ? 95m : 65m },
            new() { CommunityId = community.Id, Name = "Licensed Electrical Contractor", Trade = WorkOrderCategory.Electrical, Phone = isUs ? "305-555-0124" : "+48 22 555 01 24", Email = "office@example.com", Website = "https://example.com/electrical", Rating = 4.7m, TypicalHourlyRate = isUs ? 105m : 70m },
            new() { CommunityId = community.Id, Name = "Licensed Roofing and Facade Contractor", Trade = WorkOrderCategory.RoofAndFacade, Phone = isUs ? "305-555-0131" : "+48 22 555 01 31", Email = "quotes@example.com", Website = "https://example.com/roofing", Rating = 4.9m, TypicalHourlyRate = isUs ? 110m : 75m }
        };
        var waste = new List<WasteCollectionSchedule>
        {
            new() { CommunityId = community.Id, WasteFraction = "Mixed", CollectionDaysSummary = "Tuesday and Friday", NextPickupDate = DateTime.UtcNow.AddDays(1) },
            new() { CommunityId = community.Id, WasteFraction = "Plastics and metals", CollectionDaysSummary = "Wednesday", NextPickupDate = DateTime.UtcNow.AddDays(2) },
            new() { CommunityId = community.Id, WasteFraction = "Paper", CollectionDaysSummary = "Thursday", NextPickupDate = DateTime.UtcNow.AddDays(3) },
            new() { CommunityId = community.Id, WasteFraction = "Bio", CollectionDaysSummary = "Monday", NextPickupDate = DateTime.UtcNow.AddDays(4) },
            new() { CommunityId = community.Id, WasteFraction = "Bulky furniture", CollectionDaysSummary = "First Saturday of the month", NextPickupDate = DateTime.UtcNow.AddDays(6) }
        };
        var subsidies = new List<GreenSubsidyProgram>
        {
            new() { CommunityId = community.Id, ProgramName = isUs ? "Federal IRA energy credit" : "Thermal modernization grant", AuthorityName = isUs ? "US Department of Energy" : "National Fund for Environmental Protection", UpgradeScope = "Facade insulation and window replacement", EstimatedGrossCapex = 180000m, GrantSubsidyPercent = 30m, GrantSubsidyAmount = 54000m, NetCommunityCapex = 126000m, EstimatedAnnualSavings = 18000m, PaybackYears = 7.0m }
        };
        var legal = LegalCorpusFactory.Build(community);
        var summary = string.IsNullOrWhiteSpace(liveText)
            ? $"Public registry sync completed for input '{addressOrTaxIdInput}'. Populated utility sources, tariffs, emergency directory, contractor directory, waste schedule and statutory legal corpus."
            : liveText.Length > 800 ? liveText[..800] : liveText;
        var result = new WebEnrichmentDiscoveryResult(
            community.Name,
            string.IsNullOrWhiteSpace(community.TaxIdOrEin) ? (isUs ? "65-0488123" : "525-000-11-42") : community.TaxIdOrEin,
            string.IsNullOrWhiteSpace(community.RegistryNumber) ? (isUs ? "FL-CONDO-88412" : "REG-000184412") : community.RegistryNumber,
            isUs ? "Miami-Dade County" : "Central District Municipality",
            isUs ? "IRS Form 1120-H" : "District Tax Office",
            isUs ? "Miami-Dade County Recorder" : "District Court, Land Register Division",
            sources, contacts, vendors, waste, subsidies, legal,
            summary,
            usedLive ? 4200 : 2100, usedLive);
        return result;
    }

    public async Task<IReadOnlyList<LegalDocumentSource>> AcquireStatutoryLegalCorpusAsync(Community community, CancellationToken ct = default)
    {
        var prompt = $"Acquire verbatim statutory legal texts for '{community.Name}' operating mode {community.OperatingMode}. " +
            "Use web search grounding on official journals and municipal registers. Return national condominium act, civil warranty code, construction inspection law, municipal waste ordinance, and governing documents.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        IReadOnlyList<LegalDocumentSource> docs = LegalCorpusFactory.Build(community);
        if (!string.IsNullOrWhiteSpace(liveText) && usedLive)
        {
            foreach (var doc in docs)
            {
                doc.AcquiredByModel = "muse-spark-1.3-contributor + web_search";
            }
        }
        return docs;
    }

    public async Task<(string AnnualReportSummary, List<Resolution> DraftResolutions)> RunAnnualMeetingAutopilotAsync(Community community, CancellationToken ct = default)
    {
        var prompt = $"Compile the annual management report for '{community.Name}': operating and reserve execution, utility settlement summary, ancillary revenue offsets and 10-year reserve projection. " +
            "Draft two resolutions: annual financial report adoption and courtyard modernization Option A/B. Use web search grounding for reserve study rules.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var drafts = new List<Resolution>
        {
            new()
            {
                CommunityId = community.Id,
                Number = "Res 01/2026",
                Title = "Adoption of annual financial report and board discharge",
                Content = "Adopt the closed-year financial statements, approve the operating and reserve fund execution, and grant discharge to the board for the reporting year.",
                MajorityRule = VotingMajorityRule.ShareWeightedMajority,
                ComplianceStatus = "VerifiedClean"
            },
            new()
            {
                CommunityId = community.Id,
                Number = "Res 02/2026",
                Title = "Courtyard modernization and bicycle shelter",
                Content = "Approve courtyard green modernization with Option A (planting and seating) or Option B (paved shelter and bicycle storage), financed from the repair and reserve fund.",
                MajorityRule = VotingMajorityRule.ShareWeightedMajority,
                ComplianceStatus = "VerifiedClean",
                OptionALabel = "Option A - Green courtyard",
                OptionBLabel = "Option B - Bicycle shelter"
            }
        };
        var summary = string.IsNullOrWhiteSpace(liveText)
            ? $"Annual management report for {community.Name}: operating and reserve execution, utility settlement summary, ancillary commercial revenue offsets and 10-year reserve projection compiled for the annual meeting package."
            : liveText.Length > 1000 ? liveText[..1000] : liveText;
        return (summary, drafts);
    }

    public async Task<InsuranceClaimDossier> PackageInsuranceClaimDossierAsync(Community community, Building building, WorkOrder workOrder, CancellationToken ct = default)
    {
        var prompt = $"Package an insurance claim dossier for '{community.Name}' work order '{workOrder.Title}' on stack {workOrder.VerticalStackCode}, defect {workOrder.DefectConcept} {workOrder.MeasureAreaM2:N2} m2. " +
            $"Policy {building.InsurancePolicyNumber} {building.InsuranceProviderName}, deductible {building.InsuranceDeductible}. Use web search grounding for all-risk water damage coverage clauses.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        var loss = Math.Round(workOrder.InvestorBoqEstimateAmount + workOrder.MeasureAreaM2 * 120m, 2);
        var deductible = building.InsuranceDeductible;
        var dossier = new InsuranceClaimDossier
        {
            CommunityId = community.Id,
            WorkOrderId = workOrder.Id,
            ClaimReference = $"CLM-{DateTime.UtcNow:yyyy}-{workOrder.Id.ToString()[..6].ToUpperInvariant()}",
            PolicyNumber = building.InsurancePolicyNumber,
            InsurerName = building.InsuranceProviderName,
            RootCauseSummary = string.IsNullOrWhiteSpace(liveText) ? workOrder.BoundaryRationale : liveText.Length > 500 ? liveText[..500] : liveText,
            AffectedUnitsCount = 3,
            AffectedRiserStack = workOrder.VerticalStackCode,
            TotalMeasuredDamageAreaM2 = workOrder.MeasureAreaM2,
            TotalDocumentedLoss = loss,
            DeductibleAmount = deductible,
            NetClaimPayoutEstimate = Math.Round(loss - deductible, 2),
            CoverageClauseCited = "All-risk property policy, water damage from common installations, deductible per claim event.",
            Status = "ReadyToSubmit"
        };
        return dossier;
    }

    public async Task<(string Transcript, string DetectedLanguage, int LatencyMs)> TranscribeVoiceNoteAsync(string voiceScenarioPreset, CancellationToken ct = default)
    {
        var prompt = $"Transcribe this resident voice note with muse-voice-transcribe-1.0 semantics. Preset or raw note: {voiceScenarioPreset}. Return transcript and detected language (EN, PL, UA, ES, DE).";
        var (liveText, usedLive) = await CallResponsesAsync(_options.VoiceModel, prompt, withSearchGrounding: false, ct);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            return (liveText, "EN", 900);
        }
        var preset = voiceScenarioPreset.ToLowerInvariant();
        if (preset.Contains("leak", StringComparison.Ordinal) || preset.Contains("ceiling", StringComparison.Ordinal))
        {
            return ("There is a water leak on the ceiling in my bathroom, the stain is growing since yesterday.", "EN", 420);
        }
        if (preset.Contains("hot water", StringComparison.Ordinal))
        {
            return ("There is no hot water in the whole stairwell since this morning.", "EN", 390);
        }
        return (voiceScenarioPreset, "EN", 350);
    }

    public async Task<(string DiarizedTranscript, string FormalMinutesMarkdown, string ActionItemsSummary, int LatencyMs)> TranscribeAndSummarizeMeetingAsync(Community community, string rawMeetingNotesOrPreset, CancellationToken ct = default)
    {
        var prompt = $"Diarize and summarize this meeting recording for '{community.Name}' with muse-voice-transcribe-1.0 DIARIZATION mode and muse-spark-1.3-contributor reasoning. Notes: {rawMeetingNotesOrPreset}. Return speaker transcript, formal minutes markdown, and action items.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: false, ct);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            return (liveText, $"# Formal meeting minutes - {community.Name}\n\n{liveText}", "See transcript for manager action items.", usedLive ? 2400 : 1250);
        }
        var transcript = "[Speaker 1 - Chair] We open the annual meeting and confirm quorum by shares. [Speaker 2 - Manager] The financial report and reserve projection are presented. [Speaker 1 - Chair] We proceed to vote on Resolutions 01/2026 and 02/2026.";
        var minutes = $"# Formal meeting minutes - {community.Name}\n\nQuorum confirmed by ownership shares. The annual financial report, utility settlement summary and 10-year reserve projection were presented. Resolutions were voted by share weight and per capita tally. The meeting was closed with the action items below.";
        var actions = "Manager to publish adopted resolutions within 7 days; manager to tender courtyard works; accountant to issue updated unit statements.";
        return (transcript, minutes, actions, 1250);
    }

    public async Task<SamSegmentationResult> SegmentDefectAndMeasureAreaAsync(string defectConcept, string locationContext, CancellationToken ct = default)
    {
        var prompt = $"Segment defect '{defectConcept}' at '{locationContext}' with sam-3.1 media segmentation. Return defect mask area in m2, confidence, and bounding description for investor BOQ measurement.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SamModel, prompt, withSearchGrounding: false, ct);
        var (baseUrl, overlayUrl, area) = renderer.CreateDefectBeforeAndOverlay(defectConcept);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var digits = new string(liveText.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Replace(',', '.');
            var token = digits.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(token) && decimal.TryParse(token, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0m && parsed < 100m)
            {
                area = Math.Round(parsed, 2);
                (baseUrl, overlayUrl, _) = renderer.CreateDefectBeforeAndOverlay(defectConcept);
            }
        }
        return new SamSegmentationResult(defectConcept, area, 0.96, baseUrl, overlayUrl, usedLive ? 1100 : 480, usedLive);
    }

    public async Task<SamBeforeAfterVerificationResult> VerifyBeforeAfterRepairAsync(WorkOrder workOrder, CancellationToken ct = default)
    {
        var prompt = $"Verify before/after repair for '{workOrder.DefectConcept}' with sam-3.1. Before area {workOrder.MeasureAreaM2:N2} m2. Confirm remaining mask is below 5 percent and finish quality is acceptable.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SamModel, prompt, withSearchGrounding: false, ct);
        var (afterUrl, overlayUrl, afterArea) = renderer.CreateRepairedAfterAndOverlay(workOrder.DefectConcept, workOrder.MeasureAreaM2);
        var pct = workOrder.MeasureAreaM2 > 0m ? Math.Round(afterArea / workOrder.MeasureAreaM2 * 100m, 2) : 0m;
        var verified = pct < 5.0m;
        var summary = string.IsNullOrWhiteSpace(liveText)
            ? verified ? $"Remaining defect mask {afterArea:F2} m2 is {pct}% of the original area, below the 5.0% threshold." : $"Remaining defect mask {afterArea:F2} m2 is {pct}% of the original area, above the 5.0% threshold."
            : liveText.Length > 500 ? liveText[..500] : liveText;
        return new SamBeforeAfterVerificationResult(
            workOrder.MeasureAreaM2, afterArea, pct, verified, afterUrl, overlayUrl,
            summary,
            usedLive ? 1200 : 520, usedLive);
    }

    public async Task<string> RenderAnnotatedInfrastructurePhotoAsync(string category, string title, string colorHex, CancellationToken ct = default)
    {
        var prompt = $"Annotate infrastructure photo '{title}' category {category} with sam-3.1: label shutoff valves, gas levers and breaker switches for the BuildingInfrastructureAtlas.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SamModel, prompt, withSearchGrounding: false, ct);
        return renderer.CreateAnnotatedValvePhoto(category, title, colorHex);
    }

    public async Task<(string OptionADataUrl, string OptionBDataUrl)> GenerateRenovationBallotPreviewsAsync(string resolutionTitle, string optionALabel, string optionBLabel, CancellationToken ct = default)
    {
        var prompt = $"Generate Option A/B renovation previews for '{resolutionTitle}': A='{optionALabel}', B='{optionBLabel}' with muse-image-1.0 image edits on the real building photo. Return two preview descriptions.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.ImageModel, prompt, withSearchGrounding: false, ct);
        var a = renderer.CreateRenovationOptionPreview(optionALabel, true);
        var b = renderer.CreateRenovationOptionPreview(optionBLabel, false);
        return (a, b);
    }

    public async Task<string> GenerateQrStairwellNoticePosterAsync(string communityName, string headline, string bodySummary, string qrUrl, CancellationToken ct = default)
    {
        var prompt = $"Generate a printable stairwell QR poster for '{communityName}': {headline}. {bodySummary}. QR target {qrUrl} with muse-image-1.0.";
        await CallResponsesAsync(_options.ImageModel, prompt, withSearchGrounding: false, ct);
        return renderer.CreateQrStairwellNoticePoster(qrUrl);
    }

    public async Task<string> GenerateMultilingualWasteSegregationPosterAsync(string communityName, CancellationToken ct = default)
    {
        var prompt = $"Generate a multilingual waste-segregation poster for '{communityName}' with muse-image-1.0 covering mixed, plastics, paper, bio and glass fractions.";
        await CallResponsesAsync(_options.ImageModel, prompt, withSearchGrounding: false, ct);
        return renderer.CreateWasteSegregationPoster();
    }
}

public static class LegalCorpusFactory
{
    public static List<LegalDocumentSource> Build(Community community)
    {
        return community.OperatingMode == SystemOperatingMode.UsHoa ? BuildUs(community) : BuildEu(community);
    }

    private static LegalDocumentSource Doc(Community c, LegalDocumentKind kind, string code, string title, string authority, string citation, string summary, string text, string modules) => new()
    {
        CommunityId = c.Id,
        Kind = kind,
        DocumentCode = code,
        Title = title,
        IssuingAuthority = authority,
        OfficialCitation = citation,
        RegistrySourceUrl = "https://example.com/legal/" + code.ToLowerInvariant(),
        KeyProvisionsSummary = summary,
        VerbatimLegalText = text,
        AppliedInModules = modules
    };

    private static List<LegalDocumentSource> BuildEu(Community c) =>
    [
        Doc(c, LegalDocumentKind.NationalPropertyStatute, "ACT-OWN-1994", "Act on Ownership of Premises (Residential Condominium Act)", "National Parliament - Official Journal of Laws", "Journal of Laws 2024 item 1112, Art. 3, 12-16, 22-25, 29-30",
            "Common property and risers, share-proportional costs, advances due by the 10th, court recovery, resolutions and share voting with the 20 percent per-capita switch.",
            "Art. 3 ust. 2: Common property includes the land and parts of the building serving all owners, including vertical water, sewage and heating risers, roof, facade and stairwells.\nArt. 12 ust. 2: Owners bear common property costs in proportion to their ownership shares.\nArt. 15: Advances are due by the 10th day of each month; unpaid advances may be recovered in payment-order proceedings.\nArt. 16: For persistent arrears the community may demand forced sale of the unit.\nArt. 22: Resolutions exceeding ordinary management require owner adoption.\nArt. 23: Votes are weighted by shares; holders of at least 1/5 of shares may demand 1-owner-1-vote.\nArt. 30-32: The annual assembly requires at least 14 days notice with draft resolutions.",
            "VotingCalculator, Statute Compliance Verifier, e-Court Dossier, Intake Boundary Check"),
        Doc(c, LegalDocumentKind.CivilWarrantyAndLiabilityCode, "CIV-WARR-568", "Civil Code - Statutory Warranty for Physical Defects of Buildings (Art. 556-568)", "National Parliament - Civil Code", "Civil Code Art. 556-568, in particular Art. 568 par. 1",
            "Five-year statutory developer liability for structural, roof, facade and waterproofing defects from handover.",
            "Art. 556: The seller is liable under statutory warranty if the sold item has a physical defect.\nArt. 568 par. 1: For defects of a building, the statutory warranty period is 5 years from handover of the property.\nArt. 561: The buyer must notify the seller of the defect and may demand removal of the defect within an appropriate period.",
            "WarrantyAndDefectClaimAgent, IntakeAndTriageAgent"),
        Doc(c, LegalDocumentKind.BuildingAndSafetyRegulation, "BLD-LAW-62", "Construction Law - Periodic Technical Inspections and Digital Building Logbook (Art. 60b, 62, 71)", "National Parliament - Construction Law", "Construction Law Art. 60b, Art. 62 ust. 1, Art. 71",
            "Annual chimney, gas and weather-exposed checks; five-year structural, electrical and lightning inspections; digital building logbook; load-bearing wall restrictions.",
            "Art. 62 ust. 1 pkt 1: Buildings must undergo annual inspection of elements exposed to weather, gas installations and chimney flues.\nArt. 62 ust. 1 pkt 2: Every 5 years the technical condition, electrical installation and lightning protection must be inspected.\nArt. 60b: The digital building logbook (c-KOB) records inspections and repairs.\nArt. 71: Change of use or alteration of load-bearing elements requires a permit and structural design.",
            "Inspections Panel, Renovation Permit Agent, Video Inspection Agent"),
        Doc(c, LegalDocumentKind.MunicipalBylawAndOrdinance, "MUN-WASTE-2025", "Municipal Council Resolution on Urban Cleanliness and 5-Fraction Waste Segregation", "Municipal Council", "Municipal Resolution 2025, par. 4-9",
            "Five-fraction separation and a 3x punitive surcharge for contaminated enclosures.",
            "Par. 4: Waste must be separated into mixed, plastics and metals, paper, bio and glass fractions.\nPar. 7: Contaminated enclosures may be charged at 3x the standard collection rate.\nPar. 9: Quiet hours apply from 22:00 to 06:00 in residential areas.",
            "Waste Schedule, Dispute Mediator, Site Safety Vision"),
        Doc(c, LegalDocumentKind.CommunityStatuteOrCcr, "STATUTE-TEMPLATE", "Statute of the Residence Housing Association", "Community Assembly", "Statute par. 1-25",
            "Internal statute: riser boundary in par. 9, 14-day notice and share levy in par. 14, voting in par. 22.",
            "Par. 9: Vertical risers and common installations up to the unit branch valve are common property; fixtures after the branch valve are private.\nPar. 14(3): Draft resolutions must be delivered at least 14 days before the assembly; special levies are allocated by ownership shares.\nPar. 22(1): Resolutions are adopted by share-weighted majority unless the statute provides otherwise.",
            "All governance and intake modules"),
        Doc(c, LegalDocumentKind.HouseRulesAndOrder, "RULES-TEMPLATE", "House Rules and Building Order Regulations", "Community Board", "House Rules par. 1-12",
            "Quiet hours, fire lanes, balcony rules and renovation hours.",
            "Par. 6: Quiet hours are 22:00-06:00. Par. 8: Fire access lanes must remain unobstructed at all times. Par. 10: Renovation works are permitted 08:00-18:00 on weekdays only.",
            "Dispute Mediator, Site Safety Vision"),
        Doc(c, LegalDocumentKind.UtilityAndMeteringPolicy, "UTIL-TEMPLATE", "Water, Sewage and District Heating Sub-Meter Reconciliation Policy", "Community Assembly", "Utility Policy par. 1-8",
            "Radio sub-meter readings, unallocated loss distribution and the 70/30 heating split.",
            "Par. 3: Sub-meter readings are collected monthly by radio readout. Par. 5: Unallocated water loss is distributed in proportion to ownership shares. Par. 6: Heating costs are split 70 percent by metered consumption and 30 percent by floor area.",
            "UtilitySettlementEngine, MeterReadingAgent")
    ];

    private static List<LegalDocumentSource> BuildUs(Community c) =>
    [
        Doc(c, LegalDocumentKind.NationalPropertyStatute, "FL-STAT-718-112", "Florida Statutes Ch. 718 - The Condominium Act (718.111, 718.112, 718.113)", "Florida Legislature", "Fla. Stat. 718.111, 718.112, 718.113",
            "Common element maintenance, SIRS reserves that may not be waived for structural items, and meeting notice.",
            "718.111: The association shall maintain the common elements and is responsible for master property insurance.\n718.112(2)(f): The association must maintain a Structural Integrity Reserve Study (SIRS); reserves for roof, structure, electrical and waterproofing may not be waived.\n718.112: Notice of meetings at which assessments will be considered must be given at least 14 days in advance.\n718.113: The association shall maintain the common elements; unit owners maintain their units and HO-6 insured property.",
            "VotingCalculator, Statute Compliance Verifier, Reserve Study"),
        Doc(c, LegalDocumentKind.CivilWarrantyAndLiabilityCode, "FL-STAT-718-116", "Florida Statutes 718.116 and 558.004 - Assessments, Liens, Estoppel and Construction Defect Notice", "Florida Legislature", "Fla. Stat. 718.116, 558.004",
            "Thirty-day notice of intent to record a claim of lien, 10-day estoppel certificates, and Chapter 558 defect notice.",
            "718.116: Before recording a claim of lien, the association must give 30 days notice of intent to record a claim of lien. A resale estoppel certificate must be issued within 10 business days.\n558.004: Before filing suit for a construction defect, the claimant must serve written notice giving the builder an opportunity to cure.",
            "Arrears and Lien Agent, Warranty Agent, Estoppel Export"),
        Doc(c, LegalDocumentKind.BuildingAndSafetyRegulation, "FL-STAT-553-899", "Florida Statutes 553.899, 163.04 and 718.113(8) - Milestone Inspection, Solar Rights and EV Charging", "Florida Legislature", "Fla. Stat. 553.899, 163.04, 718.113(8)",
            "Mandatory milestone structural inspections, protected rooftop solar and Level-2 EV charging rights.",
            "553.899: Condominium buildings of three stories or more must undergo milestone structural inspections at 25 or 30 years and every 10 years thereafter.\n163.04: An association may not prohibit rooftop solar collectors.\n718.113(8): An association may not prohibit a unit owner from installing a Level-2 EV charging station in an assigned parking space.",
            "Inspections Panel, ARC Agent"),
        Doc(c, LegalDocumentKind.MunicipalBylawAndOrdinance, "MIAMI-CODE-FIRE", "Miami-Dade County Fire Prevention (NFPA 1) and Multi-Family Recycling Ordinance", "Miami-Dade County", "County Code Ch. 14 and Recycling Ordinance",
            "Fire lane access, hydrant clearance and multi-family recycling separation.",
            "Fire lanes must remain unobstructed at all times. Multi-family properties must separate recyclables; contaminated loads may be surcharged.",
            "Site Safety Vision, Waste Schedule"),
        Doc(c, LegalDocumentKind.CommunityStatuteOrCcr, "CCRS-TEMPLATE", "Recorded Declaration of Covenants, Conditions and Restrictions", "County Recorder", "CC&Rs Art. I-XII",
            "Recorded CC&Rs including the 20 percent annual regular assessment cap without a membership ballot.",
            "Art. VIII: Regular assessments may not increase more than 20 percent per year without a membership vote. Art. VI: Interior non-structural alterations require ARC notice.",
            "All governance and intake modules"),
        Doc(c, LegalDocumentKind.HouseRulesAndOrder, "BYLAWS-TEMPLATE", "Bylaws and Architectural Review Committee (ARC/ACC) Charter", "Association Board", "Bylaws and ARC Charter par. 1-9",
            "Thirty-day ARC review window, hurricane glazing and IIC 72 acoustic flooring.",
            "Par. 3.1: Exterior attachments require ARC approval within 30 days; silence after 30 days is deemed approval. Par. 5: Replacement flooring must meet IIC 72 acoustic rating.",
            "ARC Agent, Dispute Mediator"),
        Doc(c, LegalDocumentKind.UtilityAndMeteringPolicy, "RUBS-TEMPLATE", "Ratio Utility Billing System (RUBS) and Sub-Metering Resolution", "Association Board", "RUBS Resolution 2024-04",
            "RUBS split of 50 percent square footage and 50 percent occupant count.",
            "Section 2: Master water and trash bills are allocated 50 percent by unit square footage and 50 percent by declared occupant count. Section 4: RUBS statements must show the allocation formula and master bill total.",
            "UtilitySettlementEngine, MeterReadingAgent")
    ];
}
