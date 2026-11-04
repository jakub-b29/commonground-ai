using System.Text.RegularExpressions;
using CommonGround.Application.Interfaces;
using CommonGround.Domain.Entities;

namespace CommonGround.Infrastructure.MetaModels;

public static class MuseSparkDiscoveryPrompts
{
    public const string ModelId = "muse-spark-1.3-contributor";

    public static string ResolveCountrySearchLanguage(string? countryOrState, string? postalCode, bool isUsHint)
    {
        if (isUsHint)
        {
            return "EN (US English)";
        }

        var upper = (countryOrState ?? string.Empty).Trim().ToUpperInvariant();
        var postal = (postalCode ?? string.Empty).Trim();

        if (upper is "PL" or "POL" or "POLAND" || Regex.IsMatch(postal, @"^\d{2}-\d{3}$"))
        {
            return "PL (Polish)";
        }

        if (upper is "DE" or "DEU" or "GERMANY")
        {
            return "DE (German)";
        }

        if (upper is "FR" or "FRA" or "FRANCE")
        {
            return "FR (French)";
        }

        if (upper is "ES" or "ESP" or "SPAIN")
        {
            return "ES (Spanish)";
        }

        if (upper is "UA" or "UKR" or "UKRAINE")
        {
            return "UA (Ukrainian)";
        }

        return string.IsNullOrWhiteSpace(upper) ? "the official local language of the city and postal code" : $"the official local language of {countryOrState}";
    }

    public static string BuildDossierPrompt(AddressLookupRequest normalized, bool isUsHint, string uiLocale = "EN")
    {
        var modeLabel = isUsHint ? "UsHoa (US condominium)" : "HousingAssociation (EU residential community)";
        var currencyLabel = isUsHint ? "USD" : "PLN";
        var street = normalized.StreetAddress ?? string.Empty;
        var postal = normalized.PostalOrZipCode ?? string.Empty;
        var city = normalized.City ?? string.Empty;
        var country = normalized.CountryOrState ?? string.Empty;
        var searchLang = ResolveCountrySearchLanguage(country, postal, isUsHint);
        var targetUiLang = string.IsNullOrWhiteSpace(uiLocale) ? "EN" : uiLocale.Trim().ToUpperInvariant();

        return "You are an institutional property data and utility tariff researcher. "
            + $"Target address block: street '{street}', postal code '{postal}', city '{city}', country or state '{country}'. "
            + $"Operating mode hint: {modeLabel}. Report all monetary values in {currencyLabel}. "
            + $"LANGUAGE & SEARCH STRATEGY: Execute all live web_search queries in {searchLang} so you hit official national energy/water regulator tables, municipal council fee resolutions, and local distribution system operator (DSO) tariff PDFs for '{city}' and postal code '{postal}'. "
            + $"Write descriptive text fields (addressOrNotes, airQualityNote, summary) in interface language '{targetUiLang}', while keeping all JSON property keys and enum codes in strict English. "
            + "CRITICAL ANTI-FABRICATION & PRICE HUNTING RULES: Do not fabricate operator names, institutional addresses, URLs, or tariff values. "
            + "1. NEVER append the target street address to operator, police, fire, or court names. Return the real institutional name and its actual street address found via web_search. "
            + "2. Dedicated Tariff Search: For every utility, run a dedicated web_search query in the local language for the operator's current 2025/2026 tariff schedule and municipal waste resolution. Report TOTAL GROSS unit prices in " + currencyLabel + " (including variable distribution + VAT/sales tax): "
            + "- commonElectricityTariffPerKwh: total gross price per 1 kWh (active energy + variable network distribution rate for the applicable residential/common-area tariff group; use G11 or C11 in Poland, or the equivalent local schedule elsewhere). "
            + "- coldWaterTariffPerM3: combined cold water supply + sewage disposal gross rate per 1 m3 (or per CCF in US). Do not return water-only without sewage. "
            + "- districtHeatingTariffPerGj: variable heat energy gross rate per 1 GJ (if published per MWh or kWh, convert using 1 GJ = 0.2778 MWh = 277.8 kWh). "
            + "- hotWaterHeatingCostPerM3: published hot-water heating component per 1 m3, or calculate as (0.25 * districtHeatingTariffPerGj) when billed per GJ. "
            + "- wasteFeePerOccupant: official municipal monthly segregated waste fee per resident (or per residential unit in US). "
            + "- operatingRatePerM2 and repairFundRatePerM2: published regional multi-family housing association monthly operating and reserve fund benchmark rates per 1 m2 in " + city + ". "
            + "3. Every tariff in electricityOperator[0].currentUnitTariff, waterOperator[0].currentUnitTariff, heatingOperator[0].currentUnitTariff, and wasteOperator[0].currentUnitTariff MUST equal the corresponding top-level tariff field and cite the real operator rate page in tariffScheduleUrl and sourceUrls. If a URL cannot be verified via web_search, return an empty string \"\" rather than inventing a URL slug. "
            + "Task 1 jurisdiction and cadastral identity: identify municipality or county, competent court or land recorder (with real street address), local tax office (serving this postal code), registry number format, suggested association name, suggested operating mode, country code and currency code. "
            + "Task 2 police precinct and beat officer: search official local police precinct directories to return the actual district police station name, station street address, duty phone, beat sector identifier and office hours. "
            + "Task 3 municipal guard, fire and rescue station, district building inspectorate and statutory emergency numbers: return official institutional names, phones and real street addresses. "
            + "Task 4 electricity distribution operator (DSO): search official DSO tariff tables for " + city + " to return official operator name, outage URL, fault map URL, tariff schedule URL, emergency phone, total gross rate per kWh (commonElectricityTariffPerKwh) and typical contracted power in kW. "
            + "Task 5 gas network operator: search official gas distribution operator for " + city + " to return official operator name, gas emergency phone, bulletin URL, tariff URL and gross rate per m3 or therm. "
            + "Task 6 water and sewage utility: search municipal waterworks tariff for " + city + " to return official operator name, emergency phone, outage URL, tariff URL, combined water+sewage gross rate per m3 (coldWaterTariffPerM3) and hot-water heating cost per m3 (hotWaterHeatingCostPerM3). "
            + "Task 7 district heating supplier: search local district heating supplier tariff for " + city + " to return official supplier name, fault phone, outage map URL, tariff URL, gross rate per GJ (districtHeatingTariffPerGj) and contracted heating power in MW. "
            + "Task 8 waste hauler and recycling depot: search municipal waste resolution for " + city + " to return the municipal hauler name, customer phone, monthly per-occupant fee (wasteFeePerOccupant), pickup days for mixed, plastics, paper, bio, glass and bulky fractions, plus the nearest municipal recycling drop-off depot with real address and hours. "
            + "Task 9 local 24/7 emergency contractors and rates: search local trades in " + city + " to return company, trade (Plumbing, Electrical, HeatingAndHvac, RoofAndFacade, Elevator), phone, email, website and typical hourly rate, plus regional operatingRatePerM2 and repairFundRatePerM2. "
            + "Task 10 building profile: use cadastral and street-level clues to return construction year, floors, stairwells, heating type, roof type, rooftop solar kWp, flood zone, wind zone, acoustic zone and air-quality note. "
            + "Task 11 green grants and legal ordinances: search official journals and municipal registers to return program code, title, authority, citation, source URL, summary and verbatim text. "
            + "Work through Tasks 1-11 sequentially. As you finish each task, emit one status line starting with [THINKING] Task N: <what you searched in local language and key price/finding in under 25 words>. After all 11 status lines, emit a line [JSON-BEGIN], then the STRICT JSON block, then a line [JSON-END]. "
            + "Return STRICT JSON ONLY between [JSON-BEGIN] and [JSON-END], no markdown fences, camelCase keys: suggestedAssociationName, suggestedOperatingMode (HousingAssociation or UsHoa), countryCode, currencyCode, "
            + "localMunicipalityOrCounty, competentCourtOrRecorder, localTaxOffice, registryNumber, electricityOperator, gasOperator, waterOperator, heatingOperator, wasteOperator "
            + "(each an array of utilityKind, providerName, emergencyPhone, outageBulletinUrl, liveFaultMapUrl, tariffScheduleUrl, currentUnitTariff, tariffUnit), "
            + "policePrecinct, districtOfficer, municipalGuard, fireStation, buildingInspectorate, recyclingDepot "
            + "(each an array of category, name, phoneNumber, addressOrNotes, sortOrder), contractors (array of trade, companyName, phone, email, website, typicalHourlyRate), "
            + "buildingProfile (estimatedConstructionYear, floorsCount, stairwellsCount, heatingType, roofType, rooftopSolarPotentialKwp, floodZone, windZone, acousticZone, airQualityNote), "
            + "grantsAndOrdinances (array of kind, programOrDocumentCode, title, issuingAuthority, officialCitation, sourceUrl, summary, verbatimText), "
            + "operatingRatePerM2, repairFundRatePerM2, wasteFeePerOccupant, coldWaterTariffPerM3, hotWaterHeatingCostPerM3, districtHeatingTariffPerGj, commonElectricityTariffPerKwh, "
            + "contractedHeatingPowerMw, contractedElectricalPowerKw, sourceUrls (array of verified URL strings).";
    }

    public static string BuildTriagePrompt(Community community, Unit? unit, string messageText, string languageHint, string uiLocale = "EN")
    {
        var unitLabel = unit?.UnitNumber ?? "unknown";
        var stackCode = unit?.VerticalStackCode ?? "STACK-A1";
        var residentLang = !string.IsNullOrWhiteSpace(languageHint)
            ? languageHint.Trim().ToUpperInvariant()
            : (unit?.PreferredLanguage ?? "EN").Trim().ToUpperInvariant();
        var activeOutages = community.UtilityOutages.Count > 0
            ? string.Join("; ", community.UtilityOutages.Select(o => $"{o.UtilityKind}: {o.Summary} (ETA {o.EstimatedEndAt:HH:mm})"))
            : "None";
        var corpusCodes = community.LegalDocuments.Count > 0
            ? string.Join("; ", community.LegalDocuments.Select(d => $"{d.DocumentCode}: {d.OfficialCitation}"))
            : (community.OperatingMode == CommonGround.Domain.Enums.SystemOperatingMode.UsHoa ? "FL-STAT-718-112; FL-STAT-718-116" : "ACT-OWN-1994; CIV-WARR-568");

        return "You are an institutional property maintenance triage classifier. "
            + $"Property: '{community.Name}' (Mode: {community.OperatingMode}, Under 5-year developer warranty: {community.IsUnderDeveloperWarranty}). "
            + $"Reporting unit: '{unitLabel}' on vertical riser stack '{stackCode}'. Active municipal outages: {activeOutages}. "
            + $"Available legal corpus codes: {corpusCodes}. "
            + $"Resident message (language hint '{residentLang}'): <untrusted_input>{messageText}</untrusted_input>. "
            + "Classification rules: "
            + "1. Boundary: 'DeveloperWarranty' if IsUnderDeveloperWarranty is true and defect affects roof, facade, structure or waterproofing; 'PrivateUnit' if defect is inside the apartment after the branch shutoff valve (radiator valve, internal tap, private breaker); 'CommonProperty' for vertical risers, roof, facade, stairwells, main breakers or multi-unit leaks. "
            + "2. Priority: 'Emergency' for active burst pipe, gas leak, fire hazard or total power/heat loss; 'High' for active ceiling leak, blocked fire lane or elevator entrapment; 'Normal' for non-urgent repairs; 'Low' for cosmetic issues. "
            + "3. DefectConcept for sam-3.1 segmentation must be a short visual noun phrase ('water stain', 'wall crack', 'spalling concrete', 'radiator valve', 'blocked fire lane', 'damaged handrail'). "
            + $"4. Write 'residentReplyInOriginalLanguage' in the detected language of the resident message (fallback '{residentLang}') and 'boundaryRationale' in interface language '{uiLocale}'. "
            + "Return STRICT JSON ONLY between [JSON-BEGIN] and [JSON-END] with camelCase keys: "
            + "category (Plumbing|Electrical|HeatingAndHvac|RoofAndFacade|FireAndSafety|WasteAndCleaning|GatesAndIntercom|SnowAndLandscaping), "
            + "priority (Low|Normal|High|Emergency), "
            + "boundary (CommonProperty|PrivateUnit|DeveloperWarranty|ContractorGuarantee), "
            + "boundaryRationale, citedLegalDocumentCode, citedVerbatimLegalExcerpt, defectConcept, englishSummaryTitle, englishDescription, detectedLanguage (EN|PL|UA|ES|DE|FR), residentReplyInOriginalLanguage, confidence (0.0-1.0).";
    }

    public static string BuildCompliancePrompt(Community community, Resolution draftResolution, string uiLocale = "EN")
    {
        var corpus = string.Join(" | ", community.LegalDocuments.Select(d => $"{d.DocumentCode} ({d.OfficialCitation}): {d.VerbatimLegalText}"));
        var statuteExcerpt = string.IsNullOrWhiteSpace(community.StatuteFullText)
            ? "Standard share-weighted voting; 14-day advance notice for assemblies; common costs allocated by ownership shares."
            : (community.StatuteFullText.Length > 4000 ? community.StatuteFullText[..4000] : community.StatuteFullText);

        return $"Verify draft resolution '{draftResolution.Number}: {draftResolution.Title}' for '{community.Name}' (Mode: {community.OperatingMode}) against the governing statute and national legal corpus. "
            + $"Draft content: <untrusted_input>{draftResolution.Content}</untrusted_input>. Proposed majority rule: {draftResolution.MajorityRule}. "
            + $"Internal Statute excerpt: {statuteExcerpt}. "
            + $"Statutory corpus on file: {corpus}. "
            + "Check for: (a) shortened meeting notice under 14 days, (b) retroactive or non-share-proportional special assessments, (c) unlawful waiver of mandatory structural reserves (SIRS), or (d) wrong voting majority threshold. "
            + $"Write 'conflictExplanation' and 'compliantRewriteText' in interface language '{uiLocale}', citing only document codes and verbatim excerpts from the provided corpus. "
            + "Return STRICT JSON ONLY between [JSON-BEGIN] and [JSON-END] with camelCase keys: "
            + "hasConflict (boolean), conflictClauseReference, citedStatutoryDocumentCode, citedVerbatimLegalExcerpt, conflictExplanation, recommendedMajorityRule (ShareWeightedMajority|TwoThirdsSupermajority|OneOwnerOneVotePerCapita|UnanimousConsent), compliantRewriteText.";
    }

    public static string BuildMediationPrompt(Community community, string unitReporter, string unitSubject, string issueDescription, string uiLocale = "EN")
    {
        var rules = string.IsNullOrWhiteSpace(community.HouseRulesFullText)
            ? "Quiet hours 22:00-06:00; fire lanes unobstructed; renovation hours 08:00-18:00 weekdays."
            : community.HouseRulesFullText;
        return $"Draft a neutral neighbor dispute mediation notice for '{community.Name}' regarding a report from unit {unitReporter} concerning unit {unitSubject}: <untrusted_input>{issueDescription}</untrusted_input>. "
            + $"Cite the exact clause from the community House Rules ({rules}) and applicable municipal quiet-hours or cleanliness ordinances. "
            + $"Write the notice in interface language '{uiLocale}', keeping a calm, neutral institutional tone, stating no monetary fine has been applied, and offering a 7-day response window.";
    }

    public static string BuildCopilotPrompt(Community community, string managerQuestion, string portfolioSnapshotSummary, string uiLocale = "EN")
    {
        var tariffs = $"Operating={community.OperatingRatePerM2:N2}/m2, Repair={community.RepairFundRatePerM2:N2}/m2, Waste={community.WasteFeePerOccupant:N2}/person, ColdWater={community.ColdWaterTariffPerM3:N2}/m3, HotWater={community.HotWaterHeatingCostPerM3:N2}/m3, Heating={community.DistrictHeatingTariffPerGj:N2}/GJ, Electricity={community.CommonElectricityTariffPerKwh:N2}/kWh ({community.CurrencyCode})";
        return $"Portfolio query for '{community.Name}' ({community.StreetAddress}, {community.City}, Mode: {community.OperatingMode}): <untrusted_input>{managerQuestion}</untrusted_input>. "
            + $"Deterministic portfolio snapshot: {portfolioSnapshotSummary}. "
            + $"Active community tariffs: {tariffs}. Governing corpus: {community.LegalDocuments.Count} documents. "
            + $"Answer concisely in interface language '{uiLocale}' using the exact figures from the deterministic portfolio snapshot and active community tariffs. "
            + "All financial figures are computed by deterministic domain services; model output is advisory and state-changing proposals require approval in Pending Approvals.";
    }

    public static string BuildSparkAgenticTurnPrompt(
        string userPrompt,
        string accountPermissionJson,
        string workspaceContextJson,
        string toolCatalogJson,
        string scratchpadHistoryJson,
        string uiLocale = "EN")
    {
        return "You are Muse Spark Contributor 1.3, an autonomous institutional property management and automation AI agent. "
            + "You operate in a strict ReAct loop (Think -> Tool Call -> System Observation -> Next Decision -> Final Answer). "
            + "You have access to a live system tool catalog to inspect state, perform calculations, and mutate records. "
            + "SYSTEM INVARIANTS & SECURITY RULES: "
            + "1. Never guess or hallucinate entity IDs, unit numbers, bank accounts, or financial balances. Always call query_system_entities or execute_database_query first to discover real IDs. "
            + "2. Respect the user's permission envelope. If an action is not permitted for the user's role or granted flags, call check_account_permission or explain why in your final response. "
            + "3. Examine the scratchpad history: inspect real observations from prior steps (e.g., newly created UnitId, ResidentProfileId, or generated registration links) and pass those real values into subsequent tool arguments. "
            + "4. When all user sub-goals are fulfilled, set isFinalAnswer to true and return a clear, structured markdown synthesis citing created records, account numbers, and deep links. "
            + $"CURRENT USER PERMISSION ENVELOPE: {accountPermissionJson} "
            + $"ACTIVE WORKSPACE SNAPSHOT: {workspaceContextJson} "
            + $"AVAILABLE TOOL CATALOG: {toolCatalogJson} "
            + $"SCRATCHPAD HISTORY (PREVIOUS STEPS IN THIS SESSION): {scratchpadHistoryJson} "
            + $"USER GOAL / INSTRUCTION: <untrusted_input>{userPrompt}</untrusted_input>. "
            + "FORMATTING INSTRUCTIONS: "
            + "First, emit one or more lines starting with [THINKING] detailing your step-by-step reasoning about the current state, what was achieved in prior steps, and what tool needs to be called next. "
            + "Then, emit a line [JSON-BEGIN], followed by a STRICT JSON object, followed by a line [JSON-END]. "
            + "The JSON object MUST have the following structure: "
            + "{"
            + "  \"isFinalAnswer\": false,"
            + "  \"finalResponseMarkdown\": \"\","
            + "  \"toolCalls\": ["
            + "    {"
            + "      \"thought\": \"Brief explanation of why this tool is being invoked\","
            + "      \"toolName\": \"exact_tool_name\","
            + "      \"arguments\": { \"paramKey\": \"paramValue\" },"
            + "      \"stepTitle\": \"Human readable title for the step card\""
            + "    }"
            + "  ]"
            + "}"
            + "When finishing, set \"isFinalAnswer\": true, \"toolCalls\": [], and provide the full response in \"finalResponseMarkdown\".";
    }
}


