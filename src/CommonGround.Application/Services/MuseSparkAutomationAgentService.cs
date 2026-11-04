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
        if (IsFirmManager && MembershipStatus == WorkspaceMembershipStatus.Approved) return true;
        return (GrantedPermissions & flag) == flag;
    }

    public bool CanAccessCommunity(Guid communityId)
    {
        if (communityId == Guid.Empty) return true;
        if (IsFirmManager && MembershipStatus == WorkspaceMembershipStatus.Approved) return true;
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
    string? DeepLinkUrl = null);

public sealed class SparkAgentPermissionEvaluator
{
    public async Task<SparkAgentPermissionContext> ResolveContextAsync(
        ClaimsPrincipal? principal,
        IAppDbContext db,
        CancellationToken ct = default)
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

        // Introspect FirmMembership from database if available
        if (userId != Guid.Empty)
        {
            FirmMembership? membership = null;
            if (activeMembershipId != Guid.Empty)
            {
                membership = await db.FirmMemberships
                    .AsNoTracking()
                    .Include(m => m.PropertyAccesses)
                    .Include(m => m.UnitLinks)
                    .FirstOrDefaultAsync(m => m.Id == activeMembershipId, ct);
            }

            membership ??= await db.FirmMemberships
                .AsNoTracking()
                .Include(m => m.PropertyAccesses)
                .Include(m => m.UnitLinks)
                .FirstOrDefaultAsync(m => m.UserId == userId, ct);

            if (membership != null)
            {
                role = membership.Role;
                status = membership.Status;
                firmId ??= membership.ManagementFirmId;
                activeMembershipId = membership.Id;
                permissions = membership.GrantedPermissions != 0
                    ? membership.GrantedPermissions
                    : WorkspacePermissionPresets.DefaultForRole(role);

                foreach (var acc in membership.PropertyAccesses)
                {
                    if (!allowedCommunityIds.Contains(acc.CommunityId))
                    {
                        allowedCommunityIds.Add(acc.CommunityId);
                    }
                }

                if (membership.UnitLinks.Count > 0)
                {
                    var link = membership.UnitLinks[0];
                    linkedUnitId = link.UnitId;
                    var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == link.UnitId, ct);
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

    public SparkAgentPermissionEvaluationResult EvaluateToolPermission(
        SparkAgentPermissionContext context,
        string toolName,
        IReadOnlyDictionary<string, string> args)
    {
        var normalizedTool = (toolName ?? string.Empty).Trim().ToLowerInvariant();

        // CATEGORY A: Introspection, Clarification, DB Query, UI
        if (normalizedTool is "check_account_permission" or "list_account_capabilities" or "ask_user_clarification")
        {
            return new SparkAgentPermissionEvaluationResult(true, 0, 0);
        }

        if (normalizedTool is "execute_database_query" or "query_system_entities" or "get_workspace_overview")
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

        if (normalizedTool is "update_community_profile" or "upsert_community_contact" or "delete_community_contact")
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

        // CATEGORY D: Buildings & Units (Strictly Manager Only)
        if (normalizedTool is "create_building" or "upsert_riser_stack_or_structure" or "create_unit_wizard"
            or "update_unit_details" or "bulk_generate_floor_units" or "delete_unit")
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

        if (normalizedTool is "match_bank_transfers" or "run_inflation_and_power_audit")
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
        var normalized = (toolName ?? string.Empty).Trim().ToLowerInvariant();
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

                // D18. create_building
                case "create_building":
                {
                    var bld = await workspace.CreateBuildingAsync(
                        comId,
                        args.GetValueOrDefault("name") ?? "Building A",
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
                        stairwellsCount = bld.StairwellsCount
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
                    return MakeResult(new { stackId = stack.Id, stackCode = stack.Code, shutoff = stack.ShutoffValveLocation },
                        $"Upserted vertical riser stack '{stack.Code}' at {stack.ShutoffValveLocation}");
                }

                // D20. create_unit_wizard
                case "create_unit_wizard":
                {
                    var bldId = Guid.TryParse(args.GetValueOrDefault("buildingId"), out var bid) ? bid : Guid.Empty;
                    var unitNum = args.GetValueOrDefault("unitNumber") ?? "A-501";
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
                    var input = new UnitUpdateInput(
                        BuildingId: Guid.TryParse(args.GetValueOrDefault("buildingId"), out var bid) ? bid : Guid.Empty,
                        UnitNumber: args.GetValueOrDefault("unitNumber") ?? "101",
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

                // E24. create_or_update_resident_profile
                case "create_or_update_resident_profile":
                {
                    var profIdStr = args.GetValueOrDefault("residentProfileId");
                    if (Guid.TryParse(profIdStr, out var pid))
                    {
                        var input = new ResidentProfileUpdateInput(
                            args.GetValueOrDefault("firstName") ?? "Resident",
                            args.GetValueOrDefault("lastName") ?? "User",
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

                    var created = await workspace.CreateOrGetResidentProfileAsync(
                        context.ManagementFirmId,
                        context.UserId,
                        args.GetValueOrDefault("firstName") ?? "Resident",
                        args.GetValueOrDefault("lastName") ?? "User",
                        args.GetValueOrDefault("email") ?? "resident@example.local",
                        args.GetValueOrDefault("phone") ?? "+48 000 000 000",
                        args.GetValueOrDefault("preferredLanguage") ?? "EN",
                        args.GetValueOrDefault("emergencyContactName") ?? string.Empty,
                        args.GetValueOrDefault("emergencyContactPhone") ?? string.Empty,
                        args.GetValueOrDefault("notes") ?? string.Empty,
                        ct);
                    return MakeResult(new { residentProfileId = created.Id, accountNumber = created.ResidentAccountNumber, name = created.FullName },
                        $"Created resident profile {created.FullName} ({created.ResidentAccountNumber})", $"/admin/residents/{created.Id}");
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
                    return MakeResult(new
                    {
                        assignmentId = result.Id,
                        unitId = result.UnitId,
                        residentProfileId = result.ResidentProfileId,
                        status = result.Status.ToString()
                    }, $"Assigned resident to unit {unitId} (Status: {result.Status})", $"/properties/{comId}/units/{unitId}");
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
                    var unitId = Guid.TryParse(args.GetValueOrDefault("targetUnitId") ?? args.GetValueOrDefault("unitId"), out var tuid) ? tuid : Guid.Empty;
                    var mode = ParseEnum(args.GetValueOrDefault("approvalMode"), RegistrationApprovalMode.AutomaticAuthorized);
                    var linkGen = await workspace.GenerateResidentRegistrationLinkAsync(
                        comId,
                        unitId,
                        mode,
                        ParseEnum(args.GetValueOrDefault("intendedAssignmentType"), ResidentAssignmentType.Tenant),
                        DateTime.UtcNow.AddDays(int.TryParse(args.GetValueOrDefault("expiresInDays"), out var exp) ? exp : 14),
                        1,
                        args.GetValueOrDefault("labelOrReference") ?? "Registration Token",
                        context.EffectiveRole,
                        context.FullName,
                        context.ActiveMembershipId,
                        ct);
                    return MakeResult(new
                    {
                        tokenId = linkGen.TokenRecord.Id,
                        plainToken = linkGen.PlaintextToken,
                        registrationUrl = linkGen.RelativeUrl,
                        approvalMode = linkGen.TokenRecord.ApprovalMode.ToString(),
                        expiresAtUtc = linkGen.TokenRecord.ExpiresAtUtc
                    }, $"Generated Mode {(mode == RegistrationApprovalMode.AutomaticAuthorized ? "A" : "B")} registration link: {linkGen.RelativeUrl}", linkGen.RelativeUrl);
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

                // F36. generate_arrears_or_mediation_action
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
                    var workflowType = (args.GetValueOrDefault("workflowType") ?? "meeting_autopilot").ToLowerInvariant();
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

                    if (workflowType.Contains("ballot"))
                    {
                        var resId = Guid.TryParse(args.GetValueOrDefault("resolutionId"), out var rid) ? rid : Guid.Empty;
                        var tally = await workspace.DigitizePaperBallotSheetAsync(comId, resId, ct);
                        return MakeResult(new { isAdopted = tally.IsAdopted, sharesFor = tally.SharesFor, sharesAgainst = tally.SharesAgainst },
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

                default:
                    return MakeError($"Tool '{toolName}' is not recognized in the 43-tool catalog.");
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
                    query = query.Where(u => db.Communities.Any(c => c.Id == u.CommunityId && c.ManagementFirmId == context.ManagementFirmId.Value));
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
                    query = query.Where(c => c.ManagementFirmId == context.ManagementFirmId.Value);
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
                    query = query.Where(w => db.Communities.Any(c => c.Id == w.CommunityId && c.ManagementFirmId == context.ManagementFirmId.Value));
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
                    query = query.Where(r => db.Communities.Any(c => c.Id == r.CommunityId && c.ManagementFirmId == context.ManagementFirmId.Value));
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
                    unitQuery = unitQuery.Where(u => db.Communities.Any(c => c.Id == u.CommunityId && c.ManagementFirmId == context.ManagementFirmId.Value));
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

            default:
            {
                return MakeError($"Entity '{entityName}' is not indexed for dynamic database queries. Supported entities: units, communities, work_orders, resolutions, utility_meters.");
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
                q = q.Where(u => db.Communities.Any(c => c.Id == u.CommunityId && c.ManagementFirmId == context.ManagementFirmId.Value));
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
                q = q.Where(c => c.ManagementFirmId == context.ManagementFirmId.Value);
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
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userGoal))
        {
            throw new ArgumentException("User goal cannot be empty.", nameof(userGoal));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var permContext = await permissionEvaluator.ResolveContextAsync(userPrincipal, db, ct);
        var targetCommunityId = activeCommunityId
            ?? (permContext.AllowedCommunityIds.Count > 0 ? permContext.AllowedCommunityIds[0] : Guid.Empty);

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
                role = permContext.Role.ToString(),
                allowedCommunities = permContext.AllowedCommunityIds
            }, JsonOpts);

            var accountPermissionJson = JsonSerializer.Serialize(new
            {
                userId = permContext.UserId,
                role = permContext.Role.ToString(),
                isFirmManager = permContext.IsFirmManager,
                grantedPermissions = permContext.GrantedPermissions.ToString(),
                linkedUnitNumber = permContext.LinkedUnitNumber
            }, JsonOpts);

            var toolCatalogJson = "[43-Tool Catalog across Introspection, Firm, Communities, Buildings, Units, Residents, Financials, Maintenance, Governance, Vision, Voice, Security]";
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
                ct);

            totalLatencyMs += turnResult.LatencyMs;
            if (turnResult.UsedLiveApi)
            {
                usedLiveApi = true;
            }

            // Case A: Model emitted Final Answer
            if (turnResult.IsFinalAnswer)
            {
                finalMarkdown = turnResult.FinalResponseMarkdown;
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
                var argsJson = JsonSerializer.Serialize(toolCall.Arguments, JsonOpts);

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
                    LatencyMs: 5));

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
                        LatencyMs: 10);
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
                        ClarificationOptions: options);
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
                        ClarificationOptions: options);
                }

                // Execute Tool
                var toolObs = await toolExecutor.ExecuteToolAsync(toolCall.ToolName, toolCall.Arguments, permContext, targetCommunityId, ct);
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
                    LatencyMs: 25);
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
            UsedLiveApi: usedLiveApi);
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
