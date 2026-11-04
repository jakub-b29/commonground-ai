using System.Security.Claims;
using CommonGround.Application.Services;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.Persistence;
using CommonGround.Web.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CommonGround.UnitTests;

public sealed class SparkAgentUiStateAndContextTests
{
    [Fact]
    public void WorkspaceUiState_ToggleSparkAgentChat_TogglesFlagAndNotifies()
    {
        var uiState = new WorkspaceUiState();
        var notificationCount = 0;
        uiState.OnChange += () => notificationCount++;

        Assert.False(uiState.IsSparkAgentChatOpen);

        uiState.ToggleSparkAgentChat();
        Assert.True(uiState.IsSparkAgentChatOpen);
        Assert.Equal(1, notificationCount);

        uiState.ToggleSparkAgentChat();
        Assert.False(uiState.IsSparkAgentChatOpen);
        Assert.Equal(2, notificationCount);
    }

    [Fact]
    public void WorkspaceUiState_ToggleSparkAgentExpanded_TogglesFlagAndNotifies()
    {
        var uiState = new WorkspaceUiState();
        var notificationCount = 0;
        uiState.OnChange += () => notificationCount++;

        Assert.False(uiState.IsSparkAgentExpanded);

        uiState.ToggleSparkAgentExpanded();
        Assert.True(uiState.IsSparkAgentExpanded);
        Assert.Equal(1, notificationCount);

        uiState.ToggleSparkAgentExpanded();
        Assert.False(uiState.IsSparkAgentExpanded);
        Assert.Equal(2, notificationCount);
    }

    [Fact]
    public void WorkspaceUiState_SetSparkAgentChatOpen_SetsStateAndNotifiesOnlyWhenChanged()
    {
        var uiState = new WorkspaceUiState();
        var notificationCount = 0;
        uiState.OnChange += () => notificationCount++;

        uiState.SetSparkAgentChatOpen(true);
        Assert.True(uiState.IsSparkAgentChatOpen);
        Assert.Equal(1, notificationCount);

        // Setting to same value should not trigger another notification
        uiState.SetSparkAgentChatOpen(true);
        Assert.True(uiState.IsSparkAgentChatOpen);
        Assert.Equal(1, notificationCount);

        uiState.SetSparkAgentChatOpen(false);
        Assert.False(uiState.IsSparkAgentChatOpen);
        Assert.Equal(2, notificationCount);
    }

    [Fact]
    public async Task SparkAgentPermissionEvaluator_ResolveContextAsync_ExtractsUnitIdClaimFallback()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);

        var evaluator = new SparkAgentPermissionEvaluator();
        var targetUnitId = Guid.NewGuid();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, WorkspaceSecurityRole.Resident.ToString()),
            new("unit_id", targetUnitId.ToString()),
            new("unit_number", "A-304")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var context = await evaluator.ResolveContextAsync(principal, db);

        Assert.NotNull(context);
        Assert.Equal(targetUnitId, context.LinkedUnitId);
        Assert.Equal("A-304", context.LinkedUnitNumber);
        Assert.Equal(WorkspaceSecurityRole.Resident, context.Role);
    }
}

