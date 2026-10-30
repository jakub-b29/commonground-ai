using System.Security.Claims;
using System.Text.Json;
using CommonGround.Domain.Enums;
using CommonGround.Web.Auth;

namespace CommonGround.UnitTests;

public sealed class SecureRedesignRegressionTests
{
    private static readonly string[] RequiredNavKeys =
    [
        "nav.utilitiesSources", "nav.utilitiesOutagesDetail", "nav.approvals",
        "nav.inspections", "nav.permits", "nav.intake", "nav.workOrders",
        "nav.walkthroughs", "nav.claims", "nav.ledger", "nav.utilitiesDetail",
        "nav.arrears", "nav.simulations", "nav.reserves", "nav.meetings",
        "nav.minutes", "nav.voting", "nav.mediation", "nav.myUnit",
        "nav.messages", "nav.profile", "nav.account", "nav.firmContacts",
        "nav.propertySettings", "nav.signOut", "settings.accountTitle",
        "auth.footerNote", "auth.createFirmLink", "auth.haveInviteLink",
        "auth.registeredNote", "auth.inviteInvalid", "msg.noCommunityAssigned",
    ];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CommonGround.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root with CommonGround.slnx not found.");
    }

    private static Dictionary<string, string> LoadLocale(string lang)
    {
        var path = Path.Combine(FindRepoRoot(), "src", "CommonGround.Web", "wwwroot", "i18n", $"{lang}.json");
        Assert.True(File.Exists(path), $"Missing locale file {lang}.json");
        var json = File.ReadAllText(path);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        Assert.NotNull(parsed);
        return parsed!;
    }

    [Theory]
    [InlineData("en")]
    [InlineData("pl")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    public void Locales_ContainAllSplitRouteKeys(string lang)
    {
        var strings = LoadLocale(lang);
        foreach (var key in RequiredNavKeys)
        {
            Assert.True(strings.TryGetValue(key, out var value), $"Locale {lang} missing key {key}");
            Assert.False(string.IsNullOrWhiteSpace(value), $"Locale {lang} key {key} is empty");
        }
    }

    [Fact]
    public void WorkspaceClaims_FirmManagerPrincipalCarriesFirmAndAllowedCommunities()
    {
        var firmId = Guid.NewGuid();
        var allowed = new List<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        var principal = WorkspaceClaims.CreatePrincipal(
            Guid.NewGuid(), "Firm Manager", "manager@example.com",
            WorkspaceSecurityRole.FirmManager, firmId, "CG-FIRM-2026-AB12",
            allowed[0], allowed, null);

        Assert.True(WorkspaceClaims.TryGetFirmId(principal, out var parsedFirm));
        Assert.Equal(firmId, parsedFirm);
        Assert.Equal(WorkspaceSecurityRole.FirmManager, WorkspaceClaims.GetRole(principal));
        Assert.True(WorkspaceClaims.IsFirmManager(principal));
        Assert.True(WorkspaceClaims.IsManager(principal));
        Assert.Equal(2, WorkspaceClaims.GetAllowedCommunityIds(principal).Count);
    }

    [Fact]
    public void WorkspaceClaims_AnonymousPrincipalHasNoFirmAccess()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False(WorkspaceClaims.TryGetFirmId(anonymous, out _));
        Assert.Empty(WorkspaceClaims.GetAllowedCommunityIds(anonymous));
        Assert.False(WorkspaceClaims.IsManager(anonymous));
        Assert.False(WorkspaceClaims.IsFirmManager(anonymous));
    }

    [Fact]
    public void WorkspaceClaims_UnknownRoleDefaultsToResident()
    {
        var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, "NotARole") }, "WorkspaceCookie");
        var principal = new ClaimsPrincipal(identity);
        Assert.Equal(WorkspaceSecurityRole.Resident, WorkspaceClaims.GetRole(principal));
    }

    [Fact]
    public void WorkspacePages_AllWorkspaceRoutesRequireAuthorization()
    {
        var pagesDir = Path.Combine(FindRepoRoot(), "src", "CommonGround.Web", "Components", "Pages");
        var files = Directory.GetFiles(pagesDir, "*.razor");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            if (text.Contains("@layout Layout.AuthLayout"))
            {
                Assert.Contains("[AllowAnonymous]", text);
            }
            else if (name is "Error.razor" or "NotFound.razor")
            {
                continue;
            }
            else
            {
                Assert.Contains("[Authorize(Policy", text);
            }
        }
    }

    [Fact]
    public void Shell_DoesNotSendWorkspaceCodeWithoutLogin()
    {
        var root = FindRepoRoot();
        var appRazor = File.ReadAllText(Path.Combine(root, "src", "CommonGround.Web", "Components", "App.razor"));
        Assert.DoesNotContain("bootstrap", appRazor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Radzen", appRazor, StringComparison.OrdinalIgnoreCase);

        var routes = File.ReadAllText(Path.Combine(root, "src", "CommonGround.Web", "Components", "Routes.razor"));
        Assert.Contains("AuthorizeRouteView", routes, StringComparison.Ordinal);
        Assert.Contains("RedirectToLogin", routes, StringComparison.Ordinal);

        var shell = File.ReadAllText(Path.Combine(root, "src", "CommonGround.Web", "Components", "Layout", "MainLayout.razor"));
        Assert.Contains("AuthorizeView", shell, StringComparison.Ordinal);
        Assert.Contains("RedirectToLogin", shell, StringComparison.Ordinal);

        var nav = File.ReadAllText(Path.Combine(root, "src", "CommonGround.Web", "Components", "Layout", "NavMenu.razor"));
        Assert.Contains("firm/properties", nav, StringComparison.Ordinal);
        Assert.Contains("settings/account", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCommunitiesAsync", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("GetManagementFirmsAsync", nav, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalsLink_PointsToSplitApprovalsRoute()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "CommonGround.Web", "Components", "Shared", "Overview", "PendingApprovalsAndAuditPanel.razor");
        var text = File.ReadAllText(path);
        Assert.Contains("/residents/approvals", text, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/residents\"", text, StringComparison.Ordinal);
    }
}
