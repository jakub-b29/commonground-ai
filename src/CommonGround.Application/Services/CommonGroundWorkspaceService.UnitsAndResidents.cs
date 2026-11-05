using System.Security.Cryptography;
using System.Text;
using CommonGround.Application.Persistence;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public enum WizardResidentAssignmentOption
{
    None = 0,
    AssignExistingResident = 1,
    CreateAndAssignNewResident = 2
}

public sealed record UnifiedCommunityProfileInput(
    Guid CommunityId,
    string Name,
    CommunityStatus Status,
    string PropertyTypeConfiguration,
    string StreetAddress,
    string City,
    string PostalOrZipCode,
    string CountryCode,
    string CurrencyCode,
    string TaxIdOrEin,
    string RegistryNumber,
    string LocalMunicipalityOrCounty,
    string LocalTaxOffice,
    string CompetentCourtOrRecorder,
    string AdministrativeEmail,
    string AdministrativePhone,
    SystemOperatingMode OperatingMode,
    bool EnableHoaGovernance,
    bool EnableShareWeightedVoting,
    bool EnableArcRequests,
    bool EnableSubMeterReconciliation,
    bool EnableReserveStudy,
    RegistrationApprovalMode DefaultRegistrationApprovalMode,
    decimal OperatingRatePerM2,
    decimal RepairFundRatePerM2,
    decimal WasteFeePerOccupant,
    decimal ColdWaterTariffPerM3,
    decimal HotWaterHeatingCostPerM3,
    decimal DistrictHeatingTariffPerGj,
    decimal CommonElectricityTariffPerKwh);

public sealed record CommunityContactInput(
    Guid? Id,
    CommunityContactCategory Category,
    string ContactName,
    string RoleTitle,
    string Phone,
    string Email,
    string OfficeHours,
    string AddressOrLocation,
    string Notes,
    int SortOrder,
    bool IsVisibleToResidents);

public sealed record UnitCreationWizardRequest(
    Guid CommunityId,
    Guid BuildingId,
    string? NewBuildingName,
    string UnitNumber,
    int FloorNumber,
    int StairwellNumber,
    string VerticalStackCode,
    UnitKind Kind,
    decimal FloorAreaM2,
    decimal StorageAreaM2,
    int RoomsCount,
    int BathroomsCount,
    int OccupancyCapacity,
    UnitOccupancyStatus OccupancyStatus,
    int ShareNumerator,
    int ShareDenominator,
    string? ParkingBayNumber,
    string? AccessFobCode,
    string? VirtualIbanOrLockboxCode,
    string Notes,
    string OwnerFullName,
    string OwnerEmail,
    string OwnerPhone,
    UnitOwnershipRole OwnershipRole,
    decimal OwnershipPercentage,
    string? CoOwnerName,
    string? VotingProxyName,
    WizardResidentAssignmentOption ResidentAssignmentOption,
    Guid? ExistingResidentProfileId,
    string NewResidentFirstName,
    string NewResidentLastName,
    string NewResidentEmail,
    string NewResidentPhone,
    string NewResidentPreferredLanguage,
    ResidentAssignmentType AssignmentType,
    DateTime MoveInDateUtc,
    bool CreateRegistrationLinkAfterUnitCreation,
    RegistrationApprovalMode RegistrationApprovalMode);

public sealed record UnitCreationWizardResult(
    Unit Unit,
    Building Building,
    UnitOwnership? Ownership,
    ResidentProfile? Resident,
    ResidentUnitAssignment? Assignment,
    string? GeneratedRegistrationTokenPlaintext);

public sealed record UnitUpdateInput(
    Guid BuildingId,
    string UnitNumber,
    int FloorNumber,
    int StairwellNumber,
    string VerticalStackCode,
    UnitKind Kind,
    decimal FloorAreaM2,
    decimal StorageAreaM2,
    int RoomsCount,
    int BathroomsCount,
    int OccupancyCapacity,
    UnitOccupancyStatus OccupancyStatus,
    int ShareNumerator,
    int ShareDenominator,
    string PrimaryOwnerName,
    string PrimaryOwnerEmail,
    string PrimaryOwnerPhone,
    string PreferredLanguage,
    string? CoOwnerName,
    string? VotingProxyName,
    string? ParkingBayNumber,
    string? AccessFobCode,
    string VirtualIbanOrLockboxCode,
    string Notes);

public sealed record UnitSummaryRow(
    Guid UnitId,
    Guid CommunityId,
    string CommunityName,
    string CommunityAccountNumber,
    Guid BuildingId,
    string BuildingName,
    string UnitNumber,
    int FloorNumber,
    int StairwellNumber,
    string VerticalStackCode,
    UnitKind Kind,
    decimal FloorAreaM2,
    decimal StorageAreaM2,
    int RoomsCount,
    int BathroomsCount,
    int OccupancyCapacity,
    int ShareNumerator,
    int ShareDenominator,
    decimal OwnershipShare,
    UnitOccupancyStatus OccupancyStatus,
    string PrimaryOwnerName,
    string PrimaryOwnerEmail,
    IReadOnlyList<string> ActiveResidentNames,
    IReadOnlyList<string> ActiveResidentAccountNumbers,
    Guid? PrimaryResidentProfileId,
    decimal CurrentBalance,
    int DaysPastDue,
    int ActiveRegistrationLinksCount);

public sealed record GlobalUnitsDashboardSnapshot(
    IReadOnlyList<Community> Communities,
    IReadOnlyList<Building> Buildings,
    IReadOnlyList<UnitSummaryRow> Units,
    int TotalUnits,
    int OccupiedUnits,
    int VacantUnits,
    int PendingRegistrationUnits,
    int TotalActiveResidents,
    int ArrearsUnitsCount);

public sealed record UnitFullDossier(
    Community Community,
    Building Building,
    Unit Unit,
    IReadOnlyList<UnitOwnership> Ownerships,
    IReadOnlyList<ResidentUnitAssignment> ActiveAssignments,
    IReadOnlyList<ResidentUnitAssignment> HistoricalAssignments,
    IReadOnlyList<ResidentHouseholdMember> HouseholdMembers,
    IReadOnlyList<UtilityMeter> Meters,
    IReadOnlyList<MonthlyAssessment> Assessments,
    IReadOnlyList<ResidentDocument> Documents,
    IReadOnlyList<ResidentRegistrationToken> RegistrationTokens,
    IReadOnlyList<ResidentRegistrationRequest> RegistrationRequests,
    IReadOnlyList<SecurityAuditEvent> AuditEvents);

public sealed record ResidentMatchCandidate(
    ResidentProfile Profile,
    string MatchReason,
    int ConfidenceScore,
    IReadOnlyList<string> ActiveUnitLabels);

public sealed record ResidentProfileUpdateInput(
    string FirstName,
    string LastName,
    string PrimaryEmail,
    string Phone,
    DateTime? DateOfBirth,
    string? ProfilePhotoUrl,
    string AdditionalContactDetails,
    ResidentAccountStatus AccountStatus,
    ResidentVerificationStatus VerificationStatus,
    string PreferredLanguage,
    string PreferredContactMethod,
    string EmergencyContactName,
    string EmergencyContactPhone,
    string EmergencyContactRelation,
    string Notes);

public sealed record ResidentAssignmentViewItem(
    ResidentUnitAssignment Assignment,
    string CommunityName,
    string CommunityAccountNumber,
    string BuildingName,
    string UnitNumber,
    decimal FloorAreaM2);

public sealed record ResidentOwnershipViewItem(
    UnitOwnership Ownership,
    string CommunityName,
    string CommunityAccountNumber,
    string BuildingName,
    string UnitNumber);

public sealed record ResidentFullDossier(
    ResidentProfile Profile,
    IReadOnlyList<ResidentAssignmentViewItem> ActiveAssignments,
    IReadOnlyList<ResidentAssignmentViewItem> PastAssignments,
    IReadOnlyList<ResidentOwnershipViewItem> Ownerships,
    IReadOnlyList<ResidentHouseholdMember> HouseholdMembers,
    IReadOnlyList<ResidentDocument> Documents,
    IReadOnlyList<ResidentDirectMessage> DirectMessages,
    IReadOnlyList<SecurityAuditEvent> AuditEvents,
    IReadOnlyList<ResidentRegistrationToken> RegistrationTokens);

public sealed record ResidentDirectoryRow(
    ResidentProfile Profile,
    IReadOnlyList<ResidentAssignmentViewItem> ActiveAssignments,
    int PastAssignmentsCount,
    int OwnedUnitsCount,
    int DocumentsCount);

public sealed record UnitOccupancyConflictReport(
    bool HasConflict,
    bool RequiresOverrideReason,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ResidentUnitAssignment> ActiveAssignments);

public sealed record ManualResidentAssignmentRequest(
    Guid CommunityId,
    Guid BuildingId,
    Guid UnitId,
    bool CreateNewResident,
    Guid? ExistingResidentProfileId,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string PreferredLanguage,
    string EmergencyContactName,
    string EmergencyContactPhone,
    ResidentAssignmentType AssignmentType,
    UnitAssignmentStatus Status,
    bool IsPrimaryResident,
    DateTime MoveInDateUtc,
    DateTime? MoveOutDateUtc,
    string AssignmentReasonOrNotes,
    string? ConflictOverrideReason);

public sealed record RegistrationLinkGenerationResult(
    ResidentRegistrationToken TokenRecord,
    string PlaintextToken,
    string RelativeUrl);

/// <summary>
/// Strictly minimal public validation DTO for Zero-Disclosure registration links (Section 12.1).
/// Never includes Community, Building, Unit, Owner, Resident, or Financial fields.
/// </summary>
public sealed record PublicRegistrationTokenValidation(
    bool IsValid,
    string StatusMessage,
    RegistrationApprovalMode ApprovalMode);

public sealed record PublicResidentRegistrationInput(
    string PlaintextToken,
    string FirstName,
    string LastName,
    string Email,
    string Phone,
    string Password,
    string PreferredLanguage,
    string EmergencyContactName,
    string EmergencyContactPhone,
    string ResidentNotes);

public sealed record PublicResidentRegistrationResult(
    bool Succeeded,
    bool IsAutoApproved,
    string ResidentAccountNumber,
    string StatusMessage);

public sealed record RegistrationRequestQueueItem(
    ResidentRegistrationRequest Request,
    string CommunityName,
    string CommunityAccountNumber,
    string BuildingName,
    string UnitNumber,
    string TokenReference,
    IReadOnlyList<ResidentMatchCandidate> PotentialMatches,
    UnitOccupancyConflictReport ConflictReport);

public sealed partial class CommonGroundWorkspaceService
{
    private static string HashRegistrationToken(string plaintextToken)
    {
        var bytes = Encoding.UTF8.GetBytes((plaintextToken ?? string.Empty).Trim());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string NewRegistrationPlaintextToken()
    {
        // URL-safe Base64Url plaintext token (no '+', '/', '=') for use in /register-resident/{Token} route.
        var bytes = RandomNumberGenerator.GetBytes(32);
        var base64Url = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "REG-" + base64Url;
    }

    private static (string FirstName, string LastName, string FullName) NormalizeNameParts(string? firstName, string? lastName, string? fallbackFullName = null)
    {
        var f = (firstName ?? string.Empty).Trim();
        var l = (lastName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(f) && string.IsNullOrWhiteSpace(l) && !string.IsNullOrWhiteSpace(fallbackFullName))
        {
            var split = fallbackFullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            f = split.Length > 0 ? split[0] : fallbackFullName.Trim();
            l = split.Length > 1 ? split[1] : string.Empty;
        }
        var full = $"{f} {l}".Trim();
        if (string.IsNullOrWhiteSpace(full)) full = "Resident";
        if (string.IsNullOrWhiteSpace(f)) f = full;
        return (f, l, full);
    }

    private static string NormalizePhoneForMatch(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;
        return new string(phone.Where(char.IsAsciiDigit).ToArray());
    }

    private static async Task AppendChainedAuditByCommunityIdAsync(
        IAppDbContext db,
        Guid communityId,
        WorkspaceSecurityRole actorRole,
        string category,
        string severity,
        string resource,
        string summary,
        CancellationToken ct)
    {
        if (communityId == Guid.Empty) return;
        var hashes = await db.SecurityAuditEvents.AsNoTracking()
            .Where(e => e.CommunityId == communityId)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => e.EntrySha256Hash)
            .ToListAsync(ct);
        var previous = hashes.Count == 0 ? "GENESIS-0000000000000000" : hashes[^1];
        var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(
            communityId, actorRole, category, severity, resource, summary, previous);
        db.SecurityAuditEvents.Add(ev);
    }

    public async Task<string> EnsureCommunityAccountNumberAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new InvalidOperationException("Community not found.");
        if (AccountNumberGenerator.IsValidCommunityAccountNumber(community.CommunityAccountNumber))
        {
            return community.CommunityAccountNumber;
        }
        var allNumbers = await db.Communities.AsNoTracking()
            .Select(c => c.CommunityAccountNumber)
            .ToListAsync(ct);
        community.CommunityAccountNumber = AccountNumberGenerator.NextCommunityAccountNumber(allNumbers);
        await db.SaveChangesAsync(ct);
        return community.CommunityAccountNumber;
    }

    public async Task<Community> UpdateUnifiedCommunityProfileAsync(
        UnifiedCommunityProfileInput input,
        WorkspaceSecurityRole actorRole,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may update community settings.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{input.Name} {input.StreetAddress} {input.City} {input.PropertyTypeConfiguration} {input.AdministrativeEmail}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Community profile update blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == input.CommunityId, ct)
            ?? throw new InvalidOperationException("Community not found.");

        if (!AccountNumberGenerator.IsValidCommunityAccountNumber(community.CommunityAccountNumber))
        {
            var allNumbers = await db.Communities.AsNoTracking().Select(c => c.CommunityAccountNumber).ToListAsync(ct);
            community.CommunityAccountNumber = AccountNumberGenerator.NextCommunityAccountNumber(allNumbers);
        }

        if (!string.IsNullOrWhiteSpace(input.Name)) community.Name = input.Name.Trim();
        community.Status = input.Status;
        if (!string.IsNullOrWhiteSpace(input.PropertyTypeConfiguration))
            community.PropertyTypeConfiguration = input.PropertyTypeConfiguration.Trim();
        if (!string.IsNullOrWhiteSpace(input.StreetAddress)) community.StreetAddress = input.StreetAddress.Trim();
        if (!string.IsNullOrWhiteSpace(input.City)) community.City = input.City.Trim();
        community.PostalOrZipCode = (input.PostalOrZipCode ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(input.CountryCode)) community.CountryCode = input.CountryCode.Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(input.CurrencyCode)) community.CurrencyCode = input.CurrencyCode.Trim().ToUpperInvariant();
        community.TaxIdOrEin = (input.TaxIdOrEin ?? string.Empty).Trim();
        community.RegistryNumber = (input.RegistryNumber ?? string.Empty).Trim();
        community.LocalMunicipalityOrCounty = (input.LocalMunicipalityOrCounty ?? string.Empty).Trim();
        community.LocalTaxOffice = (input.LocalTaxOffice ?? string.Empty).Trim();
        community.CompetentCourtOrRecorder = (input.CompetentCourtOrRecorder ?? string.Empty).Trim();
        community.AdministrativeEmail = (input.AdministrativeEmail ?? string.Empty).Trim();
        community.AdministrativePhone = (input.AdministrativePhone ?? string.Empty).Trim();
        community.OperatingMode = input.OperatingMode;
        community.EnableHoaGovernance = input.EnableHoaGovernance;
        community.EnableShareWeightedVoting = input.EnableShareWeightedVoting;
        community.EnableArcRequests = input.EnableArcRequests;
        community.EnableSubMeterReconciliation = input.EnableSubMeterReconciliation;
        community.EnableReserveStudy = input.EnableReserveStudy;
        community.DefaultRegistrationApprovalMode = input.DefaultRegistrationApprovalMode;

        community.OperatingRatePerM2 = input.OperatingRatePerM2;
        community.RepairFundRatePerM2 = input.RepairFundRatePerM2;
        community.WasteFeePerOccupant = input.WasteFeePerOccupant;
        community.ColdWaterTariffPerM3 = input.ColdWaterTariffPerM3;
        community.HotWaterHeatingCostPerM3 = input.HotWaterHeatingCostPerM3;
        community.DistrictHeatingTariffPerGj = input.DistrictHeatingTariffPerGj;
        community.CommonElectricityTariffPerKwh = input.CommonElectricityTariffPerKwh;

        await AppendChainedAuditByCommunityIdAsync(
            db,
            community.Id,
            actorRole,
            "COMMUNITY_PROFILE_UPDATED",
            "Info",
            $"Community: {community.CommunityAccountNumber}",
            $"Unified community profile updated for {community.Name} ({community.CommunityAccountNumber}).",
            ct);

        await db.SaveChangesAsync(ct);
        return community;
    }

    public async Task<IReadOnlyList<CommunityContact>> GetCommunityContactsAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var contacts = await db.CommunityContacts.AsNoTracking()
            .Where(c => c.CommunityId == communityId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Category)
            .ToListAsync(ct);
        if (contacts.Count > 0) return contacts;

        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == communityId, ct);
        if (community is null) return [];

        var defaults = new List<CommunityContact>
        {
            new()
            {
                CommunityId = community.Id,
                Category = CommunityContactCategory.ManagementOffice,
                ContactName = $"{community.Name} Management Office",
                RoleTitle = "Community Administration",
                Phone = string.IsNullOrWhiteSpace(community.AdministrativePhone) ? "+1 (800) 555-0199" : community.AdministrativePhone,
                Email = string.IsNullOrWhiteSpace(community.AdministrativeEmail) ? "office@commonground.local" : community.AdministrativeEmail,
                OfficeHours = "Mon-Fri 08:00-17:00",
                AddressOrLocation = $"{community.StreetAddress}, {community.City}".Trim(' ', ','),
                SortOrder = 0,
                IsVisibleToResidents = true
            },
            new()
            {
                CommunityId = community.Id,
                Category = CommunityContactCategory.PropertyManager,
                ContactName = string.IsNullOrWhiteSpace(community.AssignedPropertyManagerName) ? "Assigned Property Manager" : community.AssignedPropertyManagerName,
                RoleTitle = "Lead Property Manager",
                Phone = string.IsNullOrWhiteSpace(community.AssignedPropertyManagerPhone) ? "+1 (800) 555-0199" : community.AssignedPropertyManagerPhone,
                Email = string.IsNullOrWhiteSpace(community.AssignedPropertyManagerEmail) ? "manager@commonground.local" : community.AssignedPropertyManagerEmail,
                OfficeHours = "Mon-Fri 08:30-16:30",
                AddressOrLocation = "Property Management Desk",
                SortOrder = 1,
                IsVisibleToResidents = true
            },
            new()
            {
                CommunityId = community.Id,
                Category = CommunityContactCategory.BoardAssociation,
                ContactName = "Community Board & Association Council",
                RoleTitle = "Governance & Board Office",
                Phone = string.IsNullOrWhiteSpace(community.AdministrativePhone) ? "+1 (800) 555-0199" : community.AdministrativePhone,
                Email = "board@commonground.local",
                OfficeHours = "Monthly Board Sessions & Portal",
                AddressOrLocation = community.StreetAddress,
                SortOrder = 2,
                IsVisibleToResidents = true
            },
            new()
            {
                CommunityId = community.Id,
                Category = CommunityContactCategory.Emergency,
                ContactName = "24/7 Emergency & Technical Dispatch",
                RoleTitle = "Emergency Dispatch",
                Phone = "112 / +1 (800) 555-0911",
                Email = "emergency@commonground.local",
                OfficeHours = "24/7 Continuous",
                AddressOrLocation = "Emergency Technical Desk",
                SortOrder = 3,
                IsVisibleToResidents = true
            }
        };
        foreach (var item in defaults)
        {
            db.CommunityContacts.Add(item);
        }
        await db.SaveChangesAsync(ct);
        return defaults;
    }

    public async Task<CommunityContact> UpsertCommunityContactAsync(
        Guid communityId,
        CommunityContactInput input,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may edit community contacts.");
        if (string.IsNullOrWhiteSpace(input.ContactName))
            throw new InvalidOperationException("Contact name is required.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{input.ContactName} {input.RoleTitle} {input.Phone} {input.Email} {input.Notes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Community contact update blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        CommunityContact contact;
        if (input.Id.HasValue && input.Id.Value != Guid.Empty)
        {
            contact = await db.CommunityContacts.FirstOrDefaultAsync(c => c.Id == input.Id.Value && c.CommunityId == communityId, ct)
                ?? throw new InvalidOperationException("Community contact not found.");
        }
        else
        {
            contact = new CommunityContact { CommunityId = communityId };
            db.CommunityContacts.Add(contact);
        }

        contact.Category = input.Category;
        contact.ContactName = input.ContactName.Trim();
        contact.RoleTitle = (input.RoleTitle ?? string.Empty).Trim();
        contact.Phone = (input.Phone ?? string.Empty).Trim();
        contact.Email = (input.Email ?? string.Empty).Trim();
        contact.OfficeHours = string.IsNullOrWhiteSpace(input.OfficeHours) ? "Mon-Fri 08:00-17:00" : input.OfficeHours.Trim();
        contact.AddressOrLocation = (input.AddressOrLocation ?? string.Empty).Trim();
        contact.Notes = (input.Notes ?? string.Empty).Trim();
        contact.SortOrder = input.SortOrder;
        contact.IsVisibleToResidents = input.IsVisibleToResidents;

        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "COMMUNITY_CONTACT_UPSERTED",
            "Info",
            $"CommunityContact: {contact.ContactName}",
            $"Community contact '{contact.ContactName}' ({contact.Category}) saved.",
            ct);

        await db.SaveChangesAsync(ct);
        return contact;
    }

    public async Task DeleteCommunityContactAsync(
        Guid communityId,
        Guid contactId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may delete community contacts.");

        await using var db = await CreateDbAsync(ct);
        var contact = await db.CommunityContacts.FirstOrDefaultAsync(c => c.Id == contactId && c.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Community contact not found.");
        var name = contact.ContactName;
        db.CommunityContacts.Remove(contact);
        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "COMMUNITY_CONTACT_DELETED",
            "Info",
            $"CommunityContact: {name}",
            $"Community contact '{name}' removed.",
            ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Building> CreateBuildingAsync(
        Guid communityId,
        string name,
        string streetAddress,
        int floorsCount,
        int stairwellsCount,
        int constructionYear,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may add buildings.");
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Building name is required.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{name} {streetAddress}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Building creation blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new InvalidOperationException("Community not found.");

        var building = new Building
        {
            CommunityId = community.Id,
            Name = name.Trim(),
            StreetAddress = string.IsNullOrWhiteSpace(streetAddress) ? community.StreetAddress : streetAddress.Trim(),
            FloorsCount = Math.Clamp(floorsCount, 1, 50),
            StairwellsCount = Math.Clamp(stairwellsCount, 1, 15),
            ConstructionYear = Math.Clamp(constructionYear, 1850, 2035)
        };
        db.Buildings.Add(building);
        foreach (var code in new[] { "STACK-A1", "STACK-A2", "STACK-B1" })
        {
            var stack = new VerticalRiserStack
            {
                BuildingId = building.Id,
                Code = code,
                Description = code.Contains('B') ? "Heating riser" : "Water riser",
                ShutoffValveLocation = "Basement manifold"
            };
            building.RiserStacks.Add(stack);
            db.RiserStacks.Add(stack);
        }

        await AppendChainedAuditByCommunityIdAsync(
            db,
            community.Id,
            actorRole,
            "BUILDING_CREATED",
            "Info",
            $"Building: {building.Name}",
            $"Building '{building.Name}' ({building.FloorsCount} floors, {building.StairwellsCount} stairwells) created.",
            ct);

        await db.SaveChangesAsync(ct);
        return building;
    }

    public async Task<UnitCreationWizardResult> CreateUnitFromWizardAsync(
        UnitCreationWizardRequest request,
        WorkspaceSecurityRole actorRole,
        string actorName = "Property Manager",
        Guid? actorMembershipId = null,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may create units.");

        var unitNumber = (request.UnitNumber ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(unitNumber))
            throw new InvalidOperationException("Unit number is required.");
        if (unitNumber.Length > 32)
            throw new InvalidOperationException("Unit number must be 32 characters or fewer.");
        if (request.FloorAreaM2 <= 0m || request.FloorAreaM2 > 5000m)
            throw new InvalidOperationException("Floor area (m2) must be greater than 0 and at most 5000.");
        if (request.StorageAreaM2 < 0m || request.StorageAreaM2 > 1000m)
            throw new InvalidOperationException("Storage area (m2) must be between 0 and 1000.");
        if (request.ShareNumerator < 0 || request.ShareDenominator <= 0)
            throw new InvalidOperationException("Ownership share denominator must be greater than 0.");
        if (request.OccupancyCapacity < 1 || request.OccupancyCapacity > 50)
            throw new InvalidOperationException("Occupancy capacity must be between 1 and 50.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{unitNumber} {request.OwnerFullName} {request.OwnerEmail} {request.NewResidentFirstName} {request.NewResidentLastName} {request.Notes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Unit creation blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities
            .Include(c => c.Buildings).ThenInclude(b => b.RiserStacks)
            .FirstOrDefaultAsync(c => c.Id == request.CommunityId, ct)
            ?? throw new InvalidOperationException("Selected community not found.");

        var duplicateUnit = await db.Units.AsNoTracking()
            .AnyAsync(u => u.CommunityId == community.Id && u.UnitNumber.ToUpper() == unitNumber.ToUpperInvariant(), ct);
        if (duplicateUnit)
            throw new InvalidOperationException($"Unit '{unitNumber}' already exists in {community.Name}.");

        Building? building = null;
        if (!string.IsNullOrWhiteSpace(request.NewBuildingName))
        {
            var bName = request.NewBuildingName.Trim();
            building = community.Buildings.FirstOrDefault(b => string.Equals(b.Name, bName, StringComparison.OrdinalIgnoreCase));
            if (building is null)
            {
                building = new Building
                {
                    CommunityId = community.Id,
                    Name = bName,
                    StreetAddress = community.StreetAddress,
                    FloorsCount = Math.Max(4, request.FloorNumber),
                    StairwellsCount = Math.Max(2, request.StairwellNumber)
                };
                community.Buildings.Add(building);
                db.Buildings.Add(building);
            }
        }
        else
        {
            building = community.Buildings.FirstOrDefault(b => b.Id == request.BuildingId)
                ?? community.Buildings.OrderBy(b => b.Name).FirstOrDefault();
            if (building is null)
            {
                building = new Building
                {
                    CommunityId = community.Id,
                    Name = "Building A",
                    StreetAddress = community.StreetAddress,
                    FloorsCount = Math.Max(4, request.FloorNumber),
                    StairwellsCount = Math.Max(2, request.StairwellNumber)
                };
                community.Buildings.Add(building);
                db.Buildings.Add(building);
            }
        }

        var stackCode = NormalizeStackCode(string.IsNullOrWhiteSpace(request.VerticalStackCode) ? "STACK-A1" : request.VerticalStackCode);
        if (string.IsNullOrWhiteSpace(stackCode)) stackCode = "STACK-A1";
        if (!building.RiserStacks.Any(r => string.Equals(r.Code, stackCode, StringComparison.OrdinalIgnoreCase)))
        {
            var stack = new VerticalRiserStack
            {
                BuildingId = building.Id,
                Code = stackCode,
                Description = stackCode.Contains('B') ? "Heating riser" : "Water riser",
                ShutoffValveLocation = "Basement manifold"
            };
            building.RiserStacks.Add(stack);
            db.RiserStacks.Add(stack);
        }

        var resolvedOccupancyStatus = request.OccupancyStatus;
        if (request.ResidentAssignmentOption == WizardResidentAssignmentOption.None)
        {
            resolvedOccupancyStatus = request.CreateRegistrationLinkAfterUnitCreation
                ? UnitOccupancyStatus.PendingRegistration
                : UnitOccupancyStatus.Vacant;
        }
        else
        {
            resolvedOccupancyStatus = request.AssignmentType == ResidentAssignmentType.Tenant
                ? UnitOccupancyStatus.TenantOccupied
                : UnitOccupancyStatus.OwnerOccupied;
        }

        var unit = new Unit
        {
            CommunityId = community.Id,
            BuildingId = building.Id,
            UnitNumber = unitNumber,
            FloorNumber = Math.Clamp(request.FloorNumber, 0, 100),
            StairwellNumber = Math.Clamp(request.StairwellNumber, 1, 20),
            VerticalStackCode = stackCode,
            Kind = request.Kind,
            FloorAreaM2 = request.FloorAreaM2,
            StorageAreaM2 = request.StorageAreaM2,
            RoomsCount = Math.Clamp(request.RoomsCount, 0, 50),
            BathroomsCount = Math.Clamp(request.BathroomsCount, 0, 20),
            OccupancyCapacity = Math.Clamp(request.OccupancyCapacity, 1, 50),
            OccupancyStatus = resolvedOccupancyStatus,
            ShareNumerator = request.ShareNumerator,
            ShareDenominator = request.ShareDenominator,
            DeclaredOccupants = request.ResidentAssignmentOption == WizardResidentAssignmentOption.None ? 0 : 1,
            PrimaryOwnerName = (request.OwnerFullName ?? string.Empty).Trim(),
            PrimaryOwnerEmail = (request.OwnerEmail ?? string.Empty).Trim(),
            PrimaryOwnerPhone = (request.OwnerPhone ?? string.Empty).Trim(),
            PreferredLanguage = string.IsNullOrWhiteSpace(request.NewResidentPreferredLanguage) ? "EN" : request.NewResidentPreferredLanguage.Trim().ToUpperInvariant(),
            CoOwnerName = string.IsNullOrWhiteSpace(request.CoOwnerName) ? null : request.CoOwnerName.Trim(),
            VotingProxyName = string.IsNullOrWhiteSpace(request.VotingProxyName) ? null : request.VotingProxyName.Trim(),
            ParkingBayNumber = string.IsNullOrWhiteSpace(request.ParkingBayNumber) ? null : request.ParkingBayNumber.Trim(),
            AccessFobCode = string.IsNullOrWhiteSpace(request.AccessFobCode) ? null : request.AccessFobCode.Trim(),
            VirtualIbanOrLockboxCode = string.IsNullOrWhiteSpace(request.VirtualIbanOrLockboxCode)
                ? $"LOCK-{unitNumber.ToUpperInvariant()}"
                : request.VirtualIbanOrLockboxCode.Trim(),
            Notes = (request.Notes ?? string.Empty).Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Units.Add(unit);

        UnitOwnership? primaryOwnership = null;
        if (!string.IsNullOrWhiteSpace(unit.PrimaryOwnerName))
        {
            primaryOwnership = new UnitOwnership
            {
                CommunityId = community.Id,
                BuildingId = building.Id,
                UnitId = unit.Id,
                OwnerFullName = unit.PrimaryOwnerName,
                OwnerEmail = unit.PrimaryOwnerEmail,
                OwnerPhone = unit.PrimaryOwnerPhone,
                OwnershipRole = request.OwnershipRole,
                ShareNumerator = unit.ShareNumerator,
                ShareDenominator = unit.ShareDenominator,
                OwnershipPercentage = request.OwnershipPercentage > 0m ? request.OwnershipPercentage : 100.0m,
                EffectiveStartDateUtc = DateTime.UtcNow,
                IsActive = true,
                Notes = "Created via Unit Creation Wizard"
            };
            db.UnitOwnerships.Add(primaryOwnership);
        }

        ResidentProfile? assignedResident = null;
        ResidentUnitAssignment? createdAssignment = null;

        if (request.ResidentAssignmentOption == WizardResidentAssignmentOption.AssignExistingResident
            && request.ExistingResidentProfileId.HasValue
            && request.ExistingResidentProfileId.Value != Guid.Empty)
        {
            assignedResident = await db.ResidentProfiles.FirstOrDefaultAsync(r => r.Id == request.ExistingResidentProfileId.Value, ct)
                ?? throw new InvalidOperationException("Selected existing resident profile not found.");
        }
        else if (request.ResidentAssignmentOption == WizardResidentAssignmentOption.CreateAndAssignNewResident)
        {
            var (first, last, full) = NormalizeNameParts(request.NewResidentFirstName, request.NewResidentLastName, unit.PrimaryOwnerName);
            var email = string.IsNullOrWhiteSpace(request.NewResidentEmail) ? unit.PrimaryOwnerEmail : request.NewResidentEmail.Trim();
            var phone = string.IsNullOrWhiteSpace(request.NewResidentPhone) ? unit.PrimaryOwnerPhone : request.NewResidentPhone.Trim();

            ResidentProfile? existingMatch = null;
            if (!string.IsNullOrWhiteSpace(email))
            {
                var lowerEmail = email.ToLowerInvariant();
                existingMatch = await db.ResidentProfiles.FirstOrDefaultAsync(
                    r => r.PrimaryEmail.ToLower() == lowerEmail && (r.ManagementFirmId == null || r.ManagementFirmId == community.ManagementFirmId),
                    ct);
            }

            if (existingMatch is not null)
            {
                assignedResident = existingMatch;
            }
            else
            {
                var allResNumbers = await db.ResidentProfiles.AsNoTracking().Select(r => r.ResidentAccountNumber).ToListAsync(ct);
                var nextResNumber = AccountNumberGenerator.NextResidentAccountNumber(allResNumbers);
                assignedResident = new ResidentProfile
                {
                    ResidentAccountNumber = nextResNumber,
                    ManagementFirmId = community.ManagementFirmId,
                    FirstName = first,
                    LastName = last,
                    FullName = full,
                    PrimaryEmail = email,
                    Phone = phone,
                    PreferredLanguage = unit.PreferredLanguage,
                    AccountStatus = ResidentAccountStatus.Active,
                    VerificationStatus = ResidentVerificationStatus.Verified,
                    RegistrationDateUtc = DateTime.UtcNow,
                    CreatedAtUtc = DateTime.UtcNow,
                    Notes = $"Created with Unit {unit.UnitNumber}"
                };
                db.ResidentProfiles.Add(assignedResident);
            }
        }

        if (assignedResident is not null)
        {
            if (primaryOwnership is not null && request.AssignmentType == ResidentAssignmentType.OwnerResident)
            {
                primaryOwnership.ResidentProfileId = assignedResident.Id;
            }
            if (string.IsNullOrWhiteSpace(unit.PrimaryOwnerName))
            {
                unit.PrimaryOwnerName = assignedResident.FullName;
                unit.PrimaryOwnerEmail = assignedResident.PrimaryEmail;
                unit.PrimaryOwnerPhone = assignedResident.Phone;
            }

            createdAssignment = new ResidentUnitAssignment
            {
                ResidentProfileId = assignedResident.Id,
                CommunityId = community.Id,
                BuildingId = building.Id,
                UnitId = unit.Id,
                ManagementFirmId = community.ManagementFirmId,
                AssignmentType = request.AssignmentType,
                Status = UnitAssignmentStatus.Active,
                IsPrimaryResident = true,
                MoveInDateUtc = request.MoveInDateUtc == default ? DateTime.UtcNow : request.MoveInDateUtc,
                ApprovedByMembershipId = actorMembershipId,
                ApprovedByName = actorName,
                AssignedAtUtc = DateTime.UtcNow,
                AssignmentReasonOrNotes = "Assigned during Unit Creation Wizard"
            };
            db.ResidentUnitAssignments.Add(createdAssignment);

            await EnsureResidentPortalLinksInternalAsync(db, assignedResident, community, unit, request.AssignmentType, ct);
        }

        string? generatedPlaintextToken = null;
        if (request.CreateRegistrationLinkAfterUnitCreation && community.ManagementFirmId.HasValue)
        {
            generatedPlaintextToken = NewRegistrationPlaintextToken();
            var tokenRow = new ResidentRegistrationToken
            {
                ManagementFirmId = community.ManagementFirmId.Value,
                CommunityId = community.Id,
                BuildingId = building.Id,
                TargetUnitId = unit.Id,
                TokenHash = HashRegistrationToken(generatedPlaintextToken),
                LabelOrReference = $"Unit {unit.UnitNumber} onboarding link",
                ApprovalMode = request.RegistrationApprovalMode,
                IntendedAssignmentType = request.AssignmentType,
                IntendedRole = WorkspaceSecurityRole.Resident,
                MaxUses = 1,
                ExpiresAtUtc = DateTime.UtcNow.AddDays(14),
                Status = RegistrationTokenStatus.Active,
                CreatedByMembershipId = actorMembershipId,
                CreatedByName = actorName,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ResidentRegistrationTokens.Add(tokenRow);
        }

        await AppendChainedAuditByCommunityIdAsync(
            db,
            community.Id,
            actorRole,
            "UNIT_CREATED_WIZARD",
            "Info",
            $"Unit: {unit.UnitNumber} ({community.CommunityAccountNumber})",
            $"Unit {unit.UnitNumber} created in {building.Name} ({unit.FloorAreaM2:F1} m2, status {unit.OccupancyStatus})."
                + (assignedResident is not null ? $" Assigned resident {assignedResident.FullName} ({assignedResident.ResidentAccountNumber})." : string.Empty),
            ct);

        await db.SaveChangesAsync(ct);
        return new UnitCreationWizardResult(unit, building, primaryOwnership, assignedResident, createdAssignment, generatedPlaintextToken);
    }

    public async Task<Unit> UpdateUnitFullDetailsAsync(
        Guid communityId,
        Guid unitId,
        UnitUpdateInput input,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may edit units.");

        var number = (input.UnitNumber ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(number))
            throw new InvalidOperationException("Unit number is required.");
        if (input.FloorAreaM2 <= 0m || input.FloorAreaM2 > 5000m)
            throw new InvalidOperationException("Floor area (m2) must be greater than 0.");
        if (input.ShareDenominator <= 0)
            throw new InvalidOperationException("Share denominator must be greater than 0.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{number} {input.PrimaryOwnerName} {input.PrimaryOwnerEmail} {input.Notes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Unit update blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId && u.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Unit not found in this community.");

        var duplicate = await db.Units.AsNoTracking()
            .AnyAsync(u => u.CommunityId == communityId && u.Id != unitId && u.UnitNumber.ToUpper() == number.ToUpperInvariant(), ct);
        if (duplicate)
            throw new InvalidOperationException($"Another unit with number '{number}' already exists in this community.");

        if (input.BuildingId != Guid.Empty)
        {
            var buildingExists = await db.Buildings.AnyAsync(b => b.Id == input.BuildingId && b.CommunityId == communityId, ct);
            if (buildingExists) unit.BuildingId = input.BuildingId;
        }

        var stackCode = NormalizeStackCode(input.VerticalStackCode);
        if (!string.IsNullOrWhiteSpace(stackCode))
        {
            unit.VerticalStackCode = stackCode;
            var hasStack = await db.RiserStacks.AnyAsync(r => r.BuildingId == unit.BuildingId && r.Code.ToUpper() == stackCode.ToUpperInvariant(), ct);
            if (!hasStack)
            {
                db.RiserStacks.Add(new VerticalRiserStack
                {
                    BuildingId = unit.BuildingId,
                    Code = stackCode,
                    Description = stackCode.Contains('B') ? "Heating riser" : "Water riser",
                    ShutoffValveLocation = "Basement manifold"
                });
            }
        }

        unit.UnitNumber = number;
        unit.FloorNumber = Math.Clamp(input.FloorNumber, 0, 100);
        unit.StairwellNumber = Math.Clamp(input.StairwellNumber, 1, 20);
        unit.Kind = input.Kind;
        unit.FloorAreaM2 = input.FloorAreaM2;
        unit.StorageAreaM2 = Math.Max(0m, input.StorageAreaM2);
        unit.RoomsCount = Math.Clamp(input.RoomsCount, 0, 50);
        unit.BathroomsCount = Math.Clamp(input.BathroomsCount, 0, 20);
        unit.OccupancyCapacity = Math.Clamp(input.OccupancyCapacity, 1, 50);
        unit.OccupancyStatus = input.OccupancyStatus;
        unit.ShareNumerator = Math.Max(0, input.ShareNumerator);
        unit.ShareDenominator = input.ShareDenominator;
        unit.PrimaryOwnerName = (input.PrimaryOwnerName ?? string.Empty).Trim();
        unit.PrimaryOwnerEmail = (input.PrimaryOwnerEmail ?? string.Empty).Trim();
        unit.PrimaryOwnerPhone = (input.PrimaryOwnerPhone ?? string.Empty).Trim();
        unit.PreferredLanguage = string.IsNullOrWhiteSpace(input.PreferredLanguage) ? "EN" : input.PreferredLanguage.Trim().ToUpperInvariant();
        unit.CoOwnerName = string.IsNullOrWhiteSpace(input.CoOwnerName) ? null : input.CoOwnerName.Trim();
        unit.VotingProxyName = string.IsNullOrWhiteSpace(input.VotingProxyName) ? null : input.VotingProxyName.Trim();
        unit.ParkingBayNumber = string.IsNullOrWhiteSpace(input.ParkingBayNumber) ? null : input.ParkingBayNumber.Trim();
        unit.AccessFobCode = string.IsNullOrWhiteSpace(input.AccessFobCode) ? null : input.AccessFobCode.Trim();
        if (!string.IsNullOrWhiteSpace(input.VirtualIbanOrLockboxCode))
            unit.VirtualIbanOrLockboxCode = input.VirtualIbanOrLockboxCode.Trim();
        unit.Notes = (input.Notes ?? string.Empty).Trim();

        // Sync active primary ownership record
        var activeOwnership = await db.UnitOwnerships
            .Where(o => o.UnitId == unit.Id && o.IsActive && o.OwnershipRole == UnitOwnershipRole.PrimaryOwner)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(unit.PrimaryOwnerName))
        {
            if (activeOwnership is null)
            {
                activeOwnership = new UnitOwnership
                {
                    CommunityId = communityId,
                    BuildingId = unit.BuildingId,
                    UnitId = unit.Id,
                    OwnerFullName = unit.PrimaryOwnerName,
                    OwnerEmail = unit.PrimaryOwnerEmail,
                    OwnerPhone = unit.PrimaryOwnerPhone,
                    OwnershipRole = UnitOwnershipRole.PrimaryOwner,
                    ShareNumerator = unit.ShareNumerator,
                    ShareDenominator = unit.ShareDenominator,
                    OwnershipPercentage = 100.0m,
                    EffectiveStartDateUtc = DateTime.UtcNow,
                    IsActive = true
                };
                db.UnitOwnerships.Add(activeOwnership);
            }
            else
            {
                activeOwnership.OwnerFullName = unit.PrimaryOwnerName;
                activeOwnership.OwnerEmail = unit.PrimaryOwnerEmail;
                activeOwnership.OwnerPhone = unit.PrimaryOwnerPhone;
                activeOwnership.ShareNumerator = unit.ShareNumerator;
                activeOwnership.ShareDenominator = unit.ShareDenominator;
            }
        }

        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "UNIT_UPDATED",
            "Info",
            $"Unit: {unit.UnitNumber}",
            $"Unit {unit.UnitNumber} details updated ({unit.FloorAreaM2:F1} m2, owner '{unit.PrimaryOwnerName}', status {unit.OccupancyStatus}).",
            ct);

        await db.SaveChangesAsync(ct);
        return unit;
    }

    public async Task<GlobalUnitsDashboardSnapshot> GetGlobalUnitsDashboardAsync(
        Guid firmId,
        IReadOnlyList<Guid>? allowedCommunityIds = null,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var commQuery = db.Communities.AsNoTracking();
        if (allowedCommunityIds is not null && allowedCommunityIds.Count > 0)
        {
            commQuery = commQuery.Where(c => allowedCommunityIds.Contains(c.Id));
        }
        else if (firmId != Guid.Empty)
        {
            commQuery = commQuery.Where(c => c.ManagementFirmId == firmId);
        }

        var communities = await commQuery.OrderBy(c => c.Name).ToListAsync(ct);
        var commIds = communities.Select(c => c.Id).ToList();
        var buildings = await db.Buildings.AsNoTracking()
            .Include(b => b.RiserStacks)
            .Where(b => commIds.Contains(b.CommunityId))
            .OrderBy(b => b.Name)
            .ToListAsync(ct);
        var units = await db.Units.AsNoTracking()
            .Where(u => commIds.Contains(u.CommunityId))
            .OrderBy(u => u.FloorNumber)
            .ThenBy(u => u.UnitNumber)
            .ToListAsync(ct);
        var activeAssignments = await db.ResidentUnitAssignments.AsNoTracking()
            .Include(a => a.ResidentProfile)
            .Where(a => commIds.Contains(a.CommunityId) && a.Status == UnitAssignmentStatus.Active)
            .ToListAsync(ct);
        var activeTokens = await db.ResidentRegistrationTokens.AsNoTracking()
            .Where(t => commIds.Contains(t.CommunityId) && t.Status == RegistrationTokenStatus.Active)
            .ToListAsync(ct);

        var commMap = communities.ToDictionary(c => c.Id);
        var buildingMap = buildings.ToDictionary(b => b.Id);

        var rows = new List<UnitSummaryRow>(units.Count);
        foreach (var u in units)
        {
            commMap.TryGetValue(u.CommunityId, out var comm);
            buildingMap.TryGetValue(u.BuildingId, out var bld);
            var unitAssigns = activeAssignments.Where(a => a.UnitId == u.Id).ToList();
            var residentNames = unitAssigns
                .Where(a => a.ResidentProfile is not null)
                .Select(a => a.ResidentProfile!.FullName)
                .Distinct()
                .ToList();
            var residentNumbers = unitAssigns
                .Where(a => a.ResidentProfile is not null)
                .Select(a => a.ResidentProfile!.ResidentAccountNumber)
                .Distinct()
                .ToList();
            var primaryResId = unitAssigns
                .OrderByDescending(a => a.IsPrimaryResident)
                .FirstOrDefault()?.ResidentProfileId;
            var tokenCount = activeTokens.Count(t => t.TargetUnitId == u.Id && t.ExpiresAtUtc >= DateTime.UtcNow && t.UseCount < t.MaxUses);

            var effectiveStatus = u.OccupancyStatus;
            if (unitAssigns.Count > 0 && effectiveStatus == UnitOccupancyStatus.Vacant)
            {
                effectiveStatus = unitAssigns.Any(a => a.AssignmentType == ResidentAssignmentType.Tenant)
                    ? UnitOccupancyStatus.TenantOccupied
                    : UnitOccupancyStatus.OwnerOccupied;
            }
            else if (unitAssigns.Count == 0 && tokenCount > 0 && effectiveStatus == UnitOccupancyStatus.Vacant)
            {
                effectiveStatus = UnitOccupancyStatus.PendingRegistration;
            }

            rows.Add(new UnitSummaryRow(
                u.Id,
                u.CommunityId,
                comm?.Name ?? "Community",
                comm?.CommunityAccountNumber ?? "COM-00000000",
                u.BuildingId,
                bld?.Name ?? "Building A",
                u.UnitNumber,
                u.FloorNumber,
                u.StairwellNumber,
                u.VerticalStackCode,
                u.Kind,
                u.FloorAreaM2,
                u.StorageAreaM2,
                u.RoomsCount,
                u.BathroomsCount,
                u.OccupancyCapacity,
                u.ShareNumerator,
                u.ShareDenominator,
                u.OwnershipShare,
                effectiveStatus,
                u.PrimaryOwnerName,
                u.PrimaryOwnerEmail,
                residentNames,
                residentNumbers,
                primaryResId,
                u.CurrentBalance,
                u.DaysPastDue,
                tokenCount));
        }

        var occupied = rows.Count(r => r.OccupancyStatus is UnitOccupancyStatus.OwnerOccupied or UnitOccupancyStatus.TenantOccupied || r.ActiveResidentNames.Count > 0);
        var pendingReg = rows.Count(r => r.OccupancyStatus == UnitOccupancyStatus.PendingRegistration || r.ActiveRegistrationLinksCount > 0);
        var vacant = rows.Count(r => r.OccupancyStatus == UnitOccupancyStatus.Vacant && r.ActiveResidentNames.Count == 0);
        var distinctResidents = activeAssignments.Select(a => a.ResidentProfileId).Distinct().Count();
        var arrearsCount = rows.Count(r => r.CurrentBalance > 0m);

        return new GlobalUnitsDashboardSnapshot(
            communities,
            buildings,
            rows,
            rows.Count,
            occupied,
            vacant,
            pendingReg,
            distinctResidents,
            arrearsCount);
    }

    public async Task<UnitFullDossier?> GetUnitFullDossierAsync(
        Guid communityId,
        Guid unitId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.AsNoTracking().FirstOrDefaultAsync(c => c.Id == communityId, ct);
        if (community is null) return null;
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId && u.CommunityId == communityId, ct);
        if (unit is null) return null;
        var building = await db.Buildings.AsNoTracking()
            .Include(b => b.RiserStacks)
            .FirstOrDefaultAsync(b => b.Id == unit.BuildingId, ct)
            ?? await db.Buildings.AsNoTracking().Include(b => b.RiserStacks).FirstOrDefaultAsync(b => b.CommunityId == communityId, ct)
            ?? new Building { CommunityId = communityId, Name = "Building A", StreetAddress = community.StreetAddress };

        var ownerships = await db.UnitOwnerships.AsNoTracking()
            .Where(o => o.UnitId == unitId)
            .OrderByDescending(o => o.IsActive)
            .ThenByDescending(o => o.EffectiveStartDateUtc)
            .ToListAsync(ct);
        var allAssignments = await db.ResidentUnitAssignments.AsNoTracking()
            .Include(a => a.ResidentProfile)
            .Where(a => a.UnitId == unitId)
            .OrderByDescending(a => a.MoveInDateUtc)
            .ToListAsync(ct);
        var activeAssignments = allAssignments.Where(a => a.Status is UnitAssignmentStatus.Active or UnitAssignmentStatus.PendingMoveIn or UnitAssignmentStatus.MoveOutScheduled).ToList();
        var pastAssignments = allAssignments.Where(a => a.Status is UnitAssignmentStatus.Ended or UnitAssignmentStatus.Cancelled).ToList();
        var household = await db.ResidentHouseholdMembers.AsNoTracking()
            .Where(h => h.UnitId == unitId)
            .OrderByDescending(h => h.IsActive)
            .ThenBy(h => h.FullName)
            .ToListAsync(ct);
        var meters = await db.UtilityMeters.AsNoTracking()
            .Include(m => m.Readings)
            .Where(m => m.UnitId == unitId)
            .OrderBy(m => m.Kind)
            .ToListAsync(ct);
        var assessments = await db.MonthlyAssessments.AsNoTracking()
            .Where(a => a.UnitId == unitId)
            .OrderByDescending(a => a.Period)
            .ToListAsync(ct);
        var residentIds = allAssignments.Select(a => a.ResidentProfileId).Distinct().ToList();
        var docs = await db.ResidentDocuments.AsNoTracking()
            .Where(d => d.UnitId == unitId || (d.ResidentProfileId.HasValue && residentIds.Contains(d.ResidentProfileId.Value)))
            .OrderByDescending(d => d.UploadedAtUtc)
            .ToListAsync(ct);
        var tokens = await db.ResidentRegistrationTokens.AsNoTracking()
            .Where(t => t.TargetUnitId == unitId)
            .OrderByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        var requests = await db.ResidentRegistrationRequests.AsNoTracking()
            .Where(r => r.TargetUnitId == unitId)
            .OrderByDescending(r => r.SubmittedAtUtc)
            .ToListAsync(ct);
        var unitTag = unit.UnitNumber;
        var audits = await db.SecurityAuditEvents.AsNoTracking()
            .Where(e => e.CommunityId == communityId && (e.ResourceDescriptor.Contains(unitTag) || e.Summary.Contains(unitTag)))
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(40)
            .ToListAsync(ct);

        return new UnitFullDossier(
            community,
            building,
            unit,
            ownerships,
            activeAssignments,
            pastAssignments,
            household,
            meters,
            assessments,
            docs,
            tokens,
            requests,
            audits);
    }

    public async Task<IReadOnlyList<ResidentMatchCandidate>> FindPotentialResidentMatchesAsync(
        Guid? firmId,
        string? email,
        string? phone,
        string? fullName,
        string? residentAccountNumber = null,
        Guid? userId = null,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var query = db.ResidentProfiles.AsNoTracking().AsQueryable();
        if (firmId.HasValue && firmId.Value != Guid.Empty)
        {
            query = query.Where(r => r.ManagementFirmId == null || r.ManagementFirmId == firmId.Value);
        }
        var profiles = await query.ToListAsync(ct);
        if (profiles.Count == 0) return [];

        var activeAssigns = await db.ResidentUnitAssignments.AsNoTracking()
            .Where(a => a.Status == UnitAssignmentStatus.Active)
            .ToListAsync(ct);
        var units = await db.Units.AsNoTracking().ToDictionaryAsync(u => u.Id, ct);

        var normEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        var normPhone = NormalizePhoneForMatch(phone);
        var normName = (fullName ?? string.Empty).Trim().ToLowerInvariant();
        var normAcc = (residentAccountNumber ?? string.Empty).Trim().ToUpperInvariant();

        var matches = new List<ResidentMatchCandidate>();
        foreach (var p in profiles)
        {
            var score = 0;
            var reasons = new List<string>();
            if (!string.IsNullOrWhiteSpace(normAcc) && string.Equals(p.ResidentAccountNumber, normAcc, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
                reasons.Add($"Exact Resident Account Number ({p.ResidentAccountNumber})");
            }
            if (userId.HasValue && userId.Value != Guid.Empty && p.UserId == userId.Value)
            {
                score += 95;
                reasons.Add("Linked authenticated User ID");
            }
            if (!string.IsNullOrWhiteSpace(normEmail) && string.Equals(p.PrimaryEmail.Trim(), normEmail, StringComparison.OrdinalIgnoreCase))
            {
                score += 90;
                reasons.Add($"Matching email ({p.PrimaryEmail})");
            }
            var pPhone = NormalizePhoneForMatch(p.Phone);
            if (!string.IsNullOrWhiteSpace(normPhone) && normPhone.Length >= 7 && string.Equals(pPhone, normPhone, StringComparison.Ordinal))
            {
                score += 75;
                reasons.Add($"Matching phone ({p.Phone})");
            }
            if (!string.IsNullOrWhiteSpace(normName) && normName.Length >= 3 && string.Equals(p.FullName.Trim(), normName, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
                reasons.Add($"Matching full name ({p.FullName})");
            }

            if (score > 0)
            {
                var unitLabels = activeAssigns
                    .Where(a => a.ResidentProfileId == p.Id && units.ContainsKey(a.UnitId))
                    .Select(a => $"Unit {units[a.UnitId].UnitNumber}")
                    .Distinct()
                    .ToList();
                matches.Add(new ResidentMatchCandidate(
                    p,
                    string.Join(" + ", reasons),
                    Math.Min(100, score),
                    unitLabels));
            }
        }

        return matches
            .OrderByDescending(m => m.ConfidenceScore)
            .ThenBy(m => m.Profile.FullName)
            .ToList();
    }

    public async Task<ResidentProfile> CreateOrGetResidentProfileAsync(
        Guid? firmId,
        Guid? userId,
        string firstName,
        string lastName,
        string email,
        string phone,
        string preferredLanguage = "EN",
        string emergencyContactName = "",
        string emergencyContactPhone = "",
        string notes = "",
        CancellationToken ct = default)
    {
        var (first, last, full) = NormalizeNameParts(firstName, lastName);
        var cleanEmail = (email ?? string.Empty).Trim();
        var cleanPhone = (phone ?? string.Empty).Trim();

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{full} {cleanEmail} {cleanPhone} {notes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Resident profile creation blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        // Guard against reusing a manager identity for another person.
        // When userId is supplied but its account email differs from the resident email, ignore userId.
        Guid? effectiveUserId = userId.HasValue && userId.Value != Guid.Empty ? userId.Value : null;
        if (effectiveUserId.HasValue && !string.IsNullOrWhiteSpace(cleanEmail))
        {
            var linkedUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == effectiveUserId.Value, ct);
            string? linkedEmail = linkedUser?.Email;
            if (linkedUser is null)
            {
                var linkedAccount = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == effectiveUserId.Value, ct);
                linkedEmail = linkedAccount?.Email;
            }
            if (!string.IsNullOrWhiteSpace(linkedEmail)
                && !string.Equals(linkedEmail.Trim(), cleanEmail, StringComparison.OrdinalIgnoreCase))
            {
                effectiveUserId = null;
            }
        }
        ResidentProfile? existing = null;
        if (effectiveUserId.HasValue && effectiveUserId.Value != Guid.Empty)
        {
            existing = await db.ResidentProfiles.FirstOrDefaultAsync(r => r.UserId == effectiveUserId.Value, ct);
            if (existing is not null && !string.IsNullOrWhiteSpace(cleanEmail) && !string.IsNullOrWhiteSpace(existing.PrimaryEmail)
                && !string.Equals(existing.PrimaryEmail.Trim(), cleanEmail, StringComparison.OrdinalIgnoreCase))
            {
                // Profile bound to this UserId belongs to a different person; do not reuse it.
                existing = null;
                effectiveUserId = null;
            }
        }
        if (existing is null && !string.IsNullOrWhiteSpace(cleanEmail))
        {
            var lowered = cleanEmail.ToLowerInvariant();
            existing = await db.ResidentProfiles.FirstOrDefaultAsync(
                r => r.PrimaryEmail.ToLower() == lowered && (r.ManagementFirmId == null || r.ManagementFirmId == firmId),
                ct);
        }

        if (existing is not null)
        {
            if (effectiveUserId.HasValue && effectiveUserId.Value != Guid.Empty && existing.UserId is null)
                existing.UserId = effectiveUserId;
            if (firmId.HasValue && firmId.Value != Guid.Empty && existing.ManagementFirmId is null)
                existing.ManagementFirmId = firmId;
            if (!string.IsNullOrWhiteSpace(cleanPhone) && string.IsNullOrWhiteSpace(existing.Phone))
                existing.Phone = cleanPhone;
            await db.SaveChangesAsync(ct);
            return existing;
        }

        var allNumbers = await db.ResidentProfiles.AsNoTracking().Select(r => r.ResidentAccountNumber).ToListAsync(ct);
        var nextNumber = AccountNumberGenerator.NextResidentAccountNumber(allNumbers);

        var profile = new ResidentProfile
        {
            ResidentAccountNumber = nextNumber,
            UserId = effectiveUserId,
            ManagementFirmId = firmId,
            FirstName = first,
            LastName = last,
            FullName = full,
            PrimaryEmail = cleanEmail,
            Phone = cleanPhone,
            PreferredLanguage = string.IsNullOrWhiteSpace(preferredLanguage) ? "EN" : preferredLanguage.Trim().ToUpperInvariant(),
            EmergencyContactName = (emergencyContactName ?? string.Empty).Trim(),
            EmergencyContactPhone = (emergencyContactPhone ?? string.Empty).Trim(),
            AccountStatus = ResidentAccountStatus.Active,
            VerificationStatus = ResidentVerificationStatus.Verified,
            RegistrationDateUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow,
            Notes = (notes ?? string.Empty).Trim()
        };
        db.ResidentProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<ResidentProfile> UpdateResidentFullProfileAsync(
        Guid residentProfileId,
        ResidentProfileUpdateInput input,
        WorkspaceSecurityRole actorRole,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{input.FirstName} {input.LastName} {input.PrimaryEmail} {input.Phone} {input.EmergencyContactName} {input.Notes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Resident profile update blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var profile = await db.ResidentProfiles.FirstOrDefaultAsync(r => r.Id == residentProfileId, ct)
            ?? throw new InvalidOperationException("Resident profile not found.");

        var isSelf = actorUserId.HasValue && profile.UserId == actorUserId.Value;
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole) && !isSelf)
            throw new UnauthorizedAccessException("Only an administrator or the resident themselves may update this profile.");

        // ResidentAccountNumber is strictly immutable once valid
        if (!AccountNumberGenerator.IsValidResidentAccountNumber(profile.ResidentAccountNumber))
        {
            var allNumbers = await db.ResidentProfiles.AsNoTracking().Select(r => r.ResidentAccountNumber).ToListAsync(ct);
            profile.ResidentAccountNumber = AccountNumberGenerator.NextResidentAccountNumber(allNumbers);
        }

        var (first, last, full) = NormalizeNameParts(input.FirstName, input.LastName, profile.FullName);
        profile.FirstName = first;
        profile.LastName = last;
        profile.FullName = full;
        if (!string.IsNullOrWhiteSpace(input.PrimaryEmail))
            profile.PrimaryEmail = input.PrimaryEmail.Trim();
        profile.Phone = (input.Phone ?? string.Empty).Trim();
        profile.DateOfBirth = input.DateOfBirth;
        profile.ProfilePhotoUrl = string.IsNullOrWhiteSpace(input.ProfilePhotoUrl) ? null : input.ProfilePhotoUrl.Trim();
        profile.AdditionalContactDetails = (input.AdditionalContactDetails ?? string.Empty).Trim();
        profile.PreferredLanguage = string.IsNullOrWhiteSpace(input.PreferredLanguage) ? "EN" : input.PreferredLanguage.Trim().ToUpperInvariant();
        profile.PreferredContactMethod = string.IsNullOrWhiteSpace(input.PreferredContactMethod) ? "Email" : input.PreferredContactMethod.Trim();
        profile.EmergencyContactName = (input.EmergencyContactName ?? string.Empty).Trim();
        profile.EmergencyContactPhone = (input.EmergencyContactPhone ?? string.Empty).Trim();
        profile.EmergencyContactRelation = (input.EmergencyContactRelation ?? string.Empty).Trim();

        if (DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            profile.AccountStatus = input.AccountStatus;
            profile.VerificationStatus = input.VerificationStatus;
            profile.Notes = (input.Notes ?? string.Empty).Trim();
        }

        if (profile.UserId.HasValue)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == profile.UserId.Value, ct);
            if (user is not null)
            {
                user.FullName = profile.FullName;
                user.Phone = profile.Phone;
            }
            var wsUser = await db.UserAccounts.FirstOrDefaultAsync(u => u.Id == profile.UserId.Value, ct);
            if (wsUser is not null)
            {
                wsUser.FullName = profile.FullName;
                wsUser.Phone = profile.Phone;
            }
        }

        var firstCommId = await db.ResidentUnitAssignments.AsNoTracking()
            .Where(a => a.ResidentProfileId == profile.Id)
            .Select(a => a.CommunityId)
            .FirstOrDefaultAsync(ct);
        if (firstCommId != Guid.Empty)
        {
            await AppendChainedAuditByCommunityIdAsync(
                db,
                firstCommId,
                actorRole,
                "RESIDENT_PROFILE_UPDATED",
                "Info",
                $"Resident: {profile.ResidentAccountNumber}",
                $"Resident profile {profile.FullName} ({profile.ResidentAccountNumber}) updated.",
                ct);
        }

        await db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<ResidentFullDossier?> GetResidentFullDossierAsync(Guid residentProfileId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var profile = await db.ResidentProfiles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == residentProfileId, ct);
        if (profile is null) return null;
        return await BuildResidentFullDossierInternalAsync(db, profile, ct);
    }

    public async Task<ResidentFullDossier?> GetResidentFullDossierByUserAsync(
        Guid? userId,
        string? email,
        Guid? communityId = null,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        ResidentProfile? profile = null;
        if (userId.HasValue && userId.Value != Guid.Empty)
        {
            profile = await db.ResidentProfiles.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId.Value, ct);
        }
        if (profile is null && !string.IsNullOrWhiteSpace(email))
        {
            var lower = email.Trim().ToLowerInvariant();
            profile = await db.ResidentProfiles.AsNoTracking().FirstOrDefaultAsync(r => r.PrimaryEmail.ToLower() == lower, ct);
        }
        if (profile is null && userId.HasValue && userId.Value != Guid.Empty)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            var wsUser = await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
            var fullName = user?.FullName ?? wsUser?.FullName ?? email ?? "Resident";
            var resolvedEmail = user?.Email ?? wsUser?.Email ?? email ?? string.Empty;
            var resolvedPhone = user?.Phone ?? wsUser?.Phone ?? string.Empty;
            var firmId = wsUser?.ManagementFirmId;
            if (!firmId.HasValue && communityId.HasValue)
            {
                firmId = await db.Communities.AsNoTracking().Where(c => c.Id == communityId.Value).Select(c => c.ManagementFirmId).FirstOrDefaultAsync(ct);
            }
            var created = await CreateOrGetResidentProfileAsync(firmId, userId, fullName, string.Empty, resolvedEmail, resolvedPhone, ct: ct);
            profile = created;
        }
        if (profile is null) return null;
        return await BuildResidentFullDossierInternalAsync(db, profile, ct);
    }

    private static async Task<ResidentFullDossier> BuildResidentFullDossierInternalAsync(
        IAppDbContext db,
        ResidentProfile profile,
        CancellationToken ct)
    {
        var assignments = await db.ResidentUnitAssignments.AsNoTracking()
            .Where(a => a.ResidentProfileId == profile.Id)
            .OrderByDescending(a => a.MoveInDateUtc)
            .ToListAsync(ct);
        var ownerships = await db.UnitOwnerships.AsNoTracking()
            .Where(o => o.ResidentProfileId == profile.Id
                || (!string.IsNullOrWhiteSpace(profile.PrimaryEmail) && o.OwnerEmail.ToLower() == profile.PrimaryEmail.ToLower()))
            .OrderByDescending(o => o.IsActive)
            .ToListAsync(ct);

        var commIds = assignments.Select(a => a.CommunityId).Concat(ownerships.Select(o => o.CommunityId)).Distinct().ToList();
        var unitIds = assignments.Select(a => a.UnitId).Concat(ownerships.Select(o => o.UnitId)).Distinct().ToList();

        var comms = await db.Communities.AsNoTracking().Where(c => commIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var buildings = await db.Buildings.AsNoTracking().Where(b => commIds.Contains(b.CommunityId)).ToDictionaryAsync(b => b.Id, ct);
        var units = await db.Units.AsNoTracking().Where(u => unitIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var activeItems = new List<ResidentAssignmentViewItem>();
        var pastItems = new List<ResidentAssignmentViewItem>();
        foreach (var a in assignments)
        {
            comms.TryGetValue(a.CommunityId, out var c);
            buildings.TryGetValue(a.BuildingId, out var b);
            units.TryGetValue(a.UnitId, out var u);
            var item = new ResidentAssignmentViewItem(
                a,
                c?.Name ?? "Community",
                c?.CommunityAccountNumber ?? "COM-00000000",
                b?.Name ?? "Building A",
                u?.UnitNumber ?? "—",
                u?.FloorAreaM2 ?? 0m);
            if (a.Status is UnitAssignmentStatus.Active or UnitAssignmentStatus.PendingMoveIn or UnitAssignmentStatus.MoveOutScheduled)
                activeItems.Add(item);
            else
                pastItems.Add(item);
        }

        var ownershipItems = ownerships.Select(o =>
        {
            comms.TryGetValue(o.CommunityId, out var c);
            buildings.TryGetValue(o.BuildingId, out var b);
            units.TryGetValue(o.UnitId, out var u);
            return new ResidentOwnershipViewItem(
                o,
                c?.Name ?? "Community",
                c?.CommunityAccountNumber ?? "COM-00000000",
                b?.Name ?? "Building A",
                u?.UnitNumber ?? "—");
        }).ToList();

        var household = await db.ResidentHouseholdMembers.AsNoTracking()
            .Where(h => unitIds.Contains(h.UnitId) || h.LinkedResidentProfileId == profile.Id)
            .OrderByDescending(h => h.IsActive)
            .ThenBy(h => h.FullName)
            .ToListAsync(ct);

        var docs = await db.ResidentDocuments.AsNoTracking()
            .Where(d => d.ResidentProfileId == profile.Id || (d.UnitId.HasValue && unitIds.Contains(d.UnitId.Value)))
            .OrderByDescending(d => d.UploadedAtUtc)
            .ToListAsync(ct);

        var messages = new List<ResidentDirectMessage>();
        if (!string.IsNullOrWhiteSpace(profile.PrimaryEmail))
        {
            var lower = profile.PrimaryEmail.ToLowerInvariant();
            messages = await db.ResidentDirectMessages.AsNoTracking()
                .Where(m => m.SenderEmail.ToLower() == lower || m.RecipientName.ToLower().Contains(lower))
                .OrderByDescending(m => m.SentAtUtc)
                .Take(30)
                .ToListAsync(ct);
        }

        var accNum = profile.ResidentAccountNumber;
        var audits = await db.SecurityAuditEvents.AsNoTracking()
            .Where(e => commIds.Contains(e.CommunityId) && (e.ResourceDescriptor.Contains(accNum) || e.Summary.Contains(accNum) || e.Summary.Contains(profile.FullName)))
            .OrderByDescending(e => e.OccurredAtUtc)
            .Take(30)
            .ToListAsync(ct);

        var registrationTokens = unitIds.Count == 0
            ? new List<ResidentRegistrationToken>()
            : await db.ResidentRegistrationTokens.AsNoTracking()
                .Where(t => unitIds.Contains(t.TargetUnitId))
                .OrderByDescending(t => t.CreatedAtUtc)
                .ToListAsync(ct);

        return new ResidentFullDossier(
            profile,
            activeItems,
            pastItems,
            ownershipItems,
            household,
            docs,
            messages,
            audits,
            registrationTokens);
    }

    public async Task<ResidentProfile> ClaimResidentProfileAsync(
        Guid residentProfileId,
        Guid actorUserId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (residentProfileId == Guid.Empty)
            throw new InvalidOperationException("Resident profile id is required.");
        if (actorUserId == Guid.Empty)
            throw new UnauthorizedAccessException("Sign-in is required to claim a resident profile.");

        await using var db = await CreateDbAsync(ct);
        var profile = await db.ResidentProfiles.FirstOrDefaultAsync(r => r.Id == residentProfileId, ct)
            ?? throw new InvalidOperationException("Resident profile not found.");

        if (profile.UserId.HasValue && profile.UserId.Value != Guid.Empty && profile.UserId.Value != actorUserId)
            throw new InvalidOperationException("This resident profile is already linked to another login account.");

        if (profile.UserId.HasValue && profile.UserId.Value == actorUserId)
            return profile;

        var isManager = DataSecurityAndPrivacyGuard.IsManagerRole(actorRole);
        if (!isManager)
        {
            var actorUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorUserId, ct);
            var actorAccount = actorUser is null
                ? await db.UserAccounts.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorUserId, ct)
                : null;
            var actorEmail = (actorUser?.Email ?? actorAccount?.Email ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(actorEmail)
                || string.IsNullOrWhiteSpace(profile.PrimaryEmail)
                || !string.Equals(actorEmail, profile.PrimaryEmail.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Only a manager or the resident with a matching email may claim this profile.");
        }

        // Never escalate or modify FirmMembership roles here. Claim only links ResidentProfile.UserId.
        profile.UserId = actorUserId;

        var claimCommunityId = await db.ResidentUnitAssignments.AsNoTracking()
            .Where(a => a.ResidentProfileId == profile.Id)
            .Select(a => a.CommunityId)
            .FirstOrDefaultAsync(ct);
        if (claimCommunityId == Guid.Empty)
        {
            claimCommunityId = await db.UnitOwnerships.AsNoTracking()
                .Where(o => o.ResidentProfileId == profile.Id)
                .Select(o => o.CommunityId)
                .FirstOrDefaultAsync(ct);
        }

        if (claimCommunityId != Guid.Empty)
        {
            await AppendChainedAuditByCommunityIdAsync(
                db,
                claimCommunityId,
                actorRole,
                "RESIDENT_PROFILE_CLAIMED",
                "Info",
                $"Resident: {profile.ResidentAccountNumber}",
                $"Resident profile {profile.FullName} ({profile.ResidentAccountNumber}) claimed and linked to account {actorUserId} by {actorRole}.",
                ct);
        }

        await db.SaveChangesAsync(ct);
        return profile;
    }

    public async Task<IReadOnlyList<ResidentDirectoryRow>> GetFirmResidentsDirectoryAsync(
        Guid firmId,
        IReadOnlyList<Guid>? allowedCommunityIds = null,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var commQuery = db.Communities.AsNoTracking();
        if (allowedCommunityIds is not null && allowedCommunityIds.Count > 0)
        {
            commQuery = commQuery.Where(c => allowedCommunityIds.Contains(c.Id));
        }
        else if (firmId != Guid.Empty)
        {
            commQuery = commQuery.Where(c => c.ManagementFirmId == firmId);
        }
        var communities = await commQuery.ToDictionaryAsync(c => c.Id, ct);
        var commIds = communities.Keys.ToList();

        var assignments = await db.ResidentUnitAssignments.AsNoTracking()
            .Where(a => commIds.Contains(a.CommunityId) || a.ManagementFirmId == firmId)
            .ToListAsync(ct);
        var assignedResidentIds = assignments.Select(a => a.ResidentProfileId).Distinct().ToList();

        var profiles = await db.ResidentProfiles.AsNoTracking()
            .Where(r => r.ManagementFirmId == firmId || assignedResidentIds.Contains(r.Id))
            .OrderBy(r => r.FullName)
            .ToListAsync(ct);

        var buildings = await db.Buildings.AsNoTracking()
            .Where(b => commIds.Contains(b.CommunityId))
            .ToDictionaryAsync(b => b.Id, ct);
        var units = await db.Units.AsNoTracking()
            .Where(u => commIds.Contains(u.CommunityId))
            .ToDictionaryAsync(u => u.Id, ct);
        var ownerships = await db.UnitOwnerships.AsNoTracking()
            .Where(o => commIds.Contains(o.CommunityId) && o.IsActive)
            .ToListAsync(ct);
        var docs = await db.ResidentDocuments.AsNoTracking()
            .Where(d => commIds.Contains(d.CommunityId))
            .ToListAsync(ct);

        var rows = new List<ResidentDirectoryRow>(profiles.Count);
        foreach (var p in profiles)
        {
            var resAssigns = assignments.Where(a => a.ResidentProfileId == p.Id).ToList();
            var active = resAssigns
                .Where(a => a.Status is UnitAssignmentStatus.Active or UnitAssignmentStatus.PendingMoveIn or UnitAssignmentStatus.MoveOutScheduled)
                .Select(a =>
                {
                    communities.TryGetValue(a.CommunityId, out var c);
                    buildings.TryGetValue(a.BuildingId, out var b);
                    units.TryGetValue(a.UnitId, out var u);
                    return new ResidentAssignmentViewItem(
                        a,
                        c?.Name ?? "Community",
                        c?.CommunityAccountNumber ?? "COM-00000000",
                        b?.Name ?? "Building A",
                        u?.UnitNumber ?? "—",
                        u?.FloorAreaM2 ?? 0m);
                })
                .ToList();
            var pastCount = resAssigns.Count(a => a.Status is UnitAssignmentStatus.Ended or UnitAssignmentStatus.Cancelled);
            var ownedCount = ownerships.Count(o => o.ResidentProfileId == p.Id
                || (!string.IsNullOrWhiteSpace(p.PrimaryEmail) && string.Equals(o.OwnerEmail, p.PrimaryEmail, StringComparison.OrdinalIgnoreCase)));
            var docCount = docs.Count(d => d.ResidentProfileId == p.Id);

            rows.Add(new ResidentDirectoryRow(p, active, pastCount, ownedCount, docCount));
        }

        return rows;
    }

    public async Task<UnitOccupancyConflictReport> CheckUnitOccupancyConflictAsync(
        Guid communityId,
        Guid unitId,
        ResidentAssignmentType proposedType,
        bool isPrimaryResident,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId && u.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Unit not found.");
        var activeAssignments = await db.ResidentUnitAssignments.AsNoTracking()
            .Include(a => a.ResidentProfile)
            .Where(a => a.UnitId == unitId && a.Status == UnitAssignmentStatus.Active)
            .ToListAsync(ct);
        var activeHouseholdCount = await db.ResidentHouseholdMembers.AsNoTracking()
            .CountAsync(h => h.UnitId == unitId && h.IsActive, ct);

        var warnings = new List<string>();
        var requiresOverride = false;

        if (isPrimaryResident && activeAssignments.Any(a => a.IsPrimaryResident))
        {
            var existingPrimary = activeAssignments.First(a => a.IsPrimaryResident);
            warnings.Add($"Unit {unit.UnitNumber} already has an active Primary Resident ({existingPrimary.ResidentProfile?.FullName ?? "Resident"} - {existingPrimary.ResidentProfile?.ResidentAccountNumber}).");
            requiresOverride = true;
        }

        if (proposedType == ResidentAssignmentType.Tenant && activeAssignments.Any(a => a.AssignmentType == ResidentAssignmentType.OwnerResident))
        {
            warnings.Add($"Unit {unit.UnitNumber} currently has an active Owner-Resident assignment.");
            requiresOverride = true;
        }

        var totalOccupantsAfter = activeAssignments.Count + activeHouseholdCount + 1;
        if (totalOccupantsAfter > unit.OccupancyCapacity)
        {
            warnings.Add($"Assigning this resident brings total occupants ({totalOccupantsAfter}) above Unit {unit.UnitNumber}'s configured capacity ({unit.OccupancyCapacity}).");
            requiresOverride = true;
        }

        return new UnitOccupancyConflictReport(warnings.Count > 0, requiresOverride, warnings, activeAssignments);
    }

    public async Task<ResidentUnitAssignment> AssignResidentToUnitManualAsync(
        ManualResidentAssignmentRequest request,
        WorkspaceSecurityRole actorRole,
        string actorName = "Administrator",
        Guid? actorMembershipId = null,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may assign residents to units.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{request.FirstName} {request.LastName} {request.Email} {request.AssignmentReasonOrNotes} {request.ConflictOverrideReason}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Resident assignment blocked by security guard.");

        var conflict = await CheckUnitOccupancyConflictAsync(
            request.CommunityId, request.UnitId, request.AssignmentType, request.IsPrimaryResident, ct);
        if (conflict.RequiresOverrideReason && string.IsNullOrWhiteSpace(request.ConflictOverrideReason))
        {
            throw new InvalidOperationException(
                "Occupancy conflict detected: " + string.Join(" ", conflict.Warnings) + " Provide an explicit conflict override reason to proceed.");
        }

        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == request.CommunityId, ct)
            ?? throw new InvalidOperationException("Community not found.");
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == request.UnitId && u.CommunityId == community.Id, ct)
            ?? throw new InvalidOperationException("Unit not found in the selected community.");

        ResidentProfile resident;
        if (!request.CreateNewResident && request.ExistingResidentProfileId.HasValue && request.ExistingResidentProfileId.Value != Guid.Empty)
        {
            resident = await db.ResidentProfiles.FirstOrDefaultAsync(r => r.Id == request.ExistingResidentProfileId.Value, ct)
                ?? throw new InvalidOperationException("Selected Resident Profile not found.");
            if (resident.ManagementFirmId is null && community.ManagementFirmId.HasValue)
                resident.ManagementFirmId = community.ManagementFirmId;
        }
        else
        {
            var (first, last, full) = NormalizeNameParts(request.FirstName, request.LastName);
            var cleanEmail = (request.Email ?? string.Empty).Trim();
            ResidentProfile? existing = null;
            if (!string.IsNullOrWhiteSpace(cleanEmail))
            {
                var lower = cleanEmail.ToLowerInvariant();
                existing = await db.ResidentProfiles.FirstOrDefaultAsync(
                    r => r.PrimaryEmail.ToLower() == lower && (r.ManagementFirmId == null || r.ManagementFirmId == community.ManagementFirmId),
                    ct);
            }
            if (existing is not null)
            {
                resident = existing;
            }
            else
            {
                var allNumbers = await db.ResidentProfiles.AsNoTracking().Select(r => r.ResidentAccountNumber).ToListAsync(ct);
                var nextNumber = AccountNumberGenerator.NextResidentAccountNumber(allNumbers);
                resident = new ResidentProfile
                {
                    ResidentAccountNumber = nextNumber,
                    ManagementFirmId = community.ManagementFirmId,
                    FirstName = first,
                    LastName = last,
                    FullName = full,
                    PrimaryEmail = cleanEmail,
                    Phone = (request.Phone ?? string.Empty).Trim(),
                    PreferredLanguage = string.IsNullOrWhiteSpace(request.PreferredLanguage) ? "EN" : request.PreferredLanguage.Trim().ToUpperInvariant(),
                    EmergencyContactName = (request.EmergencyContactName ?? string.Empty).Trim(),
                    EmergencyContactPhone = (request.EmergencyContactPhone ?? string.Empty).Trim(),
                    AccountStatus = ResidentAccountStatus.Active,
                    VerificationStatus = ResidentVerificationStatus.Verified,
                    RegistrationDateUtc = DateTime.UtcNow,
                    CreatedAtUtc = DateTime.UtcNow
                };
                db.ResidentProfiles.Add(resident);
            }
        }

        // If overriding an existing primary resident, demote the previous primary flag or keep co-resident
        if (request.IsPrimaryResident)
        {
            var existingPrimaries = await db.ResidentUnitAssignments
                .Where(a => a.UnitId == unit.Id && a.Status == UnitAssignmentStatus.Active && a.IsPrimaryResident)
                .ToListAsync(ct);
            foreach (var prev in existingPrimaries)
            {
                prev.IsPrimaryResident = false;
            }
        }

        var buildingId = request.BuildingId != Guid.Empty ? request.BuildingId : unit.BuildingId;
        var assignment = new ResidentUnitAssignment
        {
            ResidentProfileId = resident.Id,
            CommunityId = community.Id,
            BuildingId = buildingId,
            UnitId = unit.Id,
            ManagementFirmId = community.ManagementFirmId,
            AssignmentType = request.AssignmentType,
            Status = request.Status,
            IsPrimaryResident = request.IsPrimaryResident,
            MoveInDateUtc = request.MoveInDateUtc == default ? DateTime.UtcNow : request.MoveInDateUtc,
            MoveOutDateUtc = request.MoveOutDateUtc,
            ApprovedByMembershipId = actorMembershipId,
            ApprovedByName = actorName,
            AssignedAtUtc = DateTime.UtcNow,
            AssignmentReasonOrNotes = (request.AssignmentReasonOrNotes ?? string.Empty).Trim(),
            ConflictOverrideReason = string.IsNullOrWhiteSpace(request.ConflictOverrideReason) ? null : request.ConflictOverrideReason.Trim()
        };
        db.ResidentUnitAssignments.Add(assignment);

        if (request.Status == UnitAssignmentStatus.Active)
        {
            unit.OccupancyStatus = request.AssignmentType == ResidentAssignmentType.Tenant
                ? UnitOccupancyStatus.TenantOccupied
                : UnitOccupancyStatus.OwnerOccupied;

            if (request.AssignmentType == ResidentAssignmentType.OwnerResident && string.IsNullOrWhiteSpace(unit.PrimaryOwnerName))
            {
                unit.PrimaryOwnerName = resident.FullName;
                unit.PrimaryOwnerEmail = resident.PrimaryEmail;
                unit.PrimaryOwnerPhone = resident.Phone;
            }
        }

        await EnsureResidentPortalLinksInternalAsync(db, resident, community, unit, request.AssignmentType, ct);

        await AppendChainedAuditByCommunityIdAsync(
            db,
            community.Id,
            actorRole,
            "RESIDENT_ASSIGNED_MANUAL",
            conflict.RequiresOverrideReason ? "Warning" : "Info",
            $"Unit {unit.UnitNumber} <- {resident.ResidentAccountNumber}",
            $"Resident {resident.FullName} ({resident.ResidentAccountNumber}) assigned to Unit {unit.UnitNumber} as {request.AssignmentType} by {actorName}."
                + (!string.IsNullOrWhiteSpace(request.ConflictOverrideReason) ? $" Override: {request.ConflictOverrideReason}" : string.Empty),
            ct);

        await db.SaveChangesAsync(ct);
        assignment.ResidentProfile = resident;
        return assignment;
    }

    public async Task<ResidentUnitAssignment> ExecuteMoveOutAsync(
        Guid communityId,
        Guid assignmentId,
        DateTime moveOutDateUtc,
        string reasonOrNotes,
        WorkspaceSecurityRole actorRole,
        string actorName = "Administrator",
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may execute resident move-out.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(reasonOrNotes ?? string.Empty);
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Move-out blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var assignment = await db.ResidentUnitAssignments
            .Include(a => a.ResidentProfile)
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Resident assignment not found.");
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == assignment.UnitId, ct)
            ?? throw new InvalidOperationException("Unit not found.");

        assignment.Status = UnitAssignmentStatus.Ended;
        assignment.MoveOutDateUtc = moveOutDateUtc == default ? DateTime.UtcNow : moveOutDateUtc;
        assignment.EndedAtUtc = DateTime.UtcNow;
        assignment.IsPrimaryResident = false;
        if (!string.IsNullOrWhiteSpace(reasonOrNotes))
        {
            assignment.AssignmentReasonOrNotes = string.IsNullOrWhiteSpace(assignment.AssignmentReasonOrNotes)
                ? $"Move-out: {reasonOrNotes.Trim()}"
                : $"{assignment.AssignmentReasonOrNotes} | Move-out: {reasonOrNotes.Trim()}";
        }

        // Remove active unit portal link for this unit while preserving ResidentProfile and ResidentAccountNumber
        if (assignment.ResidentProfile?.UserId is Guid uid && uid != Guid.Empty)
        {
            var memberships = await db.FirmMemberships
                .Where(m => m.UserId == uid)
                .Select(m => m.Id)
                .ToListAsync(ct);
            var links = await db.MembershipUnitLinks
                .Where(l => memberships.Contains(l.FirmMembershipId) && l.UnitId == unit.Id)
                .ToListAsync(ct);
            foreach (var link in links)
            {
                db.MembershipUnitLinks.Remove(link);
            }
        }

        var remainingActive = await db.ResidentUnitAssignments
            .CountAsync(a => a.UnitId == unit.Id && a.Id != assignment.Id && a.Status == UnitAssignmentStatus.Active, ct);
        if (remainingActive == 0)
        {
            unit.OccupancyStatus = UnitOccupancyStatus.Vacant;
        }

        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "RESIDENT_MOVE_OUT",
            "Info",
            $"Unit {unit.UnitNumber} / {assignment.ResidentProfile?.ResidentAccountNumber}",
            $"Resident {assignment.ResidentProfile?.FullName} ({assignment.ResidentProfile?.ResidentAccountNumber}) moved out of Unit {unit.UnitNumber} by {actorName}. Historical record preserved.",
            ct);

        await db.SaveChangesAsync(ct);
        return assignment;
    }

    public async Task<ResidentHouseholdMember> UpsertHouseholdMemberAsync(
        Guid communityId,
        Guid unitId,
        Guid? memberId,
        string fullName,
        string relationshipToPrimary,
        string phone,
        string email,
        bool hasPortalAccess,
        string notes,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            throw new InvalidOperationException("Household member full name is required.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{fullName} {relationshipToPrimary} {email} {notes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Household member update blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId && u.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Unit not found.");

        ResidentHouseholdMember member;
        if (memberId.HasValue && memberId.Value != Guid.Empty)
        {
            member = await db.ResidentHouseholdMembers.FirstOrDefaultAsync(h => h.Id == memberId.Value && h.UnitId == unitId, ct)
                ?? throw new InvalidOperationException("Household member not found.");
        }
        else
        {
            var primaryAssign = await db.ResidentUnitAssignments.AsNoTracking()
                .Where(a => a.UnitId == unitId && a.Status == UnitAssignmentStatus.Active)
                .OrderByDescending(a => a.IsPrimaryResident)
                .FirstOrDefaultAsync(ct);
            member = new ResidentHouseholdMember
            {
                CommunityId = communityId,
                UnitId = unitId,
                PrimaryAssignmentId = primaryAssign?.Id,
                OccupancyStartDateUtc = DateTime.UtcNow,
                IsActive = true
            };
            db.ResidentHouseholdMembers.Add(member);
        }

        member.FullName = fullName.Trim();
        member.RelationshipToPrimary = string.IsNullOrWhiteSpace(relationshipToPrimary) ? "Household Member" : relationshipToPrimary.Trim();
        member.Phone = (phone ?? string.Empty).Trim();
        member.Email = (email ?? string.Empty).Trim();
        member.HasPortalAccess = hasPortalAccess;
        member.Notes = (notes ?? string.Empty).Trim();

        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "HOUSEHOLD_MEMBER_UPSERTED",
            "Info",
            $"Unit {unit.UnitNumber} / Household: {member.FullName}",
            $"Household member '{member.FullName}' ({member.RelationshipToPrimary}) saved for Unit {unit.UnitNumber}.",
            ct);

        await db.SaveChangesAsync(ct);
        return member;
    }

    public async Task RemoveHouseholdMemberAsync(
        Guid communityId,
        Guid memberId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var member = await db.ResidentHouseholdMembers.FirstOrDefaultAsync(h => h.Id == memberId && h.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Household member not found.");
        member.IsActive = false;
        member.OccupancyEndDateUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<ResidentDocument> AddResidentDocumentAsync(
        Guid communityId,
        Guid? unitId,
        Guid? residentProfileId,
        ResidentDocumentCategory category,
        string title,
        string fileName,
        string summaryOrNotes,
        bool isVisibleToResident,
        string uploadedByName,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException("Document title is required.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{title} {fileName} {summaryOrNotes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Document upload blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var cleanFile = string.IsNullOrWhiteSpace(fileName)
            ? $"{title.Trim().ToLowerInvariant().Replace(' ', '-')}.pdf"
            : fileName.Trim();
        var doc = new ResidentDocument
        {
            CommunityId = communityId,
            UnitId = unitId,
            ResidentProfileId = residentProfileId,
            Category = category,
            Title = title.Trim(),
            FileName = cleanFile,
            MimeType = cleanFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? "application/pdf" : "application/octet-stream",
            FileSizeBytes = 148_480,
            StoragePathOrDataUrl = $"/vault/{communityId:N}/{Guid.NewGuid():N}/{cleanFile}",
            SummaryOrNotes = (summaryOrNotes ?? string.Empty).Trim(),
            UploadedByName = string.IsNullOrWhiteSpace(uploadedByName) ? actorRole.ToString() : uploadedByName.Trim(),
            UploadedByRole = actorRole.ToString(),
            UploadedAtUtc = DateTime.UtcNow,
            IsVisibleToResident = isVisibleToResident
        };
        db.ResidentDocuments.Add(doc);

        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "RESIDENT_DOCUMENT_ADDED",
            "Info",
            $"Document: {doc.Title}",
            $"Document '{doc.Title}' ({doc.Category}) added by {doc.UploadedByName}.",
            ct);

        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<RegistrationLinkGenerationResult> GenerateResidentRegistrationLinkAsync(
        Guid communityId,
        Guid unitId,
        RegistrationApprovalMode approvalMode,
        ResidentAssignmentType intendedAssignmentType,
        DateTime? expiresAtUtc,
        int maxUses,
        string? labelOrReference,
        WorkspaceSecurityRole actorRole,
        string actorName = "Property Manager",
        Guid? actorMembershipId = null,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may generate resident registration links.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(labelOrReference ?? string.Empty);
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Registration link generation blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new InvalidOperationException("Community not found.");
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == unitId && u.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Unit not found in this community.");
        var firmId = community.ManagementFirmId ?? throw new InvalidOperationException("Community is not linked to a management firm.");

        var plaintextToken = NewRegistrationPlaintextToken();
        var tokenHash = HashRegistrationToken(plaintextToken);

        var tokenRow = new ResidentRegistrationToken
        {
            ManagementFirmId = firmId,
            CommunityId = community.Id,
            BuildingId = unit.BuildingId,
            TargetUnitId = unit.Id,
            TokenHash = tokenHash,
            LabelOrReference = string.IsNullOrWhiteSpace(labelOrReference)
                ? $"Registration link for Unit {unit.UnitNumber}"
                : labelOrReference.Trim(),
            ApprovalMode = approvalMode,
            IntendedAssignmentType = intendedAssignmentType,
            IntendedRole = WorkspaceSecurityRole.Resident,
            MaxUses = Math.Clamp(maxUses, 1, 25),
            UseCount = 0,
            ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddDays(14),
            Status = RegistrationTokenStatus.Active,
            CreatedByMembershipId = actorMembershipId,
            CreatedByName = actorName,
            CreatedAtUtc = DateTime.UtcNow
        };
        db.ResidentRegistrationTokens.Add(tokenRow);

        await AppendChainedAuditByCommunityIdAsync(
            db,
            community.Id,
            actorRole,
            "REGISTRATION_LINK_GENERATED",
            "Info",
            $"Unit {unit.UnitNumber} Registration Token",
            $"Zero-disclosure registration link generated for Unit {unit.UnitNumber} (Mode: {approvalMode}, expires {tokenRow.ExpiresAtUtc:yyyy-MM-dd}).",
            ct);

        await db.SaveChangesAsync(ct);
        return new RegistrationLinkGenerationResult(
            tokenRow,
            plaintextToken,
            $"/register-resident/{plaintextToken}");
    }

    public async Task RevokeResidentRegistrationLinkAsync(
        Guid communityId,
        Guid tokenId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may revoke registration links.");

        await using var db = await CreateDbAsync(ct);
        var token = await db.ResidentRegistrationTokens
            .FirstOrDefaultAsync(t => t.Id == tokenId && t.CommunityId == communityId, ct)
            ?? throw new InvalidOperationException("Registration token not found.");
        token.Status = RegistrationTokenStatus.Revoked;
        token.RevokedAtUtc = DateTime.UtcNow;

        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "REGISTRATION_LINK_REVOKED",
            "Warning",
            $"Token {token.Id}",
            $"Registration link '{token.LabelOrReference}' revoked.",
            ct);

        await db.SaveChangesAsync(ct);
    }

    public async Task<PublicRegistrationTokenValidation> ValidateRegistrationTokenMinimalAsync(
        string? plaintextToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plaintextToken))
        {
            return new PublicRegistrationTokenValidation(
                false,
                "Registration token is missing or invalid.",
                RegistrationApprovalMode.ManualFirmApproval);
        }

        var hash = HashRegistrationToken(plaintextToken);
        await using var db = await CreateDbAsync(ct);
        var token = await db.ResidentRegistrationTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
        {
            return new PublicRegistrationTokenValidation(
                false,
                "This registration link is invalid or no longer active.",
                RegistrationApprovalMode.ManualFirmApproval);
        }

        if (token.Status != RegistrationTokenStatus.Active)
        {
            return new PublicRegistrationTokenValidation(
                false,
                "This registration link has already been used or revoked.",
                token.ApprovalMode);
        }

        if (DateTime.UtcNow > token.ExpiresAtUtc || token.UseCount >= token.MaxUses)
        {
            return new PublicRegistrationTokenValidation(
                false,
                "This registration link has expired.",
                token.ApprovalMode);
        }

        return new PublicRegistrationTokenValidation(
            true,
            "Valid CommonGround resident registration link.",
            token.ApprovalMode);
    }

    public async Task<PublicResidentRegistrationResult> SubmitPublicResidentRegistrationAsync(
        PublicResidentRegistrationInput input,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input.PlaintextToken))
            throw new InvalidOperationException("Registration token is required.");
        if (string.IsNullOrWhiteSpace(input.FirstName) && string.IsNullOrWhiteSpace(input.LastName))
            throw new InvalidOperationException("Full name is required.");
        if (string.IsNullOrWhiteSpace(input.Email) || !input.Email.Contains('@', StringComparison.Ordinal))
            throw new InvalidOperationException("A valid email address is required.");
        if (string.IsNullOrWhiteSpace(input.Password) || input.Password.Trim().Length < 8)
            throw new InvalidOperationException("A password of at least 8 characters is required.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            $"{input.FirstName} {input.LastName} {input.Email} {input.Phone} {input.EmergencyContactName} {input.ResidentNotes}");
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Registration blocked by security guard.");

        var hash = HashRegistrationToken(input.PlaintextToken);
        await using var db = await CreateDbAsync(ct);
        var token = await db.ResidentRegistrationTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new InvalidOperationException("Registration link is invalid.");

        if (token.Status != RegistrationTokenStatus.Active || DateTime.UtcNow > token.ExpiresAtUtc || token.UseCount >= token.MaxUses)
        {
            throw new InvalidOperationException("This registration link has expired or has already been used.");
        }

        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == token.CommunityId, ct)
            ?? throw new InvalidOperationException("Target community is unavailable.");
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == token.TargetUnitId && u.CommunityId == community.Id, ct)
            ?? throw new InvalidOperationException("Target unit is unavailable.");

        var (first, last, full) = NormalizeNameParts(input.FirstName, input.LastName);
        var cleanEmail = input.Email.Trim();
        var lowerEmail = cleanEmail.ToLowerInvariant();
        var cleanPhone = (input.Phone ?? string.Empty).Trim();
        var prefLang = string.IsNullOrWhiteSpace(input.PreferredLanguage) ? "EN" : input.PreferredLanguage.Trim().ToUpperInvariant();
        var passwordHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(input.Password);

        // Create or reuse User & WorkspaceUserAccount
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == lowerEmail, ct);
        if (user is null)
        {
            user = new User
            {
                FullName = full,
                Email = cleanEmail,
                Phone = cleanPhone,
                PasswordSaltAndHash = passwordHash,
                EmailConfirmed = true,
                Provider = IdentityProvider.EmailPassword
            };
            db.Users.Add(user);
        }

        var wsUser = await db.UserAccounts.FirstOrDefaultAsync(u => u.Email.ToLower() == lowerEmail, ct);
        if (wsUser is null)
        {
            wsUser = new WorkspaceUserAccount
            {
                Id = user.Id,
                CommunityId = community.Id,
                ManagementFirmId = token.ManagementFirmId,
                FirmGroupCode = community.FirmGroupCode,
                FullName = full,
                Email = cleanEmail,
                Phone = cleanPhone,
                Provider = IdentityProvider.EmailPassword,
                ProviderSubjectId = $"email-{user.Id:N}",
                PasswordSaltAndHash = passwordHash,
                Role = WorkspaceSecurityRole.Resident,
                LinkedUnitNumber = token.ApprovalMode == RegistrationApprovalMode.AutomaticAuthorized ? unit.UnitNumber : null,
                AllowedCommunityIdsCsv = community.Id.ToString(),
                AccountKind = WorkspaceAccountKind.RegularAccount,
                EmailConfirmed = true,
                AcceptedAtUtc = DateTime.UtcNow,
                GrantedByPropertyManager = token.ApprovalMode == RegistrationApprovalMode.AutomaticAuthorized
            };
            db.UserAccounts.Add(wsUser);
        }

        // Match or create ResidentProfile with permanent ResidentAccountNumber
        var profile = await db.ResidentProfiles.FirstOrDefaultAsync(
            r => r.UserId == user.Id || (r.PrimaryEmail.ToLower() == lowerEmail && (r.ManagementFirmId == null || r.ManagementFirmId == token.ManagementFirmId)),
            ct);
        if (profile is null)
        {
            var allResNumbers = await db.ResidentProfiles.AsNoTracking().Select(r => r.ResidentAccountNumber).ToListAsync(ct);
            var nextResNumber = AccountNumberGenerator.NextResidentAccountNumber(allResNumbers);
            profile = new ResidentProfile
            {
                ResidentAccountNumber = nextResNumber,
                UserId = user.Id,
                ManagementFirmId = token.ManagementFirmId,
                FirstName = first,
                LastName = last,
                FullName = full,
                PrimaryEmail = cleanEmail,
                Phone = cleanPhone,
                PreferredLanguage = prefLang,
                EmergencyContactName = (input.EmergencyContactName ?? string.Empty).Trim(),
                EmergencyContactPhone = (input.EmergencyContactPhone ?? string.Empty).Trim(),
                AccountStatus = token.ApprovalMode == RegistrationApprovalMode.AutomaticAuthorized
                    ? ResidentAccountStatus.Active
                    : ResidentAccountStatus.PendingVerification,
                VerificationStatus = token.ApprovalMode == RegistrationApprovalMode.AutomaticAuthorized
                    ? ResidentVerificationStatus.Verified
                    : ResidentVerificationStatus.PendingReview,
                RegistrationDateUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow,
                Notes = (input.ResidentNotes ?? string.Empty).Trim()
            };
            db.ResidentProfiles.Add(profile);
        }
        else
        {
            profile.UserId ??= user.Id;
            profile.ManagementFirmId ??= token.ManagementFirmId;
            if (!string.IsNullOrWhiteSpace(cleanPhone)) profile.Phone = cleanPhone;
            if (!string.IsNullOrWhiteSpace(input.EmergencyContactName))
                profile.EmergencyContactName = input.EmergencyContactName.Trim();
            if (!string.IsNullOrWhiteSpace(input.EmergencyContactPhone))
                profile.EmergencyContactPhone = input.EmergencyContactPhone.Trim();
        }

        token.UseCount++;
        if (token.UseCount >= token.MaxUses)
        {
            token.Status = RegistrationTokenStatus.Used;
        }

        var isAuto = token.ApprovalMode == RegistrationApprovalMode.AutomaticAuthorized;
        var regRequest = new ResidentRegistrationRequest
        {
            ManagementFirmId = token.ManagementFirmId,
            CommunityId = community.Id,
            BuildingId = unit.BuildingId,
            TargetUnitId = unit.Id,
            RegistrationTokenId = token.Id,
            SubmittedFirstName = first,
            SubmittedLastName = last,
            SubmittedFullName = full,
            SubmittedEmail = cleanEmail,
            SubmittedPhone = cleanPhone,
            PreferredLanguage = prefLang,
            EmergencyContactName = (input.EmergencyContactName ?? string.Empty).Trim(),
            EmergencyContactPhone = (input.EmergencyContactPhone ?? string.Empty).Trim(),
            ResidentNotes = (input.ResidentNotes ?? string.Empty).Trim(),
            UserId = user.Id,
            MatchedOrCreatedResidentProfileId = profile.Id,
            ApprovalMode = token.ApprovalMode,
            Status = isAuto ? RegistrationRequestStatus.AutoApproved : RegistrationRequestStatus.PendingApproval,
            SubmittedAtUtc = DateTime.UtcNow,
            ReviewedAtUtc = isAuto ? DateTime.UtcNow : null,
            ReviewedByName = isAuto ? "Automatic Authorized Registration Policy" : string.Empty,
            DecisionNotesOrRejectionReason = isAuto ? "Auto-approved via Mode A registration link." : string.Empty
        };
        db.ResidentRegistrationRequests.Add(regRequest);

        if (isAuto)
        {
            var assignment = new ResidentUnitAssignment
            {
                ResidentProfileId = profile.Id,
                CommunityId = community.Id,
                BuildingId = unit.BuildingId,
                UnitId = unit.Id,
                ManagementFirmId = token.ManagementFirmId,
                AssignmentType = token.IntendedAssignmentType,
                Status = UnitAssignmentStatus.Active,
                IsPrimaryResident = true,
                MoveInDateUtc = DateTime.UtcNow,
                ApprovedByName = "Automatic Authorized Registration Policy",
                AssignedAtUtc = DateTime.UtcNow,
                AssignmentReasonOrNotes = "Auto-assigned via Mode A Resident Registration Link"
            };
            db.ResidentUnitAssignments.Add(assignment);
            unit.OccupancyStatus = token.IntendedAssignmentType == ResidentAssignmentType.Tenant
                ? UnitOccupancyStatus.TenantOccupied
                : UnitOccupancyStatus.OwnerOccupied;

            await EnsureResidentPortalLinksInternalAsync(db, profile, community, unit, token.IntendedAssignmentType, ct);
        }
        else
        {
            // Create pending FirmMembership so it is visible in membership & registration queues
            var fm = await db.FirmMemberships.FirstOrDefaultAsync(
                m => m.UserId == user.Id && m.ManagementFirmId == token.ManagementFirmId, ct);
            if (fm is null)
            {
                fm = new FirmMembership
                {
                    UserId = user.Id,
                    ManagementFirmId = token.ManagementFirmId,
                    Role = WorkspaceSecurityRole.Resident,
                    Status = WorkspaceMembershipStatus.PendingApproval,
                    GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident)
                };
                db.FirmMemberships.Add(fm);
            }
        }

        await AppendChainedAuditByCommunityIdAsync(
            db,
            community.Id,
            WorkspaceSecurityRole.Resident,
            isAuto ? "RESIDENT_REGISTRATION_AUTO_APPROVED" : "RESIDENT_REGISTRATION_SUBMITTED_PENDING",
            "Info",
            $"Registration: {profile.ResidentAccountNumber}",
            isAuto
                ? $"Resident {profile.FullName} ({profile.ResidentAccountNumber}) registered and auto-assigned via Mode A link."
                : $"Resident {profile.FullName} ({profile.ResidentAccountNumber}) submitted registration for review via Mode B link.",
            ct);

        await db.SaveChangesAsync(ct);

        return new PublicResidentRegistrationResult(
            true,
            isAuto,
            profile.ResidentAccountNumber,
            isAuto
                ? $"Registration complete. Your permanent Resident Account Number is {profile.ResidentAccountNumber} and your account is active."
                : $"Registration submitted for Management Firm review. Your permanent Resident Account Number is {profile.ResidentAccountNumber}.");
    }

    public async Task<IReadOnlyList<RegistrationRequestQueueItem>> GetRegistrationRequestsQueueAsync(
        Guid firmId,
        IReadOnlyList<Guid>? allowedCommunityIds = null,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var query = db.ResidentRegistrationRequests.AsNoTracking()
            .Where(r => r.ManagementFirmId == firmId);
        if (allowedCommunityIds is not null && allowedCommunityIds.Count > 0)
        {
            query = query.Where(r => allowedCommunityIds.Contains(r.CommunityId));
        }

        var requests = await query
            .OrderBy(r => r.Status == RegistrationRequestStatus.PendingApproval ? 0 : 1)
            .ThenByDescending(r => r.SubmittedAtUtc)
            .ToListAsync(ct);
        if (requests.Count == 0) return [];

        var commIds = requests.Select(r => r.CommunityId).Distinct().ToList();
        var comms = await db.Communities.AsNoTracking().Where(c => commIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var buildings = await db.Buildings.AsNoTracking().Where(b => commIds.Contains(b.CommunityId)).ToDictionaryAsync(b => b.Id, ct);
        var units = await db.Units.AsNoTracking().Where(u => commIds.Contains(u.CommunityId)).ToDictionaryAsync(u => u.Id, ct);
        var tokens = await db.ResidentRegistrationTokens.AsNoTracking().Where(t => t.ManagementFirmId == firmId).ToDictionaryAsync(t => t.Id, ct);

        var items = new List<RegistrationRequestQueueItem>(requests.Count);
        foreach (var req in requests)
        {
            comms.TryGetValue(req.CommunityId, out var c);
            buildings.TryGetValue(req.BuildingId, out var b);
            units.TryGetValue(req.TargetUnitId, out var u);
            tokens.TryGetValue(req.RegistrationTokenId, out var t);

            var matches = await FindPotentialResidentMatchesAsync(
                firmId, req.SubmittedEmail, req.SubmittedPhone, req.SubmittedFullName, null, req.UserId, ct);
            var conflict = u is not null
                ? await CheckUnitOccupancyConflictAsync(req.CommunityId, req.TargetUnitId, t?.IntendedAssignmentType ?? ResidentAssignmentType.PrimaryResident, true, ct)
                : new UnitOccupancyConflictReport(false, false, [], []);

            items.Add(new RegistrationRequestQueueItem(
                req,
                c?.Name ?? "Community",
                c?.CommunityAccountNumber ?? "COM-00000000",
                b?.Name ?? "Building A",
                u?.UnitNumber ?? "—",
                t?.LabelOrReference ?? "Registration Link",
                matches,
                conflict));
        }

        return items;
    }

    public async Task<ResidentRegistrationRequest> ReviewRegistrationRequestAsync(
        Guid requestId,
        bool approve,
        Guid? overrideCommunityId,
        Guid? overrideUnitId,
        Guid? useExistingResidentProfileId,
        ResidentAssignmentType assignmentType,
        string decisionNotes,
        WorkspaceSecurityRole actorRole,
        string reviewerName = "Firm Administrator",
        Guid? reviewerMembershipId = null,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may review registration requests.");

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(decisionNotes ?? string.Empty);
        if (scan.IsInjectionDetected)
            throw new InvalidOperationException("Registration review blocked by security guard.");

        await using var db = await CreateDbAsync(ct);
        var req = await db.ResidentRegistrationRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw new InvalidOperationException("Registration request not found.");
        if (req.Status != RegistrationRequestStatus.PendingApproval)
            throw new InvalidOperationException("This registration request has already been decided.");

        var targetCommId = overrideCommunityId.HasValue && overrideCommunityId.Value != Guid.Empty
            ? overrideCommunityId.Value
            : req.CommunityId;
        var targetUnitId = overrideUnitId.HasValue && overrideUnitId.Value != Guid.Empty
            ? overrideUnitId.Value
            : req.TargetUnitId;

        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == targetCommId && c.ManagementFirmId == req.ManagementFirmId, ct)
            ?? throw new InvalidOperationException("Target community not found in this firm.");
        var unit = await db.Units.FirstOrDefaultAsync(u => u.Id == targetUnitId && u.CommunityId == community.Id, ct)
            ?? throw new InvalidOperationException("Target unit not found in the selected community.");

        req.CommunityId = community.Id;
        req.BuildingId = unit.BuildingId;
        req.TargetUnitId = unit.Id;
        req.ReviewedAtUtc = DateTime.UtcNow;
        req.ReviewedByMembershipId = reviewerMembershipId;
        req.ReviewedByName = reviewerName;
        req.DecisionNotesOrRejectionReason = (decisionNotes ?? string.Empty).Trim();

        ResidentProfile? profile = null;
        if (useExistingResidentProfileId.HasValue && useExistingResidentProfileId.Value != Guid.Empty)
        {
            profile = await db.ResidentProfiles.FirstOrDefaultAsync(r => r.Id == useExistingResidentProfileId.Value, ct);
        }
        if (profile is null && req.MatchedOrCreatedResidentProfileId.HasValue)
        {
            profile = await db.ResidentProfiles.FirstOrDefaultAsync(r => r.Id == req.MatchedOrCreatedResidentProfileId.Value, ct);
        }
        if (profile is null)
        {
            var allResNumbers = await db.ResidentProfiles.AsNoTracking().Select(r => r.ResidentAccountNumber).ToListAsync(ct);
            var nextResNumber = AccountNumberGenerator.NextResidentAccountNumber(allResNumbers);
            profile = new ResidentProfile
            {
                ResidentAccountNumber = nextResNumber,
                UserId = req.UserId,
                ManagementFirmId = req.ManagementFirmId,
                FirstName = req.SubmittedFirstName,
                LastName = req.SubmittedLastName,
                FullName = req.SubmittedFullName,
                PrimaryEmail = req.SubmittedEmail,
                Phone = req.SubmittedPhone,
                PreferredLanguage = req.PreferredLanguage,
                EmergencyContactName = req.EmergencyContactName,
                EmergencyContactPhone = req.EmergencyContactPhone,
                RegistrationDateUtc = req.SubmittedAtUtc,
                CreatedAtUtc = DateTime.UtcNow
            };
            db.ResidentProfiles.Add(profile);
        }

        req.MatchedOrCreatedResidentProfileId = profile.Id;

        if (!approve)
        {
            req.Status = RegistrationRequestStatus.Rejected;
            profile.VerificationStatus = ResidentVerificationStatus.Rejected;
            if (req.UserId.HasValue)
            {
                var fm = await db.FirmMemberships.FirstOrDefaultAsync(
                    m => m.UserId == req.UserId.Value && m.ManagementFirmId == req.ManagementFirmId, ct);
                if (fm is not null && fm.Status == WorkspaceMembershipStatus.PendingApproval)
                {
                    fm.Status = WorkspaceMembershipStatus.Rejected;
                    fm.DecidedAtUtc = DateTime.UtcNow;
                    fm.DecidedByMembershipId = reviewerMembershipId;
                }
            }

            await AppendChainedAuditByCommunityIdAsync(
                db,
                community.Id,
                actorRole,
                "RESIDENT_REGISTRATION_REJECTED",
                "Warning",
                $"Registration: {profile.ResidentAccountNumber}",
                $"Registration request for {req.SubmittedFullName} ({profile.ResidentAccountNumber}) rejected by {reviewerName}. Reason: {req.DecisionNotesOrRejectionReason}",
                ct);

            await db.SaveChangesAsync(ct);
            return req;
        }

        req.Status = RegistrationRequestStatus.Approved;
        profile.AccountStatus = ResidentAccountStatus.Active;
        profile.VerificationStatus = ResidentVerificationStatus.Verified;

        var assignment = new ResidentUnitAssignment
        {
            ResidentProfileId = profile.Id,
            CommunityId = community.Id,
            BuildingId = unit.BuildingId,
            UnitId = unit.Id,
            ManagementFirmId = req.ManagementFirmId,
            AssignmentType = assignmentType,
            Status = UnitAssignmentStatus.Active,
            IsPrimaryResident = true,
            MoveInDateUtc = DateTime.UtcNow,
            ApprovedByMembershipId = reviewerMembershipId,
            ApprovedByName = reviewerName,
            AssignedAtUtc = DateTime.UtcNow,
            AssignmentReasonOrNotes = string.IsNullOrWhiteSpace(req.DecisionNotesOrRejectionReason)
                ? "Approved in Registration Request Queue"
                : req.DecisionNotesOrRejectionReason
        };
        db.ResidentUnitAssignments.Add(assignment);

        unit.OccupancyStatus = assignmentType == ResidentAssignmentType.Tenant
            ? UnitOccupancyStatus.TenantOccupied
            : UnitOccupancyStatus.OwnerOccupied;

        await EnsureResidentPortalLinksInternalAsync(db, profile, community, unit, assignmentType, ct);

        await AppendChainedAuditByCommunityIdAsync(
            db,
            community.Id,
            actorRole,
            "RESIDENT_REGISTRATION_APPROVED",
            "Info",
            $"Unit {unit.UnitNumber} <- {profile.ResidentAccountNumber}",
            $"Registration request for {profile.FullName} ({profile.ResidentAccountNumber}) approved and assigned to Unit {unit.UnitNumber} ({community.CommunityAccountNumber}) by {reviewerName}.",
            ct);

        await db.SaveChangesAsync(ct);
        return req;
    }

    private static async Task EnsureResidentPortalLinksInternalAsync(
        IAppDbContext db,
        ResidentProfile profile,
        Community community,
        Unit unit,
        ResidentAssignmentType assignmentType,
        CancellationToken ct)
    {
        if (!community.ManagementFirmId.HasValue) return;
        var firmId = community.ManagementFirmId.Value;

        User? user = null;
        if (profile.UserId.HasValue && profile.UserId.Value != Guid.Empty)
        {
            user = await db.Users.FirstOrDefaultAsync(u => u.Id == profile.UserId.Value, ct);
            if (user is not null && !string.IsNullOrWhiteSpace(profile.PrimaryEmail)
                && !string.IsNullOrWhiteSpace(user.Email)
                && !string.Equals(user.Email.Trim(), profile.PrimaryEmail.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // Stored UserId belongs to a different person (for example a manager identity).
                // Never reuse that User for a resident portal; fall through to email match or new User.
                user = null;
            }
        }
        if (user is null && !string.IsNullOrWhiteSpace(profile.PrimaryEmail))
        {
            var lower = profile.PrimaryEmail.Trim().ToLowerInvariant();
            user = await db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == lower, ct);
        }
        if (user is null && !string.IsNullOrWhiteSpace(profile.PrimaryEmail))
        {
            user = new User
            {
                FullName = profile.FullName,
                Email = profile.PrimaryEmail.Trim(),
                Phone = profile.Phone,
                PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("Demo-Password-42!"),
                EmailConfirmed = true,
                Provider = IdentityProvider.EmailPassword
            };
            db.Users.Add(user);
            profile.UserId = user.Id;
        }
        if (user is null) return;

        profile.UserId = user.Id;

        var membership = await db.FirmMemberships
            .Where(m => m.UserId == user.Id && m.ManagementFirmId == firmId)
            .OrderByDescending(m => m.Role == WorkspaceSecurityRole.Resident)
            .ThenByDescending(m => m.Status == WorkspaceMembershipStatus.Approved)
            .FirstOrDefaultAsync(ct);
        // Never attach a resident unit link to a FirmManager membership.
        // Resident portal access always uses a Resident-role membership, even when the
        // same User also holds a FirmManager membership in this firm.
        if (membership is not null && membership.Role == WorkspaceSecurityRole.FirmManager)
        {
            membership = await db.FirmMemberships.FirstOrDefaultAsync(
                m => m.UserId == user.Id && m.ManagementFirmId == firmId && m.Role == WorkspaceSecurityRole.Resident, ct);
        }
        if (membership is null)
        {
            membership = new FirmMembership
            {
                UserId = user.Id,
                ManagementFirmId = firmId,
                Role = WorkspaceSecurityRole.Resident,
                Status = WorkspaceMembershipStatus.Approved,
                GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident),
                DecidedAtUtc = DateTime.UtcNow
            };
            db.FirmMemberships.Add(membership);
        }
        else if (membership.Status == WorkspaceMembershipStatus.PendingApproval)
        {
            membership.Status = WorkspaceMembershipStatus.Approved;
            membership.DecidedAtUtc = DateTime.UtcNow;
        }

        var hasPropAccess = await db.MembershipPropertyAccesses.AnyAsync(
            a => a.FirmMembershipId == membership.Id && a.CommunityId == community.Id, ct);
        if (!hasPropAccess)
        {
            db.MembershipPropertyAccesses.Add(new MembershipPropertyAccess
            {
                FirmMembershipId = membership.Id,
                CommunityId = community.Id,
                IsPrimaryManager = false
            });
        }

        var hasUnitLink = await db.MembershipUnitLinks.AnyAsync(
            l => l.FirmMembershipId == membership.Id && l.UnitId == unit.Id, ct);
        if (!hasUnitLink)
        {
            var relKind = assignmentType switch
            {
                ResidentAssignmentType.Tenant => MembershipUnitRelationshipKind.Tenant,
                ResidentAssignmentType.OwnerResident => MembershipUnitRelationshipKind.Owner,
                ResidentAssignmentType.CoResident => MembershipUnitRelationshipKind.CoOwner,
                _ => MembershipUnitRelationshipKind.Occupant
            };
            db.MembershipUnitLinks.Add(new MembershipUnitLink
            {
                FirmMembershipId = membership.Id,
                CommunityId = community.Id,
                UnitId = unit.Id,
                RelationshipKind = relKind
            });
        }

        var wsUser = await db.UserAccounts.FirstOrDefaultAsync(
            u => u.Id == user.Id || (!string.IsNullOrWhiteSpace(user.Email) && u.Email.ToLower() == user.Email.ToLower()), ct);
        if (wsUser is not null)
        {
            wsUser.CommunityId ??= community.Id;
            wsUser.LinkedUnitNumber = unit.UnitNumber;
            wsUser.GrantedByPropertyManager = true;
        }
    }
}
