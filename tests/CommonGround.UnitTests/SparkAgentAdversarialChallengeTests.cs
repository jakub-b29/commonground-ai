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
using System.Security.Claims;

namespace CommonGround.UnitTests;

public sealed class SparkAgentAdversarialChallengeTests : IDisposable
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContext _db;
    private readonly IAppDbContextFactory _factory;
    private readonly MetaModelClients _clients;
    private readonly CommonGroundWorkspaceService _workspace;
    private readonly PropertyOnboardingService _onboardingService;
    private readonly InvitationService _invitationService;
    private readonly MembershipService _membershipService;
    private readonly SparkAgentPermissionEvaluator _evaluator;
    private readonly SparkAgentToolExecutor _toolExecutor;
    private readonly MuseSparkAutomationAgentService _agentService;

    private readonly Guid _firmId = Guid.NewGuid();
    private readonly Guid _communityAId = Guid.NewGuid();
    private readonly Guid _communityBId = Guid.NewGuid();
    private readonly Guid _unitA101Id = Guid.NewGuid();
    private readonly Guid _unitB202Id = Guid.NewGuid();
    private readonly Guid _residentUserId = Guid.NewGuid();
    private readonly Guid _firmManagerUserId = Guid.NewGuid();

    public SparkAgentAdversarialChallengeTests()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = $"spark-adversarial-{Guid.NewGuid():N}";
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName, root)
            .Options;
        _db = new AppDbContext(_options);
        _factory = new TestDbContextFactory(_options);

        var metaOptions = Options.Create(new MetaModelOptions
        {
            ApiKey = string.Empty,
            UseFallbackWhenNoKey = true
        });
        var renderer = new SamOverlayRenderer();
        _clients = new MetaModelClients(metaOptions, renderer, null);

        _workspace = new CommonGroundWorkspaceService(_factory, _clients, _clients, _clients, _clients, _clients);
        _onboardingService = new PropertyOnboardingService(_factory, _clients);
        _invitationService = new InvitationService(_factory);
        _membershipService = new MembershipService(_factory);

        _evaluator = new SparkAgentPermissionEvaluator();
        _toolExecutor = new SparkAgentToolExecutor(_workspace, _onboardingService, _invitationService, _membershipService, _clients, _factory);
        _agentService = new MuseSparkAutomationAgentService(_clients, _evaluator, _toolExecutor, _factory);

        SeedDatabaseAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IAppDbContextFactory
    {
        public Task<IAppDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<IAppDbContext>(new AppDbContext(options));
    }

    private async Task SeedDatabaseAsync()
    {
        var firm = new ManagementFirm
        {
            Id = _firmId,
            Name = "Premier Property Management",
            FirmGroupCode = "PREMIER-01",
            FirmTaxIdOrNip = "PL5250000001",
            SupportEmail = "office@premier.local",
            IsEmailConfirmed = true
        };
        _db.ManagementFirms.Add(firm);

        var comA = new Community
        {
            Id = _communityAId,
            ManagementFirmId = _firmId,
            CommunityAccountNumber = "COM-AMBER",
            Name = "Amber Park Residence",
            StreetAddress = "ul. Bursztynowa 12",
            City = "Warszawa",
            CountryCode = "PL",
            CurrencyCode = "PLN",
            OperatingMode = SystemOperatingMode.HousingAssociation,
            OperatingRatePerM2 = 3.50m,
            RepairFundRatePerM2 = 2.00m
        };
        _db.Communities.Add(comA);

        var comB = new Community
        {
            Id = _communityBId,
            ManagementFirmId = _firmId,
            CommunityAccountNumber = "COM-OAK",
            Name = "Oakridge Palms Condominium",
            StreetAddress = "742 Evergreen Terrace",
            City = "Miami",
            CountryCode = "US",
            CurrencyCode = "USD",
            OperatingMode = SystemOperatingMode.UsHoa,
            OperatingRatePerM2 = 2.80m,
            RepairFundRatePerM2 = 1.90m
        };
        _db.Communities.Add(comB);

        var bldA = new Building
        {
            Id = Guid.NewGuid(),
            CommunityId = _communityAId,
            Name = "Building A",
            StreetAddress = "ul. Bursztynowa 12",
            FloorsCount = 6,
            StairwellsCount = 2,
            ConstructionYear = 2021
        };
        _db.Buildings.Add(bldA);

        var unitA101 = new Unit
        {
            Id = _unitA101Id,
            CommunityId = _communityAId,
            BuildingId = bldA.Id,
            UnitNumber = "A-101",
            StairwellNumber = 1,
            FloorNumber = 1,
            VerticalStackCode = "STACK-A1",
            Kind = UnitKind.Apartment,
            FloorAreaM2 = 60m,
            ShareNumerator = 600,
            ShareDenominator = 10000,
            PrimaryOwnerName = "Tomasz Kaminski",
            PrimaryOwnerEmail = "resident.a101@premier.local",
            CurrentBalance = 0m,
            OccupancyStatus = UnitOccupancyStatus.OwnerOccupied
        };
        _db.Units.Add(unitA101);

        var meterA101 = new UtilityMeter
        {
            Id = Guid.NewGuid(),
            UnitId = _unitA101Id,
            Kind = UtilityKind.ColdWater,
            SerialNumber = "CW-A101-01",
            LastReadingValue = 120.0m,
            PreviousReadingValue = 110.0m,
            RollingMonthlyAverage = 10.0m
        };
        _db.UtilityMeters.Add(meterA101);

        var unitB202 = new Unit
        {
            Id = _unitB202Id,
            CommunityId = _communityAId,
            BuildingId = bldA.Id,
            UnitNumber = "B-202",
            StairwellNumber = 2,
            FloorNumber = 2,
            VerticalStackCode = "STACK-B1",
            Kind = UnitKind.Apartment,
            FloorAreaM2 = 75m,
            ShareNumerator = 750,
            ShareDenominator = 10000,
            PrimaryOwnerName = "Marek Nowak",
            PrimaryOwnerEmail = "resident.b202@premier.local",
            CurrentBalance = 150m,
            OccupancyStatus = UnitOccupancyStatus.TenantOccupied
        };
        _db.Units.Add(unitB202);

        var resolution = new Resolution
        {
            Id = Guid.NewGuid(),
            CommunityId = _communityAId,
            Number = "Res 01/2026",
            Title = "Annual Operating Budget Approval 2026",
            Content = "Approval of regular operating advances and reserve fund rate.",
            MajorityRule = VotingMajorityRule.ShareWeightedMajority,
            ComplianceStatus = "VerifiedClean"
        };
        _db.Resolutions.Add(resolution);

        var residentUser = new User
        {
            Id = _residentUserId,
            FullName = "Tomasz Kaminski",
            Email = "resident.a101@premier.local",
            PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("ResidentPass123!"),
            EmailConfirmed = true,
            Provider = IdentityProvider.EmailPassword
        };
        _db.Users.Add(residentUser);

        var residentMembership = new FirmMembership
        {
            Id = Guid.NewGuid(),
            UserId = _residentUserId,
            ManagementFirmId = _firmId,
            Role = WorkspaceSecurityRole.Resident,
            Status = WorkspaceMembershipStatus.Approved,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident),
            DecidedAtUtc = DateTime.UtcNow
        };
        residentMembership.PropertyAccesses.Add(new MembershipPropertyAccess
        {
            FirmMembershipId = residentMembership.Id,
            CommunityId = _communityAId,
            IsPrimaryManager = false
        });
        residentMembership.UnitLinks.Add(new MembershipUnitLink
        {
            FirmMembershipId = residentMembership.Id,
            CommunityId = _communityAId,
            UnitId = _unitA101Id,
            RelationshipKind = MembershipUnitRelationshipKind.Owner
        });
        _db.FirmMemberships.Add(residentMembership);

        var managerUser = new User
        {
            Id = _firmManagerUserId,
            FullName = "Anna Zarzadca",
            Email = "firm.manager@premier.local",
            PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("ManagerPass123!"),
            EmailConfirmed = true,
            Provider = IdentityProvider.EmailPassword
        };
        _db.Users.Add(managerUser);

        var managerMembership = new FirmMembership
        {
            Id = Guid.NewGuid(),
            UserId = _firmManagerUserId,
            ManagementFirmId = _firmId,
            Role = WorkspaceSecurityRole.FirmManager,
            Status = WorkspaceMembershipStatus.Approved,
            GrantedPermissions = (WorkspacePermissionFlags)~0L,
            DecidedAtUtc = DateTime.UtcNow
        };
        managerMembership.PropertyAccesses.Add(new MembershipPropertyAccess
        {
            FirmMembershipId = managerMembership.Id,
            CommunityId = _communityAId,
            IsPrimaryManager = true
        });
        managerMembership.PropertyAccesses.Add(new MembershipPropertyAccess
        {
            FirmMembershipId = managerMembership.Id,
            CommunityId = _communityBId,
            IsPrimaryManager = true
        });
        _db.FirmMemberships.Add(managerMembership);

        await _db.SaveChangesAsync();
    }

    private ClaimsPrincipal CreateResidentPrincipal()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _residentUserId.ToString()),
            new Claim(ClaimTypes.Name, "Tomasz Kaminski"),
            new Claim(ClaimTypes.Email, "resident.a101@premier.local"),
            new Claim(ClaimTypes.Role, "Resident"),
            new Claim("firm_id", _firmId.ToString()),
            new Claim("allowed_communities", _communityAId.ToString()),
            new Claim("unit_number", "A-101")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestCookieAuth"));
    }

    private ClaimsPrincipal CreateFirmManagerPrincipal()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _firmManagerUserId.ToString()),
            new Claim(ClaimTypes.Name, "Anna Zarzadca"),
            new Claim(ClaimTypes.Email, "firm.manager@premier.local"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firmId.ToString()),
            new Claim("allowed_communities", $"{_communityAId},{_communityBId}")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestCookieAuth"));
    }

    [Fact]
    public async Task PermissionEvaluator_Resident_BlockedFromAdministrativeUnitAndBuildingTools()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        var restrictedTools = new[]
        {
            "create_building",
            "upsert_riser_stack_or_structure",
            "create_unit_wizard",
            "update_unit_details",
            "bulk_generate_floor_units",
            "delete_unit"
        };

        foreach (var tool in restrictedTools)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["communityId"] = _communityAId.ToString(),
                ["unitNumber"] = "X-999"
            };

            var result = _evaluator.EvaluateToolPermission(context, tool, args);

            Assert.False(result.IsPermitted, $"Tool '{tool}' should be DENIED for Resident role.");
            Assert.True((result.RequiredFlags & WorkspacePermissionFlags.ManageUnitsAndInfrastructure) != 0,
                $"Tool '{tool}' should require ManageUnitsAndInfrastructure.");
            Assert.False(string.IsNullOrWhiteSpace(result.DenialReason),
                $"Tool '{tool}' must provide an explanatory denial reason.");
        }
    }

    [Fact]
    public async Task PermissionEvaluator_Resident_BlockedFromFirmAdministrationTools()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        var firmTools = new[]
        {
            "update_firm_contacts",
            "create_community",
            "create_scoped_invitation",
            "approve_or_update_membership",
            "create_firm_manager_group",
            "post_firm_manager_message"
        };

        foreach (var tool in firmTools)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "Unauthorized Firm Action"
            };

            var result = _evaluator.EvaluateToolPermission(context, tool, args);

            Assert.False(result.IsPermitted, $"Firm tool '{tool}' should be DENIED for Resident role.");
            Assert.False(string.IsNullOrWhiteSpace(result.DenialReason));
        }
    }

    [Fact]
    public async Task PermissionEvaluator_Resident_BlockedFromFinancialAndUtilityReconciliationTools()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        var financialTools = new[]
        {
            "run_bulk_utility_settlement",
            "match_bank_transfers",
            "run_inflation_and_power_audit"
        };

        foreach (var tool in financialTools)
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["communityId"] = _communityAId.ToString()
            };

            var result = _evaluator.EvaluateToolPermission(context, tool, args);

            Assert.False(result.IsPermitted, $"Financial tool '{tool}' should be DENIED for Resident role.");
            Assert.False(string.IsNullOrWhiteSpace(result.DenialReason));
        }
    }

    [Fact]
    public async Task PermissionEvaluator_Resident_OwnUnitVsForeignUnit_EnforcesUnitBoundary()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        // 1. Submit meter reading for own unit -> PERMITTED
        var ownMeterArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["communityId"] = _communityAId.ToString(),
            ["unitNumber"] = "A-101",
            ["simulatedReadingOverride"] = "135.0"
        };
        var ownMeterResult = _evaluator.EvaluateToolPermission(context, "submit_meter_reading", ownMeterArgs);
        Assert.True(ownMeterResult.IsPermitted, "Resident must be PERMITTED to submit meter readings for their own unit A-101.");

        // 2. Submit meter reading for another unit B-202 -> DENIED
        var foreignMeterArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["communityId"] = _communityAId.ToString(),
            ["unitNumber"] = "B-202",
            ["simulatedReadingOverride"] = "200.0"
        };
        var foreignMeterResult = _evaluator.EvaluateToolPermission(context, "submit_meter_reading", foreignMeterArgs);
        Assert.False(foreignMeterResult.IsPermitted, "Resident must be DENIED from submitting meter readings for unit B-202.");
        Assert.Contains("own linked unit", foreignMeterResult.DenialReason, StringComparison.OrdinalIgnoreCase);

        // 3. Cast vote for own unit -> PERMITTED
        var ownVoteArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflowType"] = "cast_vote",
            ["unitNumber"] = "A-101",
            ["choice"] = "InFavor"
        };
        var ownVoteResult = _evaluator.EvaluateToolPermission(context, "run_governance_and_voting_workflow", ownVoteArgs);
        Assert.True(ownVoteResult.IsPermitted, "Resident must be PERMITTED to cast votes for their own unit A-101.");

        // 4. Cast vote for another unit B-202 -> DENIED
        var foreignVoteArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflowType"] = "cast_vote",
            ["unitNumber"] = "B-202",
            ["choice"] = "Against"
        };
        var foreignVoteResult = _evaluator.EvaluateToolPermission(context, "run_governance_and_voting_workflow", foreignVoteArgs);
        Assert.False(foreignVoteResult.IsPermitted, "Resident must be DENIED from casting votes for unit B-202.");
        Assert.Contains("own linked unit", foreignVoteResult.DenialReason, StringComparison.OrdinalIgnoreCase);

        // 5. Add household member for own unit -> PERMITTED
        var ownHhArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unitNumber"] = "A-101",
            ["fullName"] = "Katarzyna Kaminska"
        };
        var ownHhResult = _evaluator.EvaluateToolPermission(context, "upsert_or_remove_household_member", ownHhArgs);
        Assert.True(ownHhResult.IsPermitted, "Resident must be PERMITTED to add household members for their own unit A-101.");

        // 6. Add household member for foreign unit B-202 -> DENIED
        var foreignHhArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["unitNumber"] = "B-202",
            ["fullName"] = "Intruder User"
        };
        var foreignHhResult = _evaluator.EvaluateToolPermission(context, "upsert_or_remove_household_member", foreignHhArgs);
        Assert.False(foreignHhResult.IsPermitted, "Resident must be DENIED from adding household members to unit B-202.");
    }

    [Fact]
    public async Task PermissionEvaluator_CrossCommunityAccess_EnforcesCommunityBoundary()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        // Community A is in AllowedCommunityIds, Community B is NOT
        Assert.True(context.CanAccessCommunity(_communityAId));
        Assert.False(context.CanAccessCommunity(_communityBId));

        var foreignDbQueryArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["communityId"] = _communityBId.ToString(),
            ["entityName"] = "units"
        };
        var dbQueryResult = _evaluator.EvaluateToolPermission(context, "execute_database_query", foreignDbQueryArgs);
        Assert.False(dbQueryResult.IsPermitted, "Resident must not query foreign Community B.");
        Assert.Contains("outside your active property access scope", dbQueryResult.DenialReason, StringComparison.OrdinalIgnoreCase);

        var foreignSystemEntityArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["communityId"] = _communityBId.ToString(),
            ["entityType"] = "units"
        };
        var systemEntityResult = _evaluator.EvaluateToolPermission(context, "query_system_entities", foreignSystemEntityArgs);
        Assert.False(systemEntityResult.IsPermitted, "Resident must not inspect foreign Community B system entities.");

        var foreignNavArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["targetCommunityId"] = _communityBId.ToString()
        };
        var navResult = _evaluator.EvaluateToolPermission(context, "switch_ui_context_or_navigate", foreignNavArgs);
        Assert.False(navResult.IsPermitted, "Resident must not navigate to foreign Community B.");
    }

    [Fact]
    public async Task PermissionEvaluator_SecurityApprovalsAndVault_RestrictsSecretsAndExecutiveActions()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        // 1. Reveal vaulted access code -> DENIED
        var revealArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["actionType"] = "reveal_access_code",
            ["accessCodeId"] = Guid.NewGuid().ToString()
        };
        var revealResult = _evaluator.EvaluateToolPermission(context, "security_approvals_sos_and_vault", revealArgs);
        Assert.False(revealResult.IsPermitted);
        Assert.Contains("requires PropertyManager or FirmManager", revealResult.DenialReason);

        // 2. Decide AI action -> DENIED
        var aiActionArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["actionType"] = "decide_ai_action",
            ["aiActionId"] = Guid.NewGuid().ToString(),
            ["approve"] = "true"
        };
        var aiActionResult = _evaluator.EvaluateToolPermission(context, "security_approvals_sos_and_vault", aiActionArgs);
        Assert.False(aiActionResult.IsPermitted);
        Assert.Contains("ApproveAiActions", aiActionResult.DenialReason);

        // 3. SOS broadcast -> DENIED
        var sosArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["actionType"] = "sos_broadcast",
            ["stackCode"] = "STACK-A1"
        };
        var sosResult = _evaluator.EvaluateToolPermission(context, "security_approvals_sos_and_vault", sosArgs);
        Assert.False(sosResult.IsPermitted);
        Assert.Contains("ManagePropertySettings", sosResult.DenialReason);

        // 4. Verify audit chain -> PERMITTED (read-only verification)
        var auditArgs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["actionType"] = "verify_audit_chain"
        };
        var auditResult = _evaluator.EvaluateToolPermission(context, "security_approvals_sos_and_vault", auditArgs);
        Assert.True(auditResult.IsPermitted);
    }

    [Fact]
    public async Task AgenticLoop_FirmManager_ExecutesFullMultiTurnWorkflowSuccessfully()
    {
        var managerPrincipal = CreateFirmManagerPrincipal();
        var goal = "Query vacant units, create unit A-501, assign resident Jan Nowak, generate registration link, and run bulk utility settlement for Amber Park.";

        var liveEvents = new List<SparkAgentLiveStepEvent>();
        var progress = new Progress<SparkAgentLiveStepEvent>(liveEvents.Add);

        var result = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: managerPrincipal,
            activeCommunityId: _communityAId,
            stepByStepMode: false,
            progress: progress);

        Assert.NotNull(result);
        Assert.True(result.IsCompleted, "Agentic loop for manager should run to completion.");
        Assert.False(string.IsNullOrWhiteSpace(result.FinalResponseMarkdown));
        Assert.NotEmpty(result.Steps);

        // Verify that no step was denied for the Firm Manager
        Assert.DoesNotContain(result.Steps, s => s.PermissionGranted == false);

        // Verify that unit A-501 was physically created in the database
        await using var db = await _factory.CreateDbContextAsync();
        var createdUnit = await db.Units.FirstOrDefaultAsync(u => u.CommunityId == _communityAId && u.UnitNumber == "A-501");
        Assert.NotNull(createdUnit);
        Assert.Equal(5, createdUnit!.FloorNumber);
        Assert.Equal("STACK-A1", createdUnit.VerticalStackCode);
    }

    [Fact]
    public async Task AgenticLoop_Resident_EnforcesBoundary_AndLogsChainedAuditEvents()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var goal = "Resident account permissions and restricted actions test with ceiling leak report and meter reading.";

        var liveEvents = new List<SparkAgentLiveStepEvent>();
        var progress = new Progress<SparkAgentLiveStepEvent>(liveEvents.Add);

        var result = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: residentPrincipal,
            activeCommunityId: _communityAId,
            stepByStepMode: false,
            progress: progress);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Steps);

        // Permitted steps should be granted
        var permittedSteps = result.Steps.Where(s => s.PermissionGranted == true).ToList();
        Assert.NotEmpty(permittedSteps);

        // Restricted steps (e.g. create_unit_wizard, security_approvals_sos_and_vault) should be denied
        var deniedSteps = result.Steps.Where(s => s.PermissionGranted == false).ToList();
        Assert.NotEmpty(deniedSteps);
        Assert.Contains(deniedSteps, s => s.PermissionBadgeText == "PERMISSION_DENIED");

        // Verify that SPARK_AGENT_PERMISSION_DENIED audit events were saved to database
        await using var db = await _factory.CreateDbContextAsync();
        var auditEvents = await db.SecurityAuditEvents
            .Where(e => e.CommunityId == _communityAId && e.ActionCategory == "SPARK_AGENT_PERMISSION_DENIED")
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync();

        Assert.NotEmpty(auditEvents);
        foreach (var ev in auditEvents)
        {
            Assert.Equal("SPARK_AGENT_PERMISSION_DENIED", ev.ActionCategory);
            Assert.Equal(WorkspaceSecurityRole.Resident, ev.ActorRole);
            Assert.False(string.IsNullOrWhiteSpace(ev.EntrySha256Hash));
            Assert.False(string.IsNullOrWhiteSpace(ev.PreviousChainHash));
        }

        // Verify the entire community audit chain remains cryptographically valid
        var allEvents = await db.SecurityAuditEvents
            .Where(e => e.CommunityId == _communityAId)
            .OrderBy(e => e.OccurredAtUtc)
            .ToListAsync();

        var (isValid, verifiedCount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(allEvents);
        Assert.True(isValid, "Audit chain must be mathematically valid following permission denial logging.");
        Assert.True(verifiedCount >= auditEvents.Count);
    }

    [Fact]
    public async Task AgenticLoop_StepByStepMode_PausesAfterFirstToolCall()
    {
        var managerPrincipal = CreateFirmManagerPrincipal();
        var goal = "Query vacant units and create new units in Amber Park.";

        var result = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: managerPrincipal,
            activeCommunityId: _communityAId,
            stepByStepMode: true);

        Assert.NotNull(result);
        // In step-by-step mode, loop must halt after the first planned tool call
        Assert.Single(result.Steps, s => s.Phase == "Observation");
    }

    [Fact]
    public async Task AgenticLoop_ClarificationFlow_PausesAndResumes()
    {
        var managerPrincipal = CreateFirmManagerPrincipal();
        var goal = "Ambiguous assignment request: which unit should be assigned to the new resident?";

        // Turn 1: AI detects ambiguity and asks for clarification
        var turn1 = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: managerPrincipal,
            activeCommunityId: _communityAId,
            stepByStepMode: false);

        Assert.NotNull(turn1);
        Assert.True(turn1.IsAwaitingClarification, "Agent must pause when awaiting clarification.");
        Assert.False(string.IsNullOrWhiteSpace(turn1.ClarificationQuestion));
        Assert.NotNull(turn1.ClarificationOptions);
        Assert.NotEmpty(turn1.ClarificationOptions!);

        // Turn 2: User provides clarification response
        var turn2 = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: managerPrincipal,
            activeCommunityId: _communityAId,
            stepByStepMode: false,
            userClarificationResponse: "Unit 101");

        Assert.NotNull(turn2);
        Assert.True(turn2.IsCompleted, "Agent should complete goal once clarification is supplied.");
    }

    [Fact]
    public void AuditChainIntegrity_TamperDetection_EmpiricallyVerified()
    {
        var communityId = Guid.NewGuid();
        var events = new List<SecurityAuditEvent>();
        var previous = "GENESIS-0000000000000000";

        var baseTime = new DateTime(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 5; i++)
        {
            var ev = new SecurityAuditEvent
            {
                Id = Guid.NewGuid(),
                CommunityId = communityId,
                ActorRole = i % 2 == 0 ? WorkspaceSecurityRole.PropertyManager : WorkspaceSecurityRole.Resident,
                ActionCategory = i % 2 == 0 ? "LEDGER_MUTATION" : "SPARK_AGENT_PERMISSION_DENIED",
                Severity = "Warning",
                ResourceDescriptor = $"Tool: test_tool_{i}",
                Summary = $"Audit record number {i}",
                PreviousChainHash = previous,
                OccurredAtUtc = baseTime.AddSeconds(i * 10)
            };
            ev.EntrySha256Hash = DataSecurityAndPrivacyGuard.ComputeEntryHash(ev);
            previous = ev.EntrySha256Hash;
            events.Add(ev);
        }

        // 1. Untampered chain MUST be valid
        var (initialValid, initialCount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(events);
        Assert.True(initialValid);
        Assert.Equal(5, initialCount);

        // 2. Tamper Test A: Modify summary in the middle of the chain (event index 2)
        events[2].Summary = "MALICIOUSLY_ALTERED_SUMMARY";
        var (tamperAValid, tamperACount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(events);
        Assert.False(tamperAValid, "Audit chain integrity verification must detect tampered summary.");
        Assert.Equal(2, tamperACount); // Failed at index 2
        events[2].Summary = "Audit record number 2"; // restore

        // 3. Tamper Test B: Modify ActorRole (event index 3)
        events[3].ActorRole = WorkspaceSecurityRole.FirmManager;
        var (tamperBValid, tamperBCount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(events);
        Assert.False(tamperBValid, "Audit chain integrity verification must detect altered ActorRole.");
        Assert.Equal(3, tamperBCount);
        events[3].ActorRole = WorkspaceSecurityRole.Resident; // restore

        // 4. Tamper Test C: Modify PreviousChainHash (event index 4)
        events[4].PreviousChainHash = "FORGED-HASH-0000000000000000";
        var (tamperCValid, tamperCCount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(events);
        Assert.False(tamperCValid, "Audit chain integrity verification must detect altered PreviousChainHash.");
        Assert.Equal(4, tamperCCount);
        events[4].PreviousChainHash = events[3].EntrySha256Hash; // restore

        // 5. Tamper Test D: Attacker recalculates EntrySha256Hash for tampered event 2, but leaves event 3 pointing to old hash
        events[2].Summary = "RECALCULATED_HASH_ATTACK";
        events[2].EntrySha256Hash = DataSecurityAndPrivacyGuard.ComputeEntryHash(events[2]);
        var (tamperDValid, tamperDCount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(events);
        Assert.False(tamperDValid, "Audit chain verification must catch hash mismatch at subsequent event in chain.");
        Assert.Equal(3, tamperDCount); // Event 2 passes local check, but Event 3's PreviousChainHash doesn't match Event 2's new hash!
        events[2].Summary = "Audit record number 2";
        events[2].EntrySha256Hash = DataSecurityAndPrivacyGuard.ComputeEntryHash(events[2]); // restore

        // 6. Tamper Test E: Attacker deletes event 2 from sequence
        var shortened = new List<SecurityAuditEvent> { events[0], events[1], events[3], events[4] };
        var (tamperEValid, tamperECount) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(shortened);
        Assert.False(tamperEValid, "Audit chain verification must detect missing/deleted event in sequence.");
        Assert.Equal(2, tamperECount); // Event 3's PreviousChainHash points to deleted event 2, not event 1
    }

    [Fact]
    public async Task PermissionEvaluator_GuestOrUnauthenticatedUser_IsStrictlyDeniedFromMutations()
    {
        // Null or unauthenticated principal should resolve to GuestContext
        var guestContext = await _evaluator.ResolveContextAsync(null, _db);
        Assert.Equal(WorkspaceSecurityRole.ExternalAuditorView, guestContext.Role);
        Assert.Equal(WorkspaceMembershipStatus.PendingApproval, guestContext.MembershipStatus);
        Assert.Equal(0, (long)guestContext.GrantedPermissions);
        Assert.Empty(guestContext.AllowedCommunityIds);

        var mutationTools = new[]
        {
            "create_building",
            "create_community",
            "create_unit_wizard",
            "assign_resident_to_unit",
            "run_bulk_utility_settlement",
            "match_bank_transfers",
            "submit_maintenance_request",
            "submit_meter_reading",
            "update_community_profile"
        };

        foreach (var tool in mutationTools)
        {
            var res = _evaluator.EvaluateToolPermission(guestContext, tool, new Dictionary<string, string>
            {
                ["communityId"] = _communityAId.ToString()
            });
            Assert.False(res.IsPermitted, $"Guest user must be DENIED from calling mutation tool '{tool}'.");
        }
    }

    [Fact]
    public async Task PermissionEvaluator_CaseInsensitivity_CannotBypassDenial()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        // Attempting to bypass by using uppercase or mixed case
        var variations = new[]
        {
            ("CREATE_BUILDING", new Dictionary<string, string> { ["communityId"] = _communityAId.ToString() }),
            ("Create_Building", new Dictionary<string, string> { ["communityId"] = _communityAId.ToString() }),
            ("CREATE_UNIT_WIZARD", new Dictionary<string, string> { ["communityId"] = _communityAId.ToString() }),
            ("Security_Approvals_Sos_And_Vault", new Dictionary<string, string> { ["communityId"] = _communityAId.ToString(), ["actionType"] = "sos_broadcast" }),
            ("RUN_BULK_UTILITY_SETTLEMENT", new Dictionary<string, string> { ["communityId"] = _communityAId.ToString() })
        };

        foreach (var (tool, args) in variations)
        {
            var res = _evaluator.EvaluateToolPermission(context, tool, args);
            Assert.False(res.IsPermitted, $"Casing trick '{tool}' must NOT bypass permission denial.");
        }
    }

    [Fact]
    public async Task VulnerabilityCheck_PermissionEvaluator_AllowByDefault_PermitsUnknownOrUnclassifiedTools()
    {
        var residentPrincipal = CreateResidentPrincipal();
        var context = await _evaluator.ResolveContextAsync(residentPrincipal, _db);

        // An attacker requests an uncataloged / unclassified tool
        var res = _evaluator.EvaluateToolPermission(context, "unlisted_arbitrary_tool", new Dictionary<string, string>());

        Assert.False(res.IsPermitted, "Evaluator must enforce Default-Deny for unhandled tools.");
        Assert.Contains("not recognized or permitted", res.DenialReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VulnerabilityCheck_DynamicDbQuery_ZeroAllowedCommunities_LeakingAllCommunities()
    {
        var guestContext = await _evaluator.ResolveContextAsync(null, _db);
        Assert.Empty(guestContext.AllowedCommunityIds);

        // Calling execute_database_query with entityName: "communities"
        var res = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            new Dictionary<string, string> { ["entityName"] = "communities" },
            guestContext,
            Guid.Empty);

        Assert.True(res.IsSuccess);
        Assert.Contains("Retrieved 0 communities", res.Summary);
    }

    [Fact]
    public async Task ToolExecutor_EdgeCasesAndMalformedInputs_HandledSafelyWithoutCrash()
    {
        var managerPrincipal = CreateFirmManagerPrincipal();
        var context = await _evaluator.ResolveContextAsync(managerPrincipal, _db);

        // 1. Unknown tool
        var unknownRes = await _toolExecutor.ExecuteToolAsync(
            "unknown_nonexistent_tool",
            new Dictionary<string, string>(),
            context,
            _communityAId);
        Assert.False(unknownRes.IsSuccess);
        Assert.Contains("not recognized", unknownRes.Summary, StringComparison.OrdinalIgnoreCase);

        // 2. Malformed parameters in dynamic DB query
        var malformedDbArgs = new Dictionary<string, string>
        {
            ["entityName"] = "units",
            ["filterField"] = "UnitNumber'; DROP TABLE Units;--",
            ["filterValue"] = "' OR '1'='1",
            ["take"] = "-999"
        };
        var dbRes = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            malformedDbArgs,
            context,
            _communityAId);
        Assert.True(dbRes.IsSuccess, "SQL injection attempt should be handled safely by parameterized LINQ.");

        // 3. Negative reading value
        var negMeterArgs = new Dictionary<string, string>
        {
            ["unitNumber"] = "A-101",
            ["simulatedReadingOverride"] = "-9999.5"
        };
        var meterRes = await _toolExecutor.ExecuteToolAsync(
            "submit_meter_reading",
            negMeterArgs,
            context,
            _communityAId);
        Assert.True(meterRes.IsSuccess);
    }
}

