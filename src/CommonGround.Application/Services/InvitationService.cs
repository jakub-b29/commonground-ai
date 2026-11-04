using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public sealed class InvitationService : IInvitationService
{
    private static readonly JsonSerializerOptions PropertyIdsJsonOptions = new();

    private readonly IAppDbContextFactory _dbFactory;

    public InvitationService(IAppDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    private async Task<IAppDbContext> CreateDbAsync(CancellationToken ct)
    {
        return await _dbFactory.CreateDbContextAsync(ct);
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes);
    }

    private static string NewPlainToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string SerializePropertyIds(IReadOnlyList<Guid> propertyIds)
    {
        var distinct = (propertyIds ?? []).Distinct().ToList();
        return JsonSerializer.Serialize(distinct, PropertyIdsJsonOptions);
    }

    private static List<Guid> ParsePropertyIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json, PropertyIdsJsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool CanInvite(FirmMembership actor)
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

    public async Task<InvitationCreationResult> CreateInvitationAsync(
        Guid firmId,
        Guid actorMembershipId,
        string recipientEmail,
        string recipientName,
        WorkspaceSecurityRole targetRole,
        WorkspacePermissionFlags grantedPermissions,
        bool requiresApproval,
        IReadOnlyList<Guid> propertyIds,
        Guid? targetUnitId,
        DateTime? expiresAtUtc,
        CancellationToken ct = default)
    {
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{recipientEmail ?? string.Empty} {recipientName ?? string.Empty} {targetUnitId}");
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Invitation blocked by security guard.");
        }

        var email = (recipientEmail ?? string.Empty).Trim();
        var name = (recipientName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Recipient email is required.");
        }

        await using var db = await CreateDbAsync(ct);
        var actor = await db.FirmMemberships.FirstOrDefaultAsync(m => m.Id == actorMembershipId, ct)
            ?? throw new InvalidOperationException("Actor membership not found.");
        if (actor.ManagementFirmId != firmId)
        {
            throw new InvalidOperationException("Actor belongs to a different firm.");
        }

        if (!CanInvite(actor))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or a Property Manager with invite rights may create invitations.");
        }

        if (actor.Role == WorkspaceSecurityRole.PropertyManager
            && (targetRole == WorkspaceSecurityRole.PropertyManager || targetRole == WorkspaceSecurityRole.FirmManager))
        {
            throw new UnauthorizedAccessException("Property Managers may only invite Resident and Contractor roles.");
        }

        if (targetRole == WorkspaceSecurityRole.FirmManager)
        {
            throw new InvalidOperationException("Firm Manager accounts are created only through firm registration.");
        }

        var distinctPropertyIds = (propertyIds ?? []).Distinct().ToList();
        if (distinctPropertyIds.Count > 0)
        {
            var validCount = await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == firmId && distinctPropertyIds.Contains(c.Id))
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
                        throw new UnauthorizedAccessException("Property Manager may only invite for assigned properties.");
                    }
                }
            }
        }

        if (targetUnitId.HasValue)
        {
            var unitFirmId = await db.Units.AsNoTracking()
                .Where(u => u.Id == targetUnitId.Value)
                .Join(db.Communities.AsNoTracking(),
                    u => u.CommunityId,
                    c => c.Id,
                    (u, c) => c.ManagementFirmId)
                .FirstOrDefaultAsync(ct);
            if (unitFirmId == Guid.Empty || unitFirmId != firmId)
            {
                throw new InvalidOperationException("Target unit does not belong to this firm.");
            }
        }

        var plainToken = NewPlainToken();
        var invitation = new Invitation
        {
            ManagementFirmId = firmId,
            TokenHash = ComputeSha256(plainToken),
            RecipientEmail = email,
            RecipientName = name,
            TargetRole = targetRole,
            GrantedPermissions = grantedPermissions == (WorkspacePermissionFlags)0
                ? WorkspacePermissionPresets.DefaultForRole(targetRole)
                : grantedPermissions,
            RequiresApproval = requiresApproval,
            PropertyIdsJson = SerializePropertyIds(distinctPropertyIds),
            TargetUnitId = targetUnitId,
            ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddDays(7),
            Status = InvitationStatus.Pending
        };
        db.Invitations.Add(invitation);

        var auditCommunityId = distinctPropertyIds.Count > 0
            ? distinctPropertyIds[0]
            : await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == firmId)
                .OrderBy(c => c.Name)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);

        await AppendAuditAsync(
            db,
            auditCommunityId,
            actor.Role,
            "INVITATION_CREATED",
            "Info",
            $"Invitation: {invitation.Id}",
            $"Invitation issued to {email} for role {targetRole}.",
            ct);

        await db.SaveChangesAsync(ct);
        return new InvitationCreationResult(invitation, plainToken);
    }

    public async Task<InvitationPreview?> GetInvitationPreviewAsync(
        string tokenHash,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return null;
        }

        var hash = tokenHash.Trim();
        await using var db = await CreateDbAsync(ct);
        var invitation = await db.Invitations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
        if (invitation is null)
        {
            return null;
        }

        var firm = await db.ManagementFirms.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == invitation.ManagementFirmId, ct);
        var email = invitation.RecipientEmail ?? string.Empty;
        var masked = email.Length <= 2
            ? "***"
            : string.Concat(email.AsSpan(0, 2), "***@", email.Contains('@', StringComparison.Ordinal)
                ? email[(email.IndexOf('@') + 1)..]
                : "***");
        var expired = invitation.IsExpired || invitation.ExpiresAtUtc < DateTime.UtcNow;
        return new InvitationPreview(
            invitation.Id,
            firm?.Name ?? string.Empty,
            firm?.FirmGroupCode ?? string.Empty,
            masked,
            invitation.TargetRole.ToString(),
            invitation.PropertyIdsJson,
            invitation.ExpiresAtUtc,
            expired,
            invitation.Status.ToString());
    }

    public async Task<FirmMembership> AcceptInvitationAsync(
        string plainToken,
        Guid userId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            throw new InvalidOperationException("Invitation token is required.");
        }

        var hash = ComputeSha256(plainToken.Trim());
        await using var db = await CreateDbAsync(ct);
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
            ?? throw new InvalidOperationException("Invitation token is invalid.");
        if (invitation.Status != InvitationStatus.Pending)
        {
            throw new InvalidOperationException("This invitation has already been used or revoked.");
        }

        if (invitation.ExpiresAtUtc < DateTime.UtcNow)
        {
            throw new InvalidOperationException("This invitation has expired. Ask a manager for a new invitation link.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException("User not found.");
        if (!string.Equals((user.Email ?? string.Empty).Trim(), (invitation.RecipientEmail ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invitation recipient email does not match this user.");
        }

        var existing = await db.FirmMemberships.FirstOrDefaultAsync(
            m => m.UserId == user.Id && m.ManagementFirmId == invitation.ManagementFirmId, ct);
        if (existing is not null
            && (existing.Status == WorkspaceMembershipStatus.PendingApproval
                || existing.Status == WorkspaceMembershipStatus.Approved))
        {
            throw new InvalidOperationException("Membership already exists for this firm.");
        }

        var propertyIds = ParsePropertyIds(invitation.PropertyIdsJson);
        Unit? unit = null;
        if (invitation.TargetUnitId.HasValue)
        {
            unit = await db.Units.FirstOrDefaultAsync(u => u.Id == invitation.TargetUnitId.Value, ct)
                ?? throw new InvalidOperationException("Target unit no longer exists.");
        }

        var membership = existing ?? new FirmMembership
        {
            UserId = user.Id,
            ManagementFirmId = invitation.ManagementFirmId
        };
        if (existing is null)
        {
            db.FirmMemberships.Add(membership);
        }

        membership.Role = invitation.TargetRole;
        membership.GrantedPermissions = invitation.GrantedPermissions == (WorkspacePermissionFlags)0
            ? WorkspacePermissionPresets.DefaultForRole(invitation.TargetRole)
            : invitation.GrantedPermissions;
        membership.Status = invitation.RequiresApproval
            ? WorkspaceMembershipStatus.PendingApproval
            : WorkspaceMembershipStatus.Approved;
        membership.DecidedAtUtc = invitation.RequiresApproval ? null : DateTime.UtcNow;

        await db.SaveChangesAsync(ct);

        foreach (var pid in propertyIds.Distinct())
        {
            var accessExists = await db.MembershipPropertyAccesses
                .AnyAsync(a => a.FirmMembershipId == membership.Id && a.CommunityId == pid, ct);
            if (!accessExists)
            {
                db.MembershipPropertyAccesses.Add(new MembershipPropertyAccess
                {
                    FirmMembershipId = membership.Id,
                    CommunityId = pid,
                    IsPrimaryManager = false
                });
            }
        }

        if (unit is not null)
        {
            var linkExists = await db.MembershipUnitLinks
                .AnyAsync(l => l.FirmMembershipId == membership.Id && l.UnitId == unit.Id, ct);
            if (!linkExists)
            {
                db.MembershipUnitLinks.Add(new MembershipUnitLink
                {
                    FirmMembershipId = membership.Id,
                    CommunityId = unit.CommunityId,
                    UnitId = unit.Id,
                    RelationshipKind = MembershipUnitRelationshipKind.Owner
                });
            }

            if (!propertyIds.Contains(unit.CommunityId))
            {
                var accessExists = await db.MembershipPropertyAccesses
                    .AnyAsync(a => a.FirmMembershipId == membership.Id && a.CommunityId == unit.CommunityId, ct);
                if (!accessExists)
                {
                    db.MembershipPropertyAccesses.Add(new MembershipPropertyAccess
                    {
                        FirmMembershipId = membership.Id,
                        CommunityId = unit.CommunityId,
                        IsPrimaryManager = false
                    });
                }
            }
        }

        invitation.Status = InvitationStatus.Accepted;

        var auditCommunityId = propertyIds.Count > 0
            ? propertyIds[0]
            : unit?.CommunityId ?? await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == invitation.ManagementFirmId)
                .OrderBy(c => c.Name)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(invitation.RecipientName ?? string.Empty);
        await AppendAuditAsync(
            db,
            auditCommunityId,
            invitation.TargetRole,
            scan.IsInjectionDetected ? "INVITATION_ACCEPTED_SANITIZED" : "INVITATION_ACCEPTED",
            "Info",
            $"Invitation: {invitation.Id}",
            $"Invitation accepted by {user.Email}.",
            ct);

        await db.SaveChangesAsync(ct);
        return membership;
    }

    public async Task<Invitation> RevokeInvitationAsync(
        Guid invitationId,
        Guid actorMembershipId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var actor = await db.FirmMemberships.FirstOrDefaultAsync(m => m.Id == actorMembershipId, ct)
            ?? throw new InvalidOperationException("Actor membership not found.");
        if (!CanInvite(actor))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or a Property Manager with invite rights may revoke invitations.");
        }

        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw new InvalidOperationException("Invitation not found.");
        if (actor.ManagementFirmId != invitation.ManagementFirmId)
        {
            throw new InvalidOperationException("Invitation belongs to a different firm.");
        }

        invitation.Status = InvitationStatus.Revoked;

        var propertyIds = ParsePropertyIds(invitation.PropertyIdsJson);
        var auditCommunityId = propertyIds.Count > 0
            ? propertyIds[0]
            : await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == invitation.ManagementFirmId)
                .OrderBy(c => c.Name)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{invitation.RecipientEmail ?? string.Empty} {invitation.RecipientName ?? string.Empty}");
        await AppendAuditAsync(
            db,
            auditCommunityId,
            actor.Role,
            scan.IsInjectionDetected ? "INVITATION_REVOKED_SANITIZED" : "INVITATION_REVOKED",
            "Warning",
            $"Invitation: {invitation.Id}",
            $"Invitation for {invitation.RecipientEmail} revoked.",
            ct);

        await db.SaveChangesAsync(ct);
        return invitation;
    }
}
