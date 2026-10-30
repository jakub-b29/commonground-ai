using System.Security.Claims;
using CommonGround.Application.Services;
using CommonGround.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace CommonGround.Web.Auth;

public static class AuthEndpoints
{
    private const string GenericFailure = "Sign-in failed. Check the firm group code, email, and password.";

    public static void MapWorkspaceAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", (Delegate)LoginAsync).DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/auth/logout", (Delegate)LogoutAsync).DisableAntiforgery();
        app.MapPost("/auth/register-firm", (Delegate)RegisterFirmAsync).DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/auth/accept-invite", (Delegate)AcceptInviteAsync).DisableAntiforgery().RequireRateLimiting("auth");
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        CommonGroundWorkspaceService workspace,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var firmCode = form["firmCode"].ToString();
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        var returnUrl = form["returnUrl"].ToString();
        try
        {
            var account = await workspace.AuthenticateUserWithFirmCodeAsync(
                firmCode, IdentityProvider.EmailPassword, email, password, ct);
            if (account is null)
                return RedirectWithError("/login", returnUrl, "invalid");
            await SignInAccountAsync(http, workspace, account, ct);
            return Results.Redirect(NormalizeReturnUrl(returnUrl, account.Role));
        }
        catch
        {
            return RedirectWithError("/login", returnUrl, "invalid");
        }
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }

    private static async Task<IResult> RegisterFirmAsync(
        HttpContext http,
        CommonGroundWorkspaceService workspace,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var firmName = form["firmName"].ToString();
        var firmLegalName = form["firmLegalName"].ToString();
        var firmTaxId = form["firmTaxId"].ToString();
        var licenseNumber = form["licenseNumber"].ToString();
        var officeAddress = form["officeAddress"].ToString();
        var modeRaw = form["mode"].ToString();
        var managerName = form["managerName"].ToString();
        var managerEmail = form["managerEmail"].ToString();
        var managerPhone = form["managerPhone"].ToString();
        var password = form["password"].ToString();
        if (string.IsNullOrWhiteSpace(firmName) || string.IsNullOrWhiteSpace(managerName)
            || string.IsNullOrWhiteSpace(managerEmail) || string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return RedirectWithError("/register-firm", string.Empty, "invalid");
        try
        {
            var mode = string.Equals(modeRaw, "UsHoa", StringComparison.OrdinalIgnoreCase)
                ? SystemOperatingMode.UsHoa
                : SystemOperatingMode.HousingAssociation;
            var firm = await workspace.RegisterManagementFirmAsync(
                firmName.Trim(),
                null,
                mode,
                managerName.Trim(),
                managerEmail.Trim(),
                managerPhone.Trim(),
                password,
                IdentityProvider.EmailPassword,
                managingFirmLegalName: firmLegalName.Trim(),
                firmTaxIdOrNip: firmTaxId.Trim(),
                licenseNumber: licenseNumber.Trim(),
                officeAddress: officeAddress.Trim(),
                ct: ct);
            var account = await workspace.AuthenticateUserWithFirmCodeAsync(
                firm.FirmGroupCode, IdentityProvider.EmailPassword, managerEmail.Trim(), password, ct);
            if (account is null)
                return Results.Redirect("/login?registered=1");
            await SignInAccountAsync(http, workspace, account, ct);
            return Results.Redirect("/firm/properties");
        }
        catch
        {
            return RedirectWithError("/register-firm", string.Empty, "invalid");
        }
    }

    private static async Task<IResult> AcceptInviteAsync(
        HttpContext http,
        CommonGroundWorkspaceService workspace,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var token = form["token"].ToString();
        var firmCode = form["firmCode"].ToString();
        var fullName = form["fullName"].ToString();
        var email = form["email"].ToString();
        var phone = form["phone"].ToString();
        var password = form["password"].ToString();
        var unit = form["unit"].ToString();
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(firmCode)
            || string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return RedirectWithError("/accept-invite", string.Empty, "invalid", token, firmCode);
        try
        {
            var account = await workspace.AcceptWorkspaceInvitationAsync(
                token.Trim(), firmCode.Trim(), fullName.Trim(), email.Trim(),
                phone.Trim(), password, IdentityProvider.EmailPassword,
                string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(), ct);
            await SignInAccountAsync(http, workspace, account, ct);
            return Results.Redirect(NormalizeReturnUrl(string.Empty, account.Role));
        }
        catch
        {
            return RedirectWithError("/accept-invite", string.Empty, "invalid", token, firmCode);
        }
    }

    private static async Task SignInAccountAsync(
        HttpContext http,
        CommonGroundWorkspaceService workspace,
        CommonGround.Domain.Entities.WorkspaceUserAccount account,
        CancellationToken ct)
    {
        var allowed = CommonGroundWorkspaceService.ParseAllowedCommunityIds(account);
        var principal = WorkspaceClaims.CreatePrincipal(
            account.Id, account.FullName, account.Email, account.Role,
            account.ManagementFirmId, account.FirmGroupCode, account.CommunityId,
            allowed, account.LinkedUnitNumber);
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
            AllowRefresh = true,
        };
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        _ = workspace;
        _ = ct;
    }

    private static string NormalizeReturnUrl(string returnUrl, WorkspaceSecurityRole role)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal))
            return returnUrl;
        return role switch
        {
            WorkspaceSecurityRole.FirmManager => "/firm/properties",
            WorkspaceSecurityRole.PropertyManager => "/",
            WorkspaceSecurityRole.Resident => "/residents/my-unit",
            WorkspaceSecurityRole.Contractor => "/maintenance/work-orders",
            _ => "/",
        };
    }

    private static IResult RedirectWithError(string page, string returnUrl, string error, string? token = null, string? code = null)
    {
        var query = $"?error={error}";
        if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/'))
            query += $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        if (!string.IsNullOrWhiteSpace(token))
            query += $"&token={Uri.EscapeDataString(token)}";
        if (!string.IsNullOrWhiteSpace(code))
            query += $"&code={Uri.EscapeDataString(code)}";
        return Results.Redirect(page + query);
    }

    public static string AuthFailureMessage => GenericFailure;
}
