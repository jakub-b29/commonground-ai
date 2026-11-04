using CommonGround.Application.Persistence;
using CommonGround.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<ManagementFirm> ManagementFirms => Set<ManagementFirm>();
    public DbSet<FirmManagerGroup> FirmManagerGroups => Set<FirmManagerGroup>();
    public DbSet<FirmManagerMessage> FirmManagerMessages => Set<FirmManagerMessage>();
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
    public DbSet<ResidentDirectMessage> ResidentDirectMessages => Set<ResidentDirectMessage>();
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
    public DbSet<WorkspaceAccountMembership> AccountMemberships => Set<WorkspaceAccountMembership>();
    public DbSet<ResidentMembershipApplication> ResidentMemberships => Set<ResidentMembershipApplication>();
    public DbSet<ResidentGroup> ResidentGroups => Set<ResidentGroup>();
    public DbSet<CommunityMessage> CommunityMessages => Set<CommunityMessage>();
    public DbSet<User> Users => Set<User>();
    public DbSet<FirmMembership> FirmMemberships => Set<FirmMembership>();
    public DbSet<MembershipPropertyAccess> MembershipPropertyAccesses => Set<MembershipPropertyAccess>();
    public DbSet<MembershipUnitLink> MembershipUnitLinks => Set<MembershipUnitLink>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<JoinRequest> JoinRequests => Set<JoinRequest>();
    public DbSet<PropertyAnnouncement> PropertyAnnouncements => Set<PropertyAnnouncement>();
    public DbSet<WorkOrderMessage> WorkOrderMessages => Set<WorkOrderMessage>();
    public DbSet<ResidentProfile> ResidentProfiles => Set<ResidentProfile>();
    public DbSet<ResidentUnitAssignment> ResidentUnitAssignments => Set<ResidentUnitAssignment>();
    public DbSet<UnitOwnership> UnitOwnerships => Set<UnitOwnership>();
    public DbSet<ResidentHouseholdMember> ResidentHouseholdMembers => Set<ResidentHouseholdMember>();
    public DbSet<ResidentDocument> ResidentDocuments => Set<ResidentDocument>();
    public DbSet<CommunityContact> CommunityContacts => Set<CommunityContact>();
    public DbSet<ResidentRegistrationToken> ResidentRegistrationTokens => Set<ResidentRegistrationToken>();
    public DbSet<ResidentRegistrationRequest> ResidentRegistrationRequests => Set<ResidentRegistrationRequest>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 4);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ManagementFirm>(e =>
        {
            e.HasMany(f => f.Communities).WithOne().HasForeignKey(c => c.ManagementFirmId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(f => f.ManagerGroups).WithOne().HasForeignKey(g => g.ManagementFirmId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(f => f.ManagerMessages).WithOne().HasForeignKey(m => m.ManagementFirmId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(f => f.Invitations).WithOne().HasForeignKey(i => i.ManagementFirmId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(f => f.ResidentProfiles).WithOne().HasForeignKey(r => r.ManagementFirmId).OnDelete(DeleteBehavior.NoAction).IsRequired(false);
            e.HasMany(f => f.RegistrationTokens).WithOne().HasForeignKey(t => t.ManagementFirmId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(f => f.RegistrationRequests).WithOne().HasForeignKey(r => r.ManagementFirmId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(f => f.FirmGroupCode).IsUnique();
        });

        modelBuilder.Entity<FirmManagerGroup>(e =>
        {
            e.HasMany(g => g.Messages).WithOne().HasForeignKey(m => m.FirmManagerGroupId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(g => new { g.ManagementFirmId, g.Code });
        });

        modelBuilder.Entity<Community>(e =>
        {
            e.HasIndex(c => c.CommunityAccountNumber).IsUnique();
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
            e.HasMany(c => c.UserAccounts).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.SetNull).IsRequired(false);
            e.HasMany(c => c.ResidentMemberships).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.ResidentGroups).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.CommunityMessages).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.AiActions).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.AgentRuns).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.CommunityContacts).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.UnitOwnerships).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.ResidentAssignments).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.HouseholdMembers).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.ResidentDocuments).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.RegistrationTokens).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.RegistrationRequests).WithOne().HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
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
            e.HasMany(u => u.Ownerships).WithOne().HasForeignKey(o => o.UnitId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(u => u.ResidentAssignments).WithOne(a => a.Unit).HasForeignKey(a => a.UnitId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(u => u.HouseholdMembers).WithOne().HasForeignKey(h => h.UnitId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(u => u.Documents).WithOne().HasForeignKey(d => d.UnitId).OnDelete(DeleteBehavior.NoAction).IsRequired(false);
            e.HasMany(u => u.RegistrationTokens).WithOne().HasForeignKey(t => t.TargetUnitId).OnDelete(DeleteBehavior.NoAction);
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
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
        });
        modelBuilder.Entity<FirmMembership>(e =>
        {
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ManagementFirm).WithMany(f => f.FirmMemberships).HasForeignKey(x => x.ManagementFirmId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserId, x.ManagementFirmId }).IsUnique();
            e.HasIndex(x => new { x.ManagementFirmId, x.Status });
        });
        modelBuilder.Entity<MembershipPropertyAccess>(e =>
        {
            e.HasOne(x => x.FirmMembership).WithMany(m => m.PropertyAccesses).HasForeignKey(x => x.FirmMembershipId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Community).WithMany(c => c.PropertyAccessGrants).HasForeignKey(x => x.CommunityId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.FirmMembershipId, x.CommunityId }).IsUnique();
        });
        modelBuilder.Entity<MembershipUnitLink>(e =>
        {
            e.HasOne(x => x.FirmMembership).WithMany(m => m.UnitLinks).HasForeignKey(x => x.FirmMembershipId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Unit).WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.FirmMembershipId, x.UnitId }).IsUnique();
            e.HasIndex(x => new { x.CommunityId, x.UnitId });
        });
        modelBuilder.Entity<Invitation>(e =>
        {
            e.HasOne(x => x.ManagementFirm).WithMany(f => f.FirmInvitations).HasForeignKey(x => x.ManagementFirmId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TokenHash);
            e.HasIndex(x => new { x.ManagementFirmId, x.Status });
        });
        modelBuilder.Entity<JoinRequest>(e =>
        {
            e.HasOne(x => x.FirmMembership).WithMany().HasForeignKey(x => x.FirmMembershipId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ManagementFirm).WithMany(f => f.JoinRequests).HasForeignKey(x => x.ManagementFirmId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => new { x.ManagementFirmId, x.Status });
        });
        modelBuilder.Entity<PropertyAnnouncement>(e =>
        {
            e.HasIndex(x => new { x.CommunityId, x.CreatedAtUtc });
        });
        modelBuilder.Entity<WorkOrderMessage>(e =>
        {
            e.HasOne(x => x.WorkOrder).WithMany(w => w.Messages).HasForeignKey(x => x.WorkOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkOrderId, x.CreatedAtUtc });
        });
        modelBuilder.Entity<LegalDocumentSource>().HasIndex(x => new { x.CommunityId, x.DocumentCode });
        modelBuilder.Entity<SecurityAuditEvent>().HasIndex(x => new { x.CommunityId, x.OccurredAtUtc });
        modelBuilder.Entity<WorkspaceUserAccount>().HasIndex(x => new { x.CommunityId, x.Email });
        modelBuilder.Entity<WorkspaceUserAccount>().HasIndex(x => new { x.ManagementFirmId, x.Email });
        modelBuilder.Entity<ResidentMembershipApplication>().HasIndex(x => new { x.CommunityId, x.Status });
        modelBuilder.Entity<CommunityMessage>().HasIndex(x => new { x.CommunityId, x.SentAtUtc });
        modelBuilder.Entity<FirmManagerMessage>().HasIndex(x => new { x.ManagementFirmId, x.SentAtUtc });
        modelBuilder.Entity<WorkspaceInvitation>().HasIndex(x => new { x.ManagementFirmId, x.Status });
        modelBuilder.Entity<WorkspaceInvitation>().HasIndex(x => x.InvitationToken).IsUnique();
        modelBuilder.Entity<WorkspaceAccountMembership>(e =>
        {
            e.HasOne(x => x.UserAccount).WithMany(u => u.Memberships).HasForeignKey(x => x.UserAccountId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ManagementFirm).WithMany(f => f.AccountMemberships).HasForeignKey(x => x.ManagementFirmId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.UserAccountId, x.ManagementFirmId });
            e.HasIndex(x => new { x.ManagementFirmId, x.ApprovalStatus });
        });
        modelBuilder.Entity<ResidentDirectMessage>().HasIndex(x => new { x.CommunityId, x.ThreadKey, x.SentAtUtc });

        modelBuilder.Entity<ResidentProfile>(e =>
        {
            e.HasIndex(r => r.ResidentAccountNumber).IsUnique();
            e.HasIndex(r => r.PrimaryEmail);
            e.HasIndex(r => r.UserId);
            e.HasIndex(r => r.ManagementFirmId);
            e.HasMany(r => r.Assignments).WithOne(a => a.ResidentProfile).HasForeignKey(a => a.ResidentProfileId).OnDelete(DeleteBehavior.NoAction);
            e.HasMany(r => r.Ownerships).WithOne().HasForeignKey(o => o.ResidentProfileId).OnDelete(DeleteBehavior.NoAction).IsRequired(false);
            e.HasMany(r => r.Documents).WithOne().HasForeignKey(d => d.ResidentProfileId).OnDelete(DeleteBehavior.NoAction).IsRequired(false);
        });

        modelBuilder.Entity<ResidentUnitAssignment>(e =>
        {
            e.HasIndex(a => new { a.UnitId, a.Status });
            e.HasIndex(a => new { a.ResidentProfileId, a.Status });
            e.HasIndex(a => new { a.CommunityId, a.Status });
        });

        modelBuilder.Entity<UnitOwnership>(e =>
        {
            e.HasIndex(o => new { o.UnitId, o.IsActive });
            e.HasIndex(o => new { o.CommunityId, o.IsActive });
        });

        modelBuilder.Entity<ResidentHouseholdMember>(e =>
        {
            e.HasIndex(h => new { h.UnitId, h.IsActive });
        });

        modelBuilder.Entity<ResidentDocument>(e =>
        {
            e.HasIndex(d => new { d.ResidentProfileId, d.UploadedAtUtc });
            e.HasIndex(d => new { d.UnitId, d.UploadedAtUtc });
        });

        modelBuilder.Entity<CommunityContact>(e =>
        {
            e.HasIndex(c => new { c.CommunityId, c.Category, c.SortOrder });
        });

        modelBuilder.Entity<ResidentRegistrationToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => new { t.ManagementFirmId, t.Status });
            e.HasIndex(t => new { t.CommunityId, t.TargetUnitId });
        });

        modelBuilder.Entity<ResidentRegistrationRequest>(e =>
        {
            e.HasIndex(r => new { r.ManagementFirmId, r.Status, r.SubmittedAtUtc });
            e.HasIndex(r => new { r.CommunityId, r.Status });
        });
    }
}
