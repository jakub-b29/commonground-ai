using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Web.Services;

public sealed class WorkspaceUiState
{
    public Guid? SelectedCommunityId { get; set; }
    public Guid? SelectedFirmId { get; set; }
    public string ActiveFirmGroupCode { get; set; } = string.Empty;
    public string CurrentTerminology { get; set; } = "Housing Association Mode";
    public WorkspaceUserAccount? CurrentUser { get; set; }
    public WorkspaceSecurityRole ActiveSecurityRole { get; set; } = WorkspaceSecurityRole.PropertyManager;
    public HashSet<Guid> RevealedAccessCodeIds { get; set; } = [];
    public bool IsPortfolioQueryOpen { get; set; }
    public bool IsEmergencyDrawerOpen { get; set; }
    public string StatusBannerMessage { get; set; } = string.Empty;
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
        WorkspaceUserAccount? account)
    {
        ActiveSecurityRole = role;
        if (firmId.HasValue) SelectedFirmId = firmId.Value;
        if (!string.IsNullOrWhiteSpace(firmCode)) ActiveFirmGroupCode = firmCode.Trim().ToUpperInvariant();
        if (account is not null) CurrentUser = account;
        if (SelectedCommunityId is null && primaryCommunityId.HasValue && primaryCommunityId.Value != Guid.Empty)
            SelectedCommunityId = primaryCommunityId.Value;
    }

    public void TogglePortfolioQuery()
    {
        IsPortfolioQueryOpen = !IsPortfolioQueryOpen;
        NotifyChanged();
    }

    public void ToggleEmergencyDrawer()
    {
        IsEmergencyDrawerOpen = !IsEmergencyDrawerOpen;
        NotifyChanged();
    }

    public void Announce(string message)
    {
        StatusBannerMessage = message;
        NotifyChanged();
    }
}
