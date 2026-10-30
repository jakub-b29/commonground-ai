using CommonGround.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Persistence;

public interface IAppDbContextFactory
{
    Task<IAppDbContext> CreateDbContextAsync(CancellationToken ct = default);
}

public interface IAppDbContext : IAsyncDisposable
{
    DbSet<Community> Communities { get; }
    DbSet<Building> Buildings { get; }
    DbSet<VerticalRiserStack> RiserStacks { get; }
    DbSet<Unit> Units { get; }
    DbSet<UtilityMeter> UtilityMeters { get; }
    DbSet<MeterReading> MeterReadings { get; }
    DbSet<InfrastructurePhoto> InfrastructurePhotos { get; }
    DbSet<BuildingAccessCode> AccessCodes { get; }
    DbSet<EmergencyAndAuthorityContact> EmergencyContacts { get; }
    DbSet<StatutoryInspection> Inspections { get; }
    DbSet<LedgerAccount> LedgerAccounts { get; }
    DbSet<JournalEntry> JournalEntries { get; }
    DbSet<JournalLine> JournalLines { get; }
    DbSet<MonthlyAssessment> MonthlyAssessments { get; }
    DbSet<UtilitySettlementPeriod> UtilitySettlementPeriods { get; }
    DbSet<UtilitySettlementUnitResult> UtilitySettlementUnitResults { get; }
    DbSet<Vendor> Vendors { get; }
    DbSet<VendorBill> VendorBills { get; }
    DbSet<BankTransactionMatch> BankTransactions { get; }
    DbSet<AncillaryCommercialContract> CommercialContracts { get; }
    DbSet<ReserveStudyComponent> ReserveComponents { get; }
    DbSet<WorkOrder> WorkOrders { get; }
    DbSet<VendorQuote> VendorQuotes { get; }
    DbSet<InsuranceClaimDossier> InsuranceClaims { get; }
    DbSet<ModificationOrArcRequest> ModificationRequests { get; }
    DbSet<Resolution> Resolutions { get; }
    DbSet<Vote> Votes { get; }
    DbSet<MeetingRecord> Meetings { get; }
    DbSet<UtilityProviderSource> UtilityProviderSources { get; }
    DbSet<UtilityOutageNotice> UtilityOutages { get; }
    DbSet<WasteCollectionSchedule> WasteSchedules { get; }
    DbSet<GreenSubsidyProgram> GreenSubsidies { get; }
    DbSet<AmenityBooking> AmenityBookings { get; }
    DbSet<AgentRun> AgentRuns { get; }
    DbSet<AgentStep> AgentSteps { get; }
    DbSet<AiAction> AiActions { get; }
    DbSet<LegalDocumentSource> LegalDocuments { get; }
    DbSet<SecurityAuditEvent> SecurityAuditEvents { get; }
    DbSet<WorkspaceUserAccount> UserAccounts { get; }
    DbSet<ResidentMembershipApplication> ResidentMemberships { get; }
    DbSet<ResidentGroup> ResidentGroups { get; }
    DbSet<CommunityMessage> CommunityMessages { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
