using System.Globalization;
using System.Security.Claims;
using CommonGround.Domain.Enums;

namespace CommonGround.Web.Auth;

public static class WorkspaceClaimTypes
{
    public const string FirmId = "firm_id";
    public const string FirmCode = "firm_code";
    public const string CommunityId = "community_id";
    public const string AllowedCommunities = "allowed_communities";
    public const string LinkedUnit = "unit_number";
    public const string AccountId = ClaimTypes.NameIdentifier;
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string ActiveMembershipId = "active_membership_id";
    public const string AccountKind = "account_kind";
    public const string MembershipId = "membership_id";
    public const string Permissions = "permissions";
    public const string EmailConfirmed = "email_confirmed";
    public const string ApprovalStatus = "approval_status";
}

public static class WorkspaceClaims
{
    public static ClaimsPrincipal CreatePrincipal(Guid userId, Guid? activeMembershipId, WorkspaceSecurityRole? role = null)
    {
        var claims = new List<Claim>
        {
            new(WorkspaceClaimTypes.UserId, userId.ToString()),
        };
        if (activeMembershipId.HasValue && activeMembershipId.Value != Guid.Empty)
            claims.Add(new Claim(WorkspaceClaimTypes.ActiveMembershipId, activeMembershipId.Value.ToString()));
        if (role.HasValue)
            claims.Add(new Claim(ClaimTypes.Role, role.Value.ToString()));
        var identity = new ClaimsIdentity(claims, "WorkspaceCookie");
        return new ClaimsPrincipal(identity);
    }

    public static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        var raw = user.FindFirstValue(WorkspaceClaimTypes.UserId);
        return Guid.TryParse(raw, out userId) && userId != Guid.Empty;
    }

    public static bool TryGetActiveMembershipId(ClaimsPrincipal user, out Guid membershipId)
    {
        membershipId = Guid.Empty;
        var raw = user.FindFirstValue(WorkspaceClaimTypes.ActiveMembershipId);
        return Guid.TryParse(raw, out membershipId) && membershipId != Guid.Empty;
    }

    public static Guid GetUserId(ClaimsPrincipal user)
        => TryGetUserId(user, out var id) ? id : Guid.Empty;

    public static Guid GetActiveMembershipId(ClaimsPrincipal user)
        => TryGetActiveMembershipId(user, out var id) ? id : Guid.Empty;

    [Obsolete("Legacy cookie shape. New sign-in issues only UserId + ActiveMembershipId; role and permissions come from the database.")]
    public static ClaimsPrincipal CreateLegacyPrincipal(
        Guid accountId,
        string fullName,
        string email,
        WorkspaceSecurityRole role,
        Guid? firmId,
        string firmCode,
        Guid? communityId,
        IReadOnlyList<Guid> allowedCommunities,
        string? linkedUnitNumber)
    {
        var claims = new List<Claim>
        {
            new(WorkspaceClaimTypes.AccountId, accountId.ToString()),
            new(ClaimTypes.Name, fullName ?? string.Empty),
            new(ClaimTypes.Email, email ?? string.Empty),
            new(ClaimTypes.Role, role.ToString()),
            new(WorkspaceClaimTypes.FirmCode, (firmCode ?? string.Empty).Trim().ToUpperInvariant()),
        };
        if (firmId.HasValue && firmId.Value != Guid.Empty)
            claims.Add(new Claim(WorkspaceClaimTypes.FirmId, firmId.Value.ToString()));
        if (communityId.HasValue && communityId.Value != Guid.Empty)
            claims.Add(new Claim(WorkspaceClaimTypes.CommunityId, communityId.Value.ToString()));
        if (allowedCommunities.Count > 0)
            claims.Add(new Claim(WorkspaceClaimTypes.AllowedCommunities, string.Join(",", allowedCommunities)));
        if (!string.IsNullOrWhiteSpace(linkedUnitNumber))
            claims.Add(new Claim(WorkspaceClaimTypes.LinkedUnit, linkedUnitNumber.Trim()));
        var identity = new ClaimsIdentity(claims, "WorkspaceCookie");
        return new ClaimsPrincipal(identity);
    }

    public static ClaimsPrincipal CreatePrincipal(
        Guid accountId,
        string fullName,
        string email,
        WorkspaceSecurityRole role,
        Guid? firmId,
        string firmCode,
        Guid? communityId,
        IReadOnlyList<Guid> allowedCommunities,
        string? linkedUnitNumber)
    {
#pragma warning disable CS0618
        var principal = CreateLegacyPrincipal(accountId, fullName, email, role, firmId, firmCode, communityId, allowedCommunities, linkedUnitNumber);
#pragma warning restore CS0618
        return principal;
    }

    public static ClaimsPrincipal CreatePrincipalWithMembership(
        Guid accountId,
        string fullName,
        string email,
        WorkspaceSecurityRole role,
        Guid? firmId,
        string firmCode,
        Guid? communityId,
        IReadOnlyList<Guid> allowedCommunities,
        string? linkedUnit,
        WorkspaceAccountKind accountKind,
        Guid? membershipId,
        WorkspacePermissionFlags permissions,
        bool emailConfirmed,
        WorkspaceMembershipStatus approvalStatus)
    {
#pragma warning disable CS0618
        var principal = CreateLegacyPrincipal(
            accountId,
            fullName,
            email,
            role,
            firmId,
            firmCode,
            communityId,
            allowedCommunities,
            linkedUnit);
#pragma warning restore CS0618
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(WorkspaceClaimTypes.AccountKind, accountKind.ToString()));
        if (membershipId.HasValue && membershipId.Value != Guid.Empty)
            identity.AddClaim(new Claim(WorkspaceClaimTypes.MembershipId, membershipId.Value.ToString()));
        identity.AddClaim(new Claim(
            WorkspaceClaimTypes.Permissions,
            ((long)permissions).ToString(CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(
            WorkspaceClaimTypes.EmailConfirmed,
            emailConfirmed ? "true" : "false"));
        identity.AddClaim(new Claim(WorkspaceClaimTypes.ApprovalStatus, approvalStatus.ToString()));
        return principal;
    }

    public static bool TryGetFirmId(ClaimsPrincipal user, out Guid firmId)
    {
        firmId = Guid.Empty;
        var raw = user.FindFirstValue(WorkspaceClaimTypes.FirmId);
        return Guid.TryParse(raw, out firmId) && firmId != Guid.Empty;
    }

    public static bool TryGetCommunityId(ClaimsPrincipal user, out Guid communityId)
    {
        communityId = Guid.Empty;
        var raw = user.FindFirstValue(WorkspaceClaimTypes.CommunityId);
        return Guid.TryParse(raw, out communityId) && communityId != Guid.Empty;
    }

    public static IReadOnlyList<Guid> GetAllowedCommunityIds(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(WorkspaceClaimTypes.AllowedCommunities);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .Distinct()
            .ToList();
    }

    public static WorkspaceSecurityRole GetRole(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<WorkspaceSecurityRole>(raw, out var role) ? role : WorkspaceSecurityRole.Resident;
    }

    public static bool IsInRole(ClaimsPrincipal user, params WorkspaceSecurityRole[] roles)
    {
        var current = GetRole(user);
        return roles.Contains(current);
    }

    public static bool IsManager(ClaimsPrincipal user)
        => IsInRole(user, WorkspaceSecurityRole.FirmManager, WorkspaceSecurityRole.PropertyManager);

    public static bool IsFirmManager(ClaimsPrincipal user)
        => IsInRole(user, WorkspaceSecurityRole.FirmManager);

    public static bool TryGetAccountKind(ClaimsPrincipal user, out WorkspaceAccountKind accountKind)
    {
        accountKind = default;
        var raw = user.FindFirstValue(WorkspaceClaimTypes.AccountKind);
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        foreach (var name in Enum.GetNames<WorkspaceAccountKind>())
        {
            if (string.Equals(name, raw.Trim(), StringComparison.Ordinal))
            {
                accountKind = Enum.Parse<WorkspaceAccountKind>(name);
                return true;
            }
        }
        return false;
    }

    public static bool TryGetMembershipId(ClaimsPrincipal user, out Guid membershipId)
    {
        membershipId = Guid.Empty;
        var raw = user.FindFirstValue(WorkspaceClaimTypes.MembershipId);
        if (!Guid.TryParse(raw, out membershipId) || membershipId == Guid.Empty)
        {
            membershipId = Guid.Empty;
            return TryGetActiveMembershipId(user, out membershipId);
        }
        return true;
    }

    public static WorkspacePermissionFlags GetPermissions(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(WorkspaceClaimTypes.Permissions);
        if (string.IsNullOrWhiteSpace(raw))
            return (WorkspacePermissionFlags)0;
        if (long.TryParse(
                raw.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
            return (WorkspacePermissionFlags)value;
        return (WorkspacePermissionFlags)0;
    }

    public static bool IsEmailConfirmed(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(WorkspaceClaimTypes.EmailConfirmed);
        return string.Equals(raw?.Trim(), "true", StringComparison.Ordinal);
    }

    public static WorkspaceMembershipStatus GetApprovalStatus(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(WorkspaceClaimTypes.ApprovalStatus);
        if (string.IsNullOrWhiteSpace(raw))
            return WorkspaceMembershipStatus.PendingEmailConfirmation;
        foreach (var name in Enum.GetNames<WorkspaceMembershipStatus>())
        {
            if (string.Equals(name, raw.Trim(), StringComparison.Ordinal))
                return Enum.Parse<WorkspaceMembershipStatus>(name);
        }
        return WorkspaceMembershipStatus.PendingEmailConfirmation;
    }

    public static bool HasPermission(ClaimsPrincipal user, WorkspacePermissionFlags flag)
    {
        if (flag == (WorkspacePermissionFlags)0)
            return true;
        var permissions = GetPermissions(user);
        return (permissions & flag) == flag;
    }
}
