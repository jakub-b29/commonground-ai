using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public sealed record SparkAgentPermissionContext(
    Guid UserId,
    string FullName,
    string Email,
    Guid? ActiveMembershipId,
    Guid? ManagementFirmId,
    WorkspaceSecurityRole Role,
    WorkspaceMembershipStatus MembershipStatus,
    WorkspacePermissionFlags GrantedPermissions,
    IReadOnlyList<Guid> AllowedCommunityIds,
    Guid? LinkedUnitId,
    string? LinkedUnitNumber,
    bool IsFirmManager,
    string Phone = "")
{
    public WorkspaceSecurityRole EffectiveRole => Role;

    public bool HasFlag(WorkspacePermissionFlags flag)
    {
        if (flag == 0) return true;
        if (IsFirmManager || Role == WorkspaceSecurityRole.FirmManager) return true;
        return (GrantedPermissions & flag) == flag;
    }

    public bool CanAccessCommunity(Guid communityId)
    {
        if (communityId == Guid.Empty) return true;
        if (IsFirmManager || Role == WorkspaceSecurityRole.FirmManager) return true;
        return AllowedCommunityIds.Contains(communityId);
    }

    public bool IsOwnUnit(Guid? unitId, string? unitNumber)
    {
        if (unitId.HasValue && unitId.Value != Guid.Empty && LinkedUnitId.HasValue)
        {
            return unitId.Value == LinkedUnitId.Value;
        }
        if (!string.IsNullOrWhiteSpace(unitNumber) && !string.IsNullOrWhiteSpace(LinkedUnitNumber))
        {
            return string.Equals(unitNumber.Trim(), LinkedUnitNumber.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}

public sealed record SparkAgentPermissionEvaluationResult(
    bool IsPermitted,
    WorkspacePermissionFlags RequiredFlags,
    WorkspacePermissionFlags MissingFlags,
    string? DenialReason = null);

public sealed record ToolObservationResult(
    bool IsSuccess,
    string ObservationJson,
    string Summary,
    string? DeepLinkUrl = null)
{
    public bool RequiresClarification { get; init; }
    public string? ClarificationQuestion { get; init; }
    public IReadOnlyList<string> ClarificationOptions { get; init; } = Array.Empty<string>();
}

public sealed class SparkAgentPermissionEvaluator
{
    public static int GetRolePrivilegeRank(WorkspaceSecurityRole role) => role switch
    {
        WorkspaceSecurityRole.FirmManager => 100,
        WorkspaceSecurityRole.PropertyManager => 90,
        WorkspaceSecurityRole.Accountant => 70,
        WorkspaceSecurityRole.MaintenanceDispatcher => 60,
        WorkspaceSecurityRole.BoardMemberReadOnly => 40,
        WorkspaceSecurityRole.Contractor => 30,
        WorkspaceSecurityRole.Resident => 20,
        WorkspaceSecurityRole.ExternalAuditorView => 10,
        _ => 0
    };

    public static async Task<Guid?> DetectTargetCommunityFromGoalAsync(
        IAppDbContext db,
        string? userGoal,
        Guid? fallbackCommunityId,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(userGoal))
        {
            var communities = await db.Communities
                .AsNoTracking()
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.CommunityAccountNumber,
                    c.StreetAddress,
                    c.City
                })
                .ToListAsync(ct);

            var goalLower = userGoal.ToLowerInvariant();
            foreach (var c in communities)
            {
                if (!string.IsNullOrWhiteSpace(c.CommunityAccountNumber)
                    && goalLower.Contains(c.CommunityAccountNumber.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    return c.Id;
                }
                if (!string.IsNullOrWhiteSpace(c.Name)
                    && goalLower.Contains(c.Name.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    return c.Id;
                }
                // Check distinctive 2-word prefix of community name (e.g. "Amber Park", "Oakridge Palms")
                if (!string.IsNullOrWhiteSpace(c.Name))
                {
                    var words = c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length >= 2)
                    {
                        var twoWordPrefix = $"{words[0]} {words[1]}".ToLowerInvariant();
                        if (twoWordPrefix.Length >= 6 && goalLower.Contains(twoWordPrefix, StringComparison.Ordinal))
                        {
                            return c.Id;
                        }
                    }
                }
                if (!string.IsNullOrWhiteSpace(c.StreetAddress)
                    && c.StreetAddress.Length >= 6
                    && goalLower.Contains(c.StreetAddress.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    return c.Id;
                }
            }
        }

        return fallbackCommunityId;
    }

    public async Task<SparkAgentPermissionContext> ResolveContextAsync(
        ClaimsPrincipal? principal,
        IAppDbContext db,
        CancellationToken ct = default,
        Guid? targetCommunityId = null,
        string? userGoal = null)
    {
        if (principal == null || !principal.Identity?.IsAuthenticated == true)
        {
            return CreateGuestContext();
        }

        Guid userId = Guid.Empty;
        var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("user_id")?.Value
            ?? principal.FindFirst("account_id")?.Value;
        if (!string.IsNullOrWhiteSpace(sub) && Guid.TryParse(sub, out var parsedUserId))
        {
            userId = parsedUserId;
        }

        Guid activeMembershipId = Guid.Empty;
        var memClaim = principal.FindFirst("active_membership_id")?.Value
            ?? principal.FindFirst("membership_id")?.Value;
        if (!string.IsNullOrWhiteSpace(memClaim) && Guid.TryParse(memClaim, out var parsedMemId))
        {
            activeMembershipId = parsedMemId;
        }

        var fullName = principal.FindFirst(ClaimTypes.Name)?.Value ?? "Workspace User";
        var email = principal.FindFirst(ClaimTypes.Email)?.Value ?? "user@commonground.local";

        WorkspaceSecurityRole role = WorkspaceSecurityRole.Resident;
        var roleClaim = principal.FindFirst(ClaimTypes.Role)?.Value ?? principal.FindFirst("role")?.Value;
        if (!string.IsNullOrWhiteSpace(roleClaim) && Enum.TryParse<WorkspaceSecurityRole>(roleClaim, true, out var parsedRole))
        {
            role = parsedRole;
        }

        Guid? firmId = null;
        var firmClaim = principal.FindFirst("firm_id")?.Value;
        if (!string.IsNullOrWhiteSpace(firmClaim) && Guid.TryParse(firmClaim, out var parsedFirmId))
        {
            firmId = parsedFirmId;
        }

        string? linkedUnitNumber = principal.FindFirst("unit_number")?.Value;
        var allowedCommunityIds = new List<Guid>();
        var allowedComClaim = principal.FindFirst("allowed_communities")?.Value
            ?? principal.FindFirst("community_id")?.Value;
        if (!string.IsNullOrWhiteSpace(allowedComClaim))
        {
            foreach (var part in allowedComClaim.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Guid.TryParse(part.Trim(), out var parsedComId) && !allowedCommunityIds.Contains(parsedComId))
                {
                    allowedCommunityIds.Add(parsedComId);
                }
            }
        }

        Guid? linkedUnitId = null;
        if (!linkedUnitId.HasValue && Guid.TryParse(principal.FindFirst("unit_id")?.Value, out var claimUnitId))
        {
            linkedUnitId = claimUnitId;
        }
        WorkspaceMembershipStatus status = WorkspaceMembershipStatus.Approved;
        WorkspacePermissionFlags permissions = WorkspacePermissionPresets.DefaultForRole(role);

        // Detect target property from the question or activeCommunityId so we can resolve the highest permission for that property
        var effectiveCommunityId = await DetectTargetCommunityFromGoalAsync(db, userGoal, targetCommunityId, ct);
        Guid? targetCommunityFirmId = null;
        if (effectiveCommunityId.HasValue && effectiveCommunityId.Value != Guid.Empty)
        {
            targetCommunityFirmId = await db.Communities
                .AsNoTracking()
                .Where(c => c.Id == effectiveCommunityId.Value)
                .Select(c => (Guid?)c.ManagementFirmId)
                .FirstOrDefaultAsync(ct);
        }

        // Introspect all FirmMemberships for the user and select the highest possible permission level for the target property
        if (userId != Guid.Empty)
        {
            var candidates = await db.FirmMemberships
                .AsNoTracking()
                .Include(m => m.PropertyAccesses)
                .Include(m => m.UnitLinks)
                .Where(m => m.UserId == userId || (activeMembershipId != Guid.Empty && m.Id == activeMembershipId))
                .ToListAsync(ct);

            var userAcc = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (candidates.Count == 0 && userAcc is not null)
            {
                candidates.Add(new FirmMembership
                {
                    Id = Guid.Empty,
                    UserId = userAcc.Id,
                    ManagementFirmId = userAcc.ManagementFirmId ?? Guid.Empty,
                    Role = userAcc.Role,
                    Status = WorkspaceMembershipStatus.Approved,
                    GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(userAcc.Role)
                });
            }

            if (candidates.Count > 0)
            {
                var approved = candidates.Where(m => m.Status == WorkspaceMembershipStatus.Approved).ToList();
                var pool = approved.Count > 0 ? approved : candidates;

                var hasTargetFirm = targetCommunityFirmId.HasValue && targetCommunityFirmId.Value != Guid.Empty;
                var targetFirmId = hasTargetFirm ? targetCommunityFirmId!.Value : Guid.Empty;
                List<FirmMembership> propertyMatches;
                if (effectiveCommunityId.HasValue && effectiveCommunityId.Value != Guid.Empty)
                {
                    var targetCom = effectiveCommunityId.Value;
                    propertyMatches = pool.Where(m =>
                        (!hasTargetFirm || m.ManagementFirmId == targetFirmId) &&
                        (m.PropertyAccesses.Any(pa => pa.CommunityId == targetCom)
                        || m.UnitLinks.Any(ul => ul.CommunityId == targetCom)
                        || (m.Role == WorkspaceSecurityRole.FirmManager && (!hasTargetFirm || m.ManagementFirmId == targetFirmId))
                        || (m.PropertyAccesses.Count == 0 && (!hasTargetFirm || m.ManagementFirmId == targetFirmId))))
                        .ToList();

                    if (propertyMatches.Count == 0 && hasTargetFirm)
                    {
                        // Prefer any membership in the target firm even when no explicit access row exists.
                        // Do not fall back to global best rank which may belong to another firm.
                        var targetFirmMatches = pool.Where(m => m.ManagementFirmId == targetFirmId).ToList();
                        propertyMatches = targetFirmMatches.Count > 0 ? targetFirmMatches : pool;
                    }
                    else if (propertyMatches.Count == 0)
                    {
                        propertyMatches = pool;
                    }
                }
                else
                {
                    propertyMatches = pool;
                }

                var bestMembership = propertyMatches
                    .OrderByDescending(m => GetRolePrivilegeRank(m.Role))
                    .ThenByDescending(m => m.Id == activeMembershipId ? 1 : 0)
                    .First();

                if (hasTargetFirm && bestMembership.ManagementFirmId != targetFirmId)
                {
                    // No target-firm match won: prefer a target-firm membership even with lower rank.
                    // Never escalate via another firm's FirmManager role.
                    var targetFirmBest = pool.Where(m => m.ManagementFirmId == targetFirmId)
                        .OrderByDescending(m => GetRolePrivilegeRank(m.Role))
                        .ThenByDescending(m => m.Id == activeMembershipId ? 1 : 0)
                        .FirstOrDefault();
                    if (targetFirmBest is not null)
                    {
                        bestMembership = targetFirmBest;
                    }
                    else if (bestMembership.Role == WorkspaceSecurityRole.FirmManager)
                    {
                        // User has no membership in the target firm: do not grant FirmManager there.
                        bestMembership = propertyMatches
                            .Where(m => m.Role != WorkspaceSecurityRole.FirmManager)
                            .OrderByDescending(m => GetRolePrivilegeRank(m.Role))
                            .ThenByDescending(m => m.Id == activeMembershipId ? 1 : 0)
                            .FirstOrDefault() ?? bestMembership;
                        if (bestMembership.Role == WorkspaceSecurityRole.FirmManager)
                        {
                            role = WorkspaceSecurityRole.Resident;
                            status = WorkspaceMembershipStatus.PendingApproval;
                            firmId = targetFirmId;
                            permissions = WorkspacePermissionPresets.DefaultForRole(role);
                            activeMembershipId = Guid.Empty;
                            foreach (var m in pool)
                            {
                                foreach (var acc in m.PropertyAccesses)
                                {
                                    if (!allowedCommunityIds.Contains(acc.CommunityId))
                                    {
                                        allowedCommunityIds.Add(acc.CommunityId);
                                    }
                                }
                            }
                            var isFirmManagerDenied = false;
                            return new SparkAgentPermissionContext(
                                UserId: userId,
                                FullName: fullName,
                                Email: email,
                                ActiveMembershipId: null,
                                ManagementFirmId: firmId,
                                Role: role,
                                MembershipStatus: status,
                                GrantedPermissions: permissions,
                                AllowedCommunityIds: allowedCommunityIds,
                                LinkedUnitId: linkedUnitId,
                                LinkedUnitNumber: linkedUnitNumber,
                                IsFirmManager: isFirmManagerDenied);
                        }
                    }
                }

                role = bestMembership.Role;
                status = bestMembership.Status;
                firmId = hasTargetFirm ? targetFirmId : (bestMembership.ManagementFirmId != Guid.Empty ? bestMembership.ManagementFirmId : firmId);
                activeMembershipId = bestMembership.Id;

                WorkspacePermissionFlags combinedPermissions = 0;
                foreach (var m in propertyMatches)
                {
                    var memberPerms = m.GrantedPermissions != 0
                        ? m.GrantedPermissions
                        : WorkspacePermissionPresets.DefaultForRole(m.Role);
                    combinedPermissions |= memberPerms;
                }
                permissions = combinedPermissions != 0
                    ? combinedPermissions
                    : WorkspacePermissionPresets.DefaultForRole(role);

                foreach (var m in pool)
                {
                    foreach (var acc in m.PropertyAccesses)
                    {
                        if (!allowedCommunityIds.Contains(acc.CommunityId))
                        {
                            allowedCommunityIds.Add(acc.CommunityId);
                        }
                    }
                }

                var unitLink = bestMembership.UnitLinks.FirstOrDefault(ul => !effectiveCommunityId.HasValue || ul.CommunityId == effectiveCommunityId.Value)
                    ?? bestMembership.UnitLinks.FirstOrDefault()
                    ?? propertyMatches.SelectMany(m => m.UnitLinks).FirstOrDefault();

                if (unitLink != null)
                {
                    linkedUnitId = unitLink.UnitId;
                    var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitLink.UnitId, ct);
                    if (unit != null)
                    {
                        linkedUnitNumber = unit.UnitNumber;
                    }
                }
            }
        }

        var isFirmManager = role == WorkspaceSecurityRole.FirmManager;
        if (isFirmManager)
        {
            permissions = (WorkspacePermissionFlags)~0L;
        }

        return new SparkAgentPermissionContext(
            UserId: userId,
            FullName: fullName,
            Email: email,
            ActiveMembershipId: activeMembershipId != Guid.Empty ? activeMembershipId : null,
            ManagementFirmId: firmId,
            Role: role,
            MembershipStatus: status,
            GrantedPermissions: permissions,
            AllowedCommunityIds: allowedCommunityIds,
            LinkedUnitId: linkedUnitId,
            LinkedUnitNumber: linkedUnitNumber,
            IsFirmManager: isFirmManager);
    }

    private static SparkAgentPermissionContext CreateGuestContext()
    {
        return new SparkAgentPermissionContext(
            UserId: Guid.Empty,
            FullName: "Guest User",
            Email: "guest@commonground.local",
            ActiveMembershipId: null,
            ManagementFirmId: null,
            Role: WorkspaceSecurityRole.ExternalAuditorView,
            MembershipStatus: WorkspaceMembershipStatus.PendingApproval,
            GrantedPermissions: 0,
            AllowedCommunityIds: Array.Empty<Guid>(),
            LinkedUnitId: null,
            LinkedUnitNumber: null,
            IsFirmManager: false);
    }

    public static string NormalizeToolName(string? toolName)
    {
        var raw = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        return raw switch
        {
            "create_resident_profile"
                or "create_resident"
                or "create_resident_wizard"
                or "register_resident"
                or "add_resident"
                or "register_resident_profile"
                or "new_resident"
                or "upsert_resident"
                or "create_or_update_resident" => "create_or_update_resident_profile",

            "list_available_tools"
                or "list_tools"
                or "get_tools"
                or "available_tools"
                or "tools_catalog"
                or "list_capabilities" => "list_account_capabilities",

            "create_unit"
                or "add_unit"
                or "new_unit" => "create_unit_wizard",

            "ask_clarification"
                or "ask_question"
                or "ask_user"
                or "clarify" => "ask_user_clarification",

            "register_link"
                or "create_invitation_link"
                or "create_registration_link"
                or "create_resident_link"
                or "generate_profile_registration_link"
                or "profile_registration_link"
                or "send_invitation_email"
                or "claim_profile"
                or "claim_resident_profile" => "generate_registration_link",

            "query_entities"
                or "search_entities" => "query_system_entities",

            "execute_query"
                or "db_query" => "execute_database_query",

            "create_property"
                or "add_community"
                or "new_community" => "create_community",

            "remove_building"
                or "delete building"
                or "remove building" => "delete_building",

            "upload_ballot_ocr"
                or "digitize_ballot"
                or "ballot_ocr" => "run_governance_and_voting_workflow",

            "weather"
                or "get_weather"
                or "check_weather"
                or "weather_scan"
                or "scan_outages"
                or "check_outages"
                or "outage_scan"
                or "outages_and_weather" => "get_weather_and_outages",

            _ => raw
        };
    }

    public SparkAgentPermissionEvaluationResult EvaluateToolPermission(
        SparkAgentPermissionContext context,
        string toolName,
        IReadOnlyDictionary<string, string> args)
    {
        var normalizedTool = NormalizeToolName(toolName);

        // CATEGORY A: Introspection, Clarification, DB Query, UI
        if (normalizedTool is "check_account_permission" or "list_account_capabilities" or "ask_user_clarification")
        {
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        if (normalizedTool is "execute_database_query" or "query_system_entities" or "get_workspace_overview" or "generate_custom_info_image" or "get_weather_and_outages")
        {
            var targetComId = ResolveCommunityId(args);
            if (targetComId.HasValue && !context.CanAccessCommunity(targetComId.Value))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    0,
                    0,
                    $"Community '{targetComId}' is outside your active property access scope.");
            }
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        if (normalizedTool is "switch_ui_context_or_navigate")
        {
            var targetComId = ResolveCommunityId(args);
            if (targetComId.HasValue && !context.CanAccessCommunity(targetComId.Value))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    0,
                    0,
                    $"Target community '{targetComId}' is outside your allowed community scope.");
            }
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        // CATEGORY B: Firm Administration
        if (normalizedTool is "update_firm_contacts" or "create_community")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.FirmFullAdministration;
            if (!context.HasFlag(required) || context.Role != WorkspaceSecurityRole.FirmManager)
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    $"Action '{toolName}' requires FirmManager role with FirmFullAdministration permission.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        if (normalizedTool is "create_scoped_invitation" or "approve_or_update_membership")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.InviteAndApproveMembers;
            if (!context.HasFlag(required))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    $"Action '{toolName}' requires InviteAndApproveMembers permission.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        if (normalizedTool is "create_firm_manager_group" or "post_firm_manager_message")
        {
            if (context.Role is not (WorkspaceSecurityRole.FirmManager or WorkspaceSecurityRole.PropertyManager))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.FirmFullAdministration,
                    WorkspacePermissionFlags.FirmFullAdministration,
                    $"Action '{toolName}' is restricted to FirmManager or PropertyManager roles.");
            }
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        // CATEGORY C: Communities & Onboarding
        if (normalizedTool is "discover_property_from_address")
        {
            var autoProvision = bool.TryParse(args.GetValueOrDefault("autoProvisionCommunity"), out var b) && b;
            if (autoProvision && (!context.HasFlag(WorkspacePermissionFlags.FirmFullAdministration) || context.Role != WorkspaceSecurityRole.FirmManager))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.FirmFullAdministration,
                    WorkspacePermissionFlags.FirmFullAdministration,
                    "Auto-provisioning communities from address discovery requires FirmManager role with FirmFullAdministration.");
            }
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        if (normalizedTool is "update_community_profile" or "upsert_community_contact" or "delete_community_contact" or "add_property_document" or "upload_property_document" or "bind_document_to_resolution" or "bind_property_document" or "unbind_document_from_resolution")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.ManagePropertySettings;
            if (!context.HasFlag(required))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    $"Action '{toolName}' requires ManagePropertySettings permission.");
            }
            var targetComId = ResolveCommunityId(args);
            if (targetComId.HasValue && !context.CanAccessCommunity(targetComId.Value))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    0,
                    $"Community '{targetComId}' is outside your allowed community scope.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        if (normalizedTool is "check_property_documents")
        {
            var targetComId = ResolveCommunityId(args);
            if (targetComId.HasValue && !context.CanAccessCommunity(targetComId.Value))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    0,
                    0,
                    $"Community '{targetComId}' is outside your allowed community scope.");
            }
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        // CATEGORY D: Buildings & Units (Strictly Manager Only)
        if (normalizedTool is "create_building" or "upsert_riser_stack_or_structure" or "create_unit_wizard"
            or "update_unit_details" or "bulk_generate_floor_units" or "delete_unit" or "delete_building")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.ManageUnitsAndInfrastructure;
            if (context.Role == WorkspaceSecurityRole.Resident || !context.HasFlag(required))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    $"Action '{toolName}' is an administrative operation requiring ManageUnitsAndInfrastructure permission.");
            }
            var targetComId = ResolveCommunityId(args);
            if (targetComId.HasValue && !context.CanAccessCommunity(targetComId.Value))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    0,
                    $"Target community '{targetComId}' is outside your property access scope.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        // CATEGORY E: Residents & Vault
        if (normalizedTool is "assign_resident_to_unit" or "execute_resident_move_out"
            or "generate_registration_link" or "revoke_registration_link" or "review_registration_request")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.InviteAndApproveMembers;
            if (context.Role == WorkspaceSecurityRole.Resident || !context.HasFlag(required))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    $"Action '{toolName}' requires InviteAndApproveMembers permission.");
            }
            var targetComId = ResolveCommunityId(args);
            if (targetComId.HasValue && !context.CanAccessCommunity(targetComId.Value))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    0,
                    $"Target community '{targetComId}' is outside your property access scope.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        if (normalizedTool is "create_or_update_resident_profile")
        {
            if (context.HasFlag(WorkspacePermissionFlags.InviteAndApproveMembers))
            {
                return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.InviteAndApproveMembers, 0);
            }
            // Resident updating their own profile
            if (context.Role == WorkspaceSecurityRole.Resident && context.HasFlag(WorkspacePermissionFlags.ResidentCommunityAndMessages))
            {
                var profileIdStr = args.GetValueOrDefault("residentProfileId");
                if (!string.IsNullOrWhiteSpace(profileIdStr) && Guid.TryParse(profileIdStr, out var pid) && pid == context.UserId)
                {
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentCommunityAndMessages, 0);
                }
            }
            return new SparkAgentPermissionEvaluationResult(
                false,
                WorkspacePermissionFlags.InviteAndApproveMembers,
                WorkspacePermissionFlags.InviteAndApproveMembers,
                "Creating or modifying resident profiles requires InviteAndApproveMembers permission.");
        }

        if (normalizedTool is "upsert_or_remove_household_member" or "add_resident_document")
        {
            if (context.HasFlag(WorkspacePermissionFlags.InviteAndApproveMembers) || context.HasFlag(WorkspacePermissionFlags.ManageUnitsAndInfrastructure))
            {
                return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.InviteAndApproveMembers, 0);
            }
            if (context.Role == WorkspaceSecurityRole.Resident)
            {
                var targetUnitId = ResolveUnitId(args);
                var targetUnitNum = args.GetValueOrDefault("unitNumber");
                if (context.IsOwnUnit(targetUnitId, targetUnitNum))
                {
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentViewUnitAndLedger, 0);
                }
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.ResidentViewUnitAndLedger,
                    WorkspacePermissionFlags.ResidentViewUnitAndLedger,
                    "Residents may only modify household members and vault documents for their own linked unit.");
            }
            return new SparkAgentPermissionEvaluationResult(
                false,
                WorkspacePermissionFlags.InviteAndApproveMembers,
                WorkspacePermissionFlags.InviteAndApproveMembers,
                $"Action '{toolName}' requires InviteAndApproveMembers permission.");
        }

        // CATEGORY F: Financials & Utilities
        if (normalizedTool is "submit_meter_reading")
        {
            if (context.HasFlag(WorkspacePermissionFlags.ManageUtilitiesAndTariffs))
            {
                return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ManageUtilitiesAndTariffs, 0);
            }
            if (context.Role == WorkspaceSecurityRole.Resident && context.HasFlag(WorkspacePermissionFlags.ResidentSubmitMeterReadings))
            {
                var targetUnitId = ResolveUnitId(args);
                var targetUnitNum = args.GetValueOrDefault("unitNumber");
                if (targetUnitId == null && string.IsNullOrWhiteSpace(targetUnitNum))
                {
                    // Meter reading submitted without explicit unit targets the caller's linked unit
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentSubmitMeterReadings, 0);
                }
                if (context.IsOwnUnit(targetUnitId, targetUnitNum))
                {
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentSubmitMeterReadings, 0);
                }
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.ResidentSubmitMeterReadings,
                    WorkspacePermissionFlags.ResidentSubmitMeterReadings,
                    "Residents may only submit meter readings for their own linked unit.");
            }
            return new SparkAgentPermissionEvaluationResult(
                false,
                WorkspacePermissionFlags.ManageUtilitiesAndTariffs,
                WorkspacePermissionFlags.ManageUtilitiesAndTariffs,
                "Submitting meter readings requires ManageUtilitiesAndTariffs or ResidentSubmitMeterReadings.");
        }

        if (normalizedTool is "run_bulk_utility_settlement")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.ManageUtilitiesAndTariffs;
            if (context.Role == WorkspaceSecurityRole.Resident || !context.HasFlag(required))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    "Running bulk utility settlements is an administrative operation requiring ManageUtilitiesAndTariffs.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        if (normalizedTool is "match_bank_transfers" or "run_inflation_and_power_audit" or "generate_czynsz_assessment")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.ManageFinancialsAndLedger;
            if (context.Role == WorkspaceSecurityRole.Resident || !context.HasFlag(required))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    $"Action '{toolName}' requires ManageFinancialsAndLedger permission.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        if (normalizedTool is "generate_arrears_or_mediation_action")
        {
            var actionType = args.GetValueOrDefault("actionType") ?? string.Empty;
            if (actionType.Contains("arrears", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.HasFlag(WorkspacePermissionFlags.ManageFinancialsAndLedger))
                {
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        WorkspacePermissionFlags.ManageFinancialsAndLedger,
                        WorkspacePermissionFlags.ManageFinancialsAndLedger,
                        "Generating arrears recovery dossiers requires ManageFinancialsAndLedger.");
                }
            }
            else
            {
                if (!context.HasFlag(WorkspacePermissionFlags.ManageGovernanceAndMeetings))
                {
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        WorkspacePermissionFlags.ManageGovernanceAndMeetings,
                        WorkspacePermissionFlags.ManageGovernanceAndMeetings,
                        "Drafting dispute mediation notices requires ManageGovernanceAndMeetings.");
                }
            }
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        // CATEGORY G: Maintenance, Vision, Voice, Governance & Security
        if (normalizedTool is "transcribe_voice_or_audio")
        {
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        if (normalizedTool is "submit_maintenance_request")
        {
            if (context.HasFlag(WorkspacePermissionFlags.ManageWorkOrdersAndInspections))
            {
                return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ManageWorkOrdersAndInspections, 0);
            }
            if (context.Role == WorkspaceSecurityRole.Resident && context.HasFlag(WorkspacePermissionFlags.ResidentReportMaintenance))
            {
                var targetUnitId = ResolveUnitId(args);
                var targetUnitNum = args.GetValueOrDefault("unitNumber");
                if (targetUnitId == null && string.IsNullOrWhiteSpace(targetUnitNum))
                {
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentReportMaintenance, 0);
                }
                if (context.IsOwnUnit(targetUnitId, targetUnitNum))
                {
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentReportMaintenance, 0);
                }
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.ResidentReportMaintenance,
                    WorkspacePermissionFlags.ResidentReportMaintenance,
                    "Residents may only report maintenance faults for their own linked unit or common elements.");
            }
            return new SparkAgentPermissionEvaluationResult(
                false,
                WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                "Reporting maintenance requires ManageWorkOrdersAndInspections or ResidentReportMaintenance.");
        }

        if (normalizedTool is "post_maintenance_message")
        {
            if (context.HasFlag(WorkspacePermissionFlags.ManageWorkOrdersAndInspections))
            {
                return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ManageWorkOrdersAndInspections, 0);
            }
            if (context.HasFlag(WorkspacePermissionFlags.ResidentReportMaintenance)
                || context.HasFlag(WorkspacePermissionFlags.ResidentCommunityAndMessages)
                || context.HasFlag(WorkspacePermissionFlags.ContractorDirectMessaging))
            {
                return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentReportMaintenance, 0);
            }
            return new SparkAgentPermissionEvaluationResult(
                false,
                WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                "Posting maintenance thread messages requires ManageWorkOrdersAndInspections or ResidentReportMaintenance.");
        }

        if (normalizedTool is "assign_maintenance_contractor")
        {
            const WorkspacePermissionFlags required = WorkspacePermissionFlags.ManageWorkOrdersAndInspections;
            if (!context.HasFlag(required))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    required,
                    required,
                    "Assigning a maintenance contractor requires ManageWorkOrdersAndInspections permission.");
            }
            return new SparkAgentPermissionEvaluationResult(true, required, 0);
        }

        if (normalizedTool is "run_vision_inspection_or_verify_repair")
        {
            if (context.HasFlag(WorkspacePermissionFlags.ManageWorkOrdersAndInspections)
                || context.HasFlag(WorkspacePermissionFlags.ContractorSamRepairVerification))
            {
                return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ManageWorkOrdersAndInspections, 0);
            }
            return new SparkAgentPermissionEvaluationResult(
                false,
                WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                "Running vision inspections requires ManageWorkOrdersAndInspections or ContractorSamRepairVerification.");
        }

        if (normalizedTool is "evaluate_or_submit_modification_request")
        {
            var isSubmit = args.ContainsKey("description") || !args.ContainsKey("requestId");
            if (isSubmit && context.Role == WorkspaceSecurityRole.Resident)
            {
                var targetUnitId = ResolveUnitId(args);
                var targetUnitNum = args.GetValueOrDefault("unitNumber");
                if (context.IsOwnUnit(targetUnitId, targetUnitNum) || (targetUnitId == null && string.IsNullOrWhiteSpace(targetUnitNum)))
                {
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentSubmitModificationRequests, 0);
                }
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.ResidentSubmitModificationRequests,
                    WorkspacePermissionFlags.ResidentSubmitModificationRequests,
                    "Residents may only submit renovation/modification requests for their own linked unit.");
            }
            if (!context.HasFlag(WorkspacePermissionFlags.ManageWorkOrdersAndInspections))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                    WorkspacePermissionFlags.ManageWorkOrdersAndInspections,
                    "Evaluating modification requests requires ManageWorkOrdersAndInspections.");
            }
            return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ManageWorkOrdersAndInspections, 0);
        }

        if (normalizedTool is "run_governance_and_voting_workflow")
        {
            var workflowType = args.GetValueOrDefault("workflowType") ?? string.Empty;
            if (workflowType.Equals("cast_vote", StringComparison.OrdinalIgnoreCase))
            {
                if (context.HasFlag(WorkspacePermissionFlags.ManageGovernanceAndMeetings))
                {
                    return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ManageGovernanceAndMeetings, 0);
                }
                if (context.Role == WorkspaceSecurityRole.Resident && context.HasFlag(WorkspacePermissionFlags.ResidentVoteAndGovernance))
                {
                    var targetUnitId = ResolveUnitId(args);
                    var targetUnitNum = args.GetValueOrDefault("unitNumber");
                    if (targetUnitId == null && string.IsNullOrWhiteSpace(targetUnitNum))
                    {
                        return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentVoteAndGovernance, 0);
                    }
                    if (context.IsOwnUnit(targetUnitId, targetUnitNum))
                    {
                        return new SparkAgentPermissionEvaluationResult(true, WorkspacePermissionFlags.ResidentVoteAndGovernance, 0);
                    }
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        WorkspacePermissionFlags.ResidentVoteAndGovernance,
                        WorkspacePermissionFlags.ResidentVoteAndGovernance,
                        "Residents may only cast votes for their own linked unit.");
                }
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    WorkspacePermissionFlags.ResidentVoteAndGovernance,
                    WorkspacePermissionFlags.ResidentVoteAndGovernance,
                    "Casting resolution votes requires ResidentVoteAndGovernance permission.");
            }

            // Other governance workflows (autopilot, compliance fix, QR poster, paper ballot OCR)
            const WorkspacePermissionFlags adminGov = WorkspacePermissionFlags.ManageGovernanceAndMeetings;
            if (context.Role == WorkspaceSecurityRole.Resident || !context.HasFlag(adminGov))
            {
                return new SparkAgentPermissionEvaluationResult(
                    false,
                    adminGov,
                    adminGov,
                    $"Governance workflow '{workflowType}' requires ManageGovernanceAndMeetings permission.");
            }
            return new SparkAgentPermissionEvaluationResult(true, adminGov, 0);
        }

        if (normalizedTool is "post_announcement_or_direct_message")
        {
            var msgType = args.GetValueOrDefault("messageType") ?? string.Empty;
            if (msgType.Contains("announcement", StringComparison.OrdinalIgnoreCase))
            {
                if (!context.HasFlag(WorkspacePermissionFlags.ManagePropertySettings))
                {
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        WorkspacePermissionFlags.ManagePropertySettings,
                        WorkspacePermissionFlags.ManagePropertySettings,
                        "Publishing property-wide announcements requires ManagePropertySettings.");
                }
            }
            else
            {
                if (!context.HasFlag(WorkspacePermissionFlags.ManagePropertySettings)
                    && !context.HasFlag(WorkspacePermissionFlags.ResidentCommunityAndMessages)
                    && !context.HasFlag(WorkspacePermissionFlags.ContractorDirectMessaging))
                {
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        WorkspacePermissionFlags.ResidentCommunityAndMessages,
                        WorkspacePermissionFlags.ResidentCommunityAndMessages,
                        "Sending messages requires ResidentCommunityAndMessages or ManagePropertySettings.");
                }
            }
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        if (normalizedTool is "security_approvals_sos_and_vault")
        {
            var actionType = (args.GetValueOrDefault("actionType") ?? string.Empty).ToLowerInvariant();
            if (actionType is "decide_ai_action" or "approve_all_ai_actions")
            {
                const WorkspacePermissionFlags req = WorkspacePermissionFlags.ApproveAiActions;
                if (!context.HasFlag(req))
                {
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        req,
                        req,
                        "Approving or rejecting proposed AI actions requires ApproveAiActions permission.");
                }
                return new SparkAgentPermissionEvaluationResult(true, req, 0);
            }
            if (actionType is "sos_broadcast")
            {
                const WorkspacePermissionFlags req = WorkspacePermissionFlags.ManagePropertySettings;
                if (!context.HasFlag(req))
                {
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        req,
                        req,
                        "Triggering 1-click emergency SOS broadcasts requires ManagePropertySettings.");
                }
                return new SparkAgentPermissionEvaluationResult(true, req, 0);
            }
            if (actionType is "reveal_access_code")
            {
                if (context.Role is not (WorkspaceSecurityRole.FirmManager or WorkspaceSecurityRole.PropertyManager))
                {
                    return new SparkAgentPermissionEvaluationResult(
                        false,
                        WorkspacePermissionFlags.ManageUnitsAndInfrastructure,
                        WorkspacePermissionFlags.ManageUnitsAndInfrastructure,
                        "Revealing permanent access PINs and boiler-room key-safes requires PropertyManager or FirmManager role.");
                }
                return new SparkAgentPermissionEvaluationResult(true, 0, 0);
            }
            if (string.IsNullOrWhiteSpace(actionType) || actionType is "verify_audit_chain")
            {
                return new SparkAgentPermissionEvaluationResult(true, 0, 0);
            }
            return new SparkAgentPermissionEvaluationResult(
                false,
                WorkspacePermissionFlags.ManagePropertySettings,
                WorkspacePermissionFlags.ManagePropertySettings,
                $"Action '{actionType}' in security_approvals_sos_and_vault is not recognized or permitted.");
        }

        // FAIL-SAFE DEFAULT: Deny any uncataloged, misspelled, or unhandled tool
        return new SparkAgentPermissionEvaluationResult(
            false,
            WorkspacePermissionFlags.FirmFullAdministration,
            WorkspacePermissionFlags.FirmFullAdministration,
            $"Tool '{toolName}' is not recognized or permitted in the active workspace context.");
    }

    private static Guid? ResolveCommunityId(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("communityId", out var cStr) && Guid.TryParse(cStr, out var cId) && cId != Guid.Empty)
            return cId;
        if (args.TryGetValue("targetCommunityId", out var tcStr) && Guid.TryParse(tcStr, out var tcId) && tcId != Guid.Empty)
            return tcId;
        return null;
    }

    private static Guid? ResolveUnitId(IReadOnlyDictionary<string, string> args)
    {
        if (args.TryGetValue("unitId", out var uStr) && Guid.TryParse(uStr, out var uId) && uId != Guid.Empty)
            return uId;
        if (args.TryGetValue("targetUnitId", out var tuStr) && Guid.TryParse(tuStr, out var tuId) && tuId != Guid.Empty)
            return tuId;
        return null;
    }
}

public sealed class SparkAgentToolExecutor(
    CommonGroundWorkspaceService workspace,
    IPropertyOnboardingService onboardingService,
    IInvitationService invitationService,
    IMembershipService membershipService,
    IMuseVoiceTranscriber voiceTranscriber,
    IAppDbContextFactory dbFactory)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public IInvitationService InvitationService => invitationService;
    public IMembershipService MembershipService => membershipService;

    public async Task<ToolObservationResult> ExecuteToolAsync(
        string toolName,
        IReadOnlyDictionary<string, string> args,
        SparkAgentPermissionContext context,
        Guid defaultCommunityId,
        CancellationToken ct = default)
    {
        var normalized = SparkAgentPermissionEvaluator.NormalizeToolName(toolName);
        var comId = ResolveCommunityId(args, context, defaultCommunityId);

        try
        {
            switch (normalized)
            {
                // A1. check_account_permission
                case "check_account_permission":
                {
                    var targetTool = args.GetValueOrDefault("toolName") ?? "create_unit_wizard";
                    var evaluator = new SparkAgentPermissionEvaluator();
                    var check = evaluator.EvaluateToolPermission(context, targetTool, args);
                    return MakeResult(new
                    {
                        toolChecked = targetTool,
                        isPermitted = check.IsPermitted,
                        requiredFlags = check.RequiredFlags.ToString(),
                        missingFlags = check.MissingFlags.ToString(),
                        denialReason = check.DenialReason,
                        activeRole = context.Role.ToString(),
                        activeUser = context.Email
                    }, $"Evaluated permission for '{targetTool}': {(check.IsPermitted ? "PERMITTED" : "DENIED")}");
                }

                // A2. list_account_capabilities
                case "list_account_capabilities":
                {
                    return MakeResult(new
                    {
                        userId = context.UserId,
                        fullName = context.FullName,
                        email = context.Email,
                        role = context.Role.ToString(),
                        firmId = context.ManagementFirmId,
                        status = context.MembershipStatus.ToString(),
                        grantedPermissions = context.GrantedPermissions.ToString(),
                        allowedCommunitiesCount = context.AllowedCommunityIds.Count,
                        linkedUnit = context.LinkedUnitNumber
                    }, $"Active account capabilities for {context.FullName} ({context.Role})");
                }

                // A3. ask_user_clarification
                case "ask_user_clarification":
                {
                    var question = args.GetValueOrDefault("question") ?? "Clarification requested.";
                    var options = ParseOptions(args.GetValueOrDefault("options"));
                    return MakeResult(new
                    {
                        status = "AwaitingResponse",
                        question,
                        options,
                        inputHint = args.GetValueOrDefault("inputHint"),
                        contextSummary = args.GetValueOrDefault("contextSummary")
                    }, $"Prompted user for clarification: \"{question}\"");
                }

                // A4. execute_database_query
                case "execute_database_query":
                {
                    return await ExecuteDynamicDbQueryAsync(args, context, comId, ct);
                }

                // A5. query_system_entities
                case "query_system_entities":
                {
                    var entityType = (args.GetValueOrDefault("entityType") ?? "units").ToLowerInvariant();
                    var searchTerm = args.GetValueOrDefault("searchTerm") ?? string.Empty;
                    return await ExecuteSystemEntityQueryAsync(entityType, searchTerm, comId, context, ct);
                }

                // A6. get_workspace_overview
                case "get_workspace_overview":
                {
                    var snapshot = await workspace.GetSnapshotAsync(comId, ct);
                    if (snapshot == null)
                    {
                        return MakeError($"Community '{comId}' not found or snapshot unavailable.");
                    }
                    return MakeResult(new
                    {
                        communityId = snapshot.Community.Id,
                        name = snapshot.Community.Name,
                        operatingCash = snapshot.OperatingCash,
                        reserveCash = snapshot.ReserveCash,
                        openWorkOrders = snapshot.OpenWorkOrders,
                        pendingApprovals = snapshot.PendingApprovals,
                        arrearsUnits = snapshot.ArrearsUnits,
                        delinquencyRate = snapshot.DelinquencyRate,
                        activeOutagesCount = snapshot.ActiveOutages.Count
                    }, $"Loaded workspace overview for {snapshot.Community.Name} (Cash: {snapshot.OperatingCash:N2} {snapshot.Community.CurrencyCode})",
                    $"/properties/{snapshot.Community.Id}/overview");
                }

                // A6b. get_weather_and_outages (live outage + weather radar via workspace scan)
                case "get_weather_and_outages":
                {
                    var summary = await workspace.ScanUtilitySourcesAndWeatherAsync(comId, ct);
                    await using var weatherDb = await dbFactory.CreateDbContextAsync(ct);
                    var outageRows = await weatherDb.UtilityOutages.AsNoTracking()
                        .Where(o => o.CommunityId == comId && o.IsActive)
                        .OrderByDescending(o => o.StartAt)
                        .Take(10)
                        .Select(o => new { o.UtilityKind, o.ProviderName, o.Summary, o.StartAt, o.EstimatedEndAt })
                        .ToListAsync(ct);
                    return MakeResult(new
                    {
                        communityId = comId,
                        summary,
                        activeOutages = outageRows
                    }, summary, $"/properties/{comId}/utilities");
                }

                // A7. switch_ui_context_or_navigate
                case "switch_ui_context_or_navigate":
                {
                    var targetRoute = args.GetValueOrDefault("targetRoute") ?? "/";
                    var theme = args.GetValueOrDefault("theme");
                    return MakeResult(new
                    {
                        status = "Success",
                        navigatedTo = targetRoute,
                        switchedCommunityId = comId,
                        switchedTheme = theme
                    }, $"UI context switched to route '{targetRoute}'", targetRoute);
                }

                // B8. update_firm_contacts
                case "update_firm_contacts":
                {
                    var firmId = context.ManagementFirmId ?? Guid.Empty;
                    var existingFirm = await workspace.GetManagementFirmAsync(firmId, ct);
                    var firm = await workspace.UpdateManagementFirmContactsAsync(
                        firmId,
                        args.GetValueOrDefault("managingFirmLegalName") ?? existingFirm?.ManagingFirmLegalName ?? existingFirm?.Name ?? "Management Firm",
                        args.GetValueOrDefault("firmTaxIdOrNip") ?? existingFirm?.FirmTaxIdOrNip ?? "000-000-00-00",
                        args.GetValueOrDefault("licenseNumber") ?? existingFirm?.LicenseNumber ?? "LIC-000",
                        args.GetValueOrDefault("leadDirectorName") ?? existingFirm?.LeadDirectorName ?? "Director",
                        args.GetValueOrDefault("officeAddress") ?? existingFirm?.OfficeAddress ?? "Headquarters",
                        args.GetValueOrDefault("supportEmail") ?? existingFirm?.SupportEmail ?? "support@firm.com",
                        args.GetValueOrDefault("emergencyDispatchPhone") ?? existingFirm?.EmergencyDispatchPhone ?? "+1-800-000-0000",
                        args.GetValueOrDefault("associationLegalName") ?? existingFirm?.AssociationLegalName ?? existingFirm?.Name ?? "HOA Association",
                        args.GetValueOrDefault("hoaEinNumber") ?? existingFirm?.HoaEinNumber ?? "00-0000000",
                        args.GetValueOrDefault("boardPresidentName") ?? existingFirm?.BoardPresidentName ?? "President",
                        args.GetValueOrDefault("managementAgentContact") ?? existingFirm?.ManagementAgentContact ?? "Agent",
                        args.GetValueOrDefault("clubhouseOrOfficeAddress") ?? existingFirm?.ClubhouseOrOfficeAddress ?? "Clubhouse",
                        args.GetValueOrDefault("hoaBoardEmail") ?? existingFirm?.HoaBoardEmail ?? "board@firm.com",
                        args.GetValueOrDefault("hoaHotlinePhone") ?? existingFirm?.HoaHotlinePhone ?? "+1-800-000-0000",
                        context.EffectiveRole,
                        ct);
                    return MakeResult(new
                    {
                        firmId = firm.Id,
                        name = firm.Name,
                        supportEmail = firm.SupportEmail,
                        updatedAtUtc = DateTime.UtcNow
                    }, $"Updated management firm profile for {firm.Name}", "/admin/firm-contacts");
                }

                // B9. create_scoped_invitation
                case "create_scoped_invitation":
                {
                    var targetRole = ParseEnum(args.GetValueOrDefault("targetRole"), WorkspaceSecurityRole.Resident);
                    var inv = await workspace.CreateWorkspaceInvitationAsync(
                        context.ManagementFirmId ?? Guid.Empty,
                        args.GetValueOrDefault("recipientEmail") ?? "invitee@commonground.local",
                        args.GetValueOrDefault("recipientName") ?? "Prospective Member",
                        args.GetValueOrDefault("recipientPhone") ?? string.Empty,
                        targetRole,
                        InvitationAssignmentMode.AutoAssignSpecificProperty,
                        comId,
                        args.GetValueOrDefault("targetUnitNumber"),
                        args.GetValueOrDefault("contractorCompanyOrTrade"),
                        context.FullName,
                        context.EffectiveRole,
                        targetCommunityIds: [comId],
                        grantedPermissions: WorkspacePermissionPresets.DefaultForRole(targetRole),
                        requiresManagerApproval: true,
                        ct: ct);
                    return MakeResult(new
                    {
                        invitationId = inv.Id,
                        recipientEmail = inv.RecipientEmail,
                        role = inv.TargetRole.ToString(),
                        inviteLink = $"/accept-invite?token={inv.InvitationToken}",
                        expiresAtUtc = inv.ExpiresAtUtc
                    }, $"Created invitation link for {inv.RecipientEmail} ({inv.TargetRole})", "/admin/members");
                }

                // B10. approve_or_update_membership
                case "approve_or_update_membership":
                {
                    var memIdStr = args.GetValueOrDefault("membershipId");
                    if (!Guid.TryParse(memIdStr, out var memId))
                    {
                        return MakeError("Valid membershipId is required.");
                    }
                    var action = (args.GetValueOrDefault("action") ?? "approve").ToLowerInvariant();
                    var updated = await workspace.ApproveOrUpdateMembershipAsync(
                        memId,
                        approve: action != "reject",
                        grantedPermissions: (WorkspacePermissionFlags)0,
                        allowedCommunityIds: null,
                        linkedUnitNumbersCsv: null,
                        contractorCompanyOrTrade: null,
                        actorRole: context.EffectiveRole,
                        ct: ct);
                    return MakeResult(new { membershipId = updated.Id, status = updated.ApprovalStatus.ToString(), role = updated.Role.ToString() },
                        $"Decided membership {updated.Id} -> {updated.ApprovalStatus}", "/admin/members");
                }

                // B11. create_firm_manager_group
                case "create_firm_manager_group":
                {
                    var group = await workspace.CreateFirmManagerGroupAsync(
                        context.ManagementFirmId ?? Guid.Empty,
                        args.GetValueOrDefault("name") ?? "Manager Coordination",
                        args.GetValueOrDefault("topic") ?? "General",
                        context.EffectiveRole,
                        ct);
                    return MakeResult(new { groupId = group.Id, name = group.Name, code = group.Code },
                        $"Created manager communication group '{group.Name}'", "/admin/overview");
                }

                // B12. post_firm_manager_message
                case "post_firm_manager_message":
                {
                    var grpIdStr = args.GetValueOrDefault("groupId");
                    var grpId = Guid.TryParse(grpIdStr, out var gid) ? gid : Guid.NewGuid();
                    var msg = await workspace.PostFirmManagerMessageAsync(
                        context.ManagementFirmId ?? Guid.Empty,
                        grpId,
                        context.FullName,
                        context.EffectiveRole,
                        context.Email,
                        context.Phone,
                        "All Firm Properties",
                        args.GetValueOrDefault("subject") ?? "Update",
                        args.GetValueOrDefault("body") ?? "Manager briefing.",
                        bool.TryParse(args.GetValueOrDefault("isPriorityNotice"), out var p) && p,
                        ct);
                    return MakeResult(new { messageId = msg.Id, subject = msg.Subject, sentAtUtc = msg.SentAtUtc },
                        $"Posted manager message: '{msg.Subject}'", "/admin/overview");
                }

                // C13. create_community
                case "create_community":
                {
                    var mode = ParseEnum(args.GetValueOrDefault("operatingMode"), SystemOperatingMode.HousingAssociation);
                    var comm = await workspace.CreatePropertyForFirmAsync(
                        context.ManagementFirmId ?? Guid.Empty,
                        args.GetValueOrDefault("name") ?? "New Community Residence",
                        args.GetValueOrDefault("streetAddress") ?? "Main Street 1",
                        args.GetValueOrDefault("city") ?? "Warsaw",
                        mode,
                        args.GetValueOrDefault("taxId") ?? "0000000000",
                        context.EffectiveRole,
                        context.UserId == Guid.Empty ? null : context.UserId,
                        context.ActiveMembershipId,
                        ct);
                    return MakeResult(new
                    {
                        communityId = comm.Id,
                        communityAccountNumber = comm.CommunityAccountNumber,
                        name = comm.Name,
                        operatingMode = comm.OperatingMode.ToString()
                    }, $"Provisioned new community '{comm.Name}' ({comm.CommunityAccountNumber})", $"/properties/{comm.Id}/overview");
                }

                // C14. discover_property_from_address
                case "discover_property_from_address":
                {
                    var lookup = new AddressLookupRequest(
                        args.GetValueOrDefault("streetAddress") ?? "Harbor Street 1",
                        args.GetValueOrDefault("postalCode") ?? "00-001",
                        args.GetValueOrDefault("city") ?? "Warsaw",
                        args.GetValueOrDefault("countryOrState") ?? "PL",
                        ParseEnum(args.GetValueOrDefault("operatingModeHint"), SystemOperatingMode.HousingAssociation));
                    var autoProv = bool.TryParse(args.GetValueOrDefault("autoProvisionCommunity"), out var ap) && ap;
                    var dossier = await onboardingService.DiscoverPropertyDossierFromAddressAsync(lookup, ct);
                    Community? createdComm = null;
                    if (autoProv && (context.ManagementFirmId.HasValue || comId != Guid.Empty))
                    {
                        var firmId = context.ManagementFirmId ?? Guid.Empty;
                        createdComm = await workspace.CreatePropertyForFirmAsync(
                            firmId,
                            dossier.SuggestedAssociationName,
                            lookup.StreetAddress,
                            lookup.City,
                            dossier.SuggestedOperatingMode,
                            "DISCOVERED-TIN",
                            context.EffectiveRole,
                            context.UserId == Guid.Empty ? null : context.UserId,
                            context.ActiveMembershipId,
                            ct);
                    }
                    return MakeResult(new
                    {
                        suggestedName = dossier.SuggestedAssociationName,
                        operatingMode = dossier.SuggestedOperatingMode.ToString(),
                        municipality = dossier.LocalMunicipalityOrCounty,
                        court = dossier.CompetentCourtOrRecorder,
                        taxOffice = dossier.LocalTaxOffice,
                        createdCommunityId = createdComm?.Id,
                        createdAccountNumber = createdComm?.CommunityAccountNumber
                    }, $"Discovered property dossier for '{lookup.StreetAddress}, {lookup.City}'", createdComm != null ? $"/properties/{createdComm.Id}/overview" : null);
                }

                // C15. update_community_profile
                case "update_community_profile":
                {
                    var existing = await workspace.GetCommunityAsync(comId, ct);
                    var comm = await workspace.UpdatePropertySettingsAsync(
                        comId,
                        args.GetValueOrDefault("name") ?? existing?.Name ?? "Community",
                        args.GetValueOrDefault("streetAddress") ?? existing?.StreetAddress ?? "Address",
                        args.GetValueOrDefault("city") ?? existing?.City ?? "City",
                        args.GetValueOrDefault("taxId") ?? existing?.TaxIdOrEin ?? "0000000000",
                        ParseDecimal(args.GetValueOrDefault("operatingRatePerM2")) ?? existing?.OperatingRatePerM2 ?? 4.5m,
                        ParseDecimal(args.GetValueOrDefault("repairFundRatePerM2")) ?? existing?.RepairFundRatePerM2 ?? 2.5m,
                        ParseDecimal(args.GetValueOrDefault("wasteFeePerOccupant")) ?? existing?.WasteFeePerOccupant ?? 35m,
                        ParseDecimal(args.GetValueOrDefault("coldWaterTariffPerM3")) ?? existing?.ColdWaterTariffPerM3 ?? 12.5m,
                        ParseDecimal(args.GetValueOrDefault("hotWaterCostPerM3")) ?? existing?.HotWaterHeatingCostPerM3 ?? 28m,
                        ParseDecimal(args.GetValueOrDefault("districtHeatingTariffPerGj")) ?? existing?.DistrictHeatingTariffPerGj ?? 115m,
                        ParseDecimal(args.GetValueOrDefault("commonElectricityTariffPerKwh")) ?? existing?.CommonElectricityTariffPerKwh ?? 1.15m,
                        context.EffectiveRole,
                        context.UserId,
                        ct);
                    return MakeResult(new
                    {
                        communityId = comm.Id,
                        name = comm.Name,
                        operatingRate = comm.OperatingRatePerM2,
                        repairFundRate = comm.RepairFundRatePerM2
                    }, $"Updated community profile and tariffs for {comm.Name}", $"/properties/{comm.Id}/settings");
                }

                // C16. upsert_community_contact
                case "upsert_community_contact":
                {
                    var contactId = Guid.TryParse(args.GetValueOrDefault("contactId"), out var cid) ? cid : (Guid?)null;
                    var cat = ParseEnum(args.GetValueOrDefault("category"), CommunityContactCategory.ManagementOffice);
                    var input = new CommunityContactInput(
                        contactId,
                        cat,
                        args.GetValueOrDefault("contactName") ?? "Main Office",
                        args.GetValueOrDefault("roleTitle") ?? "Property Manager",
                        args.GetValueOrDefault("phone") ?? "+48 22 000 0000",
                        args.GetValueOrDefault("email") ?? "office@commonground.local",
                        args.GetValueOrDefault("officeHours") ?? "Mon-Fri 08:00-17:00",
                        args.GetValueOrDefault("addressOrLocation") ?? "Building A ground floor",
                        args.GetValueOrDefault("notes") ?? string.Empty,
                        int.TryParse(args.GetValueOrDefault("sortOrder"), out var so) ? so : 0,
                        !bool.TryParse(args.GetValueOrDefault("isVisibleToResidents"), out var vis) || vis);

                    var contact = await workspace.UpsertCommunityContactAsync(comId, input, context.EffectiveRole, ct);
                    return MakeResult(new
                    {
                        contactId = contact.Id,
                        contactName = contact.ContactName,
                        roleTitle = contact.RoleTitle,
                        phone = contact.Phone,
                        email = contact.Email
                    }, $"Upserted community contact '{contact.ContactName}' ({contact.RoleTitle})", $"/properties/{comId}/team");
                }

                // C17. delete_community_contact
                case "delete_community_contact":
                {
                    if (Guid.TryParse(args.GetValueOrDefault("contactId"), out var cid))
                    {
                        await workspace.DeleteCommunityContactAsync(comId, cid, context.EffectiveRole, ct);
                        return MakeResult(new { deletedContactId = cid, status = "Deleted" }, $"Deleted community contact {cid}");
                    }
                    return MakeError("Valid contactId is required.");
                }

                // C17b. check_property_documents (read allowed for every WorkspaceMember with property access)
                case "check_property_documents":
                {
                    var docs = await workspace.GetPropertyDocumentsAsync(comId, ct);
                    var search = args.GetValueOrDefault("searchTerm") ?? args.GetValueOrDefault("searchText") ?? string.Empty;
                    var category = args.GetValueOrDefault("category") ?? string.Empty;
                    var filtered = docs.AsEnumerable();
                    if (!string.IsNullOrWhiteSpace(category)
                        && Enum.TryParse<LegalDocumentKind>(category, true, out var kind))
                    {
                        filtered = filtered.Where(d => d.Kind == kind);
                    }
                    if (!string.IsNullOrWhiteSpace(search))
                    {
                        filtered = filtered.Where(d =>
                            d.DocumentCode.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || d.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || d.VerbatimLegalText.Contains(search, StringComparison.OrdinalIgnoreCase));
                    }
                    var rows = filtered.OrderBy(d => d.DocumentCode).Take(20).Select(d => new
                    {
                        documentId = d.Id,
                        documentCode = d.DocumentCode,
                        title = d.Title,
                        category = d.Kind.ToString(),
                        hasAttachment = !string.IsNullOrWhiteSpace(d.AttachmentDataUrl),
                        attachmentFileName = d.AttachmentFileName,
                        boundResolutionId = d.BoundResolutionId,
                        excerpt = d.VerbatimLegalText.Length > 240 ? d.VerbatimLegalText[..240] + "..." : d.VerbatimLegalText
                    }).ToList();
                    return MakeResult(new { communityId = comId, totalCount = rows.Count, documents = rows },
                        $"Checked property documents panel: {rows.Count} document(s) on file ({string.Join(", ", rows.Select(r => r.documentCode))})",
                        $"/properties/{comId}/governance");
                }

                // C17c. add_property_document (write requires ManagePropertySettings)
                case "add_property_document":
                case "upload_property_document":
                {
                    var code = args.GetValueOrDefault("documentCode") ?? string.Empty;
                    var title = args.GetValueOrDefault("title") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(title))
                    {
                        return MakeError("documentCode and title are required to file a property document.");
                    }
                    Guid? bindId = null;
                    var bindRaw = args.GetValueOrDefault("boundResolutionId") ?? args.GetValueOrDefault("resolutionId");
                    if (Guid.TryParse(bindRaw, out var parsedBind) && parsedBind != Guid.Empty)
                    {
                        bindId = parsedBind;
                    }
                    var doc = await workspace.UploadPropertyDocumentAsync(
                        comId,
                        code,
                        title,
                        args.GetValueOrDefault("category") ?? LegalDocumentKind.CommunityStatuteOrCcr.ToString(),
                        args.GetValueOrDefault("verbatimText") ?? args.GetValueOrDefault("notes") ?? string.Empty,
                        args.GetValueOrDefault("attachmentFileName"),
                        args.GetValueOrDefault("attachmentContentType"),
                        args.GetValueOrDefault("attachmentDataUrl"),
                        bindId,
                        context.EffectiveRole,
                        ct);
                    return MakeResult(new
                    {
                        documentId = doc.Id,
                        documentCode = doc.DocumentCode,
                        title = doc.Title,
                        category = doc.Kind.ToString(),
                        hasAttachment = !string.IsNullOrWhiteSpace(doc.AttachmentDataUrl),
                        boundResolutionId = doc.BoundResolutionId
                    }, $"Filed property document '{doc.Title}' ({doc.DocumentCode})" + (bindId.HasValue ? " bound to decision." : string.Empty), $"/properties/{comId}/governance");
                }

                // C17d. bind_document_to_resolution
                case "bind_document_to_resolution":
                case "bind_property_document":
                case "unbind_document_from_resolution":
                {
                    var docRaw = args.GetValueOrDefault("documentId");
                    Guid.TryParse(args.GetValueOrDefault("documentCode") is string dc && Guid.TryParse(dc, out var asGuid) ? dc : null, out _);
                    var isUnbind = toolName.Contains("unbind", StringComparison.OrdinalIgnoreCase);
                    if (!Guid.TryParse(docRaw, out var documentId) || documentId == Guid.Empty)
                    {
                        await using var bindDb = await dbFactory.CreateDbContextAsync(ct);
                        var code = (args.GetValueOrDefault("documentCode") ?? string.Empty).Trim();
                        if (!string.IsNullOrWhiteSpace(code))
                        {
                            var found = await bindDb.LegalDocuments.AsNoTracking()
                                .Where(d => d.CommunityId == comId && d.DocumentCode.ToUpper() == code.ToUpperInvariant())
                                .Select(d => d.Id)
                                .FirstOrDefaultAsync(ct);
                            if (found != Guid.Empty)
                            {
                                documentId = found;
                            }
                        }
                    }
                    if (documentId == Guid.Empty)
                    {
                        return MakeError("documentId (or documentCode) is required to bind a document.");
                    }
                    if (isUnbind)
                    {
                        var unbound = await workspace.UnbindDocumentFromResolutionAsync(comId, documentId, context.EffectiveRole, ct);
                        return MakeResult(new { documentId = unbound.Id, documentCode = unbound.DocumentCode },
                            $"Unbound '{unbound.Title}' from its decision.", $"/properties/{comId}/governance");
                    }
                    var resRaw = args.GetValueOrDefault("resolutionId") ?? args.GetValueOrDefault("boundResolutionId");
                    if (!Guid.TryParse(resRaw, out var resolutionId) || resolutionId == Guid.Empty)
                    {
                        return MakeError("resolutionId is required to bind a document to a decision.");
                    }
                    var bound = await workspace.BindDocumentToResolutionAsync(comId, documentId, resolutionId, context.EffectiveRole, ct);
                    return MakeResult(new { documentId = bound.Id, documentCode = bound.DocumentCode, boundResolutionId = bound.BoundResolutionId },
                        $"Bound '{bound.Title}' to decision.", $"/properties/{comId}/governance");
                }

                // D18. create_building
                case "create_building":
                {
                    var requestedName = (args.GetValueOrDefault("name") ?? "Building A").Trim();
                    await using (var lookupDb = await dbFactory.CreateDbContextAsync(ct))
                    {
                        var existingBld = await lookupDb.Buildings.AsNoTracking()
                            .Where(b => b.CommunityId == comId && b.Name.ToUpper() == requestedName.ToUpperInvariant())
                            .Select(b => new { b.Id, b.Name, b.FloorsCount, b.StairwellsCount })
                            .FirstOrDefaultAsync(ct);
                        if (existingBld is not null)
                        {
                            return MakeResult(new
                            {
                                buildingId = existingBld.Id,
                                name = existingBld.Name,
                                floorsCount = existingBld.FloorsCount,
                                stairwellsCount = existingBld.StairwellsCount,
                                reusedExisting = true
                            }, $"Reused existing building '{existingBld.Name}' (no duplicate created)", $"/properties/{comId}/infrastructure");
                        }
                    }
                    var bld = await workspace.CreateBuildingAsync(
                        comId,
                        requestedName,
                        args.GetValueOrDefault("streetAddress") ?? "Main Street",
                        int.TryParse(args.GetValueOrDefault("floorsCount"), out var fl) ? fl : 5,
                        int.TryParse(args.GetValueOrDefault("stairwellsCount"), out var st) ? st : 2,
                        int.TryParse(args.GetValueOrDefault("constructionYear"), out var yr) ? yr : 2020,
                        context.EffectiveRole,
                        ct);
                    return MakeResult(new
                    {
                        buildingId = bld.Id,
                        name = bld.Name,
                        floorsCount = bld.FloorsCount,
                        stairwellsCount = bld.StairwellsCount,
                        reusedExisting = false
                    }, $"Created building '{bld.Name}' with {bld.FloorsCount} floors", $"/properties/{comId}/infrastructure");
                }

                // D19. upsert_riser_stack_or_structure
                case "upsert_riser_stack_or_structure":
                {
                    var bldId = Guid.TryParse(args.GetValueOrDefault("buildingId"), out var bid) ? bid : Guid.Empty;
                    var action = (args.GetValueOrDefault("action") ?? "upsert_stack").ToLowerInvariant();
                    if (action == "update_structure")
                    {
                        var fl = int.TryParse(args.GetValueOrDefault("floorsCount"), out var f) ? f : 5;
                        var st = int.TryParse(args.GetValueOrDefault("stairwellsCount"), out var s) ? s : 2;
                        var updated = await workspace.UpdateBuildingStructureAsync(comId, bldId, fl, st, context.EffectiveRole, ct);
                        return MakeResult(new { buildingId = bldId, floors = updated.FloorsCount, stairwells = updated.StairwellsCount },
                            $"Resized building structure to {fl} floors, {st} stairwells");
                    }
                    var stackCode = args.GetValueOrDefault("stackCode") ?? "STACK-A1";
                    var loc = args.GetValueOrDefault("shutoffLocation") ?? "Basement Main Lever";
                    var stack = await workspace.UpsertRiserStackAsync(comId, bldId, stackCode, loc, context.EffectiveRole, ct);
                    return MakeResult(new { stackId = stack.Id, buildingId = bldId, stackCode = stack.Code, shutoff = stack.ShutoffValveLocation },
                        $"Upserted vertical riser stack '{stack.Code}' at {stack.ShutoffValveLocation}", $"/properties/{comId}/infrastructure");
                }

                // D20. create_unit_wizard
                case "create_unit_wizard":
                {
                    var bldId = Guid.TryParse(args.GetValueOrDefault("buildingId"), out var bid) ? bid : Guid.Empty;
                    var unitNum = (args.GetValueOrDefault("unitNumber") ?? "A-501").Trim();
                    if (unitNum.Length > 1 && unitNum.All(char.IsDigit))
                    {
                        var bldName = args.GetValueOrDefault("newBuildingName") ?? "Building A";
                        var letter = bldName.Trim().ToUpperInvariant() switch
                        {
                            var n when n.Contains("BUILDING B") || n.Contains(" B ") || n.EndsWith(" B") => "B",
                            var n when n.Contains("BUILDING C") || n.Contains(" C ") || n.EndsWith(" C") => "C",
                            _ => "A"
                        };
                        unitNum = $"{letter}-{unitNum}";
                    }
                    var req = new UnitCreationWizardRequest(
                        CommunityId: comId,
                        BuildingId: bldId,
                        NewBuildingName: bldId == Guid.Empty ? args.GetValueOrDefault("newBuildingName") ?? "Building A" : null,
                        UnitNumber: unitNum,
                        FloorNumber: int.TryParse(args.GetValueOrDefault("floorNumber"), out var fl) ? fl : 1,
                        StairwellNumber: int.TryParse(args.GetValueOrDefault("stairwellNumber"), out var st) ? st : 1,
                        VerticalStackCode: args.GetValueOrDefault("verticalStackCode") ?? "STACK-A1",
                        Kind: ParseEnum(args.GetValueOrDefault("kind"), UnitKind.Apartment),
                        FloorAreaM2: ParseDecimal(args.GetValueOrDefault("floorAreaM2")) ?? 60.0m,
                        StorageAreaM2: ParseDecimal(args.GetValueOrDefault("storageAreaM2")) ?? 3.5m,
                        RoomsCount: int.TryParse(args.GetValueOrDefault("roomsCount"), out var rc) ? rc : 3,
                        BathroomsCount: int.TryParse(args.GetValueOrDefault("bathroomsCount"), out var bc) ? bc : 1,
                        OccupancyCapacity: int.TryParse(args.GetValueOrDefault("occupancyCapacity"), out var oc) ? oc : 4,
                        OccupancyStatus: ParseEnum(args.GetValueOrDefault("occupancyStatus"), UnitOccupancyStatus.Vacant),
                        ShareNumerator: int.TryParse(args.GetValueOrDefault("shareNumerator"), out var sn) ? sn : 625,
                        ShareDenominator: int.TryParse(args.GetValueOrDefault("shareDenominator"), out var sd) ? sd : 10000,
                        ParkingBayNumber: args.GetValueOrDefault("parkingBayNumber"),
                        AccessFobCode: args.GetValueOrDefault("accessFobCode"),
                        VirtualIbanOrLockboxCode: args.GetValueOrDefault("virtualIban"),
                        Notes: args.GetValueOrDefault("notes") ?? "Created via Muse Spark AI Agent",
                        OwnerFullName: args.GetValueOrDefault("ownerFullName") ?? "Jan Nowak",
                        OwnerEmail: args.GetValueOrDefault("ownerEmail") ?? "owner@example.local",
                        OwnerPhone: args.GetValueOrDefault("ownerPhone") ?? "+48 600 000 000",
                        OwnershipRole: UnitOwnershipRole.PrimaryOwner,
                        OwnershipPercentage: 100m,
                        CoOwnerName: null,
                        VotingProxyName: null,
                        ResidentAssignmentOption: bool.TryParse(args.GetValueOrDefault("assignResident"), out var ar) && ar
                            ? WizardResidentAssignmentOption.CreateAndAssignNewResident
                            : WizardResidentAssignmentOption.None,
                        ExistingResidentProfileId: null,
                        NewResidentFirstName: args.GetValueOrDefault("newResidentFirstName") ?? "Jan",
                        NewResidentLastName: args.GetValueOrDefault("newResidentLastName") ?? "Nowak",
                        NewResidentEmail: args.GetValueOrDefault("newResidentEmail") ?? "jan.nowak@example.local",
                        NewResidentPhone: args.GetValueOrDefault("newResidentPhone") ?? "+48 600 000 000",
                        NewResidentPreferredLanguage: args.GetValueOrDefault("preferredLanguage") ?? "EN",
                        AssignmentType: ResidentAssignmentType.Tenant,
                        MoveInDateUtc: DateTime.UtcNow,
                        CreateRegistrationLinkAfterUnitCreation: bool.TryParse(args.GetValueOrDefault("createRegistrationLink"), out var crl) && crl,
                        RegistrationApprovalMode: ParseEnum(args.GetValueOrDefault("registrationApprovalMode"), RegistrationApprovalMode.AutomaticAuthorized));

                    var result = await workspace.CreateUnitFromWizardAsync(req, context.EffectiveRole, context.FullName, context.ActiveMembershipId, ct);
                    return MakeResult(new
                    {
                        unitId = result.Unit.Id,
                        unitNumber = result.Unit.UnitNumber,
                        buildingId = result.Building.Id,
                        buildingName = result.Building.Name,
                        ownershipId = result.Ownership?.Id,
                        residentProfileId = result.Resident?.Id,
                        residentAccountNumber = result.Resident?.ResidentAccountNumber,
                        assignmentId = result.Assignment?.Id,
                        plainToken = result.GeneratedRegistrationTokenPlaintext,
                        registrationUrl = result.GeneratedRegistrationTokenPlaintext != null ? $"/register-resident/{result.GeneratedRegistrationTokenPlaintext}" : null
                    }, $"Created Unit {result.Unit.UnitNumber} with 5-step wizard in {result.Building.Name}", $"/properties/{comId}/units/{result.Unit.Id}");
                }

                // D21. update_unit_details
                case "update_unit_details":
                {
                    var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : Guid.Empty;
                    string? requestedNumber = args.GetValueOrDefault("unitNumber");
                    if (unitId != Guid.Empty && string.IsNullOrWhiteSpace(requestedNumber))
                    {
                        await using (var numDb = await dbFactory.CreateDbContextAsync(ct))
                        {
                            requestedNumber = await numDb.Units.AsNoTracking()
                                .Where(u => u.Id == unitId && u.CommunityId == comId)
                                .Select(u => u.UnitNumber)
                                .FirstOrDefaultAsync(ct);
                        }
                    }
                    var input = new UnitUpdateInput(
                        BuildingId: Guid.TryParse(args.GetValueOrDefault("buildingId"), out var bid) ? bid : Guid.Empty,
                        UnitNumber: string.IsNullOrWhiteSpace(requestedNumber) ? "101" : requestedNumber.Trim(),
                        FloorNumber: int.TryParse(args.GetValueOrDefault("floorNumber"), out var fl) ? fl : 1,
                        StairwellNumber: int.TryParse(args.GetValueOrDefault("stairwellNumber"), out var st) ? st : 1,
                        VerticalStackCode: args.GetValueOrDefault("verticalStackCode") ?? "STACK-A1",
                        Kind: ParseEnum(args.GetValueOrDefault("kind"), UnitKind.Apartment),
                        FloorAreaM2: ParseDecimal(args.GetValueOrDefault("floorAreaM2")) ?? 55m,
                        StorageAreaM2: ParseDecimal(args.GetValueOrDefault("storageAreaM2")) ?? 2m,
                        RoomsCount: int.TryParse(args.GetValueOrDefault("roomsCount"), out var rc) ? rc : 2,
                        BathroomsCount: int.TryParse(args.GetValueOrDefault("bathroomsCount"), out var bc) ? bc : 1,
                        OccupancyCapacity: int.TryParse(args.GetValueOrDefault("occupancyCapacity"), out var oc) ? oc : 3,
                        OccupancyStatus: ParseEnum(args.GetValueOrDefault("occupancyStatus"), UnitOccupancyStatus.OwnerOccupied),
                        ShareNumerator: int.TryParse(args.GetValueOrDefault("shareNumerator"), out var sn) ? sn : 550,
                        ShareDenominator: int.TryParse(args.GetValueOrDefault("shareDenominator"), out var sd) ? sd : 10000,
                        PrimaryOwnerName: args.GetValueOrDefault("primaryOwnerName") ?? "Owner",
                        PrimaryOwnerEmail: args.GetValueOrDefault("primaryOwnerEmail") ?? "owner@example.local",
                        PrimaryOwnerPhone: args.GetValueOrDefault("primaryOwnerPhone") ?? "+48 600 000 000",
                        PreferredLanguage: args.GetValueOrDefault("preferredLanguage") ?? "EN",
                        CoOwnerName: args.GetValueOrDefault("coOwnerName"),
                        VotingProxyName: args.GetValueOrDefault("votingProxyName"),
                        ParkingBayNumber: args.GetValueOrDefault("parkingBayNumber"),
                        AccessFobCode: args.GetValueOrDefault("accessFobCode"),
                        VirtualIbanOrLockboxCode: args.GetValueOrDefault("virtualIban") ?? "LOCK-101",
                        Notes: args.GetValueOrDefault("notes") ?? string.Empty);

                    var updated = await workspace.UpdateUnitFullDetailsAsync(comId, unitId, input, context.EffectiveRole, ct);
                    return MakeResult(new { unitId = updated.Id, unitNumber = updated.UnitNumber, floorArea = updated.FloorAreaM2 },
                        $"Updated details for Unit {updated.UnitNumber}", $"/properties/{comId}/units/{updated.Id}");
                }

                // D22. bulk_generate_floor_units
                case "bulk_generate_floor_units":
                {
                    var bldId = Guid.TryParse(args.GetValueOrDefault("buildingId"), out var bid) ? bid : Guid.Empty;
                    var sf = int.TryParse(args.GetValueOrDefault("startFloor"), out var s) ? s : 1;
                    var ef = int.TryParse(args.GetValueOrDefault("endFloor"), out var e) ? e : 3;
                    var upf = int.TryParse(args.GetValueOrDefault("unitsPerFloor"), out var u) ? u : 4;
                    var area = ParseDecimal(args.GetValueOrDefault("typicalFloorAreaM2")) ?? 58.0m;
                    var prefix = args.GetValueOrDefault("stackPrefix") ?? "STACK-A";

                    var created = new List<Unit>();
                    for (int f = sf; f <= ef; f++)
                    {
                        for (int uIdx = 1; uIdx <= upf; uIdx++)
                        {
                            var unitNumber = $"{prefix.Replace("STACK-", "")}-{f}{uIdx:D2}";
                            var stackCode = $"{prefix}{((uIdx % 2) + 1)}";
                            var unit = await workspace.UpsertUnitCellAsync(comId, bldId, f, stackCode, unitNumber, area, context.EffectiveRole, ct);
                            created.Add(unit);
                        }
                    }
                    return MakeResult(new
                    {
                        createdUnitsCount = created.Count,
                        buildingId = bldId,
                        units = created.Select(c => c.UnitNumber).ToList()
                    }, $"Bulk-generated {created.Count} units across floors {sf}-{ef}", $"/properties/{comId}/units");
                }

                // D23. delete_unit
                case "delete_unit":
                {
                    var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : Guid.Empty;
                    await workspace.RemoveUnitCellAsync(comId, unitId, context.EffectiveRole, ct);
                    return MakeResult(new { deletedUnitId = unitId, status = "Removed" }, $"Deleted unit {unitId}");
                }

                // D23b. delete_building
                case "delete_building":
                {
                    var target = await ResolveBuildingDeleteTargetAsync(args, context, comId, ct);
                    if (!target.IsResolved)
                    {
                        return target.Clarification!;
                    }

                    var confirmed = bool.TryParse(args.GetValueOrDefault("confirm"), out var c) && c;
                    if (!confirmed)
                    {
                        return MakeClarification(
                            $"Building '{target.BuildingName}' requires explicit confirmation before deletion.",
                            $"Delete building '{target.BuildingName}' ({target.UnitCount} units)? Buildings with units cannot be deleted. Reply with confirm=true to delete this empty building.",
                            [$"{target.BuildingName}"],
                            "confirm",
                            resolvedBuildingId: target.BuildingId,
                            resolvedBuildingName: target.BuildingName);
                    }

                    await workspace.DeleteBuildingAsync(target.CommunityId, target.BuildingId, context.EffectiveRole, false, ct);
                    return MakeResult(new { deletedBuildingId = target.BuildingId, buildingName = target.BuildingName, status = "Deleted" },
                        $"Deleted building '{target.BuildingName}'", $"/properties/{target.CommunityId}/infrastructure");
                }

                // E24. create_or_update_resident_profile
                case "create_or_update_resident_profile":
                {
                    var profIdStr = args.GetValueOrDefault("residentProfileId");
                    var fn = args.GetValueOrDefault("fullName");
                    string first = args.GetValueOrDefault("firstName") ?? string.Empty;
                    string last = args.GetValueOrDefault("lastName") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(fn) && string.IsNullOrWhiteSpace(first))
                    {
                        var parts = fn.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        first = parts[0];
                        last = parts.Length > 1 ? parts[1] : "Resident";
                    }
                    if (string.IsNullOrWhiteSpace(first)) first = "Resident";
                    if (string.IsNullOrWhiteSpace(last)) last = "User";

                    if (Guid.TryParse(profIdStr, out var pid))
                    {
                        var input = new ResidentProfileUpdateInput(
                            first,
                            last,
                            args.GetValueOrDefault("email") ?? "resident@example.local",
                            args.GetValueOrDefault("phone") ?? "+48 000 000 000",
                            null,
                            null,
                            string.Empty,
                            ResidentAccountStatus.Active,
                            ResidentVerificationStatus.Verified,
                            args.GetValueOrDefault("preferredLanguage") ?? "EN",
                            "Email",
                            args.GetValueOrDefault("emergencyContactName") ?? string.Empty,
                            args.GetValueOrDefault("emergencyContactPhone") ?? string.Empty,
                            args.GetValueOrDefault("emergencyContactRelation") ?? string.Empty,
                            args.GetValueOrDefault("notes") ?? string.Empty);
                        var updated = await workspace.UpdateResidentFullProfileAsync(pid, input, context.EffectiveRole, context.UserId, ct);
                        return MakeResult(new { residentProfileId = updated.Id, accountNumber = updated.ResidentAccountNumber, name = updated.FullName },
                            $"Updated resident profile {updated.FullName} ({updated.ResidentAccountNumber})", $"/admin/residents/{updated.Id}");
                    }

                    var requestedEmail = (args.GetValueOrDefault("email") ?? "resident@example.local").Trim();
                    // Only reuse the caller identity when creating the caller own profile.
                    // Manager-created profiles for other people must not inherit the manager UserId.
                    Guid? profileUserId = null;
                    if (!string.IsNullOrWhiteSpace(requestedEmail)
                        && !string.IsNullOrWhiteSpace(context.Email)
                        && string.Equals(requestedEmail, context.Email.Trim(), StringComparison.OrdinalIgnoreCase)
                        && context.UserId != Guid.Empty)
                    {
                        profileUserId = context.UserId;
                    }
                    var created = await workspace.CreateOrGetResidentProfileAsync(
                        context.ManagementFirmId,
                        profileUserId,
                        first,
                        last,
                        requestedEmail,
                        args.GetValueOrDefault("phone") ?? "+48 000 000 000",
                        args.GetValueOrDefault("preferredLanguage") ?? "EN",
                        args.GetValueOrDefault("emergencyContactName") ?? string.Empty,
                        args.GetValueOrDefault("emergencyContactPhone") ?? string.Empty,
                        args.GetValueOrDefault("notes") ?? string.Empty,
                        ct);

                    string assignmentNote = string.Empty;
                    await using (var db = await dbFactory.CreateDbContextAsync(ct))
                    {
                        Unit? foundUnit = null;
                        if (Guid.TryParse(args.GetValueOrDefault("unitId"), out var targetUid) && targetUid != Guid.Empty)
                        {
                            foundUnit = await db.Units.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetUid, ct);
                        }
                        else if (args.TryGetValue("unitNumber", out var unitNum) && !string.IsNullOrWhiteSpace(unitNum))
                        {
                            foundUnit = await db.Units.AsNoTracking().FirstOrDefaultAsync(x => x.CommunityId == comId && x.UnitNumber == unitNum.Trim(), ct);
                        }

                        if (foundUnit != null)
                        {
                            var req = new ManualResidentAssignmentRequest(
                                CommunityId: comId,
                                BuildingId: foundUnit.BuildingId,
                                UnitId: foundUnit.Id,
                                CreateNewResident: false,
                                ExistingResidentProfileId: created.Id,
                                FirstName: first,
                                LastName: last,
                                Email: args.GetValueOrDefault("email") ?? "resident@example.local",
                                Phone: args.GetValueOrDefault("phone") ?? "+48 000 000 000",
                                PreferredLanguage: args.GetValueOrDefault("preferredLanguage") ?? "EN",
                                EmergencyContactName: string.Empty,
                                EmergencyContactPhone: string.Empty,
                                AssignmentType: ResidentAssignmentType.Tenant,
                                Status: UnitAssignmentStatus.Active,
                                IsPrimaryResident: true,
                                MoveInDateUtc: DateTime.UtcNow,
                                MoveOutDateUtc: null,
                                AssignmentReasonOrNotes: "Assigned via AI Spark Resident Wizard",
                                ConflictOverrideReason: null);
                            await workspace.AssignResidentToUnitManualAsync(req, context.EffectiveRole, context.FullName, context.ActiveMembershipId, ct);
                            assignmentNote = $" and linked to unit {foundUnit.UnitNumber}";
                        }
                    }

                    return MakeResult(new { residentProfileId = created.Id, accountNumber = created.ResidentAccountNumber, name = created.FullName },
                        $"Created resident profile {created.FullName} ({created.ResidentAccountNumber}){assignmentNote}", $"/admin/residents/{created.Id}");
                }

                // E25. assign_resident_to_unit
                case "assign_resident_to_unit":
                {
                    var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : Guid.Empty;
                    var bldId = Guid.TryParse(args.GetValueOrDefault("buildingId"), out var bid) ? bid : Guid.Empty;
                    var req = new ManualResidentAssignmentRequest(
                        CommunityId: comId,
                        BuildingId: bldId,
                        UnitId: unitId,
                        CreateNewResident: bool.TryParse(args.GetValueOrDefault("createNewResident"), out var cnr) && cnr || !args.ContainsKey("residentProfileId"),
                        ExistingResidentProfileId: Guid.TryParse(args.GetValueOrDefault("residentProfileId"), out var rpid) ? rpid : null,
                        FirstName: args.GetValueOrDefault("firstName") ?? "Jan",
                        LastName: args.GetValueOrDefault("lastName") ?? "Nowak",
                        Email: args.GetValueOrDefault("email") ?? "jan.nowak@example.local",
                        Phone: args.GetValueOrDefault("phone") ?? "+48 600 000 000",
                        PreferredLanguage: args.GetValueOrDefault("preferredLanguage") ?? "EN",
                        EmergencyContactName: args.GetValueOrDefault("emergencyContactName") ?? string.Empty,
                        EmergencyContactPhone: args.GetValueOrDefault("emergencyContactPhone") ?? string.Empty,
                        AssignmentType: ParseEnum(args.GetValueOrDefault("assignmentType"), ResidentAssignmentType.Tenant),
                        Status: UnitAssignmentStatus.Active,
                        IsPrimaryResident: !bool.TryParse(args.GetValueOrDefault("isPrimaryResident"), out var ipr) || ipr,
                        MoveInDateUtc: DateTime.UtcNow,
                        MoveOutDateUtc: null,
                        AssignmentReasonOrNotes: "Assigned via Muse Spark Agent",
                        ConflictOverrideReason: args.GetValueOrDefault("conflictOverrideReason"));

                    var result = await workspace.AssignResidentToUnitManualAsync(req, context.EffectiveRole, context.FullName, context.ActiveMembershipId, ct);
                    string? assignedUnitNumber = null;
                    string? assignedBuildingName = null;
                    Guid assignedBuildingId = result.BuildingId;
                    await using (var assignmentDb = await dbFactory.CreateDbContextAsync(ct))
                    {
                        var assignedUnit = await assignmentDb.Units.AsNoTracking()
                            .Where(u => u.Id == result.UnitId && u.CommunityId == comId)
                            .Select(u => new
                            {
                                u.UnitNumber,
                                u.BuildingId,
                                BuildingName = assignmentDb.Buildings.Where(b => b.Id == u.BuildingId).Select(b => b.Name).FirstOrDefault()
                            })
                            .FirstOrDefaultAsync(ct);
                        assignedUnitNumber = assignedUnit?.UnitNumber;
                        assignedBuildingName = assignedUnit?.BuildingName;
                        if (assignedBuildingId == Guid.Empty && assignedUnit is not null)
                        {
                            assignedBuildingId = assignedUnit.BuildingId;
                        }
                    }
                    return MakeResult(new
                    {
                        assignmentId = result.Id,
                        unitId = result.UnitId,
                        unitNumber = assignedUnitNumber,
                        buildingId = assignedBuildingId,
                        buildingName = assignedBuildingName,
                        residentProfileId = result.ResidentProfileId,
                        status = result.Status.ToString()
                    }, $"Assigned resident to Unit {assignedUnitNumber ?? result.UnitId.ToString()} in {assignedBuildingName ?? "the selected building"} (Status: {result.Status})", $"/properties/{comId}/units/{result.UnitId}");
                }

                // E26. execute_resident_move_out
                case "execute_resident_move_out":
                {
                    var assignId = Guid.TryParse(args.GetValueOrDefault("assignmentId"), out var aid) ? aid : Guid.Empty;
                    var ended = await workspace.ExecuteMoveOutAsync(comId, assignId, DateTime.UtcNow, args.GetValueOrDefault("reason") ?? "End of lease", context.EffectiveRole, context.FullName, ct);
                    return MakeResult(new { assignmentId = ended.Id, residentId = ended.ResidentProfileId, status = "Ended", moveOutDateUtc = ended.MoveOutDateUtc },
                        $"Executed move-out for assignment {ended.Id}");
                }

                // E27. generate_registration_link
                case "generate_registration_link":
                {
                    var target = await ResolveRegistrationTargetAsync(args, context, comId, ct);
                    if (!target.IsResolved)
                    {
                        return target.Clarification!;
                    }

                    var mode = ParseEnum(args.GetValueOrDefault("approvalMode"), RegistrationApprovalMode.AutomaticAuthorized);
                    var linkGen = await workspace.GenerateResidentRegistrationLinkAsync(
                        target.CommunityId,
                        target.UnitId,
                        mode,
                        ParseEnum(args.GetValueOrDefault("intendedAssignmentType"), ResidentAssignmentType.Tenant),
                        DateTime.UtcNow.AddDays(int.TryParse(args.GetValueOrDefault("expiresInDays"), out var exp) ? exp : 14),
                        1,
                        args.GetValueOrDefault("labelOrReference") ?? "Registration Token",
                        context.EffectiveRole,
                        context.FullName,
                        context.ActiveMembershipId,
                        ct);
                    // Defense-in-depth: never return an empty or Guid.Empty deep link.
                    if (linkGen.TokenRecord is null
                        || linkGen.TokenRecord.Id == Guid.Empty
                        || linkGen.TokenRecord.TargetUnitId == Guid.Empty
                        || linkGen.TokenRecord.BuildingId == Guid.Empty
                        || linkGen.TokenRecord.CommunityId == Guid.Empty
                        || string.IsNullOrWhiteSpace(linkGen.PlaintextToken)
                        || string.IsNullOrWhiteSpace(linkGen.RelativeUrl))
                    {
                        return MakeClarification(
                            "Registration-link generation returned an empty token, unit, building, or URL.",
                            "The registration link could not be generated because the generated token or target ID was empty. Which building and unit should receive the link? Please confirm the exact building and unit before retrying.",
                            [$"{target.BuildingName} — Unit {target.UnitNumber}"],
                            "unitId",
                            target.UnitId,
                            target.UnitNumber,
                            target.BuildingId,
                            target.BuildingName);
                    }

                    return MakeResult(new
                    {
                        tokenId = linkGen.TokenRecord.Id,
                        communityId = target.CommunityId,
                        buildingId = target.BuildingId,
                        buildingName = target.BuildingName,
                        unitId = target.UnitId,
                        unitNumber = target.UnitNumber,
                        plainToken = linkGen.PlaintextToken,
                        registrationUrl = linkGen.RelativeUrl,
                        approvalMode = linkGen.TokenRecord.ApprovalMode.ToString(),
                        expiresAtUtc = linkGen.TokenRecord.ExpiresAtUtc
                    }, $"Generated Mode {(mode == RegistrationApprovalMode.AutomaticAuthorized ? "A" : "B")} registration link for Unit {target.UnitNumber} in {target.BuildingName}: {linkGen.RelativeUrl}", linkGen.RelativeUrl);
                }

                // E28. revoke_registration_link
                case "revoke_registration_link":
                {
                    var tokId = Guid.TryParse(args.GetValueOrDefault("tokenId"), out var tid) ? tid : Guid.Empty;
                    await workspace.RevokeResidentRegistrationLinkAsync(comId, tokId, context.EffectiveRole, ct);
                    return MakeResult(new { tokenId = tokId, status = "Revoked" }, $"Revoked registration link {tokId}");
                }

                // E29. review_registration_request
                case "review_registration_request":
                {
                    var reqId = Guid.TryParse(args.GetValueOrDefault("requestId"), out var rid) ? rid : Guid.Empty;
                    var decision = (args.GetValueOrDefault("decision") ?? "approve").ToLowerInvariant();
                    var req = await workspace.ReviewRegistrationRequestAsync(
                        reqId,
                        decision == "approve",
                        comId,
                        Guid.TryParse(args.GetValueOrDefault("overrideUnitId"), out var ouid) ? ouid : null,
                        null,
                        ParseEnum(args.GetValueOrDefault("overrideAssignmentType"), ResidentAssignmentType.Tenant),
                        args.GetValueOrDefault("decisionNotes") ?? "Reviewed via Muse Spark AI Agent",
                        context.EffectiveRole,
                        context.FullName,
                        context.ActiveMembershipId,
                        ct);
                    return MakeResult(new
                    {
                        requestId = req.Id,
                        status = req.Status.ToString()
                    }, $"Reviewed registration request {reqId} -> {req.Status}", $"/admin/members");
                }

                // E30. upsert_or_remove_household_member
                case "upsert_or_remove_household_member":
                {
                    var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : Guid.Empty;
                    var action = (args.GetValueOrDefault("action") ?? "upsert").ToLowerInvariant();
                    if (action == "remove")
                    {
                        var hmid = Guid.TryParse(args.GetValueOrDefault("householdMemberId"), out var hid) ? hid : Guid.Empty;
                        await workspace.RemoveHouseholdMemberAsync(comId, hmid, context.EffectiveRole, ct);
                        return MakeResult(new { householdMemberId = hmid, status = "Removed" }, $"Removed household member {hmid}");
                    }

                    var member = await workspace.UpsertHouseholdMemberAsync(
                        comId,
                        unitId,
                        Guid.TryParse(args.GetValueOrDefault("householdMemberId"), out var hid2) ? hid2 : null,
                        args.GetValueOrDefault("fullName") ?? "Family Member",
                        args.GetValueOrDefault("relationship") ?? "Spouse",
                        args.GetValueOrDefault("phone") ?? string.Empty,
                        args.GetValueOrDefault("email") ?? string.Empty,
                        bool.TryParse(args.GetValueOrDefault("hasPortalAccess"), out var pa) && pa,
                        args.GetValueOrDefault("notes") ?? string.Empty,
                        context.EffectiveRole,
                        ct);
                    return MakeResult(new { memberId = member.Id, fullName = member.FullName, relation = member.RelationshipToPrimary },
                        $"Upserted household member {member.FullName} ({member.RelationshipToPrimary})");
                }

                // E31. add_resident_document
                case "add_resident_document":
                {
                    var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : (Guid?)null;
                    var cat = ParseEnum(args.GetValueOrDefault("category"), ResidentDocumentCategory.Other);
                    var doc = await workspace.AddResidentDocumentAsync(
                        comId,
                        unitId,
                        Guid.TryParse(args.GetValueOrDefault("residentProfileId"), out var rpid) ? rpid : null,
                        cat,
                        args.GetValueOrDefault("title") ?? "Document Vault Entry",
                        args.GetValueOrDefault("fileName") ?? "document.pdf",
                        args.GetValueOrDefault("summaryOrNotes") ?? "Vaulted via Agent",
                        !bool.TryParse(args.GetValueOrDefault("isVisibleToResident"), out var vr) || vr,
                        context.FullName,
                        context.EffectiveRole,
                        ct);
                    return MakeResult(new { documentId = doc.Id, title = doc.Title, category = doc.Category.ToString() },
                        $"Vaulted resident document '{doc.Title}'");
                }

                // F32. submit_meter_reading
                case "submit_meter_reading":
                {
                    var overrideVal = ParseDecimal(args.GetValueOrDefault("simulatedReadingOverride")) ?? 142.50m;
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    var unitIds = await db.Units.AsNoTracking().Where(u => u.CommunityId == comId).Select(u => u.Id).ToListAsync(ct);
                    UtilityMeter? meter = null;
                    if (Guid.TryParse(args.GetValueOrDefault("meterId"), out var mid))
                    {
                        meter = await db.UtilityMeters.FirstOrDefaultAsync(m => m.Id == mid, ct);
                    }
                    if (meter == null && unitIds.Count > 0)
                    {
                        meter = await db.UtilityMeters.FirstOrDefaultAsync(m => unitIds.Contains(m.UnitId), ct);
                    }
                    if (meter == null)
                    {
                        var targetUnitId = unitIds.Count > 0 ? unitIds[0] : Guid.NewGuid();
                        meter = new UtilityMeter
                        {
                            Id = Guid.NewGuid(),
                            UnitId = targetUnitId,
                            SerialNumber = "MTR-A501-CW-2026",
                            Kind = UtilityKind.ColdWater
                        };
                        db.UtilityMeters.Add(meter);
                        await db.SaveChangesAsync(ct);
                    }

                    var reading = await workspace.SubmitPhotoOrVoiceMeterReadingAsync(comId, meter.Id, overrideVal, ct);
                    return MakeResult(new
                    {
                        readingId = reading.Id,
                        meterId = meter.Id,
                        serialNumber = meter.SerialNumber,
                        readingValue = reading.ReadingValue,
                        isAnomaly = reading.IsAnomaly,
                        anomalyReason = reading.AnomalyReason
                    }, $"Submitted reading {reading.ReadingValue:N2} for meter {meter.SerialNumber}{(reading.IsAnomaly ? " [ANOMALY DETECTED]" : "")}");
                }

                // F33. run_bulk_utility_settlement
                case "run_bulk_utility_settlement":
                {
                    var policy = ParseEnum(args.GetValueOrDefault("policy"), UtilityAllocationPolicy.SubMeterPlusShareLoss);
                    var period = await workspace.RunBulkUtilitySettlementAsync(comId, policy, ct);
                    return MakeResult(new
                    {
                        periodId = period.Id,
                        utilityKind = period.UtilityKind.ToString(),
                        policy = policy.ToString(),
                        masterCost = period.MasterInvoiceTotalCost,
                        subMeterTotal = period.SubMeterTotalConsumption,
                        lossConsumption = period.UnallocatedCommonLossConsumption
                    }, $"Executed bulk utility settlement ({policy}) (Master Total: {period.MasterInvoiceTotalCost:N2})", $"/financials/utilities");
                }

                // F34. match_bank_transfers
                case "match_bank_transfers":
                {
                    var matched = await workspace.AutoMatchBankTransfersAsync(comId, ct);
                    return MakeResult(new
                    {
                        matchedTransactionsCount = matched.Count,
                        matches = matched.Select(m => new { id = m.Id, amount = m.Amount, payer = m.PayerName, unit = m.MatchedUnitNumber }).ToList()
                    }, $"Resolved {matched.Count} bank transfers and allocated FIFO payments to unit ledgers", $"/financials/transfers");
                }

                // F35. run_inflation_and_power_audit
                case "run_inflation_and_power_audit":
                {
                    var sim = await workspace.RunInflationSimulationAsync(
                        comId,
                        ParseDecimal(args.GetValueOrDefault("heatingInflationPct")) ?? 12m,
                        ParseDecimal(args.GetValueOrDefault("electricityInflationPct")) ?? 8m,
                        ParseDecimal(args.GetValueOrDefault("wageInflationPct")) ?? 6m,
                        ParseDecimal(args.GetValueOrDefault("repairInflationPct")) ?? 10m,
                        ct);
                    var powerResult = await workspace.RunContractedPowerAuditAsync(comId, ct);
                    return MakeResult(new
                    {
                        averageIncreasePct = sim.AverageIncreasePct,
                        exceeds20PercentCap = sim.ExceedsUsHoaStatutory20PercentCap,
                        affectedUnitsCount = sim.UnitDeltas.Count,
                        powerAuditSummary = powerResult.Summary
                    }, $"Inflation simulation average increase {sim.AverageIncreasePct:N1}% (Cap exceeded: {sim.ExceedsUsHoaStatutory20PercentCap}). Power audit: {powerResult.Summary}");
                }

                // F36. generate_czynsz_assessment
                case "generate_czynsz_assessment":
                {
                    var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var cuid) ? cuid : Guid.Empty;
                    var unitNumber = args.GetValueOrDefault("unitNumber") ?? string.Empty;
                    var period = (args.GetValueOrDefault("period") ?? DateTime.UtcNow.ToString("yyyy-MM")).Trim();
                    if (unitId == Guid.Empty && !string.IsNullOrWhiteSpace(unitNumber))
                    {
                        await using var lookupDb = await dbFactory.CreateDbContextAsync(ct);
                        unitId = await lookupDb.Units.AsNoTracking()
                            .Where(u => u.CommunityId == comId
                                && u.UnitNumber.ToUpper() == unitNumber.Trim().ToUpperInvariant())
                            .Select(u => u.Id)
                            .FirstOrDefaultAsync(ct);
                    }

                    if (unitId == Guid.Empty)
                    {
                        return MakeError("Valid unitId or unitNumber is required for generate_czynsz_assessment.");
                    }

                    var czynszLines = CommonGroundWorkspaceService.RecomputeCzynszLines(
                        ParseCzynszLines(args.GetValueOrDefault("lines")));
                    var saved = await workspace.SaveCzynszAssessmentAsync(
                        comId, unitId, period, czynszLines, context.EffectiveRole, ct);
                    return MakeResult(new
                    {
                        assessmentId = saved.Id,
                        unitId = saved.UnitId,
                        unitNumber = saved.UnitNumber,
                        period = saved.Period,
                        operatingFee = saved.OperatingFee,
                        repairFundFee = saved.RepairFundFee,
                        wasteFee = saved.WasteFee,
                        coldWaterAdvance = saved.ColdWaterAdvance,
                        hotWaterAdvance = saved.HotWaterAdvance,
                        heatingAdvance = saved.HeatingAdvance,
                        commercialRevenueCredit = saved.CommercialRevenueCredit,
                        total = saved.TotalAssessment
                    }, $"Generated czynsz assessment for unit {saved.UnitNumber} period {saved.Period}: total {saved.TotalAssessment:N2}", $"/properties/{comId}/financials");
                }

                // F37. generate_arrears_or_mediation_action
                case "generate_arrears_or_mediation_action":
                {
                    var actionType = (args.GetValueOrDefault("actionType") ?? "arrears_action").ToLowerInvariant();
                    if (actionType.Contains("arrears"))
                    {
                        var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : Guid.Empty;
                        if (unitId == Guid.Empty)
                        {
                            await using var db = await dbFactory.CreateDbContextAsync(ct);
                            var sampleUnit = await db.Units.FirstOrDefaultAsync(u => u.CommunityId == comId, ct);
                            unitId = sampleUnit?.Id ?? Guid.NewGuid();
                        }
                        var action = await workspace.GenerateArrearsOrLienActionAsync(comId, unitId, ct);
                        return MakeResult(new
                        {
                            actionType = "ArrearsRecoveryAction",
                            actionId = action.Id,
                            title = action.Title,
                            status = action.Status.ToString()
                        }, $"Generated arrears recovery action: '{action.Title}'", $"/approvals");
                    }

                    var reporter = args.GetValueOrDefault("unitReporter") ?? "101";
                    var subject = args.GetValueOrDefault("unitSubject") ?? "102";
                    var issue = args.GetValueOrDefault("issueDescription") ?? "Late night noise during quiet hours.";
                    var notice = await workspace.DraftNeighborDisputeMediationAsync(comId, reporter, subject, issue, ct);
                    return MakeResult(new
                    {
                        actionType = "NeighborDisputeMediation",
                        reporterUnit = reporter,
                        subjectUnit = subject,
                        noticeText = notice
                    }, $"Drafted House Rules neighbor mediation notice for Unit {reporter} vs {subject}");
                }

                // G37. transcribe_voice_or_audio
                case "transcribe_voice_or_audio":
                {
                    var isMeeting = (args.GetValueOrDefault("audioKind") ?? string.Empty).Equals("meeting", StringComparison.OrdinalIgnoreCase);
                    var preset = args.GetValueOrDefault("voiceScenarioPreset") ?? args.GetValueOrDefault("audioPresetOrInput") ?? string.Empty;
                    if (isMeeting)
                    {
                        await using var db = await dbFactory.CreateDbContextAsync(ct);
                        var comm = await db.Communities.FirstOrDefaultAsync(c => c.Id == comId, ct) ?? new Community { Id = comId, Name = "Community" };
                        var (diarized, formalMinutes, actionItems, lat) = await voiceTranscriber.TranscribeAndSummarizeMeetingAsync(comm, preset, ct);
                        return MakeResult(new
                        {
                            audioKind = "meeting",
                            transcript = diarized,
                            formalMinutesMarkdown = formalMinutes,
                            actionItemsSummary = actionItems,
                            latencyMs = lat
                        }, $"Transcribed and diarized meeting recording via muse-voice-transcribe-1.0 ({lat} ms)");
                    }

                    var (transcript, lang, lat2) = await voiceTranscriber.TranscribeVoiceNoteAsync(preset, ct);
                    return MakeResult(new
                    {
                        audioKind = "voicenote",
                        transcript,
                        detectedLanguage = lang,
                        latencyMs = lat2
                    }, $"Transcribed voice note ({lang}): \"{transcript}\" ({lat2} ms)");
                }

                // G38. submit_maintenance_request
                case "submit_maintenance_request":
                {
                    var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : (Guid?)null;
                    var msg = args.GetValueOrDefault("messageText") ?? "Water leaking from bathroom ceiling.";
                    var lang = args.GetValueOrDefault("languageHint") ?? "EN";
                    var wo = await workspace.SubmitOmnichannelResidentRequestAsync(comId, unitId, msg, lang, null, ct);
                    return MakeResult(new
                    {
                        workOrderId = wo.Id,
                        title = wo.Title,
                        category = wo.Category.ToString(),
                        priority = wo.Priority.ToString(),
                        boundary = wo.Boundary.ToString(),
                        status = wo.Status.ToString(),
                        boqEstimate = wo.InvestorBoqEstimateAmount,
                        translatedReply = wo.TranslatedReplyToResident
                    }, $"Submitted maintenance ticket '{wo.Title}' [Boundary: {wo.Boundary}, BOQ: {wo.InvestorBoqEstimateAmount:N2}]", $"/properties/{comId}/maintenance");
                }

                // G38a. post_maintenance_message (simple forum reply with optional photo)
                case "post_maintenance_message":
                {
                    if (!Guid.TryParse(args.GetValueOrDefault("workOrderId"), out var threadId) || threadId == Guid.Empty)
                    {
                        return MakeError("Valid workOrderId is required.");
                    }
                    var body = args.GetValueOrDefault("body") ?? args.GetValueOrDefault("messageText") ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return MakeError("Message body is required.");
                    }
                    var posted = await workspace.PostWorkOrderMessageAsync(
                        comId,
                        threadId,
                        context.FullName,
                        context.EffectiveRole.ToString(),
                        body,
                        args.GetValueOrDefault("attachmentDataUrl"),
                        context.EffectiveRole,
                        ct);
                    return MakeResult(new
                    {
                        workOrderId = threadId,
                        messageId = posted.Id,
                        hasPhoto = !string.IsNullOrWhiteSpace(posted.AttachmentDataUrl),
                        sentAtUtc = posted.CreatedAtUtc
                    }, $"Posted maintenance thread message in {threadId}", $"/properties/{comId}/maintenance");
                }

                // G38b. assign_maintenance_contractor (manager converts forum thread to task)
                case "assign_maintenance_contractor":
                {
                    if (!Guid.TryParse(args.GetValueOrDefault("workOrderId"), out var assignThreadId) || assignThreadId == Guid.Empty)
                    {
                        return MakeError("Valid workOrderId is required.");
                    }
                    if (!Guid.TryParse(args.GetValueOrDefault("vendorId"), out var assignVendorId) || assignVendorId == Guid.Empty)
                    {
                        return MakeError("Valid vendorId is required.");
                    }
                    var assigned = await workspace.AssignWorkOrderVendorAsync(comId, assignThreadId, assignVendorId, context.EffectiveRole, ct);
                    return MakeResult(new
                    {
                        workOrderId = assigned.Id,
                        title = assigned.Title,
                        vendorId = assigned.AssignedVendorId,
                        vendorName = assigned.AssignedVendorName,
                        status = assigned.Status.ToString()
                    }, $"Assigned contractor {assigned.AssignedVendorName} to '{assigned.Title}'", $"/properties/{comId}/maintenance");
                }

                // G39. run_vision_inspection_or_verify_repair
                case "run_vision_inspection_or_verify_repair":
                {
                    var kind = (args.GetValueOrDefault("inspectionKind") ?? "sam_repair_verification").ToLowerInvariant();
                    var woId = Guid.TryParse(args.GetValueOrDefault("workOrderId"), out var wid) ? wid : Guid.Empty;
                    if (woId == Guid.Empty)
                    {
                        await using var db = await dbFactory.CreateDbContextAsync(ct);
                        var sampleWo = await db.WorkOrders.FirstOrDefaultAsync(w => w.CommunityId == comId, ct);
                        woId = sampleWo?.Id ?? Guid.NewGuid();
                    }

                    if (kind.Contains("walkthrough"))
                    {
                        var createdWos = await workspace.RunWalkthroughVideoSplitAsync(comId, ct);
                        return MakeResult(new { inspectionKind = "WalkthroughVideoSplit", createdWorkOrdersCount = createdWos.Count },
                            $"Walkthrough inspection segmented into {createdWos.Count} work orders", $"/properties/{comId}/maintenance");
                    }
                    if (kind.Contains("safety"))
                    {
                        var safetyWo = await workspace.RunSiteSafetyVisionMonitorAsync(comId, ct);
                        return MakeResult(new { inspectionKind = "SiteSafetyScan", workOrderId = safetyWo.Id, title = safetyWo.Title },
                            $"Site safety scan completed: '{safetyWo.Title}'");
                    }
                    if (kind.Contains("claim"))
                    {
                        var dossier = await workspace.GenerateInsuranceClaimDossierAsync(comId, woId, ct);
                        return MakeResult(new { inspectionKind = "InsuranceClaimDossier", dossierId = dossier.Id, claimRef = dossier.ClaimReference },
                            $"Packaged Insurance Claim Dossier {dossier.ClaimReference}", $"/api/export/claim-dossier/{woId}");
                    }

                    var verifiedWo = await workspace.VerifyContractorRepairPhotoAsync(comId, woId, ct);
                    return MakeResult(new
                    {
                        inspectionKind = "SamRepairVerification",
                        workOrderId = verifiedWo.Id,
                        status = verifiedWo.Status.ToString(),
                        remainingDefectArea = verifiedWo.AfterDefectAreaM2,
                        isVerified = verifiedWo.IsSamVerified
                    }, $"SAM 3.1 Repair Verification: defect remaining {verifiedWo.AfterDefectAreaM2:N2} m2 -> {(verifiedWo.IsSamVerified ? "VERIFIED AND CLOSED" : "DEFECT STILL PRESENT")}", $"/properties/{comId}/maintenance");
                }

                // G40. evaluate_or_submit_modification_request
                case "evaluate_or_submit_modification_request":
                {
                    await using var db = await dbFactory.CreateDbContextAsync(ct);
                    ModificationOrArcRequest? req = null;
                    if (Guid.TryParse(args.GetValueOrDefault("requestId"), out var rid))
                    {
                        req = await db.ModificationRequests.FirstOrDefaultAsync(m => m.Id == rid, ct);
                    }
                    if (req == null)
                    {
                        req = new ModificationOrArcRequest
                        {
                            Id = Guid.NewGuid(),
                            CommunityId = comId,
                            UnitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : Guid.NewGuid(),
                            Kind = ParseEnum(args.GetValueOrDefault("kind"), ModificationRequestKind.FacadeAirConditioner),
                            Description = args.GetValueOrDefault("description") ?? "Installation of outdoor split AC unit on balcony facade.",
                            SubmittedAt = DateTime.UtcNow
                        };
                        db.ModificationRequests.Add(req);
                        await db.SaveChangesAsync(ct);
                    }

                    var evaluated = await workspace.EvaluateModificationOrArcRequestAsync(comId, req.Id, ct);
                    return MakeResult(new
                    {
                        requestId = evaluated.Id,
                        status = evaluated.Status,
                        aiRecommendation = evaluated.AiRecommendation,
                        statutoryProtectionNote = evaluated.StatutoryProtectionNote,
                        citedStatute = evaluated.CitedLegalDocumentCode
                    }, $"Evaluated renovation/ARC request: {evaluated.AiRecommendation} ({evaluated.CitedLegalDocumentCode})");
                }

                // G41. run_governance_and_voting_workflow
                case "run_governance_and_voting_workflow":
                {
                    var workflowTypeRaw = args.GetValueOrDefault("workflowType");
                    if (string.IsNullOrWhiteSpace(workflowTypeRaw) && string.Equals(toolName?.Trim(), "upload_ballot_ocr", StringComparison.OrdinalIgnoreCase))
                    {
                        workflowTypeRaw = "digitize";
                    }
                    var workflowType = (workflowTypeRaw ?? "meeting_autopilot").ToLowerInvariant();
                    if (workflowType == "cast_vote")
                    {
                        var resId = Guid.TryParse(args.GetValueOrDefault("resolutionId"), out var rid) ? rid : Guid.Empty;
                        var unitId = Guid.TryParse(args.GetValueOrDefault("unitId"), out var uid) ? uid : (context.LinkedUnitId ?? Guid.Empty);
                        if (resId == Guid.Empty || unitId == Guid.Empty)
                        {
                            await using var db = await dbFactory.CreateDbContextAsync(ct);
                            var sampleRes = await db.Resolutions.FirstOrDefaultAsync(r => r.CommunityId == comId, ct);
                            resId = sampleRes?.Id ?? Guid.NewGuid();
                            if (unitId == Guid.Empty)
                            {
                                var sampleUnit = await db.Units.FirstOrDefaultAsync(u => u.CommunityId == comId, ct);
                                unitId = sampleUnit?.Id ?? Guid.NewGuid();
                            }
                        }
                        var choice = ParseEnum(args.GetValueOrDefault("choice"), VoteChoice.For);
                        var vote = await workspace.CastVoteAsync(comId, resId, unitId, choice, args.GetValueOrDefault("proxyName"), ct);
                        return MakeResult(new { voteId = vote.Id, resolutionId = resId, choice = vote.Choice.ToString() },
                            $"Cast vote {vote.Choice} on resolution {resId}", $"/governance/resolutions/{resId}");
                    }

                    if (workflowType.Contains("compliance"))
                    {
                        var resId = Guid.TryParse(args.GetValueOrDefault("resolutionId"), out var rid) ? rid : Guid.Empty;
                        if (resId == Guid.Empty)
                        {
                            await using var db = await dbFactory.CreateDbContextAsync(ct);
                            var sampleRes = await db.Resolutions.FirstOrDefaultAsync(r => r.CommunityId == comId, ct);
                            resId = sampleRes?.Id ?? Guid.NewGuid();
                        }
                        var fixedRes = await workspace.ApplyStatuteComplianceFixAsync(comId, resId, ct);
                        return MakeResult(new
                        {
                            resolutionId = fixedRes.Id,
                            complianceStatus = fixedRes.ComplianceStatus,
                            citedDocument = fixedRes.CitedLegalDocumentCode
                        }, $"Applied 1M-context compliance check: Status {fixedRes.ComplianceStatus} ({fixedRes.CitedLegalDocumentCode})");
                    }

                    if (workflowType.Contains("visual"))
                    {
                        var resId = Guid.TryParse(args.GetValueOrDefault("resolutionId"), out var rid) ? rid : Guid.Empty;
                        var resWithVisuals = await workspace.GenerateRenovationVisualsAndQrPosterAsync(comId, resId, ct);
                        return MakeResult(new { optionAUrl = resWithVisuals.OptionAPreviewDataUrl, optionBUrl = resWithVisuals.OptionBPreviewDataUrl, qrPosterUrl = resWithVisuals.QrPosterDataUrl },
                            "Rendered architectural renovation previews and stairwell QR poster");
                    }

                    if (workflowType.Contains("ballot") || workflowType.Contains("upload_ballot_ocr") || workflowType.Contains("digitize"))
                    {
                        var resId = Guid.TryParse(args.GetValueOrDefault("resolutionId"), out var rid) ? rid : Guid.Empty;
                        var ballotImageDataUrl = args.GetValueOrDefault("ballotImageDataUrl");
                        var tally = await workspace.DigitizePaperBallotSheetAsync(comId, resId, ballotImageDataUrl, ct);
                        var truncatedImage = string.IsNullOrWhiteSpace(ballotImageDataUrl)
                            ? string.Empty
                            : ballotImageDataUrl.Length > 64 ? ballotImageDataUrl[..64] + "..." : ballotImageDataUrl;
                        return MakeResult(new { isAdopted = tally.IsAdopted, sharesFor = tally.SharesFor, sharesAgainst = tally.SharesAgainst, ballotImagePrefix = truncatedImage },
                            $"Digitized paper ballot sheets: shares for {tally.SharesFor:P2}. Adopted: {tally.IsAdopted}");
                    }

                    if (workflowType.Contains("transcribe_minutes"))
                    {
                        var meeting = await workspace.TranscribeMeetingRecordingToMinutesAsync(comId, ct);
                        return MakeResult(new { meetingRecordId = meeting.Id, title = meeting.Title },
                            $"Transcribed meeting minutes: '{meeting.Title}'", $"/governance/meetings");
                    }

                    // Default meeting autopilot
                    var resolutions = await workspace.RunAnnualMeetingAutopilotAsync(comId, ct);
                    return MakeResult(new
                    {
                        resolutionsCount = resolutions.Count
                    }, $"Annual Meeting Autopilot completed: {resolutions.Count} statutory draft resolutions generated", $"/governance/meetings");
                }

                // G42. post_announcement_or_direct_message
                case "post_announcement_or_direct_message":
                {
                    var msgType = (args.GetValueOrDefault("messageType") ?? "community_message").ToLowerInvariant();
                    var subj = args.GetValueOrDefault("subject") ?? "Community Notice";
                    var body = args.GetValueOrDefault("body") ?? "Important community notice.";
                    if (msgType.Contains("announcement"))
                    {
                        var cat = ParseEnum(args.GetValueOrDefault("category"), PropertyAnnouncementCategory.General);
                        var pinned = bool.TryParse(args.GetValueOrDefault("isPinned"), out var pin) && pin;
                        await using var db = await dbFactory.CreateDbContextAsync(ct);
                        var ann = new PropertyAnnouncement
                        {
                            CommunityId = comId,
                            AuthorMembershipId = context.ActiveMembershipId,
                            Title = subj,
                            Body = body,
                            Category = cat,
                            IsPinned = pinned,
                            CreatedAtUtc = DateTime.UtcNow
                        };
                        db.PropertyAnnouncements.Add(ann);
                        await db.SaveChangesAsync(ct);
                        return MakeResult(new { announcementId = ann.Id, title = ann.Title, category = ann.Category.ToString() },
                            $"Published announcement '{ann.Title}'", $"/properties/{comId}/overview");
                    }

                    if (msgType.Contains("direct"))
                    {
                        var targetEmail = args.GetValueOrDefault("targetEmail") ?? "resident@example.local";
                        var dm = await workspace.PostResidentDirectMessageAsync(
                            comId,
                            context.FullName,
                            context.Email,
                            context.Role,
                            context.LinkedUnitNumber ?? string.Empty,
                            "Resident",
                            targetEmail,
                            subj,
                            body,
                            ct);
                        return MakeResult(new { messageId = dm.Id, to = targetEmail, subject = dm.Subject },
                            $"Sent direct message to {targetEmail}: '{dm.Subject}'");
                    }

                    var grpId = Guid.TryParse(args.GetValueOrDefault("groupId"), out var gid) ? gid : Guid.NewGuid();
                    var msg = await workspace.PostCommunityMessageAsync(
                        comId,
                        grpId,
                        context.FullName,
                        context.LinkedUnitNumber ?? context.Role.ToString(),
                        subj,
                        body,
                        false,
                        ct);
                    return MakeResult(new { messageId = msg.Id, subject = msg.Subject, author = msg.SenderName },
                        $"Posted community message '{msg.Subject}'");
                }

                // G43. security_approvals_sos_and_vault
                case "security_approvals_sos_and_vault":
                {
                    var actionType = (args.GetValueOrDefault("actionType") ?? "verify_audit_chain").ToLowerInvariant();
                    if (actionType == "decide_ai_action")
                    {
                        var actId = Guid.TryParse(args.GetValueOrDefault("aiActionId"), out var aid) ? aid : Guid.Empty;
                        var approve = !bool.TryParse(args.GetValueOrDefault("approve"), out var app) || app;
                        var decided = await workspace.DecideAiActionAsync(comId, actId, approve, context.Role, ct);
                        return MakeResult(new { aiActionId = decided.Id, status = decided.Status.ToString() },
                            $"Decided AI action {decided.Id}: {decided.Status}", "/approvals");
                    }
                    if (actionType == "approve_all_ai_actions")
                    {
                        var approvedCount = await workspace.ApproveAllPendingActionsAsync(comId, ct);
                        return MakeResult(new { approvedCount }, $"Approved all {approvedCount} pending AI actions for community", "/approvals");
                    }
                    if (actionType == "sos_broadcast")
                    {
                        var stack = args.GetValueOrDefault("stackCode") ?? "STACK-A1";
                        var sosAction = await workspace.TriggerOneClickSosBroadcastAsync(comId, stack, ct);
                        return MakeResult(new { sosActionId = sosAction.Id, stackCode = stack, status = sosAction.Status.ToString() },
                            $"EMERGENCY SOS BROADCAST dispatched for stack {stack}!");
                    }
                    if (actionType == "reveal_access_code")
                    {
                        var codeId = Guid.TryParse(args.GetValueOrDefault("accessCodeId"), out var acid) ? acid : Guid.Empty;
                        var revealed = await workspace.RevealVaultedAccessCodeAsync(comId, codeId, context.Role, ct);
                        return MakeResult(new { accessCodeId = codeId, revealedCode = revealed },
                            $"Revealed vaulted access PIN ({revealed}) for role {context.Role}");
                    }

                    // Default verify audit chain
                    var (isValid, verifiedCount, latestHash) = await workspace.VerifySecurityAuditChainAsync(comId, ct);
                    return MakeResult(new
                    {
                        auditChainValid = isValid,
                        verifiedEventsCount = verifiedCount,
                        latestChainHash = latestHash
                    }, $"SHA-256 Audit Chain Verification: {(isValid ? "VALID" : "COMPROMISED")} ({verifiedCount} events verified, Hash: {latestHash})");
                }

                // G44. generate_custom_info_image (muse-image-1.0)
                case "generate_custom_info_image":
                {
                    var title = args.GetValueOrDefault("title") ?? "Property Visual Notice";
                    var subtitle = args.GetValueOrDefault("subtitle") ?? "Generated with muse-image-1.0 • 640x400 PNG";
                    var rawLines = args.GetValueOrDefault("infoLines") ?? args.GetValueOrDefault("details") ?? string.Empty;
                    var infoLines = rawLines
                        .Split(new[] { '|', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                    if (infoLines.Count == 0)
                    {
                        infoLines.Add(subtitle);
                    }
                    var badgeText = args.GetValueOrDefault("badgeText") ?? "NOTICE";
                    var qrUrl = args.GetValueOrDefault("qrUrl");
                    var uiLocale = args.GetValueOrDefault("uiLocale") ?? "EN";

                    var imgResult = await workspace.GenerateCustomPropertyImageAsync(
                        comId,
                        title,
                        subtitle,
                        infoLines,
                        badgeText,
                        qrUrl,
                        uiLocale,
                        ct);

                    return MakeResult(new
                    {
                        imageDataUrl = imgResult.ImageDataUrl,
                        modelName = imgResult.ModelName,
                        resolution = imgResult.Resolution,
                        latencyMs = imgResult.LatencyMs,
                        title = imgResult.Title,
                        subtitle = imgResult.Subtitle,
                        renderedInfoLines = imgResult.RenderedInfoLines,
                        generatedAtUtc = imgResult.GeneratedAtUtc
                    }, $"Generated {imgResult.Resolution} visual '{imgResult.Title}' via {imgResult.ModelName} ({imgResult.LatencyMs} ms)");
                }

                default:
                    return MakeError($"Tool '{toolName}' is not recognized in the tool catalog.");
            }
        }
        catch (Exception ex)
        {
            return MakeError($"Tool execution failed: {ex.Message}");
        }
    }

    private async Task<ToolObservationResult> ExecuteDynamicDbQueryAsync(
        IReadOnlyDictionary<string, string> args,
        SparkAgentPermissionContext context,
        Guid communityId,
        CancellationToken ct)
    {
        var entityName = (args.GetValueOrDefault("entityName") ?? "units").Trim().ToLowerInvariant();
        var filterField = args.GetValueOrDefault("filterField");
        var filterVal = args.GetValueOrDefault("filterValue");
        var take = int.TryParse(args.GetValueOrDefault("take"), out var t) ? Math.Clamp(t, 1, 50) : 10;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        switch (entityName)
        {
            case "units":
            case "unit":
            {
                var query = db.Units.AsNoTracking().Where(u => u.CommunityId == communityId);

                // Multi-firm tenant boundary check
                if (context.IsFirmManager && context.ManagementFirmId.HasValue)
                {
                    query = query.Where(u => db.Communities.Any(c => c.Id == u.CommunityId && (c.ManagementFirmId == null || c.ManagementFirmId == context.ManagementFirmId.Value)));
                }
                else if (context.AllowedCommunityIds.Count > 0)
                {
                    query = query.Where(u => context.AllowedCommunityIds.Contains(u.CommunityId));
                }
                else
                {
                    query = query.Where(u => false);
                }

                // Resident private unit boundary check
                if (context.Role == WorkspaceSecurityRole.Resident)
                {
                    if (!string.IsNullOrWhiteSpace(context.LinkedUnitNumber))
                    {
                        query = query.Where(u => u.UnitNumber == context.LinkedUnitNumber);
                    }
                    else if (context.LinkedUnitId.HasValue)
                    {
                        query = query.Where(u => u.Id == context.LinkedUnitId.Value);
                    }
                    else
                    {
                        // Unlinked resident has no assigned unit — must return 0 units
                        query = query.Where(u => false);
                    }
                }

                if (!string.IsNullOrWhiteSpace(filterField) && !string.IsNullOrWhiteSpace(filterVal))
                {
                    if (filterField.Equals("OccupancyStatus", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<UnitOccupancyStatus>(filterVal, true, out var occ))
                        query = query.Where(u => u.OccupancyStatus == occ);
                    else if (filterField.Equals("UnitNumber", StringComparison.OrdinalIgnoreCase))
                        query = query.Where(u => u.UnitNumber == filterVal);
                }

                var list = await query.Take(take).Select(u => new
                {
                    u.Id,
                    u.UnitNumber,
                    u.FloorNumber,
                    u.FloorAreaM2,
                    OccupancyStatus = u.OccupancyStatus.ToString(),
                    u.CurrentBalance
                }).ToListAsync(ct);
                return MakeResult(new { entity = "units", count = list.Count, rows = list }, $"Retrieved {list.Count} units matching query");
            }

            case "communities":
            case "community":
            {
                var query = db.Communities.AsNoTracking().AsQueryable();
                if (context.IsFirmManager && context.ManagementFirmId.HasValue)
                {
                    query = query.Where(c => (c.ManagementFirmId == null || c.ManagementFirmId == context.ManagementFirmId.Value));
                }
                else if (context.AllowedCommunityIds.Count > 0)
                {
                    query = query.Where(c => context.AllowedCommunityIds.Contains(c.Id));
                }
                else
                {
                    // Guest, unassigned user, or firm manager without firm scope receives 0 communities
                    query = query.Where(c => false);
                }

                if (!string.IsNullOrWhiteSpace(filterField) && !string.IsNullOrWhiteSpace(filterVal))
                {
                    if (filterField.Equals("City", StringComparison.OrdinalIgnoreCase))
                        query = query.Where(c => c.City == filterVal);
                    else if (filterField.Equals("Name", StringComparison.OrdinalIgnoreCase))
                        query = query.Where(c => c.Name.Contains(filterVal));
                }

                var list = await query.Take(take).Select(c => new
                {
                    c.Id,
                    c.CommunityAccountNumber,
                    c.Name,
                    c.City,
                    OperatingMode = c.OperatingMode.ToString(),
                    c.OperatingRatePerM2,
                    c.RepairFundRatePerM2
                }).ToListAsync(ct);
                return MakeResult(new { entity = "communities", count = list.Count, rows = list }, $"Retrieved {list.Count} communities");
            }

            case "work_orders":
            case "workorders":
            {
                var query = db.WorkOrders.AsNoTracking().Where(w => w.CommunityId == communityId);

                // Multi-firm tenant boundary check
                if (context.IsFirmManager && context.ManagementFirmId.HasValue)
                {
                    query = query.Where(w => db.Communities.Any(c => c.Id == w.CommunityId && (c.ManagementFirmId == null || c.ManagementFirmId == context.ManagementFirmId.Value)));
                }
                else if (context.AllowedCommunityIds.Count > 0)
                {
                    query = query.Where(w => context.AllowedCommunityIds.Contains(w.CommunityId));
                }
                else
                {
                    query = query.Where(w => false);
                }

                // Resident boundary check: private unit work orders vs common property
                if (context.Role == WorkspaceSecurityRole.Resident)
                {
                    if (context.LinkedUnitId.HasValue)
                    {
                        query = query.Where(w => w.UnitId == null || w.UnitId == context.LinkedUnitId.Value);
                    }
                    else if (!string.IsNullOrWhiteSpace(context.LinkedUnitNumber))
                    {
                        query = query.Where(w => w.UnitId == null || w.UnitNumber == context.LinkedUnitNumber);
                    }
                    else
                    {
                        query = query.Where(w => w.UnitId == null);
                    }
                }

                if (!string.IsNullOrWhiteSpace(filterField) && !string.IsNullOrWhiteSpace(filterVal))
                {
                    if (filterField.Equals("Status", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<WorkOrderStatus>(filterVal, true, out var st))
                        query = query.Where(w => w.Status == st);
                    else if (filterField.Equals("Category", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<WorkOrderCategory>(filterVal, true, out var cat))
                        query = query.Where(w => w.Category == cat);
                }

                var list = await query.Take(take).Select(w => new
                {
                    w.Id,
                    w.Title,
                    Category = w.Category.ToString(),
                    Priority = w.Priority.ToString(),
                    Status = w.Status.ToString(),
                    w.InvestorBoqEstimateAmount
                }).ToListAsync(ct);
                return MakeResult(new { entity = "work_orders", count = list.Count, rows = list }, $"Retrieved {list.Count} work orders");
            }

            case "resolutions":
            case "resolution":
            {
                var query = db.Resolutions.AsNoTracking().Where(r => r.CommunityId == communityId);

                // Multi-firm tenant boundary check
                if (context.IsFirmManager && context.ManagementFirmId.HasValue)
                {
                    query = query.Where(r => db.Communities.Any(c => c.Id == r.CommunityId && (c.ManagementFirmId == null || c.ManagementFirmId == context.ManagementFirmId.Value)));
                }
                else if (context.AllowedCommunityIds.Count > 0)
                {
                    query = query.Where(r => context.AllowedCommunityIds.Contains(r.CommunityId));
                }
                else
                {
                    query = query.Where(r => false);
                }

                var list = await query.Take(take).Select(r => new
                {
                    r.Id,
                    r.Number,
                    r.Title,
                    MajorityRule = r.MajorityRule.ToString(),
                    r.ComplianceStatus
                }).ToListAsync(ct);
                return MakeResult(new { entity = "resolutions", count = list.Count, rows = list }, $"Retrieved {list.Count} resolutions");
            }

            case "meters":
            case "utility_meters":
            {
                var unitQuery = db.Units.AsNoTracking().Where(u => u.CommunityId == communityId);

                // Multi-firm tenant boundary check
                if (context.IsFirmManager && context.ManagementFirmId.HasValue)
                {
                    unitQuery = unitQuery.Where(u => db.Communities.Any(c => c.Id == u.CommunityId && (c.ManagementFirmId == null || c.ManagementFirmId == context.ManagementFirmId.Value)));
                }
                else if (context.AllowedCommunityIds.Count > 0)
                {
                    unitQuery = unitQuery.Where(u => context.AllowedCommunityIds.Contains(u.CommunityId));
                }
                else
                {
                    unitQuery = unitQuery.Where(u => false);
                }

                // Resident boundary check: residents may only query utility meters for their own linked unit
                if (context.Role == WorkspaceSecurityRole.Resident)
                {
                    if (context.LinkedUnitId.HasValue)
                    {
                        unitQuery = unitQuery.Where(u => u.Id == context.LinkedUnitId.Value);
                    }
                    else if (!string.IsNullOrWhiteSpace(context.LinkedUnitNumber))
                    {
                        unitQuery = unitQuery.Where(u => u.UnitNumber == context.LinkedUnitNumber);
                    }
                    else
                    {
                        unitQuery = unitQuery.Where(u => false);
                    }
                }

                var unitIds = await unitQuery.Select(u => u.Id).ToListAsync(ct);
                var query = db.UtilityMeters.AsNoTracking().Where(m => unitIds.Contains(m.UnitId));
                var list = await query.Take(take).Select(m => new
                {
                    m.Id,
                    m.UnitId,
                    m.SerialNumber,
                    Kind = m.Kind.ToString(),
                    m.LastReadingValue,
                    m.PreviousReadingValue,
                    m.RollingMonthlyAverage,
                    m.HasSilentLeakAnomaly
                }).ToListAsync(ct);
                return MakeResult(new { entity = "utility_meters", count = list.Count, rows = list }, $"Retrieved {list.Count} utility meters");
            }

            case "legal_documents":
            case "legaldocuments":
            case "property_documents":
            case "documents":
            {
                var q = db.LegalDocuments.AsNoTracking().AsQueryable();
                if (communityId != Guid.Empty)
                {
                    q = q.Where(d => d.CommunityId == communityId);
                }
                else if (context.AllowedCommunityIds.Count > 0)
                {
                    q = q.Where(d => context.AllowedCommunityIds.Contains(d.CommunityId));
                }
                else if (!(context.IsFirmManager && context.ManagementFirmId.HasValue))
                {
                    q = q.Where(d => false);
                }
                if (!string.IsNullOrWhiteSpace(filterField) && !string.IsNullOrWhiteSpace(filterVal))
                {
                    if (filterField.Equals("DocumentCode", StringComparison.OrdinalIgnoreCase))
                    {
                        q = q.Where(d => d.DocumentCode == filterVal);
                    }
                    else if (filterField.Equals("Title", StringComparison.OrdinalIgnoreCase))
                    {
                        q = q.Where(d => d.Title.Contains(filterVal));
                    }
                    else if (filterField.Equals("Kind", StringComparison.OrdinalIgnoreCase)
                        && Enum.TryParse<LegalDocumentKind>(filterVal, true, out var kind))
                    {
                        q = q.Where(d => d.Kind == kind);
                    }
                }
                var docs = await q.OrderBy(d => d.DocumentCode).Take(take).Select(d => new
                {
                    d.Id,
                    d.CommunityId,
                    documentCode = d.DocumentCode,
                    title = d.Title,
                    category = d.Kind.ToString(),
                    d.OfficialCitation
                }).ToListAsync(ct);
                return MakeResult(new { entity = "legal_documents", count = docs.Count, rows = docs }, $"Retrieved {docs.Count} property documents");
            }

            default:
            {
                return MakeError($"Entity '{entityName}' is not indexed for dynamic database queries. Supported entities: units, communities, work_orders, resolutions, utility_meters, legal_documents.");
            }
        }
    }

    private async Task<ToolObservationResult> ExecuteSystemEntityQueryAsync(
        string entityType,
        string searchTerm,
        Guid communityId,
        SparkAgentPermissionContext context,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var norm = entityType.Trim().ToLowerInvariant();

        if (norm is "units" or "unit")
        {
            var q = db.Units.AsNoTracking().Where(u => u.CommunityId == communityId);

            // Multi-firm tenant boundary check
            if (context.IsFirmManager && context.ManagementFirmId.HasValue)
            {
                q = q.Where(u => db.Communities.Any(c => c.Id == u.CommunityId && (c.ManagementFirmId == null || c.ManagementFirmId == context.ManagementFirmId.Value)));
            }
            else if (context.AllowedCommunityIds.Count > 0)
            {
                q = q.Where(u => context.AllowedCommunityIds.Contains(u.CommunityId));
            }
            else
            {
                q = q.Where(u => false);
            }

            // Resident private unit boundary check
            if (context.Role == WorkspaceSecurityRole.Resident)
            {
                if (!string.IsNullOrWhiteSpace(context.LinkedUnitNumber))
                {
                    q = q.Where(u => u.UnitNumber == context.LinkedUnitNumber);
                }
                else if (context.LinkedUnitId.HasValue)
                {
                    q = q.Where(u => u.Id == context.LinkedUnitId.Value);
                }
                else
                {
                    q = q.Where(u => false);
                }
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                if (searchTerm.Equals("vacant", StringComparison.OrdinalIgnoreCase))
                {
                    q = q.Where(u => u.OccupancyStatus == UnitOccupancyStatus.Vacant);
                }
                else
                {
                    q = q.Where(u => u.UnitNumber.Contains(searchTerm) || u.PrimaryOwnerName.Contains(searchTerm));
                }
            }
            var units = await q.Take(15).Select(u => new
            {
                u.Id,
                u.UnitNumber,
                u.FloorNumber,
                u.VerticalStackCode,
                OccupancyStatus = u.OccupancyStatus.ToString(),
                u.FloorAreaM2,
                u.CurrentBalance
            }).ToListAsync(ct);
            return MakeResult(new { entityType = "units", totalCount = units.Count, records = units },
                $"Found {units.Count} units matching search '{searchTerm}'", $"/properties/{communityId}/units");
        }

        if (norm is "communities" or "community")
        {
            var q = db.Communities.AsNoTracking().AsQueryable();
            if (context.IsFirmManager && context.ManagementFirmId.HasValue)
            {
                q = q.Where(c => (c.ManagementFirmId == null || c.ManagementFirmId == context.ManagementFirmId.Value));
            }
            else if (context.AllowedCommunityIds.Count > 0)
            {
                q = q.Where(c => context.AllowedCommunityIds.Contains(c.Id));
            }
            else
            {
                q = q.Where(c => false);
            }

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                q = q.Where(c => c.Name.Contains(searchTerm) || c.City.Contains(searchTerm));
            }
            var comms = await q.Take(10).Select(c => new
            {
                c.Id,
                c.CommunityAccountNumber,
                c.Name,
                c.StreetAddress,
                c.City,
                OperatingMode = c.OperatingMode.ToString()
            }).ToListAsync(ct);
            return MakeResult(new { entityType = "communities", totalCount = comms.Count, records = comms },
                $"Found {comms.Count} communities matching '{searchTerm}'");
        }

        if (norm is "legal_documents" or "legaldocuments" or "property_documents" or "propertydocuments" or "documents")
        {
            var scoped = communityId != Guid.Empty
                && (context.IsFirmManager || context.CanAccessCommunity(communityId));
            if (!scoped && context.AllowedCommunityIds.Count == 0)
            {
                return MakeResult(new { entityType = "legal_documents", totalCount = 0, records = Array.Empty<object>() },
                    $"No property documents visible in the current access scope.");
            }
            var q = db.LegalDocuments.AsNoTracking().AsQueryable();
            if (communityId != Guid.Empty)
            {
                q = q.Where(d => d.CommunityId == communityId);
            }
            else if (context.AllowedCommunityIds.Count > 0)
            {
                q = q.Where(d => context.AllowedCommunityIds.Contains(d.CommunityId));
            }
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                q = q.Where(d => d.DocumentCode.Contains(searchTerm) || d.Title.Contains(searchTerm) || d.VerbatimLegalText.Contains(searchTerm));
            }
            var docs = await q.OrderBy(d => d.DocumentCode).Take(20).Select(d => new
            {
                d.Id,
                d.CommunityId,
                documentCode = d.DocumentCode,
                title = d.Title,
                category = d.Kind.ToString(),
                d.OfficialCitation,
                excerpt = d.VerbatimLegalText.Length > 240 ? d.VerbatimLegalText.Substring(0, 240) + "..." : d.VerbatimLegalText
            }).ToListAsync(ct);
            return MakeResult(new { entityType = "legal_documents", totalCount = docs.Count, records = docs },
                $"Found {docs.Count} property documents matching '{searchTerm}' ({string.Join(", ", docs.Select(d => d.documentCode))})");
        }

        if (norm is "buildings" or "building")
        {
            var scoped = communityId != Guid.Empty
                && (context.IsFirmManager || context.CanAccessCommunity(communityId));
            if (!scoped && context.AllowedCommunityIds.Count == 0)
            {
                return MakeResult(new { entityType = "buildings", totalCount = 0, records = Array.Empty<object>() },
                    "No buildings visible in the current access scope.");
            }
            var q = db.Buildings.AsNoTracking().AsQueryable();
            if (communityId != Guid.Empty)
            {
                q = q.Where(b => b.CommunityId == communityId);
            }
            else if (context.AllowedCommunityIds.Count > 0)
            {
                q = q.Where(b => context.AllowedCommunityIds.Contains(b.CommunityId));
            }
            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                q = q.Where(b => b.Name.Contains(searchTerm));
            }
            var buildingRows = await q.OrderBy(b => b.Name).Take(20).Select(b => new
            {
                b.Id,
                b.CommunityId,
                b.Name,
                b.FloorsCount,
                b.StairwellsCount
            }).ToListAsync(ct);
            var buildingIds = buildingRows.Select(b => b.Id).ToList();
            var unitCounts = await db.Units.AsNoTracking()
                .Where(u => buildingIds.Contains(u.BuildingId))
                .GroupBy(u => u.BuildingId)
                .Select(g => new { BuildingId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.BuildingId, g => g.Count, ct);
            var rows = buildingRows.Select(b => new
            {
                id = b.Id,
                communityId = b.CommunityId,
                name = b.Name,
                floorsCount = b.FloorsCount,
                stairwellsCount = b.StairwellsCount,
                unitCount = unitCounts.TryGetValue(b.Id, out var count) ? count : 0
            }).ToList();
            return MakeResult(new { entityType = "buildings", totalCount = rows.Count, records = rows },
                $"Found {rows.Count} buildings matching '{searchTerm}' ({string.Join(", ", rows.Select(r => r.name))})",
                communityId == Guid.Empty ? null : $"/properties/{communityId}/infrastructure");
        }

        // Generic entity inspection
        return MakeResult(new { entityType, totalCount = 0, records = Array.Empty<object>() },
            $"Inspected system entity set '{entityType}'");
    }

    private static Guid ResolveCommunityId(
        IReadOnlyDictionary<string, string> args,
        SparkAgentPermissionContext context,
        Guid defaultCommunityId)
    {
        if (args.TryGetValue("communityId", out var cStr) && Guid.TryParse(cStr, out var cId) && cId != Guid.Empty)
            return cId;
        if (args.TryGetValue("targetCommunityId", out var tcStr) && Guid.TryParse(tcStr, out var tcId) && tcId != Guid.Empty)
            return tcId;
        if (defaultCommunityId != Guid.Empty)
            return defaultCommunityId;
        if (context.AllowedCommunityIds.Count > 0)
            return context.AllowedCommunityIds[0];
        return Guid.Empty;
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum defaultValue) where TEnum : struct, Enum
    {
        if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse<TEnum>(value.Trim(), true, out var parsed))
            return parsed;
        return defaultValue;
    }

    private static decimal? ParseDecimal(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        if (!string.IsNullOrWhiteSpace(value) && decimal.TryParse(value.Trim(), out var dLocal))
            return dLocal;
        return null;
    }

    private static IReadOnlyList<CzynszLineItem> ParseCzynszLines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<CzynszLinePayload>>(raw, JsonOpts);
            if (parsed is null || parsed.Count == 0)
            {
                return [];
            }

            var result = new List<CzynszLineItem>();
            foreach (var p in parsed.Take(50))
            {
                var kind = Enum.TryParse<CzynszLineKind>(p.Kind, true, out var k) ? k : CzynszLineKind.Fixed;
                var qty = Math.Max(0m, p.Quantity);
                var price = Math.Max(0m, p.UnitPrice);
                result.Add(new CzynszLineItem(
                    string.IsNullOrWhiteSpace(p.Key) ? $"custom-{result.Count + 1}" : p.Key.Trim(),
                    string.IsNullOrWhiteSpace(p.Label) ? "Custom line" : p.Label.Trim(),
                    kind,
                    Math.Round(qty, 3),
                    Math.Round(price, 4),
                    Math.Round(qty * price, 2),
                    p.IsCredit));
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private sealed class CzynszLinePayload
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Kind { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public bool IsCredit { get; set; }
    }

    private static IReadOnlyList<string> ParseOptions(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw, JsonOpts);
            if (parsed != null && parsed.Count > 0) return parsed;
        }
        catch { }
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }

    private ToolObservationResult MakeClarification(
        string reason,
        string question,
        IReadOnlyList<string> options,
        string? field = null,
        Guid? resolvedUnitId = null,
        string? resolvedUnitNumber = null,
        Guid? resolvedBuildingId = null,
        string? resolvedBuildingName = null)
    {
        return new ToolObservationResult(
            false,
            JsonSerializer.Serialize(new
            {
                error = "CLARIFICATION_REQUIRED",
                status = "ClarificationRequired",
                code = "CLARIFICATION_REQUIRED",
                reason,
                question,
                options,
                clarificationRequired = true,
                requiredQuestion = question,
                field,
                buildingId = resolvedBuildingId,
                buildingName = resolvedBuildingName,
                unitId = resolvedUnitId,
                unitNumber = resolvedUnitNumber
            }, JsonOpts),
            question)
        {
            RequiresClarification = true,
            ClarificationQuestion = question,
            ClarificationOptions = options
        };
    }

    private async Task<(Guid? CommunityId, bool HasAmbiguity)> ResolveClarificationCommunityAsync(
        Guid resolvedCommunityId,
        SparkAgentPermissionContext context,
        CancellationToken ct)
    {
        if (resolvedCommunityId != Guid.Empty)
        {
            return (resolvedCommunityId, false);
        }

        if (context.AllowedCommunityIds.Count == 1)
        {
            return (context.AllowedCommunityIds[0], false);
        }

        if (context.AllowedCommunityIds.Count > 1)
        {
            var scopedIds = context.AllowedCommunityIds.Where(id => id != Guid.Empty).Distinct().ToList();
            if (scopedIds.Count == 1)
            {
                return (scopedIds[0], false);
            }

            if (!context.ManagementFirmId.HasValue || context.ManagementFirmId.Value == Guid.Empty)
            {
                return (null, scopedIds.Count > 1);
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var scopedCommunities = await db.Communities.AsNoTracking()
                .Where(c => scopedIds.Contains(c.Id) && c.ManagementFirmId == context.ManagementFirmId.Value)
                .Select(c => c.Id)
                .ToListAsync(ct);
            return scopedCommunities.Count == 1 ? (scopedCommunities[0], false) : (null, true);
        }

        if (!context.ManagementFirmId.HasValue || context.ManagementFirmId.Value == Guid.Empty)
        {
            return (null, false);
        }

        await using var lookupDb = await dbFactory.CreateDbContextAsync(ct);
        var firmCommunities = await lookupDb.Communities.AsNoTracking()
            .Where(c => c.ManagementFirmId == context.ManagementFirmId.Value)
            .Select(c => c.Id)
            .Take(2)
            .ToListAsync(ct);
        return firmCommunities.Count == 1 ? (firmCommunities[0], false) : (null, firmCommunities.Count > 1);
    }

    private sealed record BuildingDeleteTargetResolution(
        bool IsResolved,
        ToolObservationResult? Clarification,
        Guid CommunityId,
        Guid BuildingId,
        string BuildingName,
        int UnitCount);

    /// <summary>
    /// Deterministically resolves the exact building before a building delete.
    /// An empty or ambiguous buildingId is never replaced with a guessed building.
    /// </summary>
    private async Task<BuildingDeleteTargetResolution> ResolveBuildingDeleteTargetAsync(
        IReadOnlyDictionary<string, string> args,
        SparkAgentPermissionContext context,
        Guid resolvedCommunityId,
        CancellationToken ct)
    {
        ToolObservationResult Clarification(string reason, string question, IReadOnlyList<string> options, string? field = null, Guid? buildingId = null, string? buildingName = null)
            => MakeClarification(reason, question, options, field, resolvedBuildingId: buildingId, resolvedBuildingName: buildingName);
        BuildingDeleteTargetResolution Needs(ToolObservationResult clarification)
            => new(false, clarification, resolvedCommunityId, Guid.Empty, string.Empty, 0);

        Guid? explicitBuildingId = null;
        if (args.TryGetValue("buildingId", out var buildingValue) && !string.IsNullOrWhiteSpace(buildingValue))
        {
            if (!Guid.TryParse(buildingValue, out var parsedBuildingId) || parsedBuildingId == Guid.Empty)
            {
                return Needs(Clarification(
                    $"The supplied buildingId '{buildingValue}' is empty or invalid.",
                    "Which building should be deleted? The supplied building ID is empty or invalid. Please choose the building; do not leave buildingId empty.",
                    Array.Empty<string>(), "buildingId"));
            }
            explicitBuildingId = parsedBuildingId;
        }

        var communityResolution = await ResolveClarificationCommunityAsync(
            resolvedCommunityId,
            context,
            ct);
        if (!communityResolution.CommunityId.HasValue || communityResolution.CommunityId.Value == Guid.Empty)
        {
            return Needs(Clarification(
                "No communityId is available for building deletion.",
                "Which housing association owns the building to delete? Please choose the property or provide its exact community ID.",
                Array.Empty<string>(), "communityId"));
        }

        var communityId = communityResolution.CommunityId.Value;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var communityExists = await db.Communities.AsNoTracking().AnyAsync(c => c.Id == communityId, ct);
        if (!communityExists)
        {
            return Needs(Clarification(
                $"Community '{communityId}' was not found.",
                "Which housing association owns the building to delete? The supplied community was not found. Please choose an existing property.",
                Array.Empty<string>(), "communityId"));
        }

        var buildingName = (args.GetValueOrDefault("buildingName") ?? args.GetValueOrDefault("building"))?.Trim() ?? string.Empty;

        if (!explicitBuildingId.HasValue && string.IsNullOrWhiteSpace(buildingName))
        {
            var buildings = await db.Buildings.AsNoTracking()
                .Where(b => b.CommunityId == communityId)
                .OrderBy(b => b.Name)
                .Select(b => b.Name)
                .ToListAsync(ct);
            return Needs(Clarification(
                "No buildingId or buildingName was supplied for building deletion.",
                "Which building should be deleted? Please choose the building; do not leave buildingId empty.",
                buildings, "buildingId"));
        }

        var buildingQuery = db.Buildings.AsNoTracking().Where(b => b.CommunityId == communityId);
        if (explicitBuildingId.HasValue)
        {
            buildingQuery = buildingQuery.Where(b => b.Id == explicitBuildingId.Value);
        }
        else
        {
            var normalizedName = buildingName.ToUpperInvariant();
            buildingQuery = buildingQuery.Where(b => b.Name.ToUpper() == normalizedName);
        }

        var candidates = await buildingQuery
            .OrderBy(b => b.Name)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.CommunityId,
                UnitCount = db.Units.Count(u => u.BuildingId == b.Id)
            })
            .Take(26)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            var buildings = await db.Buildings.AsNoTracking()
                .Where(b => b.CommunityId == communityId)
                .OrderBy(b => b.Name)
                .Select(b => b.Name)
                .ToListAsync(ct);
            var lookedUp = explicitBuildingId.HasValue ? $"building ID '{explicitBuildingId.Value}'" : $"building name '{buildingName}'";
            return Needs(Clarification(
                $"No building matching {lookedUp} was found in the selected housing association.",
                $"I could not find {lookedUp}. Which building should be deleted? Please choose the building from the options.",
                buildings, "buildingId"));
        }

        if (candidates.Count > 1)
        {
            var options = candidates.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();
            return Needs(Clarification(
                $"Building name '{buildingName}' matches {candidates.Count} buildings.",
                $"Building name '{buildingName}' is ambiguous. Which building should be deleted?",
                options, "buildingId"));
        }

        var candidate = candidates[0];
        return new BuildingDeleteTargetResolution(
            true,
            null,
            candidate.CommunityId,
            candidate.Id,
            candidate.Name,
            candidate.UnitCount);
    }

    private sealed record RegistrationTargetResolution(
        bool IsResolved,
        ToolObservationResult? Clarification,
        Guid CommunityId,
        Guid UnitId,
        string UnitNumber,
        Guid BuildingId,
        string BuildingName);

    /// <summary>
    /// Deterministically resolves the exact community, building, and unit before a
    /// registration link can be issued. A housing association can contain many
    /// buildings and a unit number can repeat across them, so an empty ID is
    /// never replaced with a guessed building or unit.
    /// </summary>
    private async Task<RegistrationTargetResolution> ResolveRegistrationTargetAsync(
        IReadOnlyDictionary<string, string> args,
        SparkAgentPermissionContext context,
        Guid resolvedCommunityId,
        CancellationToken ct)
    {
        ToolObservationResult Clarification(string reason, string question, IReadOnlyList<string> options, string? field = null, Guid? unitId = null, string? unitNumber = null, Guid? buildingId = null, string? buildingName = null)
            => MakeClarification(reason, question, options, field, unitId, unitNumber, buildingId, buildingName);
        RegistrationTargetResolution Needs(ToolObservationResult clarification)
            => new(false, clarification, resolvedCommunityId, Guid.Empty, string.Empty, Guid.Empty, string.Empty);

        Guid? explicitCommunityId = null;
        Guid? explicitBuildingId = null;
        Guid? explicitUnitId = null;

        if (args.TryGetValue("communityId", out var communityValue) && !string.IsNullOrWhiteSpace(communityValue))
        {
            if (!Guid.TryParse(communityValue, out var parsedCommunityId))
            {
                return Needs(Clarification(
                    $"The supplied communityId '{communityValue}' is not a valid community GUID.",
                    "Which housing association should receive this registration link? The supplied community ID is invalid. Please choose the property or provide its exact community ID.",
                    Array.Empty<string>(), "communityId"));
            }
            explicitCommunityId = parsedCommunityId;
        }
        else if (args.TryGetValue("targetCommunityId", out var targetCommunityValue) && !string.IsNullOrWhiteSpace(targetCommunityValue))
        {
            if (!Guid.TryParse(targetCommunityValue, out var parsedCommunityId))
            {
                return Needs(Clarification(
                    $"The supplied targetCommunityId '{targetCommunityValue}' is not a valid community GUID.",
                    "Which housing association should receive this registration link? The supplied community ID is invalid. Please choose the property or provide its exact community ID.",
                    Array.Empty<string>(), "communityId"));
            }
            explicitCommunityId = parsedCommunityId;
        }

        if (args.TryGetValue("buildingId", out var buildingValue) && !string.IsNullOrWhiteSpace(buildingValue)
            && (!Guid.TryParse(buildingValue, out var parsedBuildingId) || parsedBuildingId == Guid.Empty))
        {
            return Needs(Clarification(
                $"The supplied buildingId '{buildingValue}' is empty or invalid.",
                "Which building should receive this registration link? The supplied building ID is empty or invalid. Please choose the building; do not leave buildingId empty.",
                Array.Empty<string>(), "buildingId"));
        }
        else if (Guid.TryParse(buildingValue, out var validBuildingId) && validBuildingId != Guid.Empty)
        {
            explicitBuildingId = validBuildingId;
        }

        foreach (var unitKey in new[] { "targetUnitId", "unitId", "targetUnitID" })
        {
            if (!args.TryGetValue(unitKey, out var unitValue) || string.IsNullOrWhiteSpace(unitValue))
            {
                continue;
            }

            if (!Guid.TryParse(unitValue, out var parsedUnitId) || parsedUnitId == Guid.Empty)
            {
                return Needs(Clarification(
                    $"The supplied {unitKey} '{unitValue}' is empty or invalid.",
                    "Which unit should receive this registration link? The supplied unit ID is empty or invalid. Please choose the building and unit, or provide the exact unit ID.",
                    Array.Empty<string>(), "unitId"));
            }

            explicitUnitId = parsedUnitId;
            break;
        }

        var communityResolution = await ResolveClarificationCommunityAsync(
            explicitCommunityId ?? resolvedCommunityId,
            context,
            ct);
        if (!communityResolution.CommunityId.HasValue || communityResolution.CommunityId.Value == Guid.Empty)
        {
            if (context.AllowedCommunityIds.Count > 1)
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var names = await db.Communities.AsNoTracking()
                    .Where(c => context.AllowedCommunityIds.Contains(c.Id))
                    .OrderBy(c => c.Name)
                    .Select(c => c.Name)
                    .ToListAsync(ct);
                return Needs(Clarification(
                    "More than one housing association is in scope and no communityId was supplied.",
                    "Which housing association should receive this registration link? Please choose the property before a link is generated.",
                    names, "communityId"));
            }

            return Needs(Clarification(
                "No communityId is available for registration-link generation.",
                "Which housing association should receive this registration link? Please choose the property or provide its exact community ID.",
                Array.Empty<string>(), "communityId"));
        }

        var communityId = communityResolution.CommunityId.Value;
        await using var targetDb = await dbFactory.CreateDbContextAsync(ct);
        var communityExists = await targetDb.Communities.AsNoTracking().AnyAsync(c => c.Id == communityId, ct);
        if (!communityExists)
        {
            return Needs(Clarification(
                $"Community '{communityId}' was not found.",
                "Which housing association should receive this registration link? The supplied community was not found. Please choose an existing property.",
                Array.Empty<string>(), "communityId"));
        }

        var unitNumber = (args.GetValueOrDefault("unitNumber") ?? args.GetValueOrDefault("unit_number"))?.Trim() ?? string.Empty;
        var buildingName = (args.GetValueOrDefault("buildingName") ?? args.GetValueOrDefault("building"))?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(buildingName) && explicitBuildingId.HasValue)
        {
            buildingName = await targetDb.Buildings.AsNoTracking()
                .Where(b => b.Id == explicitBuildingId.Value && b.CommunityId == communityId)
                .Select(b => b.Name)
                .FirstOrDefaultAsync(ct) ?? string.Empty;
        }

        var unitQuery = targetDb.Units.AsNoTracking().Where(u => u.CommunityId == communityId);
        if (explicitUnitId.HasValue)
        {
            unitQuery = unitQuery.Where(u => u.Id == explicitUnitId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(unitNumber))
        {
            var normalizedUnitNumber = unitNumber.ToUpperInvariant();
            unitQuery = unitQuery.Where(u => u.UnitNumber.ToUpper() == normalizedUnitNumber);
        }
        else
        {
            return Needs(Clarification(
                "No targetUnitId, unitId, or unitNumber was supplied.",
                "Which unit should receive this registration link? Please choose the building and unit, or provide the exact unit ID or unit number.",
                Array.Empty<string>(), "unitId", buildingId: explicitBuildingId, buildingName: string.IsNullOrWhiteSpace(buildingName) ? null : buildingName));
        }

        if (explicitBuildingId.HasValue)
        {
            unitQuery = unitQuery.Where(u => u.BuildingId == explicitBuildingId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(buildingName))
        {
            var normalizedBuildingName = buildingName.ToUpperInvariant();
            unitQuery = unitQuery.Where(u => targetDb.Buildings.Any(b => b.Id == u.BuildingId && b.CommunityId == communityId && b.Name.ToUpper() == normalizedBuildingName));
        }

        var candidates = await unitQuery
            .OrderBy(u => u.UnitNumber)
            .Select(u => new
            {
                u.Id,
                u.UnitNumber,
                u.BuildingId,
                BuildingName = targetDb.Buildings.Where(b => b.Id == u.BuildingId).Select(b => b.Name).FirstOrDefault()
            })
            .Take(26)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            var buildings = await targetDb.Buildings.AsNoTracking()
                .Where(b => b.CommunityId == communityId)
                .OrderBy(b => b.Name)
                .Select(b => b.Name)
                .ToListAsync(ct);
            var lookedUp = explicitUnitId.HasValue ? $"unit ID '{explicitUnitId.Value}'" : $"unit number '{unitNumber}'";
            return Needs(Clarification(
                $"No unit matching {lookedUp} was found in the selected housing association{(string.IsNullOrWhiteSpace(buildingName) ? string.Empty : $" and building '{buildingName}'")}.",
                $"I could not find {lookedUp} for this registration link. Which building and unit should be used? Please choose the building {(buildings.Count == 0 ? string.Empty : "from the options ")}and provide the unit number.",
                buildings, "unitId", buildingId: explicitBuildingId, buildingName: string.IsNullOrWhiteSpace(buildingName) ? null : buildingName));
        }

        if (candidates.Count > 1)
        {
            var options = candidates.Select(c => $"{c.BuildingName} — Unit {c.UnitNumber}").Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();
            var label = string.IsNullOrWhiteSpace(unitNumber) ? "That unit ID" : $"Unit number '{unitNumber}'";
            return Needs(Clarification(
                $"{label} matches {candidates.Count} units in different buildings.",
                $"{label} exists in more than one building in this housing association. Which building should receive the registration link?",
                options, "buildingId", candidates[0].Id, candidates[0].UnitNumber, candidates[0].BuildingId, candidates[0].BuildingName));
        }

        var candidate = candidates[0];
        if (explicitBuildingId.HasValue && explicitBuildingId.Value != candidate.BuildingId)
        {
            return Needs(Clarification(
                "The supplied buildingId does not match the selected unit's building.",
                $"The selected unit is in {candidate.BuildingName}, not the supplied building. Which building and unit should receive this registration link?",
                [$"{candidate.BuildingName} — Unit {candidate.UnitNumber}"], "buildingId", candidate.Id, candidate.UnitNumber, candidate.BuildingId, candidate.BuildingName));
        }

        return new RegistrationTargetResolution(
            true,
            null,
            communityId,
            candidate.Id,
            candidate.UnitNumber,
            candidate.BuildingId,
            candidate.BuildingName ?? string.Empty);
    }

    private static ToolObservationResult MakeResult(object data, string summary, string? deepLink = null)
    {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        return new ToolObservationResult(true, json, summary, deepLink);
    }

    private static ToolObservationResult MakeError(string error)
    {
        var json = JsonSerializer.Serialize(new { error, status = "Failed" }, JsonOpts);
        return new ToolObservationResult(false, json, error);
    }
}

public sealed class MuseSparkAutomationAgentService(
    IMuseSparkClient museSparkClient,
    SparkAgentPermissionEvaluator permissionEvaluator,
    SparkAgentToolExecutor toolExecutor,
    IAppDbContextFactory dbFactory) : IMuseSparkAutomationAgentService
{
    private const string SparkModelId = "muse-spark-1.3-contributor";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async Task<AgentRunResult> ExecuteAgenticTurnAsync(
        string userGoal,
        ClaimsPrincipal userPrincipal,
        Guid? activeCommunityId,
        bool stepByStepMode,
        IProgress<SparkAgentLiveStepEvent>? progress = null,
        string? userClarificationResponse = null,
        CancellationToken ct = default,
        string uiLocale = "EN")
    {
        if (string.IsNullOrWhiteSpace(userGoal))
        {
            throw new ArgumentException("User goal cannot be empty.", nameof(userGoal));
        }

        var normalizedLocale = string.IsNullOrWhiteSpace(uiLocale) ? "EN" : uiLocale.Trim().ToUpperInvariant();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var detectedCommunityId = await SparkAgentPermissionEvaluator.DetectTargetCommunityFromGoalAsync(
            db,
            userGoal,
            activeCommunityId,
            ct);
        var permContext = await permissionEvaluator.ResolveContextAsync(
            userPrincipal,
            db,
            ct,
            detectedCommunityId,
            userGoal);

        var targetCommunityId = detectedCommunityId
            ?? activeCommunityId
            ?? (permContext.AllowedCommunityIds.Count > 0 ? permContext.AllowedCommunityIds[0] : Guid.Empty);

        var effectivePropertyName = string.Empty;
        if (targetCommunityId != Guid.Empty)
        {
            effectivePropertyName = await db.Communities
                .AsNoTracking()
                .Where(c => c.Id == targetCommunityId)
                .Select(c => c.Name)
                .FirstOrDefaultAsync(ct) ?? string.Empty;
        }

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            CommunityId = targetCommunityId,
            AgentName = "MuseSparkAutomationAgent",
            TriggerSummary = userGoal.Length > 200 ? userGoal[..200] : userGoal,
            ModelName = SparkModelId,
            StartedAt = DateTime.UtcNow
        };

        var stepsLog = new List<SparkAgentLiveStepEvent>();
        var scratchpad = new List<ScratchpadStepRecord>();
        var stepCounter = 1;
        var totalLatencyMs = 0;
        var usedLiveApi = false;
        var finalMarkdown = string.Empty;

        // If resuming with user clarification response
        if (!string.IsNullOrWhiteSpace(userClarificationResponse))
        {
            scratchpad.Add(new ScratchpadStepRecord(
                Iteration: 0,
                Thought: "User supplied clarification answer.",
                ToolName: "ask_user_clarification",
                Arguments: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["response"] = userClarificationResponse },
                IsPermitted: true,
                DenialReason: null,
                ObservationJson: JsonSerializer.Serialize(new { status = "Answered", userResponse = userClarificationResponse }),
                ObservationSummary: $"User response: {userClarificationResponse}"));
        }

        while (stepCounter <= 8)
        {
            ct.ThrowIfCancellationRequested();

            var workspaceContextJson = JsonSerializer.Serialize(new
            {
                activeCommunityId = targetCommunityId,
                activeCommunityName = effectivePropertyName,
                role = permContext.Role.ToString(),
                allowedCommunities = permContext.AllowedCommunityIds,
                uiLocale = normalizedLocale
            }, JsonOpts);

            var accountPermissionJson = JsonSerializer.Serialize(new
            {
                userId = permContext.UserId,
                role = permContext.Role.ToString(),
                isFirmManager = permContext.IsFirmManager,
                grantedPermissions = permContext.GrantedPermissions.ToString(),
                linkedUnitNumber = permContext.LinkedUnitNumber,
                resolvedForProperty = effectivePropertyName
            }, JsonOpts);

            var toolCatalogJson = FullToolCatalogJson;
            var scratchpadHistoryJson = JsonSerializer.Serialize(scratchpad, JsonOpts);

            // Stream thinking event
            progress?.Report(new SparkAgentLiveStepEvent(
                RunId: run.Id,
                StepNumber: stepCounter,
                Phase: "Thinking",
                Thought: $"Analyzing goal and system state (Iteration {stepCounter})...",
                ToolName: null,
                ToolArgumentsJson: null,
                PermissionGranted: null,
                PermissionBadgeText: null,
                ObservationJson: null,
                FinalMarkdown: null,
                DeepLinkUrl: null,
                LatencyMs: 15));

            var turnResult = await museSparkClient.RunAgenticTurnAsync(
                userGoal,
                accountPermissionJson,
                workspaceContextJson,
                toolCatalogJson,
                scratchpadHistoryJson,
                ct,
                normalizedLocale);

            totalLatencyMs += turnResult.LatencyMs;
            if (turnResult.UsedLiveApi)
            {
                usedLiveApi = true;
            }

            // Case A: Model emitted Final Answer
            if (turnResult.IsFinalAnswer)
            {
                finalMarkdown = CleanMarkdownOutput(turnResult.FinalResponseMarkdown);
                if (ContainsRegistrationLinkReference(finalMarkdown)
                    && !ObservationsContainRegistrationLink(scratchpad))
                {
                    finalMarkdown = "I did not generate a registration link because no successful registration-link result is available in this run. Which building and unit should receive the link? Please confirm the housing association, building, and unit so the link can be generated from a verified target.";
                }
                var finalEv = new SparkAgentLiveStepEvent(
                    RunId: run.Id,
                    StepNumber: stepCounter,
                    Phase: "FinalAnswer",
                    Thought: turnResult.Thinking,
                    ToolName: null,
                    ToolArgumentsJson: null,
                    PermissionGranted: true,
                    PermissionBadgeText: "COMPLETE",
                    ObservationJson: null,
                    FinalMarkdown: finalMarkdown,
                    DeepLinkUrl: null,
                    LatencyMs: turnResult.LatencyMs);
                stepsLog.Add(finalEv);
                progress?.Report(finalEv);

                run.Steps.Add(new AgentStep
                {
                    AgentRunId = run.Id,
                    StepOrder = stepCounter,
                    StepTitle = "Final Answer",
                    ModelOrToolName = SparkModelId,
                    Detail = finalMarkdown,
                    LatencyMs = turnResult.LatencyMs
                });

                break;
            }

            // Case B: Model emitted Tool Call(s)
            if (turnResult.ToolCalls.Count > 0)
            {
                var toolCall = turnResult.ToolCalls[0];
                if (!toolCall.Arguments.ContainsKey("uiLocale"))
                {
                    toolCall.Arguments["uiLocale"] = normalizedLocale;
                }

                // If tool specifies a target communityId, re-resolve the user's highest permission for that property
                if ((toolCall.Arguments.TryGetValue("communityId", out var argComStr) || toolCall.Arguments.TryGetValue("targetCommunityId", out argComStr))
                    && Guid.TryParse(argComStr, out var argComId)
                    && argComId != Guid.Empty
                    && argComId != targetCommunityId)
                {
                    var reResolved = await permissionEvaluator.ResolveContextAsync(userPrincipal, db, ct, argComId, userGoal);
                    if (reResolved.CanAccessCommunity(argComId))
                    {
                        permContext = reResolved;
                        targetCommunityId = argComId;
                        effectivePropertyName = await db.Communities
                            .AsNoTracking()
                            .Where(c => c.Id == targetCommunityId)
                            .Select(c => c.Name)
                            .FirstOrDefaultAsync(ct) ?? effectivePropertyName;
                    }
                }

                var argsJson = JsonSerializer.Serialize(toolCall.Arguments, JsonOpts);
                var humanInputSummary = BuildHumanInputSummary(toolCall.Arguments);

                // Permission Evaluation
                var permCheck = permissionEvaluator.EvaluateToolPermission(permContext, toolCall.ToolName, toolCall.Arguments);
                var permBadge = permCheck.IsPermitted ? "PERMITTED" : "DENIED";

                progress?.Report(new SparkAgentLiveStepEvent(
                    RunId: run.Id,
                    StepNumber: stepCounter,
                    Phase: "PermissionCheck",
                    Thought: toolCall.Thought,
                    ToolName: toolCall.ToolName,
                    ToolArgumentsJson: argsJson,
                    PermissionGranted: permCheck.IsPermitted,
                    PermissionBadgeText: permBadge,
                    ObservationJson: null,
                    FinalMarkdown: null,
                    DeepLinkUrl: null,
                    LatencyMs: 5,
                    HumanInputSummary: humanInputSummary));

                if (!permCheck.IsPermitted)
                {
                    // Append permission denial audit event with SHA-256 hash chaining
                    await AppendPermissionDeniedAuditEventAsync(
                        db,
                        targetCommunityId,
                        permContext.Role,
                        permContext.ActiveMembershipId,
                        $"Tool: {toolCall.ToolName} | Arguments: {argsJson}",
                        permCheck.DenialReason ?? "Unauthorized execution attempt blocked by SparkAgentPermissionEvaluator.",
                        ct);

                    var denialObsJson = JsonSerializer.Serialize(new
                    {
                        error = "PERMISSION_DENIED",
                        denialReason = permCheck.DenialReason,
                        requiredFlags = permCheck.RequiredFlags.ToString(),
                        missingFlags = permCheck.MissingFlags.ToString()
                    }, JsonOpts);

                    var obsEv = new SparkAgentLiveStepEvent(
                        RunId: run.Id,
                        StepNumber: stepCounter,
                        Phase: "Observation",
                        Thought: toolCall.Thought,
                        ToolName: toolCall.ToolName,
                        ToolArgumentsJson: argsJson,
                        PermissionGranted: false,
                        PermissionBadgeText: "PERMISSION_DENIED",
                        ObservationJson: denialObsJson,
                        FinalMarkdown: null,
                        DeepLinkUrl: null,
                        LatencyMs: 10,
                        HumanInputSummary: humanInputSummary,
                        ObservationSummary: permCheck.DenialReason ?? "Permission denied for active account role.",
                        Highlights: new[] { $"Required Permission: {permCheck.RequiredFlags}", $"Active Role: {permContext.Role}" });
                    stepsLog.Add(obsEv);
                    progress?.Report(obsEv);

                    scratchpad.Add(new ScratchpadStepRecord(
                        Iteration: stepCounter,
                        Thought: toolCall.Thought,
                        ToolName: toolCall.ToolName,
                        Arguments: toolCall.Arguments,
                        IsPermitted: false,
                        DenialReason: permCheck.DenialReason,
                        ObservationJson: denialObsJson,
                        ObservationSummary: permCheck.DenialReason ?? "Permission Denied"));

                    run.Steps.Add(new AgentStep
                    {
                        AgentRunId = run.Id,
                        StepOrder = stepCounter,
                        StepTitle = toolCall.StepTitle,
                        ModelOrToolName = toolCall.ToolName,
                        Detail = $"DENIED: {permCheck.DenialReason}",
                        LatencyMs = 10
                    });

                    if (stepByStepMode)
                    {
                        break;
                    }

                    stepCounter++;
                    continue;
                }

                // If tool is ask_user_clarification
                if (toolCall.ToolName.Equals("ask_user_clarification", StringComparison.OrdinalIgnoreCase))
                {
                    var question = toolCall.Arguments.GetValueOrDefault("question") ?? "Clarification requested.";
                    var options = ParseOptionsList(toolCall.Arguments.GetValueOrDefault("options"));
                    var clarEv = new SparkAgentLiveStepEvent(
                        RunId: run.Id,
                        StepNumber: stepCounter,
                        Phase: "AwaitingClarification",
                        Thought: toolCall.Thought,
                        ToolName: toolCall.ToolName,
                        ToolArgumentsJson: argsJson,
                        PermissionGranted: true,
                        PermissionBadgeText: "AWAITING_INPUT",
                        ObservationJson: null,
                        FinalMarkdown: null,
                        DeepLinkUrl: null,
                        LatencyMs: 10,
                        ClarificationOptions: options,
                        HumanInputSummary: humanInputSummary,
                        ObservationSummary: question);
                    stepsLog.Add(clarEv);
                    progress?.Report(clarEv);

                    run.Steps.Add(new AgentStep
                    {
                        AgentRunId = run.Id,
                        StepOrder = stepCounter,
                        StepTitle = toolCall.StepTitle,
                        ModelOrToolName = toolCall.ToolName,
                        Detail = $"Clarification: {question}",
                        LatencyMs = 10
                    });

                    await PersistRunTraceAsync(db, run, totalLatencyMs, usedLiveApi, ct);

                    return new AgentRunResult(
                        RunId: run.Id,
                        UserGoal: userGoal,
                        IsCompleted: false,
                        FinalResponseMarkdown: string.Empty,
                        Steps: stepsLog,
                        TotalLatencyMs: totalLatencyMs,
                        UsedLiveApi: usedLiveApi,
                        IsAwaitingClarification: true,
                        ClarificationQuestion: question,
                        ClarificationOptions: options,
                        EffectiveRole: permContext.Role.ToString(),
                        EffectivePropertyName: effectivePropertyName);
                }

                // Execute Tool. A deterministic clarification-required result is
                // structural (not model text), so convert it to the existing
                // ask_user_clarification pause instead of rendering an error.
                var toolObs = await toolExecutor.ExecuteToolAsync(toolCall.ToolName, toolCall.Arguments, permContext, targetCommunityId, ct);
                if (toolObs.RequiresClarification)
                {
                    var clarificationQuestion = string.IsNullOrWhiteSpace(toolObs.ClarificationQuestion)
                        ? toolObs.Summary
                        : toolObs.ClarificationQuestion;
                    var clarificationEv = new SparkAgentLiveStepEvent(
                        RunId: run.Id,
                        StepNumber: stepCounter,
                        Phase: "AwaitingClarification",
                        Thought: toolCall.Thought,
                        ToolName: "ask_user_clarification",
                        ToolArgumentsJson: argsJson,
                        PermissionGranted: true,
                        PermissionBadgeText: "AWAITING_INPUT",
                        ObservationJson: toolObs.ObservationJson,
                        FinalMarkdown: null,
                        DeepLinkUrl: null,
                        LatencyMs: 10,
                        ClarificationOptions: toolObs.ClarificationOptions,
                        HumanInputSummary: humanInputSummary,
                        ObservationSummary: clarificationQuestion);
                    stepsLog.Add(clarificationEv);
                    progress?.Report(clarificationEv);

                    run.Steps.Add(new AgentStep
                    {
                        AgentRunId = run.Id,
                        StepOrder = stepCounter,
                        StepTitle = "Ask User Clarification",
                        ModelOrToolName = "ask_user_clarification",
                        Detail = $"Clarification: {clarificationQuestion}",
                        LatencyMs = 10
                    });

                    scratchpad.Add(new ScratchpadStepRecord(
                        Iteration: stepCounter,
                        Thought: toolCall.Thought,
                        ToolName: toolCall.ToolName,
                        Arguments: toolCall.Arguments,
                        IsPermitted: true,
                        DenialReason: null,
                        ObservationJson: toolObs.ObservationJson,
                        ObservationSummary: clarificationQuestion));

                    await PersistRunTraceAsync(db, run, totalLatencyMs, usedLiveApi, ct);

                    return new AgentRunResult(
                        RunId: run.Id,
                        UserGoal: userGoal,
                        IsCompleted: false,
                        FinalResponseMarkdown: string.Empty,
                        Steps: stepsLog,
                        TotalLatencyMs: totalLatencyMs,
                        UsedLiveApi: usedLiveApi,
                        IsAwaitingClarification: true,
                        ClarificationQuestion: clarificationQuestion,
                        ClarificationOptions: toolObs.ClarificationOptions,
                        EffectiveRole: permContext.Role.ToString(),
                        EffectivePropertyName: effectivePropertyName);
                }
                var (highlights, generatedImageDataUrl, generatedImageMetaSummary) = ExtractObservationHighlightsAndImage(toolObs.ObservationJson);
                var execEv = new SparkAgentLiveStepEvent(
                    RunId: run.Id,
                    StepNumber: stepCounter,
                    Phase: "Observation",
                    Thought: toolCall.Thought,
                    ToolName: toolCall.ToolName,
                    ToolArgumentsJson: argsJson,
                    PermissionGranted: true,
                    PermissionBadgeText: "EXECUTED",
                    ObservationJson: toolObs.ObservationJson,
                    FinalMarkdown: null,
                    DeepLinkUrl: toolObs.DeepLinkUrl,
                    LatencyMs: 25,
                    HumanInputSummary: humanInputSummary,
                    ObservationSummary: toolObs.Summary,
                    GeneratedImageDataUrl: generatedImageDataUrl,
                    GeneratedImageMetaSummary: generatedImageMetaSummary,
                    Highlights: highlights);
                stepsLog.Add(execEv);
                progress?.Report(execEv);

                scratchpad.Add(new ScratchpadStepRecord(
                    Iteration: stepCounter,
                    Thought: toolCall.Thought,
                    ToolName: toolCall.ToolName,
                    Arguments: toolCall.Arguments,
                    IsPermitted: true,
                    DenialReason: null,
                    ObservationJson: toolObs.ObservationJson,
                    ObservationSummary: toolObs.Summary));

                run.Steps.Add(new AgentStep
                {
                    AgentRunId = run.Id,
                    StepOrder = stepCounter,
                    StepTitle = toolCall.StepTitle,
                    ModelOrToolName = toolCall.ToolName,
                    Detail = toolObs.Summary,
                    LatencyMs = 25
                });

                if (stepByStepMode)
                {
                    break;
                }
            }
            else
            {
                break;
            }

            stepCounter++;
        }

        await PersistRunTraceAsync(db, run, totalLatencyMs, usedLiveApi, ct);

        return new AgentRunResult(
            RunId: run.Id,
            UserGoal: userGoal,
            IsCompleted: !string.IsNullOrWhiteSpace(finalMarkdown),
            FinalResponseMarkdown: finalMarkdown,
            Steps: stepsLog,
            TotalLatencyMs: totalLatencyMs,
            UsedLiveApi: usedLiveApi,
            EffectiveRole: permContext.Role.ToString(),
            EffectivePropertyName: effectivePropertyName);
    }

    private static string CleanMarkdownOutput(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }
        var cleaned = Regex.Replace(raw, @"\[JSON-BEGIN\][\s\S]*?\[JSON-END\]", string.Empty, RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"```json[\s\S]*?```", string.Empty, RegexOptions.IgnoreCase).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? raw.Trim() : cleaned;
    }

    private static bool ContainsRegistrationLinkReference(string? markdown)
        => !string.IsNullOrWhiteSpace(markdown)
           && markdown.Contains("/register-resident/", StringComparison.OrdinalIgnoreCase);

    private static bool ObservationsContainRegistrationLink(IReadOnlyList<ScratchpadStepRecord> scratchpad)
    {
        foreach (var step in scratchpad)
        {
            if (string.IsNullOrWhiteSpace(step.ObservationJson))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(step.ObservationJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (doc.RootElement.TryGetProperty("registrationUrl", out var url)
                    && url.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(url.GetString()))
                {
                    return true;
                }

                if (doc.RootElement.TryGetProperty("plainToken", out var token)
                    && token.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(token.GetString()))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static string BuildHumanInputSummary(IReadOnlyDictionary<string, string> args)
    {
        if (args.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var kv in args)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)
                || string.Equals(kv.Key, "uiLocale", StringComparison.OrdinalIgnoreCase)
                || kv.Value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var val = kv.Value.Length > 48 ? kv.Value[..47] + "…" : kv.Value;
            parts.Add($"{FormatFriendlyKey(kv.Key)}: {val}");
            if (parts.Count >= 4)
            {
                break;
            }
        }
        return string.Join(" • ", parts);
    }

    private static (IReadOnlyList<string> Highlights, string? ImageDataUrl, string? ImageMetaSummary) ExtractObservationHighlightsAndImage(string? observationJson)
    {
        var highlights = new List<string>();
        string? imageDataUrl = null;
        string? modelName = null;
        string? resolution = null;

        if (string.IsNullOrWhiteSpace(observationJson))
        {
            return (highlights, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(observationJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString() ?? string.Empty;
                        if (s.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                        {
                            imageDataUrl ??= s;
                            continue;
                        }
                        if (string.Equals(prop.Name, "modelName", StringComparison.OrdinalIgnoreCase))
                        {
                            modelName = s;
                        }
                        if (string.Equals(prop.Name, "resolution", StringComparison.OrdinalIgnoreCase))
                        {
                            resolution = s;
                        }
                        if (!string.IsNullOrWhiteSpace(s) && s.Length <= 160)
                        {
                            highlights.Add($"{FormatFriendlyKey(prop.Name)}: {s}");
                        }
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        highlights.Add($"{FormatFriendlyKey(prop.Name)}: {prop.Value.GetRawText()}");
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Array && string.Equals(prop.Name, "renderedInfoLines", StringComparison.OrdinalIgnoreCase))
                    {
                        var lines = prop.Value.EnumerateArray()
                            .Where(x => x.ValueKind == JsonValueKind.String)
                            .Select(x => x.GetString())
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .ToList();
                        if (lines.Count > 0)
                        {
                            highlights.Add($"Rendered on image: {string.Join(" | ", lines)}");
                        }
                    }
                }
            }
        }
        catch
        {
        }

        string? imageMeta = null;
        if (!string.IsNullOrWhiteSpace(imageDataUrl))
        {
            imageMeta = $"{modelName ?? "muse-image-1.0"} • {resolution ?? "640×400 PNG"}";
        }

        return (highlights.Take(6).ToList(), imageDataUrl, imageMeta);
    }

    private static string FormatFriendlyKey(string camelKey)
    {
        if (string.IsNullOrWhiteSpace(camelKey)) return string.Empty;
        var spaced = Regex.Replace(camelKey, "([a-z])([A-Z])", "$1 $2");
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private static async Task AppendPermissionDeniedAuditEventAsync(
        IAppDbContext db,
        Guid communityId,
        WorkspaceSecurityRole actorRole,
        Guid? actorMembershipId,
        string resourceDescriptor,
        string summary,
        CancellationToken ct)
    {
        var hashes = await db.SecurityAuditEvents
            .AsNoTracking()
            .Where(e => e.CommunityId == communityId)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => e.EntrySha256Hash)
            .ToListAsync(ct);
        var previousChainHash = hashes.Count == 0 ? "GENESIS-0000000000000000" : hashes[^1];

        var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(
            communityId,
            actorRole,
            "SPARK_AGENT_PERMISSION_DENIED",
            "Warning",
            resourceDescriptor,
            summary,
            previousChainHash);

        ev.ActorMembershipId = actorMembershipId;
        db.SecurityAuditEvents.Add(ev);
        await db.SaveChangesAsync(ct);
    }

    private static async Task PersistRunTraceAsync(
        IAppDbContext db,
        AgentRun run,
        int totalLatencyMs,
        bool usedLiveApi,
        CancellationToken ct)
    {
        try
        {
            run.TotalLatencyMs = totalLatencyMs;
            run.UsedLiveApi = usedLiveApi;
            var existing = await db.AgentRuns.FirstOrDefaultAsync(r => r.Id == run.Id, ct);
            if (existing == null)
            {
                db.AgentRuns.Add(run);
            }
            await db.SaveChangesAsync(ct);
        }
        catch
        {
        }
    }

    public static readonly string FullToolCatalogJson = BuildFullToolCatalog();

    private static string BuildFullToolCatalog()
    {
        var tools = new object[]
        {
            new
            {
                name = "check_account_permission",
                description = "Checks whether the active account has permission to execute a specific tool on a property.",
                parameters = new { toolName = "string (name of tool to check, e.g. 'create_unit_wizard')" }
            },
            new
            {
                name = "list_account_capabilities",
                description = "Lists all permissions, roles, allowed properties, and firm attributes for the active user. (Aliases: list_available_tools, list_tools, get_tools, available_tools).",
                parameters = new { }
            },
            new
            {
                name = "ask_user_clarification",
                description = "CRITICAL: Call this tool whenever user intent is ambiguous, when essential parameters (property, unit number, resident name/email) are missing, or when you are not 100% sure how to proceed. Never invent or guess arguments.",
                parameters = new
                {
                    question = "string (the exact question to ask the user)",
                    options = "string (optional comma-separated choices for quick selection, e.g. 'Yes, No' or 'Unit 1, Unit 2')",
                    inputHint = "string (optional short placeholder or hint for the input box)"
                }
            },
            new
            {
                name = "execute_database_query",
                description = "Queries real database entities to look up IDs, statuses, or fields. Supported entities: units, communities, work_orders, resolutions, meters, legal_documents.",
                parameters = new
                {
                    entityName = "string ('units'|'communities'|'work_orders'|'resolutions'|'meters'|'legal_documents')",
                    filter = "string (optional predicate, e.g. 'UnitNumber == \"A-101\"')",
                    communityId = "string (optional property guid)"
                }
            },
            new
            {
                name = "query_system_entities",
                description = "Searches system entities by search term, status, or property.",
                parameters = new
                {
                    entityName = "string ('units'|'communities'|'buildings'|'work_orders'|'resolutions'|'meters'|'legal_documents')",
                    searchTerm = "string (e.g. '101' or 'Oakridge')",
                    status = "string (optional filter)",
                    communityId = "string (optional property guid)"
                }
            },
            new
            {
                name = "get_workspace_overview",
                description = "Retrieves high-level property and portfolio KPIs, unit counts, arrears, and active work orders.",
                parameters = new { communityId = "string (optional property guid)" }
            },
            new
            {
                name = "get_weather_and_outages",
                description = "Checks live utility outages and weather radar for a property via ScanUtilitySourcesAndWeatherAsync. Use for any weather, outage, storm, snow, or grid question. (Aliases: weather, get_weather, check_weather, scan_outages.)",
                parameters = new { communityId = "string (optional property guid)" }
            },
            new
            {
                name = "switch_ui_context_or_navigate",
                description = "Navigates the UI to a specific screen or property.",
                parameters = new
                {
                    targetUrl = "string (e.g. '/properties/{communityId}/units')",
                    communityId = "string (optional property guid)"
                }
            },
            new
            {
                name = "update_firm_contacts",
                description = "Updates firm emergency phone, dispatch email, or office address.",
                parameters = new
                {
                    emergencyPhone = "string",
                    dispatchEmail = "string",
                    officeAddress = "string"
                }
            },
            new
            {
                name = "create_scoped_invitation",
                description = "Generates a cryptographic zero-disclosure invitation link for a new property manager or board member.",
                parameters = new
                {
                    communityId = "string",
                    email = "string",
                    role = "string ('PropertyManager'|'BoardMemberReadOnly'|'Accountant'|'Resident')",
                    unitNumber = "string (optional for resident invitations)"
                }
            },
            new
            {
                name = "approve_or_update_membership",
                description = "Approves, rejects, or adjusts role/permissions for a pending workspace member.",
                parameters = new
                {
                    membershipId = "string (guid)",
                    newStatus = "string ('Approved'|'Rejected')",
                    newRole = "string ('FirmManager'|'PropertyManager'|'Resident')"
                }
            },
            new
            {
                name = "create_firm_manager_group",
                description = "Creates a cross-property manager collaboration channel.",
                parameters = new { groupName = "string", communityId = "string (optional)" }
            },
            new
            {
                name = "post_firm_manager_message",
                description = "Posts an internal dispatch message to property managers or board members.",
                parameters = new { communityId = "string", subject = "string", messageBody = "string" }
            },
            new
            {
                name = "create_community",
                description = "Registers a new managed residential community/condominium association.",
                parameters = new
                {
                    name = "string",
                    code = "string",
                    country = "string ('PL'|'US')",
                    streetAddress = "string",
                    postalCode = "string",
                    city = "string"
                }
            },
            new
            {
                name = "discover_property_from_address",
                description = "Enriches a property by address using public registries to identify local utility operators, gross tariffs, and emergency authorities.",
                parameters = new
                {
                    streetAddress = "string",
                    city = "string",
                    postalCode = "string",
                    country = "string ('PL'|'US')"
                }
            },
            new
            {
                name = "update_community_profile",
                description = "Updates condominium legal details, bank IBAN, or tax ID.",
                parameters = new
                {
                    communityId = "string",
                    name = "string",
                    bankAccount = "string",
                    taxId = "string"
                }
            },
            new
            {
                name = "upsert_community_contact",
                description = "Adds or updates an emergency authority or contractor contact for a community.",
                parameters = new
                {
                    communityId = "string",
                    name = "string",
                    phone = "string",
                    role = "string",
                    category = "string ('Emergency'|'Maintenance'|'Municipal')"
                }
            },
            new
            {
                name = "delete_community_contact",
                description = "Deletes a community contact entry.",
                parameters = new { contactId = "string (guid)" }
            },
            new
            {
                name = "check_property_documents",
                description = "Lists property documents (statutes, fee schedules, house rules) filed in the property documents panel so answers can cite document codes and titles.",
                parameters = new
                {
                    communityId = "string (optional property guid)",
                    searchTerm = "string (optional code/title/text filter)",
                    category = "string (optional LegalDocumentKind filter)"
                }
            },
            new
            {
                name = "add_property_document",
                description = "Files a property document (title, code, category, verbatim excerpt). Requires ManagePropertySettings. (Alias: upload_property_document supports attachmentDataUrl file bytes + boundResolutionId.)",
                parameters = new
                {
                    communityId = "string (optional property guid)",
                    documentCode = "string (e.g. 'HOUSE-RULES-2026')",
                    title = "string",
                    category = "string (LegalDocumentKind enum name)",
                    verbatimText = "string (excerpt, file name or notes)",
                    attachmentFileName = "string (optional upload file name)",
                    attachmentContentType = "string (optional MIME type)",
                    attachmentDataUrl = "string (optional data: URL file bytes, 4 MB limit)",
                    boundResolutionId = "string (optional resolution guid to bind)"
                }
            },
            new
            {
                name = "bind_document_to_resolution",
                description = "Binds a filed property document to a governance decision so votes cite the attachment. (Aliases: bind_property_document, unbind_document_from_resolution.) Requires ManagePropertySettings.",
                parameters = new
                {
                    communityId = "string (optional property guid)",
                    documentId = "string (document guid; or pass documentCode)",
                    documentCode = "string (optional code lookup when id unknown)",
                    resolutionId = "string (decision guid)"
                }
            },
            new
            {
                name = "create_building",
                description = "Creates a new standalone building with the standard 3-stack riser structure (STACK-A1, STACK-A2, STACK-B1). Reuses an existing building with the same name instead of creating a duplicate.",
                parameters = new
                {
                    communityId = "string (guid)",
                    name = "string (call query_system_entities with entityType 'buildings' first; reuse the existing buildingId when the name already exists)",
                    streetAddress = "string",
                    floorsCount = "int",
                    stairwellsCount = "int",
                    constructionYear = "int"
                }
            },
            new
            {
                name = "upsert_riser_stack_or_structure",
                description = "Creates or updates a vertical utility riser stack in a building (e.g. STACK-A1).",
                parameters = new
                {
                    buildingId = "string",
                    stackCode = "string (e.g. 'STACK-A1')",
                    systemType = "string ('WaterPotable'|'CentralHeating'|'Drainage')"
                }
            },
            new
            {
                name = "create_unit_wizard",
                description = "Creates an individual unit/apartment in a building with surface area and ownership share fraction.",
                parameters = new
                {
                    buildingId = "string",
                    unitNumber = "string (e.g. 'A-101')",
                    floor = "int",
                    squareMeters = "double (e.g. 62.5)",
                    shareFractionNumerator = "long (e.g. 625)",
                    shareFractionDenominator = "long (e.g. 10000)"
                }
            },
            new
            {
                name = "update_unit_details",
                description = "Updates surface area, occupancy capacity, or notes on a unit.",
                parameters = new
                {
                    unitId = "string",
                    squareMeters = "double",
                    occupancyCount = "int"
                }
            },
            new
            {
                name = "bulk_generate_floor_units",
                description = "Generates all apartments across a specified building floor in bulk.",
                parameters = new
                {
                    buildingId = "string",
                    floorNumber = "int",
                    unitsPerFloor = "int"
                }
            },
            new
            {
                name = "delete_unit",
                description = "Deletes an unassigned apartment unit.",
                parameters = new { unitId = "string" }
            },
            new
            {
                name = "delete_building",
                description = "Deletes an empty building. Buildings with units cannot be deleted; delete units first. Call query_system_entities with entityType 'buildings' first to list building Id and Name.",
                parameters = new
                {
                    communityId = "string (guid; ask instead of guessing if empty or ambiguous)",
                    buildingId = "string (guid; ask instead of guessing if empty or ambiguous)",
                    buildingName = "string (required when the name is ambiguous)",
                    confirm = "bool (must be true to delete)"
                }
            },
            new
            {
                name = "create_or_update_resident_profile",
                description = "Creates or updates a resident profile in the directory, and optionally links/assigns them to an apartment unit. (Aliases: create_resident_profile, create_resident, create_resident_wizard, register_resident, add_resident).",
                parameters = new
                {
                    communityId = "string (guid of community)",
                    fullName = "string (e.g. 'Anna Nowak')",
                    firstName = "string",
                    lastName = "string",
                    email = "string (required unique resident email; never reuse a manager email or 'resident@example.local' placeholder for a real person)",
                    phone = "string",
                    unitNumber = "string (optional unit to link, e.g. 'A-101'; preserve the exact letter prefix, never strip to digits)",
                    unitId = "string (optional unit guid to link)",
                    preferredLanguage = "string ('PL'|'EN'|'ES'|'DE')",
                    isPrimaryResident = "bool"
                }
            },
            new
            {
                name = "assign_resident_to_unit",
                description = "Links an existing resident profile to an apartment unit with tenancy dates.",
                parameters = new
                {
                    residentProfileId = "string (guid)",
                    unitId = "string (guid)",
                    isPrimary = "bool"
                }
            },
            new
            {
                name = "execute_resident_move_out",
                description = "Records a resident move-out and terminates active tenancy/lease link.",
                parameters = new
                {
                    assignmentId = "string (guid)",
                    moveOutDate = "string (yyyy-MM-dd)"
                }
            },
            new
            {
                name = "generate_registration_link",
                description = "Issues a single-use self-registration URL with a 32-byte zero-disclosure token for a resident.",
                parameters = new
                {
                    communityId = "string (guid; ask instead of guessing if empty or ambiguous)",
                    buildingId = "string (guid; ask instead of guessing if empty or ambiguous)",
                    buildingName = "string (required when a unit number exists in multiple buildings)",
                    unitNumber = "string (e.g. 'A-101'; ask instead of guessing if empty)",
                    unitId = "string (guid; prefer the exact unitId from a prior tool observation, never Guid.Empty)",
                    targetUnitId = "string (guid alias for unitId; prefer the exact unitId from a prior tool observation, never Guid.Empty)",
                    residentName = "string",
                    residentEmail = "string"
                }
            },
            new
            {
                name = "revoke_registration_link",
                description = "Revokes a pending resident self-registration link.",
                parameters = new { tokenId = "string (guid)", communityId = "string" }
            },
            new
            {
                name = "review_registration_request",
                description = "Approves or rejects a resident self-registration queue entry.",
                parameters = new
                {
                    requestId = "string (guid)",
                    decision = "string ('Approve'|'Reject')"
                }
            },
            new
            {
                name = "upsert_or_remove_household_member",
                description = "Adds, updates, or removes household co-occupants (spouse, child, pet).",
                parameters = new
                {
                    residentProfileId = "string (guid)",
                    fullName = "string",
                    relationship = "string ('Spouse'|'Child'|'Tenant')"
                }
            },
            new
            {
                name = "add_resident_document",
                description = "Uploads or links an ownership deed or lease agreement to a resident profile.",
                parameters = new
                {
                    residentProfileId = "string (guid)",
                    title = "string",
                    documentType = "string ('Deed'|'Lease'|'ID')"
                }
            },
            new
            {
                name = "submit_meter_reading",
                description = "Submits a sub-meter reading with optional dial photo OCR.",
                parameters = new
                {
                    communityId = "string",
                    meterId = "string (guid)",
                    readingValue = "double",
                    simulatedReadingOverride = "double"
                }
            },
            new
            {
                name = "run_bulk_utility_settlement",
                description = "Runs the deterministic bulk-to-sub-meter utility reconciliation engine for cold water, heating, or US RUBS.",
                parameters = new
                {
                    communityId = "string",
                    periodName = "string (e.g. 'Q3 2026')",
                    utilityType = "string ('ColdWater'|'Heating'|'Gas')"
                }
            },
            new
            {
                name = "match_bank_transfers",
                description = "Matches incoming bank transfer titles to resident accounts via deterministic FIFO rules.",
                parameters = new { communityId = "string" }
            },
            new
            {
                name = "run_inflation_and_power_audit",
                description = "Audits contracted power tariffs against peak measured demand and runs 10-year inflation simulations.",
                parameters = new { communityId = "string", targetYear = "int" }
            },
            new
            {
                name = "generate_czynsz_assessment",
                description = "Generates and saves an editable per-unit czynsz (rent/maintenance fee) assessment with standard Polish components and custom lines.",
                parameters = new
                {
                    unitId = "string (guid)",
                    unitNumber = "string (e.g. 'A-101'; resolved when unitId is empty)",
                    period = "string (yyyy-MM, e.g. '2026-11')",
                    lines = "string (JSON array of {key,label,kind,quantity,unitPrice,isCredit})"
                }
            },
            new
            {
                name = "generate_arrears_or_mediation_action",
                description = "Drafts polite arrears reminders, installment agreements, or neighbor mediation notices.",
                parameters = new
                {
                    unitId = "string",
                    arrearsNoticeType = "string ('Reminder'|'PreLitigation'|'Mediation')"
                }
            },
            new
            {
                name = "transcribe_voice_or_audio",
                description = "Transcribes resident voice memos using Whisper ASR with multilingual translation.",
                parameters = new { audioBase64 = "string", audioUrl = "string" }
            },
            new
            {
                name = "submit_maintenance_request",
                description = "Submits a maintenance report, cross-referencing vertical riser stacks and checking active municipal outages.",
                parameters = new
                {
                    communityId = "string",
                    unitNumber = "string",
                    messageText = "string",
                    category = "string ('Plumbing'|'Electrical'|'HVAC'|'Structure')"
                }
            },
            new
            {
                name = "post_maintenance_message",
                description = "Posts a forum reply with optional photo to an existing maintenance thread.",
                parameters = new
                {
                    communityId = "string",
                    workOrderId = "string (guid)",
                    body = "string",
                    attachmentDataUrl = "string (optional data URL photo)"
                }
            },
            new
            {
                name = "assign_maintenance_contractor",
                description = "Assigns a contractor vendor to a maintenance thread and converts it to a task.",
                parameters = new
                {
                    communityId = "string",
                    workOrderId = "string (guid)",
                    vendorId = "string (guid)"
                }
            },
            new
            {
                name = "run_vision_inspection_or_verify_repair",
                description = "Segments damage area (m²) via SAM 3.1 and generates an independent investor BOQ estimate, or verifies contractor repair.",
                parameters = new
                {
                    workOrderId = "string (guid)",
                    inspectionType = "string ('Damage'|'RepairVerification')",
                    photoBase64 = "string"
                }
            },
            new
            {
                name = "evaluate_or_submit_modification_request",
                description = "Audits architectural remodeling (split-AC, solar, balcony glazing) against structural risers and statutes.",
                parameters = new
                {
                    unitId = "string",
                    description = "string",
                    category = "string ('Balcony'|'Facade'|'Solar'|'HVAC')"
                }
            },
            new
            {
                name = "run_governance_and_voting_workflow",
                description = "Calculates statutory voting tallies, verifies 1-owner-1-vote thresholds, and checks quorum.",
                parameters = new
                {
                    communityId = "string",
                    resolutionId = "string (guid)",
                    action = "string ('CheckQuorum'|'Tally'|'Adopt')"
                }
            },
            new
            {
                name = "upload_ballot_ocr",
                description = "Alias of run_governance_and_voting_workflow with workflowType=digitize; digitizes uploaded paper ballot images via OCR.",
                parameters = new
                {
                    communityId = "string",
                    resolutionId = "string (guid)",
                    workflowType = "string ('digitize')",
                    ballotImageDataUrl = "string (data URL, truncated in observation)"
                }
            },
            new
            {
                name = "post_announcement_or_direct_message",
                description = "Broadcasts announcements or emergency alerts to community residents.",
                parameters = new
                {
                    communityId = "string",
                    title = "string",
                    messageBody = "string",
                    targetAudience = "string ('AllResidents'|'StackA1'|'BuildingA')"
                }
            },
            new
            {
                name = "security_approvals_sos_and_vault",
                description = "Approves/rejects pending human-in-the-loop AiActions, reveals emergency access PINs, or triggers SOS broadcast.",
                parameters = new
                {
                    action = "string ('Approve'|'Reject'|'EmergencySosBroadcast')",
                    targetId = "string (guid)"
                }
            },
            new
            {
                name = "generate_custom_info_image",
                description = "Renders notice posters, shutoff valve guides, or waste segregation graphics.",
                parameters = new
                {
                    prompt = "string",
                    communityId = "string"
                }
            }
        };

        return JsonSerializer.Serialize(tools, JsonOpts);
    }

    private static IReadOnlyList<string> ParseOptionsList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<List<string>>(raw, JsonOpts);
            if (parsed != null && parsed.Count > 0) return parsed;
        }
        catch { }
        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
    }
}

public sealed record ScratchpadStepRecord(
    int Iteration,
    string Thought,
    string ToolName,
    Dictionary<string, string> Arguments,
    bool IsPermitted,
    string? DenialReason,
    string ObservationJson,
    string ObservationSummary);
