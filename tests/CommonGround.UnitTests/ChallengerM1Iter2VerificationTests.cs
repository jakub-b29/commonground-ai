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
/// Empirical challenge and stress tests for Milestone M1 (Iteration 2).
/// Adversarially stress-tests:
/// 1. FirmManager community isolation & cross-community parameter tampering
/// 2. Resident unit privacy across units, work orders, and utility meters
/// 3. Default case structured error handling for unindexed entities
/// 4. Fallback planner residentProfileId extraction and chained feed-forward
/// </summary>
public sealed class ChallengerM1Iter2VerificationTests : IDisposable
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
    private readonly Guid _commA1Id = Guid.NewGuid();
    private readonly Guid _commA2Id = Guid.NewGuid();
    private readonly Guid _commB1Id = Guid.NewGuid();

    private readonly Guid _unitA101Id = Guid.NewGuid();
    private readonly Guid _unitA102Id = Guid.NewGuid();
    private readonly Guid _unitB201Id = Guid.NewGuid();

    private readonly Guid _meterA101Id = Guid.NewGuid();
    private readonly Guid _meterA102Id = Guid.NewGuid();
    private readonly Guid _meterB201Id = Guid.NewGuid();
    private readonly Guid _residentUserId = Guid.NewGuid();

    public ChallengerM1Iter2VerificationTests()
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

        SeedStressData();
    }

    private void SeedStressData()
    {
        var firmA = new ManagementFirm
        {
            Id = _firmAId,
            Name = "Firm Alpha Sp. z o.o.",
            ManagingFirmLegalName = "Firm Alpha Sp. z o.o.",
            FirmTaxIdOrNip = "111-222-33-44",
            SupportEmail = "alpha@firm.local"
        };
        var firmB = new ManagementFirm
        {
            Id = _firmBId,
            Name = "Firm Beta LLC",
            ManagingFirmLegalName = "Firm Beta LLC",
            FirmTaxIdOrNip = "999-888-77-66",
            SupportEmail = "beta@firm.local"
        };
        _db.ManagementFirms.AddRange(firmA, firmB);

        var commA1 = new Community
        {
            Id = _commA1Id,
            ManagementFirmId = _firmAId,
            Name = "Amber Park Residence",
            StreetAddress = "Bursztynowa 14",
            City = "Warsaw",
            CurrencyCode = "PLN",
            OperatingMode = SystemOperatingMode.HousingAssociation,
            CommunityAccountNumber = "COMM-WAR-001"
        };
        var commA2 = new Community
        {
            Id = _commA2Id,
            ManagementFirmId = _firmAId,
            Name = "Emerald Hill Apartments",
            StreetAddress = "Szmaragdowa 8",
            City = "Warsaw",
            CurrencyCode = "PLN",
            OperatingMode = SystemOperatingMode.HousingAssociation,
            CommunityAccountNumber = "COMM-WAR-002"
        };
        var commB1 = new Community
        {
            Id = _commB1Id,
            ManagementFirmId = _firmBId,
            Name = "Oakridge Palms Condominium",
            StreetAddress = "100 Ocean Drive",
            City = "Miami",
            CurrencyCode = "USD",
            OperatingMode = SystemOperatingMode.UsHoa,
            CommunityAccountNumber = "COMM-MIA-003"
        };
        _db.Communities.AddRange(commA1, commA2, commB1);

        var bldA = new Building
        {
            Id = Guid.NewGuid(),
            CommunityId = _commA1Id,
            Name = "Building A",
            FloorsCount = 5,
            StairwellsCount = 2
        };
        var bldB = new Building
        {
            Id = Guid.NewGuid(),
            CommunityId = _commB1Id,
            Name = "Tower One",
            FloorsCount = 10,
            StairwellsCount = 1
        };
        _db.Buildings.AddRange(bldA, bldB);

        var uA101 = new Unit
        {
            Id = _unitA101Id,
            CommunityId = _commA1Id,
            BuildingId = bldA.Id,
            UnitNumber = "A-101",
            FloorNumber = 1,
            StairwellNumber = 1,
            FloorAreaM2 = 55.0m,
            OccupancyStatus = UnitOccupancyStatus.OwnerOccupied,
            CurrentBalance = 120.00m
        };
        var uA102 = new Unit
        {
            Id = _unitA102Id,
            CommunityId = _commA1Id,
            BuildingId = bldA.Id,
            UnitNumber = "A-102",
            FloorNumber = 1,
            StairwellNumber = 1,
            FloorAreaM2 = 48.0m,
            OccupancyStatus = UnitOccupancyStatus.Vacant,
            CurrentBalance = 0.00m
        };
        var uB201 = new Unit
        {
            Id = _unitB201Id,
            CommunityId = _commB1Id,
            BuildingId = bldB.Id,
            UnitNumber = "B-201",
            FloorNumber = 2,
            StairwellNumber = 1,
            FloorAreaM2 = 75.0m,
            OccupancyStatus = UnitOccupancyStatus.TenantOccupied,
            CurrentBalance = 500.00m
        };
        _db.Units.AddRange(uA101, uA102, uB201);

        var meterA101 = new UtilityMeter
        {
            Id = _meterA101Id,
            UnitId = _unitA101Id,
            SerialNumber = "MTR-A101-W",
            Kind = UtilityKind.ColdWater,
            LastReadingValue = 150m,
            PreviousReadingValue = 140m
        };
        var meterA102 = new UtilityMeter
        {
            Id = _meterA102Id,
            UnitId = _unitA102Id,
            SerialNumber = "MTR-A102-W",
            Kind = UtilityKind.ColdWater,
            LastReadingValue = 40m,
            PreviousReadingValue = 40m
        };
        var meterB201 = new UtilityMeter
        {
            Id = _meterB201Id,
            UnitId = _unitB201Id,
            SerialNumber = "MTR-B201-E",
            Kind = UtilityKind.Electricity,
            LastReadingValue = 890m,
            PreviousReadingValue = 800m
        };
        _db.UtilityMeters.AddRange(meterA101, meterA102, meterB201);

        var woCommon = new WorkOrder
        {
            Id = Guid.NewGuid(),
            CommunityId = _commA1Id,
            UnitId = null,
            Title = "Main stairwell lighting replacement",
            Category = WorkOrderCategory.Electrical,
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.Reported
        };
        var woUnitA101 = new WorkOrder
        {
            Id = Guid.NewGuid(),
            CommunityId = _commA1Id,
            UnitId = _unitA101Id,
            Title = "A-101 bathroom tap leak",
            Category = WorkOrderCategory.Plumbing,
            Priority = WorkOrderPriority.Emergency,
            Status = WorkOrderStatus.Reported
        };
        var woUnitA102 = new WorkOrder
        {
            Id = Guid.NewGuid(),
            CommunityId = _commA1Id,
            UnitId = _unitA102Id,
            Title = "A-102 radiator valve whistling",
            Category = WorkOrderCategory.HeatingAndHvac,
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.Reported
        };
        var woFirmB = new WorkOrder
        {
            Id = Guid.NewGuid(),
            CommunityId = _commB1Id,
            UnitId = _unitB201Id,
            Title = "B-201 balcony railing corrosion",
            Category = WorkOrderCategory.RoofAndFacade,
            Priority = WorkOrderPriority.High,
            Status = WorkOrderStatus.Reported
        };
        _db.WorkOrders.AddRange(woCommon, woUnitA101, woUnitA102, woFirmB);

        var resA1 = new Resolution
        {
            Id = Guid.NewGuid(),
            CommunityId = _commA1Id,
            Number = "Res 01/2026",
            Title = "Annual Budget 2026",
            MajorityRule = VotingMajorityRule.ShareWeightedMajority,
            ComplianceStatus = "Compliant"
        };
        var resB1 = new Resolution
        {
            Id = Guid.NewGuid(),
            CommunityId = _commB1Id,
            Number = "Res 01/2026-B",
            Title = "Security Gate Replacement",
            MajorityRule = VotingMajorityRule.OneOwnerOneVote,
            ComplianceStatus = "Compliant"
        };
        _db.Resolutions.AddRange(resA1, resB1);

        var residentUser = new User
        {
            Id = _residentUserId,
            FullName = "Tomasz Kaminski",
            Email = "tomasz.kaminski@amber.local",
            PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("ResidentPass123!"),
            EmailConfirmed = true,
            Provider = IdentityProvider.EmailPassword
        };
        _db.Users.Add(residentUser);

        var residentMembership = new FirmMembership
        {
            Id = Guid.NewGuid(),
            UserId = _residentUserId,
            ManagementFirmId = _firmAId,
            Role = WorkspaceSecurityRole.Resident,
            Status = WorkspaceMembershipStatus.Approved,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident),
            DecidedAtUtc = DateTime.UtcNow
        };
        residentMembership.PropertyAccesses.Add(new MembershipPropertyAccess
        {
            FirmMembershipId = residentMembership.Id,
            CommunityId = _commA1Id,
            IsPrimaryManager = false
        });
        residentMembership.UnitLinks.Add(new MembershipUnitLink
        {
            FirmMembershipId = residentMembership.Id,
            CommunityId = _commA1Id,
            UnitId = _unitA101Id,
            RelationshipKind = MembershipUnitRelationshipKind.Owner
        });
        _db.FirmMemberships.Add(residentMembership);

        _db.SaveChanges();
    }

    [Fact]
    public async Task Area1_FirmManager_CrossCommunityParameterTampering_ReturnsZeroRows()
    {
        // Manager belongs to Firm Alpha
        var alphaManagerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Firm Alpha Manager"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firmAId.ToString()),
            new Claim("community_id", _commA1Id.ToString())
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(alphaManagerPrincipal, _db);

        // Attack scenario 1: Alpha manager explicitly passes Firm Beta's community ID to query units
        var unitArgs = new Dictionary<string, string>
        {
            ["entityName"] = "units",
            ["take"] = "10"
        };
        var unitObs = await _toolExecutor.ExecuteToolAsync("execute_database_query", unitArgs, context, _commB1Id);
        Assert.True(unitObs.IsSuccess);
        using (var doc = JsonDocument.Parse(unitObs.ObservationJson))
        {
            var count = doc.RootElement.GetProperty("count").GetInt32();
            Assert.Equal(0, count);
        }

        // Attack scenario 2: Alpha manager explicitly passes Firm Beta's community ID to query work orders
        var woArgs = new Dictionary<string, string>
        {
            ["entityName"] = "work_orders",
            ["take"] = "10"
        };
        var woObs = await _toolExecutor.ExecuteToolAsync("execute_database_query", woArgs, context, _commB1Id);
        Assert.True(woObs.IsSuccess);
        using (var doc = JsonDocument.Parse(woObs.ObservationJson))
        {
            var count = doc.RootElement.GetProperty("count").GetInt32();
            Assert.Equal(0, count);
        }

        // Attack scenario 3: Alpha manager explicitly passes Firm Beta's community ID to query resolutions
        var resArgs = new Dictionary<string, string>
        {
            ["entityName"] = "resolutions",
            ["take"] = "10"
        };
        var resObs = await _toolExecutor.ExecuteToolAsync("execute_database_query", resArgs, context, _commB1Id);
        Assert.True(resObs.IsSuccess);
        using (var doc = JsonDocument.Parse(resObs.ObservationJson))
        {
            var count = doc.RootElement.GetProperty("count").GetInt32();
            Assert.Equal(0, count);
        }

        // Attack scenario 4: Alpha manager queries communities — must return only Alpha's 2 communities, NEVER Beta's
        var commArgs = new Dictionary<string, string>
        {
            ["entityName"] = "communities",
            ["take"] = "10"
        };
        var commObs = await _toolExecutor.ExecuteToolAsync("execute_database_query", commArgs, context, _commA1Id);
        Assert.True(commObs.IsSuccess);
        using (var doc = JsonDocument.Parse(commObs.ObservationJson))
        {
            var rows = doc.RootElement.GetProperty("rows");
            var returnedIds = rows.EnumerateArray().Select(r => Guid.Parse(r.GetProperty("id").GetString()!)).ToList();
            Assert.Contains(_commA1Id, returnedIds);
            Assert.Contains(_commA2Id, returnedIds);
            Assert.DoesNotContain(_commB1Id, returnedIds);
        }
    }

    [Fact]
    public async Task Area1_FirmManager_WithoutFirmClaim_ReceivesZeroCommunities()
    {
        // FirmManager claim with NO firm_id and NO allowed_communities
        var brokenManagerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Orphan Manager"),
            new Claim(ClaimTypes.Role, "FirmManager")
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(brokenManagerPrincipal, _db);
        Assert.Null(context.ManagementFirmId);
        Assert.Empty(context.AllowedCommunityIds);

        var queryArgs = new Dictionary<string, string>
        {
            ["entityName"] = "communities",
            ["take"] = "10"
        };
        var obs = await _toolExecutor.ExecuteToolAsync("execute_database_query", queryArgs, context, _commA1Id);
        Assert.True(obs.IsSuccess);
        using var doc = JsonDocument.Parse(obs.ObservationJson);
        var count = doc.RootElement.GetProperty("count").GetInt32();
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Area2_Resident_UnitPrivacy_CannotSeeRivalUnitsOrPrivateWorkOrders()
    {
        // Resident in Comm A1 linked strictly to Unit A-101
        var residentPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _residentUserId.ToString()),
            new Claim(ClaimTypes.Name, "Tomasz Kaminski"),
            new Claim(ClaimTypes.Role, "Resident"),
            new Claim("community_id", _commA1Id.ToString()),
            new Claim("unit_id", _unitA101Id.ToString()),
            new Claim("unit_number", "A-101")
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(residentPrincipal, _db);
        Assert.Equal("A-101", context.LinkedUnitNumber);
        Assert.Equal(_unitA101Id, context.LinkedUnitId);

        // 1. Units query: should ONLY see A-101, NOT A-102
        var unitObs = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            new Dictionary<string, string> { ["entityName"] = "units", ["take"] = "10" },
            context,
            _commA1Id);
        Assert.True(unitObs.IsSuccess);
        using (var doc = JsonDocument.Parse(unitObs.ObservationJson))
        {
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            Assert.Single(rows);
            Assert.Equal("A-101", rows[0].GetProperty("unitNumber").GetString());
        }

        // 2. Work Orders query: should see Common Property work orders and A-101, but NOT A-102
        var woObs = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            new Dictionary<string, string> { ["entityName"] = "work_orders", ["take"] = "10" },
            context,
            _commA1Id);
        Assert.True(woObs.IsSuccess);
        using (var doc = JsonDocument.Parse(woObs.ObservationJson))
        {
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            var titles = rows.Select(r => r.GetProperty("title").GetString()).ToList();
            Assert.Contains("Main stairwell lighting replacement", titles); // Common property (UnitId == null)
            Assert.Contains("A-101 bathroom tap leak", titles);             // Resident's own unit
            Assert.DoesNotContain("A-102 radiator valve whistling", titles); // Another resident's private unit!
        }

        // 3. Utility Meters query: should ONLY see MTR-A101-W, NOT MTR-A102-W
        var meterObs = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            new Dictionary<string, string> { ["entityName"] = "utility_meters", ["take"] = "10" },
            context,
            _commA1Id);
        Assert.True(meterObs.IsSuccess);
        using (var doc = JsonDocument.Parse(meterObs.ObservationJson))
        {
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            Assert.Single(rows);
            Assert.Equal("MTR-A101-W", rows[0].GetProperty("serialNumber").GetString());
        }
    }

    [Fact]
    public async Task Area2_UnlinkedResident_ReceivesZeroPrivateData()
    {
        // Resident in Comm A1 with no linked unit
        var unlinkedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Unlinked Resident"),
            new Claim(ClaimTypes.Role, "Resident"),
            new Claim("community_id", _commA1Id.ToString())
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(unlinkedPrincipal, _db);
        Assert.Null(context.LinkedUnitNumber);
        Assert.Null(context.LinkedUnitId);

        // 1. Units query -> 0 units
        var unitObs = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            new Dictionary<string, string> { ["entityName"] = "units", ["take"] = "10" },
            context,
            _commA1Id);
        Assert.True(unitObs.IsSuccess);
        using (var doc = JsonDocument.Parse(unitObs.ObservationJson))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        }

        // 2. Meters query -> 0 meters
        var meterObs = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            new Dictionary<string, string> { ["entityName"] = "utility_meters", ["take"] = "10" },
            context,
            _commA1Id);
        Assert.True(meterObs.IsSuccess);
        using (var doc = JsonDocument.Parse(meterObs.ObservationJson))
        {
            Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        }

        // 3. Work orders query -> ONLY common property (UnitId == null), 0 private unit work orders
        var woObs = await _toolExecutor.ExecuteToolAsync(
            "execute_database_query",
            new Dictionary<string, string> { ["entityName"] = "work_orders", ["take"] = "10" },
            context,
            _commA1Id);
        Assert.True(woObs.IsSuccess);
        using (var doc = JsonDocument.Parse(woObs.ObservationJson))
        {
            var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
            Assert.Single(rows); // Only the stairwell lighting
            Assert.Equal("Main stairwell lighting replacement", rows[0].GetProperty("title").GetString());
        }
    }

    [Theory]
    [InlineData("ledger_accounts")]
    [InlineData("journal_entries")]
    [InlineData("users")]
    [InlineData("bank_transactions")]
    [InlineData("ancillary_contracts")]
    [InlineData("vendor_bills")]
    [InlineData("nonexistent_random_table")]
    public async Task Area3_DefaultCase_ReturnsStructuredErrorForUnindexedEntities(string unindexedEntity)
    {
        var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Alpha Admin"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firmAId.ToString()),
            new Claim("community_id", _commA1Id.ToString())
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(managerPrincipal, _db);

        var queryArgs = new Dictionary<string, string>
        {
            ["entityName"] = unindexedEntity,
            ["take"] = "10"
        };

        var obs = await _toolExecutor.ExecuteToolAsync("execute_database_query", queryArgs, context, _commA1Id);

        // Verification of contract:
        Assert.False(obs.IsSuccess, $"Query for unindexed entity '{unindexedEntity}' must have IsSuccess = false.");
        Assert.Contains("not indexed for dynamic database queries", obs.Summary, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(obs.ObservationJson);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Contains("not indexed for dynamic database queries", errorProp.GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Supported entities: units, communities, work_orders, resolutions, utility_meters", errorProp.GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Area4_FallbackPlanner_ExtractsResidentProfileIdAndFeedsForwardIntoLinkAndFinalAnswer()
    {
        var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Firm Admin"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firmAId.ToString()),
            new Claim("community_id", _commA1Id.ToString())
        }, "TestCookie"));

        var goal = "Inspect Amber park vacant units, create unit A-501 on floor 5, assign Jan Nowak as tenant, issue registration link, and run bulk utility reconciliation.";

        var liveSteps = new List<SparkAgentLiveStepEvent>();
        var progress = new Progress<SparkAgentLiveStepEvent>(e => liveSteps.Add(e));

        var result = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: managerPrincipal,
            activeCommunityId: _commA1Id,
            stepByStepMode: false,
            progress: progress);

        Assert.True(result.IsCompleted);

        // 1. Locate assign_resident_to_unit step observation
        var assignStep = result.Steps.FirstOrDefault(s => s.ToolName == "assign_resident_to_unit" && s.Phase == "Observation");
        Assert.NotNull(assignStep);
        Assert.NotNull(assignStep.ObservationJson);

        using var assignDoc = JsonDocument.Parse(assignStep.ObservationJson);
        Assert.True(assignDoc.RootElement.TryGetProperty("residentProfileId", out var rpidProp));
        var assignedProfileId = rpidProp.GetString();
        Assert.False(string.IsNullOrWhiteSpace(assignedProfileId));

        // 2. Locate generate_registration_link step tool arguments
        var linkStep = result.Steps.FirstOrDefault(s => s.ToolName == "generate_registration_link" && s.Phase == "Observation");
        Assert.NotNull(linkStep);
        Assert.NotNull(linkStep.ToolArgumentsJson);

        using var linkArgDoc = JsonDocument.Parse(linkStep.ToolArgumentsJson);
        Assert.True(linkArgDoc.RootElement.TryGetProperty("residentProfileId", out var linkFedRpidProp));
        var linkFedProfileId = linkFedRpidProp.GetString();

        // 3. Empirically assert that the exact residentProfileId was fed forward!
        Assert.Equal(assignedProfileId, linkFedProfileId);

        // 4. Empirically verify that final synthesized markdown contains the residentProfileId
        Assert.Contains(assignedProfileId, result.FinalResponseMarkdown);
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
