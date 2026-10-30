using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Web.Services;

public sealed class WorkspaceUiState
{
    public Guid? SelectedCommunityId { get; set; }
    public string CurrentTerminology { get; set; } = "Housing Association Mode";
    public WorkspaceUserAccount? CurrentUser { get; set; }
    public WorkspaceSecurityRole ActiveSecurityRole { get; set; } = WorkspaceSecurityRole.PropertyManagerAdmin;
    public HashSet<Guid> RevealedAccessCodeIds { get; set; } = [];
    public bool IsPortfolioQueryOpen { get; set; }
    public string StatusBannerMessage { get; set; } = string.Empty;

    public event Action? OnChange;

    public void NotifyChanged() => OnChange?.Invoke();

    public void SelectCommunity(Guid communityId, string terminology)
    {
        SelectedCommunityId = communityId;
        CurrentTerminology = terminology;
        StatusBannerMessage = $"Active community switched. Terminology: {terminology}.";
        NotifyChanged();
    }

    public void SetUser(WorkspaceUserAccount? user)
    {
        CurrentUser = user;
        if (user is not null)
        {
            ActiveSecurityRole = user.Role;
        }
        NotifyChanged();
    }

    public void SetRole(WorkspaceSecurityRole role)
    {
        ActiveSecurityRole = role;
        NotifyChanged();
    }

    public void TogglePortfolioQuery()
    {
        IsPortfolioQueryOpen = !IsPortfolioQueryOpen;
        NotifyChanged();
    }

    public void Announce(string message)
    {
        StatusBannerMessage = message;
        NotifyChanged();
    }
}
