using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace CommonGround.Web.Services;

public sealed class WorkspaceAuthenticationStateProvider(WorkspaceUiState state) : AuthenticationStateProvider
{
    private readonly WorkspaceUiState _state = state;

    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var user = _state.CurrentUser;
        if (user is null)
        {
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("auth_provider", user.Provider.ToString()),
        };
        if (!string.IsNullOrWhiteSpace(user.LinkedUnitNumber))
        {
            claims.Add(new Claim("unit_number", user.LinkedUnitNumber));
        }

        var identity = new ClaimsIdentity(claims, "WorkspaceAuth");
        return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
    }

    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
}
