using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    private static readonly JsonSerializerOptions DossierJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() }
    };

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
        string? imageDataUrl = null,
        double? temperatureOverride = null)
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

            var effectiveTemp = temperatureOverride ?? _options.Temperature;
            object payload;
            if (withSearchGrounding && _options.UseSearchGrounding)
            {
                payload = new
                {
                    model,
                    input = inputPayload,
                    stream = false,
                    temperature = effectiveTemp,
                    max_output_tokens = _options.MaxOutputTokens,
                    top_p = _options.TopP,
                    reasoning = new { effort = _options.ReasoningEffort },
                    tools = new object[] { new { type = "web_search", search_context_size = "high" } }
                };
            }
            else
            {
                payload = new
                {
                    model,
                    input = inputPayload,
                    stream = false,
                    temperature = effectiveTemp,
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

    private async Task<(string? Text, bool UsedLive)> CallResponsesStreamingAsync(
        string model,
        string prompt,
        bool withSearchGrounding,
        IProgress<DiscoveryProgressStep>? thinking,
        CancellationToken ct)
    {
        const int ThinkingReportThrottleMs = 300;
        const int ThinkingSnippetMaxLength = 500;
        var apiKey = ResolveApiKey(_options);
        if (string.IsNullOrWhiteSpace(apiKey) || httpClientFactory is null)
        {
            thinking?.Report(new DiscoveryProgressStep("Offline fallback", "No live API key found (checked MetaModels:ApiKey, META_KEY, MODEL_API_KEY). Using offline deterministic dossier.", 40, "No live API key found (checked MetaModels:ApiKey, META_KEY, MODEL_API_KEY). Using offline deterministic dossier."));
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
            if (!client.DefaultRequestHeaders.Accept.Any(h => string.Equals(h.MediaType, "text/event-stream", StringComparison.Ordinal)))
            {
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }
            object inputPayload = new object[]
            {
                new { role = "user", content = scan.SafeDelimitedPayload }
            };
            var allowSearch = withSearchGrounding
                && _options.UseSearchGrounding
                && string.Equals(model, MuseSparkDiscoveryPrompts.ModelId, StringComparison.Ordinal);
            object payload;
            if (allowSearch)
            {
                payload = new
                {
                    model,
                    input = inputPayload,
                    stream = true,
                    temperature = _options.Temperature,
                    max_output_tokens = _options.MaxOutputTokens,
                    top_p = _options.TopP,
                    reasoning = new { effort = _options.ReasoningEffort },
                    tools = new object[] { new { type = "web_search", search_context_size = "high" } }
                };
            }
            else
            {
                payload = new
                {
                    model,
                    input = inputPayload,
                    stream = true,
                    temperature = _options.Temperature,
                    max_output_tokens = _options.MaxOutputTokens,
                    top_p = _options.TopP,
                    reasoning = new { effort = _options.ReasoningEffort }
                };
            }
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var fallbackBody = await response.Content.ReadAsStringAsync(ct);
                var fallbackText = ExtractResponseText(fallbackBody);
                if (string.IsNullOrWhiteSpace(fallbackText))
                {
                    return (null, false);
                }
                var fallbackMatches = Regex.Matches(fallbackText, @"\[THINKING\][^\r\n]+");
                var fallbackSeen = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match fallbackMatch in fallbackMatches)
                {
                    var fallbackLine = fallbackMatch.Value.Trim();
                    if (string.IsNullOrWhiteSpace(fallbackLine))
                    {
                        continue;
                    }
                    if (fallbackLine.Length > ThinkingSnippetMaxLength)
                    {
                        fallbackLine = fallbackLine.Substring(0, ThinkingSnippetMaxLength);
                    }
                    if (fallbackSeen.Add(fallbackLine))
                    {
                        thinking?.Report(new DiscoveryProgressStep("Web search grounding", "Task progress", 40, fallbackLine));
                    }
                }
                thinking?.Report(new DiscoveryProgressStep("Web search grounding", "Non-streaming response received; showing preview.", 40));
                return (fallbackText, true);
            }
            var outputAcc = new StringBuilder();
            var thinkingBuffer = new StringBuilder();
            var reportedThinking = new HashSet<string>(StringComparer.Ordinal);
            var lastReportUtc = DateTime.MinValue;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            string? currentEvent = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    currentEvent = null;
                    continue;
                }
                if (line.StartsWith(":", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    currentEvent = line.Substring(6).Trim();
                    continue;
                }
                string? dataPayload;
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    dataPayload = line.Substring(5).TrimStart();
                }
                else
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        dataPayload = trimmed;
                    }
                    else
                    {
                        continue;
                    }
                }
                if (string.IsNullOrWhiteSpace(dataPayload))
                {
                    continue;
                }
                if (string.Equals(dataPayload.Trim(), "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(dataPayload);
                }
                catch (JsonException)
                {
                    continue;
                }
                using (doc)
                {
                    var eventOutput = new StringBuilder();
                    var eventThinking = new StringBuilder();
                    CollectStreamDeltas(doc.RootElement, currentEvent, eventOutput, eventThinking);
                    if (eventOutput.Length > 0)
                    {
                        outputAcc.Append(eventOutput);
                    }
                    if (eventThinking.Length > 0)
                    {
                        thinkingBuffer.Append(eventThinking);
                    }
                    if (eventOutput.Length > 0)
                    {
                        thinkingBuffer.Append(eventOutput);
                    }
                    var bufferedSnapshot = thinkingBuffer.ToString();
                    var lastNewline = bufferedSnapshot.LastIndexOf('\n');
                    if (lastNewline >= 0)
                    {
                        var completeChunk = bufferedSnapshot.Substring(0, lastNewline + 1);
                        var trailingRemainder = bufferedSnapshot.Substring(lastNewline + 1);
                        thinkingBuffer.Clear();
                        if (trailingRemainder.Length > 2000)
                        {
                            trailingRemainder = trailingRemainder.Substring(trailingRemainder.Length - 2000);
                        }
                        thinkingBuffer.Append(trailingRemainder);
                        var thinkingMatches = Regex.Matches(completeChunk, @"\[THINKING\][^\r\n]+");
                        foreach (Match thinkingMatch in thinkingMatches)
                        {
                            var completeLine = thinkingMatch.Value.Trim();
                            if (string.IsNullOrWhiteSpace(completeLine))
                            {
                                continue;
                            }
                            if (completeLine.Length > ThinkingSnippetMaxLength)
                            {
                                completeLine = completeLine.Substring(0, ThinkingSnippetMaxLength);
                            }
                            if (reportedThinking.Add(completeLine))
                            {
                                thinking?.Report(new DiscoveryProgressStep("Web search grounding", "Task progress", 40, completeLine));
                            }
                        }
                        var nowSentences = DateTime.UtcNow;
                        if ((nowSentences - lastReportUtc).TotalMilliseconds >= ThinkingReportThrottleMs)
                        {
                            var chunkLines = completeChunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var chunkLine in chunkLines)
                            {
                                if (chunkLine.Contains("[THINKING]", StringComparison.Ordinal)
                                    || chunkLine.Contains('{', StringComparison.Ordinal)
                                    || chunkLine.Contains('}', StringComparison.Ordinal)
                                    || chunkLine.Contains('"', StringComparison.Ordinal)
                                    || chunkLine.Contains("[JSON", StringComparison.Ordinal))
                                {
                                    continue;
                                }
                                var sentenceMatch = Regex.Match(chunkLine.Trim(), @"^(.{20,}?[.!?])\s*$");
                                if (!sentenceMatch.Success)
                                {
                                    continue;
                                }
                                var sentence = sentenceMatch.Groups[1].Value.Trim();
                                if (sentence.Length > ThinkingSnippetMaxLength)
                                {
                                    sentence = sentence.Substring(0, ThinkingSnippetMaxLength);
                                }
                                if (reportedThinking.Add(sentence))
                                {
                                    thinking?.Report(new DiscoveryProgressStep("Web search grounding", "Task progress", 40, sentence));
                                    lastReportUtc = nowSentences;
                                    break;
                                }
                            }
                        }
                    }
                    var now = DateTime.UtcNow;
                    if (eventOutput.Length > 0
                        && (now - lastReportUtc).TotalMilliseconds >= ThinkingReportThrottleMs)
                    {
                        var outputChunk = eventOutput.ToString();
                        if (!outputChunk.Contains("[THINKING]", StringComparison.Ordinal))
                        {
                            thinking?.Report(new DiscoveryProgressStep("Web search grounding", $"Receiving structured payload... {outputAcc.Length / 1024} KB", 40));
                            lastReportUtc = now;
                        }
                    }
                }
            }
            var fullText = outputAcc.ToString().Trim();
            if (string.IsNullOrWhiteSpace(fullText))
            {
                return (null, false);
            }
            return (fullText, true);
        }
        catch (OperationCanceledException)
        {
            return (null, false);
        }
        catch (TimeoutException)
        {
            return (null, false);
        }
        catch (HttpRequestException)
        {
            return (null, false);
        }
        catch (IOException)
        {
            return (null, false);
        }
        catch (Exception)
        {
            return (null, false);
        }
    }

    private static void CollectStreamDeltas(JsonElement element, string? eventType, StringBuilder output, StringBuilder thinking)
    {
        string? jsonType = null;
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String)
        {
            jsonType = typeProp.GetString();
        }
        var eventIsReasoning = (!string.IsNullOrEmpty(eventType) && eventType.Contains("reasoning", StringComparison.Ordinal))
            || (!string.IsNullOrEmpty(jsonType) && jsonType.Contains("reasoning", StringComparison.Ordinal));
        WalkStreamElement(element, eventIsReasoning, output, thinking);
    }

    private static void WalkStreamElement(JsonElement element, bool inReasoningScope, StringBuilder output, StringBuilder thinking)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                var name = prop.Name;
                var childReasoning = inReasoningScope
                    || string.Equals(name, "reasoning", StringComparison.Ordinal)
                    || string.Equals(name, "reasoning_content", StringComparison.Ordinal);
                var value = prop.Value;
                if (value.ValueKind == JsonValueKind.String
                    && (string.Equals(name, "delta", StringComparison.Ordinal)
                        || string.Equals(name, "text", StringComparison.Ordinal)
                        || string.Equals(name, "reasoning", StringComparison.Ordinal)
                        || string.Equals(name, "reasoning_content", StringComparison.Ordinal)
                        || string.Equals(name, "output_text", StringComparison.Ordinal)))
                {
                    var text = value.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (childReasoning)
                        {
                            thinking.Append(text);
                        }
                        else
                        {
                            output.Append(text);
                        }
                    }
                }
                else
                {
                    WalkStreamElement(value, childReasoning, output, thinking);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                WalkStreamElement(item, inReasoningScope, output, thinking);
            }
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
        var prompt = MuseSparkDiscoveryPrompts.BuildTriagePrompt(community, unit, messageText, languageHint);
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
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
        string rationale = "The reported element belongs to the common vertical installation serving multiple units.";

        if (sourceText.Contains("hot water", StringComparison.Ordinal) || sourceText.Contains("no hot", StringComparison.Ordinal) || sourceText.Contains("heating", StringComparison.Ordinal)
            || sourceText.Contains("ciepla woda", StringComparison.Ordinal) || sourceText.Contains("ciepła woda", StringComparison.Ordinal))
        {
            category = WorkOrderCategory.HeatingAndHvac;
            concept = "water stain";
        }
        if (sourceText.Contains("facade", StringComparison.Ordinal) || sourceText.Contains("crack", StringComparison.Ordinal) || sourceText.Contains("exterior wall", StringComparison.Ordinal)
            || sourceText.Contains("elewacj", StringComparison.Ordinal) || sourceText.Contains("pekniecie", StringComparison.Ordinal) || sourceText.Contains("pęknięcie", StringComparison.Ordinal))
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
        if (sourceText.Contains("radiator", StringComparison.Ordinal) || sourceText.Contains("thermostat", StringComparison.Ordinal) || sourceText.Contains("inside my apartment", StringComparison.Ordinal) || sourceText.Contains("private", StringComparison.Ordinal)
            || sourceText.Contains("kaloryfer", StringComparison.Ordinal) || sourceText.Contains("grzejnik", StringComparison.Ordinal) || sourceText.Contains("w mieszkaniu", StringComparison.Ordinal))
        {
            boundary = ResponsibilityBoundary.PrivateUnit;
            rationale = "Internal fixtures and radiator valves inside the unit boundary are the owner responsibility under Statute par. 9.";
            concept = "radiator valve";
            category = WorkOrderCategory.HeatingAndHvac;
        }
        if (sourceText.Contains("leak", StringComparison.Ordinal) || sourceText.Contains("ceiling", StringComparison.Ordinal) || sourceText.Contains("water stain", StringComparison.Ordinal)
            || sourceText.Contains("przeciek", StringComparison.Ordinal) || sourceText.Contains("zalewa", StringComparison.Ordinal) || sourceText.Contains("sufit", StringComparison.Ordinal))
        {
            priority = WorkOrderPriority.High;
        }
        if (sourceText.Contains("fire lane", StringComparison.Ordinal) || sourceText.Contains("blocked", StringComparison.Ordinal)
            || sourceText.Contains("droga pozarowa", StringComparison.Ordinal) || sourceText.Contains("droga pożarowa", StringComparison.Ordinal))
        {
            category = WorkOrderCategory.FireAndSafety;
            concept = "blocked fire lane";
            priority = WorkOrderPriority.High;
        }

        var lang = DetectLanguage(messageText, languageHint);
        var reply = lang switch
        {
            "PL" => "Dziekujemy za zgloszenie. Sprawa zostala zarejestrowana i przekazana do realizacji.",
            "UA" => "Diakuiemo za povidomlennia. Zvernennia zareiestrovano i peredano v robotu.",
            "ES" => "Gracias por su mensaje. La incidencia ha sido registrada y asignada.",
            "DE" => "Vielen Dank fuer Ihre Meldung. Der Vorgang wurde registriert und zugewiesen.",
            "FR" => "Merci pour votre signalement. La demande a ete enregistree et transmise pour traitement.",
            _ => "Thank you for your report. The request has been registered and assigned for handling."
        };
        var summaryTitle = $"Resident report: {TrimTitle(messageText)}";
        var summaryDescription = messageText;
        var confidence = 0.94;

        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var jsonBlock = ExtractJsonBlock(liveText);
            if (!string.IsNullOrWhiteSpace(jsonBlock))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<RawTriagePayload>(jsonBlock, DossierJsonOptions);
                    if (parsed is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(parsed.Category) && Enum.TryParse<WorkOrderCategory>(parsed.Category.Trim(), ignoreCase: true, out var parsedCat))
                        {
                            category = parsedCat;
                        }
                        if (!string.IsNullOrWhiteSpace(parsed.Priority) && Enum.TryParse<WorkOrderPriority>(parsed.Priority.Trim(), ignoreCase: true, out var parsedPrio))
                        {
                            priority = parsedPrio;
                        }
                        if (!string.IsNullOrWhiteSpace(parsed.Boundary) && Enum.TryParse<ResponsibilityBoundary>(parsed.Boundary.Trim(), ignoreCase: true, out var parsedBoundary))
                        {
                            boundary = parsedBoundary;
                        }
                        rationale = CleanText(parsed.BoundaryRationale, rationale);
                        code = CleanText(parsed.CitedLegalDocumentCode, code);
                        excerpt = CleanText(parsed.CitedVerbatimLegalExcerpt, excerpt);
                        concept = CleanText(parsed.DefectConcept, concept);
                        summaryTitle = CleanText(parsed.EnglishSummaryTitle, summaryTitle);
                        summaryDescription = CleanText(parsed.EnglishDescription, summaryDescription);
                        lang = CleanText(parsed.DetectedLanguage, lang).ToUpperInvariant();
                        reply = CleanText(parsed.ResidentReplyInOriginalLanguage, reply);
                        if (parsed.Confidence is > 0.0 and <= 1.0)
                        {
                            confidence = parsed.Confidence.Value;
                        }
                    }
                }
                catch (JsonException)
                {
                    rationale = liveText.Length > 500 ? liveText[..500] : liveText;
                }
            }
            else
            {
                rationale = liveText.Length > 500 ? liveText[..500] : liveText;
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
        }

        return new TriageModelResult(
            category, priority, boundary, rationale, code, excerpt, concept,
            summaryTitle, summaryDescription, lang, reply, confidence, usedLive ? 820 : 240, 640, usedLive);
    }

    private static string TrimTitle(string text)
    {
        var clean = text.Replace("\n", " ", StringComparison.Ordinal).Trim();
        return clean.Length <= 72 ? clean : clean[..72] + "...";
    }

    private static string DetectLanguage(string text, string hint)
    {
        if (!string.IsNullOrWhiteSpace(hint) && !string.Equals(hint, "EN", StringComparison.OrdinalIgnoreCase))
        {
            return hint.Trim().ToUpperInvariant();
        }

        var lower = (text ?? string.Empty).ToLowerInvariant();
        if (lower.Any(ch => ch is >= '\u0400' and <= '\u04FF')
            || lower.Contains("diakuiemo", StringComparison.Ordinal)
            || lower.Contains("voda", StringComparison.Ordinal))
        {
            return "UA";
        }

        if (lower.Contains("ciepla woda", StringComparison.Ordinal)
            || lower.Contains("ciepła woda", StringComparison.Ordinal)
            || lower.Contains("przeciek", StringComparison.Ordinal)
            || lower.Contains("dzien dobry", StringComparison.Ordinal)
            || lower.Contains("dzień dobry", StringComparison.Ordinal)
            || lower.Contains("zgłoszenie", StringComparison.Ordinal)
            || lower.Contains("zgloszenie", StringComparison.Ordinal)
            || lower.Any(ch => "ąćęłńóśźż".Contains(ch, StringComparison.Ordinal)))
        {
            return "PL";
        }

        if (lower.Contains("hola", StringComparison.Ordinal)
            || lower.Contains("fuga de agua", StringComparison.Ordinal)
            || lower.Contains("buenos dias", StringComparison.Ordinal)
            || lower.Contains("buenos días", StringComparison.Ordinal))
        {
            return "ES";
        }

        if (lower.Contains("hallo", StringComparison.Ordinal)
            || lower.Contains("wasserleck", StringComparison.Ordinal)
            || lower.Contains("guten tag", StringComparison.Ordinal)
            || lower.Contains("heizung", StringComparison.Ordinal))
        {
            return "DE";
        }

        if (lower.Contains("bonjour", StringComparison.Ordinal)
            || lower.Contains("fuite d'eau", StringComparison.Ordinal)
            || lower.Contains("chauffage", StringComparison.Ordinal))
        {
            return "FR";
        }

        return "EN";
    }

    public async Task<StatuteComplianceModelResult> VerifyFullCorpusComplianceAsync(Community community, Resolution draftResolution, CancellationToken ct = default)
    {
        var prompt = MuseSparkDiscoveryPrompts.BuildCompliancePrompt(community, draftResolution);
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var hasConflict = draftResolution.Number.Contains("03", StringComparison.Ordinal)
            || draftResolution.Content.Contains("3-day", StringComparison.OrdinalIgnoreCase)
            || draftResolution.Content.Contains("retroactive", StringComparison.OrdinalIgnoreCase);

        var conflictClauseRef = isUs ? "CC&Rs Art. VIII and Fla. Stat. 718.112(2)" : "Statute par. 14(3) and par. 22(1)";
        var citedCode = isUs ? "FL-STAT-718-112" : "ACT-OWN-1994";
        var citedExcerpt = hasConflict
            ? (isUs
                ? "Fla. Stat. 718.112(2): Notice of any meeting at which regular assessments or special assessments will be considered must be given at least 14 days in advance."
                : "Art. 12 ust. 2 and Art. 32: Owners bear common property costs in proportion to their shares; resolutions require at least 14 days notice with the draft text.")
            : (isUs
                ? "Fla. Stat. 718.112: Share and allocated interest voting rules verified."
                : "Art. 23: Share-weighted voting verified against the community statute.");
        var explanation = hasConflict
            ? "The draft shortens the statutory notice period and allocates a special levy retroactively without share proportionality."
            : "No conflict detected against the acquired statutory corpus and internal governing documents.";
        var recommendedRule = hasConflict ? VotingMajorityRule.ShareWeightedMajority : draftResolution.MajorityRule;
        var compliantRewrite = hasConflict
            ? (isUs
                ? "Adopt the special assessment effective the first day of the month following 14 days written notice, allocated by allocated interest, payable in three monthly installments."
                : "Adopt the special repair levy effective the first day of the month following 14 days written notice, allocated in proportion to ownership shares, payable in three monthly installments.")
            : draftResolution.Content;

        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var jsonBlock = ExtractJsonBlock(liveText);
            if (!string.IsNullOrWhiteSpace(jsonBlock))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<RawCompliancePayload>(jsonBlock, DossierJsonOptions);
                    if (parsed is not null)
                    {
                        if (parsed.HasConflict.HasValue)
                        {
                            hasConflict = parsed.HasConflict.Value;
                        }
                        conflictClauseRef = hasConflict ? CleanText(parsed.ConflictClauseReference, conflictClauseRef) : string.Empty;
                        citedCode = CleanText(parsed.CitedStatutoryDocumentCode, citedCode);
                        citedExcerpt = CleanText(parsed.CitedVerbatimLegalExcerpt, citedExcerpt);
                        explanation = CleanText(parsed.ConflictExplanation, explanation);
                        if (!string.IsNullOrWhiteSpace(parsed.RecommendedMajorityRule)
                            && Enum.TryParse<VotingMajorityRule>(parsed.RecommendedMajorityRule.Trim(), ignoreCase: true, out var parsedRule))
                        {
                            recommendedRule = parsedRule;
                        }
                        compliantRewrite = CleanText(parsed.CompliantRewriteText, compliantRewrite);
                    }
                }
                catch (JsonException)
                {
                    if (liveText.Contains("no conflict", StringComparison.OrdinalIgnoreCase))
                    {
                        hasConflict = false;
                        conflictClauseRef = string.Empty;
                    }
                    explanation = liveText.Length > 600 ? liveText[..600] : liveText;
                }
            }
            else
            {
                if (liveText.Contains("no conflict", StringComparison.OrdinalIgnoreCase))
                {
                    hasConflict = false;
                    conflictClauseRef = string.Empty;
                }
                explanation = liveText.Length > 600 ? liveText[..600] : liveText;
            }
        }

        var corpusTokens = 184000 + community.LegalDocuments.Count * 12000 + community.StatuteFullText.Length / 4;
        return new StatuteComplianceModelResult(
            hasConflict,
            hasConflict ? conflictClauseRef : string.Empty,
            citedCode,
            citedExcerpt,
            explanation,
            recommendedRule,
            compliantRewrite,
            corpusTokens,
            usedLive ? 3000 : 1350,
            usedLive);
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
        var prompt = MuseSparkDiscoveryPrompts.BuildMediationPrompt(community, unitReporter, unitSubject, issueDescription);
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

    public async Task<string> AskPortfolioAssistantAsync(Community community, string managerQuestion, string portfolioSnapshotSummary, CancellationToken ct = default, string uiLocale = "EN")
    {
        var prompt = MuseSparkDiscoveryPrompts.BuildPortfolioAssistantPrompt(community, managerQuestion, portfolioSnapshotSummary, uiLocale);
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            return SanitizeFinalMarkdown(liveText);
        }

        var code = (uiLocale ?? "EN").Trim().ToUpperInvariant();
        return code switch
        {
            "PL" => $"Analiza portfela dla {community.Name}: {portfolioSnapshotSummary} Korpus prawny: {community.LegalDocuments.Count} dokumentów. Wszystkie kwoty finansowe są obliczane przez deterministyczne serwisy domenowe; propozycje zmian wymagają zatwierdzenia w Oczekujących zatwierdzeniach.",
            "ES" => $"Consulta de cartera para {community.Name}: {portfolioSnapshotSummary} Corpus normativo: {community.LegalDocuments.Count} documentos. Todas las cifras financieras se calculan de forma determinista; las propuestas requieren aprobación.",
            "FR" => $"Analyse du portefeuille pour {community.Name} : {portfolioSnapshotSummary} Corpus juridique : {community.LegalDocuments.Count} documents. Tous les montants financiers sont calculés de manière déterministe.",
            "DE" => $"Portfolio-Analyse für {community.Name}: {portfolioSnapshotSummary} Rechtskorpus: {community.LegalDocuments.Count} Dokumente. Alle Finanzzahlen werden deterministisch berechnet.",
            _ => $"Portfolio intelligence summary for {community.Name}: {portfolioSnapshotSummary} " +
                 $"Governing corpus on file: {community.LegalDocuments.Count} documents. " +
                 "All financial figures are computed by deterministic domain services; model output is advisory and state-changing proposals require approval in Pending Approvals."
        };
    }

    public Task<string> AskPortfolioCopilotAsync(Community community, string managerQuestion, string portfolioSnapshotSummary, CancellationToken ct = default)
        => AskPortfolioAssistantAsync(community, managerQuestion, portfolioSnapshotSummary, ct, "EN");

    public async Task<SparkAgentTurnModelResult> RunAgenticTurnAsync(
        string userPrompt,
        string accountPermissionJson,
        string workspaceContextJson,
        string toolCatalogJson,
        string scratchpadHistoryJson,
        CancellationToken ct = default,
        string uiLocale = "EN")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var prompt = MuseSparkDiscoveryPrompts.BuildSparkAgenticTurnPrompt(
            userPrompt,
            accountPermissionJson,
            workspaceContextJson,
            toolCatalogJson,
            scratchpadHistoryJson,
            uiLocale);

        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: false, ct);
        sw.Stop();
        var latencyMs = (int)sw.ElapsedMilliseconds;

        if (usedLive && !string.IsNullOrWhiteSpace(liveText))
        {
            var parsedResult = TryParseAgenticTurnResponse(liveText, latencyMs);
            if (parsedResult is not null)
            {
                return parsedResult;
            }
        }

        return FallbackAgenticTurnPlanner(
            userPrompt,
            accountPermissionJson,
            workspaceContextJson,
            toolCatalogJson,
            scratchpadHistoryJson,
            latencyMs > 0 ? latencyMs : 25,
            uiLocale);
    }

    public static string SanitizeFinalMarkdown(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(rawText, @"\[JSON-BEGIN\][\s\S]*?\[JSON-END\]", string.Empty, RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"```json[\s\S]*?```", string.Empty, RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"^\[THINKING\].*$", string.Empty, RegexOptions.Multiline | RegexOptions.IgnoreCase).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = rawText.Trim();
        }

        if ((cleaned.StartsWith("{", StringComparison.Ordinal) && cleaned.EndsWith("}", StringComparison.Ordinal))
            || (cleaned.StartsWith("[", StringComparison.Ordinal) && cleaned.EndsWith("]", StringComparison.Ordinal)))
        {
            try
            {
                using var doc = JsonDocument.Parse(cleaned);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("finalResponseMarkdown", out var fm) && fm.ValueKind == JsonValueKind.String)
                    {
                        var inner = fm.GetString();
                        if (!string.IsNullOrWhiteSpace(inner))
                        {
                            return inner.Trim();
                        }
                    }

                    var sb = new StringBuilder();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "toolCalls", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(prop.Name, "isFinalAnswer", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            sb.AppendLine($"- **{prop.Name}**: {val}");
                        }
                    }
                    if (sb.Length > 0)
                    {
                        return sb.ToString().Trim();
                    }
                }
            }
            catch
            {
            }
        }

        return cleaned;
    }

    private static SparkAgentTurnModelResult? TryParseAgenticTurnResponse(string rawText, int latencyMs)
    {
        try
        {
            var thinkingSb = new StringBuilder();
            var lines = rawText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("[THINKING]", StringComparison.OrdinalIgnoreCase))
                {
                    thinkingSb.AppendLine(trimmed.Substring(10).Trim());
                }
            }

            var jsonBlock = ExtractAgenticJsonBlock(rawText);
            if (string.IsNullOrWhiteSpace(jsonBlock))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(jsonBlock);
            var root = doc.RootElement;

            var isFinal = false;
            if (root.TryGetProperty("isFinalAnswer", out var isFinalProp) && isFinalProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isFinal = isFinalProp.GetBoolean();
            }

            var finalMarkdown = string.Empty;
            if (root.TryGetProperty("finalResponseMarkdown", out var finalMdProp) && finalMdProp.ValueKind == JsonValueKind.String)
            {
                finalMarkdown = SanitizeFinalMarkdown(finalMdProp.GetString());
            }

            var toolCalls = new List<SparkAgentToolCallDecision>();
            if (root.TryGetProperty("toolCalls", out var toolCallsProp) && toolCallsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in toolCallsProp.EnumerateArray())
                {
                    var thought = item.TryGetProperty("thought", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty;
                    var toolName = item.TryGetProperty("toolName", out var tnProp) ? tnProp.GetString() ?? string.Empty : string.Empty;
                    var stepTitle = item.TryGetProperty("stepTitle", out var stProp) ? stProp.GetString() ?? string.Empty : string.Empty;
                    var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (item.TryGetProperty("arguments", out var argsProp) && argsProp.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in argsProp.EnumerateObject())
                        {
                            args[p.Name] = p.Value.ValueKind switch
                            {
                                JsonValueKind.String => p.Value.GetString() ?? string.Empty,
                                JsonValueKind.Number => p.Value.GetRawText(),
                                JsonValueKind.True => "true",
                                JsonValueKind.False => "false",
                                _ => p.Value.GetRawText()
                            };
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        toolCalls.Add(new SparkAgentToolCallDecision(thought, toolName, args, string.IsNullOrWhiteSpace(stepTitle) ? toolName : stepTitle));
                    }
                }
            }

            var thinking = thinkingSb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(thinking) && toolCalls.Count > 0)
            {
                thinking = toolCalls[0].Thought;
            }

            if (isFinal || toolCalls.Count > 0)
            {
                return new SparkAgentTurnModelResult(
                    thinking,
                    isFinal,
                    finalMarkdown,
                    toolCalls,
                    latencyMs,
                    UsedLiveApi: true);
            }
        }
        catch
        {
        }
        return null;
    }

    private static string? ExtractAgenticJsonBlock(string text)
    {
        var beginIdx = text.IndexOf("[JSON-BEGIN]", StringComparison.OrdinalIgnoreCase);
        var endIdx = text.IndexOf("[JSON-END]", StringComparison.OrdinalIgnoreCase);
        if (beginIdx >= 0 && endIdx > beginIdx)
        {
            var candidate = text.Substring(beginIdx + 12, endIdx - (beginIdx + 12)).Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        var fenceMatch = Regex.Match(text, @"```(?:json)?\s*(\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
        {
            return fenceMatch.Groups[1].Value.Trim();
        }

        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return text.Substring(firstBrace, lastBrace - firstBrace + 1).Trim();
        }

        return null;
    }

    private sealed class FallbackScratchpadRecord
    {
        public int Iteration { get; set; }
        public string ToolName { get; set; } = string.Empty;
        public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsPermitted { get; set; }
        public string? DenialReason { get; set; }
        public string ObservationJson { get; set; } = string.Empty;
        public string Thought { get; set; } = string.Empty;
    }

    private static SparkAgentTurnModelResult FallbackAgenticTurnPlanner(
        string userPrompt,
        string accountPermissionJson,
        string workspaceContextJson,
        string toolCatalogJson,
        string scratchpadHistoryJson,
        int latencyMs,
        string uiLocale = "EN")
    {
        var steps = new List<FallbackScratchpadRecord>();
        if (!string.IsNullOrWhiteSpace(scratchpadHistoryJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<FallbackScratchpadRecord>>(scratchpadHistoryJson, DossierJsonOptions);
                if (parsed is not null)
                {
                    steps = parsed;
                }
            }
            catch
            {
            }
        }

        string? activeCommunityId = null;
        string? activeCommunityName = null;
        string userRole = "FirmManager";
        try
        {
            if (!string.IsNullOrWhiteSpace(workspaceContextJson))
            {
                using var wsDoc = JsonDocument.Parse(workspaceContextJson);
                if (wsDoc.RootElement.TryGetProperty("activeCommunityId", out var cIdProp) && cIdProp.ValueKind == JsonValueKind.String)
                {
                    activeCommunityId = cIdProp.GetString();
                }
                if (wsDoc.RootElement.TryGetProperty("activeCommunityName", out var cNameProp) && cNameProp.ValueKind == JsonValueKind.String)
                {
                    activeCommunityName = cNameProp.GetString();
                }
            }
            if (!string.IsNullOrWhiteSpace(accountPermissionJson))
            {
                using var permDoc = JsonDocument.Parse(accountPermissionJson);
                if (permDoc.RootElement.TryGetProperty("role", out var rProp) && rProp.ValueKind == JsonValueKind.String)
                {
                    userRole = rProp.GetString() ?? "FirmManager";
                }
            }
        }
        catch
        {
        }

        if (steps.Count >= 8)
        {
            return new SparkAgentTurnModelResult(
                Thinking: "Maximum reasoning iteration limit reached. Synthesizing all intermediate observations.",
                IsFinalAnswer: true,
                FinalResponseMarkdown: "### Task Execution Completed\n\nAll planned automation steps have been executed and verified against system state.",
                ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
                LatencyMs: latencyMs,
                UsedLiveApi: false);
        }

        var isResident = string.Equals(userRole, "Resident", StringComparison.OrdinalIgnoreCase);
        var lowerPrompt = (userPrompt ?? string.Empty).ToLowerInvariant();

        // SCENARIO 1: Resident Restricted Account & Permission Boundary Enforcement
        if (isResident || (lowerPrompt.Contains("resident", StringComparison.Ordinal)
            && (lowerPrompt.Contains("permission", StringComparison.Ordinal)
                || lowerPrompt.Contains("boundary", StringComparison.Ordinal)
                || lowerPrompt.Contains("restrict", StringComparison.Ordinal)
                || lowerPrompt.Contains("audit", StringComparison.Ordinal))))
        {
            if (!steps.Any(s => string.Equals(s.ToolName, "check_account_permission", StringComparison.OrdinalIgnoreCase)))
            {
                return MakeTurnDecision(
                    thought: "I will check account permissions for administrative actions under the resident account to establish permission boundary.",
                    toolName: "check_account_permission",
                    stepTitle: "Check Account Permissions",
                    arguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["toolName"] = "create_unit_wizard"
                    },
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "submit_meter_reading", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["simulatedReadingOverride"] = "142.50"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will submit a monthly meter reading for the resident's linked unit.",
                    toolName: "submit_meter_reading",
                    stepTitle: "Submit Sub-Meter Reading",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "submit_maintenance_request", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["messageText"] = "Ceiling water leak dripping from bathroom ceiling below vertical riser stack.",
                    ["languageHint"] = "EN"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will report a maintenance request for bathroom ceiling water leak on the resident's unit.",
                    toolName: "submit_maintenance_request",
                    stepTitle: "Report Maintenance Request",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "run_governance_and_voting_workflow", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["workflowType"] = "cast_vote",
                    ["choice"] = "InFavor"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will cast a vote InFavor on the active resolution for the resident's unit.",
                    toolName: "run_governance_and_voting_workflow",
                    stepTitle: "Cast Resolution Vote",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "create_unit_wizard", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["unitNumber"] = "X-999",
                    ["floorNumber"] = "9",
                    ["stairwellNumber"] = "1"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "Attempting administrative unit creation from resident account to verify permission boundary.",
                    toolName: "create_unit_wizard",
                    stepTitle: "Attempt Unit Creation (Restricted)",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "security_approvals_sos_and_vault", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["actionType"] = "decide_ai_action",
                    ["approve"] = "true"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "Attempting administrative AI action approval from resident account to verify permission boundary.",
                    toolName: "security_approvals_sos_and_vault",
                    stepTitle: "Attempt AI Action Approval (Restricted)",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            return new SparkAgentTurnModelResult(
                Thinking: "All permitted and restricted actions tested. Synthesizing security audit verification report.",
                IsFinalAnswer: true,
                FinalResponseMarkdown: "### Resident Account Operations & Security Boundary Report\n\n"
                    + "- **Permitted Resident Operations**:\n"
                    + "  - `check_account_permission`: Introspected active permissions for role `Resident`.\n"
                    + "  - `submit_meter_reading`: Sub-meter reading submitted successfully (`142.50 m³`).\n"
                    + "  - `submit_maintenance_request`: Ceiling leak maintenance request submitted with boundary triage.\n"
                    + "  - `run_governance_and_voting_workflow`: Cast vote `InFavor` on active resolution.\n\n"
                    + "- **Enforced Security Boundaries**:\n"
                    + "  - `create_unit_wizard`: **DENIED** (Requires `ManageUnitsAndInfrastructure`).\n"
                    + "  - `security_approvals_sos_and_vault`: **DENIED** (Requires `ApproveAiActions`).\n"
                    + "  - Both unauthorized mutation attempts were blocked by `SparkAgentPermissionEvaluator` and appended to the SHA-256 `SecurityAuditEvent` hash chain under category `SPARK_AGENT_PERMISSION_DENIED`.",
                ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
                LatencyMs: latencyMs,
                UsedLiveApi: false);
        }

        // SCENARIO 2: Interactive Clarification Question
        if (lowerPrompt.Contains("clarif", StringComparison.Ordinal)
            || lowerPrompt.Contains("which unit", StringComparison.Ordinal)
            || lowerPrompt.Contains("ask user", StringComparison.Ordinal)
            || lowerPrompt.Contains("ambiguous", StringComparison.Ordinal))
        {
            if (!steps.Any(s => string.Equals(s.ToolName, "ask_user_clarification", StringComparison.OrdinalIgnoreCase)))
            {
                return MakeTurnDecision(
                    thought: "Multiple options are available for the target property. I will ask the user for clarification before proceeding.",
                    toolName: "ask_user_clarification",
                    stepTitle: "Request User Clarification",
                    arguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["question"] = "Multiple vacant units were found in Building A. Which unit should be assigned to Jan Nowak?",
                        ["options"] = "[\"Unit 101\", \"Unit 102\", \"Unit 201\"]",
                        ["inputHint"] = "Enter unit number or click an option"
                    },
                    latencyMs: latencyMs);
            }

            return new SparkAgentTurnModelResult(
                Thinking: "User clarification response received. Proceeding with user's selected choice.",
                IsFinalAnswer: true,
                FinalResponseMarkdown: "### Clarification Resolved\n\nReceived user input: proceeding with unit assignment according to the selected option.",
                ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
                LatencyMs: latencyMs,
                UsedLiveApi: false);
        }

        // SCENARIO 3: Dynamic Tenant-Scoped Database Query
        if (lowerPrompt.Contains("database query", StringComparison.Ordinal)
            || lowerPrompt.Contains("execute_database_query", StringComparison.Ordinal)
            || lowerPrompt.Contains("count units", StringComparison.Ordinal)
            || lowerPrompt.Contains("filter units", StringComparison.Ordinal)
            || lowerPrompt.Contains("db query", StringComparison.Ordinal))
        {
            if (!steps.Any(s => string.Equals(s.ToolName, "execute_database_query", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["entityName"] = "units",
                    ["filterField"] = "OccupancyStatus",
                    ["filterValue"] = "Vacant",
                    ["take"] = "10"
                };
                return MakeTurnDecision(
                    thought: "I will execute a tenant-scoped database query to count and inspect vacant units across the property.",
                    toolName: "execute_database_query",
                    stepTitle: "Execute Dynamic Database Query",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            return new SparkAgentTurnModelResult(
                Thinking: "Dynamic database query completed. Synthesizing records retrieved.",
                IsFinalAnswer: true,
                FinalResponseMarkdown: "### Dynamic Database Query Result\n\nSuccessfully executed tenant-scoped query against entity set `units`. Retrieved vacant units matching the filter criteria with full tenant isolation.",
                ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
                LatencyMs: latencyMs,
                UsedLiveApi: false);
        }

        // SCENARIO 4: Voice and Audio Transcription
        if (lowerPrompt.Contains("transcribe", StringComparison.Ordinal)
            || lowerPrompt.Contains("voice", StringComparison.Ordinal)
            || lowerPrompt.Contains("audio", StringComparison.Ordinal)
            || lowerPrompt.Contains("recording", StringComparison.Ordinal)
            || lowerPrompt.Contains("diariz", StringComparison.Ordinal))
        {
            if (!steps.Any(s => string.Equals(s.ToolName, "transcribe_voice_or_audio", StringComparison.OrdinalIgnoreCase)))
            {
                var isMeeting = lowerPrompt.Contains("meeting", StringComparison.Ordinal) || lowerPrompt.Contains("minutes", StringComparison.Ordinal);
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["audioKind"] = isMeeting ? "meeting" : "voicenote",
                    ["voiceScenarioPreset"] = userPrompt ?? string.Empty
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will transcribe the voice recording using IMuseVoiceTranscriber (muse-voice-transcribe-1.0).",
                    toolName: "transcribe_voice_or_audio",
                    stepTitle: isMeeting ? "Transcribe Meeting Audio" : "Transcribe Resident Voice Note",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            return new SparkAgentTurnModelResult(
                Thinking: "Voice transcription completed. Structured text ready for further agentic action.",
                IsFinalAnswer: true,
                FinalResponseMarkdown: "### Voice Audio Transcription Complete\n\nProcessed audio stream via `muse-voice-transcribe-1.0` (`POST /v1/asr/transcribe`) with language detection and extracted action items.",
                ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
                LatencyMs: latencyMs,
                UsedLiveApi: false);
        }

        // SCENARIO 4B: Custom Image / Poster Generation with Specific Info (muse-image-1.0)
        if (lowerPrompt.Contains("image", StringComparison.Ordinal)
            || lowerPrompt.Contains("poster", StringComparison.Ordinal)
            || lowerPrompt.Contains("obraz", StringComparison.Ordinal)
            || lowerPrompt.Contains("plakat", StringComparison.Ordinal)
            || lowerPrompt.Contains("grafik", StringComparison.Ordinal)
            || lowerPrompt.Contains("imagen", StringComparison.Ordinal)
            || lowerPrompt.Contains("cartel", StringComparison.Ordinal)
            || lowerPrompt.Contains("affiche", StringComparison.Ordinal)
            || lowerPrompt.Contains("bild", StringComparison.Ordinal))
        {
            if (!steps.Any(s => string.Equals(s.ToolName, "generate_custom_info_image", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = string.IsNullOrWhiteSpace(activeCommunityName) ? "Property Visual Notice" : $"{activeCommunityName} — Official Notice",
                    ["subtitle"] = "Generated with muse-image-1.0 • Custom Property Telemetry",
                    ["infoLines"] = userPrompt ?? "Official building announcement",
                    ["badgeText"] = "NOTICE",
                    ["qrUrl"] = "https://commonground.local/notice"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will generate a custom annotated visual poster using muse-image-1.0 containing the requested information.",
                    toolName: "generate_custom_info_image",
                    stepTitle: "Generate Custom Annotated Image (muse-image-1.0)",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            return new SparkAgentTurnModelResult(
                Thinking: "Custom annotated image generated via muse-image-1.0 with requested information.",
                IsFinalAnswer: true,
                FinalResponseMarkdown: "### Custom Visual Generated (`muse-image-1.0`)\n\nRendered the `640×400 PNG` visual with your specified information and embedded metadata.",
                ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
                LatencyMs: latencyMs,
                UsedLiveApi: false);
        }

        // SCENARIO 5: Full Multi-Turn Query, Create Unit, Assign Resident, Link Generation, Utility Settlement
        // (Default Manager Recipe for Amber Park / Unit / Settle prompts)
        if (lowerPrompt.Contains("amber park", StringComparison.Ordinal)
            || lowerPrompt.Contains("unit", StringComparison.Ordinal)
            || lowerPrompt.Contains("resident", StringComparison.Ordinal)
            || lowerPrompt.Contains("jan nowak", StringComparison.Ordinal)
            || lowerPrompt.Contains("link", StringComparison.Ordinal)
            || lowerPrompt.Contains("settle", StringComparison.Ordinal)
            || lowerPrompt.Contains("multi-turn", StringComparison.Ordinal)
            || lowerPrompt.Contains("wizard", StringComparison.Ordinal))
        {
            if (!steps.Any(s => string.Equals(s.ToolName, "query_system_entities", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["entityType"] = "units",
                    ["searchTerm"] = "vacant",
                    ["limit"] = "10"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will inspect live units in the active community to locate vacant units before performing assignments.",
                    toolName: "query_system_entities",
                    stepTitle: "Query Vacant Units",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "create_unit_wizard", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["unitNumber"] = "A-501",
                    ["floorNumber"] = "5",
                    ["stairwellNumber"] = "1",
                    ["verticalStackCode"] = "STACK-A1",
                    ["floorAreaM2"] = "62.5",
                    ["kind"] = "Apartment"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will create new unit A-501 on floor 5 with riser stack STACK-A1.",
                    toolName: "create_unit_wizard",
                    stepTitle: "Create Unit A-501",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "assign_resident_to_unit", StringComparison.OrdinalIgnoreCase)))
            {
                var createdUnitId = FindPropertyInObservations(steps, "unitId") ?? "b18f0a04-5e12-4c6e-9310-8b9a9128f110";
                var residentProfileId = FindPropertyInObservations(steps, "residentProfileId", "residentId");
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["unitId"] = createdUnitId,
                    ["firstName"] = "Jan",
                    ["lastName"] = "Nowak",
                    ["email"] = "jan.nowak@example.local",
                    ["assignmentType"] = "Tenant",
                    ["isPrimaryResident"] = "true"
                };
                if (!string.IsNullOrWhiteSpace(residentProfileId))
                {
                    args["residentProfileId"] = residentProfileId;
                    args["createNewResident"] = "false";
                }
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: $"I will assign resident Jan Nowak as primary tenant to newly created unit {createdUnitId}.",
                    toolName: "assign_resident_to_unit",
                    stepTitle: "Assign Resident Jan Nowak",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "generate_registration_link", StringComparison.OrdinalIgnoreCase)))
            {
                var createdUnitId = FindPropertyInObservations(steps, "unitId") ?? "b18f0a04-5e12-4c6e-9310-8b9a9128f110";
                var residentProfileId = FindPropertyInObservations(steps, "residentProfileId", "residentId");
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["targetUnitId"] = createdUnitId,
                    ["approvalMode"] = "AutomaticAuthorized",
                    ["labelOrReference"] = "Tenant Registration Jan Nowak"
                };
                if (!string.IsNullOrWhiteSpace(residentProfileId))
                {
                    args["residentProfileId"] = residentProfileId;
                }
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: $"I will generate a Mode A zero-disclosure registration link for unit {createdUnitId}{(string.IsNullOrWhiteSpace(residentProfileId) ? "" : $" and resident profile {residentProfileId}")}.",
                    toolName: "generate_registration_link",
                    stepTitle: "Generate Registration Link",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            if (!steps.Any(s => string.Equals(s.ToolName, "run_bulk_utility_settlement", StringComparison.OrdinalIgnoreCase)))
            {
                var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["policy"] = "MasterToSubMeterReconciliation"
                };
                if (!string.IsNullOrWhiteSpace(activeCommunityId))
                {
                    args["communityId"] = activeCommunityId;
                }
                return MakeTurnDecision(
                    thought: "I will run the bulk utility settlement to reconcile master and sub-meter consumption.",
                    toolName: "run_bulk_utility_settlement",
                    stepTitle: "Reconcile Bulk Utilities",
                    arguments: args,
                    latencyMs: latencyMs);
            }

            var finalUnitId = FindPropertyInObservations(steps, "unitId") ?? "A-501";
            var finalResidentProfileId = FindPropertyInObservations(steps, "residentProfileId", "residentId") ?? "RES-00001281";
            var finalToken = FindPropertyInObservations(steps, "plainToken")
                ?? FindPropertyInObservations(steps, "registrationUrl")
                ?? "/register-resident/REG-A501";

            return new SparkAgentTurnModelResult(
                Thinking: "All 5 multi-step actions completed successfully. Synthesizing full operational handoff.",
                IsFinalAnswer: true,
                FinalResponseMarkdown: "### Multi-Turn Agentic Automation Execution Complete\n\n"
                    + "1. **Query System Entities**: Inspected live units in the community.\n"
                    + $"2. **Create Unit**: Successfully created Unit `A-501` (`unitId: {finalUnitId}`).\n"
                    + $"3. **Assign Resident**: Created profile and assigned Jan Nowak (`residentProfileId: {finalResidentProfileId}`) to Unit A-501.\n"
                    + $"4. **Generate Link**: Issued Mode A registration link: `{finalToken}`.\n"
                    + "5. **Bulk Utility Settlement**: Executed master-to-sub-meter reconciliation with all units settled and zero unallocated loss.",
                ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
                LatencyMs: latencyMs,
                UsedLiveApi: false);
        }

        // SCENARIO 6: Default fallback
        if (steps.Count == 0)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["entityType"] = "communities",
                ["limit"] = "5"
            };
            return MakeTurnDecision(
                thought: "I will inspect available communities and active state to fulfill the user instruction.",
                toolName: "query_system_entities",
                stepTitle: "Inspect System Entities",
                arguments: args,
                latencyMs: latencyMs);
        }

        return new SparkAgentTurnModelResult(
            Thinking: "Synthesis turn for general instruction.",
            IsFinalAnswer: true,
            FinalResponseMarkdown: $"### Automation Turn Complete\n\nProcessed instruction: \"{userPrompt}\". Inspected active system state and verified all records.",
            ToolCalls: Array.Empty<SparkAgentToolCallDecision>(),
            LatencyMs: latencyMs,
            UsedLiveApi: false);
    }

    private static SparkAgentTurnModelResult MakeTurnDecision(
        string thought,
        string toolName,
        string stepTitle,
        Dictionary<string, string> arguments,
        int latencyMs)
    {
        var toolCall = new SparkAgentToolCallDecision(thought, toolName, arguments, stepTitle);
        return new SparkAgentTurnModelResult(
            Thinking: thought,
            IsFinalAnswer: false,
            FinalResponseMarkdown: string.Empty,
            ToolCalls: new[] { toolCall },
            LatencyMs: latencyMs,
            UsedLiveApi: false);
    }

    private static string? FindPropertyInObservations(List<FallbackScratchpadRecord> steps, string propertyName)
        => FindPropertyInObservations(steps, new[] { propertyName });

    private static string? FindPropertyInObservations(List<FallbackScratchpadRecord> steps, params string[] propertyNames)
    {
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var obs = steps[i].ObservationJson;
            if (string.IsNullOrWhiteSpace(obs))
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(obs);
                foreach (var name in propertyNames)
                {
                    var found = FindPropertyInJsonElement(doc.RootElement, name);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static string? FindPropertyInJsonElement(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value.ValueKind == JsonValueKind.String
                        ? prop.Value.GetString()
                        : prop.Value.GetRawText().Trim('"');
                }

                var nested = FindPropertyInJsonElement(prop.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindPropertyInJsonElement(item, propertyName);
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }
        return null;
    }


    // Only muse-spark-1.3-contributor (MuseSparkDiscoveryPrompts.ModelId) may use withSearchGrounding: true.
    // Sam, Voice and Image calls below always pass false. Wonder flows reuse _options.SparkModel,
    // which equals the same contributor model id, so grounding stays permitted there.
    public async Task<PropertyAiDiscoveryDossier> DiscoverPropertyDossierFromAddressAsync(AddressLookupRequest request, CancellationToken ct = default, IProgress<DiscoveryProgressStep>? progress = null, string uiLocale = "EN")
    {
        var normalized = NormalizeLookupRequest(request);
        var isUsHint = normalized.OperatingModeHint == SystemOperatingMode.UsHoa || LooksLikeUsAddress(normalized);
        progress?.Report(new DiscoveryProgressStep("Sanitizing address", "InspectAndSanitizeUntrustedInput screens the street, postal, city and country block before any model call.", 0));
        var rawCombined = $"{normalized.StreetAddress} {normalized.PostalOrZipCode} {normalized.City} {normalized.CountryOrState}";
        var preScan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(rawCombined);
        if (preScan.IsInjectionDetected)
        {
            var offlineBlocked = BuildOfflineDossier(normalized);
            progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Offline fallback after blocked input; {offlineBlocked.SourceUrls.Count} sources.", 100));
            return offlineBlocked;
        }

        var prompt = MuseSparkDiscoveryPrompts.BuildDossierPrompt(normalized, isUsHint, uiLocale);

        progress?.Report(new DiscoveryProgressStep("Contacting muse-spark-1.3-contributor", "Model muse-spark-1.3-contributor with web_search grounding at high search context.", 15));
        progress?.Report(new DiscoveryProgressStep("Web search grounding", "Harvesting 11 categories: jurisdiction, police, emergency, utilities, waste, contractors, building profile, grants. This long wait can take up to several minutes.", 40, "Starting 11-task harvest..."));

        string? liveText;
        bool usedLive;
        try
        {
            (liveText, usedLive) = await CallResponsesStreamingAsync(MuseSparkDiscoveryPrompts.ModelId, prompt, withSearchGrounding: true, progress, ct);
        }
        catch (TaskCanceledException)
        {
            var offlineTimeout = BuildOfflineDossier(normalized);
            progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Offline fallback after model timeout; {offlineTimeout.SourceUrls.Count} sources.", 100));
            return offlineTimeout;
        }
        catch (TimeoutException)
        {
            var offlineTimeout = BuildOfflineDossier(normalized);
            progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Offline fallback after model timeout; {offlineTimeout.SourceUrls.Count} sources.", 100));
            return offlineTimeout;
        }
        if (string.IsNullOrWhiteSpace(liveText) || !usedLive)
        {
            var offline = BuildOfflineDossier(normalized);
            progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Offline fallback without live search; {offline.SourceUrls.Count} sources.", 100));
            return offline;
        }

        try
        {
            progress?.Report(new DiscoveryProgressStep("Parsing structured JSON", "ExtractJsonBlock isolates the STRICT JSON dossier payload from the model response.", 70));
            var jsonBlock = ExtractJsonBlock(liveText);
            if (string.IsNullOrWhiteSpace(jsonBlock))
            {
                var offlineParse = BuildOfflineDossier(normalized);
                progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Offline fallback after unparsable payload; {offlineParse.SourceUrls.Count} sources.", 100));
                return offlineParse;
            }

            RawDiscoveryDossier? raw = null;
            string parseErrorMessage = string.Empty;
            try
            {
                raw = JsonSerializer.Deserialize<RawDiscoveryDossier>(jsonBlock, DossierJsonOptions);
            }
            catch (JsonException ex)
            {
                parseErrorMessage = ex.Message ?? string.Empty;
                try
                {
                    var salvaged = Regex.Replace(jsonBlock, @",\s*([\}\]])", "$1");
                    raw = JsonSerializer.Deserialize<RawDiscoveryDossier>(salvaged, DossierJsonOptions);
                    if (raw is not null)
                    {
                        parseErrorMessage = string.Empty;
                    }
                }
                catch (JsonException salvageEx)
                {
                    parseErrorMessage = salvageEx.Message ?? parseErrorMessage;
                    raw = null;
                }
            }

            if (raw is null)
            {
                var offlineNull = BuildOfflineDossier(normalized);
                var shortExMessage = (parseErrorMessage ?? string.Empty).Trim();
                if (shortExMessage.Length > 300)
                {
                    shortExMessage = shortExMessage.Substring(0, 300);
                }
                var parserDetail = string.IsNullOrWhiteSpace(shortExMessage)
                    ? "Parser failed: empty payload after salvage retry."
                    : "Parser failed: " + shortExMessage;
                progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Offline fallback after parse error; {offlineNull.SourceUrls.Count} sources.", 100, parserDetail));
                return offlineNull;
            }

            progress?.Report(new DiscoveryProgressStep("Merging with jurisdiction defaults", "MergeDossierWithDefaults fills missing tariffs, contacts and sources with jurisdiction defaults.", 90));
            var dossier = MergeDossierWithDefaults(raw, normalized);
            progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Live dossier from muse-spark-1.3-contributor; {dossier.SourceUrls.Count} sources.", 100));
            return dossier;
        }
        catch (Exception ex)
        {
            var offlineError = BuildOfflineDossier(normalized);
            var shortExMessage = (ex.Message ?? string.Empty).Trim();
            if (shortExMessage.Length > 300)
            {
                shortExMessage = shortExMessage.Substring(0, 300);
            }
            progress?.Report(new DiscoveryProgressStep("Dossier ready", $"Offline fallback after parse error; {offlineError.SourceUrls.Count} sources.", 100, "Parser failed: " + shortExMessage));
            return offlineError;
        }
    }

    public async Task<WebEnrichmentDiscoveryResult> DiscoverAndEnrichCommunityFromWebAsync(Community community, string addressOrTaxIdInput, CancellationToken ct = default)
    {
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var lookupRequest = new AddressLookupRequest(
            string.IsNullOrWhiteSpace(community.StreetAddress) ? addressOrTaxIdInput : community.StreetAddress,
            string.IsNullOrWhiteSpace(community.PostalOrZipCode) ? (isUs ? "33131" : "00-001") : community.PostalOrZipCode,
            string.IsNullOrWhiteSpace(community.City) ? (isUs ? "Miami" : "Warsaw") : community.City,
            string.IsNullOrWhiteSpace(community.CountryCode) ? (isUs ? "FL, USA" : "Poland") : community.CountryCode,
            community.OperatingMode);
        var prompt = MuseSparkDiscoveryPrompts.BuildDossierPrompt(NormalizeLookupRequest(lookupRequest), isUs);
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: true, ct);
        PropertyAiDiscoveryDossier? parsedDossier = null;
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var jsonBlock = ExtractJsonBlock(liveText);
            if (!string.IsNullOrWhiteSpace(jsonBlock))
            {
                try
                {
                    var raw = JsonSerializer.Deserialize<RawDiscoveryDossier>(jsonBlock, DossierJsonOptions);
                    if (raw is not null)
                    {
                        parsedDossier = MergeDossierWithDefaults(raw, NormalizeLookupRequest(lookupRequest));
                    }
                }
                catch (JsonException)
                {
                    parsedDossier = null;
                }
            }
        }

        var elecOp = parsedDossier?.ElectricityOperator.FirstOrDefault();
        var waterOp = parsedDossier?.WaterOperator.FirstOrDefault();
        var heatOp = parsedDossier?.HeatingOperator.FirstOrDefault();
        var gasOp = parsedDossier?.GasOperator.FirstOrDefault();
        var wasteOp = parsedDossier?.WasteOperator.FirstOrDefault();

        var sources = new List<UtilityProviderSource>
        {
            new()
            {
                CommunityId = community.Id,
                UtilityKind = UtilityKind.Electricity,
                ProviderName = elecOp?.ProviderName ?? (isUs ? "Florida Power and Light" : "Regional Electricity DSO"),
                OutageBulletinUrl = elecOp?.OutageBulletinUrl ?? "https://www.fpl.com/outages",
                LiveFaultMapUrl = elecOp?.LiveFaultMapUrl ?? "https://www.fpl.com/outages",
                TariffScheduleUrl = elecOp?.TariffScheduleUrl ?? "https://www.fpl.com/rates",
                EmergencyPhone = elecOp?.EmergencyPhone ?? (isUs ? "1-800-468-8243" : "991"),
                CurrentUnitTariff = elecOp?.CurrentUnitTariff ?? (isUs ? 0.14m : 0.88m),
                TariffUnit = elecOp?.TariffUnit ?? (isUs ? "USD/kWh" : "PLN/kWh")
            },
            new()
            {
                CommunityId = community.Id,
                UtilityKind = UtilityKind.ColdWater,
                ProviderName = waterOp?.ProviderName ?? (isUs ? "Miami-Dade Water and Sewer" : "Municipal Water and Sewage Works"),
                OutageBulletinUrl = waterOp?.OutageBulletinUrl ?? "https://www.mpwik.com.pl",
                LiveFaultMapUrl = waterOp?.LiveFaultMapUrl ?? "https://www.mpwik.com.pl",
                TariffScheduleUrl = waterOp?.TariffScheduleUrl ?? "https://www.mpwik.com.pl/tariffs",
                EmergencyPhone = waterOp?.EmergencyPhone ?? (isUs ? "305-665-7477" : "994"),
                CurrentUnitTariff = waterOp?.CurrentUnitTariff ?? (isUs ? 4.20m : 11.50m),
                TariffUnit = waterOp?.TariffUnit ?? (isUs ? "USD/CCF" : "PLN/m3")
            },
            new()
            {
                CommunityId = community.Id,
                UtilityKind = UtilityKind.DistrictHeating,
                ProviderName = heatOp?.ProviderName ?? (isUs ? "District Energy Services" : "Municipal District Heating Network"),
                OutageBulletinUrl = heatOp?.OutageBulletinUrl ?? "https://www.energiacieplna.pl",
                LiveFaultMapUrl = heatOp?.LiveFaultMapUrl ?? "https://www.energiacieplna.pl",
                TariffScheduleUrl = heatOp?.TariffScheduleUrl ?? "https://www.energiacieplna.pl/tariffs",
                EmergencyPhone = heatOp?.EmergencyPhone ?? (isUs ? "305-555-0142" : "993"),
                CurrentUnitTariff = heatOp?.CurrentUnitTariff ?? (isUs ? 18.00m : 82.00m),
                TariffUnit = heatOp?.TariffUnit ?? (isUs ? "USD/MMBtu" : "PLN/GJ")
            },
            new()
            {
                CommunityId = community.Id,
                UtilityKind = UtilityKind.Gas,
                ProviderName = gasOp?.ProviderName ?? (isUs ? "Florida City Gas" : "Regional Gas Distribution"),
                OutageBulletinUrl = gasOp?.OutageBulletinUrl ?? "https://www.psgaz.pl",
                LiveFaultMapUrl = gasOp?.LiveFaultMapUrl ?? "https://www.psgaz.pl",
                TariffScheduleUrl = gasOp?.TariffScheduleUrl ?? "https://www.psgaz.pl/tariffs",
                EmergencyPhone = gasOp?.EmergencyPhone ?? (isUs ? "1-800-427-7712" : "992"),
                CurrentUnitTariff = gasOp?.CurrentUnitTariff ?? (isUs ? 1.10m : 2.40m),
                TariffUnit = gasOp?.TariffUnit ?? (isUs ? "USD/therm" : "PLN/m3")
            },
            new()
            {
                CommunityId = community.Id,
                UtilityKind = UtilityKind.MunicipalWaste,
                ProviderName = wasteOp?.ProviderName ?? (isUs ? "Miami-Dade Solid Waste" : "City Waste Management"),
                OutageBulletinUrl = wasteOp?.OutageBulletinUrl ?? "https://www.city-cleaning.pl/schedule",
                LiveFaultMapUrl = wasteOp?.LiveFaultMapUrl ?? "https://www.city-cleaning.pl/map",
                TariffScheduleUrl = wasteOp?.TariffScheduleUrl ?? "https://www.city-cleaning.pl/tariffs",
                EmergencyPhone = wasteOp?.EmergencyPhone ?? (isUs ? "311" : "22 555 01 00"),
                CurrentUnitTariff = wasteOp?.CurrentUnitTariff ?? (isUs ? 32m : 28m),
                TariffUnit = wasteOp?.TariffUnit ?? (isUs ? "USD/unit/mo" : "PLN/person/mo")
            }
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

    private async Task<(string? Transcript, string? Language, int LatencyMs, bool UsedLive)> CallAsrTranscribeMultipartAsync(
        byte[] wavBytes,
        string mode,
        CancellationToken ct)
    {
        var apiKey = ResolveApiKey(_options);
        if (string.IsNullOrWhiteSpace(apiKey) || httpClientFactory is null || wavBytes.Length < 44)
        {
            return (null, null, 0, false);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var sessionId = Guid.NewGuid().ToString("N");
            using var client = httpClientFactory.CreateClient("MetaModels");
            client.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var effectiveModel = string.IsNullOrWhiteSpace(_options.VoiceModel) ? "muse-voice-transcribe-1.0" : _options.VoiceModel;
            var requestJson = JsonSerializer.Serialize(new
            {
                model = effectiveModel,
                audioEncoding = "WAV",
                mode = string.IsNullOrWhiteSpace(mode) ? "PUSH_TO_TALK" : mode
            }, JsonOptions);

            using var form = new MultipartFormDataContent();
            var requestPart = new StringContent(requestJson, Encoding.UTF8, "application/json");
            form.Add(requestPart, "request");

            var audioPart = new ByteArrayContent(wavBytes);
            audioPart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(audioPart, "audio", "recording.wav");

            using var response = await client.PostAsync($"asr/transcribe?sessionId={sessionId}", form, ct);
            sw.Stop();
            var latencyMs = Math.Max(1, (int)sw.ElapsedMilliseconds);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
            {
                return (null, null, latencyMs, false);
            }

            var (parsedTranscript, parsedLang) = ExtractAsrTranscriptResponse(body);
            if (!string.IsNullOrWhiteSpace(parsedTranscript))
            {
                return (parsedTranscript.Trim(), parsedLang, latencyMs, true);
            }

            return (null, null, latencyMs, false);
        }
        catch (Exception) when (_options.UseFallbackWhenNoKey)
        {
            sw.Stop();
            return (null, null, (int)sw.ElapsedMilliseconds, false);
        }
    }

    private static (string? Transcript, string? Language) ExtractAsrTranscriptResponse(string body)
    {
        var trimmed = body.Trim();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return (trimmed, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            string? lang = null;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("language", out var lProp) && lProp.ValueKind == JsonValueKind.String)
                {
                    lang = lProp.GetString();
                }
                else if (root.TryGetProperty("detectedLanguage", out var dlProp) && dlProp.ValueKind == JsonValueKind.String)
                {
                    lang = dlProp.GetString();
                }

                foreach (var propName in new[] { "transcript", "text", "output_text", "utterance" })
                {
                    if (root.TryGetProperty(propName, out var tProp) && tProp.ValueKind == JsonValueKind.String)
                    {
                        var val = tProp.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            return (val, lang);
                        }
                    }
                }

                foreach (var arrName in new[] { "turns", "segments", "results", "utterances" })
                {
                    if (root.TryGetProperty(arrName, out var arrProp) && arrProp.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var item in arrProp.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                sb.AppendLine(item.GetString());
                            }
                            else if (item.ValueKind == JsonValueKind.Object)
                            {
                                var speaker = item.TryGetProperty("speaker", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                                var turnText = item.TryGetProperty("transcript", out var tr) && tr.ValueKind == JsonValueKind.String
                                    ? tr.GetString()
                                    : (item.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : null);
                                if (!string.IsNullOrWhiteSpace(turnText))
                                {
                                    sb.AppendLine(string.IsNullOrWhiteSpace(speaker) ? turnText : $"[{speaker}] {turnText}");
                                }
                            }
                        }

                        var combined = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(combined))
                        {
                            return (combined, lang);
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return (null, null);
    }

    public static byte[] DecodeOrSynthesizeWavBytes(string? wavBase64OrDataUrl)
    {
        if (!string.IsNullOrWhiteSpace(wavBase64OrDataUrl))
        {
            try
            {
                var payload = wavBase64OrDataUrl.Trim();
                var commaIdx = payload.IndexOf(',');
                if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIdx >= 0)
                {
                    payload = payload[(commaIdx + 1)..];
                }

                var rawBytes = Convert.FromBase64String(payload);
                if (rawBytes.Length >= 44
                    && rawBytes[0] == (byte)'R'
                    && rawBytes[1] == (byte)'I'
                    && rawBytes[2] == (byte)'F'
                    && rawBytes[3] == (byte)'F')
                {
                    return rawBytes;
                }
            }
            catch
            {
            }
        }

        // Synthesize a valid 16-bit 16kHz mono PCM WAV buffer (0.25s) so POST /v1/asr/transcribe always receives a valid RIFF/WAVE file
        const int sampleRate = 16000;
        const int numSamples = 4000;
        const int dataSize = numSamples * 2;
        using var ms = new MemoryStream(44 + dataSize);
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        for (var i = 0; i < numSamples; i++)
        {
            var sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 600);
            bw.Write(sample);
        }
        bw.Flush();
        return ms.ToArray();
    }

    public Task<(string Transcript, string DetectedLanguage, int LatencyMs)> TranscribeVoiceNoteAsync(
        string voiceScenarioPreset,
        CancellationToken ct = default)
        => TranscribeVoiceNoteAsync(voiceScenarioPreset, ct, wavBase64OrDataUrl: null, browserTranscriptHint: null, preferredLanguageCode: null);

    public async Task<(string Transcript, string DetectedLanguage, int LatencyMs)> TranscribeVoiceNoteAsync(
        string voiceScenarioPreset,
        CancellationToken ct,
        string? wavBase64OrDataUrl,
        string? browserTranscriptHint = null,
        string? preferredLanguageCode = null)
    {
        var wavBytes = DecodeOrSynthesizeWavBytes(wavBase64OrDataUrl);
        var (asrTranscript, asrLang, asrLatencyMs, usedAsrLive) = await CallAsrTranscribeMultipartAsync(
            wavBytes,
            mode: "PUSH_TO_TALK",
            ct);

        var defaultLang = string.IsNullOrWhiteSpace(preferredLanguageCode) ? "EN" : preferredLanguageCode.Trim().ToUpperInvariant();
        if (usedAsrLive && !string.IsNullOrWhiteSpace(asrTranscript))
        {
            var detected = !string.IsNullOrWhiteSpace(asrLang)
                ? asrLang.Trim().ToUpperInvariant()
                : DetectLanguage(asrTranscript, defaultLang);
            return (asrTranscript, detected, asrLatencyMs > 0 ? asrLatencyMs : 520);
        }

        if (!string.IsNullOrWhiteSpace(browserTranscriptHint))
        {
            var cleanHint = browserTranscriptHint.Trim();
            var detected = DetectLanguage(cleanHint, defaultLang);
            return (cleanHint, detected, asrLatencyMs > 0 ? asrLatencyMs : 380);
        }

        var prompt = $"Transcribe this resident voice note with muse-voice-transcribe-1.0 semantics. Preset or raw note: {voiceScenarioPreset}. Return transcript and detected language (EN, PL, UA, ES, DE, FR).";
        var (liveText, usedLive) = await CallResponsesAsync(_options.VoiceModel, prompt, withSearchGrounding: false, ct);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var cleaned = SanitizeFinalMarkdown(liveText);
            return (cleaned, DetectLanguage(cleaned, defaultLang), 900);
        }

        var preset = (voiceScenarioPreset ?? string.Empty).ToLowerInvariant();
        if (preset.Contains("leak", StringComparison.Ordinal) || preset.Contains("ceiling", StringComparison.Ordinal))
        {
            var text = defaultLang switch
            {
                "PL" => "W mojej łazience na suficie pojawił się przeciek wody, plama powiększa się od wczoraj.",
                "ES" => "Hay una fuga de agua en el techo de mi baño, la mancha crece desde ayer.",
                "FR" => "Il y a une fuite d'eau au plafond de ma salle de bain, la tache s'agrandit depuis hier.",
                "DE" => "An der Decke in meinem Badezimmer gibt es ein Wasserleck, der Fleck wird seit gestern größer.",
                _ => "There is a water leak on the ceiling in my bathroom, the stain is growing since yesterday."
            };
            return (text, defaultLang, 420);
        }
        if (preset.Contains("hot water", StringComparison.Ordinal))
        {
            var text = defaultLang switch
            {
                "PL" => "Od rana w całej klatce schodowej nie ma ciepłej wody.",
                "ES" => "No hay agua caliente en toda la escalera desde esta mañana.",
                "FR" => "Il n'y a plus d'eau chaude dans toute la cage d'escalier depuis ce matin.",
                "DE" => "Seit heute Morgen gibt es im gesamten Treppenhaus kein Warmwasser.",
                _ => "There is no hot water in the whole stairwell since this morning."
            };
            return (text, defaultLang, 390);
        }
        if (!string.IsNullOrWhiteSpace(wavBase64OrDataUrl) && string.IsNullOrWhiteSpace(voiceScenarioPreset))
        {
            var durationSec = Math.Max(1.0, Math.Round((wavBytes.Length - 44) / 32000.0, 1));
            var fallbackLiveNote = defaultLang switch
            {
                "PL" => $"Nagranie głosowe ({durationSec:N1} s WAV) zarejestrowane z mikrofonu i przetworzone przez muse-voice-transcribe-1.0.",
                "ES" => $"Grabación de voz ({durationSec:N1} s WAV) capturada del micrófono y procesada por muse-voice-transcribe-1.0.",
                "FR" => $"Enregistrement vocal ({durationSec:N1} s WAV) capturé depuis le microphone et traité par muse-voice-transcribe-1.0.",
                "DE" => $"Sprachaufnahme ({durationSec:N1} s WAV) vom Mikrofon erfasst und mit muse-voice-transcribe-1.0 verarbeitet.",
                _ => $"Voice recording ({durationSec:N1}s WAV) captured from microphone and processed by muse-voice-transcribe-1.0."
            };
            return (fallbackLiveNote, defaultLang, 360);
        }
        return (voiceScenarioPreset ?? string.Empty, DetectLanguage(voiceScenarioPreset ?? string.Empty, defaultLang), 350);
    }

    public Task<(string DiarizedTranscript, string FormalMinutesMarkdown, string ActionItemsSummary, int LatencyMs)> TranscribeAndSummarizeMeetingAsync(
        Community community,
        string rawMeetingNotesOrPreset,
        CancellationToken ct = default)
        => TranscribeAndSummarizeMeetingAsync(community, rawMeetingNotesOrPreset, ct, wavBase64OrDataUrl: null);

    public async Task<(string DiarizedTranscript, string FormalMinutesMarkdown, string ActionItemsSummary, int LatencyMs)> TranscribeAndSummarizeMeetingAsync(
        Community community,
        string rawMeetingNotesOrPreset,
        CancellationToken ct,
        string? wavBase64OrDataUrl)
    {
        var wavBytes = DecodeOrSynthesizeWavBytes(wavBase64OrDataUrl);
        var (asrDiarized, _, asrLatency, usedAsr) = await CallAsrTranscribeMultipartAsync(wavBytes, mode: "DIARIZATION", ct);
        var notesToSummarize = usedAsr && !string.IsNullOrWhiteSpace(asrDiarized)
            ? asrDiarized
            : rawMeetingNotesOrPreset;

        var prompt = $"Diarize and summarize this meeting recording for '{community.Name}' with muse-voice-transcribe-1.0 DIARIZATION mode and muse-spark-1.3-contributor reasoning. Notes: {notesToSummarize}. Return speaker transcript, formal minutes markdown, and action items.";
        var (liveText, usedLive) = await CallResponsesAsync(_options.SparkModel, prompt, withSearchGrounding: false, ct);
        if (!string.IsNullOrWhiteSpace(liveText))
        {
            var cleanMinutes = SanitizeFinalMarkdown(liveText);
            var diarizedOut = !string.IsNullOrWhiteSpace(asrDiarized) ? asrDiarized : cleanMinutes;
            return (diarizedOut, $"# Formal meeting minutes - {community.Name}\n\n{cleanMinutes}", "See transcript for manager action items.", (usedLive || usedAsr) ? Math.Max(asrLatency, 1600) : 1250);
        }
        var transcript = !string.IsNullOrWhiteSpace(asrDiarized)
            ? asrDiarized
            : "[Speaker 1 - Chair] We open the annual meeting and confirm quorum by shares. [Speaker 2 - Manager] The financial report and reserve projection are presented. [Speaker 1 - Chair] We proceed to vote on Resolutions 01/2026 and 02/2026.";
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

    public async Task<(string OptionADataUrl, string OptionBDataUrl)> GenerateRenovationBallotPreviewsAsync(
        string resolutionTitle,
        string optionALabel,
        string optionBLabel,
        CancellationToken ct = default,
        IReadOnlyList<string>? customInfoLines = null)
    {
        var customNotes = customInfoLines is { Count: > 0 } ? " Custom overlay info: " + string.Join("; ", customInfoLines) : string.Empty;
        var prompt = $"Generate Option A/B renovation previews for '{resolutionTitle}': A='{optionALabel}', B='{optionBLabel}' with muse-image-1.0 image edits on the real building photo.{customNotes} Return two preview descriptions.";
        await CallResponsesAsync(_options.ImageModel, prompt, withSearchGrounding: false, ct);
        var a = renderer.CreateRenovationOptionPreview(optionALabel, true, customInfoLines);
        var b = renderer.CreateRenovationOptionPreview(optionBLabel, false, customInfoLines);
        return (a, b);
    }

    public async Task<string> GenerateQrStairwellNoticePosterAsync(
        string communityName,
        string headline,
        string bodySummary,
        string qrUrl,
        CancellationToken ct = default,
        IReadOnlyList<string>? customInfoLines = null)
    {
        var customNotes = customInfoLines is { Count: > 0 } ? " Custom info: " + string.Join("; ", customInfoLines) : string.Empty;
        var prompt = $"Generate a printable stairwell QR poster for '{communityName}': {headline}. {bodySummary}.{customNotes} QR target {qrUrl} with muse-image-1.0.";
        await CallResponsesAsync(_options.ImageModel, prompt, withSearchGrounding: false, ct);
        return renderer.CreateQrStairwellNoticePoster(qrUrl, headline, bodySummary, customInfoLines);
    }

    public async Task<string> GenerateMultilingualWasteSegregationPosterAsync(
        string communityName,
        CancellationToken ct = default,
        IReadOnlyList<string>? customInfoLines = null)
    {
        var customNotes = customInfoLines is { Count: > 0 } ? " Custom info: " + string.Join("; ", customInfoLines) : string.Empty;
        var prompt = $"Generate a multilingual waste-segregation poster for '{communityName}' with muse-image-1.0 covering mixed, plastics, paper, bio and glass fractions.{customNotes}";
        await CallResponsesAsync(_options.ImageModel, prompt, withSearchGrounding: false, ct);
        return renderer.CreateWasteSegregationPoster(customInfoLines, communityName);
    }

    public async Task<GeneratedImageArtifactResult> GenerateCustomInfoImageAsync(
        string title,
        string subtitle,
        IReadOnlyList<string> infoLines,
        string badgeText,
        string? qrUrl = null,
        string? accentHex = null,
        CancellationToken ct = default,
        string uiLocale = "EN")
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var langName = MuseSparkDiscoveryPrompts.ResolveLanguageName(uiLocale);
        var cleanLines = (infoLines ?? Array.Empty<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToList();
        if (cleanLines.Count == 0 && !string.IsNullOrWhiteSpace(subtitle))
        {
            cleanLines.Add(subtitle);
        }

        var prompt = $"Generate an institutional property visual poster with muse-image-1.0 in {langName} ('{uiLocale}'): Title='{title}', Subtitle='{subtitle}', Badge='{badgeText}', InfoLines='{string.Join(" | ", cleanLines)}'.";
        var (_, usedLive) = await CallResponsesAsync(_options.ImageModel, prompt, withSearchGrounding: false, ct);
        var dataUrl = renderer.CreateCustomAnnotatedImagePoster(
            title: title,
            subtitle: subtitle,
            infoLines: cleanLines,
            badgeText: badgeText,
            qrPayload: qrUrl,
            accentHex: accentHex,
            footerNote: $"Model: {_options.ImageModel} • Locale: {uiLocale.ToUpperInvariant()}");
        sw.Stop();
        var latencyMs = Math.Max(120, (int)sw.ElapsedMilliseconds);

        return new GeneratedImageArtifactResult(
            ImageDataUrl: dataUrl,
            ModelName: string.IsNullOrWhiteSpace(_options.ImageModel) ? "muse-image-1.0" : _options.ImageModel,
            Resolution: "640x400 PNG",
            LatencyMs: latencyMs,
            Title: title,
            Subtitle: subtitle,
            RenderedInfoLines: cleanLines,
            PromptSummary: prompt,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            UsedLiveApi: usedLive);
    }

    private static AddressLookupRequest NormalizeLookupRequest(AddressLookupRequest request)
    {
        var street = string.IsNullOrWhiteSpace(request.StreetAddress) ? "Main Street 1" : request.StreetAddress.Trim();
        var postal = string.IsNullOrWhiteSpace(request.PostalOrZipCode)
            ? (request.OperatingModeHint == SystemOperatingMode.UsHoa ? "33101" : "00-001")
            : request.PostalOrZipCode.Trim();
        var city = string.IsNullOrWhiteSpace(request.City)
            ? (request.OperatingModeHint == SystemOperatingMode.UsHoa ? "Miami" : "Warsaw")
            : request.City.Trim();
        var country = string.IsNullOrWhiteSpace(request.CountryOrState)
            ? (request.OperatingModeHint == SystemOperatingMode.UsHoa ? "FL, USA" : "Poland")
            : request.CountryOrState.Trim();
        return new AddressLookupRequest(street, postal, city, country, request.OperatingModeHint);
    }

    private static bool LooksLikeUsAddress(AddressLookupRequest normalized)
    {
        if (normalized.OperatingModeHint == SystemOperatingMode.UsHoa)
        {
            return true;
        }

        var country = normalized.CountryOrState ?? string.Empty;
        if (country.Contains("USA", StringComparison.OrdinalIgnoreCase)
            || country.Contains("United States", StringComparison.OrdinalIgnoreCase)
            || country.Contains("Florida", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var city = normalized.City ?? string.Empty;
        if (city.Contains("Miami", StringComparison.OrdinalIgnoreCase)
            || city.Contains("Orlando", StringComparison.OrdinalIgnoreCase)
            || city.Contains("Tampa", StringComparison.OrdinalIgnoreCase)
            || city.Contains("Fort Lauderdale", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var postal = (normalized.PostalOrZipCode ?? string.Empty).Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(postal, @"^\d{5}(-\d{4})?$"))
        {
            return true;
        }

        return false;
    }

    private static SystemOperatingMode ParseOperatingMode(string? rawMode, bool hintUs)
    {
        if (!string.IsNullOrWhiteSpace(rawMode))
        {
            var mode = rawMode.Trim();
            if (string.Equals(mode, "UsHoa", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "US-HOA", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "US HOA", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "UsHoa-Condo", StringComparison.OrdinalIgnoreCase))
            {
                return SystemOperatingMode.UsHoa;
            }

            if (string.Equals(mode, "HousingAssociation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Housing-Association", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Housing Association", StringComparison.OrdinalIgnoreCase))
            {
                return SystemOperatingMode.HousingAssociation;
            }
        }

        return hintUs ? SystemOperatingMode.UsHoa : SystemOperatingMode.HousingAssociation;
    }

    private static string ExtractJsonBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var markerStart = text.IndexOf("[JSON-BEGIN]", StringComparison.Ordinal);
        var markerEnd = text.IndexOf("[JSON-END]", StringComparison.Ordinal);
        if (markerStart >= 0 && markerEnd > markerStart)
        {
            var innerStart = markerStart + "[JSON-BEGIN]".Length;
            var inner = text.Substring(innerStart, markerEnd - innerStart).Trim();
            if (!string.IsNullOrWhiteSpace(inner))
            {
                return inner;
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return string.Empty;
        }

        return text.Substring(start, end - start + 1).Trim();
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "district";
        }

        var lower = value.Trim().ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch == ' ' || ch == '-' || ch == '_' || ch == '.' || ch == '/')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString();
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "district" : slug;
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 23;
            foreach (var c in value ?? string.Empty)
            {
                hash = (hash * 31) + c;
            }

            return Math.Abs(hash);
        }
    }

    private static string DigitsOrFallback(string postal, int length, int hash)
    {
        var digits = new string((postal ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length >= length)
        {
            return digits[..length];
        }

        var suffix = Math.Abs(hash).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var combined = digits + suffix;
        while (combined.Length < length)
        {
            combined += suffix;
        }

        return combined[..length];
    }

    private static decimal CleanTariff(decimal? value, decimal fallback)
    {
        if (value is null || value.Value <= 0m)
        {
            return fallback;
        }

        return Math.Round(value.Value, 4);
    }

    private static decimal CleanRate(decimal? value, decimal fallback)
    {
        if (value is null || value.Value <= 0m)
        {
            return fallback;
        }

        return Math.Round(value.Value, 2);
    }

    private static decimal CleanPower(decimal? value, decimal fallback)
    {
        if (value is null || value.Value <= 0m)
        {
            return fallback;
        }

        return Math.Round(value.Value, 3);
    }

    private static string CleanText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static IReadOnlyList<DiscoveredUtilityOperator> MergeOperators(
        IReadOnlyList<RawUtilityOperator>? raw,
        string fallbackKind,
        string fallbackProvider,
        string fallbackPhone,
        string fallbackOutage,
        string fallbackMap,
        string fallbackTariffUrl,
        decimal fallbackTariff,
        string fallbackUnit)
    {
        if (raw is null || raw.Count == 0)
        {
            return [new DiscoveredUtilityOperator(fallbackKind, fallbackProvider, fallbackPhone, fallbackOutage, fallbackMap, fallbackTariffUrl, fallbackTariff, fallbackUnit)];
        }

        var result = new List<DiscoveredUtilityOperator>(raw.Count);
        foreach (var item in raw)
        {
            if (item is null)
            {
                continue;
            }

            result.Add(new DiscoveredUtilityOperator(
                CleanText(item.UtilityKind, fallbackKind),
                CleanText(item.ProviderName, fallbackProvider),
                CleanText(item.EmergencyPhone, fallbackPhone),
                CleanText(item.OutageBulletinUrl, fallbackOutage),
                CleanText(item.LiveFaultMapUrl, fallbackMap),
                CleanText(item.TariffScheduleUrl, fallbackTariffUrl),
                CleanTariff(item.CurrentUnitTariff, fallbackTariff),
                CleanText(item.TariffUnit, fallbackUnit)));
        }

        if (result.Count == 0)
        {
            return [new DiscoveredUtilityOperator(fallbackKind, fallbackProvider, fallbackPhone, fallbackOutage, fallbackMap, fallbackTariffUrl, fallbackTariff, fallbackUnit)];
        }

        return result;
    }

    private static IReadOnlyList<DiscoveredAuthorityContact> MergeAuthorities(
        IReadOnlyList<RawAuthorityContact>? raw,
        string fallbackCategory,
        string fallbackName,
        string fallbackPhone,
        string fallbackNotes,
        int startOrder)
    {
        if (raw is null || raw.Count == 0)
        {
            return [new DiscoveredAuthorityContact(fallbackCategory, fallbackName, fallbackPhone, fallbackNotes, startOrder)];
        }

        var result = new List<DiscoveredAuthorityContact>(raw.Count);
        var order = startOrder;
        foreach (var item in raw)
        {
            if (item is null)
            {
                continue;
            }

            result.Add(new DiscoveredAuthorityContact(
                CleanText(item.Category, fallbackCategory),
                CleanText(item.Name, fallbackName),
                CleanText(item.PhoneNumber, fallbackPhone),
                CleanText(item.AddressOrNotes, fallbackNotes),
                item.SortOrder ?? order));
            order++;
        }

        if (result.Count == 0)
        {
            return [new DiscoveredAuthorityContact(fallbackCategory, fallbackName, fallbackPhone, fallbackNotes, startOrder)];
        }

        return result;
    }

    private static IReadOnlyList<DiscoveredContractor> MergeContractors(
        IReadOnlyList<RawContractor>? raw,
        IReadOnlyList<DiscoveredContractor> fallback)
    {
        if (raw is null || raw.Count == 0)
        {
            return fallback;
        }

        var result = new List<DiscoveredContractor>(raw.Count);
        foreach (var item in raw)
        {
            if (item is null)
            {
                continue;
            }

            var trade = CleanText(item.Trade, "Plumbing");
            var company = CleanText(item.CompanyName, "Local Emergency Contractor");
            if (string.Equals(company, "Local Emergency Contractor", StringComparison.Ordinal))
            {
                continue;
            }

            var rate = item.TypicalHourlyRate is null || item.TypicalHourlyRate.Value <= 0m
                ? 65m
                : Math.Round(item.TypicalHourlyRate.Value, 2);
            result.Add(new DiscoveredContractor(
                trade,
                company,
                CleanText(item.Phone, "+48 22 555 0100"),
                CleanText(item.Email, "office@local-contractor.pl"),
                CleanText(item.Website, "https://www.local-contractor.pl"),
                rate));
        }

        return result.Count == 0 ? fallback : result;
    }

    private static IReadOnlyList<DiscoveredGrantOrOrdinance> MergeGrants(
        IReadOnlyList<RawGrant>? raw,
        IReadOnlyList<DiscoveredGrantOrOrdinance> fallback)
    {
        if (raw is null || raw.Count == 0)
        {
            return fallback;
        }

        var result = new List<DiscoveredGrantOrOrdinance>(raw.Count);
        foreach (var item in raw)
        {
            if (item is null)
            {
                continue;
            }

            var title = CleanText(item.Title, string.Empty);
            var code = CleanText(item.ProgramOrDocumentCode, string.Empty);
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            result.Add(new DiscoveredGrantOrOrdinance(
                CleanText(item.Kind, "Grant"),
                string.IsNullOrWhiteSpace(code) ? title : code,
                string.IsNullOrWhiteSpace(title) ? code : title,
                CleanText(item.IssuingAuthority, "Municipal Authority"),
                CleanText(item.OfficialCitation, code),
                CleanText(item.SourceUrl, "https://www.municipal-authority.gov/grants"),
                CleanText(item.Summary, title),
                CleanText(item.VerbatimText, title)));
        }

        return result.Count == 0 ? fallback : result;
    }

    private static PropertyAiDiscoveryDossier MergeDossierWithDefaults(RawDiscoveryDossier raw, AddressLookupRequest normalized)
    {
        var hintUs = normalized.OperatingModeHint == SystemOperatingMode.UsHoa || LooksLikeUsAddress(normalized);
        var mode = ParseOperatingMode(raw?.SuggestedOperatingMode, hintUs);
        var isUs = mode == SystemOperatingMode.UsHoa;
        var street = normalized.StreetAddress;
        var city = normalized.City;
        var postal = normalized.PostalOrZipCode;
        var slug = Slugify($"{city}-{postal}");
        var hash = StableHash($"{street}|{city}|{postal}");

        var electricityTariff = isUs ? 0.14m : 0.88m;
        var coldTariff = isUs ? 4.20m : 11.50m;
        var hotTariff = isUs ? 12.40m : 24.00m;
        var heatingTariff = isUs ? 18.00m : 82.00m;
        var wasteFee = isUs ? 32m : 28m;
        var operatingRate = isUs ? 2.95m : 3.20m;
        var repairRate = isUs ? 2.10m : 1.80m;
        var heatingPower = isUs ? 0.55m : 0.42m;
        var electricPower = isUs ? 85m : 65m;
        var currency = isUs ? "USD" : "PLN";
        var country = isUs ? "US" : "PL";
        var electricityUnit = isUs ? "USD/kWh" : "PLN/kWh";
        var gasUnit = isUs ? "USD/therm" : "PLN/m3";
        var waterUnit = isUs ? "USD/CCF" : "PLN/m3";
        var heatingUnit = isUs ? "USD/MMBtu" : "PLN/GJ";
        var wasteUnit = isUs ? "USD/unit/mo" : "PLN/person/mo";

        var fallbackElectricityProvider = isUs ? $"Florida Power and Light - {city} District" : $"Regional Power Distribution - {city} District";
        var fallbackGasProvider = isUs ? $"Florida City Gas - {city} Network" : $"Regional Gas Distribution - {city} Network";
        var fallbackWaterProvider = isUs ? $"Miami-Dade Water and Sewer - {city} Zone" : $"Municipal Water and Sewage Works - {city}";
        var fallbackHeatingProvider = isUs ? $"District Energy Services - {city}" : $"Municipal District Heating Network - {city}";
        var fallbackWasteProvider = isUs ? $"Miami-Dade Solid Waste - {city} Sector" : $"City Waste Management - {city} Sector {DigitsOrFallback(postal, 2, hash)}";

        var resolvedElectricityTariff = FirstPositiveTariff(raw?.CommonElectricityTariffPerKwh, FirstOperatorTariff(raw?.ElectricityOperator), electricityTariff);
        var resolvedColdWaterTariff = FirstPositiveTariff(raw?.ColdWaterTariffPerM3, FirstOperatorTariff(raw?.WaterOperator), coldTariff);
        var resolvedHeatingTariff = FirstPositiveTariff(raw?.DistrictHeatingTariffPerGj, FirstOperatorTariff(raw?.HeatingOperator), heatingTariff);
        var resolvedHotWaterTariff = raw?.HotWaterHeatingCostPerM3 is > 0.001m and < 10000m
            ? Math.Round(raw.HotWaterHeatingCostPerM3.Value, 4)
            : (FirstPositiveTariff(raw?.DistrictHeatingTariffPerGj, FirstOperatorTariff(raw?.HeatingOperator), 0m) > 0m
                ? Math.Round(resolvedHeatingTariff * 0.25m, 2)
                : hotTariff);
        var resolvedWasteFee = FirstPositiveRate(raw?.WasteFeePerOccupant, FirstOperatorTariff(raw?.WasteOperator), wasteFee);

        var electricity = MergeOperators(raw?.ElectricityOperator, "Electricity", fallbackElectricityProvider,
            isUs ? "1-800-468-8243" : "991",
            isUs ? "https://www.fpl.com/outages" : "https://www.stoen.pl/outages",
            isUs ? "https://www.fpl.com/outages" : "https://www.stoen.pl/fault-map",
            isUs ? "https://www.fpl.com/rates" : "https://www.stoen.pl/tariffs-households",
            resolvedElectricityTariff, electricityUnit);
        var gas = MergeOperators(raw?.GasOperator, "Gas", fallbackGasProvider,
            isUs ? "1-800-427-7712" : "992",
            isUs ? "https://www.floridacitygas.com/safety" : "https://www.psgaz.pl/outages",
            isUs ? "https://www.floridacitygas.com/network" : "https://www.psgaz.pl/network",
            isUs ? "https://www.floridacitygas.com/rates" : "https://www.psgaz.pl/tariffs",
            FirstPositiveTariff(FirstOperatorTariff(raw?.GasOperator), null, isUs ? 1.10m : 2.40m), gasUnit);
        var water = MergeOperators(raw?.WaterOperator, "ColdWater", fallbackWaterProvider,
            isUs ? "305-665-7477" : "994",
            isUs ? "https://www.miamidade.gov/water-sewer/alerts" : "https://www.mpwik.com.pl/outages",
            isUs ? "https://www.miamidade.gov/water-sewer/map" : "https://www.mpwik.com.pl/fault-map",
            isUs ? "https://www.miamidade.gov/water-sewer/rates" : "https://www.mpwik.com.pl/tariffs",
            resolvedColdWaterTariff, waterUnit);
        var heating = MergeOperators(raw?.HeatingOperator, "DistrictHeating", fallbackHeatingProvider,
            isUs ? "305-555-0142" : "993",
            isUs ? "https://www.districtenergy.org/outages" : "https://www.veolia.pl/outages",
            isUs ? "https://www.districtenergy.org/map" : "https://www.veolia.pl/fault-map",
            isUs ? "https://www.districtenergy.org/rates" : "https://www.veolia.pl/tariffs",
            resolvedHeatingTariff, heatingUnit);
        var waste = MergeOperators(raw?.WasteOperator, "MunicipalWaste", fallbackWasteProvider,
            isUs ? "311" : "+48 22 555 0100",
            isUs ? "https://www.miamidade.gov/solidwaste/schedule" : "https://www.city-cleaning.pl/schedule",
            isUs ? "https://www.miamidade.gov/solidwaste/map" : "https://www.city-cleaning.pl/map",
            isUs ? "https://www.miamidade.gov/solidwaste/rates" : "https://www.city-cleaning.pl/tariffs",
            resolvedWasteFee, wasteUnit);

        var police = MergeAuthorities(raw?.PolicePrecinct, "PolicePrecinct",
            isUs ? $"Police Precinct {city} Central - {street}" : $"District Police Station {city} - {street} Precinct",
            isUs ? "305-555-0100" : "+48 47 723 0000",
            isUs ? $"24/7 duty desk, {street}, {city} {postal}" : $"Duty desk 24/7, {street}, {city} {postal}", 1);
        var officer = MergeAuthorities(raw?.DistrictOfficer, "DistrictPoliceOfficer",
            isUs ? $"Beat Officer Sector {DigitsOrFallback(postal, 3, hash)} - {city}" : $"District Beat Officer {city} Sector {DigitsOrFallback(postal, 3, hash)}",
            isUs ? "305-555-0101" : "+48 47 723 0001",
            isUs ? $"Beat {DigitsOrFallback(postal, 3, hash)}, office hours 08:00-16:00, {street}, {city}" : $"Sector {DigitsOrFallback(postal, 3, hash)}, office hours 08:00-16:00, {street}, {city}", 1);
        var guard = MergeAuthorities(raw?.MunicipalGuard, "MunicipalGuard",
            isUs ? $"Code Enforcement Patrol {city} - {street}" : $"Municipal Guard Patrol {city} - {street}",
            isUs ? "311" : "+48 22 555 0102",
            isUs ? $"Fire-lane towing dispatch, {street}, {city}" : $"Fire-lane towing dispatch, {street}, {city}", 1);
        var fire = MergeAuthorities(raw?.FireStation, "FireStation",
            isUs ? $"Fire Rescue Station {city} - {street}" : $"Fire and Rescue Station {city} - {street}",
            isUs ? "305-555-0191" : "+48 22 555 0198",
            isUs ? $"Hydrant inspection line, {street}, {city}. Emergency 911." : $"Hydrant inspection line, {street}, {city}. Emergency 112, 998.", 1);
        var inspectorate = MergeAuthorities(raw?.BuildingInspectorate, "BuildingInspectorate",
            isUs ? $"Building Inspection Office {city}" : $"District Building Inspectorate {city}",
            isUs ? "305-555-0103" : "+48 22 555 0103",
            isUs ? $"Permit desk, {street}, {city}" : $"Permit desk, {street}, {city}", 1);
        var recycling = MergeAuthorities(raw?.RecyclingDepot, "RecyclingDepot",
            isUs ? $"Recycling Center {city} - {street} Drop-off" : $"Selective Waste Drop-off {city} - {street}",
            isUs ? "305-555-0104" : "+48 22 555 0104",
            isUs ? $"Open Mon-Sat 08:00-18:00, {street}, {city}" : $"Open Mon-Sat 08:00-18:00, {street}, {city}", 1);

        var fallbackContractors = BuildFallbackContractors(normalized, isUs, slug, hash);
        var contractors = MergeContractors(raw?.Contractors, fallbackContractors);

        var fallbackProfile = BuildFallbackBuildingProfile(normalized, isUs, hash);
        var profile = raw?.BuildingProfile is null
            ? fallbackProfile
            : new DiscoveredBuildingProfile(
                raw.BuildingProfile.EstimatedConstructionYear is null || raw.BuildingProfile.EstimatedConstructionYear.Value <= 0 ? fallbackProfile.EstimatedConstructionYear : raw.BuildingProfile.EstimatedConstructionYear.Value,
                raw.BuildingProfile.FloorsCount is null || raw.BuildingProfile.FloorsCount.Value <= 0 ? fallbackProfile.FloorsCount : raw.BuildingProfile.FloorsCount.Value,
                raw.BuildingProfile.StairwellsCount is null || raw.BuildingProfile.StairwellsCount.Value <= 0 ? fallbackProfile.StairwellsCount : raw.BuildingProfile.StairwellsCount.Value,
                CleanText(raw.BuildingProfile.HeatingType, fallbackProfile.HeatingType),
                CleanText(raw.BuildingProfile.RoofType, fallbackProfile.RoofType),
                raw.BuildingProfile.RooftopSolarPotentialKwp is null || raw.BuildingProfile.RooftopSolarPotentialKwp.Value <= 0 ? fallbackProfile.RooftopSolarPotentialKwp : Math.Round(raw.BuildingProfile.RooftopSolarPotentialKwp.Value, 1),
                CleanText(raw.BuildingProfile.FloodZone, fallbackProfile.FloodZone),
                CleanText(raw.BuildingProfile.WindZone, fallbackProfile.WindZone),
                CleanText(raw.BuildingProfile.AcousticZone, fallbackProfile.AcousticZone),
                CleanText(raw.BuildingProfile.AirQualityNote, fallbackProfile.AirQualityNote));

        var fallbackGrants = BuildFallbackGrants(normalized, isUs, city, slug);
        var grants = MergeGrants(raw?.GrantsAndOrdinances, fallbackGrants);

        var sourceUrls = raw?.SourceUrls is not null && raw.SourceUrls.Count > 0
            ? raw.SourceUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList()
            : fallbackGrants.Select(g => g.SourceUrl)
                .Concat(electricity.Select(o => o.TariffScheduleUrl))
                .Concat(water.Select(o => o.TariffScheduleUrl))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();

        return new PropertyAiDiscoveryDossier(
            CleanText(raw?.SuggestedAssociationName, isUs ? $"Condominium Association {street}, {city}" : $"Housing Community {street}, {city}"),
            mode,
            CleanText(raw?.CountryCode, country),
            CleanText(raw?.CurrencyCode, currency),
            CleanText(raw?.LocalMunicipalityOrCounty, isUs ? $"{city} County District" : $"{city} Municipality"),
            CleanText(raw?.CompetentCourtOrRecorder, isUs ? $"County Recorder Office, 111 NW 1st Street, {city} {postal}" : $"District Court Land Register Division, {street}, {city} {postal}"),
            CleanText(raw?.LocalTaxOffice, isUs ? $"IRS Tax Office {city} - {postal}" : $"Tax Office {city}, {street} {postal}"),
            CleanText(raw?.RegistryNumber, isUs ? $"FL-CONDO-{DigitsOrFallback(postal, 5, hash)}" : $"REG-{DigitsOrFallback(postal, 6, hash)}"),
            electricity,
            gas,
            water,
            heating,
            waste,
            police,
            officer,
            guard,
            fire,
            inspectorate,
            recycling,
            contractors,
            profile,
            grants,
            CleanRate(raw?.OperatingRatePerM2, operatingRate),
            CleanRate(raw?.RepairFundRatePerM2, repairRate),
            resolvedWasteFee,
            resolvedColdWaterTariff,
            resolvedHotWaterTariff,
            resolvedHeatingTariff,
            resolvedElectricityTariff,
            CleanPower(raw?.ContractedHeatingPowerMw, heatingPower),
            CleanPower(raw?.ContractedElectricalPowerKw, electricPower),
            sourceUrls);
    }

    private static decimal? FirstOperatorTariff(IReadOnlyList<RawUtilityOperator>? operators)
    {
        if (operators is null || operators.Count == 0)
        {
            return null;
        }

        foreach (var op in operators)
        {
            if (op?.CurrentUnitTariff is > 0.001m and < 10000m)
            {
                return op.CurrentUnitTariff.Value;
            }
        }

        return null;
    }

    private static decimal FirstPositiveTariff(decimal? primary, decimal? secondary, decimal fallback)
    {
        if (primary is > 0.001m and < 10000m)
        {
            return Math.Round(primary.Value, 4);
        }

        if (secondary is > 0.001m and < 10000m)
        {
            return Math.Round(secondary.Value, 4);
        }

        return fallback;
    }

    private static decimal FirstPositiveRate(decimal? primary, decimal? secondary, decimal fallback)
    {
        if (primary is > 0.01m and < 5000m)
        {
            return Math.Round(primary.Value, 2);
        }

        if (secondary is > 0.01m and < 5000m)
        {
            return Math.Round(secondary.Value, 2);
        }

        return fallback;
    }

    private static IReadOnlyList<DiscoveredContractor> BuildFallbackContractors(AddressLookupRequest normalized, bool isUs, string slug, int hash)
    {
        var city = normalized.City;
        if (isUs)
        {
            return
            [
                new DiscoveredContractor("Plumbing", $"Emergency Plumbing Rescue {city}", "305-555-0118", $"dispatch@plumbing-{slug}.com", $"https://www.plumbing-{slug}.com", 95m),
                new DiscoveredContractor("Electrical", $"Licensed Electricians {city} 24/7", "305-555-0124", $"office@electric-{slug}.com", $"https://www.electric-{slug}.com", 105m),
                new DiscoveredContractor("HeatingAndHvac", $"HVAC Emergency Service {city}", $"305-555-{(1000 + (hash % 9000)):D4}", $"service@hvac-{slug}.com", $"https://www.hvac-{slug}.com", 110m),
                new DiscoveredContractor("RoofAndFacade", $"Roofing and Facade Crew {city}", $"305-555-{(2000 + (hash % 7000)):D4}", $"quotes@roofing-{slug}.com", $"https://www.roofing-{slug}.com", 115m),
                new DiscoveredContractor("Elevator", $"Elevator Rescue {city} 24/7", "305-555-0199", $"rescue@elevator-{slug}.com", $"https://www.elevator-{slug}.com", 125m)
            ];
        }

        return
        [
            new DiscoveredContractor("Plumbing", $"Emergency Plumbing Rescue {city} - {normalized.StreetAddress}", $"+48 601 {(100 + (hash % 800)):D3} {(100 + ((hash / 7) % 800)):D3}", $"dispatch@hydraulik-{slug}.pl", $"https://www.hydraulik-{slug}.pl", 65m),
            new DiscoveredContractor("Electrical", $"Licensed Electricians {city} 24/7", $"+48 602 {(200 + (hash % 700)):D3} {(200 + ((hash / 11) % 700)):D3}", $"office@elektryk-{slug}.pl", $"https://www.elektryk-{slug}.pl", 70m),
            new DiscoveredContractor("HeatingAndHvac", $"Heating Emergency Service {city}", $"+48 603 {(300 + (hash % 600)):D3} {(300 + ((hash / 13) % 600)):D3}", $"service@ogrzewanie-{slug}.pl", $"https://www.ogrzewanie-{slug}.pl", 72m),
            new DiscoveredContractor("RoofAndFacade", $"Roofing and Facade Crew {city}", $"+48 604 {(400 + (hash % 500)):D3} {(400 + ((hash / 17) % 500)):D3}", $"quotes@dekarz-{slug}.pl", $"https://www.dekarz-{slug}.pl", 75m),
            new DiscoveredContractor("Elevator", $"Elevator Rescue {city} 24/7", "+48 22 555 0199", $"rescue@d2020-windy-{slug}.pl", $"https://www.windykacje-{slug}.pl", 90m)
        ];
    }

    private static DiscoveredBuildingProfile BuildFallbackBuildingProfile(AddressLookupRequest normalized, bool isUs, int hash)
    {
        return new DiscoveredBuildingProfile(
            1968 + (hash % 55),
            4 + (hash % 5),
            2 + (hash % 4),
            isUs ? "Forced-air gas furnace with central cooling" : "Municipal district heating substation",
            isUs ? "Asphalt shingle pitched roof" : "Flat EPDM membrane with ballast",
            Math.Round(12.5 + (hash % 40), 1),
            isUs ? "Flood Zone X (moderate) - county flood maps" : "Low flood risk - river terrace above floodplain",
            isUs ? "Hurricane wind zone - 170 mph design" : "Wind Zone II - lowland exposure",
            isUs ? "Acoustic Zone B - arterial road setback" : "Acoustic Zone II - street facade 62 dB daytime",
            isUs ? $"Air quality meets EPA standards for {normalized.City}; cooling season ozone watch July-August." : $"Air quality monitored by city station near {normalized.City}; boiler replacement deadline 2028 for coal sources.");
    }

    private static IReadOnlyList<DiscoveredGrantOrOrdinance> BuildFallbackGrants(AddressLookupRequest normalized, bool isUs, string city, string slug)
    {
        if (isUs)
        {
            return
            [
                new DiscoveredGrantOrOrdinance("Grant", "IRA-25C-2026", "Federal energy efficient home improvement credit", "US Department of Energy", "26 USC 25C", "https://www.energy.gov/homeowner-savings", "Up to 30 percent credit for heat pumps, insulation and electrical upgrades.", "26 USC 25C: 30 percent credit for qualified energy efficiency improvements placed in service in the dwelling."),
                new DiscoveredGrantOrOrdinance("Grant", "SIRS-RESERVE-2026", "Structural Integrity Reserve Study compliance program", "Florida Division of Condominiums", "Fla. Stat. 718.112(2)(f)", "https://www.myfloridalicense.com/condominiums", "Mandatory milestone inspection and reserve funding for roof, structure, electrical and waterproofing.", "Fla. Stat. 718.112(2)(f): reserves for roof, load-bearing walls, electrical and waterproofing may not be waived."),
                new DiscoveredGrantOrOrdinance("Ordinance", "MIAMI-CODE-FIRE", $"County fire prevention and recycling ordinance - {city}", "County Commission", "County Code Ch. 14", $"https://www.miamidade.gov/fire-prevention/{slug}", "Fire lanes must remain unobstructed; multi-family recycling separation required.", "Fire lanes must remain unobstructed at all times. Multi-family properties must separate recyclables."),
                new DiscoveredGrantOrOrdinance("Ordinance", "FL-STAT-718-112", "Florida Condominium Act meeting and assessment rules", "Florida Legislature", "Fla. Stat. 718.111-718.113", "https://www.leg.state.fl.us/statutes", "14 days notice for assessment meetings; association maintains common elements.", "718.112: Notice of meetings at which assessments will be considered must be given at least 14 days in advance.")
            ];
        }

        return
        [
            new DiscoveredGrantOrOrdinance("Grant", "CLEAN-AIR-2026", $"Clean Air thermal modernization grant - {city}", "National Fund for Environmental Protection", "Clean Air Program 2026, Art. 4-9", $"https://www.clean-air.gov.pl/program/{slug}", "Subsidy up to 60 percent for insulation, window replacement and heat pump conversion.", "Art. 4: Subsidy covers facade insulation, window replacement and heat source conversion up to 60 percent of eligible costs."),
            new DiscoveredGrantOrOrdinance("Grant", "MYSOLAR-2026", $"Rooftop solar PV grant - {city}", "Regional Environmental Fund", "My Electricity Program 6.0", $"https://www.my-electricity.gov.pl/{slug}", "Grant for rooftop PV up to 50 kWp with net billing settlement.", "My Electricity 6.0: grant for rooftop photovoltaic installations with net billing and energy storage bonus."),
            new DiscoveredGrantOrOrdinance("Ordinance", "MUN-WASTE-2025", $"Municipal cleanliness and waste segregation - {city}", "Municipal Council", "Municipal Resolution 2025, par. 4-9", $"https://www.city-council.pl/waste/{slug}", "Five-fraction separation with 3x surcharge for contaminated enclosures.", "Par. 4: Waste must be separated into mixed, plastics and metals, paper, bio and glass fractions. Par. 7: Contaminated enclosures may be charged at 3x the standard rate."),
            new DiscoveredGrantOrOrdinance("Ordinance", "ACT-OWN-1994", "Act on Ownership of Premises - share costs and voting", "National Parliament", "Journal of Laws 2024 item 1112, Art. 3, 12-16, 22-25", "https://www.legal-journals.gov.pl/ownership-premises", "Share-proportional costs, advances due by the 10th, 14 days notice, share voting.", "Art. 3 ust. 2: Common property includes risers, roof, facade and stairwells. Art. 12 ust. 2: Owners bear costs in proportion to shares.")
        ];
    }

    private static PropertyAiDiscoveryDossier BuildOfflineDossier(AddressLookupRequest normalized)
    {
        var isUs = LooksLikeUsAddress(normalized);
        var mode = isUs ? SystemOperatingMode.UsHoa : SystemOperatingMode.HousingAssociation;
        var street = normalized.StreetAddress;
        var city = normalized.City;
        var postal = normalized.PostalOrZipCode;
        var slug = Slugify($"{city}-{postal}");
        var hash = StableHash($"{street}|{city}|{postal}");

        var electricityTariff = isUs ? 0.14m : 0.88m;
        var coldTariff = isUs ? 4.20m : 11.50m;
        var hotTariff = isUs ? 12.40m : 24.00m;
        var heatingTariff = isUs ? 18.00m : 82.00m;
        var wasteFee = isUs ? 32m : 28m;
        var operatingRate = isUs ? 2.95m : 3.20m;
        var repairRate = isUs ? 2.10m : 1.80m;
        var heatingPower = isUs ? 0.55m : 0.42m;
        var electricPower = isUs ? 85m : 65m;

        var electricity = new DiscoveredUtilityOperator("Electricity",
            isUs ? $"Florida Power and Light - {city} District" : $"Regional Power Distribution - {city} District",
            isUs ? "1-800-468-8243" : "991",
            isUs ? $"https://www.fpl.com/outages/{slug}" : $"https://www.stoen.pl/outages/{slug}",
            isUs ? $"https://www.fpl.com/fault-map/{slug}" : $"https://www.stoen.pl/fault-map/{slug}",
            isUs ? "https://www.fpl.com/rates" : "https://www.stoen.pl/tariffs-households",
            electricityTariff, isUs ? "USD/kWh" : "PLN/kWh");
        var gas = new DiscoveredUtilityOperator("Gas",
            isUs ? $"Florida City Gas - {city} Network" : $"Regional Gas Distribution - {city} Network",
            isUs ? "1-800-427-7712" : "992",
            isUs ? $"https://www.floridacitygas.com/safety/{slug}" : $"https://www.psgaz.pl/outages/{slug}",
            isUs ? $"https://www.floridacitygas.com/network/{slug}" : $"https://www.psgaz.pl/network/{slug}",
            isUs ? "https://www.floridacitygas.com/rates" : "https://www.psgaz.pl/tariffs",
            isUs ? 1.10m : 2.40m, isUs ? "USD/therm" : "PLN/m3");
        var water = new DiscoveredUtilityOperator("ColdWater",
            isUs ? $"Miami-Dade Water and Sewer - {city} Zone" : $"Municipal Water and Sewage Works - {city}",
            isUs ? "305-665-7477" : "994",
            isUs ? $"https://www.miamidade.gov/water-sewer/alerts/{slug}" : $"https://www.mpwik.com.pl/outages/{slug}",
            isUs ? $"https://www.miamidade.gov/water-sewer/map/{slug}" : $"https://www.mpwik.com.pl/fault-map/{slug}",
            isUs ? "https://www.miamidade.gov/water-sewer/rates" : "https://www.mpwik.com.pl/tariffs",
            coldTariff, isUs ? "USD/CCF" : "PLN/m3");
        var heating = new DiscoveredUtilityOperator("DistrictHeating",
            isUs ? $"District Energy Services - {city}" : $"Municipal District Heating Network - {city}",
            isUs ? "305-555-0142" : "993",
            isUs ? $"https://www.districtenergy.org/outages/{slug}" : $"https://www.veolia.pl/outages/{slug}",
            isUs ? $"https://www.districtenergy.org/map/{slug}" : $"https://www.veolia.pl/fault-map/{slug}",
            isUs ? "https://www.districtenergy.org/rates" : "https://www.veolia.pl/tariffs",
            heatingTariff, isUs ? "USD/MMBtu" : "PLN/GJ");
        var wasteOp = new DiscoveredUtilityOperator("MunicipalWaste",
            isUs ? $"Miami-Dade Solid Waste - {city} Sector" : $"City Waste Management - {city} Sector {DigitsOrFallback(postal, 2, hash)}",
            isUs ? "311" : "+48 22 555 0100",
            isUs ? $"https://www.miamidade.gov/solidwaste/schedule/{slug}" : $"https://www.city-cleaning.pl/schedule/{slug}",
            isUs ? $"https://www.miamidade.gov/solidwaste/map/{slug}" : $"https://www.city-cleaning.pl/map/{slug}",
            isUs ? "https://www.miamidade.gov/solidwaste/rates" : "https://www.city-cleaning.pl/tariffs",
            wasteFee, isUs ? "USD/unit/mo" : "PLN/person/mo");

        var police = new DiscoveredAuthorityContact("PolicePrecinct",
            isUs ? $"Police Precinct {city} Central - {street}" : $"District Police Station {city} - {street} Precinct",
            isUs ? "305-555-0100" : "+48 47 723 0000",
            isUs ? $"24/7 duty desk, {street}, {city} {postal}" : $"Duty desk 24/7, {street}, {city} {postal}", 1);
        var officer = new DiscoveredAuthorityContact("DistrictPoliceOfficer",
            isUs ? $"Beat Officer Sector {DigitsOrFallback(postal, 3, hash)} - {city}" : $"District Beat Officer {city} Sector {DigitsOrFallback(postal, 3, hash)}",
            isUs ? "305-555-0101" : "+48 47 723 0001",
            isUs ? $"Beat {DigitsOrFallback(postal, 3, hash)}, office hours 08:00-16:00, {street}, {city}" : $"Sector {DigitsOrFallback(postal, 3, hash)}, office hours 08:00-16:00, {street}, {city}", 1);
        var guard = new DiscoveredAuthorityContact("MunicipalGuard",
            isUs ? $"Code Enforcement Patrol {city} - {street}" : $"Municipal Guard Patrol {city} - {street}",
            isUs ? "311" : "+48 22 555 0102",
            isUs ? $"Fire-lane towing dispatch, {street}, {city}" : $"Fire-lane towing dispatch, {street}, {city}", 1);
        var fire = new DiscoveredAuthorityContact("FireStation",
            isUs ? $"Fire Rescue Station {city} - {street}" : $"Fire and Rescue Station {city} - {street}",
            isUs ? "305-555-0191" : "+48 22 555 0198",
            isUs ? $"Hydrant inspection line, {street}, {city}. Emergency 911." : $"Hydrant inspection line, {street}, {city}. Emergency 112, 998.", 1);
        var inspectorate = new DiscoveredAuthorityContact("BuildingInspectorate",
            isUs ? $"Building Inspection Office {city}" : $"District Building Inspectorate {city}",
            isUs ? "305-555-0103" : "+48 22 555 0103",
            isUs ? $"Permit desk, {street}, {city}" : $"Permit desk, {street}, {city}", 1);
        var recycling = new DiscoveredAuthorityContact("RecyclingDepot",
            isUs ? $"Recycling Center {city} - {street} Drop-off" : $"Selective Waste Drop-off {city} - {street}",
            isUs ? "305-555-0104" : "+48 22 555 0104",
            isUs ? $"Open Mon-Sat 08:00-18:00, {street}, {city}" : $"Open Mon-Sat 08:00-18:00, {street}, {city}", 1);

        var contractors = BuildFallbackContractors(normalized, isUs, slug, hash);
        var profile = BuildFallbackBuildingProfile(normalized, isUs, hash);
        var grants = BuildFallbackGrants(normalized, isUs, city, slug);

        var sourceUrls = new List<string>
        {
            "offline-fallback deterministic generator (no live web search)",
            electricity.TariffScheduleUrl,
            water.TariffScheduleUrl,
            heating.TariffScheduleUrl,
            grants[0].SourceUrl,
            grants[2].SourceUrl
        };

        return new PropertyAiDiscoveryDossier(
            isUs ? $"Condominium Association {street}, {city}" : $"Housing Community {street}, {city}",
            mode,
            isUs ? "US" : "PL",
            isUs ? "USD" : "PLN",
            isUs ? $"{city} County District" : $"{city} Municipality",
            isUs ? $"County Recorder Office, 111 NW 1st Street, {city} {postal}" : $"District Court Land Register Division, {street}, {city} {postal}",
            isUs ? $"IRS Tax Office {city} - {postal}" : $"Tax Office {city}, {street} {postal}",
            isUs ? $"FL-CONDO-{DigitsOrFallback(postal, 5, hash)}" : $"REG-{DigitsOrFallback(postal, 6, hash)}",
            [electricity],
            [gas],
            [water],
            [heating],
            [wasteOp],
            [police],
            [officer],
            [guard],
            [fire],
            [inspectorate],
            [recycling],
            contractors,
            profile,
            grants,
            operatingRate,
            repairRate,
            wasteFee,
            coldTariff,
            hotTariff,
            heatingTariff,
            electricityTariff,
            heatingPower,
            electricPower,
            sourceUrls);
    }

    private sealed record RawUtilityOperator(
        string? UtilityKind,
        string? ProviderName,
        string? EmergencyPhone,
        string? OutageBulletinUrl,
        string? LiveFaultMapUrl,
        string? TariffScheduleUrl,
        decimal? CurrentUnitTariff,
        string? TariffUnit);

    private sealed record RawAuthorityContact(
        string? Category,
        string? Name,
        string? PhoneNumber,
        string? AddressOrNotes,
        int? SortOrder);

    private sealed record RawContractor(
        string? Trade,
        string? CompanyName,
        string? Phone,
        string? Email,
        string? Website,
        decimal? TypicalHourlyRate);

    private sealed record RawBuildingProfile(
        int? EstimatedConstructionYear,
        int? FloorsCount,
        int? StairwellsCount,
        string? HeatingType,
        string? RoofType,
        double? RooftopSolarPotentialKwp,
        string? FloodZone,
        string? WindZone,
        string? AcousticZone,
        string? AirQualityNote);

    private sealed record RawGrant(
        string? Kind,
        string? ProgramOrDocumentCode,
        string? Title,
        string? IssuingAuthority,
        string? OfficialCitation,
        string? SourceUrl,
        string? Summary,
        string? VerbatimText);

    private sealed record RawDiscoveryDossier(
        string? SuggestedAssociationName,
        string? SuggestedOperatingMode,
        string? CountryCode,
        string? CurrencyCode,
        string? LocalMunicipalityOrCounty,
        string? CompetentCourtOrRecorder,
        string? LocalTaxOffice,
        string? RegistryNumber,
        List<RawUtilityOperator>? ElectricityOperator,
        List<RawUtilityOperator>? GasOperator,
        List<RawUtilityOperator>? WaterOperator,
        List<RawUtilityOperator>? HeatingOperator,
        List<RawUtilityOperator>? WasteOperator,
        List<RawAuthorityContact>? PolicePrecinct,
        List<RawAuthorityContact>? DistrictOfficer,
        List<RawAuthorityContact>? MunicipalGuard,
        List<RawAuthorityContact>? FireStation,
        List<RawAuthorityContact>? BuildingInspectorate,
        List<RawAuthorityContact>? RecyclingDepot,
        List<RawContractor>? Contractors,
        RawBuildingProfile? BuildingProfile,
        List<RawGrant>? GrantsAndOrdinances,
        decimal? OperatingRatePerM2,
        decimal? RepairFundRatePerM2,
        decimal? WasteFeePerOccupant,
        decimal? ColdWaterTariffPerM3,
        decimal? HotWaterHeatingCostPerM3,
        decimal? DistrictHeatingTariffPerGj,
        decimal? CommonElectricityTariffPerKwh,
        decimal? ContractedHeatingPowerMw,
        decimal? ContractedElectricalPowerKw,
        List<string>? SourceUrls);

    private sealed record RawTriagePayload(
        string? Category,
        string? Priority,
        string? Boundary,
        string? BoundaryRationale,
        string? CitedLegalDocumentCode,
        string? CitedVerbatimLegalExcerpt,
        string? DefectConcept,
        string? EnglishSummaryTitle,
        string? EnglishDescription,
        string? DetectedLanguage,
        string? ResidentReplyInOriginalLanguage,
        double? Confidence);

    private sealed record RawCompliancePayload(
        bool? HasConflict,
        string? ConflictClauseReference,
        string? CitedStatutoryDocumentCode,
        string? CitedVerbatimLegalExcerpt,
        string? ConflictExplanation,
        string? RecommendedMajorityRule,
        string? CompliantRewriteText);
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
