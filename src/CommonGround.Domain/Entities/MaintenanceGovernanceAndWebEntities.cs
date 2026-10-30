using CommonGround.Domain.Enums;

namespace CommonGround.Domain.Entities;

public sealed class WorkOrder : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public Guid? UnitId { get; set; }
    public string? UnitNumber { get; set; }
    public string VerticalStackCode { get; set; } = "STACK-A1";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OriginalResidentMessage { get; set; } = string.Empty;
    public string OriginalLanguage { get; set; } = "EN";
    public string TranslatedReplyToResident { get; set; } = string.Empty;
    public WorkOrderCategory Category { get; set; }
    public WorkOrderPriority Priority { get; set; }
    public ResponsibilityBoundary Boundary { get; set; }
    public string BoundaryRationale { get; set; } = string.Empty;
    public string CitedLegalDocumentCode { get; set; } = string.Empty;
    public string CitedVerbatimLegalExcerpt { get; set; } = string.Empty;
    public WorkOrderStatus Status { get; set; }
    public string DefectConcept { get; set; } = "water stain";
    public decimal MeasureAreaM2 { get; set; }
    public decimal InvestorBoqEstimateAmount { get; set; }
    public string InvestorBoqSummary { get; set; } = string.Empty;
    public Guid? AssignedVendorId { get; set; }
    public string? AssignedVendorName { get; set; }
    public string AttachedAccessCodesSummary { get; set; } = string.Empty;
    public string AttachedShutoffValveNote { get; set; } = string.Empty;
    public string BeforePhotoDataUrl { get; set; } = string.Empty;
    public string SamBeforeOverlayDataUrl { get; set; } = string.Empty;
    public string? AfterPhotoDataUrl { get; set; }
    public string? SamAfterOverlayDataUrl { get; set; }
    public decimal? AfterDefectAreaM2 { get; set; }
    public bool IsSamVerified { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<VendorQuote> Quotes { get; set; } = [];
}

public sealed class VendorQuote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkOrderId { get; set; }
    public Guid VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public decimal QuotedGrossAmount { get; set; }
    public int WarrantyMonths { get; set; } = 24;
    public int LeadTimeDays { get; set; } = 2;
    public decimal VarianceVsBoqPercent { get; set; }
    public bool IsRecommended { get; set; }
}

public sealed class InsuranceClaimDossier : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid WorkOrderId { get; set; }
    public string ClaimReference { get; set; } = string.Empty;
    public string PolicyNumber { get; set; } = string.Empty;
    public string InsurerName { get; set; } = string.Empty;
    public string RootCauseSummary { get; set; } = string.Empty;
    public int AffectedUnitsCount { get; set; } = 3;
    public string AffectedRiserStack { get; set; } = "STACK-A1";
    public decimal TotalMeasuredDamageAreaM2 { get; set; }
    public decimal TotalDocumentedLoss { get; set; }
    public decimal DeductibleAmount { get; set; }
    public decimal NetClaimPayoutEstimate { get; set; }
    public string CoverageClauseCited { get; set; } = string.Empty;
    public string Status { get; set; } = "ReadyToSubmit";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ModificationOrArcRequest : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid UnitId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public ModificationRequestKind Kind { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow.AddDays(-4);
    public DateTime StatutoryDeadlineAt { get; set; } = DateTime.UtcNow.AddDays(26);
    public string StatutoryProtectionNote { get; set; } = string.Empty;
    public string CitedLegalDocumentCode { get; set; } = string.Empty;
    public string CitedVerbatimLegalExcerpt { get; set; } = string.Empty;
    public string AiRecommendation { get; set; } = "ApproveWithConditions";
    public string TechnicalConditionsOrDecisionText { get; set; } = string.Empty;
    public string Status { get; set; } = "PendingBoardReview";
}

public sealed class Resolution : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public VotingMajorityRule MajorityRule { get; set; } = VotingMajorityRule.ShareWeightedMajority;
    public bool SwitchToOneOwnerOneVoteTriggered { get; set; }
    public string ComplianceStatus { get; set; } = "VerifiedClean";
    public string ComplianceConflictSummary { get; set; } = string.Empty;
    public string CitedLegalDocumentCode { get; set; } = string.Empty;
    public string CitedVerbatimLegalExcerpt { get; set; } = string.Empty;
    public string SuggestedCompliantRewrite { get; set; } = string.Empty;
    public string? OptionALabel { get; set; }
    public string? OptionAPreviewDataUrl { get; set; }
    public string? OptionBLabel { get; set; }
    public string? OptionBPreviewDataUrl { get; set; }
    public string? QrPosterDataUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<Vote> Votes { get; set; } = [];
}

public sealed class Vote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResolutionId { get; set; }
    public Guid UnitId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public string VoterName { get; set; } = string.Empty;
    public string? ProxyName { get; set; }
    public VoteChoice Choice { get; set; }
    public decimal OwnershipShareWeight { get; set; }
    public VoteSourceChannel Channel { get; set; }
    public DateTime CastAt { get; set; } = DateTime.UtcNow;
}

public sealed class MeetingRecord : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime ScheduledAt { get; set; } = DateTime.UtcNow.AddDays(14);
    public string ConferenceUrl { get; set; } = string.Empty;
    public string DiarizedTranscriptText { get; set; } = string.Empty;
    public string GeneratedMinutesMarkdown { get; set; } = string.Empty;
    public string ExtractedActionItemsSummary { get; set; } = string.Empty;
}

public sealed class UtilityProviderSource : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public UtilityKind UtilityKind { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string OutageBulletinUrl { get; set; } = string.Empty;
    public string LiveFaultMapUrl { get; set; } = string.Empty;
    public string TariffScheduleUrl { get; set; } = string.Empty;
    public string EmergencyPhone { get; set; } = string.Empty;
    public decimal CurrentUnitTariff { get; set; }
    public string TariffUnit { get; set; } = "EUR/unit";
    public DateTime LastCheckedAt { get; set; } = DateTime.UtcNow;
    public string HealthStatus { get; set; } = "Online (200 OK)";
    public bool IsAiDiscovered { get; set; } = true;
}

public sealed class UtilityOutageNotice : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public UtilityKind UtilityKind { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string AffectedStreets { get; set; } = string.Empty;
    public DateTime StartAt { get; set; } = DateTime.UtcNow.AddHours(-1);
    public DateTime EstimatedEndAt { get; set; } = DateTime.UtcNow.AddHours(3);
    public bool IsEmergency { get; set; } = true;
    public string Summary { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class WasteCollectionSchedule : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string WasteFraction { get; set; } = string.Empty;
    public string CollectionDaysSummary { get; set; } = string.Empty;
    public DateTime NextPickupDate { get; set; }
    public bool ContaminationWarningActive { get; set; }
    public string? SegregationPosterDataUrl { get; set; }
}

public sealed class GreenSubsidyProgram : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string ProgramName { get; set; } = string.Empty;
    public string AuthorityName { get; set; } = string.Empty;
    public string UpgradeScope { get; set; } = string.Empty;
    public decimal EstimatedGrossCapex { get; set; }
    public decimal GrantSubsidyPercent { get; set; }
    public decimal GrantSubsidyAmount { get; set; }
    public decimal NetCommunityCapex { get; set; }
    public decimal EstimatedAnnualSavings { get; set; }
    public decimal PaybackYears { get; set; }
}

public sealed class AmenityBooking : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string ResourceName { get; set; } = string.Empty;
    public string UnitNumber { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string Purpose { get; set; } = string.Empty;
}

public sealed class AgentRun : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string TriggerSummary { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int TotalLatencyMs { get; set; }
    public int TotalTokens { get; set; }
    public bool UsedLiveApi { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public List<AgentStep> Steps { get; set; } = [];
}

public sealed class AgentStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentRunId { get; set; }
    public int StepOrder { get; set; }
    public string StepTitle { get; set; } = string.Empty;
    public string ModelOrToolName { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public int LatencyMs { get; set; }
}

public sealed class AiAction : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public AiActionKind Kind { get; set; }
    public string AgentName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Rationale { get; set; } = string.Empty;
    public string ModelUsed { get; set; } = "muse-spark-1.3";
    public double Confidence { get; set; } = 0.94;
    public AiActionStatus Status { get; set; } = AiActionStatus.Proposed;
    public string TargetEntitySummary { get; set; } = string.Empty;
    public string ProposedPayloadJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
}

public sealed class LegalDocumentSource : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public LegalDocumentKind Kind { get; set; } = LegalDocumentKind.NationalPropertyStatute;
    public string DocumentCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string IssuingAuthority { get; set; } = string.Empty;
    public string OfficialCitation { get; set; } = string.Empty;
    public string RegistrySourceUrl { get; set; } = string.Empty;
    public string AcquiredByModel { get; set; } = "muse-wonder + muse-spark-1.3";
    public DateTime EffectiveDate { get; set; } = new(2024, 1, 1);
    public DateTime AcquiredAtUtc { get; set; } = DateTime.UtcNow;
    public string KeyProvisionsSummary { get; set; } = string.Empty;
    public string VerbatimLegalText { get; set; } = string.Empty;
    public string AppliedInModules { get; set; } = string.Empty;
}

public sealed class SecurityAuditEvent : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public WorkspaceSecurityRole ActorRole { get; set; } = WorkspaceSecurityRole.PropertyManager;
    public string ActionCategory { get; set; } = string.Empty;
    public string Severity { get; set; } = "Info";
    public string ResourceDescriptor { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string PreviousChainHash { get; set; } = "GENESIS-0000000000000000";
    public string EntrySha256Hash { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WorkspaceUserAccount : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid? ManagementFirmId { get; set; }
    public string FirmGroupCode { get; set; } = "CG-FIRM-2026";
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public IdentityProvider Provider { get; set; } = IdentityProvider.EmailPassword;
    public string ProviderSubjectId { get; set; } = string.Empty;
    public string PasswordSaltAndHash { get; set; } = string.Empty;
    public WorkspaceSecurityRole Role { get; set; } = WorkspaceSecurityRole.PropertyManager;
    public string? LinkedUnitNumber { get; set; }
    public string? ContractorCompanyOrTrade { get; set; }
    public ContractorPropertyScope ContractorScope { get; set; } = ContractorPropertyScope.SpecificProperty;
    public string AllowedCommunityIdsCsv { get; set; } = string.Empty;
    public bool IsBlankRegisteredPendingAssignment { get; set; }
    public bool GrantedByPropertyManager { get; set; } = true;
    public bool MfaVerified { get; set; } = true;
    public DateTime LastLoginAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class FirmManagerGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagementFirmId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<FirmManagerMessage> Messages { get; set; } = [];
}

public sealed class FirmManagerMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagementFirmId { get; set; }
    public Guid FirmManagerGroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public Guid? SenderUserId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public WorkspaceSecurityRole SenderRole { get; set; } = WorkspaceSecurityRole.FirmManager;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderPhone { get; set; } = string.Empty;
    public string PropertyScopeName { get; set; } = "All Firm Properties";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsPriorityNotice { get; set; }
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WorkspaceInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagementFirmId { get; set; }
    public string FirmGroupCode { get; set; } = "CG-FIRM-2026";
    public string InvitationToken { get; set; } = string.Empty;
    public string InvitationTokenHash { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public string RecipientPhone { get; set; } = string.Empty;
    public WorkspaceSecurityRole TargetRole { get; set; } = WorkspaceSecurityRole.Resident;
    public InvitationAssignmentMode AssignmentMode { get; set; } = InvitationAssignmentMode.AutoAssignSpecificProperty;
    public Guid? TargetCommunityId { get; set; }
    public string TargetCommunityName { get; set; } = string.Empty;
    public string? TargetUnitNumber { get; set; }
    public string? ContractorCompanyOrTrade { get; set; }
    public WorkspaceInvitationStatus Status { get; set; } = WorkspaceInvitationStatus.Pending;
    public string RegistrationRelativeUrl { get; set; } = string.Empty;
    public string IssuedByManagerName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
    public DateTime? AcceptedAtUtc { get; set; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAtUtc;
}

public sealed class ResidentMembershipApplication : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public WorkspaceSecurityRole RequestedRole { get; set; } = WorkspaceSecurityRole.Resident;
    public Guid? UnitId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public string? ContractorCompanyOrTrade { get; set; }
    public string ApplicantFullName { get; set; } = string.Empty;
    public string ApplicantEmail { get; set; } = string.Empty;
    public string ApplicantPhone { get; set; } = string.Empty;
    public IdentityProvider Provider { get; set; } = IdentityProvider.EmailPassword;
    public string OwnershipOrLeaseProofSummary { get; set; } = string.Empty;
    public string AssignedGroupsSummary { get; set; } = string.Empty;
    public ResidentMembershipStatus Status { get; set; } = ResidentMembershipStatus.PendingApproval;
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionNote { get; set; }
}

public sealed class ResidentGroup : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public ResidentGroupKind Kind { get; set; } = ResidentGroupKind.BuildingWide;
    public string TargetScope { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int MemberUnitsCount { get; set; }
    public List<CommunityMessage> Messages { get; set; } = [];
}

public sealed class CommunityMessage : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid ResidentGroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderUnitOrRole { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsPinnedAnnouncement { get; set; }
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ResidentDirectMessage : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid? ManagementFirmId { get; set; }
    public string ThreadKey { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderEmail { get; set; } = string.Empty;
    public WorkspaceSecurityRole SenderRole { get; set; } = WorkspaceSecurityRole.Resident;
    public string SenderUnitNumber { get; set; } = string.Empty;
    public string RecipientScope { get; set; } = "PropertyManager";
    public string RecipientName { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public bool IsReadByRecipient { get; set; }
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}
