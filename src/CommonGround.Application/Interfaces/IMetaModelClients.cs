using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Application.Interfaces;

public sealed record TriageModelResult(
    WorkOrderCategory Category,
    WorkOrderPriority Priority,
    ResponsibilityBoundary Boundary,
    string BoundaryRationale,
    string CitedLegalDocumentCode,
    string CitedVerbatimLegalExcerpt,
    string DefectConcept,
    string EnglishSummaryTitle,
    string EnglishDescription,
    string DetectedLanguage,
    string ResidentReplyInOriginalLanguage,
    double Confidence,
    int LatencyMs,
    int TokensUsed,
    bool UsedLiveApi);

public sealed record StatuteComplianceModelResult(
    bool HasConflict,
    string ConflictClauseReference,
    string CitedStatutoryDocumentCode,
    string CitedVerbatimLegalExcerpt,
    string ConflictExplanation,
    VotingMajorityRule RecommendedMajorityRule,
    string CompliantRewriteText,
    int CorpusTokensProcessed,
    int LatencyMs,
    bool UsedLiveApi);

public sealed record MeterOcrModelResult(
    string ExtractedSerialNumber,
    decimal ExtractedReadingValue,
    double Confidence,
    int LatencyMs,
    bool UsedLiveApi);

public sealed record PaperBallotRowExtracted(
    string UnitNumber,
    string OwnerName,
    VoteChoice Choice,
    bool SignatureVerified);

public sealed record PaperBallotOcrResult(
    IReadOnlyList<PaperBallotRowExtracted> Rows,
    int LatencyMs,
    bool UsedLiveApi);

public sealed record SamSegmentationResult(
    string ConceptPrompt,
    decimal MeasureAreaM2,
    double Confidence,
    string BaseImageDataUrl,
    string AnnotatedOverlayDataUrl,
    int LatencyMs,
    bool UsedLiveApi);

public sealed record SamBeforeAfterVerificationResult(
    decimal BeforeAreaM2,
    decimal AfterAreaM2,
    decimal RemainingAreaPercent,
    bool IsVerifiedResolved,
    string AfterPhotoDataUrl,
    string AfterAnnotatedOverlayDataUrl,
    string VerificationSummary,
    int LatencyMs,
    bool UsedLiveApi);

public sealed record WebEnrichmentDiscoveryResult(
    string OfficialCommunityName,
    string TaxIdOrEin,
    string RegistryNumber,
    string MunicipalityOrCounty,
    string TaxOffice,
    string CompetentCourtOrRecorder,
    IReadOnlyList<UtilityProviderSource> DiscoveredUtilitySources,
    IReadOnlyList<EmergencyAndAuthorityContact> DiscoveredEmergencyContacts,
    IReadOnlyList<Vendor> DiscoveredLocalVendors,
    IReadOnlyList<WasteCollectionSchedule> DiscoveredWasteSchedules,
    IReadOnlyList<GreenSubsidyProgram> DiscoveredGreenSubsidies,
    IReadOnlyList<LegalDocumentSource> DiscoveredLegalDocuments,
    string SummaryReport,
    int LatencyMs,
    bool UsedLiveApi);

public interface IMuseSparkClient
{
    Task<TriageModelResult> TriageResidentRequestAsync(Community community, Unit? unit, string messageText, string languageHint, CancellationToken ct = default);
    Task<StatuteComplianceModelResult> VerifyFullCorpusComplianceAsync(Community community, Resolution draftResolution, CancellationToken ct = default);
    Task<MeterOcrModelResult> ReadSubMeterFromPhotoAsync(UtilityMeter meter, decimal? simulatedReadingOverride, CancellationToken ct = default);
    Task<PaperBallotOcrResult> ExtractPaperBallotRowsAsync(Community community, Resolution resolution, CancellationToken ct = default);
    Task<string> DraftNeighborDisputeMediationAsync(Community community, string unitReporter, string unitSubject, string issueDescription, CancellationToken ct = default);
    Task<string> EvaluateModificationOrArcRequestAsync(Community community, ModificationOrArcRequest request, CancellationToken ct = default);
    Task<string> AskPortfolioCopilotAsync(Community community, string managerQuestion, string portfolioSnapshotSummary, CancellationToken ct = default);
}

public interface IMuseWonderClient
{
    Task<WebEnrichmentDiscoveryResult> DiscoverAndEnrichCommunityFromWebAsync(Community community, string addressOrTaxIdInput, CancellationToken ct = default);
    Task<IReadOnlyList<LegalDocumentSource>> AcquireStatutoryLegalCorpusAsync(Community community, CancellationToken ct = default);
    Task<(string AnnualReportSummary, List<Resolution> DraftResolutions)> RunAnnualMeetingAutopilotAsync(Community community, CancellationToken ct = default);
    Task<InsuranceClaimDossier> PackageInsuranceClaimDossierAsync(Community community, Building building, WorkOrder workOrder, CancellationToken ct = default);
}

public interface IMuseVoiceTranscriber
{
    Task<(string Transcript, string DetectedLanguage, int LatencyMs)> TranscribeVoiceNoteAsync(string voiceScenarioPreset, CancellationToken ct = default);
    Task<(string DiarizedTranscript, string FormalMinutesMarkdown, string ActionItemsSummary, int LatencyMs)> TranscribeAndSummarizeMeetingAsync(Community community, string rawMeetingNotesOrPreset, CancellationToken ct = default);
}

public interface ISam31Client
{
    Task<SamSegmentationResult> SegmentDefectAndMeasureAreaAsync(string defectConcept, string locationContext, CancellationToken ct = default);
    Task<SamBeforeAfterVerificationResult> VerifyBeforeAfterRepairAsync(WorkOrder workOrder, CancellationToken ct = default);
    Task<string> RenderAnnotatedInfrastructurePhotoAsync(string category, string title, string colorHex, CancellationToken ct = default);
}

public interface IMuseImageClient
{
    Task<(string OptionADataUrl, string OptionBDataUrl)> GenerateRenovationBallotPreviewsAsync(string resolutionTitle, string optionALabel, string optionBLabel, CancellationToken ct = default);
    Task<string> GenerateQrStairwellNoticePosterAsync(string communityName, string headline, string bodySummary, string qrUrl, CancellationToken ct = default);
    Task<string> GenerateMultilingualWasteSegregationPosterAsync(string communityName, CancellationToken ct = default);
}
