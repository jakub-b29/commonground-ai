using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public sealed partial class CommonGroundWorkspaceService
{
    private static string NormalizeFirmGroupCode(string code)
        => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static string NewFirmGroupCode(string firmName)
    {
        var slug = new string(firmName.Where(char.IsLetterOrDigit).Take(6).ToArray()).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(slug)) slug = "FIRM";
        return $"CG-{slug}-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}";
    }

    private static string NewInvitationToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return "INV-" + Convert.ToHexString(bytes);
    }

    private static string HashInvitationToken(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token ?? string.Empty);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static bool InvitationTokenMatches(WorkspaceInvitation invitation, string token)
    {
        if (string.IsNullOrWhiteSpace(token) || invitation is null) return false;
        var hash = HashInvitationToken(token.Trim());
        if (!string.IsNullOrWhiteSpace(invitation.InvitationTokenHash)
            && string.Equals(invitation.InvitationTokenHash, hash, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrWhiteSpace(invitation.InvitationToken)
            && string.Equals(invitation.InvitationToken, token.Trim(), StringComparison.Ordinal);
    }

    public async Task<IReadOnlyList<ManagementFirm>> GetManagementFirmsAsync(CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.ManagementFirms.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);
    }

    public async Task<ManagementFirm?> GetManagementFirmAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.ManagementFirms
            .Include(f => f.Communities)
            .Include(f => f.ManagerGroups).ThenInclude(g => g.Messages)
            .Include(f => f.ManagerMessages)
            .Include(f => f.Invitations)
            .FirstOrDefaultAsync(f => f.Id == firmId, ct);
    }

    public async Task<ManagementFirm?> GetManagementFirmByCodeAsync(string firmGroupCode, CancellationToken ct = default)
    {
        var code = NormalizeFirmGroupCode(firmGroupCode);
        await using var db = await CreateDbAsync(ct);
        return await db.ManagementFirms
            .Include(f => f.Communities)
            .Include(f => f.ManagerGroups).ThenInclude(g => g.Messages)
            .Include(f => f.ManagerMessages)
            .Include(f => f.Invitations)
            .FirstOrDefaultAsync(f => f.FirmGroupCode.ToUpper() == code, ct);
    }

    public async Task<IReadOnlyList<Community>> GetFirmCommunitiesAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.Communities.AsNoTracking().Where(c => c.ManagementFirmId == firmId).OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<ManagementFirm> RegisterManagementFirmAsync(
        string firmName,
        string? requestedFirmGroupCode,
        SystemOperatingMode operatingMode,
        string managerFullName,
        string managerEmail,
        string managerPhone,
        string password,
        IdentityProvider provider,
        string managingFirmLegalName = "",
        string firmTaxIdOrNip = "",
        string licenseNumber = "",
        string leadDirectorName = "",
        string officeAddress = "",
        string supportEmail = "",
        string emergencyDispatchPhone = "",
        string associationLegalName = "",
        string hoaEinNumber = "",
        string boardPresidentName = "",
        string managementAgentContact = "",
        string clubhouseOrOfficeAddress = "",
        string hoaBoardEmail = "",
        string hoaHotlinePhone = "",
        bool requireEmailConfirmation = false,
        CancellationToken ct = default)
    {
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{firmName} {managerFullName} {managerEmail} {officeAddress}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Firm registration blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var code = string.IsNullOrWhiteSpace(requestedFirmGroupCode)
            ? NewFirmGroupCode(firmName)
            : NormalizeFirmGroupCode(requestedFirmGroupCode);
        if (await db.ManagementFirms.AnyAsync(f => f.FirmGroupCode.ToUpper() == code, ct))
            throw new InvalidOperationException($"Firm group code '{code}' is already in use.");

        var firm = new ManagementFirm
        {
            Name = firmName.Trim(),
            FirmGroupCode = code,
            OperatingMode = operatingMode,
            ManagingFirmLegalName = managingFirmLegalName.Trim(),
            FirmTaxIdOrNip = firmTaxIdOrNip.Trim(),
            LicenseNumber = licenseNumber.Trim(),
            LeadDirectorName = string.IsNullOrWhiteSpace(leadDirectorName) ? managerFullName.Trim() : leadDirectorName.Trim(),
            OfficeAddress = officeAddress.Trim(),
            SupportEmail = supportEmail.Trim(),
            EmergencyDispatchPhone = emergencyDispatchPhone.Trim(),
            AssociationLegalName = associationLegalName.Trim(),
            HoaEinNumber = hoaEinNumber.Trim(),
            BoardPresidentName = boardPresidentName.Trim(),
            ManagementAgentContact = managementAgentContact.Trim(),
            ClubhouseOrOfficeAddress = clubhouseOrOfficeAddress.Trim(),
            HoaBoardEmail = hoaBoardEmail.Trim(),
            HoaHotlinePhone = hoaHotlinePhone.Trim()
        };
        db.ManagementFirms.Add(firm);
        firm.RequireEmailConfirmation = requireEmailConfirmation;
        firm.IsEmailConfirmed = !requireEmailConfirmation;
        firm.AcceptedAtUtc = requireEmailConfirmation ? null : DateTime.UtcNow;
        if (requireEmailConfirmation)
        {
            var etok = NewInvitationToken();
            firm.EmailConfirmationTokenHash = HashInvitationToken(etok);
        }

        

        var manager = new WorkspaceUserAccount
        {
            CommunityId = null,
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            FullName = managerFullName.Trim(),
            Email = managerEmail.Trim(),
            Phone = managerPhone.Trim(),
            Provider = provider,
            ProviderSubjectId = $"{provider.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(password),
            Role = WorkspaceSecurityRole.FirmManager,
            AccountKind = WorkspaceAccountKind.FirmAccount,
            EmailConfirmed = !requireEmailConfirmation,
            EmailConfirmationTokenHash = firm.EmailConfirmationTokenHash,
            AcceptedAtUtc = requireEmailConfirmation ? null : DateTime.UtcNow,
            AllowedCommunityIdsCsv = string.Empty,
            GrantedByPropertyManager = true
        };
        db.UserAccounts.Add(manager);

        var defaultGroup = new FirmManagerGroup
        {
            ManagementFirmId = firm.Id,
            Code = "FIRM-EXEC-1",
            Name = "Firm Executive Channel",
            Topic = "Coordination between Firm Manager and all Property Managers"
        };
        db.FirmManagerGroups.Add(defaultGroup);
        firm.ManagerGroups.Add(defaultGroup);

        var welcome = new FirmManagerMessage
        {
            ManagementFirmId = firm.Id,
            FirmManagerGroupId = defaultGroup.Id,
            GroupName = defaultGroup.Name,
            SenderUserId = manager.Id,
            SenderName = manager.FullName,
            SenderRole = WorkspaceSecurityRole.FirmManager,
            SenderEmail = manager.Email,
            SenderPhone = manager.Phone,
            PropertyScopeName = "All Firm Properties",
            Subject = $"Firm '{firm.Name}' registered with code {firm.FirmGroupCode}",
            Body = $"Firm group {firm.FirmGroupCode} is ready. Create additional properties under /firm and invite Property Managers, Residents, and Contractors with scoped registration links.",
            IsPriorityNotice = true
        };
        db.FirmManagerMessages.Add(welcome);
        firm.ManagerMessages.Add(welcome);
        defaultGroup.Messages.Add(welcome);

        await db.SaveChangesAsync(ct);
        var membership = new WorkspaceAccountMembership
        {
            UserAccountId = manager.Id,
            ManagementFirmId = firm.Id,
            Role = WorkspaceSecurityRole.FirmManager,
            ApprovalStatus = requireEmailConfirmation ? WorkspaceMembershipStatus.PendingEmailConfirmation : WorkspaceMembershipStatus.Approved,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.FirmManager),
            AllowedCommunityIdsCsv = string.Empty,
            DecidedAtUtc = requireEmailConfirmation ? null : DateTime.UtcNow
        };
        db.AccountMemberships.Add(membership);
        await db.SaveChangesAsync(ct);
        return firm;
    }

    public sealed record FirmEmailConfirmationPreview(
        Guid FirmId,
        string FirmName,
        string FirmGroupCode,
        string ManagerEmail,
        bool RequireEmailConfirmation,
        bool IsEmailConfirmed,
        DateTime? AcceptedAtUtc);

    public async Task<FirmEmailConfirmationPreview?> GetFirmEmailConfirmationPreviewAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.AsNoTracking().FirstOrDefaultAsync(f => f.Id == firmId, ct);
        if (firm is null) return null;
        var manager = await db.UserAccounts.AsNoTracking()
            .Where(u => u.ManagementFirmId == firmId && u.Role == WorkspaceSecurityRole.FirmManager)
            .OrderBy(u => u.FullName)
            .FirstOrDefaultAsync(ct);
        return new FirmEmailConfirmationPreview(
            firm.Id,
            firm.Name,
            firm.FirmGroupCode,
            manager?.Email ?? string.Empty,
            firm.RequireEmailConfirmation,
            firm.IsEmailConfirmed,
            firm.AcceptedAtUtc);
    }

    public async Task<ManagementFirm> ConfirmFirmEmailAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Confirmation token is required.");
        var trimmed = token.Trim();
        var hash = HashInvitationToken(trimmed);
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.EmailConfirmationTokenHash == hash, ct)
            ?? throw new InvalidOperationException("Confirmation token is invalid.");
        firm.IsEmailConfirmed = true;
        firm.AcceptedAtUtc ??= DateTime.UtcNow;
        firm.EmailConfirmationTokenHash = null;
        var managers = await db.UserAccounts
            .Where(u => u.ManagementFirmId == firm.Id && u.Role == WorkspaceSecurityRole.FirmManager)
            .ToListAsync(ct);
        foreach (var account in managers)
        {
            if (account.EmailConfirmationTokenHash is not null
                && !string.Equals(account.EmailConfirmationTokenHash, hash, StringComparison.Ordinal))
                continue;
            account.EmailConfirmed = true;
            account.AcceptedAtUtc ??= DateTime.UtcNow;
            account.EmailConfirmationTokenHash = null;
        }
        var pendingMemberships = await db.AccountMemberships
            .Where(m => m.ManagementFirmId == firm.Id && m.ApprovalStatus == WorkspaceMembershipStatus.PendingEmailConfirmation)
            .ToListAsync(ct);
        foreach (var pending in pendingMemberships)
        {
            pending.ApprovalStatus = WorkspaceMembershipStatus.Approved;
            pending.DecidedAtUtc ??= DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return firm;
    }

    public async Task<ManagementFirm> SetFirmEmailConfirmationPolicyAsync(Guid firmId, bool enabled, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        if (actorRole != WorkspaceSecurityRole.FirmManager)
            throw new UnauthorizedAccessException("Only a firm manager may change the email confirmation policy.");
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.Id == firmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");
        firm.RequireEmailConfirmation = enabled;
        if (!enabled)
        {
            firm.IsEmailConfirmed = true;
            firm.AcceptedAtUtc ??= DateTime.UtcNow;
            firm.EmailConfirmationTokenHash = null;
            var accounts = await db.UserAccounts.Where(u => u.ManagementFirmId == firmId).ToListAsync(ct);
            foreach (var account in accounts)
            {
                account.EmailConfirmed = true;
                account.AcceptedAtUtc ??= DateTime.UtcNow;
                account.EmailConfirmationTokenHash = null;
            }
            var pending = await db.AccountMemberships
                .Where(m => m.ManagementFirmId == firmId && m.ApprovalStatus == WorkspaceMembershipStatus.PendingEmailConfirmation)
                .ToListAsync(ct);
            foreach (var entry in pending)
            {
                entry.ApprovalStatus = WorkspaceMembershipStatus.Approved;
                entry.DecidedAtUtc ??= DateTime.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
        return firm;
    }

    public async Task<ManagementFirm> UpdateManagementFirmContactsAsync(
        Guid firmId,
        string managingFirmLegalName,
        string firmTaxIdOrNip,
        string licenseNumber,
        string leadDirectorName,
        string officeAddress,
        string supportEmail,
        string emergencyDispatchPhone,
        string associationLegalName,
        string hoaEinNumber,
        string boardPresidentName,
        string managementAgentContact,
        string clubhouseOrOfficeAddress,
        string hoaBoardEmail,
        string hoaHotlinePhone,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (actorRole != WorkspaceSecurityRole.FirmManager)
            throw new UnauthorizedAccessException("Only the Firm Manager may edit firm or HOA contact details.");
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.Id == firmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{officeAddress} {supportEmail} {clubhouseOrOfficeAddress}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Contact update blocked by security guard.");
        firm.ManagingFirmLegalName = managingFirmLegalName.Trim();
        firm.FirmTaxIdOrNip = firmTaxIdOrNip.Trim();
        firm.LicenseNumber = licenseNumber.Trim();
        firm.LeadDirectorName = leadDirectorName.Trim();
        firm.OfficeAddress = officeAddress.Trim();
        firm.SupportEmail = supportEmail.Trim();
        firm.EmergencyDispatchPhone = emergencyDispatchPhone.Trim();
        firm.AssociationLegalName = associationLegalName.Trim();
        firm.HoaEinNumber = hoaEinNumber.Trim();
        firm.BoardPresidentName = boardPresidentName.Trim();
        firm.ManagementAgentContact = managementAgentContact.Trim();
        firm.ClubhouseOrOfficeAddress = clubhouseOrOfficeAddress.Trim();
        firm.HoaBoardEmail = hoaBoardEmail.Trim();
        firm.HoaHotlinePhone = hoaHotlinePhone.Trim();
        await db.SaveChangesAsync(ct);
        return firm;
    }

    public async Task<Community> CreatePropertyForFirmAsync(
        Guid firmId,
        string propertyName,
        string streetAddress,
        string city,
        SystemOperatingMode operatingMode,
        string taxIdOrEin,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (actorRole != WorkspaceSecurityRole.FirmManager)
            throw new UnauthorizedAccessException("Only the Firm Manager may create properties.");
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.Id == firmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{propertyName} {streetAddress} {city}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Property creation blocked by security guard.");
        var defaultManager = await db.UserAccounts
            .Where(u => u.ManagementFirmId == firm.Id && u.Role == WorkspaceSecurityRole.FirmManager)
            .OrderBy(u => u.Email)
            .FirstOrDefaultAsync(ct);
        var existingNumbers = await db.Communities.AsNoTracking().Select(c => c.CommunityAccountNumber).ToListAsync(ct);
        var community = new Community
        {
            CommunityAccountNumber = AccountNumberGenerator.NextCommunityAccountNumber(existingNumbers),
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            Name = propertyName.Trim(),
            StreetAddress = streetAddress.Trim(),
            City = city.Trim(),
            OperatingMode = operatingMode,
            TaxIdOrEin = taxIdOrEin.Trim(),
            AssignedPropertyManagerId = defaultManager?.Id,
            AssignedPropertyManagerName = defaultManager?.FullName ?? string.Empty,
            AssignedPropertyManagerEmail = defaultManager?.Email ?? string.Empty,
            AssignedPropertyManagerPhone = defaultManager?.Phone ?? string.Empty,
            AdministrativeEmail = defaultManager?.Email ?? firm.SupportEmail,
            AdministrativePhone = defaultManager?.Phone ?? firm.EmergencyDispatchPhone
        };
        db.Communities.Add(community);
        var defaultBuilding = new Building
        {
            CommunityId = community.Id,
            Name = "Building A",
            StreetAddress = community.StreetAddress,
            FloorsCount = 4,
            StairwellsCount = 2
        };
        community.Buildings.Add(defaultBuilding);
        db.Buildings.Add(defaultBuilding);
        foreach (var stackCode in new[] { "STACK-A1", "STACK-A2", "STACK-B1" })
        {
            var stack = new VerticalRiserStack
            {
                BuildingId = defaultBuilding.Id,
                Code = stackCode,
                Description = stackCode.Contains('B') ? "Heating riser" : "Cold water riser",
                ShutoffValveLocation = "Basement manifold"
            };
            defaultBuilding.RiserStacks.Add(stack);
            db.RiserStacks.Add(stack);
        }
        await db.SaveChangesAsync(ct);
        return community;
    }

    public async Task<IReadOnlyList<WorkspaceUserAccount>> GetFirmPropertyManagerCandidatesAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.UserAccounts.AsNoTracking()
            .Where(u => u.ManagementFirmId == firmId && (u.Role == WorkspaceSecurityRole.PropertyManager || u.Role == WorkspaceSecurityRole.FirmManager))
            .OrderBy(u => u.FullName)
            .ToListAsync(ct);
    }

    public async Task<WorkspaceUserAccount?> GetFirmDefaultManagerAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.UserAccounts.AsNoTracking()
            .Where(u => u.ManagementFirmId == firmId && u.Role == WorkspaceSecurityRole.FirmManager)
            .OrderBy(u => u.Email)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Community> UpdatePropertySettingsAsync(
        Guid communityId,
        string name,
        string streetAddress,
        string city,
        string taxIdOrEin,
        decimal operatingRatePerM2,
        decimal repairFundRatePerM2,
        decimal wasteFeePerOccupant,
        decimal coldWaterTariff,
        decimal hotWaterTariff,
        decimal heatingTariff,
        decimal electricityTariff,
        WorkspaceSecurityRole actorRole,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var isFirmManager = actorRole == WorkspaceSecurityRole.FirmManager;
        var isAssignedManager = actorRole == WorkspaceSecurityRole.PropertyManager
            && (community.AssignedPropertyManagerId == actorUserId || actorUserId is null);
        if (!isFirmManager && !isAssignedManager)
            throw new UnauthorizedAccessException("Only the Firm Manager or the assigned Property Manager may edit this property.");
        community.Name = string.IsNullOrWhiteSpace(name) ? community.Name : name.Trim();
        community.StreetAddress = string.IsNullOrWhiteSpace(streetAddress) ? community.StreetAddress : streetAddress.Trim();
        community.City = string.IsNullOrWhiteSpace(city) ? community.City : city.Trim();
        community.TaxIdOrEin = string.IsNullOrWhiteSpace(taxIdOrEin) ? community.TaxIdOrEin : taxIdOrEin.Trim();
        community.OperatingRatePerM2 = operatingRatePerM2;
        community.RepairFundRatePerM2 = repairFundRatePerM2;
        community.WasteFeePerOccupant = wasteFeePerOccupant;
        community.ColdWaterTariffPerM3 = coldWaterTariff;
        community.HotWaterHeatingCostPerM3 = hotWaterTariff;
        community.DistrictHeatingTariffPerGj = heatingTariff;
        community.CommonElectricityTariffPerKwh = electricityTariff;
        await db.SaveChangesAsync(ct);
        return community;
    }

    public async Task<WorkspaceUserAccount> AssignPropertyManagerAsync(
        Guid communityId,
        string managerName,
        string managerEmail,
        string managerPhone,
        Guid? existingUserId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (actorRole != WorkspaceSecurityRole.FirmManager)
            throw new UnauthorizedAccessException("Only the Firm Manager may assign Property Managers.");
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        WorkspaceUserAccount? account = null;
        if (existingUserId.HasValue)
            account = community.UserAccounts.FirstOrDefault(u => u.Id == existingUserId.Value)
                ?? await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == existingUserId.Value, ct);
        if (account is null)
            account = community.UserAccounts.FirstOrDefault(u => string.Equals(u.Email, managerEmail.Trim(), StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            account = new WorkspaceUserAccount
            {
                CommunityId = community.Id,
                ManagementFirmId = community.ManagementFirmId,
                FirmGroupCode = community.FirmGroupCode,
                FullName = managerName.Trim(),
                Email = managerEmail.Trim(),
                Phone = managerPhone.Trim(),
                Provider = IdentityProvider.EmailPassword,
                ProviderSubjectId = $"email-{managerEmail.Trim().ToLowerInvariant()}",
                PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
                Role = WorkspaceSecurityRole.PropertyManager,
                GrantedByPropertyManager = true
            };
            db.UserAccounts.Add(account);
            community.UserAccounts.Add(account);
        }
        else
        {
            account.Role = WorkspaceSecurityRole.PropertyManager;
            account.ManagementFirmId = community.ManagementFirmId;
            account.FirmGroupCode = community.FirmGroupCode;
            account.CommunityId = community.Id;
            if (!string.IsNullOrWhiteSpace(managerName)) account.FullName = managerName.Trim();
            if (!string.IsNullOrWhiteSpace(managerPhone)) account.Phone = managerPhone.Trim();
            account.IsBlankRegisteredPendingAssignment = false;
        }
        community.AssignedPropertyManagerId = account.Id;
        community.AssignedPropertyManagerName = account.FullName;
        community.AssignedPropertyManagerEmail = account.Email;
        community.AssignedPropertyManagerPhone = account.Phone;
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<WorkspaceInvitation> CreateWorkspaceInvitationAsync(
        Guid firmId,
        string recipientEmail,
        string recipientName,
        string recipientPhone,
        WorkspaceSecurityRole targetRole,
        InvitationAssignmentMode assignmentMode,
        Guid? targetCommunityId,
        string? targetUnitNumber,
        string? contractorCompanyOrTrade,
        string issuedByName,
        WorkspaceSecurityRole actorRole,
        IReadOnlyList<Guid>? targetCommunityIds = null,
        WorkspacePermissionFlags grantedPermissions = (WorkspacePermissionFlags)0,
        bool requiresManagerApproval = true,
        CancellationToken ct = default)
    {
        var canInvite = actorRole == WorkspaceSecurityRole.FirmManager
            || (actorRole == WorkspaceSecurityRole.PropertyManager && targetRole != WorkspaceSecurityRole.PropertyManager && targetRole != WorkspaceSecurityRole.FirmManager);
        if (!canInvite)
            throw new UnauthorizedAccessException("Only the Firm Manager may invite Property Managers. Property Managers may invite Residents and Contractors.");
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.Include(f => f.Communities).FirstOrDefaultAsync(f => f.Id == firmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");
        if (targetRole == WorkspaceSecurityRole.FirmManager)
            throw new InvalidOperationException("Firm Manager accounts are created only through firm registration.");
        Community? target = null;
        if (targetCommunityId.HasValue)
        {
            target = await db.Communities.FirstOrDefaultAsync(c => c.Id == targetCommunityId.Value, ct)
                ?? throw new InvalidOperationException("Target property not found.");
            if (target.ManagementFirmId != firm.Id)
                throw new InvalidOperationException("Target property does not belong to this firm.");
        }
        else if (assignmentMode == InvitationAssignmentMode.AutoAssignSpecificProperty)
        {
            target = firm.Communities.OrderBy(c => c.Name).FirstOrDefault()
                ?? await db.Communities.Where(c => c.ManagementFirmId == firm.Id).OrderBy(c => c.Name).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Firm has no properties to assign.");
        }
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{recipientEmail} {recipientName} {contractorCompanyOrTrade}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Invitation blocked by security guard.");
        var token = NewInvitationToken();
        var communityIds = targetCommunityIds ?? (targetCommunityId.HasValue ? new[] { targetCommunityId.Value } : Array.Empty<Guid>());
        var preAssigned = grantedPermissions == (WorkspacePermissionFlags)0
            ? WorkspacePermissionPresets.DefaultForRole(targetRole)
            : grantedPermissions;
        Guid? compatTargetCommunityId = target?.Id ?? (communityIds.Count > 0 ? communityIds[0] : null);
        if (compatTargetCommunityId == Guid.Empty)
            compatTargetCommunityId = null;
        var invitation = new WorkspaceInvitation
        {
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            InvitationToken = token,
            InvitationTokenHash = HashInvitationToken(token),
            RecipientEmail = recipientEmail.Trim(),
            RecipientName = recipientName.Trim(),
            RecipientPhone = (recipientPhone ?? string.Empty).Trim(),
            TargetRole = targetRole,
            AssignmentMode = assignmentMode,
            TargetCommunityId = compatTargetCommunityId,
            TargetCommunityName = target?.Name ?? string.Empty,
            TargetCommunityIdsCsv = string.Join(",", communityIds),
            PreAssignedPermissions = preAssigned,
            RequiresManagerApproval = requiresManagerApproval,
            TargetUnitNumber = targetUnitNumber?.Trim(),
            ContractorCompanyOrTrade = contractorCompanyOrTrade?.Trim(),
            IssuedByManagerName = issuedByName.Trim(),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        };
        invitation.RegistrationRelativeUrl = $"/accept-invite?token={invitation.InvitationToken}&code={firm.FirmGroupCode}";
        db.WorkspaceInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        return invitation;
    }

    public async Task<IReadOnlyList<WorkspaceInvitation>> GetFirmInvitationsAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.WorkspaceInvitations.AsNoTracking()
            .Where(i => i.ManagementFirmId == firmId)
            .OrderByDescending(i => i.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<WorkspaceInvitation> RevokeWorkspaceInvitationAsync(Guid invitationId, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        if (actorRole != WorkspaceSecurityRole.FirmManager && actorRole != WorkspaceSecurityRole.PropertyManager)
            throw new UnauthorizedAccessException("Only a manager may revoke invitations.");
        await using var db = await CreateDbAsync(ct);
        var invitation = await db.WorkspaceInvitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct)
            ?? throw new InvalidOperationException("Invitation not found.");
        invitation.Status = WorkspaceInvitationStatus.Revoked;
        await db.SaveChangesAsync(ct);
        return invitation;
    }

    public async Task<WorkspaceInvitation?> GetWorkspaceInvitationAsync(string invitationToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(invitationToken)) return null;
        var trimmed = invitationToken.Trim();
        var hash = HashInvitationToken(trimmed);
        await using var db = await CreateDbAsync(ct);
        return await db.WorkspaceInvitations.AsNoTracking()
            .FirstOrDefaultAsync(i => i.InvitationToken == trimmed || i.InvitationTokenHash == hash, ct);
    }

    public sealed record WorkspaceInvitationPreview(
        Guid InvitationId,
        string FirmName,
        string FirmGroupCode,
        string MaskedRecipientEmail,
        string TargetRole,
        string TargetPropertyName,
        string? TargetUnitNumber,
        string AssignmentMode,
        DateTime ExpiresAtUtc,
        bool IsExpired,
        string Status);

    public async Task<WorkspaceInvitationPreview?> GetWorkspaceInvitationPreviewAsync(string invitationToken, CancellationToken ct = default)
    {
        var invitation = await GetWorkspaceInvitationAsync(invitationToken, ct);
        if (invitation is null) return null;
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.AsNoTracking().FirstOrDefaultAsync(f => f.Id == invitation.ManagementFirmId, ct);
        var email = invitation.RecipientEmail ?? string.Empty;
        var masked = email.Length <= 2 ? "***" : string.Concat(email.AsSpan(0, 2), "***@", email.Contains('@', StringComparison.Ordinal) ? email[(email.IndexOf('@') + 1)..] : "***");
        return new WorkspaceInvitationPreview(
            invitation.Id,
            firm?.Name ?? string.Empty,
            invitation.FirmGroupCode,
            masked,
            invitation.TargetRole.ToString(),
            invitation.TargetCommunityName,
            invitation.TargetUnitNumber,
            invitation.AssignmentMode.ToString(),
            invitation.ExpiresAtUtc,
            invitation.IsExpired || invitation.ExpiresAtUtc < DateTime.UtcNow,
            invitation.Status.ToString());
    }

    public async Task<WorkspaceUserAccount> AcceptWorkspaceInvitationAsync(
        string invitationToken,
        string firmGroupCode,
        string fullName,
        string email,
        string phone,
        string password,
        IdentityProvider provider,
        string? unitNumber,
        CancellationToken ct = default)
    {
        if (provider != IdentityProvider.EmailPassword)
            throw new InvalidOperationException("Social sign-in is not configured. Use email and password.");
        if (string.IsNullOrWhiteSpace(password) || password.Trim().Length < 8)
            throw new InvalidOperationException("A password of at least 8 characters is required.");
        await using var db = await CreateDbAsync(ct);
        var trimmed = (invitationToken ?? string.Empty).Trim();
        var hash = HashInvitationToken(trimmed);
        var invitation = await db.WorkspaceInvitations.FirstOrDefaultAsync(i => i.InvitationToken == trimmed || i.InvitationTokenHash == hash, ct)
            ?? throw new InvalidOperationException("Invitation token is invalid.");
        if (invitation.Status != WorkspaceInvitationStatus.Pending)
            throw new InvalidOperationException("This invitation has already been used or revoked.");
        if (invitation.ExpiresAtUtc < DateTime.UtcNow)
            throw new InvalidOperationException("This invitation has expired. Ask a manager for a new invitation link.");
        if (!string.Equals(NormalizeFirmGroupCode(invitation.FirmGroupCode), NormalizeFirmGroupCode(firmGroupCode), StringComparison.Ordinal))
            throw new InvalidOperationException("Firm group code does not match this invitation. Signing always requires the correct firm group code.");
        var firm = await db.ManagementFirms.Include(f => f.Communities).FirstOrDefaultAsync(f => f.Id == invitation.ManagementFirmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");

        Community? target = null;
        if (invitation.TargetCommunityId.HasValue)
            target = await LoadCommunityAsync(db, invitation.TargetCommunityId.Value, ct);
        target ??= await db.Communities.Where(c => c.ManagementFirmId == firm.Id).OrderBy(c => c.Name).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Firm has no properties yet.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{fullName} {email} {phone}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Registration blocked by security guard.");

        var resolvedEmail = string.IsNullOrWhiteSpace(email) ? invitation.RecipientEmail : email.Trim();
        var normalized = resolvedEmail.Trim().ToLowerInvariant();
        var existing = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
        var allowedCsv = !string.IsNullOrWhiteSpace(invitation.TargetCommunityIdsCsv)
            ? invitation.TargetCommunityIdsCsv
            : target.Id.ToString();
        var perms = invitation.PreAssignedPermissions != (WorkspacePermissionFlags)0
            ? invitation.PreAssignedPermissions
            : WorkspacePermissionPresets.DefaultForRole(invitation.TargetRole);
        var membershipStatus = invitation.RequiresManagerApproval
            ? WorkspaceMembershipStatus.PendingApproval
            : WorkspaceMembershipStatus.Approved;
        var membershipDecidedAt = invitation.RequiresManagerApproval
            ? (DateTime?)null
            : DateTime.UtcNow;
        var linkedUnits = unitNumber?.Trim() ?? invitation.TargetUnitNumber?.Trim() ?? string.Empty;

        if (existing is not null)
        {
            var verified = DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(password, existing.PasswordSaltAndHash);
            if (!verified)
                throw new InvalidOperationException("Password does not match the existing account.");
            var existingMembership = new WorkspaceAccountMembership
            {
                UserAccountId = existing.Id,
                ManagementFirmId = firm.Id,
                Role = invitation.TargetRole,
                ApprovalStatus = membershipStatus,
                GrantedPermissions = perms,
                AllowedCommunityIdsCsv = allowedCsv,
                LinkedUnitNumbersCsv = linkedUnits,
                ContractorCompanyOrTrade = invitation.ContractorCompanyOrTrade,
                DecidedAtUtc = membershipDecidedAt
            };
            db.AccountMemberships.Add(existingMembership);
            if (invitation.TargetRole == WorkspaceSecurityRole.PropertyManager)
            {
                target.AssignedPropertyManagerId = existing.Id;
                target.AssignedPropertyManagerName = existing.FullName;
                target.AssignedPropertyManagerEmail = existing.Email;
                target.AssignedPropertyManagerPhone = existing.Phone;
            }
            invitation.Status = WorkspaceInvitationStatus.Accepted;
            invitation.AcceptedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var isContractor = invitation.TargetRole == WorkspaceSecurityRole.Contractor;
        var isManager = invitation.TargetRole == WorkspaceSecurityRole.PropertyManager;
        var account = new WorkspaceUserAccount
        {
            CommunityId = target.Id,
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            FullName = string.IsNullOrWhiteSpace(fullName) ? invitation.RecipientName : fullName.Trim(),
            Email = resolvedEmail,
            Phone = string.IsNullOrWhiteSpace(phone) ? invitation.RecipientPhone : phone.Trim(),
            Provider = provider,
            ProviderSubjectId = string.Concat(provider.ToString().ToLowerInvariant(), "-", Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture)),
            PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(password),
            Role = invitation.TargetRole,
            LinkedUnitNumber = isContractor || isManager ? null : (unitNumber?.Trim() ?? invitation.TargetUnitNumber?.Trim()),
            ContractorCompanyOrTrade = invitation.ContractorCompanyOrTrade,
            ContractorScope = invitation.AssignmentMode == InvitationAssignmentMode.AutoAssignAllFirmProperties
                ? ContractorPropertyScope.AllFirmProperties
                : ContractorPropertyScope.SpecificProperty,
            AllowedCommunityIdsCsv = invitation.AssignmentMode == InvitationAssignmentMode.AutoAssignAllFirmProperties
                ? string.Join(",", firm.Communities.Select(c => c.Id).Concat(new[] { target.Id }).Distinct())
                : target.Id.ToString(),
            IsBlankRegisteredPendingAssignment = invitation.AssignmentMode == InvitationAssignmentMode.BlankManualAssign,
            GrantedByPropertyManager = true,
            AccountKind = WorkspaceAccountKind.RegularAccount,
            EmailConfirmed = true,
            AcceptedAtUtc = DateTime.UtcNow
        };
        db.UserAccounts.Add(account);
        target.UserAccounts.Add(account);

        if (isManager)
        {
            target.AssignedPropertyManagerId = account.Id;
            target.AssignedPropertyManagerName = account.FullName;
            target.AssignedPropertyManagerEmail = account.Email;
            target.AssignedPropertyManagerPhone = account.Phone;
        }

        var membership = new WorkspaceAccountMembership
        {
            UserAccountId = account.Id,
            ManagementFirmId = firm.Id,
            Role = invitation.TargetRole,
            ApprovalStatus = membershipStatus,
            GrantedPermissions = perms,
            AllowedCommunityIdsCsv = allowedCsv,
            LinkedUnitNumbersCsv = linkedUnits,
            ContractorCompanyOrTrade = invitation.ContractorCompanyOrTrade,
            DecidedAtUtc = membershipDecidedAt
        };
        db.AccountMemberships.Add(membership);

        invitation.Status = WorkspaceInvitationStatus.Accepted;
        invitation.AcceptedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<WorkspaceUserAccount?> AuthenticateUserByEmailAsync(
        string email,
        string? passwordOrProviderToken,
        IdentityProvider provider = IdentityProvider.EmailPassword,
        CancellationToken ct = default)
    {
        if (provider != IdentityProvider.EmailPassword)
            throw new InvalidOperationException("Social sign-in is not configured. Use email and password.");
        if (string.IsNullOrWhiteSpace(email))
            return null;
        await using var db = await CreateDbAsync(ct);
        var normalized = email.Trim().ToLowerInvariant();
        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
        if (user is null)
            return null;
        var success = passwordOrProviderToken is not null && DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(passwordOrProviderToken, user.PasswordSaltAndHash);
        if (!success)
            return null;
        user.LastLoginAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<WorkspaceUserAccount?> AuthenticateUserWithFirmCodeAsync(
        string firmGroupCode,
        IdentityProvider provider,
        string email,
        string? passwordOrProviderToken,
        CancellationToken ct = default)
    {
        if (provider != IdentityProvider.EmailPassword)
            throw new InvalidOperationException("Social sign-in is not configured. Use email and password.");
        var code = NormalizeFirmGroupCode(firmGroupCode);
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Firm group code is required for sign-in.");
        if (string.IsNullOrWhiteSpace(email))
            return null;
        await using var db = await CreateDbAsync(ct);
        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await db.UserAccounts
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail, ct);
        if (user is null) return null;
        if (!string.Equals(NormalizeFirmGroupCode(user.FirmGroupCode), code, StringComparison.Ordinal))
        {
            var communityCode = string.Empty;
            if (user.CommunityId.HasValue)
            {
                var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == user.CommunityId.Value, ct);
                communityCode = community is null ? string.Empty : NormalizeFirmGroupCode(community.FirmGroupCode);
            }
            if (!string.Equals(communityCode, code, StringComparison.Ordinal))
                throw new InvalidOperationException("Firm group code does not match this account. Signing always requires the correct firm group code.");
        }
        var success = passwordOrProviderToken is not null && DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(passwordOrProviderToken, user.PasswordSaltAndHash);
        if (!success) return null;
        user.LastLoginAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<WorkspaceUserAccount?> GetWorkspaceAccountAsync(Guid accountId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == accountId, ct);
    }

    public async Task<IReadOnlyList<Community>> GetAllowedFirmCommunitiesAsync(Guid firmId, IReadOnlyList<Guid> allowedCommunityIds, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var query = db.Communities.AsNoTracking().Where(c => c.ManagementFirmId == firmId);
        if (allowedCommunityIds.Count > 0)
            query = query.Where(c => allowedCommunityIds.Contains(c.Id));
        return await query.OrderBy(c => c.Name).ToListAsync(ct);
    }

    public static IReadOnlyList<Guid> ParseAllowedCommunityIds(WorkspaceUserAccount account)
    {
        if (account is null) return [];
        if (!string.IsNullOrWhiteSpace(account.AllowedCommunityIdsCsv))
        {
            var parsed = account.AllowedCommunityIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => Guid.TryParse(s, out var g) ? g : (Guid?)null)
                .Where(g => g.HasValue)
                .Select(g => g!.Value)
                .Distinct()
                .ToList();
            if (parsed.Count > 0) return parsed;
        }
        return account.CommunityId.HasValue && account.CommunityId.Value != Guid.Empty ? [account.CommunityId.Value] : [];
    }

    public async Task<FirmManagerGroup> CreateFirmManagerGroupAsync(Guid firmId, string name, string topic, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        if (actorRole != WorkspaceSecurityRole.FirmManager && actorRole != WorkspaceSecurityRole.PropertyManager)
            throw new UnauthorizedAccessException("Only managers may create firm chat groups.");
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.Id == firmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{name} {topic}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Group creation blocked by security guard.");
        var group = new FirmManagerGroup
        {
            ManagementFirmId = firm.Id,
            Code = $"FIRM-{name.ToUpperInvariant().Replace(' ', '-')}-{DateTime.UtcNow:yyyyMMddHHmmss}",
            Name = name.Trim(),
            Topic = topic.Trim()
        };
        if (group.Code.Length > 32) group.Code = group.Code[..32];
        db.FirmManagerGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<IReadOnlyList<FirmManagerGroup>> GetFirmManagerGroupsAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.FirmManagerGroups.AsNoTracking().Where(g => g.ManagementFirmId == firmId).OrderBy(g => g.Name).ToListAsync(ct);
    }

    public async Task<FirmManagerMessage> PostFirmManagerMessageAsync(
        Guid firmId,
        Guid groupId,
        string senderName,
        WorkspaceSecurityRole senderRole,
        string senderEmail,
        string senderPhone,
        string propertyScopeName,
        string subject,
        string body,
        bool isPriority,
        CancellationToken ct = default)
    {
        if (senderRole != WorkspaceSecurityRole.FirmManager && senderRole != WorkspaceSecurityRole.PropertyManager)
            throw new UnauthorizedAccessException("Only the Firm Manager and Property Managers may post in the firm manager channel.");
        await using var db = await CreateDbAsync(ct);
        var group = await db.FirmManagerGroups.FirstOrDefaultAsync(g => g.Id == groupId && g.ManagementFirmId == firmId, ct)
            ?? throw new InvalidOperationException("Firm manager group not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{subject} {body}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Message blocked by security guard.");
        var sender = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email.ToLower() == senderEmail.Trim().ToLower(), ct);
        var msg = new FirmManagerMessage
        {
            ManagementFirmId = firmId,
            FirmManagerGroupId = group.Id,
            GroupName = group.Name,
            SenderUserId = sender?.Id,
            SenderName = senderName.Trim(),
            SenderRole = senderRole,
            SenderEmail = senderEmail.Trim(),
            SenderPhone = (senderPhone ?? string.Empty).Trim(),
            PropertyScopeName = string.IsNullOrWhiteSpace(propertyScopeName) ? "All Firm Properties" : propertyScopeName.Trim(),
            Subject = subject.Trim(),
            Body = body.Trim(),
            IsPriorityNotice = isPriority
        };
        db.FirmManagerMessages.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<IReadOnlyList<FirmManagerMessage>> GetFirmManagerMessagesAsync(Guid firmId, Guid? groupId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var query = db.FirmManagerMessages.AsNoTracking().Where(m => m.ManagementFirmId == firmId);
        if (groupId.HasValue) query = query.Where(m => m.FirmManagerGroupId == groupId.Value);
        return await query.OrderByDescending(m => m.SentAtUtc).Take(100).ToListAsync(ct);
    }

    public async Task<ResidentDirectMessage> PostResidentDirectMessageAsync(
        Guid communityId,
        string senderName,
        string senderEmail,
        WorkspaceSecurityRole senderRole,
        string senderUnitNumber,
        string recipientScope,
        string recipientName,
        string subject,
        string body,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new InvalidOperationException("Property not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{subject} {body}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Message blocked by security guard.");
        var threadKey = $"DM-{NormalizeFirmGroupCode(community.FirmGroupCode)}-{communityId:N}-{Guid.NewGuid():N}";
        var msg = new ResidentDirectMessage
        {
            CommunityId = community.Id,
            ManagementFirmId = community.ManagementFirmId,
            ThreadKey = threadKey,
            SenderName = senderName.Trim(),
            SenderEmail = senderEmail.Trim(),
            SenderRole = senderRole,
            SenderUnitNumber = (senderUnitNumber ?? string.Empty).Trim(),
            RecipientScope = recipientScope.Trim(),
            RecipientName = recipientName.Trim(),
            Subject = subject.Trim(),
            Body = body.Trim()
        };
        db.ResidentDirectMessages.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    public async Task<IReadOnlyList<ResidentDirectMessage>> GetResidentDirectMessagesAsync(Guid communityId, string? participantEmail, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var query = db.ResidentDirectMessages.AsNoTracking().Where(m => m.CommunityId == communityId);
        if (!string.IsNullOrWhiteSpace(participantEmail))
        {
            var lowered = participantEmail.Trim().ToLowerInvariant();
            query = query.Where(m => m.SenderEmail.ToLower() == lowered || m.RecipientName.ToLower().Contains(lowered));
        }
        return await query.OrderByDescending(m => m.SentAtUtc).Take(100).ToListAsync(ct);
    }

    public async Task<WorkspaceUserAccount> UpdateResidentProfileAsync(Guid userId, string fullName, string phone, string? linkedUnitNumber, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var user = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException("Account not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{fullName} {phone}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Profile update blocked by security guard.");
        if (!string.IsNullOrWhiteSpace(fullName)) user.FullName = fullName.Trim();
        if (!string.IsNullOrWhiteSpace(phone)) user.Phone = phone.Trim();
        if (user.Role == WorkspaceSecurityRole.Resident && linkedUnitNumber is not null)
            user.LinkedUnitNumber = linkedUnitNumber.Trim();
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task<WorkspaceAccountMembership> RequestJoinFirmWithCodeAsync(
        string firmGroupCode,
        string fullName,
        string email,
        string phone,
        string password,
        IdentityProvider provider,
        string? unitNumber = null,
        string? contractorCompanyOrTrade = null,
        IReadOnlyList<Guid>? targetCommunityIds = null,
        WorkspaceSecurityRole role = WorkspaceSecurityRole.Resident,
        CancellationToken ct = default)
    {
        if (provider != IdentityProvider.EmailPassword)
            throw new InvalidOperationException("Social sign-in is not configured. Use email and password.");
        var code = NormalizeFirmGroupCode(firmGroupCode);
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Firm group code is required.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{fullName} {email} {phone} {contractorCompanyOrTrade}");
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Registration blocked by security guard.");
        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.Include(f => f.Communities).FirstOrDefaultAsync(f => f.FirmGroupCode.ToUpper() == code, ct)
            ?? throw new InvalidOperationException("Firm group code is invalid.");
        var normalized = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Email is required.");
        var existing = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email.ToLower() == normalized, ct);
        WorkspaceUserAccount account;
        if (existing is not null)
        {
            var verified = DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(password ?? string.Empty, existing.PasswordSaltAndHash);
            if (!verified)
                throw new InvalidOperationException("Password does not match the existing account.");
            account = existing;
        }
        else
        {
            var firstTarget = targetCommunityIds is not null && targetCommunityIds.Count > 0
                ? targetCommunityIds[0]
                : (Guid?)null;
            if (firstTarget == Guid.Empty)
                firstTarget = null;
            var fallback = firm.Communities.OrderBy(c => c.Name).FirstOrDefault()
                ?? await db.Communities.Where(c => c.ManagementFirmId == firm.Id).OrderBy(c => c.Name).FirstOrDefaultAsync(ct);
            account = new WorkspaceUserAccount
            {
                CommunityId = firstTarget ?? fallback?.Id,
                ManagementFirmId = firm.Id,
                FirmGroupCode = firm.FirmGroupCode,
                FullName = (fullName ?? string.Empty).Trim(),
                Email = (email ?? string.Empty).Trim(),
                Phone = (phone ?? string.Empty).Trim(),
                Provider = provider,
                ProviderSubjectId = string.Concat(provider.ToString().ToLowerInvariant(), "-", Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture)),
                PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(password ?? string.Empty),
                Role = role,
                AccountKind = WorkspaceAccountKind.RegularAccount,
                EmailConfirmed = true,
                AcceptedAtUtc = DateTime.UtcNow,
                GrantedByPropertyManager = false
            };
            db.UserAccounts.Add(account);
        }
        var allowedCsv = targetCommunityIds is not null && targetCommunityIds.Count > 0
            ? string.Join(",", targetCommunityIds)
            : string.Empty;
        var membership = new WorkspaceAccountMembership
        {
            UserAccountId = account.Id,
            ManagementFirmId = firm.Id,
            Role = role,
            ApprovalStatus = WorkspaceMembershipStatus.PendingApproval,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(role),
            AllowedCommunityIdsCsv = allowedCsv,
            LinkedUnitNumbersCsv = (unitNumber ?? string.Empty).Trim(),
            ContractorCompanyOrTrade = contractorCompanyOrTrade?.Trim()
        };
        db.AccountMemberships.Add(membership);
        await db.SaveChangesAsync(ct);
        return membership;
    }

    public async Task<WorkspaceAccountMembership> ApproveOrUpdateMembershipAsync(
        Guid membershipId,
        bool approve,
        WorkspacePermissionFlags grantedPermissions,
        IReadOnlyList<Guid>? allowedCommunityIds,
        string? linkedUnitNumbersCsv,
        string? contractorCompanyOrTrade,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (actorRole != WorkspaceSecurityRole.FirmManager && actorRole != WorkspaceSecurityRole.PropertyManager)
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may approve memberships.");
        await using var db = await CreateDbAsync(ct);
        var membership = await db.AccountMemberships.FirstOrDefaultAsync(m => m.Id == membershipId, ct)
            ?? throw new InvalidOperationException("Membership not found.");
        if (actorRole == WorkspaceSecurityRole.PropertyManager
            && (membership.Role == WorkspaceSecurityRole.PropertyManager || membership.Role == WorkspaceSecurityRole.FirmManager))
            throw new UnauthorizedAccessException("Property Managers may only approve Resident and Contractor memberships.");
        membership.ApprovalStatus = approve ? WorkspaceMembershipStatus.Approved : WorkspaceMembershipStatus.Rejected;
        if (grantedPermissions != (WorkspacePermissionFlags)0)
            membership.GrantedPermissions = grantedPermissions;
        if (allowedCommunityIds is not null)
            membership.AllowedCommunityIdsCsv = string.Join(",", allowedCommunityIds);
        if (linkedUnitNumbersCsv is not null)
            membership.LinkedUnitNumbersCsv = linkedUnitNumbersCsv;
        if (contractorCompanyOrTrade is not null)
            membership.ContractorCompanyOrTrade = contractorCompanyOrTrade;
        membership.DecidedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return membership;
    }

    public async Task<IReadOnlyList<WorkspaceAccountMembership>> GetFirmMembershipQueueAsync(Guid firmId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.AccountMemberships.AsNoTracking()
            .Where(m => m.ManagementFirmId == firmId && m.ApprovalStatus == WorkspaceMembershipStatus.PendingApproval)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<WorkspaceAccountMembership>> GetUserMembershipsAsync(Guid userAccountId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.AccountMemberships.AsNoTracking()
            .Where(m => m.UserAccountId == userAccountId)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(ct);
    }
}
