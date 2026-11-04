using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Web.Services;

public sealed class WorkspaceUiState
{
    public Guid? SelectedCommunityId { get; set; }
    public Guid? SelectedPropertyId
    {
        get => SelectedCommunityId;
        set => SelectedCommunityId = value;
    }
    public Guid? SelectedFirmId { get; set; }
    public string ActiveFirmGroupCode { get; set; } = string.Empty;
    public string CurrentTerminology { get; set; } = "Housing Association Mode";
    public WorkspaceUserAccount? CurrentUser { get; set; }
    public WorkspaceSecurityRole ActiveSecurityRole { get; set; } = WorkspaceSecurityRole.PropertyManager;
    public HashSet<Guid> RevealedAccessCodeIds { get; set; } = [];
    public bool IsPortfolioQueryOpen { get; set; }
    public bool IsSparkAgentChatOpen { get; set; }
    public bool IsSparkAgentExpanded { get; set; }
    public bool IsEmergencyDrawerOpen { get; set; }
    public bool IsSidebarCollapsed { get; set; }
    public string ActiveTheme { get; private set; } = "dark";
    public bool IsDarkTheme => !string.Equals(ActiveTheme, "light", StringComparison.OrdinalIgnoreCase);
    public string StatusBannerMessage { get; set; } = string.Empty;
    public IReadOnlyList<WorkspaceAccountMembership> Memberships { get; private set; } = Array.Empty<WorkspaceAccountMembership>();
    public Guid? ActiveMembershipId { get; private set; }
    public WorkspaceAccountKind ActiveAccountKind { get; private set; } = WorkspaceAccountKind.RegularAccount;
    public WorkspaceAccountMembership? ActiveMembership => Memberships.FirstOrDefault(m => m.Id == ActiveMembershipId) ?? Memberships.FirstOrDefault(m => m.ApprovalStatus == WorkspaceMembershipStatus.Approved);
    public bool IsPendingApproval => ActiveMembership?.ApprovalStatus == WorkspaceMembershipStatus.PendingApproval;
    public bool IsPendingEmailConfirmation => ActiveMembership?.ApprovalStatus == WorkspaceMembershipStatus.PendingEmailConfirmation;
    public bool IsFirmManager => ActiveSecurityRole == WorkspaceSecurityRole.FirmManager;
    public bool IsPropertyManager => ActiveSecurityRole == WorkspaceSecurityRole.PropertyManager;
    public bool IsResident => ActiveSecurityRole == WorkspaceSecurityRole.Resident;
    public bool IsContractor => ActiveSecurityRole == WorkspaceSecurityRole.Contractor;
    public bool IsManager => IsFirmManager || IsPropertyManager;

    public event Action? OnChange;

    public void NotifyChanged() => OnChange?.Invoke();

    public void SelectFirm(Guid firmId, string firmGroupCode)
    {
        SelectedFirmId = firmId;
        ActiveFirmGroupCode = (firmGroupCode ?? string.Empty).Trim().ToUpperInvariant();
        NotifyChanged();
    }

    public void SelectCommunity(Guid communityId, string terminology)
    {
        if (SelectedCommunityId == communityId && CurrentTerminology == terminology)
        {
            return;
        }
        var hadPrevious = SelectedCommunityId.HasValue;
        SelectedCommunityId = communityId;
        CurrentTerminology = terminology;
        if (hadPrevious)
        {
            StatusBannerMessage = $"Active community switched. Terminology: {terminology}.";
        }
        NotifyChanged();
    }

    public void ClearCommunitySelection()
    {
        SelectedCommunityId = null;
        NotifyChanged();
    }

    public void SetUser(WorkspaceUserAccount? user)
    {
        CurrentUser = user;
        if (user is not null)
        {
            ActiveSecurityRole = user.Role;
            if (user.ManagementFirmId.HasValue)
            {
                SelectedFirmId = user.ManagementFirmId.Value;
            }
            if (!string.IsNullOrWhiteSpace(user.FirmGroupCode))
            {
                ActiveFirmGroupCode = user.FirmGroupCode.Trim().ToUpperInvariant();
            }
        }
        NotifyChanged();
    }

    public void SetRole(WorkspaceSecurityRole role)
    {
        if (ActiveSecurityRole == role)
        {
            return;
        }
        ActiveSecurityRole = role;
        NotifyChanged();
    }

    public void SyncFromClaims(
        WorkspaceSecurityRole role,
        Guid? firmId,
        string firmCode,
        Guid? primaryCommunityId,
        WorkspaceUserAccount? account,
        WorkspaceAccountKind accountKind = WorkspaceAccountKind.RegularAccount,
        Guid? membershipId = null)
    {
        ActiveSecurityRole = role;
        ActiveAccountKind = accountKind;
        if (membershipId.HasValue)
        {
            ActiveMembershipId = membershipId.Value;
        }
        if (firmId.HasValue) SelectedFirmId = firmId.Value;
        if (!string.IsNullOrWhiteSpace(firmCode)) ActiveFirmGroupCode = firmCode.Trim().ToUpperInvariant();
        if (account is not null) CurrentUser = account;
        if (SelectedCommunityId is null && primaryCommunityId.HasValue && primaryCommunityId.Value != Guid.Empty)
            SelectedCommunityId = primaryCommunityId.Value;
    }

    public void SetMemberships(IReadOnlyList<WorkspaceAccountMembership> memberships, Guid? activeId = null)
    {
        Memberships = memberships ?? Array.Empty<WorkspaceAccountMembership>();
        if (activeId.HasValue)
        {
            ActiveMembershipId = activeId.Value;
        }
        else if (ActiveMembershipId.HasValue && Memberships.All(m => m.Id != ActiveMembershipId.Value))
        {
            ActiveMembershipId = null;
        }
        if (ActiveMembership is not null)
        {
            ActiveSecurityRole = ActiveMembership.Role;
        }
        NotifyChanged();
    }

    public void SyncMembershipState(IReadOnlyList<WorkspaceAccountMembership> memberships, Guid? activeMembershipId = null, WorkspaceAccountKind accountKind = WorkspaceAccountKind.RegularAccount)
    {
        Memberships = memberships ?? Array.Empty<WorkspaceAccountMembership>();
        ActiveAccountKind = accountKind;
        if (activeMembershipId.HasValue)
        {
            ActiveMembershipId = activeMembershipId.Value;
        }
        if (ActiveMembership is not null)
        {
            ActiveSecurityRole = ActiveMembership.Role;
        }
        NotifyChanged();
    }

    public void SwitchActiveMembership(Guid membershipId, Guid? communityId = null)
    {
        ActiveMembershipId = membershipId;
        var membership = Memberships.FirstOrDefault(m => m.Id == membershipId);
        if (membership is not null)
        {
            ActiveSecurityRole = membership.Role;
        }
        if (communityId.HasValue)
        {
            SelectedCommunityId = communityId;
        }
        NotifyChanged();
    }

    public bool HasPermission(WorkspacePermissionFlags flag) => ActiveMembership?.GrantedPermissions.HasFlag(flag) == true || ActiveSecurityRole == WorkspaceSecurityRole.FirmManager;

    public void TogglePortfolioQuery()
    {
        IsPortfolioQueryOpen = !IsPortfolioQueryOpen;
        NotifyChanged();
    }

    public void ToggleSparkAgentChat()
    {
        IsSparkAgentChatOpen = !IsSparkAgentChatOpen;
        NotifyChanged();
    }

    public void ToggleSparkAgentExpanded()
    {
        IsSparkAgentExpanded = !IsSparkAgentExpanded;
        NotifyChanged();
    }

    public void SetSparkAgentChatOpen(bool isOpen)
    {
        if (IsSparkAgentChatOpen == isOpen)
        {
            return;
        }
        IsSparkAgentChatOpen = isOpen;
        NotifyChanged();
    }

    public void ToggleEmergencyDrawer()
    {
        IsEmergencyDrawerOpen = !IsEmergencyDrawerOpen;
        NotifyChanged();
    }

    public void ToggleSidebarCollapsed()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
        NotifyChanged();
    }

    public void SetTheme(string theme)
    {
        var normalized = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        if (string.Equals(ActiveTheme, normalized, StringComparison.Ordinal))
        {
            return;
        }
        ActiveTheme = normalized;
        NotifyChanged();
    }

    public string ToggleTheme()
    {
        var next = IsDarkTheme ? "light" : "dark";
        SetTheme(next);
        return next;
    }

    public void Announce(string message)
    {
        StatusBannerMessage = message;
        NotifyChanged();
    }
}
