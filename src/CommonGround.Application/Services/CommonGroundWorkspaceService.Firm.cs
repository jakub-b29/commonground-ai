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

        

        var manager = new WorkspaceUserAccount
        {
            CommunityId = System.Guid.Empty,
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            FullName = managerFullName.Trim(),
            Email = managerEmail.Trim(),
            Phone = managerPhone.Trim(),
            Provider = provider,
            ProviderSubjectId = $"{provider.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
            PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(password),
            Role = WorkspaceSecurityRole.FirmManager,
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
        var community = new Community
        {
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            Name = propertyName.Trim(),
            StreetAddress = streetAddress.Trim(),
            City = city.Trim(),
            OperatingMode = operatingMode,
            TaxIdOrEin = taxIdOrEin.Trim()
        };
        db.Communities.Add(community);
        await db.SaveChangesAsync(ct);
        return community;
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
            TargetCommunityId = target?.Id,
            TargetCommunityName = target?.Name ?? string.Empty,
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

        var isContractor = invitation.TargetRole == WorkspaceSecurityRole.Contractor;
        var isManager = invitation.TargetRole == WorkspaceSecurityRole.PropertyManager;
        var account = new WorkspaceUserAccount
        {
            CommunityId = target.Id,
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            FullName = string.IsNullOrWhiteSpace(fullName) ? invitation.RecipientName : fullName.Trim(),
            Email = string.IsNullOrWhiteSpace(email) ? invitation.RecipientEmail : email.Trim(),
            Phone = string.IsNullOrWhiteSpace(phone) ? invitation.RecipientPhone : phone.Trim(),
            Provider = provider,
            ProviderSubjectId = $"{provider.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
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
            GrantedByPropertyManager = true
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

        invitation.Status = WorkspaceInvitationStatus.Accepted;
        invitation.AcceptedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return account;
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
            var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == user.CommunityId, ct);
            var communityCode = community is null ? string.Empty : NormalizeFirmGroupCode(community.FirmGroupCode);
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
        return account.CommunityId == Guid.Empty ? [] : [account.CommunityId];
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
}
