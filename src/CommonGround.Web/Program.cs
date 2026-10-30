using Radzen;
using System.Security.Claims;
using CommonGround.Infrastructure;
using CommonGround.Web.Components;
using CommonGround.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => o.LoginPath = "/login");
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<WorkspaceUiState>();
builder.Services.AddScoped<WorkspaceAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<WorkspaceAuthenticationStateProvider>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddCommonGroundInfrastructure(builder.Configuration);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/api/export/meeting-pack/{communityId:guid}", async (Guid communityId, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    var snapshot = await workspace.GetSnapshotAsync(communityId);
    if (snapshot is null) return Results.NotFound();
    var pdf = exporter.GenerateAnnualMeetingPackPdf(snapshot.Community);
    return Results.File(pdf, "application/pdf", "Annual-Meeting-Pack.pdf");
});

app.MapGet("/api/export/minutes-pdf/{communityId:guid}", async (Guid communityId, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    var snapshot = await workspace.GetSnapshotAsync(communityId);
    if (snapshot is null) return Results.NotFound();
    var meeting = snapshot.Community.Meetings.OrderByDescending(m => m.ScheduledAt).FirstOrDefault() ?? new CommonGround.Domain.Entities.MeetingRecord { CommunityId = communityId, Title = "Annual meeting" };
    var pdf = exporter.GenerateMeetingMinutesPdf(snapshot.Community, meeting, snapshot.Community.Resolutions);
    return Results.File(pdf, "application/pdf", "Formal-Meeting-Minutes.pdf");
});

app.MapGet("/api/export/minutes-docx/{communityId:guid}", async (Guid communityId, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    var snapshot = await workspace.GetSnapshotAsync(communityId);
    if (snapshot is null) return Results.NotFound();
    var meeting = snapshot.Community.Meetings.OrderByDescending(m => m.ScheduledAt).FirstOrDefault() ?? new CommonGround.Domain.Entities.MeetingRecord { CommunityId = communityId, Title = "Annual meeting" };
    var docx = exporter.GenerateMeetingMinutesDocx(snapshot.Community, meeting, snapshot.Community.Resolutions);
    return Results.File(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "Formal-Meeting-Minutes.docx");
});

app.MapGet("/api/export/claim-dossier/{workOrderId:guid}", async (Guid workOrderId, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    var communities = await workspace.GetCommunitiesAsync();
    foreach (var c in communities)
    {
        var snapshot = await workspace.GetSnapshotAsync(c.Id);
        var workOrder = snapshot?.Community.WorkOrders.FirstOrDefault(w => w.Id == workOrderId);
        if (workOrder is not null && snapshot is not null)
        {
            var dossier = await workspace.GenerateInsuranceClaimDossierAsync(c.Id, workOrderId);
            var pdf = exporter.GenerateInsuranceClaimDossierPdf(snapshot.Community, workOrder, dossier);
            return Results.File(pdf, "application/pdf", "Insurance-Claim-Dossier.pdf");
        }
    }
    return Results.NotFound();
});

app.MapGet("/api/export/legal-package/{unitId:guid}", async (Guid unitId, CommonGround.Application.Services.CommonGroundWorkspaceService workspace, CommonGround.Application.Interfaces.IDocumentExporter exporter) =>
{
    var communities = await workspace.GetCommunitiesAsync();
    foreach (var c in communities)
    {
        var snapshot = await workspace.GetSnapshotAsync(c.Id);
        var unit = snapshot?.Community.Units.FirstOrDefault(u => u.Id == unitId);
        if (unit is not null && snapshot is not null)
        {
            var pdf = exporter.GenerateLegalOrEstoppelPackagePdf(snapshot.Community, unit);
            return Results.File(pdf, "application/pdf", "Legal-Enforcement-Or-Estoppel.pdf");
        }
    }
    return Results.NotFound();
});

await app.Services.EnsureCommonGroundSeededAsync();
app.Run();
