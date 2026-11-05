using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommonGround.Application.Services;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Web.Auth;

public static class AuthEndpoints
{
    private const string GenericFailure = "Sign-in failed. Check the email and password.";

    public static void MapWorkspaceAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/auth/login", (Delegate)LoginAsync).DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/auth/logout", (Delegate)LogoutAsync).DisableAntiforgery();
        app.MapPost("/auth/register-firm", (Delegate)RegisterFirmAsync).DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/auth/verify-firm-email", (Delegate)VerifyFirmEmailAsync).DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/auth/verify-email", (Delegate)VerifyEmailAsync).DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/auth/accept-invite", (Delegate)AcceptInviteAsync).DisableAntiforgery().RequireRateLimiting("auth");
        app.MapPost("/auth/join-firm", (Delegate)JoinFirmAsync).DisableAntiforgery().RequireRateLimiting("auth");
    }

    private static string HashToken(string token)
    {
        var bytes = Encoding.UTF8.GetBytes((token ?? string.Empty).Trim());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string NewRawToken(string prefix)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return prefix + Convert.ToHexString(bytes);
    }

    private static string NormalizeFirmCode(string code)
        => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static async Task SignInMinimalAsync(HttpContext http, Guid userId, Guid? activeMembershipId, WorkspaceSecurityRole? role = null)
    {
        var principal = WorkspaceClaims.CreatePrincipal(userId, activeMembershipId, role);
        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
            AllowRefresh = true,
        };
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
    }

    private static string SafeReturnUrl(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal))
            return returnUrl;
        return string.Empty;
    }

    private static string LandingForRole(WorkspaceSecurityRole role, Guid? firstPropertyId)
    {
        if (role == WorkspaceSecurityRole.FirmManager)
            return "/admin";
        if (firstPropertyId.HasValue && firstPropertyId.Value != Guid.Empty)
            return $"/properties/{firstPropertyId.Value}/overview";
        return role switch
        {
            WorkspaceSecurityRole.Resident => "/my-unit",
            WorkspaceSecurityRole.PropertyManager => "/admin",
            WorkspaceSecurityRole.Contractor => "/my-unit",
            _ => "/my-unit",
        };
    }

    private static async Task<Guid?> FirstAccessiblePropertyAsync(AppDbContext db, Guid membershipId, Guid firmId, WorkspaceSecurityRole role, CancellationToken ct)
    {
        if (role == WorkspaceSecurityRole.FirmManager)
        {
            var firstFirmProperty = await db.Communities.AsNoTracking()
                .Where(c => c.ManagementFirmId == firmId)
                .OrderBy(c => c.Name)
                .Select(c => c.Id)
                .FirstOrDefaultAsync(ct);
            return firstFirmProperty == Guid.Empty ? null : firstFirmProperty;
        }

        var firstGranted = await db.MembershipPropertyAccesses.AsNoTracking()
            .Where(p => p.FirmMembershipId == membershipId)
            .OrderBy(p => p.CommunityId)
            .Select(p => p.CommunityId)
            .FirstOrDefaultAsync(ct);
        return firstGranted == Guid.Empty ? null : firstGranted;
    }

    private static async Task AppendAuditAsync(AppDbContext db, Guid communityId, WorkspaceSecurityRole actorRole, Guid? actorMembershipId, string category, string resource, string summary, CancellationToken ct)
    {
        if (communityId == Guid.Empty)
            return;
        var exists = await db.Communities.AsNoTracking().AnyAsync(c => c.Id == communityId, ct);
        if (!exists)
            return;
        var previous = await db.SecurityAuditEvents.AsNoTracking()
            .Where(e => e.CommunityId == communityId)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => e.EntrySha256Hash)
            .LastOrDefaultAsync(ct) ?? "GENESIS-0000000000000000";
        var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(communityId, actorRole, category, "Info", resource, summary, previous);
        ev.ActorMembershipId = actorMembershipId;
        db.SecurityAuditEvents.Add(ev);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext http,
        IDbContextFactory<AppDbContext> dbFactory,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var email = form["email"].ToString();
        var password = form["password"].ToString();
        var returnUrl = form["returnUrl"].ToString();
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return RedirectWithError("/login", returnUrl, "invalid");
        try
        {
            var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(email);
            if (scan.IsInjectionDetected)
                return RedirectWithError("/login", returnUrl, "invalid");

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var normalized = email.Trim().ToLowerInvariant();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
            if (user is not null)
            {
                var ok = DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(password, user.PasswordSaltAndHash);
                if (!ok)
                    return RedirectWithError("/login", returnUrl, "invalid");
                if (!user.EmailConfirmed)
                {
                    await SignInMinimalAsync(http, user.Id, null);
                    return Results.Redirect("/verify-email?email=" + Uri.EscapeDataString(user.Email));
                }

                var memberships = await db.FirmMemberships.AsNoTracking()
                    .Where(m => m.UserId == user.Id)
                    .OrderBy(m => m.ManagementFirmId)
                    .ToListAsync(ct);
                var approved = memberships.Where(m => m.Status == WorkspaceMembershipStatus.Approved).ToList();
                var pending = memberships.Where(m => m.Status == WorkspaceMembershipStatus.PendingApproval || m.Status == WorkspaceMembershipStatus.PendingEmailConfirmation).ToList();
                if (approved.Count == 1)
                {
                    var active = approved[0];
                    await SignInMinimalAsync(http, user.Id, active.Id, active.Role);
                    var safe = SafeReturnUrl(returnUrl);
                    if (!string.IsNullOrWhiteSpace(safe))
                        return Results.Redirect(safe);
                    var firstProperty = await FirstAccessiblePropertyAsync(db, active.Id, active.ManagementFirmId, active.Role, ct);
                    return Results.Redirect(LandingForRole(active.Role, firstProperty));
                }

                if (approved.Count > 1)
                {
                    var best = approved.OrderByDescending(m => m.Role == WorkspaceSecurityRole.FirmManager ? 1 : 0).First();
                    await SignInMinimalAsync(http, user.Id, best.Id, best.Role);
                    return Results.Redirect("/choose-membership");
                }

                if (pending.Count > 0)
                {
                    await SignInMinimalAsync(http, user.Id, pending[0].Id, pending[0].Role);
                    return Results.Redirect(pending[0].Status == WorkspaceMembershipStatus.PendingEmailConfirmation ? "/verify-email" : "/pending");
                }

                await SignInMinimalAsync(http, user.Id, null);
                return Results.Redirect("/pending");
            }

            var legacy = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
            if (legacy is null)
                return RedirectWithError("/login", returnUrl, "invalid");
            var legacyOk = DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(password, legacy.PasswordSaltAndHash);
            if (!legacyOk)
                return RedirectWithError("/login", returnUrl, "invalid");

            var legacyMemberships = await db.AccountMemberships.AsNoTracking()
                .Where(m => m.UserAccountId == legacy.Id)
                .OrderBy(m => m.CreatedAtUtc)
                .ToListAsync(ct);
            var legacyApproved = legacyMemberships.FirstOrDefault(m => m.ApprovalStatus == WorkspaceMembershipStatus.Approved);
            var legacyActiveId = legacyApproved?.Id ?? legacyMemberships.FirstOrDefault()?.Id;
            await SignInMinimalAsync(http, legacy.Id, legacyActiveId, legacy.Role);
            var legacySafe = SafeReturnUrl(returnUrl);
            if (!string.IsNullOrWhiteSpace(legacySafe))
                return Results.Redirect(legacySafe);
            if (legacyApproved is null)
                return Results.Redirect("/pending");
            return Results.Redirect(legacy.Role switch
            {
                WorkspaceSecurityRole.FirmManager => "/admin",
                WorkspaceSecurityRole.Resident => "/my-unit",
                _ => "/my-unit",
            });
        }
        catch
        {
            return RedirectWithError("/login", returnUrl, "invalid");
        }
    }

    private static Task<IResult> LogoutAsync(HttpContext http)
    {
        return Task.FromResult<IResult>(Results.Redirect("/login"));
    }

    private static async Task<IResult> ActualLogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login");
    }

    private static async Task<IResult> RegisterFirmAsync(
        HttpContext http,
        CommonGroundWorkspaceService workspace,
        IDbContextFactory<AppDbContext> dbFactory,
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
        var requireEmailConfirmationRaw = form["requireEmailConfirmation"].ToString();
        var requireEmailConfirmation = string.Equals(requireEmailConfirmationRaw, "on", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requireEmailConfirmationRaw, "true", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(firmName) || string.IsNullOrWhiteSpace(managerName)
            || string.IsNullOrWhiteSpace(managerEmail) || string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return RedirectWithError("/register-firm", string.Empty, "invalid");
        try
        {
            var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{firmName} {managerName} {managerEmail} {officeAddress}");
            if (scan.IsInjectionDetected)
                return RedirectWithError("/register-firm", string.Empty, "invalid");

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
                requireEmailConfirmation: requireEmailConfirmation,
                ct: ct);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var normalized = managerEmail.Trim().ToLowerInvariant();
            var existingUser = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
            User user;
            if (existingUser is null)
            {
                user = new User
                {
                    FullName = managerName.Trim(),
                    Email = managerEmail.Trim(),
                    Phone = managerPhone.Trim(),
                    PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(password),
                    EmailConfirmed = !requireEmailConfirmation,
                    EmailConfirmationTokenHash = firm.EmailConfirmationTokenHash,
                    Provider = IdentityProvider.EmailPassword,
                };
                db.Users.Add(user);
            }
            else
            {
                user = existingUser;
                if (requireEmailConfirmation && user.EmailConfirmed)
                {
                    user.EmailConfirmed = false;
                    user.EmailConfirmationTokenHash = firm.EmailConfirmationTokenHash;
                }
            }

            await db.SaveChangesAsync(ct);
            var membership = await db.FirmMemberships.FirstOrDefaultAsync(m => m.UserId == user.Id && m.ManagementFirmId == firm.Id, ct);
            if (membership is null)
            {
                membership = new FirmMembership
                {
                    UserId = user.Id,
                    ManagementFirmId = firm.Id,
                    Role = WorkspaceSecurityRole.FirmManager,
                    Status = requireEmailConfirmation ? WorkspaceMembershipStatus.PendingEmailConfirmation : WorkspaceMembershipStatus.Approved,
                    GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.FirmManager),
                    DecidedAtUtc = requireEmailConfirmation ? null : DateTime.UtcNow,
                };
                db.FirmMemberships.Add(membership);
                await db.SaveChangesAsync(ct);
            }

            if (firm.RequireEmailConfirmation && !firm.IsEmailConfirmed)
                return Results.Redirect($"/verify-firm-email?firmId={firm.Id}");

            await SignInMinimalAsync(http, user.Id, membership.Id, membership.Role);
            return Results.Redirect("/admin");
        }
        catch
        {
            return RedirectWithError("/register-firm", string.Empty, "invalid");
        }
    }

    private static async Task<IResult> VerifyFirmEmailAsync(
        HttpContext http,
        CommonGroundWorkspaceService workspace,
        IDbContextFactory<AppDbContext> dbFactory,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var token = form["token"].ToString();
        if (string.IsNullOrWhiteSpace(token))
            return Results.Redirect("/verify-firm-email?error=invalid");
        try
        {
            var firm = await workspace.ConfirmFirmEmailAsync(token.Trim(), ct);
            var hash = HashToken(token.Trim());
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var users = await db.Users.Where(u => u.EmailConfirmationTokenHash == hash).ToListAsync(ct);
            Guid? activeMembershipId = null;
            foreach (var user in users)
            {
                user.EmailConfirmed = true;
                user.EmailConfirmationTokenHash = null;
                var memberships = await db.FirmMemberships
                    .Where(m => m.UserId == user.Id && m.ManagementFirmId == firm.Id && m.Status == WorkspaceMembershipStatus.PendingEmailConfirmation)
                    .ToListAsync(ct);
                foreach (var membership in memberships)
                {
                    membership.Status = WorkspaceMembershipStatus.Approved;
                    membership.DecidedAtUtc ??= DateTime.UtcNow;
                    activeMembershipId ??= membership.Id;
                }
            }

            await db.SaveChangesAsync(ct);
            if (users.Count == 0)
            {
                var account = await workspace.GetFirmDefaultManagerAsync(firm.Id, ct);
                if (account is null)
                    return Results.Redirect("/verify-firm-email?error=invalid");
                var legacyMemberships = await db.AccountMemberships.AsNoTracking()
                    .Where(m => m.UserAccountId == account.Id && m.ManagementFirmId == firm.Id)
                    .ToListAsync(ct);
                var legacyActive = legacyMemberships.FirstOrDefault(m => m.ApprovalStatus == WorkspaceMembershipStatus.Approved)?.Id
                    ?? legacyMemberships.FirstOrDefault()?.Id;
                await SignInMinimalAsync(http, account.Id, legacyActive);
                return Results.Redirect("/admin");
            }

            var firstUser = users[0];
            if (activeMembershipId is null)
            {
                var approved = await db.FirmMemberships.AsNoTracking()
                    .Where(m => m.UserId == firstUser.Id && m.ManagementFirmId == firm.Id && m.Status == WorkspaceMembershipStatus.Approved)
                    .OrderBy(m => m.Role)
                    .FirstOrDefaultAsync(ct);
                activeMembershipId = approved?.Id;
            }

            await SignInMinimalAsync(http, firstUser.Id, activeMembershipId);
            return Results.Redirect("/admin");
        }
        catch
        {
            return Results.Redirect("/verify-firm-email?error=invalid");
        }
    }

    private static async Task<IResult> VerifyEmailAsync(
        HttpContext http,
        IDbContextFactory<AppDbContext> dbFactory,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var token = form["token"].ToString();
        var email = form["email"].ToString();
        if (string.IsNullOrWhiteSpace(token))
            return Results.Redirect("/verify-email?error=invalid");
        try
        {
            var hash = HashToken(token.Trim());
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            User? user = null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                var normalized = email.Trim().ToLowerInvariant();
                user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized && u.EmailConfirmationTokenHash == hash, ct);
            }
            else
            {
                user = await db.Users.FirstOrDefaultAsync(u => u.EmailConfirmationTokenHash == hash, ct);
            }

            if (user is null)
                return Results.Redirect("/verify-email?error=invalid");

            var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{user.FullName} {user.Email}");
            if (scan.IsInjectionDetected)
                return Results.Redirect("/verify-email?error=invalid");

            user.EmailConfirmed = true;
            user.EmailConfirmationTokenHash = null;
            var memberships = await db.FirmMemberships
                .Where(m => m.UserId == user.Id && m.Status == WorkspaceMembershipStatus.PendingEmailConfirmation)
                .ToListAsync(ct);
            foreach (var membership in memberships)
            {
                membership.Status = WorkspaceMembershipStatus.PendingApproval;
                var firm = await db.ManagementFirms.AsNoTracking().FirstOrDefaultAsync(f => f.Id == membership.ManagementFirmId, ct);
                if (firm is not null && !firm.RequireEmailConfirmation && membership.Role == WorkspaceSecurityRole.FirmManager)
                {
                    membership.Status = WorkspaceMembershipStatus.Approved;
                    membership.DecidedAtUtc ??= DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            var approved = await db.FirmMemberships.AsNoTracking()
                .Where(m => m.UserId == user.Id && m.Status == WorkspaceMembershipStatus.Approved)
                .OrderBy(m => m.Role)
                .FirstOrDefaultAsync(ct);
            if (approved is not null)
            {
                await SignInMinimalAsync(http, user.Id, approved.Id, approved.Role);
                var firstProperty = await FirstAccessiblePropertyAsync(db, approved.Id, approved.ManagementFirmId, approved.Role, ct);
                return Results.Redirect(LandingForRole(approved.Role, firstProperty));
            }

            var pending = await db.FirmMemberships.AsNoTracking()
                .Where(m => m.UserId == user.Id && (m.Status == WorkspaceMembershipStatus.PendingApproval || m.Status == WorkspaceMembershipStatus.PendingEmailConfirmation))
                .OrderBy(m => m.ManagementFirmId)
                .FirstOrDefaultAsync(ct);
            await SignInMinimalAsync(http, user.Id, pending?.Id, pending?.Role);
            return Results.Redirect("/pending");
        }
        catch
        {
            return Results.Redirect("/verify-email?error=invalid");
        }
    }

    private static async Task<IResult> AcceptInviteAsync(
        HttpContext http,
        IDbContextFactory<AppDbContext> dbFactory,
        CommonGroundWorkspaceService workspace,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var token = form["token"].ToString();
        var fullName = form["fullName"].ToString();
        var email = form["email"].ToString();
        var phone = form["phone"].ToString();
        var password = form["password"].ToString();
        var unit = form["unit"].ToString();
        if (string.IsNullOrWhiteSpace(token))
            return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
        try
        {
            var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{fullName} {email} {phone} {unit}");
            if (scan.IsInjectionDetected)
                return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var hash = HashToken(token.Trim());
            var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
            if (invitation is not null)
            {
                if (invitation.Status != InvitationStatus.Pending)
                    return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
                if (invitation.ExpiresAtUtc < DateTime.UtcNow)
                    return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);

                var resolvedEmail = string.IsNullOrWhiteSpace(email) ? invitation.RecipientEmail : email.Trim();
                if (string.IsNullOrWhiteSpace(resolvedEmail))
                    return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
                var normalized = resolvedEmail.Trim().ToLowerInvariant();
                var existing = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
                User user;
                if (existing is not null)
                {
                    var verified = DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(password, existing.PasswordSaltAndHash);
                    if (!verified)
                        return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
                    user = existing;
                    if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(fullName))
                        user.FullName = fullName.Trim();
                    if (string.IsNullOrWhiteSpace(user.Phone) && !string.IsNullOrWhiteSpace(phone))
                        user.Phone = phone.Trim();
                    user.EmailConfirmed = true;
                    user.EmailConfirmationTokenHash = null;
                }
                else
                {
                    user = new User
                    {
                        FullName = string.IsNullOrWhiteSpace(fullName) ? invitation.RecipientName : fullName.Trim(),
                        Email = resolvedEmail.Trim(),
                        Phone = string.IsNullOrWhiteSpace(phone) ? string.Empty : phone.Trim(),
                        PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(password),
                        EmailConfirmed = true,
                        Provider = IdentityProvider.EmailPassword,
                    };
                    db.Users.Add(user);
                    await db.SaveChangesAsync(ct);
                }

                var targetStatus = invitation.RequiresApproval ? WorkspaceMembershipStatus.PendingApproval : WorkspaceMembershipStatus.Approved;
                var granted = invitation.GrantedPermissions == (WorkspacePermissionFlags)0
                    ? WorkspacePermissionPresets.DefaultForRole(invitation.TargetRole)
                    : invitation.GrantedPermissions;
                var membership = await db.FirmMemberships.FirstOrDefaultAsync(m => m.UserId == user.Id && m.ManagementFirmId == invitation.ManagementFirmId, ct);
                if (membership is null)
                {
                    membership = new FirmMembership
                    {
                        UserId = user.Id,
                        ManagementFirmId = invitation.ManagementFirmId,
                        Role = invitation.TargetRole,
                        Status = targetStatus,
                        GrantedPermissions = granted,
                        DecidedAtUtc = targetStatus == WorkspaceMembershipStatus.Approved ? DateTime.UtcNow : null,
                    };
                    db.FirmMemberships.Add(membership);
                    await db.SaveChangesAsync(ct);
                }
                else if (membership.Status != WorkspaceMembershipStatus.Approved && targetStatus == WorkspaceMembershipStatus.Approved)
                {
                    membership.Status = WorkspaceMembershipStatus.Approved;
                    membership.DecidedAtUtc ??= DateTime.UtcNow;
                    if (granted != (WorkspacePermissionFlags)0)
                        membership.GrantedPermissions = granted;
                    await db.SaveChangesAsync(ct);
                }

                var propertyIds = ParsePropertyIds(invitation.PropertyIdsJson);
                if (invitation.TargetUnitId.HasValue)
                {
                    var targetUnit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == invitation.TargetUnitId.Value, ct);
                    if (targetUnit is not null && !propertyIds.Contains(targetUnit.CommunityId))
                        propertyIds.Add(targetUnit.CommunityId);
                }

                foreach (var propertyId in propertyIds.Distinct().ToList())
                {
                    var communityExists = await db.Communities.AsNoTracking().AnyAsync(c => c.Id == propertyId && c.ManagementFirmId == invitation.ManagementFirmId, ct);
                    if (!communityExists)
                        continue;
                    var hasAccess = await db.MembershipPropertyAccesses.AnyAsync(p => p.FirmMembershipId == membership.Id && p.CommunityId == propertyId, ct);
                    if (!hasAccess)
                    {
                        db.MembershipPropertyAccesses.Add(new MembershipPropertyAccess
                        {
                            FirmMembershipId = membership.Id,
                            CommunityId = propertyId,
                            IsPrimaryManager = invitation.TargetRole == WorkspaceSecurityRole.PropertyManager && propertyIds.Count == 1,
                        });
                    }
                }

                await db.SaveChangesAsync(ct);
                if (invitation.TargetUnitId.HasValue)
                {
                    var targetUnit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == invitation.TargetUnitId.Value, ct);
                    if (targetUnit is not null)
                    {
                        var hasLink = await db.MembershipUnitLinks.AnyAsync(l => l.FirmMembershipId == membership.Id && l.UnitId == targetUnit.Id, ct);
                        if (!hasLink)
                        {
                            db.MembershipUnitLinks.Add(new MembershipUnitLink
                            {
                                FirmMembershipId = membership.Id,
                                CommunityId = targetUnit.CommunityId,
                                UnitId = targetUnit.Id,
                                RelationshipKind = MembershipUnitRelationshipKind.Owner,
                            });
                            await db.SaveChangesAsync(ct);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(unit))
                {
                    var trimmedUnit = unit.Trim();
                    var candidateUnits = await db.Units.AsNoTracking()
                        .Where(u => propertyIds.Contains(u.CommunityId) && u.UnitNumber.ToLower() == trimmedUnit.ToLower())
                        .ToListAsync(ct);
                    foreach (var candidate in candidateUnits)
                    {
                        var hasLink = await db.MembershipUnitLinks.AnyAsync(l => l.FirmMembershipId == membership.Id && l.UnitId == candidate.Id, ct);
                        if (!hasLink)
                        {
                            db.MembershipUnitLinks.Add(new MembershipUnitLink
                            {
                                FirmMembershipId = membership.Id,
                                CommunityId = candidate.CommunityId,
                                UnitId = candidate.Id,
                                RelationshipKind = MembershipUnitRelationshipKind.Owner,
                            });
                        }
                    }

                    await db.SaveChangesAsync(ct);
                }

                invitation.Status = InvitationStatus.Accepted;
                await db.SaveChangesAsync(ct);
                var auditCommunity = propertyIds.FirstOrDefault();
                if (auditCommunity != Guid.Empty)
                {
                    await AppendAuditAsync(db, auditCommunity, membership.Role, membership.Id, "INVITATION_ACCEPTED", $"Invitation {invitation.Id}", $"User {user.Email} accepted invitation as {membership.Role} with status {membership.Status}.", ct);
                    await db.SaveChangesAsync(ct);
                }

                await SignInMinimalAsync(http, user.Id, membership.Id, membership.Role);
                if (membership.Status == WorkspaceMembershipStatus.Approved)
                {
                    var firstProperty = await FirstAccessiblePropertyAsync(db, membership.Id, membership.ManagementFirmId, membership.Role, ct);
                    return Results.Redirect(LandingForRole(membership.Role, firstProperty));
                }

                return Results.Redirect("/pending");
            }

            var legacyPreview = await workspace.GetWorkspaceInvitationPreviewAsync(token.Trim(), ct);
            if (legacyPreview is null)
                return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
            try
            {
                var legacyAccount = await workspace.AcceptWorkspaceInvitationAsync(
                    token.Trim(),
                    legacyPreview.FirmGroupCode,
                    string.IsNullOrWhiteSpace(fullName) ? legacyPreview.FirmName : fullName.Trim(),
                    string.IsNullOrWhiteSpace(email) ? string.Empty : email.Trim(),
                    string.IsNullOrWhiteSpace(phone) ? string.Empty : phone.Trim(),
                    password,
                    IdentityProvider.EmailPassword,
                    string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(),
                    ct);
                var legacyMemberships = await db.AccountMemberships.AsNoTracking()
                    .Where(m => m.UserAccountId == legacyAccount.Id)
                    .OrderByDescending(m => m.CreatedAtUtc)
                    .ToListAsync(ct);
                var legacyActive = legacyMemberships.FirstOrDefault(m => m.ApprovalStatus == WorkspaceMembershipStatus.Approved)
                    ?? legacyMemberships.FirstOrDefault();
                await SignInMinimalAsync(http, legacyAccount.Id, legacyActive?.Id);
                if (legacyActive is null || legacyActive.ApprovalStatus != WorkspaceMembershipStatus.Approved)
                    return Results.Redirect("/pending");
                return Results.Redirect(legacyAccount.Role switch
                {
                    WorkspaceSecurityRole.FirmManager => "/admin",
                    WorkspaceSecurityRole.Resident => "/my-unit",
                    _ => "/my-unit",
                });
            }
            catch
            {
                return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
            }
        }
        catch
        {
            return RedirectWithError("/accept-invite", string.Empty, "invalid", token, null);
        }
    }

    private static async Task<IResult> JoinFirmAsync(
        HttpContext http,
        IDbContextFactory<AppDbContext> dbFactory,
        CancellationToken ct)
    {
        var form = await http.Request.ReadFormAsync(ct);
        var firmCode = form["firmCode"].ToString();
        var fullName = form["fullName"].ToString();
        var email = form["email"].ToString();
        var phone = form["phone"].ToString();
        var password = form["password"].ToString();
        var unit = form["unit"].ToString();
        var proof = form["proof"].ToString();
        if (string.IsNullOrWhiteSpace(proof))
            proof = form["proofOrCompanyNote"].ToString();
        if (string.IsNullOrWhiteSpace(firmCode) || string.IsNullOrWhiteSpace(fullName)
            || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return RedirectWithError("/join", string.Empty, "invalid", null, firmCode);
        try
        {
            var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{fullName} {email} {phone} {proof} {unit}");
            if (scan.IsInjectionDetected)
                return RedirectWithError("/join", string.Empty, "invalid", null, firmCode);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var code = NormalizeFirmCode(firmCode);
            var firm = await db.ManagementFirms.AsNoTracking().FirstOrDefaultAsync(f => f.FirmGroupCode.ToUpper() == code, ct);
            if (firm is null)
                return RedirectWithError("/join", string.Empty, "invalid", null, firmCode);

            var normalized = email.Trim().ToLowerInvariant();
            var existing = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
            User user;
            if (existing is not null)
            {
                var verified = DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(password, existing.PasswordSaltAndHash);
                if (!verified)
                    return RedirectWithError("/join", string.Empty, "invalid", null, firmCode);
                user = existing;
                user.EmailConfirmed = true;
            }
            else
            {
                user = new User
                {
                    FullName = fullName.Trim(),
                    Email = email.Trim(),
                    Phone = (phone ?? string.Empty).Trim(),
                    PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(password),
                    EmailConfirmed = true,
                    Provider = IdentityProvider.EmailPassword,
                };
                db.Users.Add(user);
                await db.SaveChangesAsync(ct);
            }

            var membership = await db.FirmMemberships.FirstOrDefaultAsync(m => m.UserId == user.Id && m.ManagementFirmId == firm.Id, ct);
            if (membership is null)
            {
                membership = new FirmMembership
                {
                    UserId = user.Id,
                    ManagementFirmId = firm.Id,
                    Role = WorkspaceSecurityRole.Resident,
                    Status = WorkspaceMembershipStatus.PendingApproval,
                    GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident),
                };
                db.FirmMemberships.Add(membership);
                await db.SaveChangesAsync(ct);
            }
            else if (membership.Status == WorkspaceMembershipStatus.Approved)
            {
                await SignInMinimalAsync(http, user.Id, membership.Id, membership.Role);
                var firstProperty = await FirstAccessiblePropertyAsync(db, membership.Id, membership.ManagementFirmId, membership.Role, ct);
                return Results.Redirect(LandingForRole(membership.Role, firstProperty));
            }

            var hasRequest = await db.JoinRequests.AnyAsync(j => j.FirmMembershipId == membership.Id && j.Status == JoinRequestStatus.PendingApproval, ct);
            if (!hasRequest)
            {
                Guid? requestedCommunity = null;
                var firstFirmProperty = await db.Communities.AsNoTracking()
                    .Where(c => c.ManagementFirmId == firm.Id)
                    .OrderBy(c => c.Name)
                    .Select(c => c.Id)
                    .FirstOrDefaultAsync(ct);
                if (firstFirmProperty != Guid.Empty)
                    requestedCommunity = firstFirmProperty;
                db.JoinRequests.Add(new JoinRequest
                {
                    ManagementFirmId = firm.Id,
                    FirmMembershipId = membership.Id,
                    RequestedRole = WorkspaceSecurityRole.Resident,
                    RequestedCommunityId = requestedCommunity,
                    RequestedUnitNumber = (unit ?? string.Empty).Trim(),
                    ProofOrCompanyNote = (proof ?? string.Empty).Trim(),
                    Status = JoinRequestStatus.PendingApproval,
                    CreatedAtUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
                if (requestedCommunity.HasValue)
                {
                    await AppendAuditAsync(db, requestedCommunity.Value, membership.Role, membership.Id, "JOIN_REQUEST_CREATED", $"Firm {firm.FirmGroupCode}", $"User {user.Email} requested to join with code {code}.", ct);
                    await db.SaveChangesAsync(ct);
                }
            }

            await SignInMinimalAsync(http, user.Id, membership.Id, membership.Role);
            return Results.Redirect("/pending");
        }
        catch
        {
            return RedirectWithError("/join", string.Empty, "invalid", null, firmCode);
        }
    }

    private static List<Guid> ParsePropertyIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            var ids = JsonSerializer.Deserialize<List<Guid>>(json);
            return ids ?? [];
        }
        catch
        {
            try
            {
                var strings = JsonSerializer.Deserialize<List<string>>(json);
                if (strings is null)
                    return [];
                return strings
                    .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                    .Where(g => g.HasValue && g.Value != Guid.Empty)
                    .Select(g => g!.Value)
                    .ToList();
            }
            catch
            {
                return [];
            }
        }
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
