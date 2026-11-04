using CommonGround.Domain.Enums;

namespace CommonGround.Domain.Entities;

public interface ICommunityScoped
{
    Guid CommunityId { get; set; }
}

public sealed class ManagementFirm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string FirmGroupCode { get; set; } = "CG-FIRM-2026";
    public SystemOperatingMode OperatingMode { get; set; } = SystemOperatingMode.HousingAssociation;

    // Firm-style contact details (HousingAssociation / Commercial Property Management Firm)
    public string ManagingFirmLegalName { get; set; } = string.Empty;
    public string FirmTaxIdOrNip { get; set; } = string.Empty;
    public string LicenseNumber { get; set; } = string.Empty;
    public string LeadDirectorName { get; set; } = string.Empty;
    public string OfficeAddress { get; set; } = string.Empty;
    public string SupportEmail { get; set; } = string.Empty;
    public string EmergencyDispatchPhone { get; set; } = string.Empty;

    // HOA-style contact details (UsHoa / Self-Governed Association Board)
    public string AssociationLegalName { get; set; } = string.Empty;
    public string HoaEinNumber { get; set; } = string.Empty;
    public string BoardPresidentName { get; set; } = string.Empty;
    public string ManagementAgentContact { get; set; } = string.Empty;
    public string ClubhouseOrOfficeAddress { get; set; } = string.Empty;
    public string HoaBoardEmail { get; set; } = string.Empty;
    public string HoaHotlinePhone { get; set; } = string.Empty;

    public bool RequireEmailConfirmation { get; set; } = false;
    public bool IsEmailConfirmed { get; set; } = true;
    public string? EmailConfirmationTokenHash { get; set; }
    public DateTime? AcceptedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<Community> Communities { get; set; } = [];
    public List<FirmMembership> FirmMemberships { get; set; } = [];
    public List<Invitation> FirmInvitations { get; set; } = [];
    public List<JoinRequest> JoinRequests { get; set; } = [];
    public List<ResidentProfile> ResidentProfiles { get; set; } = [];
    public List<ResidentRegistrationToken> RegistrationTokens { get; set; } = [];
    public List<ResidentRegistrationRequest> RegistrationRequests { get; set; } = [];
    // Legacy compat for persistence migration wave. Use FirmMemberships instead.
    public List<WorkspaceInvitation> Invitations { get; set; } = [];
    // Legacy compat for persistence migration wave. Use FirmMemberships instead.
    public List<WorkspaceAccountMembership> AccountMemberships { get; set; } = [];
    public List<FirmManagerGroup> ManagerGroups { get; set; } = [];
    public List<FirmManagerMessage> ManagerMessages { get; set; } = [];
}

public sealed class Community
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CommunityAccountNumber { get; set; } = Calculators.AccountNumberGenerator.NewCommunityAccountNumber();
    public Guid? ManagementFirmId { get; set; }
    public string FirmGroupCode { get; set; } = "CG-FIRM-2026";
    public Guid? AssignedPropertyManagerId { get; set; }
    public string AssignedPropertyManagerName { get; set; } = string.Empty;
    public string AssignedPropertyManagerEmail { get; set; } = string.Empty;
    public string AssignedPropertyManagerPhone { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public CommunityStatus Status { get; set; } = CommunityStatus.Active;
    public string PropertyTypeConfiguration { get; set; } = "Residential Condominium & HOA";
    public string AdministrativeEmail { get; set; } = string.Empty;
    public string AdministrativePhone { get; set; } = string.Empty;
    public bool EnableHoaGovernance { get; set; } = true;
    public bool EnableShareWeightedVoting { get; set; } = true;
    public bool EnableArcRequests { get; set; } = true;
    public bool EnableSubMeterReconciliation { get; set; } = true;
    public bool EnableReserveStudy { get; set; } = true;
    public RegistrationApprovalMode DefaultRegistrationApprovalMode { get; set; } = RegistrationApprovalMode.ManualFirmApproval;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public SystemOperatingMode OperatingMode { get; set; } = SystemOperatingMode.HousingAssociation;
    public string StreetAddress { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string PostalOrZipCode { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "EU";
    public string CurrencyCode { get; set; } = "EUR";
    public string TaxIdOrEin { get; set; } = string.Empty;
    public string RegistryNumber { get; set; } = string.Empty;
    public string LocalMunicipalityOrCounty { get; set; } = string.Empty;
    public string LocalTaxOffice { get; set; } = string.Empty;
    public string CompetentCourtOrRecorder { get; set; } = string.Empty;
    public DateTime ConstructionHandoverDate { get; set; } = DateTime.UtcNow.AddYears(-3);
    public bool IsUnderDeveloperWarranty => ConstructionHandoverDate.AddYears(5) > DateTime.UtcNow;

    public decimal OperatingRatePerM2 { get; set; } = 3.20m;
    public decimal RepairFundRatePerM2 { get; set; } = 1.80m;
    public decimal WasteFeePerOccupant { get; set; } = 28.00m;
    public decimal ColdWaterTariffPerM3 { get; set; } = 11.50m;
    public decimal HotWaterHeatingCostPerM3 { get; set; } = 24.00m;
    public decimal DistrictHeatingTariffPerGj { get; set; } = 82.00m;
    public decimal CommonElectricityTariffPerKwh { get; set; } = 0.88m;

    public decimal ContractedHeatingPowerMw { get; set; } = 0.42m;
    public decimal PeakMeasuredHeatingPowerMw { get; set; } = 0.29m;
    public decimal ContractedElectricalPowerKw { get; set; } = 65.0m;
    public decimal PeakMeasuredElectricalPowerKw { get; set; } = 38.0m;

    public string StatuteFullText { get; set; } = string.Empty;
    public string HouseRulesFullText { get; set; } = string.Empty;
    public string UtilityPolicyFullText { get; set; } = string.Empty;
    public DateTime? LastWebEnrichedAt { get; set; }

    public List<Building> Buildings { get; set; } = [];
    public List<Unit> Units { get; set; } = [];
    public List<LedgerAccount> LedgerAccounts { get; set; } = [];
    public List<JournalEntry> JournalEntries { get; set; } = [];
    public List<Vendor> Vendors { get; set; } = [];
    public List<VendorBill> VendorBills { get; set; } = [];
    public List<BankTransactionMatch> BankTransactions { get; set; } = [];
    public List<AncillaryCommercialContract> CommercialContracts { get; set; } = [];
    public List<ReserveStudyComponent> ReserveComponents { get; set; } = [];
    public List<WorkOrder> WorkOrders { get; set; } = [];
    public List<ModificationOrArcRequest> ModificationRequests { get; set; } = [];
    public List<Resolution> Resolutions { get; set; } = [];
    public List<MeetingRecord> Meetings { get; set; } = [];
    public List<UtilityProviderSource> UtilityProviderSources { get; set; } = [];
    public List<UtilityOutageNotice> UtilityOutages { get; set; } = [];
    public List<EmergencyAndAuthorityContact> EmergencyContacts { get; set; } = [];
    public List<InfrastructurePhoto> InfrastructurePhotos { get; set; } = [];
    public List<BuildingAccessCode> AccessCodes { get; set; } = [];
    public List<StatutoryInspection> Inspections { get; set; } = [];
    public List<WasteCollectionSchedule> WasteSchedules { get; set; } = [];
    public List<GreenSubsidyProgram> GreenSubsidies { get; set; } = [];
    public List<AmenityBooking> AmenityBookings { get; set; } = [];
    public List<LegalDocumentSource> LegalDocuments { get; set; } = [];
    public List<SecurityAuditEvent> SecurityAuditEvents { get; set; } = [];
    public List<PropertyAnnouncement> Announcements { get; set; } = [];
    public List<MembershipPropertyAccess> PropertyAccessGrants { get; set; } = [];
    public List<MembershipUnitLink> UnitLinks { get; set; } = [];
    public List<WorkspaceUserAccount> UserAccounts { get; set; } = [];
    public List<ResidentMembershipApplication> ResidentMemberships { get; set; } = [];
    public List<ResidentGroup> ResidentGroups { get; set; } = [];
    public List<CommunityMessage> CommunityMessages { get; set; } = [];
    public List<AiAction> AiActions { get; set; } = [];
    public List<AgentRun> AgentRuns { get; set; } = [];
    public List<CommunityContact> CommunityContacts { get; set; } = [];
    public List<UnitOwnership> UnitOwnerships { get; set; } = [];
    public List<ResidentUnitAssignment> ResidentAssignments { get; set; } = [];
    public List<ResidentHouseholdMember> HouseholdMembers { get; set; } = [];
    public List<ResidentDocument> ResidentDocuments { get; set; } = [];
    public List<ResidentRegistrationToken> RegistrationTokens { get; set; } = [];
    public List<ResidentRegistrationRequest> RegistrationRequests { get; set; } = [];
}

public sealed class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string PasswordSaltAndHash { get; set; } = string.Empty;
    public bool EmailConfirmed { get; set; }
    public string? EmailConfirmationTokenHash { get; set; }
    public IdentityProvider Provider { get; set; } = IdentityProvider.EmailPassword;
    public List<FirmMembership> Memberships { get; set; } = [];
}

public sealed class FirmMembership
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid ManagementFirmId { get; set; }
    public WorkspaceSecurityRole Role { get; set; } = WorkspaceSecurityRole.Resident;
    public WorkspaceMembershipStatus Status { get; set; } = WorkspaceMembershipStatus.PendingApproval;
    public WorkspacePermissionFlags GrantedPermissions { get; set; }
    public Guid? InvitedByMembershipId { get; set; }
    public Guid? DecidedByMembershipId { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public User? User { get; set; }
    public ManagementFirm? ManagementFirm { get; set; }
    public List<MembershipPropertyAccess> PropertyAccesses { get; set; } = [];
    public List<MembershipUnitLink> UnitLinks { get; set; } = [];
}

public sealed class MembershipPropertyAccess : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FirmMembershipId { get; set; }
    public Guid CommunityId { get; set; }
    public bool IsPrimaryManager { get; set; }
    public FirmMembership? FirmMembership { get; set; }
    public Community? Community { get; set; }
}

public sealed class MembershipUnitLink : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FirmMembershipId { get; set; }
    public Guid CommunityId { get; set; }
    public Guid UnitId { get; set; }
    public MembershipUnitRelationshipKind RelationshipKind { get; set; } = MembershipUnitRelationshipKind.Owner;
    public FirmMembership? FirmMembership { get; set; }
    public Unit? Unit { get; set; }
}

public sealed class Invitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagementFirmId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string RecipientEmail { get; set; } = string.Empty;
    public string RecipientName { get; set; } = string.Empty;
    public WorkspaceSecurityRole TargetRole { get; set; } = WorkspaceSecurityRole.Resident;
    public WorkspacePermissionFlags GrantedPermissions { get; set; }
    public bool RequiresApproval { get; set; } = true;
    public string PropertyIdsJson { get; set; } = "[]";
    public Guid? TargetUnitId { get; set; }
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(7);
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;
    public ManagementFirm? ManagementFirm { get; set; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAtUtc;
}

public sealed class JoinRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagementFirmId { get; set; }
    public Guid FirmMembershipId { get; set; }
    public WorkspaceSecurityRole RequestedRole { get; set; } = WorkspaceSecurityRole.Resident;
    public Guid? RequestedCommunityId { get; set; }
    public string RequestedUnitNumber { get; set; } = string.Empty;
    public string ProofOrCompanyNote { get; set; } = string.Empty;
    public JoinRequestStatus Status { get; set; } = JoinRequestStatus.PendingApproval;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public FirmMembership? FirmMembership { get; set; }
    public ManagementFirm? ManagementFirm { get; set; }
}

public sealed class Building : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string StreetAddress { get; set; } = string.Empty;
    public int FloorsCount { get; set; } = 4;
    public int StairwellsCount { get; set; } = 2;
    public int ConstructionYear { get; set; } = 2023;
    public string HeatingType { get; set; } = "Municipal District Heating Substation";
    public string RoofType { get; set; } = "Flat EPDM Membrane with Ballast";
    public string InsurancePolicyNumber { get; set; } = "POL-ALLRISK-2026-8841";
    public string InsuranceProviderName { get; set; } = "Allianz Property Commercial";
    public decimal InsuranceDeductible { get; set; } = 500.00m;
    public List<VerticalRiserStack> RiserStacks { get; set; } = [];
}

public sealed class VerticalRiserStack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BuildingId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShutoffValveLocation { get; set; } = string.Empty;
    public Guid? ShutoffPhotoId { get; set; }
}

public sealed class Unit : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public int StairwellNumber { get; set; } = 1;
    public int FloorNumber { get; set; } = 1;
    public string VerticalStackCode { get; set; } = "STACK-A1";
    public UnitKind Kind { get; set; } = UnitKind.Apartment;
    public decimal FloorAreaM2 { get; set; } = 58.0m;
    public decimal StorageAreaM2 { get; set; } = 4.5m;
    public int ShareNumerator { get; set; } = 625;
    public int ShareDenominator { get; set; } = 10000;
    public decimal OwnershipShare => ShareDenominator > 0 ? Math.Round((decimal)ShareNumerator / ShareDenominator, 6) : 0m;
    public int DeclaredOccupants { get; set; } = 2;
    public string PrimaryOwnerName { get; set; } = string.Empty;
    public string PrimaryOwnerEmail { get; set; } = string.Empty;
    public string PrimaryOwnerPhone { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "EN";
    public string? CoOwnerName { get; set; }
    public string? VotingProxyName { get; set; }
    public bool IsShortTermRental { get; set; }
    public string? ParkingBayNumber { get; set; }
    public string? AccessFobCode { get; set; }
    public string VirtualIbanOrLockboxCode { get; set; } = string.Empty;
    public decimal CurrentBalance { get; set; }
    public int DaysPastDue { get; set; }
    public ArrearsStage ArrearsStage { get; set; } = ArrearsStage.Current;
    public int RoomsCount { get; set; } = 3;
    public int BathroomsCount { get; set; } = 1;
    public int OccupancyCapacity { get; set; } = 4;
    public UnitOccupancyStatus OccupancyStatus { get; set; } = UnitOccupancyStatus.OwnerOccupied;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<UtilityMeter> Meters { get; set; } = [];
    public List<MonthlyAssessment> Assessments { get; set; } = [];
    public List<UnitOwnership> Ownerships { get; set; } = [];
    public List<ResidentUnitAssignment> ResidentAssignments { get; set; } = [];
    public List<ResidentHouseholdMember> HouseholdMembers { get; set; } = [];
    public List<ResidentDocument> Documents { get; set; } = [];
    public List<ResidentRegistrationToken> RegistrationTokens { get; set; } = [];
}

public sealed class UtilityMeter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UnitId { get; set; }
    public UtilityKind Kind { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public DateTime CalibrationExpiryDate { get; set; } = DateTime.UtcNow.AddYears(2);
    public decimal PreviousReadingValue { get; set; }
    public decimal LastReadingValue { get; set; }
    public DateTime LastReadingDate { get; set; } = DateTime.UtcNow.AddDays(-5);
    public decimal RollingMonthlyAverage { get; set; } = 4.2m;
    public bool HasSilentLeakAnomaly { get; set; }
    public string? AnomalyNote { get; set; }
    public List<MeterReading> Readings { get; set; } = [];
}

public sealed class MeterReading
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UtilityMeterId { get; set; }
    public decimal ReadingValue { get; set; }
    public decimal ConsumptionDelta { get; set; }
    public DateTime ReadingDate { get; set; } = DateTime.UtcNow;
    public string SourceChannel { get; set; } = "ResidentPhotoOcr";
    public bool IsAnomaly { get; set; }
    public string? AnomalyReason { get; set; }
}

public sealed class InfrastructurePhoto : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = "WaterValve";
    public string RiserStackOrZone { get; set; } = string.Empty;
    public string PhysicalLocationNote { get; set; } = string.Empty;
    public string ShutoffInstructions { get; set; } = string.Empty;
    public string ColorTag { get; set; } = "#0284c7";
    public string ImageDataUrl { get; set; } = string.Empty;
    public string SamAnnotatedDataUrl { get; set; } = string.Empty;
}

public sealed class BuildingAccessCode : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public string TargetArea { get; set; } = string.Empty;
    public string ResidentCode { get; set; } = string.Empty;
    public string ContractorOrCourierCode { get; set; } = string.Empty;
    public string KeySafeLocation { get; set; } = string.Empty;
    public bool ShareWithAssignedContractors { get; set; } = true;
}

public sealed class EmergencyAndAuthorityContact : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string Category { get; set; } = EmergencyContactCategories.EmergencyService;
    public string Name { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? AddressOrNotes { get; set; }
    public int SortOrder { get; set; }
    public bool IsAiDiscovered { get; set; } = true;
}

public static class EmergencyContactCategories
{
    public const string EmergencyService = "EmergencyService";
    public const string PolicePrecinct = "PolicePrecinct";
    public const string DistrictPoliceOfficer = "DistrictPoliceOfficer";
    public const string MunicipalGuard = "MunicipalGuard";
    public const string FireStation = "FireStation";
    public const string BuildingInspectorate = "BuildingInspectorate";
    public const string RecyclingDepot = "RecyclingDepot";
}

/// <summary>
/// Strongly typed AI web discovery harvest covering all 11 plan section 3.2 categories.
/// Persisted indirectly via Community and related entity rows; this DTO carries the parsed model output.
/// </summary>
public sealed record PropertyAiDiscoveryDossier(
    string SuggestedAssociationName,
    SystemOperatingMode SuggestedOperatingMode,
    string CountryCode,
    string CurrencyCode,
    string LocalMunicipalityOrCounty,
    string CompetentCourtOrRecorder,
    string LocalTaxOffice,
    string RegistryNumber,
    IReadOnlyList<DiscoveredUtilityOperator> ElectricityOperator,
    IReadOnlyList<DiscoveredUtilityOperator> GasOperator,
    IReadOnlyList<DiscoveredUtilityOperator> WaterOperator,
    IReadOnlyList<DiscoveredUtilityOperator> HeatingOperator,
    IReadOnlyList<DiscoveredUtilityOperator> WasteOperator,
    IReadOnlyList<DiscoveredAuthorityContact> PolicePrecinct,
    IReadOnlyList<DiscoveredAuthorityContact> DistrictOfficer,
    IReadOnlyList<DiscoveredAuthorityContact> MunicipalGuard,
    IReadOnlyList<DiscoveredAuthorityContact> FireStation,
    IReadOnlyList<DiscoveredAuthorityContact> BuildingInspectorate,
    IReadOnlyList<DiscoveredAuthorityContact> RecyclingDepot,
    IReadOnlyList<DiscoveredContractor> Contractors,
    DiscoveredBuildingProfile BuildingProfile,
    IReadOnlyList<DiscoveredGrantOrOrdinance> GrantsAndOrdinances,
    decimal OperatingRatePerM2,
    decimal RepairFundRatePerM2,
    decimal WasteFeePerOccupant,
    decimal ColdWaterTariffPerM3,
    decimal HotWaterHeatingCostPerM3,
    decimal DistrictHeatingTariffPerGj,
    decimal CommonElectricityTariffPerKwh,
    decimal ContractedHeatingPowerMw,
    decimal ContractedElectricalPowerKw,
    IReadOnlyList<string> SourceUrls);

public sealed record DiscoveredUtilityOperator(
    string UtilityKind,
    string ProviderName,
    string EmergencyPhone,
    string OutageBulletinUrl,
    string LiveFaultMapUrl,
    string TariffScheduleUrl,
    decimal CurrentUnitTariff,
    string TariffUnit);

public sealed record DiscoveredAuthorityContact(
    string Category,
    string Name,
    string PhoneNumber,
    string AddressOrNotes,
    int SortOrder);

public sealed record DiscoveredContractor(
    string Trade,
    string CompanyName,
    string Phone,
    string Email,
    string Website,
    decimal TypicalHourlyRate);

public sealed record DiscoveredBuildingProfile(
    int EstimatedConstructionYear,
    int FloorsCount,
    int StairwellsCount,
    string HeatingType,
    string RoofType,
    double RooftopSolarPotentialKwp,
    string FloodZone,
    string WindZone,
    string AcousticZone,
    string AirQualityNote);

public sealed record DiscoveredGrantOrOrdinance(
    string Kind,
    string ProgramOrDocumentCode,
    string Title,
    string IssuingAuthority,
    string OfficialCitation,
    string SourceUrl,
    string Summary,
    string VerbatimText);

public sealed class StatutoryInspection : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public InspectionKind Kind { get; set; }
    public DateTime LastPerformedDate { get; set; }
    public DateTime NextDueDate { get; set; }
    public string InspectorCompany { get; set; } = string.Empty;
    public string Status { get; set; } = "Valid";
    public string SummaryOfDefects { get; set; } = string.Empty;
    public int ExtractedWorkOrdersCount { get; set; }
}

/// <summary>
/// Permanent Resident identity independent of apartment assignment, Community, or ManagementFirm.
/// Holds the immutable ResidentAccountNumber (e.g. RES-00001284).
/// </summary>
public sealed class ResidentProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ResidentAccountNumber { get; set; } = Calculators.AccountNumberGenerator.NewResidentAccountNumber();
    public Guid? UserId { get; set; }
    public Guid? ManagementFirmId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? ProfilePhotoUrl { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string PrimaryEmail { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string AdditionalContactDetails { get; set; } = string.Empty;
    public ResidentAccountStatus AccountStatus { get; set; } = ResidentAccountStatus.Active;
    public ResidentVerificationStatus VerificationStatus { get; set; } = ResidentVerificationStatus.Verified;
    public string PreferredLanguage { get; set; } = "EN";
    public string PreferredContactMethod { get; set; } = "Email";
    public string EmergencyContactName { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;
    public string EmergencyContactRelation { get; set; } = string.Empty;
    public DateTime RegistrationDateUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
    public string Notes { get; set; } = string.Empty;
    public List<ResidentUnitAssignment> Assignments { get; set; } = [];
    public List<UnitOwnership> Ownerships { get; set; } = [];
    public List<ResidentDocument> Documents { get; set; } = [];
}

/// <summary>
/// Time-bounded occupancy/tenancy relationship between a ResidentProfile and a Unit.
/// </summary>
public sealed class ResidentUnitAssignment : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ResidentProfileId { get; set; }
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? ManagementFirmId { get; set; }
    public ResidentAssignmentType AssignmentType { get; set; } = ResidentAssignmentType.PrimaryResident;
    public UnitAssignmentStatus Status { get; set; } = UnitAssignmentStatus.Active;
    public bool IsPrimaryResident { get; set; } = true;
    public DateTime MoveInDateUtc { get; set; } = DateTime.UtcNow;
    public DateTime? MoveOutDateUtc { get; set; }
    public Guid? ApprovedByMembershipId { get; set; }
    public string ApprovedByName { get; set; } = string.Empty;
    public DateTime AssignedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAtUtc { get; set; }
    public string AssignmentReasonOrNotes { get; set; } = string.Empty;
    public string? ConflictOverrideReason { get; set; }
    public ResidentProfile? ResidentProfile { get; set; }
    public Unit? Unit { get; set; }
}

/// <summary>
/// Legal or administrative ownership record for a Unit, separate from occupancy/residency.
/// </summary>
public sealed class UnitOwnership : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? ResidentProfileId { get; set; }
    public string OwnerFullName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string OwnerPhone { get; set; } = string.Empty;
    public UnitOwnershipRole OwnershipRole { get; set; } = UnitOwnershipRole.PrimaryOwner;
    public int ShareNumerator { get; set; } = 625;
    public int ShareDenominator { get; set; } = 10000;
    public decimal OwnershipPercentage { get; set; } = 100.0m;
    public DateTime EffectiveStartDateUtc { get; set; } = DateTime.UtcNow.AddYears(-1);
    public DateTime? EffectiveEndDateUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Household member or additional occupant associated with a Unit and/or Primary Resident Assignment.
/// </summary>
public sealed class ResidentHouseholdMember : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? PrimaryAssignmentId { get; set; }
    public Guid? LinkedResidentProfileId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string RelationshipToPrimary { get; set; } = "Household Member";
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool HasPortalAccess { get; set; }
    public DateTime OccupancyStartDateUtc { get; set; } = DateTime.UtcNow;
    public DateTime? OccupancyEndDateUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; } = string.Empty;
}

/// <summary>
/// Document vault entry linked to a ResidentProfile, Unit, and/or Community.
/// </summary>
public sealed class ResidentDocument : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? ResidentProfileId { get; set; }
    public ResidentDocumentCategory Category { get; set; } = ResidentDocumentCategory.Other;
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/pdf";
    public long FileSizeBytes { get; set; }
    public string StoragePathOrDataUrl { get; set; } = string.Empty;
    public string SummaryOrNotes { get; set; } = string.Empty;
    public string UploadedByName { get; set; } = string.Empty;
    public string UploadedByRole { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsVisibleToResident { get; set; } = true;
}

/// <summary>
/// Structured Community Contact directory entry (Management Office, Property Manager, Board, Emergency, Technical, Accounting, Maintenance, Other).
/// </summary>
public sealed class CommunityContact : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public CommunityContactCategory Category { get; set; } = CommunityContactCategory.ManagementOffice;
    public string ContactName { get; set; } = string.Empty;
    public string RoleTitle { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string OfficeHours { get; set; } = "Mon-Fri 08:00-17:00";
    public string AddressOrLocation { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsVisibleToResidents { get; set; } = true;
}

/// <summary>
/// Cryptographically hashed registration-only link token for Resident registration.
/// Never exposes Community, Unit, Building, Owner, or Financial information to the public page.
/// </summary>
public sealed class ResidentRegistrationToken : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagementFirmId { get; set; }
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public Guid TargetUnitId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string LabelOrReference { get; set; } = string.Empty;
    public RegistrationApprovalMode ApprovalMode { get; set; } = RegistrationApprovalMode.ManualFirmApproval;
    public ResidentAssignmentType IntendedAssignmentType { get; set; } = ResidentAssignmentType.PrimaryResident;
    public WorkspaceSecurityRole IntendedRole { get; set; } = WorkspaceSecurityRole.Resident;
    public int MaxUses { get; set; } = 1;
    public int UseCount { get; set; }
    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddDays(14);
    public RegistrationTokenStatus Status { get; set; } = RegistrationTokenStatus.Active;
    public Guid? CreatedByMembershipId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAtUtc { get; set; }
    public bool IsUsable => Status == RegistrationTokenStatus.Active && DateTime.UtcNow <= ExpiresAtUtc && UseCount < MaxUses;
}

/// <summary>
/// Submitted Resident registration request resolved strictly server-side from a ResidentRegistrationToken.
/// </summary>
public sealed class ResidentRegistrationRequest : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ManagementFirmId { get; set; }
    public Guid CommunityId { get; set; }
    public Guid BuildingId { get; set; }
    public Guid TargetUnitId { get; set; }
    public Guid RegistrationTokenId { get; set; }
    public string SubmittedFirstName { get; set; } = string.Empty;
    public string SubmittedLastName { get; set; } = string.Empty;
    public string SubmittedFullName { get; set; } = string.Empty;
    public string SubmittedEmail { get; set; } = string.Empty;
    public string SubmittedPhone { get; set; } = string.Empty;
    public string PreferredLanguage { get; set; } = "EN";
    public string EmergencyContactName { get; set; } = string.Empty;
    public string EmergencyContactPhone { get; set; } = string.Empty;
    public string ResidentNotes { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public Guid? MatchedOrCreatedResidentProfileId { get; set; }
    public RegistrationApprovalMode ApprovalMode { get; set; } = RegistrationApprovalMode.ManualFirmApproval;
    public RegistrationRequestStatus Status { get; set; } = RegistrationRequestStatus.PendingApproval;
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAtUtc { get; set; }
    public Guid? ReviewedByMembershipId { get; set; }
    public string ReviewedByName { get; set; } = string.Empty;
    public string DecisionNotesOrRejectionReason { get; set; } = string.Empty;
}
