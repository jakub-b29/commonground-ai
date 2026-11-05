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
        return await LoadActiveMembershipAsync(null, ct);
    }

    private async Task<FirmMembership?> LoadActiveMembershipAsync(Guid? communityIdHint, CancellationToken ct = default)
    {
        var user = await GetUserAsync();
        if (!(user.Identity?.IsAuthenticated ?? false)) return null;
        if (!WorkspaceClaims.TryGetUserId(user, out var userId)) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (WorkspaceClaims.TryGetActiveMembershipId(user, out var activeId) && activeId != Guid.Empty)
        {
            var m = await db.FirmMemberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == activeId && m.UserId == userId, ct);
            if (m is not null)
            {
                if (communityIdHint.HasValue && communityIdHint.Value != Guid.Empty)
                {
                    var hintFirmId = await db.Communities.AsNoTracking()
                        .Where(c => c.Id == communityIdHint.Value)
                        .Select(c => c.ManagementFirmId)
                        .FirstOrDefaultAsync(ct);
                    if (hintFirmId.HasValue && hintFirmId.Value != Guid.Empty && m.ManagementFirmId != hintFirmId.Value)
                    {
                        // Claimed membership belongs to another firm; prefer a membership in the hint community firm.
                    }
                    else
                    {
                        return m;
                    }
                }
                else
                {
                    return m;
                }
            }
        }

        Guid? preferredFirmId = null;
        if (communityIdHint.HasValue && communityIdHint.Value != Guid.Empty)
        {
            preferredFirmId = await db.Communities.AsNoTracking()
                .Where(c => c.Id == communityIdHint.Value)
                .Select(c => c.ManagementFirmId)
                .FirstOrDefaultAsync(ct);
        }

        var approvedQuery = db.FirmMemberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == WorkspaceMembershipStatus.Approved);
        FirmMembership? fallback;
        if (preferredFirmId.HasValue && preferredFirmId.Value != Guid.Empty)
        {
            fallback = await approvedQuery
                .Where(m => m.ManagementFirmId == preferredFirmId.Value)
                .OrderByDescending(m => m.Role == WorkspaceSecurityRole.FirmManager)
                .ThenByDescending(m => m.Role == WorkspaceSecurityRole.PropertyManager)
                .FirstOrDefaultAsync(ct)
                ?? await approvedQuery
                    .OrderByDescending(m => m.ManagementFirmId == preferredFirmId.Value)
                    .ThenByDescending(m => m.Role == WorkspaceSecurityRole.FirmManager)
                    .FirstOrDefaultAsync(ct);
        }
        else
        {
            fallback = await approvedQuery
                .OrderByDescending(m => m.Role == WorkspaceSecurityRole.FirmManager)
                .FirstOrDefaultAsync(ct);
        }
        if (fallback is not null) return fallback;

        var account = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (account is not null)
        {
            return new FirmMembership
            {
                Id = Guid.Empty,
                UserId = account.Id,
                ManagementFirmId = account.ManagementFirmId ?? Guid.Empty,
                Role = account.Role,
                Status = WorkspaceMembershipStatus.Approved,
                GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(account.Role)
            };
        }

        var claimRole = WorkspaceClaims.GetRole(user);
        WorkspaceClaims.TryGetFirmId(user, out var claimFirmId);
        return new FirmMembership
        {
            Id = Guid.Empty,
            UserId = userId,
            ManagementFirmId = claimFirmId,
            Role = claimRole,
            Status = WorkspaceMembershipStatus.Approved,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(claimRole)
        };
    }

    private async Task<FirmMembership?> LoadApprovedMembershipAsync(Guid userId, Guid membershipId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (membershipId != Guid.Empty)
        {
            var m = await db.FirmMemberships.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == membershipId && m.UserId == userId && m.Status == WorkspaceMembershipStatus.Approved, ct);
            if (m is not null) return m;
        }
        return await LoadActiveMembershipAsync(ct);
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
        if (membership?.ManagementFirmId != null && membership.ManagementFirmId != Guid.Empty)
            return membership.ManagementFirmId;

        var user = await GetUserAsync();
        if (WorkspaceClaims.TryGetFirmId(user, out var claimFirmId) && claimFirmId != Guid.Empty)
            return claimFirmId;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var defaultFirm = await db.ManagementFirms.AsNoTracking().Select(f => f.Id).FirstOrDefaultAsync();
        return defaultFirm != Guid.Empty ? defaultFirm : null;
    }

    public async Task<IReadOnlyList<Guid>> GetAllowedCommunityIdsAsync()
    {
        return await GetAllowedCommunityIdsAsync(null);
    }

    public async Task<IReadOnlyList<Guid>> GetAllowedCommunityIdsAsync(Guid? communityIdHint)
    {
        var membership = await LoadActiveMembershipAsync(communityIdHint);
        if (membership is null || membership.Status != WorkspaceMembershipStatus.Approved) return [];
        if (membership.Id == Guid.Empty)
        {
            // Synthetic fallback membership has no access rows; FirmManager bypass is role-scoped elsewhere.
            return [];
        }
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
        var membership = await LoadActiveMembershipAsync(communityId);
        if (membership is null || membership.Status != WorkspaceMembershipStatus.Approved) return false;
        if (membership.Role == WorkspaceSecurityRole.FirmManager)
        {
            await using var firmDb = await _dbFactory.CreateDbContextAsync();
            var communityFirmId = await firmDb.Communities.AsNoTracking()
                .Where(c => c.Id == communityId)
                .Select(c => c.ManagementFirmId)
                .FirstOrDefaultAsync();
            if (!communityFirmId.HasValue || communityFirmId.Value == Guid.Empty || communityFirmId.Value == membership.ManagementFirmId)
            {
                return true;
            }
            // Same user may hold FirmManager in another firm; that role grants nothing here.
            // Fall through to explicit access check for the requested community firm.
            await using var crossDb = await _dbFactory.CreateDbContextAsync();
            var crossMembershipIds = await crossDb.FirmMemberships.AsNoTracking()
                .Where(m => m.UserId == membership.UserId && m.ManagementFirmId == communityFirmId.Value && m.Status == WorkspaceMembershipStatus.Approved)
                .Select(m => m.Id)
                .ToListAsync();
            if (crossMembershipIds.Count == 0) return false;
            return await crossDb.MembershipPropertyAccesses.AsNoTracking()
                .AnyAsync(p => crossMembershipIds.Contains(p.FirmMembershipId) && p.CommunityId == communityId);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.MembershipPropertyAccesses.AsNoTracking()
            .AnyAsync(p => p.FirmMembershipId == membership.Id && p.CommunityId == communityId);
    }

    public Task<bool> HasPermissionAsync(WorkspacePermissionFlags flag)
        => HasPermissionAsync(flag, default);

    public async Task<bool> HasPermissionAsync(WorkspacePermissionFlags flag, CancellationToken ct)
    {
        if (flag == (WorkspacePermissionFlags)0)
            return true;
        var membership = await LoadActiveMembershipAsync(ct);
        if (membership is null || membership.Status != WorkspaceMembershipStatus.Approved) return false;
        if (membership.Role == WorkspaceSecurityRole.FirmManager)
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

        var membership = await LoadActiveMembershipAsync(propertyId, ct);
        if (membership is null || membership.Status != WorkspaceMembershipStatus.Approved)
            throw new UnauthorizedAccessException("An approved firm membership is required to access this property.");

        // FirmManager can do EVERYTHING with properties in their own firm.
        if (membership.Role == WorkspaceSecurityRole.FirmManager)
        {
            await using var firmDb = await _dbFactory.CreateDbContextAsync(ct);
            var communityFirmId = await firmDb.Communities.AsNoTracking()
                .Where(c => c.Id == propertyId)
                .Select(c => c.ManagementFirmId)
                .FirstOrDefaultAsync(ct);
            if (!communityFirmId.HasValue || communityFirmId.Value == Guid.Empty || communityFirmId.Value == membership.ManagementFirmId)
            {
                return;
            }
            throw new UnauthorizedAccessException("Access to this property is denied for the current account.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hasRow = await db.MembershipPropertyAccesses.AsNoTracking()
            .AnyAsync(p => p.FirmMembershipId == membership.Id && p.CommunityId == propertyId, ct);
        if (!hasRow)
            throw new UnauthorizedAccessException("Access to this property is denied for the current account.");

        if (flag != (WorkspacePermissionFlags)0)
        {
            var grants = membership.GrantedPermissions;
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
            var firmId = membership.ManagementFirmId;
            var query = db.Communities.AsNoTracking().Where(c => c.ManagementFirmId == firmId);
            return await query.AsSplitQuery().OrderBy(c => c.Name).ToListAsync();
        }

        var allowedIds = await db.MembershipPropertyAccesses.AsNoTracking()
            .Where(p => p.FirmMembershipId == membership.Id)
            .Select(p => p.CommunityId)
            .Distinct()
            .ToListAsync();
        if (allowedIds.Count == 0) return [];
        return await db.Communities.AsNoTracking()
            .Where(c => allowedIds.Contains(c.Id))
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
