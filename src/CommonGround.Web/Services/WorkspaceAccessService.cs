using System.Security.Claims;
using CommonGround.Application.Services;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Web.Auth;
using Microsoft.AspNetCore.Components.Authorization;

namespace CommonGround.Web.Services;

public sealed class WorkspaceAccessService(
    AuthenticationStateProvider authState,
    CommonGroundWorkspaceService workspace)
{
    private readonly AuthenticationStateProvider _authState = authState;
    private readonly CommonGroundWorkspaceService _workspace = workspace;

    public async Task<ClaimsPrincipal> GetUserAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        return state.User;
    }

    public async Task<Guid?> GetAccountIdAsync()
    {
        var user = await GetUserAsync();
        var raw = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public async Task<WorkspaceSecurityRole> GetRoleAsync()
    {
        var user = await GetUserAsync();
        return WorkspaceClaims.GetRole(user);
    }

    public async Task<Guid?> GetFirmIdAsync()
    {
        var user = await GetUserAsync();
        return WorkspaceClaims.TryGetFirmId(user, out var id) ? id : null;
    }

    public async Task<IReadOnlyList<Guid>> GetAllowedCommunityIdsAsync()
    {
        var user = await GetUserAsync();
        return WorkspaceClaims.GetAllowedCommunityIds(user);
    }

    public async Task<string?> GetLinkedUnitAsync()
    {
        var user = await GetUserAsync();
        return user.FindFirst(WorkspaceClaimTypes.LinkedUnit)?.Value;
    }

    public async Task<bool> CanAccessCommunityAsync(Guid communityId)
    {
        var user = await GetUserAsync();
        if (!(user.Identity?.IsAuthenticated ?? false)) return false;
        if (!WorkspaceClaims.TryGetFirmId(user, out var firmId)) return false;
        if (WorkspaceClaims.IsFirmManager(user))
        {
            var owned = await _workspace.GetCommunityAsync(communityId);
            return owned is not null && owned.ManagementFirmId == firmId;
        }
        var allowed = WorkspaceClaims.GetAllowedCommunityIds(user);
        if (allowed.Contains(communityId)) return true;
        if (WorkspaceClaims.TryGetCommunityId(user, out var primary) && primary == communityId) return true;
        return false;
    }

    public async Task<WorkspaceUserAccount?> GetAccountAsync()
    {
        var accountId = await GetAccountIdAsync();
        if (accountId is null) return null;
        return await _workspace.GetWorkspaceAccountAsync(accountId.Value);
    }

    public async Task EnsureCommunityAccessAsync(Guid communityId)
    {
        if (!await CanAccessCommunityAsync(communityId))
            throw new UnauthorizedAccessException("Access to this property is denied for the current account.");
    }

    public async Task<IReadOnlyList<Community>> GetAllowedCommunitiesAsync()
    {
        var user = await GetUserAsync();
        if (!(user.Identity?.IsAuthenticated ?? false)) return [];
        if (!WorkspaceClaims.TryGetFirmId(user, out var firmId)) return [];
        if (WorkspaceClaims.IsFirmManager(user))
            return await _workspace.GetFirmCommunitiesAsync(firmId);
        var allowed = WorkspaceClaims.GetAllowedCommunityIds(user);
        if (allowed.Count == 0 && WorkspaceClaims.TryGetCommunityId(user, out var primary) && primary != Guid.Empty)
            allowed = [primary];
        return await _workspace.GetAllowedFirmCommunitiesAsync(firmId, allowed);
    }

    public async Task<ManagementFirm?> GetFirmAsync()
    {
        var firmId = await GetFirmIdAsync();
        if (firmId is null) return null;
        return await _workspace.GetManagementFirmAsync(firmId.Value);
    }
}
