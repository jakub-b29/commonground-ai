using CommonGround.Domain.Enums;

namespace CommonGround.Domain.Entities;

public interface ICommunityScoped
{
    Guid CommunityId { get; set; }
}

public sealed class Community
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
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
    public List<WorkspaceUserAccount> UserAccounts { get; set; } = [];
    public List<AiAction> AiActions { get; set; } = [];
    public List<AgentRun> AgentRuns { get; set; } = [];
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
    public List<UtilityMeter> Meters { get; set; } = [];
    public List<MonthlyAssessment> Assessments { get; set; } = [];
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
    public string Category { get; set; } = "EmergencyService";
    public string Name { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string? AddressOrNotes { get; set; }
    public int SortOrder { get; set; }
    public bool IsAiDiscovered { get; set; } = true;
}

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
