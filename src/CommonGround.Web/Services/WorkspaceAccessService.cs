using System.Security.Claims;
using CommonGround.Application.Persistence;
using CommonGround.Application.Services;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.Persistence;
using CommonGround.Web.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Web.Services;

public sealed class WorkspaceAccessService(
    AuthenticationStateProvider authState,
    CommonGroundWorkspaceService workspace,
    IAppDbContextFactory dbFactory)
{
    private readonly AuthenticationStateProvider _authState = authState;
    private readonly CommonGroundWorkspaceService _workspace = workspace;
    private readonly IAppDbContextFactory _dbFactory = dbFactory;

    public async Task<ClaimsPrincipal> GetUserAsync()
    {
        var state = await _authState.GetAuthenticationStateAsync();
        return state.User;
    }

    public async Task<Guid?> GetAccountIdAsync()
    {
        var user = await GetUserAsync();
        return WorkspaceClaims.TryGetUserId(user, out var id) ? id : null;
    }

    public Task<Guid?> GetUserIdAsync() => GetAccountIdAsync();

    public async Task<Guid?> GetActiveMembershipIdAsync()
    {
        var user = await GetUserAsync();
        return WorkspaceClaims.TryGetActiveMembershipId(user, out var id) ? id : null;
    }

    private async Task<FirmMembership?> LoadActiveMembershipAsync(CancellationToken ct = default)
    {
        var user = await GetUserAsync();
        if (!(user.Identity?.IsAuthenticated ?? false)) return null;
        if (!WorkspaceClaims.TryGetActiveMembershipId(user, out var activeId)) return null;
        if (!WorkspaceClaims.TryGetUserId(user, out var userId)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.FirmMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == activeId && m.UserId == userId, ct);
    }

    private async Task<FirmMembership?> LoadApprovedMembershipAsync(Guid userId, Guid membershipId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.FirmMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == userId && m.Status == WorkspaceMembershipStatus.Approved, ct);
    }

    public async Task<WorkspaceSecurityRole?> GetRoleAsync()
    {
        var membership = await LoadActiveMembershipAsync();
        return membership?.Role;
    }

    Task<WorkspaceSecurityRole> GetRoleAsyncCompatShim()
        => throw new NotSupportedException("Use GetRoleAsync returning nullable role resolved from the database.");

    public async Task<Guid?> GetFirmIdAsync()
    {
        var membership = await LoadActiveMembershipAsync();
        return membership?.ManagementFirmId;
    }

    public async Task<IReadOnlyList<Guid>> GetAllowedCommunityIdsAsync()
    {
        var membership = await LoadActiveMembershipAsync();
        if (membership is null || membership.Status != WorkspaceMembershipStatus.Approved) return [];
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ids = await db.MembershipPropertyAccesses.AsNoTracking()
            .Where(p => p.FirmMembershipId == membership.Id)
            .Select(p => p.CommunityId)
            .Distinct()
            .ToListAsync();
        return ids;
    }

    public async Task<string?> GetLinkedUnitAsync()
    {
        var membership = await LoadActiveMembershipAsync();
        if (membership is null) return null;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var link = await db.MembershipUnitLinks.AsNoTracking()
            .Where(l => l.FirmMembershipId == membership.Id)
            .OrderBy(l => l.UnitId)
            .Select(l => l.UnitId)
            .FirstOrDefaultAsync();
        if (link == Guid.Empty) return null;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == link);
        return unit?.UnitNumber;
    }

    public async Task<bool> CanAccessCommunityAsync(Guid communityId)
    {
        var user = await GetUserAsync();
        if (!(user.Identity?.IsAuthenticated ?? false)) return false;
        if (!WorkspaceClaims.TryGetUserId(user, out var userId)) return false;
        if (!WorkspaceClaims.TryGetActiveMembershipId(user, out var membershipId)) return false;
        await using var db = await _dbFactory.CreateDbContextAsync();
        var membership = await db.FirmMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == userId && m.Status == WorkspaceMembershipStatus.Approved);
        if (membership is null) return false;
        if (membership.Role == WorkspaceSecurityRole.FirmManager)
        {
            var owned = await db.Communities.AsNoTracking()
                .AnyAsync(c => c.Id == communityId && c.ManagementFirmId == membership.ManagementFirmId);
            return owned;
        }

        return await db.MembershipPropertyAccesses.AsNoTracking()
            .AnyAsync(p => p.FirmMembershipId == membership.Id && p.CommunityId == communityId);
    }

    public Task<bool> HasPermissionAsync(WorkspacePermissionFlags flag)
        => HasPermissionAsync(flag, default);

    public async Task<bool> HasPermissionAsync(WorkspacePermissionFlags flag, CancellationToken ct)
    {
        if (flag == (WorkspacePermissionFlags)0)
            return true;
        var user = await GetUserAsync();
        if (!WorkspaceClaims.TryGetUserId(user, out var userId)) return false;
        if (!WorkspaceClaims.TryGetActiveMembershipId(user, out var membershipId)) return false;
        var membership = await LoadApprovedMembershipAsync(userId, membershipId, ct);
        if (membership is null) return false;
        if (membership.Role == WorkspaceSecurityRole.FirmManager && membership.Status == WorkspaceMembershipStatus.Approved)
            return true;
        return (membership.GrantedPermissions & flag) == flag;
    }

    public async Task RequirePropertyAccessAsync(Guid propertyId, WorkspacePermissionFlags flag, CancellationToken ct = default)
    {
        var user = await GetUserAsync();
        if (!(user.Identity?.IsAuthenticated ?? false))
            throw new UnauthorizedAccessException("Sign-in is required to access this property.");
        if (!WorkspaceClaims.TryGetUserId(user, out var userId))
            throw new UnauthorizedAccessException("Sign-in is required to access this property.");
        if (!WorkspaceClaims.TryGetActiveMembershipId(user, out var membershipId))
            throw new UnauthorizedAccessException("An approved firm membership is required to access this property.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var membership = await db.FirmMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == userId, ct);
        if (membership is null || membership.Status != WorkspaceMembershipStatus.Approved)
            throw new UnauthorizedAccessException("Access to this property is denied for the current account.");

        var hasRow = await db.MembershipPropertyAccesses.AsNoTracking()
            .AnyAsync(p => p.FirmMembershipId == membership.Id && p.CommunityId == propertyId, ct);
        var belongsToFirm = await db.Communities.AsNoTracking()
            .AnyAsync(c => c.Id == propertyId && c.ManagementFirmId == membership.ManagementFirmId, ct);
        var isFirmManagerWithScope = membership.Role == WorkspaceSecurityRole.FirmManager && belongsToFirm;
        if (!hasRow && !isFirmManagerWithScope)
            throw new UnauthorizedAccessException("Access to this property is denied for the current account.");

        if (flag != (WorkspacePermissionFlags)0)
        {
            var grants = membership.Role == WorkspaceSecurityRole.FirmManager
                ? WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.FirmManager)
                : membership.GrantedPermissions;
            if ((grants & flag) != flag)
                throw new UnauthorizedAccessException("The active membership does not grant this property action.");
        }
    }

    public async Task EnsureCommunityAccessAsync(Guid communityId)
    {
        if (!await CanAccessCommunityAsync(communityId))
            throw new UnauthorizedAccessException("Access to this property is denied for the current account.");
    }

    public async Task<IReadOnlyList<Community>> GetAllowedCommunitiesAsync()
    {
        var membership = await LoadActiveMembershipAsync();
        if (membership is null || membership.Status != WorkspaceMembershipStatus.Approved) return [];
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (membership.Role == WorkspaceSecurityRole.FirmManager)
        {
            return await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == membership.ManagementFirmId)
                .AsSplitQuery()
                .OrderBy(c => c.Name)
                .ToListAsync();
        }

        var allowedIds = await db.MembershipPropertyAccesses.AsNoTracking()
            .Where(p => p.FirmMembershipId == membership.Id)
            .Select(p => p.CommunityId)
            .Distinct()
            .ToListAsync();
        if (allowedIds.Count == 0) return [];
        return await db.Communities.AsNoTracking()
            .Where(c => c.ManagementFirmId == membership.ManagementFirmId && allowedIds.Contains(c.Id))
            .AsSplitQuery()
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    public async Task<ManagementFirm?> GetFirmAsync()
    {
        var firmId = await GetFirmIdAsync();
        if (firmId is null) return null;
        return await _workspace.GetManagementFirmAsync(firmId.Value);
    }

    public async Task<IReadOnlyList<WorkspaceAccountMembership>> GetUserMembershipsAsync()
    {
        var accountId = await GetAccountIdAsync();
        if (accountId is null) return [];
        return await _workspace.GetUserMembershipsAsync(accountId.Value);
    }

    public async Task<IReadOnlyList<WorkspaceAccountMembership>> GetApprovedMembershipsAsync()
    {
        var memberships = await GetUserMembershipsAsync();
        return memberships.Where(m => m.ApprovalStatus == WorkspaceMembershipStatus.Approved).ToList();
    }

    public async Task<WorkspaceUserAccount?> GetAccountAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (!WorkspaceClaims.TryGetUserId(await GetUserAsync(), out var userId)) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return null;
        return new WorkspaceUserAccount
        {
            Id = user.Id,
            FullName = user.FullName,
            Email = user.Email,
            Phone = user.Phone,
            Provider = user.Provider,
            PasswordSaltAndHash = user.PasswordSaltAndHash,
            EmailConfirmed = user.EmailConfirmed,
            EmailConfirmationTokenHash = user.EmailConfirmationTokenHash,
        };
    }
}
