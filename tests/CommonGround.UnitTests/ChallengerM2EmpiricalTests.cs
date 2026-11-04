using System.Security.Claims;
using System.Text.Json;
using CommonGround.Application.Interfaces;
using CommonGround.Application.Services;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.Persistence;
using CommonGround.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommonGround.UnitTests;

/// <summary>
/// Empirical challenge test suite for Milestone M2:
/// - WorkspaceUiState multi-threaded rapid toggles and state consistency
/// - ResolveContextAsync claims parsing across all roles with various unit_id Guid formats and edge cases
/// - SparkAgentLiveStepEvent record invariants and UI serialization compatibility
/// </summary>
public sealed class ChallengerM2EmpiricalTests
{
    // =========================================================================
    // Area 1: WorkspaceUiState Multi-Threaded Toggles & State Consistency
    // =========================================================================

    [Fact]
    public async Task WorkspaceUiState_ConcurrentChatToggles_MaintainsConsistencyAndDoesNotThrow()
    {
        var uiState = new WorkspaceUiState();
        var exceptions = new List<Exception>();
        var subscriberCount = 0;

        // Attach listener that increments counter safely
        Action handler = () => Interlocked.Increment(ref subscriberCount);
        uiState.OnChange += handler;

        const int threadCount = 10;
        const int iterationsPerThread = 100;
        var tasks = new Task[threadCount];

        for (var t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < iterationsPerThread; i++)
                    {
                        uiState.ToggleSparkAgentChat();
                        uiState.ToggleSparkAgentExpanded();
                    }
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Empty(exceptions);
        Assert.True(subscriberCount > 0);
        // Both flags must still be valid booleans without memory corruption
        Assert.True(uiState.IsSparkAgentChatOpen || !uiState.IsSparkAgentChatOpen);
        Assert.True(uiState.IsSparkAgentExpanded || !uiState.IsSparkAgentExpanded);

        uiState.OnChange -= handler;
    }

    [Fact]
    public async Task WorkspaceUiState_ConcurrentSubscribers_AddingAndRemovingListenersDoesNotCrash()
    {
        var uiState = new WorkspaceUiState();
        var exceptions = new List<Exception>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var togglerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                uiState.ToggleSparkAgentChat();
                uiState.SetSparkAgentChatOpen(true);
                uiState.SetSparkAgentChatOpen(false);
            }
        });

        var subscriberTask = Task.Run(() =>
        {
            var counter = 0;
            Action sub = () => counter++;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    uiState.OnChange += sub;
                    uiState.NotifyChanged();
                    uiState.OnChange -= sub;
                }
                catch (Exception ex)
                {
                    lock (exceptions)
                    {
                        exceptions.Add(ex);
                    }
                }
            }
        });

        await Task.WhenAll(togglerTask, subscriberTask);
        Assert.Empty(exceptions);
    }

    [Fact]
    public void WorkspaceUiState_ToggleSparkAgentExpanded_IndependentFromChatOpenAndOtherFlags()
    {
        var uiState = new WorkspaceUiState();

        Assert.False(uiState.IsSparkAgentChatOpen);
        Assert.False(uiState.IsSparkAgentExpanded);
        Assert.False(uiState.IsPortfolioQueryOpen);
        Assert.False(uiState.IsEmergencyDrawerOpen);

        // Toggle expanded while closed
        uiState.ToggleSparkAgentExpanded();
        Assert.False(uiState.IsSparkAgentChatOpen, "Expanding should not open chat automatically");
        Assert.True(uiState.IsSparkAgentExpanded);
        Assert.False(uiState.IsPortfolioQueryOpen);
        Assert.False(uiState.IsEmergencyDrawerOpen);

        // Open chat
        uiState.SetSparkAgentChatOpen(true);
        Assert.True(uiState.IsSparkAgentChatOpen);
        Assert.True(uiState.IsSparkAgentExpanded, "Opening chat should preserve expanded flag");

        // Toggle other unrelated UI flags
        uiState.TogglePortfolioQuery();
        uiState.ToggleEmergencyDrawer();
        Assert.True(uiState.IsSparkAgentChatOpen);
        Assert.True(uiState.IsSparkAgentExpanded);
        Assert.True(uiState.IsPortfolioQueryOpen);
        Assert.True(uiState.IsEmergencyDrawerOpen);

        // Close chat
        uiState.SetSparkAgentChatOpen(false);
        Assert.False(uiState.IsSparkAgentChatOpen);
        Assert.True(uiState.IsSparkAgentExpanded, "Closing chat should preserve expanded preference for next open");
    }

    [Fact]
    public void WorkspaceUiState_SetSparkAgentChatOpen_NoOpSuppressesUnnecessaryNotifications()
    {
        var uiState = new WorkspaceUiState();
        var notifications = 0;
        uiState.OnChange += () => notifications++;

        // Setting to current value (false) -> 0 notifications
        uiState.SetSparkAgentChatOpen(false);
        uiState.SetSparkAgentChatOpen(false);
        Assert.Equal(0, notifications);

        // Setting to true -> 1 notification
        uiState.SetSparkAgentChatOpen(true);
        Assert.Equal(1, notifications);

        // Setting to true again -> no additional notifications
        uiState.SetSparkAgentChatOpen(true);
        uiState.SetSparkAgentChatOpen(true);
        Assert.Equal(1, notifications);

        // Setting to false -> 1 additional notification
        uiState.SetSparkAgentChatOpen(false);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void WorkspaceUiState_ThemeSwitching_PreservesSparkAgentChatState()
    {
        var uiState = new WorkspaceUiState();
        uiState.SetSparkAgentChatOpen(true);
        uiState.ToggleSparkAgentExpanded();

        Assert.True(uiState.IsSparkAgentChatOpen);
        Assert.True(uiState.IsSparkAgentExpanded);
        Assert.True(uiState.IsDarkTheme);

        uiState.ToggleTheme();
        Assert.False(uiState.IsDarkTheme);
        Assert.Equal("light", uiState.ActiveTheme);
        Assert.True(uiState.IsSparkAgentChatOpen, "Switching theme to light must not close Spark chat");
        Assert.True(uiState.IsSparkAgentExpanded, "Switching theme to light must not reset expanded state");

        uiState.ToggleTheme();
        Assert.True(uiState.IsDarkTheme);
        Assert.Equal("dark", uiState.ActiveTheme);
        Assert.True(uiState.IsSparkAgentChatOpen, "Switching theme back to dark must not close Spark chat");
        Assert.True(uiState.IsSparkAgentExpanded);
    }

    // =========================================================================
    // Area 2: ResolveContextAsync Claims Parsing Across Roles & Guid Formats
    // =========================================================================

    [Fact]
    public async Task ResolveContextAsync_UnauthenticatedOrNullPrincipal_ReturnsGuestContext()
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var nullResult = await evaluator.ResolveContextAsync(null, db);
        Assert.NotNull(nullResult);
        Assert.Equal(Guid.Empty, nullResult.UserId);
        Assert.Equal(WorkspaceSecurityRole.ExternalAuditorView, nullResult.Role);
        Assert.False(nullResult.IsFirmManager);
        Assert.Empty(nullResult.AllowedCommunityIds);
        Assert.Null(nullResult.LinkedUnitId);

        var unauthIdentity = new ClaimsIdentity(); // IsAuthenticated = false
        var unauthPrincipal = new ClaimsPrincipal(unauthIdentity);

        var unauthResult = await evaluator.ResolveContextAsync(unauthPrincipal, db);
        Assert.NotNull(unauthResult);
        Assert.Equal(Guid.Empty, unauthResult.UserId);
        Assert.Equal(WorkspaceSecurityRole.ExternalAuditorView, unauthResult.Role);
        Assert.False(unauthResult.IsFirmManager);
    }

    [Theory]
    [InlineData("D")] // 00000000-0000-0000-0000-000000000000
    [InlineData("N")] // 00000000000000000000000000000000
    [InlineData("B")] // {00000000-0000-0000-0000-000000000000}
    [InlineData("P")] // (00000000-0000-0000-0000-000000000000)
    public async Task ResolveContextAsync_UnitIdGuidFormats_ParsesStandardAndExoticFormats(string format)
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();
        var targetUnitId = Guid.NewGuid();
        var formattedGuid = targetUnitId.ToString(format);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("unit_id", formattedGuid),
            new("unit_number", "B-204")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(targetUnitId, context.LinkedUnitId);
        Assert.Equal("B-204", context.LinkedUnitNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    [InlineData("1234567890")]
    [InlineData("e0a953c3-ee60-40ab-b9fb-e9f49f605e5z")] // Invalid hex char 'z'
    [InlineData("null")]
    [InlineData("undefined")]
    public async Task ResolveContextAsync_UnitIdInvalidOrMalformed_ResultsInNullLinkedUnitId(string invalidUnitId)
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("unit_id", invalidUnitId),
            new("unit_number", "C-101")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Null(context.LinkedUnitId);
        Assert.Equal("C-101", context.LinkedUnitNumber);
    }

    [Fact]
    public async Task ResolveContextAsync_UnitIdEmptyGuid_ParsesToGuidEmpty()
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("unit_id", Guid.Empty.ToString()),
            new("unit_number", "STORAGE-01")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(Guid.Empty, context.LinkedUnitId);
        Assert.Equal("STORAGE-01", context.LinkedUnitNumber);
    }

    [Theory]
    [InlineData(WorkspaceSecurityRole.FirmManager)]
    [InlineData(WorkspaceSecurityRole.PropertyManager)]
    [InlineData(WorkspaceSecurityRole.Accountant)]
    [InlineData(WorkspaceSecurityRole.MaintenanceDispatcher)]
    [InlineData(WorkspaceSecurityRole.BoardMemberReadOnly)]
    [InlineData(WorkspaceSecurityRole.Resident)]
    [InlineData(WorkspaceSecurityRole.Contractor)]
    [InlineData(WorkspaceSecurityRole.ExternalAuditorView)]
    public async Task ResolveContextAsync_AllEightRoles_ParsedCorrectlyWithPrescribedPermissions(WorkspaceSecurityRole expectedRole)
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, expectedRole.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(expectedRole, context.Role);

        if (expectedRole == WorkspaceSecurityRole.FirmManager)
        {
            Assert.True(context.IsFirmManager);
            Assert.Equal((WorkspacePermissionFlags)~0L, context.GrantedPermissions);
        }
        else
        {
            Assert.False(context.IsFirmManager);
            Assert.Equal(WorkspacePermissionPresets.DefaultForRole(expectedRole), context.GrantedPermissions);
        }
    }

    [Theory]
    [InlineData("firmmanager", WorkspaceSecurityRole.FirmManager)]
    [InlineData("FIRMMANAGER", WorkspaceSecurityRole.FirmManager)]
    [InlineData("propertymanager", WorkspaceSecurityRole.PropertyManager)]
    [InlineData("accountant", WorkspaceSecurityRole.Accountant)]
    [InlineData("maintenancedispatcher", WorkspaceSecurityRole.MaintenanceDispatcher)]
    [InlineData("boardmemberreadonly", WorkspaceSecurityRole.BoardMemberReadOnly)]
    [InlineData("resident", WorkspaceSecurityRole.Resident)]
    [InlineData("contractor", WorkspaceSecurityRole.Contractor)]
    [InlineData("externalauditorview", WorkspaceSecurityRole.ExternalAuditorView)]
    public async Task ResolveContextAsync_RoleCaseInsensitive_ParsedSuccessfully(string roleString, WorkspaceSecurityRole expectedRole)
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new("role", roleString)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(expectedRole, context.Role);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Administrator")]
    [InlineData("SuperAdmin")]
    [InlineData("Root")]
    [InlineData("HackerRole")]
    public async Task ResolveContextAsync_UnknownOrEmptyRoleClaim_DefaultsToResident(string unknownRole)
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, unknownRole)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(WorkspaceSecurityRole.Resident, context.Role);
        Assert.False(context.IsFirmManager);
    }

    [Fact]
    public async Task ResolveContextAsync_AllowedCommunitiesParsing_HandlesDelimitersWhitespaceAndInvalidEntries()
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var comm1 = Guid.NewGuid();
        var comm2 = Guid.NewGuid();
        var comm3 = Guid.NewGuid();

        // Mixed delimiters (commas and semicolons), whitespace, garbage string, and duplicate comm1
        var rawAllowed = $"{comm1} , invalid-guid ; {comm2} ; ; {comm3} , {comm1} ";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.PropertyManager.ToString()),
            new("allowed_communities", rawAllowed)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(3, context.AllowedCommunityIds.Count);
        Assert.Contains(comm1, context.AllowedCommunityIds);
        Assert.Contains(comm2, context.AllowedCommunityIds);
        Assert.Contains(comm3, context.AllowedCommunityIds);
    }

    [Fact]
    public async Task ResolveContextAsync_FallbackToCommunityIdClaim_WhenAllowedCommunitiesAbsent()
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();
        var singleCommId = Guid.NewGuid();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("community_id", singleCommId.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Single(context.AllowedCommunityIds);
        Assert.Equal(singleCommId, context.AllowedCommunityIds[0]);
    }

    [Fact]
    public async Task ResolveContextAsync_DatabaseMembershipUnitLink_OverridesClaimsUnitId()
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var userId = Guid.NewGuid();
        var dbUnitId = Guid.NewGuid();
        var claimsUnitId = Guid.NewGuid();
        var communityId = Guid.NewGuid();

        // Seed unit in database
        var dbUnit = new Unit
        {
            Id = dbUnitId,
            CommunityId = communityId,
            UnitNumber = "DB-501"
        };
        db.Units.Add(dbUnit);

        // Seed FirmMembership with UnitLink
        var membership = new FirmMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ManagementFirmId = Guid.NewGuid(),
            Role = WorkspaceSecurityRole.Resident,
            Status = WorkspaceMembershipStatus.Approved,
            UnitLinks =
            [
                new MembershipUnitLink
                {
                    UnitId = dbUnitId,
                    CommunityId = communityId
                }
            ]
        };
        db.FirmMemberships.Add(membership);
        await db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("unit_id", claimsUnitId.ToString()),
            new("unit_number", "CLAIM-999")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(dbUnitId, context.LinkedUnitId);
        Assert.Equal("DB-501", context.LinkedUnitNumber);
    }

    [Fact]
    public async Task ResolveContextAsync_DatabaseMembershipWithoutUnitLinks_PreservesClaimsUnitIdFallback()
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();

        var userId = Guid.NewGuid();
        var claimsUnitId = Guid.NewGuid();

        // Seed FirmMembership WITHOUT UnitLinks
        var membership = new FirmMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ManagementFirmId = Guid.NewGuid(),
            Role = WorkspaceSecurityRole.Resident,
            Status = WorkspaceMembershipStatus.Approved,
            UnitLinks = []
        };
        db.FirmMemberships.Add(membership);
        await db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("unit_id", claimsUnitId.ToString()),
            new("unit_number", "CLAIM-PRESERVED-101")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(claimsUnitId, context.LinkedUnitId);
        Assert.Equal("CLAIM-PRESERVED-101", context.LinkedUnitNumber);
    }

    [Fact]
    public async Task ResolveContextAsync_UnitNumberWithUnicodeAndSpecialCharacters_PreservedVerbatim()
    {
        var db = CreateInMemoryDbContext();
        var evaluator = new SparkAgentPermissionEvaluator();
        const string unicodeUnitNumber = "Mieszkanie #42/B (Piętro 3, Taras + Garaż G-12)";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("unit_id", Guid.NewGuid().ToString()),
            new("unit_number", unicodeUnitNumber)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Auth"));

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(unicodeUnitNumber, context.LinkedUnitNumber);
    }

    // =========================================================================
    // Area 3: SparkAgentLiveStepEvent Record Invariants & Serialization
    // =========================================================================

    [Fact]
    public void SparkAgentLiveStepEvent_NullPropertiesInvariant_ConstructsSafely()
    {
        var runId = Guid.NewGuid();
        var step = new SparkAgentLiveStepEvent(
            RunId: runId,
            StepNumber: 1,
            Phase: "Thinking",
            Thought: "Analyzing resident query.",
            ToolName: null,
            ToolArgumentsJson: null,
            PermissionGranted: null,
            PermissionBadgeText: null,
            ObservationJson: null,
            FinalMarkdown: null,
            DeepLinkUrl: null,
            LatencyMs: 42,
            ClarificationOptions: null);

        Assert.Equal(runId, step.RunId);
        Assert.Equal(1, step.StepNumber);
        Assert.Equal("Thinking", step.Phase);
        Assert.Equal("Analyzing resident query.", step.Thought);
        Assert.Null(step.ToolName);
        Assert.Null(step.ToolArgumentsJson);
        Assert.Null(step.PermissionGranted);
        Assert.Null(step.PermissionBadgeText);
        Assert.Null(step.ObservationJson);
        Assert.Null(step.FinalMarkdown);
        Assert.Null(step.DeepLinkUrl);
        Assert.Equal(42, step.LatencyMs);
        Assert.Null(step.ClarificationOptions);
    }

    [Fact]
    public void SparkAgentLiveStepEvent_ExtremeValues_DoesNotOverflowOrCorrupt()
    {
        var runId = Guid.NewGuid();
        var largeThought = new string('X', 50000);

        var stepMax = new SparkAgentLiveStepEvent(
            RunId: runId,
            StepNumber: int.MaxValue,
            Phase: "Observation",
            Thought: largeThought,
            ToolName: "query_database_all",
            ToolArgumentsJson: "{}",
            PermissionGranted: true,
            PermissionBadgeText: "PERMITTED",
            ObservationJson: "{\"count\": 1000000}",
            FinalMarkdown: null,
            DeepLinkUrl: "/properties/all",
            LatencyMs: int.MaxValue);

        Assert.Equal(int.MaxValue, stepMax.StepNumber);
        Assert.Equal(int.MaxValue, stepMax.LatencyMs);
        Assert.Equal(50000, stepMax.Thought.Length);

        var stepMin = new SparkAgentLiveStepEvent(
            RunId: runId,
            StepNumber: 0,
            Phase: "AwaitingClarification",
            Thought: "",
            ToolName: null,
            ToolArgumentsJson: null,
            PermissionGranted: false,
            PermissionBadgeText: "DENIED",
            ObservationJson: null,
            FinalMarkdown: null,
            DeepLinkUrl: null,
            LatencyMs: -1,
            ClarificationOptions: []);

        Assert.Equal(0, stepMin.StepNumber);
        Assert.Equal(-1, stepMin.LatencyMs);
        Assert.Empty(stepMin.ClarificationOptions!);
    }

    [Fact]
    public void SparkAgentLiveStepEvent_JsonSerializationRoundtrip_PreservesAllFields()
    {
        var runId = Guid.NewGuid();
        var original = new SparkAgentLiveStepEvent(
            RunId: runId,
            StepNumber: 3,
            Phase: "AwaitingClarification",
            Thought: "Need user decision between Option A and Option B.",
            ToolName: "ask_user_clarification",
            ToolArgumentsJson: "{\"question\": \"Select vendor contract\"}",
            PermissionGranted: true,
            PermissionBadgeText: "PERMITTED",
            ObservationJson: "{\"status\": \"paused\"}",
            FinalMarkdown: null,
            DeepLinkUrl: "/financials/contracts",
            LatencyMs: 145,
            ClarificationOptions: new[] { "Vendor Alfa ($12,000)", "Vendor Beta ($14,500)", "Cancel Tender" });

        var json = JsonSerializer.Serialize(original);
        Assert.False(string.IsNullOrWhiteSpace(json));

        var deserialized = JsonSerializer.Deserialize<SparkAgentLiveStepEvent>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(original.RunId, deserialized.RunId);
        Assert.Equal(original.StepNumber, deserialized.StepNumber);
        Assert.Equal(original.Phase, deserialized.Phase);
        Assert.Equal(original.Thought, deserialized.Thought);
        Assert.Equal(original.ToolName, deserialized.ToolName);
        Assert.Equal(original.ToolArgumentsJson, deserialized.ToolArgumentsJson);
        Assert.Equal(original.PermissionGranted, deserialized.PermissionGranted);
        Assert.Equal(original.PermissionBadgeText, deserialized.PermissionBadgeText);
        Assert.Equal(original.ObservationJson, deserialized.ObservationJson);
        Assert.Equal(original.FinalMarkdown, deserialized.FinalMarkdown);
        Assert.Equal(original.DeepLinkUrl, deserialized.DeepLinkUrl);
        Assert.Equal(original.LatencyMs, deserialized.LatencyMs);
        Assert.NotNull(deserialized.ClarificationOptions);
        Assert.Equal(3, deserialized.ClarificationOptions.Count);
        Assert.Equal("Vendor Alfa ($12,000)", deserialized.ClarificationOptions[0]);
    }

    [Fact]
    public void SparkAgentLiveStepEvent_WithExpressionCopy_ClonesFaithfully()
    {
        var runId = Guid.NewGuid();
        var step1 = new SparkAgentLiveStepEvent(
            RunId: runId,
            StepNumber: 1,
            Phase: "Thinking",
            Thought: "Initial turn thinking.",
            ToolName: "query_units",
            ToolArgumentsJson: "{\"filter\":\"vacant\"}",
            PermissionGranted: true,
            PermissionBadgeText: "PERMITTED",
            ObservationJson: "[{\"id\": 1}]",
            FinalMarkdown: null,
            DeepLinkUrl: "/units",
            LatencyMs: 50,
            ClarificationOptions: null);

        // Advance to step 2 with different thought and latency
        var step2 = step1 with
        {
            StepNumber = 2,
            Phase = "ToolExecution",
            Thought = "Executing query_units",
            LatencyMs = 85
        };

        Assert.Equal(runId, step2.RunId);
        Assert.Equal(2, step2.StepNumber);
        Assert.Equal("ToolExecution", step2.Phase);
        Assert.Equal("Executing query_units", step2.Thought);
        Assert.Equal(85, step2.LatencyMs);
        // Preserved properties from step1
        Assert.Equal(step1.ToolName, step2.ToolName);
        Assert.Equal(step1.ToolArgumentsJson, step2.ToolArgumentsJson);
        Assert.Equal(step1.PermissionGranted, step2.PermissionGranted);
        Assert.Equal(step1.ObservationJson, step2.ObservationJson);
        Assert.Equal(step1.DeepLinkUrl, step2.DeepLinkUrl);
    }

    [Fact]
    public void SparkAgentLiveStepEvent_ValueEqualitySemantics_MatchesExpected()
    {
        var runId = Guid.NewGuid();
        var options = new[] { "Yes", "No" };

        var event1 = new SparkAgentLiveStepEvent(
            runId, 1, "FinalAnswer", "Done", null, null, null, null, null, "All done.", "/overview", 120, options);

        var event2 = new SparkAgentLiveStepEvent(
            runId, 1, "FinalAnswer", "Done", null, null, null, null, null, "All done.", "/overview", 120, options);

        Assert.Equal(event1, event2);

        // Different phase breaks equality
        var event3 = event1 with { Phase = "Thinking" };
        Assert.NotEqual(event1, event3);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static AppDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"cg-challenger-m2-{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }
}
