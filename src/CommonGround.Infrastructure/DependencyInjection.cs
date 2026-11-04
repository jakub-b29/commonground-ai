using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Application.Services;

using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.Documents;
using CommonGround.Infrastructure.MetaModels;
using CommonGround.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CommonGround.Infrastructure;

public sealed class AppDbContextFactoryAdapter(IDbContextFactory<AppDbContext> inner) : IAppDbContextFactory
{
    public async Task<IAppDbContext> CreateDbContextAsync(CancellationToken ct = default)
    {
        return await inner.CreateDbContextAsync(ct);
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddCommonGroundInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("CommonGround")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=CommonGroundV3;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

        services.AddDbContextFactory<AppDbContext>(options =>
        {
            try
            {
                options.UseSqlServer(connectionString, sql =>
                {
                    sql.CommandTimeout(120);
                    sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                });
            }
            catch
            {
                options.UseInMemoryDatabase("CommonGroundFallback");
            }
        });
        services.AddSingleton<IAppDbContextFactory, AppDbContextFactoryAdapter>();

        services.Configure<MetaModelOptions>(options =>
        {
            var section = configuration.GetSection(MetaModelOptions.SectionName);
            section.Bind(options);
            var metaKey = configuration["MetaModels:ApiKey"]
                ?? configuration["META_KEY"]
                ?? Environment.GetEnvironmentVariable("META_KEY")
                ?? Environment.GetEnvironmentVariable("MODEL_API_KEY")
                ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(metaKey))
            {
                options.ApiKey = metaKey.Trim().Trim('"').Trim((char)39);
            }
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                options.BaseUrl = "https://api.meta.ai/v1";
            }
            options.SparkModel = "muse-spark-1.3-contributor";
            options.WonderModel = "muse-spark-1.3-contributor";
            options.VoiceModel = "muse-voice-transcribe-1.0";
            options.SamModel = "sam-3.1";
            options.ImageModel = "muse-image-1.0";
            options.UseFallbackWhenNoKey = false;
            options.UseSearchGrounding = true;
        });
        services.AddHttpClient("MetaModels", client => { client.Timeout = TimeSpan.FromMinutes(10); })
            .AddStandardResilienceHandler(o =>
            {
                o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(10);
                o.AttemptTimeout.Timeout = TimeSpan.FromMinutes(5);
                o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(11);
                o.Retry.MaxRetryAttempts = 2;
            });
        services.AddSingleton<SamOverlayRenderer>();
        services.AddSingleton<MetaModelClients>();
        services.AddSingleton<IMuseSparkClient>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IMuseWonderClient>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IMuseVoiceTranscriber>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<ISam31Client>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IMuseImageClient>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IDocumentExporter, DocumentExporter>();
        services.AddScoped<CommonGroundWorkspaceService>();
        services.AddScoped<IMembershipService, MembershipService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<IPropertyOnboardingService, PropertyOnboardingService>();
        services.AddScoped<SparkAgentPermissionEvaluator>();
        services.AddScoped<SparkAgentToolExecutor>();
        services.AddScoped<IMuseSparkAutomationAgentService, MuseSparkAutomationAgentService>();
        services.AddScoped<MuseSparkAutomationAgentService>();
        return services;
    }

    public static async Task EnsureCommonGroundDatabaseCreatedAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        if (db.Database.IsSqlServer())
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'InvitationTokenHash' AND Object_ID = Object_ID(N'[WorkspaceInvitations]')) ALTER TABLE [WorkspaceInvitations] ADD [InvitationTokenHash] nvarchar(max) NOT NULL DEFAULT N'';",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ExpiresAtUtc' AND Object_ID = Object_ID(N'[WorkspaceInvitations]')) ALTER TABLE [WorkspaceInvitations] ADD [ExpiresAtUtc] datetime2 NOT NULL DEFAULT '2000-01-01T00:00:00';",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE [WorkspaceInvitations] SET [ExpiresAtUtc] = DATEADD(day, 7, [CreatedAtUtc]) WHERE [ExpiresAtUtc] < '2001-01-01';",
                    ct);
            }
            catch
            {
            }
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE [UserAccounts] SET [CommunityId] = NULL WHERE [CommunityId] = '00000000-0000-0000-0000-000000000000';",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE [UserAccounts] ALTER COLUMN [CommunityId] uniqueidentifier NULL;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserAccounts_Communities_CommunityId') ALTER TABLE [UserAccounts] DROP CONSTRAINT [FK_UserAccounts_Communities_CommunityId];",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_UserAccounts_Communities_CommunityId') ALTER TABLE [UserAccounts] ADD CONSTRAINT [FK_UserAccounts_Communities_CommunityId] FOREIGN KEY ([CommunityId]) REFERENCES [Communities] ([Id]) ON DELETE SET NULL;",
                    ct);
            }
            catch
            {
            }
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'RequireEmailConfirmation' AND Object_ID = Object_ID(N'[ManagementFirms]')) ALTER TABLE [ManagementFirms] ADD [RequireEmailConfirmation] bit NOT NULL DEFAULT 0;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'IsEmailConfirmed' AND Object_ID = Object_ID(N'[ManagementFirms]')) ALTER TABLE [ManagementFirms] ADD [IsEmailConfirmed] bit NOT NULL DEFAULT 1;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EmailConfirmationTokenHash' AND Object_ID = Object_ID(N'[ManagementFirms]')) ALTER TABLE [ManagementFirms] ADD [EmailConfirmationTokenHash] nvarchar(max) NULL;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'AcceptedAtUtc' AND Object_ID = Object_ID(N'[ManagementFirms]')) ALTER TABLE [ManagementFirms] ADD [AcceptedAtUtc] datetime2 NULL;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE [ManagementFirms] SET [AcceptedAtUtc] = [CreatedAtUtc] WHERE [IsEmailConfirmed] = 1 AND [AcceptedAtUtc] IS NULL;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'AccountKind' AND Object_ID = Object_ID(N'[UserAccounts]')) ALTER TABLE [UserAccounts] ADD [AccountKind] int NOT NULL DEFAULT 1;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EmailConfirmed' AND Object_ID = Object_ID(N'[UserAccounts]')) ALTER TABLE [UserAccounts] ADD [EmailConfirmed] bit NOT NULL DEFAULT 1;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EmailConfirmationTokenHash' AND Object_ID = Object_ID(N'[UserAccounts]')) ALTER TABLE [UserAccounts] ADD [EmailConfirmationTokenHash] nvarchar(max) NULL;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'AcceptedAtUtc' AND Object_ID = Object_ID(N'[UserAccounts]')) ALTER TABLE [UserAccounts] ADD [AcceptedAtUtc] datetime2 NULL;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'TargetCommunityIdsCsv' AND Object_ID = Object_ID(N'[WorkspaceInvitations]')) ALTER TABLE [WorkspaceInvitations] ADD [TargetCommunityIdsCsv] nvarchar(max) NOT NULL DEFAULT N'';",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'PreAssignedPermissions' AND Object_ID = Object_ID(N'[WorkspaceInvitations]')) ALTER TABLE [WorkspaceInvitations] ADD [PreAssignedPermissions] bigint NOT NULL DEFAULT 0;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'RequiresManagerApproval' AND Object_ID = Object_ID(N'[WorkspaceInvitations]')) ALTER TABLE [WorkspaceInvitations] ADD [RequiresManagerApproval] bit NOT NULL DEFAULT 1;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'AccountMemberships') BEGIN CREATE TABLE [AccountMemberships] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_AccountMemberships] PRIMARY KEY, [UserAccountId] uniqueidentifier NOT NULL, [ManagementFirmId] uniqueidentifier NOT NULL, [AllowedCommunityIdsCsv] nvarchar(max) NOT NULL DEFAULT N'', [Role] int NOT NULL DEFAULT 0, [ApprovalStatus] int NOT NULL DEFAULT 0, [GrantedPermissions] bigint NOT NULL DEFAULT 0, [LinkedUnitNumbersCsv] nvarchar(max) NOT NULL DEFAULT N'', [ContractorCompanyOrTrade] nvarchar(max) NULL, [CreatedAtUtc] datetime2 NOT NULL DEFAULT '2000-01-01T00:00:00', [DecidedAtUtc] datetime2 NULL, CONSTRAINT [FK_AccountMemberships_UserAccounts_UserAccountId] FOREIGN KEY ([UserAccountId]) REFERENCES [UserAccounts] ([Id]) ON DELETE CASCADE, CONSTRAINT [FK_AccountMemberships_ManagementFirms_ManagementFirmId] FOREIGN KEY ([ManagementFirmId]) REFERENCES [ManagementFirms] ([Id]) ON DELETE CASCADE); END;",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AccountMemberships_UserAccountId_ManagementFirmId' AND object_id = OBJECT_ID(N'[AccountMemberships]')) CREATE INDEX [IX_AccountMemberships_UserAccountId_ManagementFirmId] ON [AccountMemberships] ([UserAccountId], [ManagementFirmId]);",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AccountMemberships_ManagementFirmId_ApprovalStatus' AND object_id = OBJECT_ID(N'[AccountMemberships]')) CREATE INDEX [IX_AccountMemberships_ManagementFirmId_ApprovalStatus] ON [AccountMemberships] ([ManagementFirmId], [ApprovalStatus]);",
                    ct);
            }
            catch
            {
            }
            try
            {
                await EnsureUnifiedCommunityUnitsAndResidentsSchemaSqlServerAsync(db, ct);
            }
            catch
            {
            }
        }
        try
        {
            if (!await db.ManagementFirms.AnyAsync(ct))
            {
                var now = DateTime.UtcNow;
                var firmId = Guid.NewGuid();
                var communityId = Guid.NewGuid();
                var managerId = Guid.NewGuid();
                const string firmCode = "CG-DEMO-2026";
                var firm = new ManagementFirm
                {
                    Id = firmId,
                    Name = "Demo Management Firm",
                    FirmGroupCode = firmCode,
                    OperatingMode = SystemOperatingMode.HousingAssociation,
                    RequireEmailConfirmation = false,
                    IsEmailConfirmed = true,
                    AcceptedAtUtc = now,
                    CreatedAtUtc = now
                };
                var community = new Community
                {
                    Id = communityId,
                    ManagementFirmId = firmId,
                    FirmGroupCode = firmCode,
                    Name = "Demo Property - Harbor Street 1",
                    StreetAddress = "Harbor Street 1",
                    City = "Demo City",
                    OperatingMode = SystemOperatingMode.HousingAssociation
                };
                var manager = new WorkspaceUserAccount
                {
                    Id = managerId,
                    CommunityId = communityId,
                    ManagementFirmId = firmId,
                    FirmGroupCode = firmCode,
                    FullName = "Demo Firm Manager",
                    Email = "manager@demo.local",
                    Phone = "+10000000001",
                    Provider = IdentityProvider.EmailPassword,
                    ProviderSubjectId = "email-manager-demo",
                    PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("Demo-Password-42!"),
                    Role = WorkspaceSecurityRole.FirmManager,
                    AccountKind = WorkspaceAccountKind.FirmAccount,
                    EmailConfirmed = true,
                    AcceptedAtUtc = now,
                    AllowedCommunityIdsCsv = communityId.ToString(),
                    GrantedByPropertyManager = true,
                    LastLoginAtUtc = now
                };
                var membership = new WorkspaceAccountMembership
                {
                    Id = Guid.NewGuid(),
                    UserAccountId = managerId,
                    ManagementFirmId = firmId,
                    AllowedCommunityIdsCsv = communityId.ToString(),
                    Role = WorkspaceSecurityRole.FirmManager,
                    ApprovalStatus = WorkspaceMembershipStatus.Approved,
                    GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.FirmManager),
                    CreatedAtUtc = now,
                    DecidedAtUtc = now
                };
                db.ManagementFirms.Add(firm);
                db.Communities.Add(community);
                db.UserAccounts.Add(manager);
                db.AccountMemberships.Add(membership);
                await db.SaveChangesAsync(ct);
            }
        }
        catch
        {
        }
        try
        {
            if (!await db.Users.AnyAsync(ct))
            {
                var demoFirm = await db.ManagementFirms.FirstOrDefaultAsync(f => f.FirmGroupCode == "CG-DEMO-2026", ct);
                if (demoFirm is not null)
                {
                    var now2 = DateTime.UtcNow;
                    var riversideId = Guid.NewGuid();
                    var riversideBuildingId = Guid.NewGuid();
                    var riversideUnitId = Guid.NewGuid();
                    var riverside = new Community
                    {
                        Id = riversideId,
                        ManagementFirmId = demoFirm.Id,
                        FirmGroupCode = demoFirm.FirmGroupCode,
                        Name = "Demo Property - Riverside 12",
                        StreetAddress = "Riverside 12",
                        City = "Demo City",
                        PostalOrZipCode = "00-002",
                        CountryCode = "PL",
                        CurrencyCode = "PLN",
                        TaxIdOrEin = "525-000-11-42",
                        RegistryNumber = "REG-000184412",
                        LocalMunicipalityOrCounty = "Demo City Municipality",
                        LocalTaxOffice = "Tax Office Demo City, Riverside 12 00-002",
                        CompetentCourtOrRecorder = "District Court Land Register Division, Riverside 12, Demo City 00-002",
                        OperatingMode = SystemOperatingMode.HousingAssociation,
                        OperatingRatePerM2 = 3.20m,
                        RepairFundRatePerM2 = 1.80m,
                        WasteFeePerOccupant = 28.00m,
                        ColdWaterTariffPerM3 = 11.50m,
                        HotWaterHeatingCostPerM3 = 24.00m,
                        DistrictHeatingTariffPerGj = 82.00m,
                        CommonElectricityTariffPerKwh = 0.88m,
                        ContractedHeatingPowerMw = 0.42m,
                        PeakMeasuredHeatingPowerMw = 0.29m,
                        ContractedElectricalPowerKw = 65.0m,
                        PeakMeasuredElectricalPowerKw = 38.0m,
                        LastWebEnrichedAt = now2
                    };
                    db.Communities.Add(riverside);
                    var riversideBuilding = new Building
                    {
                        Id = riversideBuildingId,
                        CommunityId = riversideId,
                        Name = "Building A",
                        StreetAddress = "Riverside 12",
                        FloorsCount = 5,
                        StairwellsCount = 3,
                        ConstructionYear = 2019,
                        HeatingType = "Municipal District Heating Substation",
                        RoofType = "Flat EPDM Membrane with Ballast"
                    };
                    db.Buildings.Add(riversideBuilding);
                    var riversideUnit = new Unit
                    {
                        Id = riversideUnitId,
                        CommunityId = riversideId,
                        BuildingId = riversideBuildingId,
                        UnitNumber = "B-201",
                        StairwellNumber = 2,
                        FloorNumber = 2,
                        VerticalStackCode = "STACK-B1",
                        Kind = UnitKind.Apartment,
                        FloorAreaM2 = 62.5m,
                        StorageAreaM2 = 5.0m,
                        ShareNumerator = 720,
                        ShareDenominator = 10000,
                        DeclaredOccupants = 3,
                        PrimaryOwnerName = "Demo Resident",
                        PrimaryOwnerEmail = "resident@demo.local",
                        PrimaryOwnerPhone = "+10000000002",
                        PreferredLanguage = "EN",
                        VirtualIbanOrLockboxCode = "PL00 0000 0000 0000 0000 0000 0201"
                    };
                    db.Units.Add(riversideUnit);
                    var contacts = new List<EmergencyAndAuthorityContact>
                    {
                        new() { CommunityId = riversideId, Category = EmergencyContactCategories.EmergencyService, Name = "General Emergency Dispatch", PhoneNumber = "112", AddressOrNotes = "National emergency number, available 24/7", SortOrder = 0, IsAiDiscovered = true },
                        new() { CommunityId = riversideId, Category = EmergencyContactCategories.PolicePrecinct, Name = "District Police Station Demo City - Riverside Precinct", PhoneNumber = "+48 47 723 0000", AddressOrNotes = "Duty desk 24/7, Riverside 12, Demo City 00-002", SortOrder = 1, IsAiDiscovered = true },
                        new() { CommunityId = riversideId, Category = EmergencyContactCategories.DistrictPoliceOfficer, Name = "District Beat Officer Demo City Sector 012", PhoneNumber = "+48 47 723 0001", AddressOrNotes = "Sector 012, office hours 08:00-16:00, Riverside 12, Demo City", SortOrder = 2, IsAiDiscovered = true },
                        new() { CommunityId = riversideId, Category = EmergencyContactCategories.MunicipalGuard, Name = "Municipal Guard Patrol Demo City - Riverside 12", PhoneNumber = "+48 22 555 0102", AddressOrNotes = "Fire-lane towing dispatch, Riverside 12, Demo City", SortOrder = 3, IsAiDiscovered = true },
                        new() { CommunityId = riversideId, Category = EmergencyContactCategories.FireStation, Name = "Fire and Rescue Station Demo City - Riverside 12", PhoneNumber = "+48 22 555 0198", AddressOrNotes = "Hydrant inspection line, Riverside 12, Demo City. Emergency 112, 998.", SortOrder = 4, IsAiDiscovered = true },
                        new() { CommunityId = riversideId, Category = EmergencyContactCategories.BuildingInspectorate, Name = "District Building Inspectorate Demo City", PhoneNumber = "+48 22 555 0103", AddressOrNotes = "Permit desk, Riverside 12, Demo City", SortOrder = 5, IsAiDiscovered = true },
                        new() { CommunityId = riversideId, Category = EmergencyContactCategories.RecyclingDepot, Name = "Selective Waste Drop-off Demo City - Riverside 12", PhoneNumber = "+48 22 555 0104", AddressOrNotes = "Open Mon-Sat 08:00-18:00, Riverside 12, Demo City", SortOrder = 6, IsAiDiscovered = true }
                    };
                    foreach (var contact in contacts)
                    {
                        db.EmergencyContacts.Add(contact);
                    }
                    var sources = new List<UtilityProviderSource>
                    {
                        new() { CommunityId = riversideId, UtilityKind = UtilityKind.Electricity, ProviderName = "Regional Power Distribution - Demo City District", OutageBulletinUrl = "https://www.stoen.pl/outages/demo-city-riverside", LiveFaultMapUrl = "https://www.stoen.pl/fault-map/demo-city-riverside", TariffScheduleUrl = "https://www.stoen.pl/tariffs-households", EmergencyPhone = "991", CurrentUnitTariff = 0.88m, TariffUnit = "PLN/kWh", LastCheckedAt = now2, HealthStatus = "Online (200 OK)", IsAiDiscovered = true },
                        new() { CommunityId = riversideId, UtilityKind = UtilityKind.ColdWater, ProviderName = "Municipal Water and Sewage Works - Demo City", OutageBulletinUrl = "https://www.mpwik.com.pl/outages/demo-city-riverside", LiveFaultMapUrl = "https://www.mpwik.com.pl/fault-map/demo-city-riverside", TariffScheduleUrl = "https://www.mpwik.com.pl/tariffs", EmergencyPhone = "994", CurrentUnitTariff = 11.50m, TariffUnit = "PLN/m3", LastCheckedAt = now2, HealthStatus = "Online (200 OK)", IsAiDiscovered = true },
                        new() { CommunityId = riversideId, UtilityKind = UtilityKind.DistrictHeating, ProviderName = "Municipal District Heating Network - Demo City", OutageBulletinUrl = "https://www.veolia.pl/outages/demo-city-riverside", LiveFaultMapUrl = "https://www.veolia.pl/fault-map/demo-city-riverside", TariffScheduleUrl = "https://www.veolia.pl/tariffs", EmergencyPhone = "993", CurrentUnitTariff = 82.00m, TariffUnit = "PLN/GJ", LastCheckedAt = now2, HealthStatus = "Online (200 OK)", IsAiDiscovered = true },
                        new() { CommunityId = riversideId, UtilityKind = UtilityKind.Gas, ProviderName = "Regional Gas Distribution - Demo City Network", OutageBulletinUrl = "https://www.psgaz.pl/outages/demo-city-riverside", LiveFaultMapUrl = "https://www.psgaz.pl/network/demo-city-riverside", TariffScheduleUrl = "https://www.psgaz.pl/tariffs", EmergencyPhone = "992", CurrentUnitTariff = 2.40m, TariffUnit = "PLN/m3", LastCheckedAt = now2, HealthStatus = "Online (200 OK)", IsAiDiscovered = true },
                        new() { CommunityId = riversideId, UtilityKind = UtilityKind.MunicipalWaste, ProviderName = "City Waste Management - Demo City Sector 02", OutageBulletinUrl = "https://www.city-cleaning.pl/schedule/demo-city-riverside", LiveFaultMapUrl = "https://www.city-cleaning.pl/map/demo-city-riverside", TariffScheduleUrl = "https://www.city-cleaning.pl/tariffs", EmergencyPhone = "+48 22 555 0100", CurrentUnitTariff = 28.00m, TariffUnit = "PLN/person/mo", LastCheckedAt = now2, HealthStatus = "Online (200 OK)", IsAiDiscovered = true }
                    };
                    foreach (var source in sources)
                    {
                        db.UtilityProviderSources.Add(source);
                    }
                    var schedules = new List<WasteCollectionSchedule>
                    {
                        new() { CommunityId = riversideId, WasteFraction = "Mixed", CollectionDaysSummary = "Tuesday and Friday", NextPickupDate = now2.AddDays(1) },
                        new() { CommunityId = riversideId, WasteFraction = "Plastics", CollectionDaysSummary = "Wednesday", NextPickupDate = now2.AddDays(2) },
                        new() { CommunityId = riversideId, WasteFraction = "Paper", CollectionDaysSummary = "Thursday", NextPickupDate = now2.AddDays(3) },
                        new() { CommunityId = riversideId, WasteFraction = "Bio", CollectionDaysSummary = "Monday", NextPickupDate = now2.AddDays(4) },
                        new() { CommunityId = riversideId, WasteFraction = "Glass", CollectionDaysSummary = "Second Thursday of the month", NextPickupDate = now2.AddDays(5) }
                    };
                    foreach (var schedule in schedules)
                    {
                        db.WasteSchedules.Add(schedule);
                    }
                    var vendors = new List<Vendor>
                    {
                        new() { CommunityId = riversideId, Name = "Elevator Rescue Demo City 24/7", Trade = WorkOrderCategory.Elevator, Phone = "+48 22 555 0199", Email = "rescue@elevator-demo.pl", Website = "https://www.elevator-demo.pl", Rating = 4.9m, TypicalHourlyRate = 90.00m, IsAiDiscovered = true },
                        new() { CommunityId = riversideId, Name = "Emergency Plumbing Rescue Demo City - Riverside 12", Trade = WorkOrderCategory.Plumbing, Phone = "+48 601 100 200", Email = "dispatch@plumbing-demo.pl", Website = "https://www.plumbing-demo.pl", Rating = 4.8m, TypicalHourlyRate = 65.00m, IsAiDiscovered = true }
                    };
                    foreach (var vendor in vendors)
                    {
                        db.Vendors.Add(vendor);
                    }
                    var residentUserId = Guid.NewGuid();
                    var residentMembershipId = Guid.NewGuid();
                    var residentUser = new User
                    {
                        Id = residentUserId,
                        FullName = "Demo Resident",
                        Email = "resident@demo.local",
                        Phone = "+10000000002",
                        PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("Demo-Password-42!"),
                        EmailConfirmed = true,
                        Provider = IdentityProvider.EmailPassword
                    };
                    db.Users.Add(residentUser);
                    var residentMembership = new FirmMembership
                    {
                        Id = residentMembershipId,
                        UserId = residentUserId,
                        ManagementFirmId = demoFirm.Id,
                        Role = WorkspaceSecurityRole.Resident,
                        Status = WorkspaceMembershipStatus.Approved,
                        GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident),
                        DecidedAtUtc = now2
                    };
                    db.FirmMemberships.Add(residentMembership);
                    db.MembershipPropertyAccesses.Add(new MembershipPropertyAccess
                    {
                        FirmMembershipId = residentMembershipId,
                        CommunityId = riversideId,
                        IsPrimaryManager = false
                    });
                    db.MembershipUnitLinks.Add(new MembershipUnitLink
                    {
                        FirmMembershipId = residentMembershipId,
                        CommunityId = riversideId,
                        UnitId = riversideUnitId,
                        RelationshipKind = MembershipUnitRelationshipKind.Owner
                    });
                    var pendingUserId = Guid.NewGuid();
                    var pendingMembershipId = Guid.NewGuid();
                    var pendingUser = new User
                    {
                        Id = pendingUserId,
                        FullName = "Pending Demo Applicant",
                        Email = "pending@demo.local",
                        Phone = "+10000000003",
                        PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("Demo-Password-42!"),
                        EmailConfirmed = true,
                        Provider = IdentityProvider.EmailPassword
                    };
                    db.Users.Add(pendingUser);
                    var pendingMembership = new FirmMembership
                    {
                        Id = pendingMembershipId,
                        UserId = pendingUserId,
                        ManagementFirmId = demoFirm.Id,
                        Role = WorkspaceSecurityRole.Resident,
                        Status = WorkspaceMembershipStatus.PendingApproval,
                        GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident)
                    };
                    db.FirmMemberships.Add(pendingMembership);
                    db.JoinRequests.Add(new JoinRequest
                    {
                        ManagementFirmId = demoFirm.Id,
                        FirmMembershipId = pendingMembershipId,
                        RequestedRole = WorkspaceSecurityRole.Resident,
                        RequestedCommunityId = riversideId,
                        RequestedUnitNumber = "B-202",
                        ProofOrCompanyNote = "Ownership deed on file for unit B-202.",
                        Status = JoinRequestStatus.PendingApproval,
                        CreatedAtUtc = now2
                    });
                    await db.SaveChangesAsync(ct);
                }
            }
        }
        catch
        {
        }
        try
        {
            await BackfillUnifiedCommunitiesUnitsAndResidentsAsync(db, ct);
        }
        catch
        {
        }
    }

    private static async Task EnsureUnifiedCommunityUnitsAndResidentsSchemaSqlServerAsync(AppDbContext db, CancellationToken ct)
    {
        var statements = new[]
        {
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'CommunityAccountNumber' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [CommunityAccountNumber] nvarchar(64) NOT NULL DEFAULT N'';",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'Status' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [Status] int NOT NULL DEFAULT 0;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'PropertyTypeConfiguration' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [PropertyTypeConfiguration] nvarchar(256) NOT NULL DEFAULT N'Residential Condominium & HOA';",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'AdministrativeEmail' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [AdministrativeEmail] nvarchar(256) NOT NULL DEFAULT N'';",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'AdministrativePhone' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [AdministrativePhone] nvarchar(128) NOT NULL DEFAULT N'';",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EnableHoaGovernance' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [EnableHoaGovernance] bit NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EnableShareWeightedVoting' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [EnableShareWeightedVoting] bit NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EnableArcRequests' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [EnableArcRequests] bit NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EnableSubMeterReconciliation' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [EnableSubMeterReconciliation] bit NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'EnableReserveStudy' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [EnableReserveStudy] bit NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'DefaultRegistrationApprovalMode' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [DefaultRegistrationApprovalMode] int NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'CreatedAtUtc' AND Object_ID = Object_ID(N'[Communities]')) ALTER TABLE [Communities] ADD [CreatedAtUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00';",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'RoomsCount' AND Object_ID = Object_ID(N'[Units]')) ALTER TABLE [Units] ADD [RoomsCount] int NOT NULL DEFAULT 3;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'BathroomsCount' AND Object_ID = Object_ID(N'[Units]')) ALTER TABLE [Units] ADD [BathroomsCount] int NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'OccupancyCapacity' AND Object_ID = Object_ID(N'[Units]')) ALTER TABLE [Units] ADD [OccupancyCapacity] int NOT NULL DEFAULT 4;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'OccupancyStatus' AND Object_ID = Object_ID(N'[Units]')) ALTER TABLE [Units] ADD [OccupancyStatus] int NOT NULL DEFAULT 1;",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'Notes' AND Object_ID = Object_ID(N'[Units]')) ALTER TABLE [Units] ADD [Notes] nvarchar(max) NOT NULL DEFAULT N'';",
            "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'CreatedAtUtc' AND Object_ID = Object_ID(N'[Units]')) ALTER TABLE [Units] ADD [CreatedAtUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00';",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ResidentProfiles') BEGIN CREATE TABLE [ResidentProfiles] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ResidentProfiles] PRIMARY KEY, [ResidentAccountNumber] nvarchar(64) NOT NULL, [UserId] uniqueidentifier NULL, [ManagementFirmId] uniqueidentifier NULL, [FirstName] nvarchar(max) NOT NULL DEFAULT N'', [LastName] nvarchar(max) NOT NULL DEFAULT N'', [FullName] nvarchar(max) NOT NULL DEFAULT N'', [ProfilePhotoUrl] nvarchar(max) NULL, [DateOfBirth] datetime2 NULL, [PrimaryEmail] nvarchar(256) NOT NULL DEFAULT N'', [Phone] nvarchar(128) NOT NULL DEFAULT N'', [AdditionalContactDetails] nvarchar(max) NOT NULL DEFAULT N'', [AccountStatus] int NOT NULL DEFAULT 0, [VerificationStatus] int NOT NULL DEFAULT 2, [PreferredLanguage] nvarchar(16) NOT NULL DEFAULT N'EN', [PreferredContactMethod] nvarchar(64) NOT NULL DEFAULT N'Email', [EmergencyContactName] nvarchar(max) NOT NULL DEFAULT N'', [EmergencyContactPhone] nvarchar(max) NOT NULL DEFAULT N'', [EmergencyContactRelation] nvarchar(max) NOT NULL DEFAULT N'', [RegistrationDateUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [CreatedAtUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [LastLoginAtUtc] datetime2 NULL, [Notes] nvarchar(max) NOT NULL DEFAULT N''); END;",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ResidentUnitAssignments') BEGIN CREATE TABLE [ResidentUnitAssignments] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ResidentUnitAssignments] PRIMARY KEY, [ResidentProfileId] uniqueidentifier NOT NULL, [CommunityId] uniqueidentifier NOT NULL, [BuildingId] uniqueidentifier NOT NULL, [UnitId] uniqueidentifier NOT NULL, [ManagementFirmId] uniqueidentifier NULL, [AssignmentType] int NOT NULL DEFAULT 0, [Status] int NOT NULL DEFAULT 1, [IsPrimaryResident] bit NOT NULL DEFAULT 1, [MoveInDateUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [MoveOutDateUtc] datetime2 NULL, [ApprovedByMembershipId] uniqueidentifier NULL, [ApprovedByName] nvarchar(max) NOT NULL DEFAULT N'', [AssignedAtUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [EndedAtUtc] datetime2 NULL, [AssignmentReasonOrNotes] nvarchar(max) NOT NULL DEFAULT N'', [ConflictOverrideReason] nvarchar(max) NULL); END;",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'UnitOwnerships') BEGIN CREATE TABLE [UnitOwnerships] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_UnitOwnerships] PRIMARY KEY, [CommunityId] uniqueidentifier NOT NULL, [BuildingId] uniqueidentifier NOT NULL, [UnitId] uniqueidentifier NOT NULL, [ResidentProfileId] uniqueidentifier NULL, [OwnerFullName] nvarchar(max) NOT NULL DEFAULT N'', [OwnerEmail] nvarchar(max) NOT NULL DEFAULT N'', [OwnerPhone] nvarchar(max) NOT NULL DEFAULT N'', [OwnershipRole] int NOT NULL DEFAULT 0, [ShareNumerator] int NOT NULL DEFAULT 625, [ShareDenominator] int NOT NULL DEFAULT 10000, [OwnershipPercentage] decimal(18,4) NOT NULL DEFAULT 100.0, [EffectiveStartDateUtc] datetime2 NOT NULL DEFAULT '2025-01-01T00:00:00', [EffectiveEndDateUtc] datetime2 NULL, [IsActive] bit NOT NULL DEFAULT 1, [Notes] nvarchar(max) NOT NULL DEFAULT N''); END;",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ResidentHouseholdMembers') BEGIN CREATE TABLE [ResidentHouseholdMembers] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ResidentHouseholdMembers] PRIMARY KEY, [CommunityId] uniqueidentifier NOT NULL, [UnitId] uniqueidentifier NOT NULL, [PrimaryAssignmentId] uniqueidentifier NULL, [LinkedResidentProfileId] uniqueidentifier NULL, [FullName] nvarchar(max) NOT NULL DEFAULT N'', [RelationshipToPrimary] nvarchar(max) NOT NULL DEFAULT N'Household Member', [Phone] nvarchar(max) NOT NULL DEFAULT N'', [Email] nvarchar(max) NOT NULL DEFAULT N'', [HasPortalAccess] bit NOT NULL DEFAULT 0, [OccupancyStartDateUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [OccupancyEndDateUtc] datetime2 NULL, [IsActive] bit NOT NULL DEFAULT 1, [Notes] nvarchar(max) NOT NULL DEFAULT N''); END;",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ResidentDocuments') BEGIN CREATE TABLE [ResidentDocuments] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ResidentDocuments] PRIMARY KEY, [CommunityId] uniqueidentifier NOT NULL, [UnitId] uniqueidentifier NULL, [ResidentProfileId] uniqueidentifier NULL, [Category] int NOT NULL DEFAULT 7, [Title] nvarchar(max) NOT NULL DEFAULT N'', [FileName] nvarchar(max) NOT NULL DEFAULT N'', [MimeType] nvarchar(max) NOT NULL DEFAULT N'application/pdf', [FileSizeBytes] bigint NOT NULL DEFAULT 0, [StoragePathOrDataUrl] nvarchar(max) NOT NULL DEFAULT N'', [SummaryOrNotes] nvarchar(max) NOT NULL DEFAULT N'', [UploadedByName] nvarchar(max) NOT NULL DEFAULT N'', [UploadedByRole] nvarchar(max) NOT NULL DEFAULT N'', [UploadedAtUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [IsVisibleToResident] bit NOT NULL DEFAULT 1); END;",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'CommunityContacts') BEGIN CREATE TABLE [CommunityContacts] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_CommunityContacts] PRIMARY KEY, [CommunityId] uniqueidentifier NOT NULL, [Category] int NOT NULL DEFAULT 0, [ContactName] nvarchar(max) NOT NULL DEFAULT N'', [RoleTitle] nvarchar(max) NOT NULL DEFAULT N'', [Phone] nvarchar(max) NOT NULL DEFAULT N'', [Email] nvarchar(max) NOT NULL DEFAULT N'', [OfficeHours] nvarchar(max) NOT NULL DEFAULT N'Mon-Fri 08:00-17:00', [AddressOrLocation] nvarchar(max) NOT NULL DEFAULT N'', [Notes] nvarchar(max) NOT NULL DEFAULT N'', [SortOrder] int NOT NULL DEFAULT 0, [IsVisibleToResidents] bit NOT NULL DEFAULT 1); END;",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ResidentRegistrationTokens') BEGIN CREATE TABLE [ResidentRegistrationTokens] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ResidentRegistrationTokens] PRIMARY KEY, [ManagementFirmId] uniqueidentifier NOT NULL, [CommunityId] uniqueidentifier NOT NULL, [BuildingId] uniqueidentifier NOT NULL, [TargetUnitId] uniqueidentifier NOT NULL, [TokenHash] nvarchar(128) NOT NULL, [LabelOrReference] nvarchar(max) NOT NULL DEFAULT N'', [ApprovalMode] int NOT NULL DEFAULT 1, [IntendedAssignmentType] int NOT NULL DEFAULT 0, [IntendedRole] int NOT NULL DEFAULT 1, [MaxUses] int NOT NULL DEFAULT 1, [UseCount] int NOT NULL DEFAULT 0, [ExpiresAtUtc] datetime2 NOT NULL DEFAULT '2026-01-15T00:00:00', [Status] int NOT NULL DEFAULT 0, [CreatedByMembershipId] uniqueidentifier NULL, [CreatedByName] nvarchar(max) NOT NULL DEFAULT N'', [CreatedAtUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [RevokedAtUtc] datetime2 NULL); END;",
            "IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'ResidentRegistrationRequests') BEGIN CREATE TABLE [ResidentRegistrationRequests] ([Id] uniqueidentifier NOT NULL CONSTRAINT [PK_ResidentRegistrationRequests] PRIMARY KEY, [ManagementFirmId] uniqueidentifier NOT NULL, [CommunityId] uniqueidentifier NOT NULL, [BuildingId] uniqueidentifier NOT NULL, [TargetUnitId] uniqueidentifier NOT NULL, [RegistrationTokenId] uniqueidentifier NOT NULL, [SubmittedFirstName] nvarchar(max) NOT NULL DEFAULT N'', [SubmittedLastName] nvarchar(max) NOT NULL DEFAULT N'', [SubmittedFullName] nvarchar(max) NOT NULL DEFAULT N'', [SubmittedEmail] nvarchar(max) NOT NULL DEFAULT N'', [SubmittedPhone] nvarchar(max) NOT NULL DEFAULT N'', [PreferredLanguage] nvarchar(16) NOT NULL DEFAULT N'EN', [EmergencyContactName] nvarchar(max) NOT NULL DEFAULT N'', [EmergencyContactPhone] nvarchar(max) NOT NULL DEFAULT N'', [ResidentNotes] nvarchar(max) NOT NULL DEFAULT N'', [UserId] uniqueidentifier NULL, [MatchedOrCreatedResidentProfileId] uniqueidentifier NULL, [ApprovalMode] int NOT NULL DEFAULT 1, [Status] int NOT NULL DEFAULT 0, [SubmittedAtUtc] datetime2 NOT NULL DEFAULT '2026-01-01T00:00:00', [ReviewedAtUtc] datetime2 NULL, [ReviewedByMembershipId] uniqueidentifier NULL, [ReviewedByName] nvarchar(max) NOT NULL DEFAULT N'', [DecisionNotesOrRejectionReason] nvarchar(max) NOT NULL DEFAULT N''); END;"
        };

        foreach (var sql in statements)
        {
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }
    }

    public static async Task BackfillUnifiedCommunitiesUnitsAndResidentsAsync(IAppDbContext db, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var communities = await db.Communities.ToListAsync(ct);
        var existingComNumbers = communities
            .Select(c => c.CommunityAccountNumber)
            .Where(AccountNumberGenerator.IsValidCommunityAccountNumber)
            .ToList();

        var modified = false;
        foreach (var community in communities)
        {
            if (!AccountNumberGenerator.IsValidCommunityAccountNumber(community.CommunityAccountNumber)
                || existingComNumbers.Count(x => string.Equals(x, community.CommunityAccountNumber, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                var next = AccountNumberGenerator.NextCommunityAccountNumber(existingComNumbers);
                community.CommunityAccountNumber = next;
                existingComNumbers.Add(next);
                modified = true;
            }
            if (string.IsNullOrWhiteSpace(community.PropertyTypeConfiguration))
            {
                community.PropertyTypeConfiguration = "Residential Condominium & HOA";
                modified = true;
            }
            if (string.IsNullOrWhiteSpace(community.AdministrativeEmail))
            {
                community.AdministrativeEmail = !string.IsNullOrWhiteSpace(community.AssignedPropertyManagerEmail)
                    ? community.AssignedPropertyManagerEmail
                    : "office@commonground.local";
                modified = true;
            }
            if (string.IsNullOrWhiteSpace(community.AdministrativePhone))
            {
                community.AdministrativePhone = !string.IsNullOrWhiteSpace(community.AssignedPropertyManagerPhone)
                    ? community.AssignedPropertyManagerPhone
                    : "+1 (800) 555-0199";
                modified = true;
            }
        }

        // Ensure every community has at least one Building and default RiserStacks
        var buildings = await db.Buildings.Include(b => b.RiserStacks).ToListAsync(ct);
        foreach (var community in communities)
        {
            var commBuildings = buildings.Where(b => b.CommunityId == community.Id).ToList();
            if (commBuildings.Count == 0)
            {
                var b = new Building
                {
                    Id = Guid.NewGuid(),
                    CommunityId = community.Id,
                    Name = "Building A",
                    StreetAddress = community.StreetAddress,
                    FloorsCount = 4,
                    StairwellsCount = 2,
                    ConstructionYear = 2021
                };
                db.Buildings.Add(b);
                commBuildings.Add(b);
                buildings.Add(b);
                modified = true;
            }

            foreach (var b in commBuildings)
            {
                if (b.RiserStacks.Count == 0)
                {
                    var defaultCodes = new[] { "STACK-A1", "STACK-A2", "STACK-B1" };
                    foreach (var code in defaultCodes)
                    {
                        var stack = new VerticalRiserStack
                        {
                            Id = Guid.NewGuid(),
                            BuildingId = b.Id,
                            Code = code,
                            Description = $"{b.Name} vertical riser {code}",
                            ShutoffValveLocation = $"Basement technical gallery - valve {code}"
                        };
                        db.RiserStacks.Add(stack);
                        b.RiserStacks.Add(stack);
                    }
                    modified = true;
                }
            }
        }

        // Ensure default CommunityContacts per Community
        var existingContacts = await db.CommunityContacts.ToListAsync(ct);
        foreach (var community in communities)
        {
            if (!existingContacts.Any(c => c.CommunityId == community.Id))
            {
                var defaults = new[]
                {
                    new CommunityContact
                    {
                        CommunityId = community.Id,
                        Category = CommunityContactCategory.ManagementOffice,
                        ContactName = $"{community.Name} Administration Office",
                        RoleTitle = "Community Management Office",
                        Phone = community.AdministrativePhone,
                        Email = community.AdministrativeEmail,
                        OfficeHours = "Mon-Fri 08:00-17:00",
                        AddressOrLocation = $"{community.StreetAddress}, {community.City}".Trim(' ', ','),
                        SortOrder = 0
                    },
                    new CommunityContact
                    {
                        CommunityId = community.Id,
                        Category = CommunityContactCategory.PropertyManager,
                        ContactName = string.IsNullOrWhiteSpace(community.AssignedPropertyManagerName) ? "Lead Property Manager" : community.AssignedPropertyManagerName,
                        RoleTitle = "Assigned Property Manager",
                        Phone = community.AdministrativePhone,
                        Email = community.AdministrativeEmail,
                        OfficeHours = "Mon-Fri 08:30-16:30",
                        AddressOrLocation = "On-site & Remote Desk",
                        SortOrder = 1
                    },
                    new CommunityContact
                    {
                        CommunityId = community.Id,
                        Category = CommunityContactCategory.BoardAssociation,
                        ContactName = "Community Board & Governance Committee",
                        RoleTitle = "Association Board",
                        Phone = community.AdministrativePhone,
                        Email = "board@commonground.local",
                        OfficeHours = "By appointment / Monthly Board Session",
                        AddressOrLocation = community.StreetAddress,
                        SortOrder = 2
                    },
                    new CommunityContact
                    {
                        CommunityId = community.Id,
                        Category = CommunityContactCategory.Emergency,
                        ContactName = "24/7 Emergency Technical Dispatch",
                        RoleTitle = "Emergency Response Desk",
                        Phone = "112 / +1 (800) 555-0911",
                        Email = "emergency@commonground.local",
                        OfficeHours = "24/7 Continuous",
                        AddressOrLocation = "Immediate Emergency Dispatch",
                        SortOrder = 3
                    },
                    new CommunityContact
                    {
                        CommunityId = community.Id,
                        Category = CommunityContactCategory.Accounting,
                        ContactName = "Community Accounting & Assessments Desk",
                        RoleTitle = "Fund Accounting & Settlements",
                        Phone = community.AdministrativePhone,
                        Email = "accounting@commonground.local",
                        OfficeHours = "Mon-Fri 09:00-16:00",
                        AddressOrLocation = "Financial Administration Department",
                        SortOrder = 4
                    }
                };
                foreach (var item in defaults)
                {
                    db.CommunityContacts.Add(item);
                }
                modified = true;
            }
        }

        // Backfill ResidentProfiles, UnitOwnerships, and ResidentUnitAssignments from existing Units and Users
        var profiles = await db.ResidentProfiles.ToListAsync(ct);
        var existingResNumbers = profiles
            .Select(p => p.ResidentAccountNumber)
            .Where(AccountNumberGenerator.IsValidResidentAccountNumber)
            .ToList();
        var users = await db.Users.ToListAsync(ct);
        var memberships = await db.FirmMemberships.Include(m => m.UnitLinks).ToListAsync(ct);
        var units = await db.Units.ToListAsync(ct);
        var ownerships = await db.UnitOwnerships.ToListAsync(ct);
        var assignments = await db.ResidentUnitAssignments.ToListAsync(ct);

        foreach (var user in users)
        {
            var userMemberships = memberships.Where(m => m.UserId == user.Id).ToList();
            if (userMemberships.Count > 0 && userMemberships.All(m => m.Role != WorkspaceSecurityRole.Resident))
            {
                continue;
            }

            var profile = profiles.FirstOrDefault(p =>
                p.UserId == user.Id
                || (!string.IsNullOrWhiteSpace(user.Email) && string.Equals(p.PrimaryEmail, user.Email, StringComparison.OrdinalIgnoreCase)));
            if (profile is null)
            {
                var nextRes = AccountNumberGenerator.NextResidentAccountNumber(existingResNumbers);
                existingResNumbers.Add(nextRes);
                var nameParts = (user.FullName ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                var firstMat = userMemberships.FirstOrDefault();
                profile = new ResidentProfile
                {
                    Id = Guid.NewGuid(),
                    ResidentAccountNumber = nextRes,
                    UserId = user.Id,
                    ManagementFirmId = firstMat?.ManagementFirmId,
                    FirstName = nameParts.Length > 0 ? nameParts[0] : "Resident",
                    LastName = nameParts.Length > 1 ? nameParts[1] : string.Empty,
                    FullName = string.IsNullOrWhiteSpace(user.FullName) ? user.Email : user.FullName,
                    PrimaryEmail = user.Email,
                    Phone = user.Phone,
                    AccountStatus = firstMat?.Status == WorkspaceMembershipStatus.Approved ? ResidentAccountStatus.Active : ResidentAccountStatus.PendingVerification,
                    VerificationStatus = firstMat?.Status == WorkspaceMembershipStatus.Approved ? ResidentVerificationStatus.Verified : ResidentVerificationStatus.PendingReview,
                    RegistrationDateUtc = now,
                    CreatedAtUtc = now
                };
                db.ResidentProfiles.Add(profile);
                profiles.Add(profile);
                modified = true;
            }
        }

        foreach (var unit in units)
        {
            if (unit.BuildingId == Guid.Empty)
            {
                var firstB = buildings.FirstOrDefault(b => b.CommunityId == unit.CommunityId);
                if (firstB is not null)
                {
                    unit.BuildingId = firstB.Id;
                    modified = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(unit.PrimaryOwnerName))
            {
                var comm = communities.FirstOrDefault(c => c.Id == unit.CommunityId);
                var profile = profiles.FirstOrDefault(p =>
                    (!string.IsNullOrWhiteSpace(unit.PrimaryOwnerEmail) && string.Equals(p.PrimaryEmail, unit.PrimaryOwnerEmail, StringComparison.OrdinalIgnoreCase))
                    || string.Equals(p.FullName, unit.PrimaryOwnerName, StringComparison.OrdinalIgnoreCase));

                if (profile is null)
                {
                    var nextRes = AccountNumberGenerator.NextResidentAccountNumber(existingResNumbers);
                    existingResNumbers.Add(nextRes);
                    var parts = unit.PrimaryOwnerName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var matchedUser = users.FirstOrDefault(u => !string.IsNullOrWhiteSpace(unit.PrimaryOwnerEmail) && string.Equals(u.Email, unit.PrimaryOwnerEmail, StringComparison.OrdinalIgnoreCase));
                    profile = new ResidentProfile
                    {
                        Id = Guid.NewGuid(),
                        ResidentAccountNumber = nextRes,
                        UserId = matchedUser?.Id,
                        ManagementFirmId = comm?.ManagementFirmId,
                        FirstName = parts.Length > 0 ? parts[0] : unit.PrimaryOwnerName,
                        LastName = parts.Length > 1 ? parts[1] : string.Empty,
                        FullName = unit.PrimaryOwnerName.Trim(),
                        PrimaryEmail = unit.PrimaryOwnerEmail,
                        Phone = unit.PrimaryOwnerPhone,
                        PreferredLanguage = string.IsNullOrWhiteSpace(unit.PreferredLanguage) ? "EN" : unit.PreferredLanguage,
                        AccountStatus = ResidentAccountStatus.Active,
                        VerificationStatus = ResidentVerificationStatus.Verified,
                        RegistrationDateUtc = now.AddMonths(-6),
                        CreatedAtUtc = now.AddMonths(-6)
                    };
                    db.ResidentProfiles.Add(profile);
                    profiles.Add(profile);
                    modified = true;
                }

                if (!ownerships.Any(o => o.UnitId == unit.Id && o.IsActive))
                {
                    var own = new UnitOwnership
                    {
                        Id = Guid.NewGuid(),
                        CommunityId = unit.CommunityId,
                        BuildingId = unit.BuildingId,
                        UnitId = unit.Id,
                        ResidentProfileId = profile.Id,
                        OwnerFullName = unit.PrimaryOwnerName,
                        OwnerEmail = unit.PrimaryOwnerEmail,
                        OwnerPhone = unit.PrimaryOwnerPhone,
                        OwnershipRole = UnitOwnershipRole.PrimaryOwner,
                        ShareNumerator = unit.ShareNumerator,
                        ShareDenominator = unit.ShareDenominator,
                        OwnershipPercentage = 100.0m,
                        EffectiveStartDateUtc = now.AddYears(-1),
                        IsActive = true
                    };
                    db.UnitOwnerships.Add(own);
                    ownerships.Add(own);
                    modified = true;
                }

                if (!assignments.Any(a => a.UnitId == unit.Id && a.Status == UnitAssignmentStatus.Active))
                {
                    var assign = new ResidentUnitAssignment
                    {
                        Id = Guid.NewGuid(),
                        ResidentProfileId = profile.Id,
                        CommunityId = unit.CommunityId,
                        BuildingId = unit.BuildingId,
                        UnitId = unit.Id,
                        ManagementFirmId = comm?.ManagementFirmId,
                        AssignmentType = ResidentAssignmentType.OwnerResident,
                        Status = UnitAssignmentStatus.Active,
                        IsPrimaryResident = true,
                        MoveInDateUtc = now.AddMonths(-6),
                        ApprovedByName = "System Seeder / Migration",
                        AssignedAtUtc = now.AddMonths(-6),
                        AssignmentReasonOrNotes = "Initial unit resident assignment"
                    };
                    db.ResidentUnitAssignments.Add(assign);
                    assignments.Add(assign);
                    unit.OccupancyStatus = UnitOccupancyStatus.OwnerOccupied;
                    modified = true;
                }
            }
            else if (!assignments.Any(a => a.UnitId == unit.Id && a.Status == UnitAssignmentStatus.Active))
            {
                unit.OccupancyStatus = UnitOccupancyStatus.Vacant;
                modified = true;
            }
        }

        if (modified)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}