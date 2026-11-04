using System.Globalization;
using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public sealed class MembershipService : IMembershipService
{
    private readonly IAppDbContextFactory _dbFactory;

    public MembershipService(IAppDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private async Task<IAppDbContext> CreateDbAsync(CancellationToken ct)
    {
        return await _dbFactory.CreateDbContextAsync(ct);
    }

    private static string NormalizeFirmCode(string code)
    {
        return (code ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static bool CanManageMembers(FirmMembership actor)
    {
        if (actor.Status != WorkspaceMembershipStatus.Approved)
        {
            return false;
        }

        if (actor.Role == WorkspaceSecurityRole.FirmManager)
        {
            return true;
        }

        return actor.Role == WorkspaceSecurityRole.PropertyManager
            && actor.GrantedPermissions.HasFlag(WorkspacePermissionFlags.InviteAndApproveMembers);
    }

    private static async Task<string> GetPreviousChainHashAsync(IAppDbContext db, Guid communityId, CancellationToken ct)
    {
        var hashes = await db.SecurityAuditEvents
            .AsNoTracking()
            .Where(e => e.CommunityId == communityId)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => e.EntrySha256Hash)
            .ToListAsync(ct);
        return hashes.Count == 0 ? "GENESIS-0000000000000000" : hashes[^1];
    }

    private static async Task AppendAuditAsync(
        IAppDbContext db,
        Guid communityId,
        WorkspaceSecurityRole actorRole,
        string category,
        string severity,
        string resource,
        string summary,
        CancellationToken ct)
    {
        var previous = await GetPreviousChainHashAsync(db, communityId, ct);
        var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(
            communityId, actorRole, category, severity, resource, summary, previous);
        db.SecurityAuditEvents.Add(ev);
    }

    private static async Task<User> GetOrMaterializeUserAsync(IAppDbContext db, Guid userId, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is not null)
        {
            return user;
        }

        var legacy = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (legacy is null)
        {
            throw new InvalidOperationException("User not found.");
        }

        var materialized = new User
        {
            Id = legacy.Id,
            FullName = legacy.FullName,
            Email = legacy.Email,
            Phone = legacy.Phone,
            PasswordSaltAndHash = legacy.PasswordSaltAndHash,
            EmailConfirmed = legacy.EmailConfirmed,
            EmailConfirmationTokenHash = legacy.EmailConfirmationTokenHash,
            Provider = legacy.Provider
        };
        db.Users.Add(materialized);
        await db.SaveChangesAsync(ct);
        return materialized;
    }

    public async Task<FirmMembership> RequestJoinByFirmCodeAsync(
        string firmCode,
        Guid userId,
        WorkspaceSecurityRole requestedRole,
        Guid? communityId,
        string? unitNumber,
        string? note,
        CancellationToken ct = default)
    {
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(note ?? string.Empty);
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Join request blocked by security guard.");
        }

        var code = NormalizeFirmCode(firmCode);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Firm group code is required.");
        }

        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.FirmGroupCode.ToUpper() == code, ct)
            ?? throw new InvalidOperationException("Firm group code is invalid.");

        var user = await GetOrMaterializeUserAsync(db, userId, ct);

        var existing = await db.FirmMemberships.FirstOrDefaultAsync(
            m => m.UserId == user.Id && m.ManagementFirmId == firm.Id, ct);
        if (existing is not null
            && (existing.Status == WorkspaceMembershipStatus.PendingApproval
                || existing.Status == WorkspaceMembershipStatus.Approved))
        {
            throw new InvalidOperationException("Membership already exists for this firm.");
        }

        if (communityId.HasValue)
        {
            var belongs = await db.Communities.AsNoTracking()
                .AnyAsync(c => c.Id == communityId.Value && c.ManagementFirmId == firm.Id, ct);
            if (!belongs)
            {
                throw new InvalidOperationException("Target property does not belong to this firm.");
            }
        }

        var membership = new FirmMembership
        {
            UserId = user.Id,
            ManagementFirmId = firm.Id,
            Role = requestedRole,
            Status = WorkspaceMembershipStatus.PendingApproval,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(requestedRole)
        };
        db.FirmMemberships.Add(membership);

        var joinRequest = new JoinRequest
        {
            ManagementFirmId = firm.Id,
            FirmMembershipId = membership.Id,
            RequestedRole = requestedRole,
            RequestedCommunityId = communityId,
            RequestedUnitNumber = (unitNumber ?? string.Empty).Trim(),
            ProofOrCompanyNote = (note ?? string.Empty).Trim(),
            Status = JoinRequestStatus.PendingApproval,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.JoinRequests.Add(joinRequest);

        var auditCommunityId = communityId
            ?? await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == firm.Id)
                .OrderBy(c => c.Name)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);

        await AppendAuditAsync(
            db,
            auditCommunityId,
            requestedRole,
            "MEMBERSHIP_JOIN_REQUESTED",
            "Info",
            $"FirmMembership: {membership.Id}",
            $"Join request by {user.Email} for role {requestedRole}.",
            ct);

        await db.SaveChangesAsync(ct);
        return membership;
    }

    public async Task<FirmMembership> ApproveMembershipAsync(
        Guid actorMembershipId,
        Guid membershipId,
        WorkspaceSecurityRole role,
        WorkspacePermissionFlags permissionFlags,
        IReadOnlyList<Guid> propertyIds,
        Guid? unitId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var actor = await db.FirmMemberships.FirstOrDefaultAsync(m => m.Id == actorMembershipId, ct)
            ?? throw new InvalidOperationException("Actor membership not found.");
        if (!CanManageMembers(actor))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or a Property Manager with invite rights may approve memberships.");
        }

        var target = await db.FirmMemberships.FirstOrDefaultAsync(m => m.Id == membershipId, ct)
            ?? throw new InvalidOperationException("Membership not found.");
        if (!string.Equals(actor.ManagementFirmId.ToString("D", CultureInfo.InvariantCulture), target.ManagementFirmId.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Membership belongs to a different firm.");
        }

        if (actor.Role == WorkspaceSecurityRole.PropertyManager
            && (role == WorkspaceSecurityRole.PropertyManager || role == WorkspaceSecurityRole.FirmManager))
        {
            throw new UnauthorizedAccessException("Property Managers may only approve Resident and Contractor memberships.");
        }

        var distinctPropertyIds = (propertyIds ?? []).Distinct().ToList();
        if (distinctPropertyIds.Count > 0)
        {
            var validCount = await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == target.ManagementFirmId && distinctPropertyIds.Contains(c.Id))
                .CountAsync(ct);
            if (validCount != distinctPropertyIds.Count)
            {
                throw new InvalidOperationException("One or more target properties do not belong to this firm.");
            }

            if (actor.Role == WorkspaceSecurityRole.PropertyManager)
            {
                var actorPropertyIds = await db.MembershipPropertyAccesses.AsNoTracking()
                    .Where(a => a.FirmMembershipId == actor.Id)
                    .Select(a => a.CommunityId)
                    .ToListAsync(ct);
                foreach (var pid in distinctPropertyIds)
                {
                    if (!actorPropertyIds.Contains(pid))
                    {
                        throw new UnauthorizedAccessException("Property Manager may only approve access for assigned properties.");
                    }
                }
            }
        }

        Unit? unit = null;
        if (unitId.HasValue)
        {
            unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId.Value, ct)
                ?? throw new InvalidOperationException("Unit not found.");
            var unitFirmId = await db.Communities.AsNoTracking()
                .Where(c => c.Id == unit.CommunityId)
                .Select(c => c.ManagementFirmId)
                .FirstOrDefaultAsync(ct);
            if (unitFirmId != target.ManagementFirmId)
            {
                throw new InvalidOperationException("Unit does not belong to this firm.");
            }
        }

        target.Role = role;
        target.GrantedPermissions = permissionFlags == (WorkspacePermissionFlags)0
            ? WorkspacePermissionPresets.DefaultForRole(role)
            : permissionFlags;
        target.Status = WorkspaceMembershipStatus.Approved;
        target.DecidedByMembershipId = actor.Id;
        target.DecidedAtUtc = DateTime.UtcNow;

        foreach (var pid in distinctPropertyIds)
        {
            var exists = await db.MembershipPropertyAccesses
                .AnyAsync(a => a.FirmMembershipId == target.Id && a.CommunityId == pid, ct);
            if (!exists)
            {
                db.MembershipPropertyAccesses.Add(new MembershipPropertyAccess
                {
                    FirmMembershipId = target.Id,
                    CommunityId = pid,
                    IsPrimaryManager = false
                });
            }
        }

        if (unit is not null)
        {
            var linkExists = await db.MembershipUnitLinks
                .AnyAsync(l => l.FirmMembershipId == target.Id && l.UnitId == unit.Id, ct);
            if (!linkExists)
            {
                db.MembershipUnitLinks.Add(new MembershipUnitLink
                {
                    FirmMembershipId = target.Id,
                    CommunityId = unit.CommunityId,
                    UnitId = unit.Id,
                    RelationshipKind = MembershipUnitRelationshipKind.Owner
                });
            }

            if (!distinctPropertyIds.Contains(unit.CommunityId))
            {
                var accessExists = await db.MembershipPropertyAccesses
                    .AnyAsync(a => a.FirmMembershipId == target.Id && a.CommunityId == unit.CommunityId, ct);
                if (!accessExists)
                {
                    db.MembershipPropertyAccesses.Add(new MembershipPropertyAccess
                    {
                        FirmMembershipId = target.Id,
                        CommunityId = unit.CommunityId,
                        IsPrimaryManager = false
                    });
                }
            }
        }

        var pendingRequest = await db.JoinRequests
            .Where(j => j.FirmMembershipId == target.Id && j.Status == JoinRequestStatus.PendingApproval)
            .ToListAsync(ct);
        foreach (var req in pendingRequest)
        {
            req.Status = JoinRequestStatus.Approved;
        }

        var auditCommunityId = distinctPropertyIds.Count > 0
            ? distinctPropertyIds[0]
            : unit?.CommunityId ?? Guid.Empty;
        if (auditCommunityId == Guid.Empty)
        {
            auditCommunityId = await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == target.ManagementFirmId)
                .OrderBy(c => c.Name)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
        }

        await AppendAuditAsync(
            db,
            auditCommunityId,
            actor.Role,
            "MEMBERSHIP_APPROVED",
            "Info",
            $"FirmMembership: {target.Id}",
            $"Membership approved for role {role} with {distinctPropertyIds.Count} properties.",
            ct);

        await db.SaveChangesAsync(ct);
        return target;
    }

    public async Task<FirmMembership> RevokeMembershipAsync(
        Guid actorMembershipId,
        Guid membershipId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var actor = await db.FirmMemberships.FirstOrDefaultAsync(m => m.Id == actorMembershipId, ct)
            ?? throw new InvalidOperationException("Actor membership not found.");
        if (!CanManageMembers(actor))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or a Property Manager with invite rights may revoke memberships.");
        }

        var target = await db.FirmMemberships.FirstOrDefaultAsync(m => m.Id == membershipId, ct)
            ?? throw new InvalidOperationException("Membership not found.");
        if (actor.ManagementFirmId != target.ManagementFirmId)
        {
            throw new InvalidOperationException("Membership belongs to a different firm.");
        }

        if (actor.Role == WorkspaceSecurityRole.PropertyManager
            && (target.Role == WorkspaceSecurityRole.PropertyManager || target.Role == WorkspaceSecurityRole.FirmManager))
        {
            throw new UnauthorizedAccessException("Property Managers may only revoke Resident and Contractor memberships.");
        }

        target.Status = WorkspaceMembershipStatus.Revoked;
        target.DecidedByMembershipId = actor.Id;
        target.DecidedAtUtc = DateTime.UtcNow;

        var pendingRequests = await db.JoinRequests
            .Where(j => j.FirmMembershipId == target.Id && j.Status == JoinRequestStatus.PendingApproval)
            .ToListAsync(ct);
        foreach (var req in pendingRequests)
        {
            req.Status = JoinRequestStatus.Rejected;
        }

        var auditCommunityId = await db.MembershipPropertyAccesses.AsNoTracking()
            .Where(a => a.FirmMembershipId == target.Id)
            .OrderBy(a => a.CommunityId)
            .Select(a => a.CommunityId)
            .FirstOrDefaultAsync(ct);
        if (auditCommunityId == Guid.Empty)
        {
            auditCommunityId = await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == target.ManagementFirmId)
                .OrderBy(c => c.Name)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
        }

        await AppendAuditAsync(
            db,
            auditCommunityId,
            actor.Role,
            "MEMBERSHIP_REVOKED",
            "Warning",
            $"FirmMembership: {target.Id}",
            "Membership revoked.",
            ct);

        await db.SaveChangesAsync(ct);
        return target;
    }

    public async Task<IReadOnlyList<FirmMembership>> GetPendingApprovalsAsync(
        Guid firmId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.FirmMemberships
            .AsNoTracking()
            .Where(m => m.ManagementFirmId == firmId && m.Status == WorkspaceMembershipStatus.PendingApproval)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FirmMembership>> GetPendingApprovalsAsync(
        Guid firmId,
        Guid actorMembershipId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var actor = await db.FirmMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == actorMembershipId, ct)
            ?? throw new InvalidOperationException("Actor membership not found.");
        if (actor.ManagementFirmId != firmId)
        {
            throw new InvalidOperationException("Actor belongs to a different firm.");
        }

        if (!CanManageMembers(actor))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or a Property Manager with invite rights may view pending approvals.");
        }

        return await db.FirmMemberships
            .AsNoTracking()
            .Where(m => m.ManagementFirmId == firmId && m.Status == WorkspaceMembershipStatus.PendingApproval)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);
    }
}
