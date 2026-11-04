using System.Security.Claims;
using System.Text.Json;
using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Application.Services;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.MetaModels;
using CommonGround.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommonGround.UnitTests;

public sealed class ChallengerM1EmpiricalTests : IDisposable
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

    private readonly Guid _firm1Id = Guid.NewGuid();
    private readonly Guid _firm2Id = Guid.NewGuid();
    private readonly Guid _comm1Id = Guid.NewGuid();
    private readonly Guid _comm2Id = Guid.NewGuid();

    public ChallengerM1EmpiricalTests()
    {
        var root = new InMemoryDatabaseRoot();
        var dbName = $"cg-challenger-{Guid.NewGuid():N}";
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName, root)
            .Options;
        _db = new AppDbContext(_options);
        _factory = new TestFactory(_options);

        var metaOptions = Options.Create(new MetaModelOptions { ApiKey = string.Empty, UseFallbackWhenNoKey = false });
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
        var firm1 = new ManagementFirm
        {
            Id = _firm1Id,
            Name = "Firm Alpha Sp. z o.o.",
            ManagingFirmLegalName = "Firm Alpha Sp. z o.o.",
            FirmTaxIdOrNip = "111-222-33-44",
            SupportEmail = "alpha@firm.local"
        };
        var firm2 = new ManagementFirm
        {
            Id = _firm2Id,
            Name = "Firm Beta LLC",
            ManagingFirmLegalName = "Firm Beta LLC",
            FirmTaxIdOrNip = "999-888-77-66",
            SupportEmail = "beta@firm.local"
        };
        _db.ManagementFirms.AddRange(firm1, firm2);

        var comm1 = new Community
        {
            Id = _comm1Id,
            ManagementFirmId = _firm1Id,
            Name = "Amber Park Residence",
            StreetAddress = "Bursztynowa 14",
            City = "Warsaw",
            CurrencyCode = "PLN",
            OperatingMode = SystemOperatingMode.HousingAssociation,
            CommunityAccountNumber = "COMM-WAR-001"
        };
        var comm2 = new Community
        {
            Id = _comm2Id,
            ManagementFirmId = _firm2Id,
            Name = "Oakridge Palms Condominium",
            StreetAddress = "100 Ocean Drive",
            City = "Miami",
            CurrencyCode = "USD",
            OperatingMode = SystemOperatingMode.UsHoa,
            CommunityAccountNumber = "COMM-MIA-002"
        };
        _db.Communities.AddRange(comm1, comm2);

        var bld1 = new Building
        {
            Id = Guid.NewGuid(),
            CommunityId = _comm1Id,
            Name = "Building A",
            FloorsCount = 5,
            StairwellsCount = 2
        };
        var bld2 = new Building
        {
            Id = Guid.NewGuid(),
            CommunityId = _comm2Id,
            Name = "Tower One",
            FloorsCount = 10,
            StairwellsCount = 1
        };
        _db.Buildings.AddRange(bld1, bld2);

        var u1 = new Unit
        {
            Id = Guid.NewGuid(),
            CommunityId = _comm1Id,
            BuildingId = bld1.Id,
            UnitNumber = "A-101",
            FloorNumber = 1,
            StairwellNumber = 1,
            FloorAreaM2 = 55.0m,
            OccupancyStatus = UnitOccupancyStatus.OwnerOccupied,
            CurrentBalance = 120.00m
        };
        var u2 = new Unit
        {
            Id = Guid.NewGuid(),
            CommunityId = _comm1Id,
            BuildingId = bld1.Id,
            UnitNumber = "A-102",
            FloorNumber = 1,
            StairwellNumber = 1,
            FloorAreaM2 = 48.0m,
            OccupancyStatus = UnitOccupancyStatus.Vacant,
            CurrentBalance = 0.00m
        };
        var u3 = new Unit
        {
            Id = Guid.NewGuid(),
            CommunityId = _comm2Id,
            BuildingId = bld2.Id,
            UnitNumber = "T-201",
            FloorNumber = 2,
            StairwellNumber = 1,
            FloorAreaM2 = 75.0m,
            OccupancyStatus = UnitOccupancyStatus.TenantOccupied,
            CurrentBalance = 500.00m
        };
        _db.Units.AddRange(u1, u2, u3);

        var wo1 = new WorkOrder
        {
            Id = Guid.NewGuid(),
            CommunityId = _comm1Id,
            UnitId = u1.Id,
            Title = "Private pipe leak under sink",
            Category = WorkOrderCategory.Plumbing,
            Priority = WorkOrderPriority.Normal,
            Status = WorkOrderStatus.Reported
        };
        var wo2 = new WorkOrder
        {
            Id = Guid.NewGuid(),
            CommunityId = _comm2Id,
            UnitId = u3.Id,
            Title = "Roof terrace drainage blockage",
            Category = WorkOrderCategory.RoofAndFacade,
            Priority = WorkOrderPriority.High,
            Status = WorkOrderStatus.Reported
        };
        _db.WorkOrders.AddRange(wo1, wo2);

        _db.SaveChanges();
    }

    [Fact]
    public async Task Empirical_ReActMultiTurnLoop_ExecutesFullFlowAndFeedsForwardUnitId()
    {
        // 1. Arrange a FirmManager principal
        var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Firm Admin"),
            new Claim(ClaimTypes.Email, "admin@firm-alpha.local"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firm1Id.ToString()),
            new Claim("community_id", _comm1Id.ToString())
        }, "TestCookie"));

        var liveSteps = new List<SparkAgentLiveStepEvent>();
        var progress = new Progress<SparkAgentLiveStepEvent>(e => liveSteps.Add(e));

        var goal = "Inspect Amber park vacant units, create unit A-501 on floor 5, assign Jan Nowak as tenant, issue registration link, and run bulk utility reconciliation.";

        // 2. Act: Run agentic turn
        var result = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: managerPrincipal,
            activeCommunityId: _comm1Id,
            stepByStepMode: false,
            progress: progress);

        // 3. Assert: Loop completed
        Assert.NotNull(result);
        Assert.True(result.IsCompleted, "Agent run should mark IsCompleted = true");
        Assert.False(string.IsNullOrWhiteSpace(result.FinalResponseMarkdown));

        // Verify intermediate steps
        var toolSteps = result.Steps.Where(s => s.Phase == "Observation" && s.PermissionGranted == true).ToList();
        Assert.True(toolSteps.Count >= 5, $"Expected at least 5 successful tool observations, got {toolSteps.Count}");

        // Empirically check unitId feed-forward:
        var createUnitStep = toolSteps.FirstOrDefault(s => s.ToolName == "create_unit_wizard");
        Assert.NotNull(createUnitStep);
        Assert.NotNull(createUnitStep.ObservationJson);

        using var createDoc = JsonDocument.Parse(createUnitStep.ObservationJson);
        Assert.True(createDoc.RootElement.TryGetProperty("unitId", out var createdUnitIdProp));
        var createdUnitGuid = createdUnitIdProp.GetString();
        Assert.NotNull(createdUnitGuid);

        var assignStep = toolSteps.FirstOrDefault(s => s.ToolName == "assign_resident_to_unit");
        Assert.NotNull(assignStep);
        Assert.NotNull(assignStep.ToolArgumentsJson);

        using var assignDoc = JsonDocument.Parse(assignStep.ToolArgumentsJson);
        Assert.True(assignDoc.RootElement.TryGetProperty("unitId", out var fedUnitIdProp));
        var fedUnitGuid = fedUnitIdProp.GetString();

        // Check if observation from create_unit_wizard was fed forward to assign_resident_to_unit
        Assert.Equal(createdUnitGuid, fedUnitGuid);
    }

    [Fact]
    public async Task Empirical_DynamicDbQuery_Communities_CrossFirmTenantLeakVulnerability()
    {
        // Manager of Firm Alpha queries "communities"
        var alphaManagerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Alpha Admin"),
            new Claim(ClaimTypes.Email, "alpha@firm.local"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firm1Id.ToString()),
            new Claim("community_id", _comm1Id.ToString())
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(alphaManagerPrincipal, _db);

        var queryArgs = new Dictionary<string, string>
        {
            ["entityName"] = "communities",
            ["take"] = "10"
        };

        var obs = await _toolExecutor.ExecuteToolAsync("execute_database_query", queryArgs, context, _comm1Id);
        Assert.True(obs.IsSuccess);

        using var doc = JsonDocument.Parse(obs.ObservationJson);
        var rows = doc.RootElement.GetProperty("rows");

        // Count how many communities were returned
        var returnedComms = new List<Guid>();
        foreach (var r in rows.EnumerateArray())
        {
            if (r.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var cid))
            {
                returnedComms.Add(cid);
            }
        }

        // VULNERABILITY CHECK: Did Firm Alpha Manager see Firm Beta's community (_comm2Id)?
        // If Firm 1 manager sees Firm 2's community, that is an empirical tenant leak finding!
        bool leaksFirm2Community = returnedComms.Contains(_comm2Id);
        // We record this observation:
        Assert.False(leaksFirm2Community, "Firm Alpha Manager must never receive communities belonging to Firm Beta!");
        Assert.Contains(_comm1Id, returnedComms);
    }

    [Fact]
    public async Task Empirical_DynamicDbQuery_Resident_UnlinkedUnit_LeaksAllUnitsVulnerability()
    {
        // Resident in Comm 1 whose LinkedUnitNumber is null/unresolved
        var residentPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Resident Without Unit"),
            new Claim(ClaimTypes.Email, "pending.resident@commonground.local"),
            new Claim(ClaimTypes.Role, "Resident"),
            new Claim("community_id", _comm1Id.ToString())
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(residentPrincipal, _db);
        Assert.Null(context.LinkedUnitNumber);

        var queryArgs = new Dictionary<string, string>
        {
            ["entityName"] = "units",
            ["take"] = "10"
        };

        var obs = await _toolExecutor.ExecuteToolAsync("execute_database_query", queryArgs, context, _comm1Id);
        Assert.True(obs.IsSuccess);

        using var doc = JsonDocument.Parse(obs.ObservationJson);
        var count = doc.RootElement.GetProperty("count").GetInt32();

        // VULNERABILITY CHECK: When LinkedUnitNumber is null, does it return ALL units in the community?
        // In Comm1 we have 2 units (A-101 and A-102).
        // A resident without a linked unit should NOT see all units and balances!
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Empirical_DynamicDbQuery_DefaultEntity_CountsUnitsInsteadOfQuerying()
    {
        var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Firm Admin"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firm1Id.ToString()),
            new Claim("community_id", _comm1Id.ToString())
        }, "TestCookie"));

        var context = await _permissionEvaluator.ResolveContextAsync(managerPrincipal, _db);

        var queryArgs = new Dictionary<string, string>
        {
            ["entityName"] = "ledger_accounts",
            ["take"] = "10"
        };

        var obs = await _toolExecutor.ExecuteToolAsync("execute_database_query", queryArgs, context, _comm1Id);
        Assert.False(obs.IsSuccess);

        using var doc = JsonDocument.Parse(obs.ObservationJson);
        Assert.True(doc.RootElement.TryGetProperty("error", out var errProp));
        Assert.Contains("not indexed for dynamic database queries", errProp.GetString());
        Assert.Contains("ledger_accounts", errProp.GetString());
    }

    [Fact]
    public async Task Empirical_ObservationFeedForward_ResidentProfileId_IsNotForwardedInFallbackPlanner()
    {
        // When checking FallbackAgenticTurnPlanner:
        // Does the fallback planner ever search for or forward residentProfileId?
        // We know from code inspection of MetaModelClients.cs that FindPropertyInObservations is only called for:
        // "unitId", "plainToken", "registrationUrl".
        // Let's verify whether residentProfileId is forwarded into any subsequent tool call.

        var managerPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Name, "Firm Admin"),
            new Claim(ClaimTypes.Role, "FirmManager"),
            new Claim("firm_id", _firm1Id.ToString()),
            new Claim("community_id", _comm1Id.ToString())
        }, "TestCookie"));

        var goal = "Inspect Amber park vacant units, create unit A-501 on floor 5, assign Jan Nowak as tenant, issue registration link, and run bulk utility reconciliation.";

        var result = await _agentService.ExecuteAgenticTurnAsync(
            userGoal: goal,
            userPrincipal: managerPrincipal,
            activeCommunityId: _comm1Id,
            stepByStepMode: false);

        var toolSteps = result.Steps.Where(s => s.Phase == "Observation" && s.PermissionGranted == true).ToList();

        // Check all arguments across all tool steps
        bool residentProfileIdEverFed = false;
        foreach (var s in toolSteps)
        {
            if (s.ToolArgumentsJson != null && s.ToolArgumentsJson.Contains("residentProfileId", StringComparison.OrdinalIgnoreCase))
            {
                residentProfileIdEverFed = true;
            }
        }

        // Assert that residentProfileId was fed forward in subsequent tools in the 5-step loop!
        Assert.True(residentProfileIdEverFed, "residentProfileId should be extracted and fed forward in the fallback planner loop.");
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
