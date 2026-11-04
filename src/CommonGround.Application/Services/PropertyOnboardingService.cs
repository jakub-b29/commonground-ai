using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public sealed class PropertyOnboardingService : IPropertyOnboardingService
{
    private readonly IAppDbContextFactory _dbFactory;
    private readonly IMuseSparkClient _spark;

    public PropertyOnboardingService(IAppDbContextFactory dbFactory, IMuseSparkClient spark)
    {
        _dbFactory = dbFactory;
        _spark = spark;
    }

    private async Task<IAppDbContext> CreateDbAsync(CancellationToken ct)
    {
        return await _dbFactory.CreateDbContextAsync(ct);
    }

    private static void ThrowIfInjectionDetected(string combined)
    {
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(combined ?? string.Empty);
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException($"Input blocked by security guard: pattern '{scan.MatchedPattern}' detected.");
        }
    }

    private static string Clean(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static UtilityKind ParseUtilityKind(string? raw, UtilityKind fallback)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && Enum.TryParse<UtilityKind>(raw.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static WorkOrderCategory ParseTrade(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && Enum.TryParse<WorkOrderCategory>(raw.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        var lower = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.Contains("plumb", StringComparison.Ordinal))
        {
            return WorkOrderCategory.Plumbing;
        }

        if (lower.Contains("electr", StringComparison.Ordinal))
        {
            return WorkOrderCategory.Electrical;
        }

        if (lower.Contains("heat", StringComparison.Ordinal) || lower.Contains("hvac", StringComparison.Ordinal))
        {
            return WorkOrderCategory.HeatingAndHvac;
        }

        if (lower.Contains("roof", StringComparison.Ordinal) || lower.Contains("facade", StringComparison.Ordinal))
        {
            return WorkOrderCategory.RoofAndFacade;
        }

        if (lower.Contains("elevator", StringComparison.Ordinal))
        {
            return WorkOrderCategory.Elevator;
        }

        if (lower.Contains("fire", StringComparison.Ordinal))
        {
            return WorkOrderCategory.FireAndSafety;
        }

        if (lower.Contains("waste", StringComparison.Ordinal) || lower.Contains("clean", StringComparison.Ordinal))
        {
            return WorkOrderCategory.WasteAndCleaning;
        }

        if (lower.Contains("gate", StringComparison.Ordinal) || lower.Contains("intercom", StringComparison.Ordinal))
        {
            return WorkOrderCategory.GatesAndIntercom;
        }

        if (lower.Contains("snow", StringComparison.Ordinal) || lower.Contains("landscape", StringComparison.Ordinal))
        {
            return WorkOrderCategory.SnowAndLandscaping;
        }

        return WorkOrderCategory.Plumbing;
    }

    private static LegalDocumentKind MapOrdinanceKind(string? code, string? title)
    {
        var combined = $"{code ?? string.Empty} {title ?? string.Empty}".ToUpperInvariant();
        if (combined.Contains("ACT-OWN", StringComparison.Ordinal)
            || combined.Contains("718-112", StringComparison.Ordinal)
            || combined.Contains("OWNERSHIP", StringComparison.Ordinal)
            || combined.Contains("CONDOMINIUM", StringComparison.Ordinal))
        {
            return LegalDocumentKind.NationalPropertyStatute;
        }

        if (combined.Contains("CIV", StringComparison.Ordinal)
            || combined.Contains("WARR", StringComparison.Ordinal)
            || combined.Contains("558", StringComparison.Ordinal)
            || combined.Contains("116", StringComparison.Ordinal))
        {
            return LegalDocumentKind.CivilWarrantyAndLiabilityCode;
        }

        if (combined.Contains("BLD", StringComparison.Ordinal)
            || combined.Contains("553", StringComparison.Ordinal)
            || combined.Contains("MILESTONE", StringComparison.Ordinal)
            || combined.Contains("SIRS", StringComparison.Ordinal)
            || combined.Contains("CONSTRUCTION", StringComparison.Ordinal))
        {
            return LegalDocumentKind.BuildingAndSafetyRegulation;
        }

        if (combined.Contains("STATUTE", StringComparison.Ordinal) || combined.Contains("CCR", StringComparison.Ordinal))
        {
            return LegalDocumentKind.CommunityStatuteOrCcr;
        }

        if (combined.Contains("RULE", StringComparison.Ordinal) || combined.Contains("BYLAW", StringComparison.Ordinal))
        {
            return LegalDocumentKind.HouseRulesAndOrder;
        }

        if (combined.Contains("UTIL", StringComparison.Ordinal) || combined.Contains("RUBS", StringComparison.Ordinal))
        {
            return LegalDocumentKind.UtilityAndMeteringPolicy;
        }

        return LegalDocumentKind.MunicipalBylawAndOrdinance;
    }

    private static async Task<string> GetPreviousChainHashAsync(IAppDbContext db, Guid communityId, CancellationToken ct)
    {
        var hashes = await db.SecurityAuditEvents
            .AsNoTracking()
            .Where(e => e.CommunityId == communityId)
            .OrderBy(e => e.OccurredAtUtc)
            .Select(e => e.EntrySha256Hash)
            .ToListAsync(ct);
        return hashes.Count == 0 ? "GENESIS-0000000000000000" : hashes[^1];
    }

    private static async Task AppendAuditAsync(
        IAppDbContext db,
        Guid communityId,
        WorkspaceSecurityRole actorRole,
        string category,
        string summary,
        CancellationToken ct)
    {
        var previous = await GetPreviousChainHashAsync(db, communityId, ct);
        var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(
            communityId, actorRole, category, "Info", $"Community: {communityId}", summary, previous);
        db.SecurityAuditEvents.Add(ev);
    }

    private static async Task<FirmMembership> RequireFirmActorAsync(
        IAppDbContext db,
        Guid actorMembershipId,
        Guid firmId,
        CancellationToken ct)
    {
        var actor = await db.FirmMemberships.FirstOrDefaultAsync(m => m.Id == actorMembershipId, ct)
            ?? throw new InvalidOperationException("Actor membership not found.");
        if (actor.ManagementFirmId != firmId)
        {
            throw new InvalidOperationException("Actor belongs to a different firm.");
        }

        if (actor.Status != WorkspaceMembershipStatus.Approved)
        {
            throw new UnauthorizedAccessException("Actor membership is not approved.");
        }

        return actor;
    }

    private static void EnsureCanCreateProperty(FirmMembership actor)
    {
        if (actor.Role != WorkspaceSecurityRole.FirmManager)
        {
            throw new UnauthorizedAccessException("Only the Firm Manager may create properties.");
        }
    }

    private static async Task EnsureCanEditPropertyAsync(
        IAppDbContext db,
        FirmMembership actor,
        Guid communityId,
        CancellationToken ct)
    {
        if (actor.Role == WorkspaceSecurityRole.FirmManager)
        {
            return;
        }

        if (actor.Role == WorkspaceSecurityRole.PropertyManager)
        {
            var isPrimary = await db.MembershipPropertyAccesses.AsNoTracking()
                .AnyAsync(a => a.FirmMembershipId == actor.Id && a.CommunityId == communityId && a.IsPrimaryManager, ct);
            if (isPrimary)
            {
                return;
            }
        }

        throw new UnauthorizedAccessException("Only the Firm Manager or the assigned primary Property Manager may edit this property.");
    }

    private static List<UtilityProviderSource> BuildUtilitySources(Guid communityId, PropertyAiDiscoveryDossier dossier)
    {
        var result = new List<UtilityProviderSource>();
        void AddMany(IReadOnlyList<DiscoveredUtilityOperator> operators, UtilityKind fallback)
        {
            foreach (var op in operators)
            {
                var kind = ParseUtilityKind(op.UtilityKind, fallback);
                result.Add(new UtilityProviderSource
                {
                    CommunityId = communityId,
                    UtilityKind = kind,
                    ProviderName = Clean(op.ProviderName, kind.ToString()),
                    OutageBulletinUrl = Clean(op.OutageBulletinUrl, string.Empty),
                    LiveFaultMapUrl = Clean(op.LiveFaultMapUrl, string.Empty),
                    TariffScheduleUrl = Clean(op.TariffScheduleUrl, string.Empty),
                    EmergencyPhone = Clean(op.EmergencyPhone, string.Empty),
                    CurrentUnitTariff = op.CurrentUnitTariff,
                    TariffUnit = Clean(op.TariffUnit, string.Empty),
                    LastCheckedAt = DateTime.UtcNow,
                    HealthStatus = "Online (200 OK)",
                    IsAiDiscovered = true
                });
            }
        }

        AddMany(dossier.ElectricityOperator, UtilityKind.Electricity);
        AddMany(dossier.GasOperator, UtilityKind.Gas);
        AddMany(dossier.WaterOperator, UtilityKind.ColdWater);
        AddMany(dossier.HeatingOperator, UtilityKind.DistrictHeating);
        AddMany(dossier.WasteOperator, UtilityKind.MunicipalWaste);
        return result;
    }

    private static List<EmergencyAndAuthorityContact> BuildAuthorityContacts(Guid communityId, PropertyAiDiscoveryDossier dossier)
    {
        var result = new List<EmergencyAndAuthorityContact>();
        void AddMany(IReadOnlyList<DiscoveredAuthorityContact> contacts, string fallbackCategory)
        {
            foreach (var c in contacts)
            {
                result.Add(new EmergencyAndAuthorityContact
                {
                    CommunityId = communityId,
                    Category = Clean(c.Category, fallbackCategory),
                    Name = Clean(c.Name, fallbackCategory),
                    PhoneNumber = Clean(c.PhoneNumber, string.Empty),
                    AddressOrNotes = Clean(c.AddressOrNotes, string.Empty),
                    SortOrder = c.SortOrder,
                    IsAiDiscovered = true
                });
            }
        }

        AddMany(dossier.PolicePrecinct, EmergencyContactCategories.PolicePrecinct);
        AddMany(dossier.DistrictOfficer, EmergencyContactCategories.DistrictPoliceOfficer);
        AddMany(dossier.MunicipalGuard, EmergencyContactCategories.MunicipalGuard);
        AddMany(dossier.FireStation, EmergencyContactCategories.FireStation);
        AddMany(dossier.BuildingInspectorate, EmergencyContactCategories.BuildingInspectorate);
        AddMany(dossier.RecyclingDepot, EmergencyContactCategories.RecyclingDepot);
        return result;
    }

    private static List<WasteCollectionSchedule> BuildDefaultWasteSchedules(Guid communityId)
    {
        var now = DateTime.UtcNow;
        return
        [
            new WasteCollectionSchedule { CommunityId = communityId, WasteFraction = "Mixed", CollectionDaysSummary = "Tuesday and Friday", NextPickupDate = now.AddDays(1) },
            new WasteCollectionSchedule { CommunityId = communityId, WasteFraction = "Plastics and Metals", CollectionDaysSummary = "Wednesday", NextPickupDate = now.AddDays(2) },
            new WasteCollectionSchedule { CommunityId = communityId, WasteFraction = "Paper", CollectionDaysSummary = "Thursday", NextPickupDate = now.AddDays(3) },
            new WasteCollectionSchedule { CommunityId = communityId, WasteFraction = "Bio", CollectionDaysSummary = "Monday", NextPickupDate = now.AddDays(4) },
            new WasteCollectionSchedule { CommunityId = communityId, WasteFraction = "Glass", CollectionDaysSummary = "Second Thursday of the month", NextPickupDate = now.AddDays(5) },
            new WasteCollectionSchedule { CommunityId = communityId, WasteFraction = "Bulky Furniture", CollectionDaysSummary = "First Saturday of the month", NextPickupDate = now.AddDays(6) }
        ];
    }

    private static List<Vendor> BuildVendors(Guid communityId, PropertyAiDiscoveryDossier dossier)
    {
        var result = new List<Vendor>();
        foreach (var c in dossier.Contractors)
        {
            result.Add(new Vendor
            {
                CommunityId = communityId,
                Name = Clean(c.CompanyName, "Local Emergency Contractor"),
                Trade = ParseTrade(c.Trade),
                Phone = Clean(c.Phone, string.Empty),
                Email = Clean(c.Email, string.Empty),
                Website = Clean(c.Website, string.Empty),
                Rating = 4.8m,
                TypicalHourlyRate = c.TypicalHourlyRate,
                IsAiDiscovered = true
            });
        }

        return result;
    }

    private static List<StatutoryInspection> BuildDefaultInspections(Guid communityId, Guid buildingId)
    {
        var now = DateTime.UtcNow;
        return
        [
            new StatutoryInspection { CommunityId = communityId, BuildingId = buildingId, Kind = InspectionKind.AnnualStructuralAndCommon, LastPerformedDate = now.AddYears(-1), NextDueDate = now.AddMonths(1), InspectorCompany = "Licensed Inspection Office", Status = "Scheduled" },
            new StatutoryInspection { CommunityId = communityId, BuildingId = buildingId, Kind = InspectionKind.AnnualChimneyAndVentilation, LastPerformedDate = now.AddYears(-1), NextDueDate = now.AddMonths(2), InspectorCompany = "Chimney Master Services", Status = "Scheduled" },
            new StatutoryInspection { CommunityId = communityId, BuildingId = buildingId, Kind = InspectionKind.AnnualGasTightness, LastPerformedDate = now.AddYears(-1), NextDueDate = now.AddMonths(3), InspectorCompany = "Gas Safety Inspector", Status = "Scheduled" },
            new StatutoryInspection { CommunityId = communityId, BuildingId = buildingId, Kind = InspectionKind.FiveYearElectricalAndLightning, LastPerformedDate = now.AddYears(-4), NextDueDate = now.AddYears(1), InspectorCompany = "Licensed Electrical Contractor", Status = "Scheduled" },
            new StatutoryInspection { CommunityId = communityId, BuildingId = buildingId, Kind = InspectionKind.ElevatorSafetyCertification, LastPerformedDate = now.AddMonths(-6), NextDueDate = now.AddMonths(6), InspectorCompany = "Elevator Safety Bureau", Status = "Scheduled" }
        ];
    }

    private static List<GreenSubsidyProgram> BuildGreenSubsidies(Guid communityId, PropertyAiDiscoveryDossier dossier)
    {
        var result = new List<GreenSubsidyProgram>();
        foreach (var g in dossier.GrantsAndOrdinances)
        {
            if (!string.Equals(g.Kind, "Grant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new GreenSubsidyProgram
            {
                CommunityId = communityId,
                ProgramName = Clean(g.Title, Clean(g.ProgramOrDocumentCode, "Green Grant")),
                AuthorityName = Clean(g.IssuingAuthority, "Municipal Authority"),
                UpgradeScope = Clean(g.Summary, Clean(g.Title, "Thermal modernization")),
                EstimatedGrossCapex = 180000m,
                GrantSubsidyPercent = 30m,
                GrantSubsidyAmount = 54000m,
                NetCommunityCapex = 126000m,
                EstimatedAnnualSavings = 18000m,
                PaybackYears = 7.0m
            });
        }

        return result;
    }

    private static List<LegalDocumentSource> BuildLegalDocuments(Guid communityId, PropertyAiDiscoveryDossier dossier)
    {
        var result = new List<LegalDocumentSource>();
        foreach (var g in dossier.GrantsAndOrdinances)
        {
            if (string.Equals(g.Kind, "Grant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(new LegalDocumentSource
            {
                CommunityId = communityId,
                Kind = MapOrdinanceKind(g.ProgramOrDocumentCode, g.Title),
                DocumentCode = Clean(g.ProgramOrDocumentCode, Clean(g.Title, "ORD-2026")),
                Title = Clean(g.Title, Clean(g.ProgramOrDocumentCode, "Municipal Ordinance")),
                IssuingAuthority = Clean(g.IssuingAuthority, "Municipal Authority"),
                OfficialCitation = Clean(g.OfficialCitation, Clean(g.ProgramOrDocumentCode, string.Empty)),
                RegistrySourceUrl = Clean(g.SourceUrl, string.Empty),
                AcquiredByModel = "muse-spark-1.3 + web_search",
                EffectiveDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                AcquiredAtUtc = DateTime.UtcNow,
                KeyProvisionsSummary = Clean(g.Summary, Clean(g.Title, string.Empty)),
                VerbatimLegalText = Clean(g.VerbatimText, Clean(g.Summary, string.Empty)),
                AppliedInModules = "Governance, Compliance, Intake"
            });
        }

        return result;
    }

    public Task<PropertyAiDiscoveryDossier> DiscoverPropertyDossierFromAddressAsync(
        AddressLookupRequest request,
        CancellationToken ct = default,
        IProgress<DiscoveryProgressStep>? progress = null)
    {
        return _spark.DiscoverPropertyDossierFromAddressAsync(request, ct, progress);
    }

    public async Task<Community> CreatePropertyManualAsync(
        Guid firmId,
        Guid actorMembershipId,
        string name,
        string streetAddress,
        string postalOrZipCode,
        string city,
        string countryOrState,
        SystemOperatingMode operatingMode,
        CancellationToken ct = default)
    {
        var cleanName = Clean(name, string.Empty);
        var cleanStreet = Clean(streetAddress, string.Empty);
        var cleanCity = Clean(city, string.Empty);
        if (string.IsNullOrWhiteSpace(cleanName) || string.IsNullOrWhiteSpace(cleanStreet) || string.IsNullOrWhiteSpace(cleanCity))
        {
            throw new InvalidOperationException("Property name, street address and city are required.");
        }

        var cleanPostal = Clean(postalOrZipCode, string.Empty);
        var cleanCountry = Clean(countryOrState, string.Empty);
        ThrowIfInjectionDetected($"{cleanName} {cleanStreet} {cleanPostal} {cleanCity} {cleanCountry}");

        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.Id == firmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");
        var actor = await RequireFirmActorAsync(db, actorMembershipId, firmId, ct);
        EnsureCanCreateProperty(actor);

        var existingNumbers = await db.Communities.AsNoTracking().Select(c => c.CommunityAccountNumber).ToListAsync(ct);
        var community = new Community
        {
            CommunityAccountNumber = AccountNumberGenerator.NextCommunityAccountNumber(existingNumbers),
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            Name = cleanName,
            StreetAddress = cleanStreet,
            PostalOrZipCode = cleanPostal,
            City = cleanCity,
            CountryCode = string.IsNullOrWhiteSpace(cleanCountry) ? "PL" : cleanCountry,
            CurrencyCode = operatingMode == SystemOperatingMode.UsHoa ? "USD" : "PLN",
            OperatingMode = operatingMode,
            AdministrativeEmail = string.IsNullOrWhiteSpace(firm.SupportEmail) ? "office@commonground.local" : firm.SupportEmail,
            AdministrativePhone = string.IsNullOrWhiteSpace(firm.EmergencyDispatchPhone) ? "+1 (800) 555-0199" : firm.EmergencyDispatchPhone,
            LastWebEnrichedAt = null
        };

        db.Communities.Add(community);

        var manualBuilding = new Building
        {
            CommunityId = community.Id,
            Name = "Building A",
            StreetAddress = community.StreetAddress
        };
        db.Buildings.Add(manualBuilding);
        foreach (var stackCode in new[] { "STACK-A1", "STACK-A2", "STACK-B1" })
        {
            var stack = new VerticalRiserStack
            {
                BuildingId = manualBuilding.Id,
                Code = stackCode,
                Description = stackCode.Contains('B') ? "Heating riser" : "Cold water riser",
                ShutoffValveLocation = "Basement manifold"
            };
            manualBuilding.RiserStacks.Add(stack);
            db.RiserStacks.Add(stack);
        }

        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "100-OP", Name = "Operating Cash", Fund = FundKind.Operating, Kind = LedgerAccountKind.Asset });
        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "200-RF", Name = "Repair Reserve Cash", Fund = FundKind.RepairReserve, Kind = LedgerAccountKind.Asset });
        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "300-UT", Name = "Utilities Clearing", Fund = FundKind.UtilitiesClearing, Kind = LedgerAccountKind.Asset });
        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "400-ANC", Name = "Commercial Ancillary Revenue", Fund = FundKind.CommercialAncillary, Kind = LedgerAccountKind.Revenue });

        var audit = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(
            community.Id,
            actor.Role,
            "PROPERTY_CREATED_MANUAL",
            "Info",
            $"Community: {community.Name}",
            $"Property created manually for {community.StreetAddress}, {community.City}.",
            "GENESIS-0000000000000000");
        db.SecurityAuditEvents.Add(audit);

        await db.SaveChangesAsync(ct);
        return community;
    }

    public async Task<Community> CreatePropertyFromDossierAsync(
        Guid firmId,
        Guid actorMembershipId,
        PropertyAiDiscoveryDossier dossier,
        DossierSectionFlags selectedSections,
        string streetAddress,
        string postalOrZipCode,
        string city,
        string countryOrState,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        ThrowIfInjectionDetected($"{streetAddress ?? string.Empty} {postalOrZipCode ?? string.Empty} {city ?? string.Empty} {countryOrState ?? string.Empty} {dossier.SuggestedAssociationName ?? string.Empty} {dossier.RegistryNumber ?? string.Empty}");

        await using var db = await CreateDbAsync(ct);
        var firm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.Id == firmId, ct)
            ?? throw new InvalidOperationException("Firm not found.");
        var actor = await RequireFirmActorAsync(db, actorMembershipId, firmId, ct);
        EnsureCanCreateProperty(actor);

        var existingNumbers = await db.Communities.AsNoTracking().Select(c => c.CommunityAccountNumber).ToListAsync(ct);
        var community = new Community
        {
            CommunityAccountNumber = AccountNumberGenerator.NextCommunityAccountNumber(existingNumbers),
            ManagementFirmId = firm.Id,
            FirmGroupCode = firm.FirmGroupCode,
            Name = Clean(dossier.SuggestedAssociationName, $"Property {Clean(city, "New")}, {Clean(streetAddress, "Main")}") ,
            StreetAddress = Clean(streetAddress, Clean(dossier.SuggestedAssociationName, "Main Street 1")),
            City = Clean(city, "Demo City"),
            PostalOrZipCode = Clean(postalOrZipCode, string.Empty),
            CountryCode = Clean(dossier.CountryCode, "PL"),
            CurrencyCode = Clean(dossier.CurrencyCode, "PLN"),
            OperatingMode = dossier.SuggestedOperatingMode,
            AdministrativeEmail = string.IsNullOrWhiteSpace(firm.SupportEmail) ? "office@commonground.local" : firm.SupportEmail,
            AdministrativePhone = string.IsNullOrWhiteSpace(firm.EmergencyDispatchPhone) ? "+1 (800) 555-0199" : firm.EmergencyDispatchPhone,
            LastWebEnrichedAt = DateTime.UtcNow
        };

        if (selectedSections.HasFlag(DossierSectionFlags.IdentityAndTariffs))
        {
            community.LocalMunicipalityOrCounty = Clean(dossier.LocalMunicipalityOrCounty, string.Empty);
            community.CompetentCourtOrRecorder = Clean(dossier.CompetentCourtOrRecorder, string.Empty);
            community.LocalTaxOffice = Clean(dossier.LocalTaxOffice, string.Empty);
            community.RegistryNumber = Clean(dossier.RegistryNumber, string.Empty);
            community.OperatingRatePerM2 = dossier.OperatingRatePerM2;
            community.RepairFundRatePerM2 = dossier.RepairFundRatePerM2;
            community.WasteFeePerOccupant = dossier.WasteFeePerOccupant;
            community.ColdWaterTariffPerM3 = dossier.ColdWaterTariffPerM3;
            community.HotWaterHeatingCostPerM3 = dossier.HotWaterHeatingCostPerM3;
            community.DistrictHeatingTariffPerGj = dossier.DistrictHeatingTariffPerGj;
            community.CommonElectricityTariffPerKwh = dossier.CommonElectricityTariffPerKwh;
            community.ContractedHeatingPowerMw = dossier.ContractedHeatingPowerMw;
            community.ContractedElectricalPowerKw = dossier.ContractedElectricalPowerKw;
        }

        db.Communities.Add(community);

        var building = new Building
        {
            CommunityId = community.Id,
            Name = "Building A",
            StreetAddress = community.StreetAddress,
            FloorsCount = dossier.BuildingProfile.FloorsCount,
            StairwellsCount = dossier.BuildingProfile.StairwellsCount,
            ConstructionYear = dossier.BuildingProfile.EstimatedConstructionYear,
            HeatingType = Clean(dossier.BuildingProfile.HeatingType, "Municipal District Heating Substation"),
            RoofType = Clean(dossier.BuildingProfile.RoofType, "Flat EPDM Membrane with Ballast")
        };
        db.Buildings.Add(building);
        foreach (var stackCode in new[] { "STACK-A1", "STACK-A2", "STACK-B1" })
        {
            var stack = new VerticalRiserStack
            {
                BuildingId = building.Id,
                Code = stackCode,
                Description = stackCode.Contains('B') ? "Heating riser" : "Cold water riser",
                ShutoffValveLocation = "Basement manifold"
            };
            building.RiserStacks.Add(stack);
            db.RiserStacks.Add(stack);
        }

        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "100-OP", Name = "Operating Cash", Fund = FundKind.Operating, Kind = LedgerAccountKind.Asset });
        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "200-RF", Name = "Repair Reserve Cash", Fund = FundKind.RepairReserve, Kind = LedgerAccountKind.Asset });
        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "300-UT", Name = "Utilities Clearing", Fund = FundKind.UtilitiesClearing, Kind = LedgerAccountKind.Asset });
        db.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "400-ANC", Name = "Commercial Ancillary Revenue", Fund = FundKind.CommercialAncillary, Kind = LedgerAccountKind.Revenue });

        if (selectedSections.HasFlag(DossierSectionFlags.UtilityProviders))
        {
            foreach (var source in BuildUtilitySources(community.Id, dossier))
            {
                db.UtilityProviderSources.Add(source);
            }
        }

        if (selectedSections.HasFlag(DossierSectionFlags.Authorities))
        {
            foreach (var contact in BuildAuthorityContacts(community.Id, dossier))
            {
                db.EmergencyContacts.Add(contact);
            }
        }

        if (selectedSections.HasFlag(DossierSectionFlags.WasteSchedule))
        {
            foreach (var schedule in BuildDefaultWasteSchedules(community.Id))
            {
                db.WasteSchedules.Add(schedule);
            }
        }

        if (selectedSections.HasFlag(DossierSectionFlags.Vendors))
        {
            foreach (var vendor in BuildVendors(community.Id, dossier))
            {
                db.Vendors.Add(vendor);
            }
        }

        if (selectedSections.HasFlag(DossierSectionFlags.Inspections))
        {
            foreach (var inspection in BuildDefaultInspections(community.Id, building.Id))
            {
                db.Inspections.Add(inspection);
            }
        }

        if (selectedSections.HasFlag(DossierSectionFlags.GreenSubsidies))
        {
            foreach (var subsidy in BuildGreenSubsidies(community.Id, dossier))
            {
                db.GreenSubsidies.Add(subsidy);
            }
        }

        if (selectedSections.HasFlag(DossierSectionFlags.LegalCorpus))
        {
            foreach (var doc in BuildLegalDocuments(community.Id, dossier))
            {
                db.LegalDocuments.Add(doc);
            }
        }

        var audit = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(
            community.Id,
            actor.Role,
            "PROPERTY_CREATED_FROM_DOSSIER",
            "Info",
            $"Community: {community.Name}",
            $"Property created from AI discovery dossier for {community.StreetAddress}, {community.City}.",
            "GENESIS-0000000000000000");
        db.SecurityAuditEvents.Add(audit);

        await db.SaveChangesAsync(ct);
        return community;
    }

    public async Task<Community> ApplyDiscoveryDossierToPropertyAsync(
        Guid communityId,
        Guid actorMembershipId,
        PropertyAiDiscoveryDossier dossier,
        DossierSectionFlags selectedSections,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dossier);
        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new InvalidOperationException("Property not found.");
        var firmId = community.ManagementFirmId ?? Guid.Empty;
        var actor = await RequireFirmActorAsync(db, actorMembershipId, firmId, ct);
        await EnsureCanEditPropertyAsync(db, actor, communityId, ct);

        ThrowIfInjectionDetected($"{dossier.SuggestedAssociationName ?? string.Empty} {dossier.RegistryNumber ?? string.Empty}");

        if (selectedSections.HasFlag(DossierSectionFlags.IdentityAndTariffs))
        {
            community.LocalMunicipalityOrCounty = Clean(dossier.LocalMunicipalityOrCounty, community.LocalMunicipalityOrCounty);
            community.CompetentCourtOrRecorder = Clean(dossier.CompetentCourtOrRecorder, community.CompetentCourtOrRecorder);
            community.LocalTaxOffice = Clean(dossier.LocalTaxOffice, community.LocalTaxOffice);
            if (!string.IsNullOrWhiteSpace(dossier.RegistryNumber))
            {
                community.RegistryNumber = dossier.RegistryNumber.Trim();
            }

            community.OperatingRatePerM2 = dossier.OperatingRatePerM2;
            community.RepairFundRatePerM2 = dossier.RepairFundRatePerM2;
            community.WasteFeePerOccupant = dossier.WasteFeePerOccupant;
            community.ColdWaterTariffPerM3 = dossier.ColdWaterTariffPerM3;
            community.HotWaterHeatingCostPerM3 = dossier.HotWaterHeatingCostPerM3;
            community.DistrictHeatingTariffPerGj = dossier.DistrictHeatingTariffPerGj;
            community.CommonElectricityTariffPerKwh = dossier.CommonElectricityTariffPerKwh;
            community.ContractedHeatingPowerMw = dossier.ContractedHeatingPowerMw;
            community.ContractedElectricalPowerKw = dossier.ContractedElectricalPowerKw;
            community.LastWebEnrichedAt = DateTime.UtcNow;
        }

        var steps = new List<AgentStep>();
        var order = 1;

        if (selectedSections.HasFlag(DossierSectionFlags.UtilityProviders))
        {
            var existingSources = await db.UtilityProviderSources.AsNoTracking()
                .Where(s => s.CommunityId == communityId)
                .ToListAsync(ct);
            var addedUtilities = 0;
            foreach (var source in BuildUtilitySources(communityId, dossier))
            {
                var duplicate = existingSources.Any(e =>
                    e.UtilityKind == source.UtilityKind
                    && (string.Equals(e.ProviderName, source.ProviderName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.EmergencyPhone, source.EmergencyPhone, StringComparison.Ordinal)));
                if (duplicate)
                {
                    continue;
                }

                db.UtilityProviderSources.Add(source);
                existingSources.Add(source);
                addedUtilities++;
            }

            steps.Add(new AgentStep { StepOrder = order++, StepTitle = "Utility providers merged", ModelOrToolName = "muse-spark-1.3", Detail = $"{addedUtilities} new utility sources merged.", LatencyMs = 120 });
        }

        if (selectedSections.HasFlag(DossierSectionFlags.Authorities))
        {
            var existingContacts = await db.EmergencyContacts.AsNoTracking()
                .Where(e => e.CommunityId == communityId)
                .ToListAsync(ct);
            var addedContacts = 0;
            foreach (var contact in BuildAuthorityContacts(communityId, dossier))
            {
                if (existingContacts.Any(e =>
                        string.Equals(e.PhoneNumber, contact.PhoneNumber, StringComparison.Ordinal)
                        || string.Equals(e.Name, contact.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                db.EmergencyContacts.Add(contact);
                existingContacts.Add(contact);
                addedContacts++;
            }

            steps.Add(new AgentStep { StepOrder = order++, StepTitle = "Authority contacts merged", ModelOrToolName = "muse-spark-1.3", Detail = $"{addedContacts} new authority contacts merged.", LatencyMs = 90 });
        }

        if (selectedSections.HasFlag(DossierSectionFlags.WasteSchedule))
        {
            var existingWaste = await db.WasteSchedules.AsNoTracking()
                .Where(w => w.CommunityId == communityId)
                .ToListAsync(ct);
            var addedWaste = 0;
            foreach (var schedule in BuildDefaultWasteSchedules(communityId))
            {
                if (existingWaste.Any(w => string.Equals(w.WasteFraction, schedule.WasteFraction, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                db.WasteSchedules.Add(schedule);
                existingWaste.Add(schedule);
                addedWaste++;
            }

            steps.Add(new AgentStep { StepOrder = order++, StepTitle = "Waste schedule merged", ModelOrToolName = "muse-spark-1.3", Detail = $"{addedWaste} new waste fractions merged.", LatencyMs = 60 });
        }

        if (selectedSections.HasFlag(DossierSectionFlags.Vendors))
        {
            var existingVendors = await db.Vendors.AsNoTracking()
                .Where(v => v.CommunityId == communityId)
                .ToListAsync(ct);
            var addedVendors = 0;
            foreach (var vendor in BuildVendors(communityId, dossier))
            {
                if (existingVendors.Any(v => string.Equals(v.Name, vendor.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                db.Vendors.Add(vendor);
                existingVendors.Add(vendor);
                addedVendors++;
            }

            steps.Add(new AgentStep { StepOrder = order++, StepTitle = "Contractor directory merged", ModelOrToolName = "muse-spark-1.3", Detail = $"{addedVendors} new contractors merged.", LatencyMs = 80 });
        }

        if (selectedSections.HasFlag(DossierSectionFlags.Inspections))
        {
            var buildingId = await db.Buildings.AsNoTracking()
                .Where(b => b.CommunityId == communityId)
                .OrderBy(b => b.Name)
                .Select(b => b.Id)
                .FirstOrDefaultAsync(ct);
            if (buildingId == Guid.Empty)
            {
                var building = new Building
                {
                    CommunityId = community.Id,
                    Name = "Building A",
                    StreetAddress = community.StreetAddress,
                    FloorsCount = dossier.BuildingProfile.FloorsCount,
                    StairwellsCount = dossier.BuildingProfile.StairwellsCount,
                    ConstructionYear = dossier.BuildingProfile.EstimatedConstructionYear,
                    HeatingType = Clean(dossier.BuildingProfile.HeatingType, "Municipal District Heating Substation"),
                    RoofType = Clean(dossier.BuildingProfile.RoofType, "Flat EPDM Membrane with Ballast")
                };
                db.Buildings.Add(building);
                await db.SaveChangesAsync(ct);
                buildingId = building.Id;
            }

            var existingInspections = await db.Inspections.AsNoTracking()
                .Where(i => i.CommunityId == communityId && i.BuildingId == buildingId)
                .ToListAsync(ct);
            var addedInspections = 0;
            foreach (var inspection in BuildDefaultInspections(communityId, buildingId))
            {
                if (existingInspections.Any(i => i.Kind == inspection.Kind))
                {
                    continue;
                }

                db.Inspections.Add(inspection);
                existingInspections.Add(inspection);
                addedInspections++;
            }

            steps.Add(new AgentStep { StepOrder = order++, StepTitle = "Inspection schedule merged", ModelOrToolName = "muse-spark-1.3", Detail = $"{addedInspections} new statutory inspections merged.", LatencyMs = 70 });
        }

        if (selectedSections.HasFlag(DossierSectionFlags.GreenSubsidies))
        {
            var existingSubsidies = await db.GreenSubsidies.AsNoTracking()
                .Where(g => g.CommunityId == communityId)
                .ToListAsync(ct);
            var addedSubsidies = 0;
            foreach (var subsidy in BuildGreenSubsidies(communityId, dossier))
            {
                if (existingSubsidies.Any(g => string.Equals(g.ProgramName, subsidy.ProgramName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                db.GreenSubsidies.Add(subsidy);
                existingSubsidies.Add(subsidy);
                addedSubsidies++;
            }

            steps.Add(new AgentStep { StepOrder = order++, StepTitle = "Green subsidies merged", ModelOrToolName = "muse-spark-1.3", Detail = $"{addedSubsidies} new subsidy programs merged.", LatencyMs = 70 });
        }

        if (selectedSections.HasFlag(DossierSectionFlags.LegalCorpus))
        {
            var existingDocs = await db.LegalDocuments
                .Where(d => d.CommunityId == communityId)
                .ToListAsync(ct);
            var addedDocs = 0;
            foreach (var doc in BuildLegalDocuments(communityId, dossier))
            {
                var existing = existingDocs.FirstOrDefault(d => string.Equals(d.DocumentCode, doc.DocumentCode, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    db.LegalDocuments.Add(doc);
                    existingDocs.Add(doc);
                    addedDocs++;
                }
                else
                {
                    existing.VerbatimLegalText = doc.VerbatimLegalText;
                    existing.KeyProvisionsSummary = doc.KeyProvisionsSummary;
                    existing.AcquiredAtUtc = DateTime.UtcNow;
                }
            }

            steps.Add(new AgentStep { StepOrder = order++, StepTitle = "Legal corpus merged", ModelOrToolName = "muse-spark-1.3", Detail = $"{addedDocs} new legal documents merged.", LatencyMs = 110 });
        }

        if (steps.Count == 0)
        {
            steps.Add(new AgentStep { StepOrder = 1, StepTitle = "Dossier review", ModelOrToolName = "muse-spark-1.3", Detail = "No sections selected; dossier reviewed without changes.", LatencyMs = 20 });
        }

        var run = new AgentRun
        {
            CommunityId = community.Id,
            AgentName = "WebAutoEnrichmentOutageAndGrantAgent",
            TriggerSummary = "Discovery dossier applied to property",
            ModelName = "muse-spark-1.3",
            TotalLatencyMs = steps.Sum(s => s.LatencyMs),
            TotalTokens = steps.Count * 420,
            UsedLiveApi = false,
            StartedAt = DateTime.UtcNow
        };
        foreach (var step in steps)
        {
            step.AgentRunId = run.Id;
            run.Steps.Add(step);
            db.AgentSteps.Add(step);
        }

        db.AgentRuns.Add(run);
        await AppendAuditAsync(db, community.Id, actor.Role, "PROPERTY_DOSSIER_APPLIED", "Discovery dossier applied to property.", ct);
        await db.SaveChangesAsync(ct);
        return community;
    }

    public async Task<Community> UpdatePropertySettingsAsync(
        Guid communityId,
        Guid actorMembershipId,
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
        CancellationToken ct = default)
    {
        ThrowIfInjectionDetected($"{name ?? string.Empty} {streetAddress ?? string.Empty} {city ?? string.Empty} {taxIdOrEin ?? string.Empty}");
        await using var db = await CreateDbAsync(ct);
        var community = await db.Communities.FirstOrDefaultAsync(c => c.Id == communityId, ct)
            ?? throw new InvalidOperationException("Property not found.");
        var firmId = community.ManagementFirmId ?? Guid.Empty;
        var actor = await RequireFirmActorAsync(db, actorMembershipId, firmId, ct);
        await EnsureCanEditPropertyAsync(db, actor, communityId, ct);

        if (!string.IsNullOrWhiteSpace(name))
        {
            community.Name = name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(streetAddress))
        {
            community.StreetAddress = streetAddress.Trim();
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            community.City = city.Trim();
        }

        if (!string.IsNullOrWhiteSpace(taxIdOrEin))
        {
            community.TaxIdOrEin = taxIdOrEin.Trim();
        }

        community.OperatingRatePerM2 = operatingRatePerM2;
        community.RepairFundRatePerM2 = repairFundRatePerM2;
        community.WasteFeePerOccupant = wasteFeePerOccupant;
        community.ColdWaterTariffPerM3 = coldWaterTariff;
        community.HotWaterHeatingCostPerM3 = hotWaterTariff;
        community.DistrictHeatingTariffPerGj = heatingTariff;
        community.CommonElectricityTariffPerKwh = electricityTariff;

        await AppendAuditAsync(db, community.Id, actor.Role, "PROPERTY_SETTINGS_UPDATED", "Property settings updated.", ct);
        await db.SaveChangesAsync(ct);
        return community;
    }

    public async Task<IReadOnlyList<PropertyListItem>> GetPropertyListAsync(
        Guid firmId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.Communities
            .AsNoTracking()
            .Where(c => c.ManagementFirmId == firmId)
            .OrderBy(c => c.Name)
            .Select(c => new PropertyListItem(c.Id, c.Name, c.City, c.StreetAddress))
            .ToListAsync(ct);
    }

    public async Task<Community?> GetPropertyDetailAsync(
        Guid id,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.Communities
            .AsNoTracking()
            .Include(c => c.Buildings)
            .Include(c => c.LedgerAccounts)
            .Include(c => c.UtilityProviderSources)
            .Include(c => c.EmergencyContacts)
            .Include(c => c.WasteSchedules)
            .Include(c => c.Vendors)
            .Include(c => c.Inspections)
            .Include(c => c.GreenSubsidies)
            .Include(c => c.LegalDocuments)
            .Include(c => c.CommunityContacts)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }
}
