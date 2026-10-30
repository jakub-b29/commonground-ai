using System.Security.Claims;
using CommonGround.Infrastructure;
using CommonGround.Web.Auth;
using CommonGround.Web.Components;
using CommonGround.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

static void LoadDotEnvFile()
{
    try
    {
        var envPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env");
        var normalized = Path.GetFullPath(envPath);
        if (!File.Exists(normalized))
        {
            var fallback = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            normalized = fallback;
        }
        if (!File.Exists(normalized))
        {
            return;
        }
        foreach (var rawLine in File.ReadAllLines(normalized))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"').Trim('\'');
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            Environment.SetEnvironmentVariable(key, value);
        }
    }
    catch
    {
    }
}

LoadDotEnvFile();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddDataProtection();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.Name = "CommonGround.Workspace";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("FirmManagerOnly", p => p.RequireRole("FirmManager"));
    options.AddPolicy("ManagersOnly", p => p.RequireRole("FirmManager", "PropertyManager"));
    options.AddPolicy("WorkspaceMember", p => p.RequireAuthenticatedUser());
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("auth", o =>
    {
        o.PermitLimit = 20;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueLimit = 0;
    });
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<WorkspaceUiState>();
builder.Services.AddScoped<WorkspaceLocalization>();
builder.Services.AddScoped<WorkspaceAccessService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCommonGroundInfrastructure(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});

app.MapStaticAssets();
app.MapWorkspaceAuthEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

static bool TryGetFirmId(ClaimsPrincipal user, out Guid firmId)
{
    firmId = Guid.Empty;
    var raw = user.FindFirst(WorkspaceClaimTypes.FirmId)?.Value;
    return Guid.TryParse(raw, out firmId) && firmId != Guid.Empty;
}

static bool CanAccessCommunity(ClaimsPrincipal user, CommonGround.Domain.Entities.Community community)
{
    if (!TryGetFirmId(user, out var firmId)) return false;
    if (community.ManagementFirmId != firmId) return false;
    if (user.IsInRole("FirmManager")) return true;
    var allowedRaw = user.FindFirst(WorkspaceClaimTypes.AllowedCommunities)?.Value;
    if (!string.IsNullOrWhiteSpace(allowedRaw))
    {
        var allowed = allowedRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (allowed.Any(s => string.Equals(s, community.Id.ToString(), StringComparison.OrdinalIgnoreCase)))
            return true;
    }
    var primaryRaw = user.FindFirst(WorkspaceClaimTypes.CommunityId)?.Value;
    return Guid.TryParse(primaryRaw, out var primary) && primary == community.Id;
}

app.MapGet("/api/export/meeting-pack/{communityId:guid}", async (Guid communityId, HttpContext http, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!http.User.IsInRole("FirmManager") && !http.User.IsInRole("PropertyManager")) return Results.Forbid();
    var snapshot = await workspace.GetSnapshotAsync(communityId);
    if (snapshot is null) return Results.NotFound();
    if (!CanAccessCommunity(http.User, snapshot.Community)) return Results.Forbid();
    var pdf = exporter.GenerateAnnualMeetingPackPdf(snapshot.Community);
    return Results.File(pdf, "application/pdf", "Annual-Meeting-Pack.pdf");
}).RequireAuthorization("ManagersOnly");

app.MapGet("/api/export/minutes-pdf/{communityId:guid}", async (Guid communityId, HttpContext http, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!http.User.IsInRole("FirmManager") && !http.User.IsInRole("PropertyManager")) return Results.Forbid();
    var snapshot = await workspace.GetSnapshotAsync(communityId);
    if (snapshot is null) return Results.NotFound();
    if (!CanAccessCommunity(http.User, snapshot.Community)) return Results.Forbid();
    var meeting = snapshot.Community.Meetings.OrderByDescending(m => m.ScheduledAt).FirstOrDefault() ?? new CommonGround.Domain.Entities.MeetingRecord { CommunityId = communityId, Title = "Annual meeting" };
    var pdf = exporter.GenerateMeetingMinutesPdf(snapshot.Community, meeting, snapshot.Community.Resolutions);
    return Results.File(pdf, "application/pdf", "Formal-Meeting-Minutes.pdf");
}).RequireAuthorization("ManagersOnly");

app.MapGet("/api/export/minutes-docx/{communityId:guid}", async (Guid communityId, HttpContext http, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!http.User.IsInRole("FirmManager") && !http.User.IsInRole("PropertyManager")) return Results.Forbid();
    var snapshot = await workspace.GetSnapshotAsync(communityId);
    if (snapshot is null) return Results.NotFound();
    if (!CanAccessCommunity(http.User, snapshot.Community)) return Results.Forbid();
    var meeting = snapshot.Community.Meetings.OrderByDescending(m => m.ScheduledAt).FirstOrDefault() ?? new CommonGround.Domain.Entities.MeetingRecord { CommunityId = communityId, Title = "Annual meeting" };
    var docx = exporter.GenerateMeetingMinutesDocx(snapshot.Community, meeting, snapshot.Community.Resolutions);
    return Results.File(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Formal-Meeting-Minutes.docx");
}).RequireAuthorization("ManagersOnly");

app.MapGet("/api/export/claim-dossier/{workOrderId:guid}", async (Guid workOrderId, HttpContext http, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!http.User.IsInRole("FirmManager") && !http.User.IsInRole("PropertyManager")) return Results.Forbid();
    if (!TryGetFirmId(http.User, out var firmId)) return Results.Forbid();
    var communities = await workspace.GetAllowedFirmCommunitiesAsync(firmId, WorkspaceClaims.GetAllowedCommunityIds(http.User));
    if (http.User.IsInRole("FirmManager"))
        communities = await workspace.GetFirmCommunitiesAsync(firmId);
    foreach (var c in communities)
    {
        var snapshot = await workspace.GetSnapshotAsync(c.Id);
        var workOrder = snapshot?.Community.WorkOrders.FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is not null && snapshot is not null)
        {
            if (!CanAccessCommunity(http.User, snapshot.Community)) return Results.Forbid();
            var dossier = await workspace.GenerateInsuranceClaimDossierAsync(c.Id, workOrderId);
            var pdf = exporter.GenerateInsuranceClaimDossierPdf(snapshot.Community, workOrder, dossier);
            return Results.File(pdf, "application/pdf", "Insurance-Claim-Dossier.pdf");
        }
    }
    return Results.NotFound();
}).RequireAuthorization("ManagersOnly");

app.MapGet("/api/export/legal-package/{unitId:guid}", async (Guid unitId, HttpContext http, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    if (http.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!http.User.IsInRole("FirmManager") && !http.User.IsInRole("PropertyManager")) return Results.Forbid();
    if (!TryGetFirmId(http.User, out var firmId)) return Results.Forbid();
    var communities = await workspace.GetAllowedFirmCommunitiesAsync(firmId, WorkspaceClaims.GetAllowedCommunityIds(http.User));
    if (http.User.IsInRole("FirmManager"))
        communities = await workspace.GetFirmCommunitiesAsync(firmId);
    foreach (var c in communities)
    {
        var snapshot = await workspace.GetSnapshotAsync(c.Id);
        var unit = snapshot?.Community.Units.FirstOrDefault(u => u.Id == unitId);
        if (unit is not null && snapshot is not null)
        {
            if (!CanAccessCommunity(http.User, snapshot.Community)) return Results.Forbid();
            var pdf = exporter.GenerateLegalOrEstoppelPackagePdf(snapshot.Community, unit);
            return Results.File(pdf, "application/pdf", "Legal-Enforcement-Or-Estoppel.pdf");
        }
    }
    return Results.NotFound();
}).RequireAuthorization("ManagersOnly");

await app.Services.EnsureCommonGroundDatabaseCreatedAsync();
app.Run();
