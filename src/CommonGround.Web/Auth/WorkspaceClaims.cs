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
}

public static class WorkspaceClaims
{
    public static ClaimsPrincipal CreatePrincipal(
        Guid accountId,
        string fullName,
        string email,
        WorkspaceSecurityRole role,
        Guid? firmId,
        string firmCode,
        Guid communityId,
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
        if (communityId != Guid.Empty)
            claims.Add(new Claim(WorkspaceClaimTypes.CommunityId, communityId.ToString()));
        if (allowedCommunities.Count > 0)
            claims.Add(new Claim(WorkspaceClaimTypes.AllowedCommunities, string.Join(",", allowedCommunities)));
        if (!string.IsNullOrWhiteSpace(linkedUnitNumber))
            claims.Add(new Claim(WorkspaceClaimTypes.LinkedUnit, linkedUnitNumber.Trim()));
        var identity = new ClaimsIdentity(claims, "WorkspaceCookie");
        return new ClaimsPrincipal(identity);
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
}
