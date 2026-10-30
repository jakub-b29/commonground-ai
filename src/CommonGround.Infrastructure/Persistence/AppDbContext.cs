using CommonGround.Application.Persistence;
using CommonGround.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Community> Communities => Set<Community>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<VerticalRiserStack> RiserStacks => Set<VerticalRiserStack>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<UtilityMeter> UtilityMeters => Set<UtilityMeter>();
    public DbSet<MeterReading> MeterReadings => Set<MeterReading>();
    public DbSet<InfrastructurePhoto> InfrastructurePhotos => Set<InfrastructurePhoto>();
    public DbSet<BuildingAccessCode> AccessCodes => Set<BuildingAccessCode>();
    public DbSet<EmergencyAndAuthorityContact> EmergencyContacts => Set<EmergencyAndAuthorityContact>();
    public DbSet<StatutoryInspection> Inspections => Set<StatutoryInspection>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<MonthlyAssessment> MonthlyAssessments => Set<MonthlyAssessment>();
    public DbSet<UtilitySettlementPeriod> UtilitySettlementPeriods => Set<UtilitySettlementPeriod>();
    public DbSet<UtilitySettlementUnitResult> UtilitySettlementUnitResults => Set<UtilitySettlementUnitResult>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<VendorBill> VendorBills => Set<VendorBill>();
    public DbSet<BankTransactionMatch> BankTransactions => Set<BankTransactionMatch>();
    public DbSet<AncillaryCommercialContract> CommercialContracts => Set<AncillaryCommercialContract>();
    public DbSet<ReserveStudyComponent> ReserveComponents => Set<ReserveStudyComponent>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<VendorQuote> VendorQuotes => Set<VendorQuote>();
    public DbSet<InsuranceClaimDossier> InsuranceClaims => Set<InsuranceClaimDossier>();
    public DbSet<ModificationOrArcRequest> ModificationRequests => Set<ModificationOrArcRequest>();
    public DbSet<Resolution> Resolutions => Set<Resolution>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<MeetingRecord> Meetings => Set<MeetingRecord>();
    public DbSet<UtilityProviderSource> UtilityProviderSources => Set<UtilityProviderSource>();
    public DbSet<UtilityOutageNotice> UtilityOutages => Set<UtilityOutageNotice>();
    public DbSet<WasteCollectionSchedule> WasteSchedules => Set<WasteCollectionSchedule>();
    public DbSet<GreenSubsidyProgram> GreenSubsidies => Set<GreenSubsidyProgram>();
    public DbSet<AmenityBooking> AmenityBookings => Set<AmenityBooking>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<AgentStep> AgentSteps => Set<AgentStep>();
    public DbSet<AiAction> AiActions => Set<AiAction>();
    public DbSet<LegalDocumentSource> LegalDocuments => Set<LegalDocumentSource>();
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();
    public DbSet<WorkspaceUserAccount> UserAccounts => Set<WorkspaceUserAccount>();
    public DbSet<ResidentMembershipApplication> ResidentMemberships => Set<ResidentMembershipApplication>();
    public DbSet<ResidentGroup> ResidentGroups => Set<ResidentGroup>();
    public DbSet<CommunityMessage> CommunityMessages => Set<CommunityMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Community>(e =>
        {
            e.HasMany(c => c.Buildings).WithOne().HasForeignKey(b => b.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Units).WithOne().HasForeignKey(u => u.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.LedgerAccounts).WithOne().HasForeignKey(a => a.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.JournalEntries).WithOne().HasForeignKey(j => j.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Vendors).WithOne().HasForeignKey(v => v.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.VendorBills).WithOne().HasForeignKey(v => v.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.BankTransactions).WithOne().HasForeignKey(b => b.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.CommercialContracts).WithOne().HasForeignKey(a => a.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.ReserveComponents).WithOne().HasForeignKey(r => r.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.WorkOrders).WithOne().HasForeignKey(w => w.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.ModificationRequests).WithOne().HasForeignKey(m => m.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Resolutions).WithOne().HasForeignKey(r => r.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Meetings).WithOne().HasForeignKey(m => m.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.UtilityProviderSources).WithOne().HasForeignKey(u => u.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.UtilityOutages).WithOne().HasForeignKey(u => u.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.EmergencyContacts).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.InfrastructurePhotos).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.AccessCodes).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Inspections).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.WasteSchedules).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.GreenSubsidies).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.AmenityBookings).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.LegalDocuments).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.SecurityAuditEvents).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.UserAccounts).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.ResidentMemberships).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.ResidentGroups).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.CommunityMessages).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.AiActions).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.AgentRuns).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Building>(e =>
        {
            e.HasMany(b => b.RiserStacks).WithOne().HasForeignKey(r => r.BuildingId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(b => new { b.CommunityId, b.Name });
        });

        modelBuilder.Entity<Unit>(e =>
        {
            e.HasMany(u => u.Meters).WithOne().HasForeignKey(m => m.UnitId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(u => u.Assessments).WithOne().HasForeignKey(a => a.UnitId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => new { u.CommunityId, u.UnitNumber }).IsUnique();
            e.HasIndex(u => new { u.CommunityId, u.VerticalStackCode });
            e.Ignore(u => u.OwnershipShare);
        });

        modelBuilder.Entity<UtilityMeter>(e =>
        {
            e.HasMany(m => m.Readings).WithOne().HasForeignKey(r => r.UtilityMeterId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.HasMany(j => j.Lines).WithOne().HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(j => new { j.CommunityId, j.EntryDate });
        });

        modelBuilder.Entity<UtilitySettlementPeriod>(e =>
        {
            e.HasMany(p => p.UnitResults).WithOne().HasForeignKey(r => r.SettlementPeriodId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkOrder>(e =>
        {
            e.HasMany(w => w.Quotes).WithOne().HasForeignKey(q => q.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(w => new { w.CommunityId, w.Status });
        });

        modelBuilder.Entity<Resolution>(e =>
        {
            e.HasMany(r => r.Votes).WithOne().HasForeignKey(v => v.ResolutionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => new { r.CommunityId, r.Number });
        });

        modelBuilder.Entity<AgentRun>(e =>
        {
            e.HasMany(r => r.Steps).WithOne().HasForeignKey(s => s.AgentRunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResidentGroup>(e =>
        {
            e.HasMany(g => g.Messages).WithOne().HasForeignKey(m => m.ResidentGroupId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(g => new { g.CommunityId, g.Code });
        });

        modelBuilder.Entity<Community>().Ignore(c => c.IsUnderDeveloperWarranty);
        modelBuilder.Entity<LegalDocumentSource>().HasIndex(x => new { x.CommunityId, x.DocumentCode });
        modelBuilder.Entity<SecurityAuditEvent>().HasIndex(x => new { x.CommunityId, x.OccurredAtUtc });
        modelBuilder.Entity<WorkspaceUserAccount>().HasIndex(x => new { x.CommunityId, x.Email });
        modelBuilder.Entity<ResidentMembershipApplication>().HasIndex(x => new { x.CommunityId, x.Status });
        modelBuilder.Entity<CommunityMessage>().HasIndex(x => new { x.CommunityId, x.SentAtUtc });
    }
}
