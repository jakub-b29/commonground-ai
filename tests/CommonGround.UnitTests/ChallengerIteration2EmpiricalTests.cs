using System.Security.Claims;
using System.Text.Json;
using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Application.Services;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.MetaModels;
using CommonGround.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommonGround.UnitTests;

/// <summary>
/// Empirical Challenger suite for Milestone M1 Iteration 2.
/// Stress-tests permission evaluator fail-safe defaults, guest multi-tenant isolation,
/// dynamic DB query boundary checks, and SHA-256 audit chain tamper detection.
/// </summary>
public sealed class ChallengerIteration2EmpiricalTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContext _db;
    private readonly IAppDbContextFactory _factory;
    private readonly MetaModelClients _clients;
    private readonly CommonGroundWorkspaceService _workspace;
    private readonly PropertyOnboardingService _onboardingService;
    private readonly MembershipService _membershipService;
    private readonly InvitationService _invitationService;
    private readonly SparkAgentPermissionEvaluator _permissionEvaluator;
    private readonly SparkAgentToolExecutor _toolExecutor;
    private readonly MuseSparkAutomationAgentService _agentService;

    private readonly Guid _firmAId = Guid.NewGuid();
    private readonly Guid _firmBId = Guid.NewGuid();
    private readonly Guid _commAId = Guid.NewGuid();
    private readonly Guid _commBId = Guid.NewGuid();
    private readonly Guid _unitA1Id = Guid.NewGuid();
    private readonly Guid _unitB1Id = Guid.NewGuid();
    private readonly Guid _residentAId = Guid.NewGuid();
    private readonly Guid _managerAId = Guid.NewGuid();

    public ChallengerIteration2EmpiricalTests()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = $"cg-challenger-iter2-{Guid.NewGuid():N}";
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName, root)
            .Options;
        _db = new AppDbContext(_options);
        _factory = new TestFactory(_options);

        var metaOptions = Options.Create(new MetaModelOptions { ApiKey = string.Empty, UseFallbackWhenNoKey = true });
        var renderer = new SamOverlayRenderer();
        _clients = new MetaModelClients(metaOptions, renderer, null);

        _workspace = new CommonGroundWorkspaceService(_factory, _clients, _clients, _clients, _clients, _clients);
        _onboardingService = new PropertyOnboardingService(_factory, _clients);
        _membershipService = new MembershipService(_factory);
        _invitationService = new InvitationService(_factory);
        _permissionEvaluator = new SparkAgentPermissionEvaluator();

        _toolExecutor = new SparkAgentToolExecutor(
            _workspace,
            _onboardingService,
            _invitationService,
            _membershipService,
            _clients,
            _factory);

        _agentService = new MuseSparkAutomationAgentService(
            _clients,
            _permissionEvaluator,
            _toolExecutor,
            _factory);

        SeedData();
    }

    private void SeedData()
    {
        var firmA = new ManagementFirm
        {
            Id = _firmAId,
            Name = "Firm Alpha Sp. z o.o.",
            FirmGroupCode = "FIRM-ALPHA",
            FirmTaxIdOrNip = "111-222-33-44",
            SupportEmail = "alpha@firm.local"
        };
        var firmB = new ManagementFirm
        {
            Id = _firmBId,
            Name = "Firm Beta LLC",
            FirmGroupCode = "FIRM-BETA",
            FirmTaxIdOrNip = "999-888-77-66",
            SupportEmail = "beta@firm.local"
        };
        _db.ManagementFirms.AddRange(firmA, firmB);

        var commA = new Community
        {
            Id = _commAId,
            ManagementFirmId = _firmAId,
            Name = "Amber Park Residence",
            StreetAddress = "Bursztynowa 14",
            City = "Warsaw",
            CurrencyCode = "PLN",
            OperatingMode = SystemOperatingMode.HousingAssociation,
            CommunityAccountNumber = "COMM-WAR-001"
        };
        var commB = new Community
        {
            Id = _commBId,
            ManagementFirmId = _firmBId,
            Name = "Oakridge Palms Condominium",
            StreetAddress = "100 Ocean Drive",
            City = "Miami",
            CurrencyCode = "USD",
            OperatingMode = SystemOperatingMode.UsHoa,
            CommunityAccountNumber = "COMM-MIA-002"
        };
        _db.Communities.AddRange(commA, commB);

        var bldA = new Building
        {
            Id = Guid.NewGuid(),
            CommunityId = _commAId,
            Name = "Building A",
            FloorsCount = 5,
            StairwellsCount = 2
        };
        _db.Buildings.Add(bldA);

        var uA1 = new Unit
        {
            Id = _unitA1Id,
            CommunityId = _commAId,
            BuildingId = bldA.Id,
            UnitNumber = "A-101",
            FloorNumber = 1,
            StairwellNumber = 1,
            FloorAreaM2 = 55.0m,
            OccupancyStatus = UnitOccupancyStatus.OwnerOccupied,
            CurrentBalance = 120.00m
        };
        _db.Units.Add(uA1);

        var meterA1 = new UtilityMeter
        {
            Id = Guid.NewGuid(),
            UnitId = _unitA1Id,
            Kind = UtilityKind.ColdWater,
            SerialNumber = "CW-A101-01",
            LastReadingValue = 100.0m
        };
        _db.UtilityMeters.Add(meterA1);

        var resA = new Resolution
        {
            Id = Guid.NewGuid(),
            CommunityId = _commAId,
            Number = "Res 01/2026",
            Title = "2026 Annual Budget",
            Content = "Approve budget",
            MajorityRule = VotingMajorityRule.ShareWeightedMajority,
            ComplianceStatus = "VerifiedClean"
        };
        _db.Resolutions.Add(resA);

        var woA = new WorkOrder
        {
            Id = Guid.NewGuid(),
            CommunityId = _commAId,
            UnitId = _unitA1Id,
            Title = "Sink pipe drip",
            Category = WorkOrderCategory.Plumbing,
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.Reported
        };
        _db.WorkOrders.Add(woA);

        _db.SaveChanges();
    }

    [Fact]
    public async Task Adversarial_UnknownAndHostileTools_AreAlwaysDeniedAcrossAllRoles()
    {
        // Test with multiple roles: Guest, Resident, PropertyManager, and FirmManager
        var guestContext = await _permissionEvaluator.ResolveContextAsync(null, _db);

        var residentPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _residentAId.ToString()),
            new Claim(ClaimTypes.Role, "Resident"),
            new Claim("firm_id", _firmAId.ToString()),
            new Claim("allowed_communities", _commAId.ToString()),
            new Claim("unit_number", "A-101")
        }, "TestCookie"));
        var residentContext = await _permissionEvaluator.ResolveContextAsync(residentPrincipal, _db);

        var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _managerAId.ToString()),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firmAId.ToString()),
            new Claim("allowed_communities", _commAId.ToString())
        }, "TestCookie"));
        var managerContext = await _permissionEvaluator.ResolveContextAsync(managerPrincipal, _db);

        var contexts = new[] { guestContext, residentContext, managerContext };

        var adversarialToolNames = new[]
        {
            "",
            "   ",
            "\t\n\r",
            "unknown_arbitrary_tool",
            "unlisted_tool",
            "__proto__",
            "constructor",
            "toString",
            "DROP TABLE Units;--",
            "../../../../etc/passwd",
            "<script>alert('xss')</script>",
            "grant_full_admin_privileges",
            "bypass_security_guard",
            "eval()",
            "cmd.exe",
            "powershell -Command Remove-Item",
            "execute_database_query_unsafe",
            "security_approvals_sos_and_vault_backdoor",
            "run_arbitrary_code",
            "SELECT * FROM Users",
            "null",
            "undefined"
        };

        foreach (var ctx in contexts)
        {
            foreach (var tool in adversarialToolNames)
            {
                var result = _permissionEvaluator.EvaluateToolPermission(ctx, tool, new Dictionary<string, string>());
                Assert.False(result.IsPermitted, $"Tool '{tool}' must be DENIED for role '{ctx.Role}' under Default-Deny.");
                Assert.Contains("not recognized or permitted", result.DenialReason, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Adversarial_SecurityApprovalsAndVault_UnknownActions_AreDenied()
    {
        var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _managerAId.ToString()),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firmAId.ToString()),
            new Claim("allowed_communities", _commAId.ToString())
        }, "TestCookie"));
        var managerContext = await _permissionEvaluator.ResolveContextAsync(managerPrincipal, _db);

        var maliciousActions = new[]
        {
            "exploit_action",
            "override_permissions",
            "drop_audit_chain",
            "export_all_passwords",
            "bypass_vault_pin",
            "delete_all_records",
            "__unknown_vault_action__"
        };

        foreach (var action in maliciousActions)
        {
            var args = new Dictionary<string, string> { ["actionType"] = action };
            var result = _permissionEvaluator.EvaluateToolPermission(managerContext, "security_approvals_sos_and_vault", args);

            Assert.False(result.IsPermitted, $"Vault action '{action}' must be DENIED.");
            Assert.Contains("not recognized or permitted", result.DenialReason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Adversarial_GuestAndUnapprovedUsers_ReceiveZeroCommunitiesAndEntities()
    {
        // 1. Unauthenticated Guest
        var guestContext = await _permissionEvaluator.ResolveContextAsync(null, _db);

        // 2. User with zero allowed communities
        var zeroComContext = new SparkAgentPermissionContext(
            UserId: Guid.NewGuid(),
            FullName: "Unassigned User",
            Email: "unassigned@commonground.local",
            ActiveMembershipId: null,
            ManagementFirmId: null,
            Role: WorkspaceSecurityRole.ExternalAuditorView,
            MembershipStatus: WorkspaceMembershipStatus.PendingApproval,
            GrantedPermissions: 0,
            AllowedCommunityIds: Array.Empty<Guid>(),
            LinkedUnitId: null,
            LinkedUnitNumber: null,
            IsFirmManager: false);

        // 3. Resident with zero allowed communities and unlinked unit
        var unlinkedResidentContext = new SparkAgentPermissionContext(
            UserId: Guid.NewGuid(),
            FullName: "Unlinked Resident",
            Email: "unlinked@commonground.local",
            ActiveMembershipId: null,
            ManagementFirmId: _firmAId,
            Role: WorkspaceSecurityRole.Resident,
            MembershipStatus: WorkspaceMembershipStatus.Approved,
            GrantedPermissions: WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident),
            AllowedCommunityIds: Array.Empty<Guid>(),
            LinkedUnitId: null,
            LinkedUnitNumber: null,
            IsFirmManager: false);

        // 4. FirmManager without ManagementFirmId and zero allowed communities
        var orphanManagerContext = new SparkAgentPermissionContext(
            UserId: Guid.NewGuid(),
            FullName: "Orphan Manager",
            Email: "orphan@commonground.local",
            ActiveMembershipId: null,
            ManagementFirmId: null,
            Role: WorkspaceSecurityRole.FirmManager,
            MembershipStatus: WorkspaceMembershipStatus.Approved,
            GrantedPermissions: (WorkspacePermissionFlags)~0L,
            AllowedCommunityIds: Array.Empty<Guid>(),
            LinkedUnitId: null,
            LinkedUnitNumber: null,
            IsFirmManager: true);

        var testContexts = new[] { guestContext, zeroComContext, unlinkedResidentContext, orphanManagerContext };

        foreach (var ctx in testContexts)
        {
            // A. Query communities via execute_database_query
            var commObs = await _toolExecutor.ExecuteToolAsync(
                "execute_database_query",
                new Dictionary<string, string> { ["entityName"] = "communities" },
                ctx,
                Guid.Empty);
            Assert.True(commObs.IsSuccess);
            Assert.Contains("Retrieved 0 communities", commObs.Summary);

            // B. Query units via execute_database_query
            var unitObs = await _toolExecutor.ExecuteToolAsync(
                "execute_database_query",
                new Dictionary<string, string> { ["entityName"] = "units" },
                ctx,
                _commAId);
            Assert.True(unitObs.IsSuccess);
            Assert.Contains("Retrieved 0 units", unitObs.Summary);

            // C. Query work_orders via execute_database_query
            var woObs = await _toolExecutor.ExecuteToolAsync(
                "execute_database_query",
                new Dictionary<string, string> { ["entityName"] = "work_orders" },
                ctx,
                _commAId);
            Assert.True(woObs.IsSuccess);
            Assert.Contains("Retrieved 0 work orders", woObs.Summary);

            // D. Query resolutions via execute_database_query
            var resObs = await _toolExecutor.ExecuteToolAsync(
                "execute_database_query",
                new Dictionary<string, string> { ["entityName"] = "resolutions" },
                ctx,
                _commAId);
            Assert.True(resObs.IsSuccess);
            Assert.Contains("Retrieved 0 resolutions", resObs.Summary);

            // E. Query utility_meters via execute_database_query
            var meterObs = await _toolExecutor.ExecuteToolAsync(
                "execute_database_query",
                new Dictionary<string, string> { ["entityName"] = "utility_meters" },
                ctx,
                _commAId);
            Assert.True(meterObs.IsSuccess);
            Assert.Contains("Retrieved 0 utility meters", meterObs.Summary);

            // F. Query communities via query_system_entities
            var sysCommObs = await _toolExecutor.ExecuteToolAsync(
                "query_system_entities",
                new Dictionary<string, string> { ["entityType"] = "communities" },
                ctx,
                Guid.Empty);
            Assert.True(sysCommObs.IsSuccess);
            Assert.Contains("Found 0 communities", sysCommObs.Summary);

            // G. Query units via query_system_entities
            var sysUnitObs = await _toolExecutor.ExecuteToolAsync(
                "query_system_entities",
                new Dictionary<string, string> { ["entityType"] = "units" },
                ctx,
                _commAId);
            Assert.True(sysUnitObs.IsSuccess);
            Assert.Contains("Found 0 units", sysUnitObs.Summary);
        }
    }

    [Fact]
    public void Adversarial_Sha256AuditChain_ExhaustiveTamperDetectionOracles()
    {
        var communityId = Guid.NewGuid();
        var chain = new List<SecurityAuditEvent>();
        var prevHash = "GENESIS-0000000000000000";
        var t0 = new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 8; i++)
        {
            var ev = new SecurityAuditEvent
            {
                Id = Guid.NewGuid(),
                CommunityId = communityId,
                ActorRole = (WorkspaceSecurityRole)(i % 4),
                ActionCategory = i % 2 == 0 ? "SPARK_AGENT_PERMISSION_DENIED" : "WORK_ORDER_DISPATCH",
                Severity = "Warning",
                ResourceDescriptor = $"Resource_{i}",
                Summary = $"Legitimate log entry {i}",
                PreviousChainHash = prevHash,
                OccurredAtUtc = t0.AddSeconds(i * 15)
            };
            ev.EntrySha256Hash = DataSecurityAndPrivacyGuard.ComputeEntryHash(ev);
            prevHash = ev.EntrySha256Hash;
            chain.Add(ev);
        }

        // Oracle 1: Pristine chain must verify cleanly
        var (valid, count) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(chain);
        Assert.True(valid);
        Assert.Equal(8, count);

        // Oracle 2: Tamper with Genesis event (index 0) PreviousChainHash
        chain[0].PreviousChainHash = "MALICIOUS_GENESIS";
        var (res2, count2) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(chain);
        Assert.False(res2);
        Assert.Equal(0, count2);
        chain[0].PreviousChainHash = "GENESIS-0000000000000000";

        // Oracle 3: Tamper with ResourceDescriptor in index 3
        var origRes = chain[3].ResourceDescriptor;
        chain[3].ResourceDescriptor = "Injected_Resource_3";
        var (res3, count3) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(chain);
        Assert.False(res3);
        Assert.Equal(3, count3);
        chain[3].ResourceDescriptor = origRes;

        // Oracle 4: Tamper with ActionCategory in index 4
        var origCat4 = chain[4].ActionCategory;
        chain[4].ActionCategory = "UNAUTHORIZED_CATEGORY";
        var (res4, count4) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(chain);
        Assert.False(res4);
        Assert.Equal(4, count4);
        chain[4].ActionCategory = origCat4;

        // Oracle 5: Tamper with CommunityId in index 2
        chain[2].CommunityId = Guid.NewGuid();
        var (res5, count5) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(chain);
        Assert.False(res5);
        Assert.Equal(2, count5);
        chain[2].CommunityId = communityId;

        // Oracle 6: Tamper with EntrySha256Hash directly (bit flip)
        var origHash = chain[6].EntrySha256Hash;
        chain[6].EntrySha256Hash = origHash[..^1] + (origHash[^1] == 'a' ? 'b' : 'a');
        var (res6, count6) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(chain);
        Assert.False(res6);
        Assert.Equal(6, count6);
        chain[6].EntrySha256Hash = origHash;

        // Oracle 7: Tamper with event 3, recompute its EntrySha256Hash, but leave event 4 pointing to old hash
        chain[3].Summary = "Stealth mutation with updated hash";
        chain[3].EntrySha256Hash = DataSecurityAndPrivacyGuard.ComputeEntryHash(chain[3]);
        var (res7, count7) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(chain);
        Assert.False(res7);
        Assert.Equal(4, count7); // Event 4 fails because its PreviousChainHash still points to event 3's old hash!
        chain[3].Summary = "Legitimate log entry 3";
        chain[3].EntrySha256Hash = DataSecurityAndPrivacyGuard.ComputeEntryHash(chain[3]);

        // Oracle 8: Reorder events (swap index 2 and index 3)
        var reordered = new List<SecurityAuditEvent>
        {
            chain[0], chain[1], chain[3], chain[2], chain[4], chain[5], chain[6], chain[7]
        };
        // Even if sorted by OccurredAtUtc, OccurredAtUtc of index 3 is greater than index 2,
        // but if someone forged timestamps:
        reordered[2].OccurredAtUtc = t0.AddSeconds(20);
        reordered[3].OccurredAtUtc = t0.AddSeconds(45);
        var (res8, count8) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(reordered);
        Assert.False(res8);
        Assert.True(count8 <= 4);

        // Oracle 9: Empty chain is valid with 0 count
        var (res9, count9) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(Array.Empty<SecurityAuditEvent>());
        Assert.True(res9);
        Assert.Equal(0, count9);
    }

    [Fact]
    public async Task Adversarial_AgentLoop_PermissionDenied_MaintainsCryptographicIntegrityInDatabase()
    {
        var residentPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _residentAId.ToString()),
            new Claim(ClaimTypes.Role, "Resident"),
            new Claim("firm_id", _firmAId.ToString()),
            new Claim("allowed_communities", _commAId.ToString()),
            new Claim("unit_number", "A-101")
        }, "TestCookie"));

        // Ask for administrative action that will be denied
        var goal = "Create unit B-999, run bulk utility settlement, and trigger SOS broadcast.";

        var result = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: residentPrincipal,
            activeCommunityId: _commAId,
            stepByStepMode: false);

        Assert.NotNull(result);

        // Verify that denied steps exist
        var deniedSteps = result.Steps.Where(s => s.PermissionGranted == false).ToList();
        Assert.NotEmpty(deniedSteps);

        // Introspect DB security audit events
        await using var db = await _factory.CreateDbContextAsync();
        var events = await db.SecurityAuditEvents
            .Where(e => e.CommunityId == _commAId)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync();

        Assert.NotEmpty(events);
        Assert.Contains(events, e => e.ActionCategory == "SPARK_AGENT_PERMISSION_DENIED");

        // Verify cryptographic chain
        var (isValid, verifiedCount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(events);
        Assert.True(isValid, "Audit chain in database must be cryptographically sound.");
        Assert.Equal(events.Count, verifiedCount);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private sealed class TestFactory(DbContextOptions<AppDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<IAppDbContext>(new AppDbContext(options));
    }
}
