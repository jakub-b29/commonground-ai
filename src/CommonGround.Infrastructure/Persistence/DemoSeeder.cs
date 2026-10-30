using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.MetaModels;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Infrastructure.Persistence;

public sealed class DemoSeeder(AppDbContext db, SamOverlayRenderer renderer)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        if (await db.Communities.AnyAsync(ct)) return;
        var amber = BuildAmberPark();
        var oakridge = BuildOakridge();
        db.Communities.AddRange(amber, oakridge);
        await db.SaveChangesAsync(ct);
    }

    private Community BuildAmberPark()
    {
        var community = new Community
        {
            Name = "Amber Park Residence",
            OperatingMode = SystemOperatingMode.HousingAssociation,
            StreetAddress = "14 Parkowa Ave",
            City = "Central District",
            PostalOrZipCode = "00-180",
            CountryCode = "EU",
            CurrencyCode = "EUR",
            TaxIdOrEin = "525-000-11-42",
            RegistryNumber = "REG-000184412",
            LocalMunicipalityOrCounty = "Central District Municipality",
            LocalTaxOffice = "District Tax Office",
            CompetentCourtOrRecorder = "District Court, Land Register Division",
            ConstructionHandoverDate = DateTime.UtcNow.AddYears(-3),
            StatuteFullText = "Statute of the Amber Park Residence Housing Association. Par. 9: Vertical risers and common installations up to the unit branch valve are common property; fixtures after the branch valve are private. Par. 14(3): Draft resolutions must be delivered at least 14 days before the assembly; special levies are allocated by ownership shares. Par. 22(1): Resolutions are adopted by share-weighted majority unless the statute provides otherwise.",
            HouseRulesFullText = "House Rules and Building Order Regulations. Par. 6: Quiet hours are 22:00-06:00. Par. 8: Fire access lanes must remain unobstructed at all times. Par. 10: Renovation works are permitted 08:00-18:00 on weekdays only.",
            UtilityPolicyFullText = "Water, Sewage and District Heating Sub-Meter Reconciliation Policy. Par. 3: Sub-meter readings are collected monthly by radio readout. Par. 5: Unallocated water loss is distributed in proportion to ownership shares. Par. 6: Heating costs are split 70 percent by metered consumption and 30 percent by floor area.",
            LastWebEnrichedAt = DateTime.UtcNow.AddDays(-2)
        };

        var building = new Building
        {
            CommunityId = community.Id,
            Name = "Building A - Parkowa 14",
            StreetAddress = community.StreetAddress,
            FloorsCount = 4,
            StairwellsCount = 2,
            ConstructionYear = 2023,
            RiserStacks =
            [
                new VerticalRiserStack { Code = "STACK-A1", Description = "Left wing water and heating riser", ShutoffValveLocation = "Basement corridor A, door 2, blue valve" },
                new VerticalRiserStack { Code = "STACK-A2", Description = "Center wing water and heating riser", ShutoffValveLocation = "Basement corridor A, manifold room" },
                new VerticalRiserStack { Code = "STACK-B1", Description = "Right wing water and heating riser", ShutoffValveLocation = "Basement corridor B, red valve" }
            ]
        };
        community.Buildings.Add(building);

        var owners = new[]
        {
            ("Anna Kowalska", "anna.kowalska@example.com", "+48 601 202 303"),
            ("Piotr Nowak", "piotr.nowak@example.com", "+48 602 303 404"),
            ("Maria Zielinska", "maria.zielinska@example.com", "+48 603 404 505"),
            ("Tomasz Lewandowski", "tomasz.lewandowski@example.com", "+48 604 505 606"),
            ("Katarzyna Wojcik", "katarzyna.wojcik@example.com", "+48 605 606 707"),
            ("Michal Kaminski", "michal.kaminski@example.com", "+48 606 707 808")
        };

        var stacks = new[] { "STACK-A1", "STACK-A2", "STACK-B1" };
        var sharePlan = new[] { 850, 850, 800, 850, 850, 800, 850, 850, 800, 850, 850, 800 };
        var unitIndex = 0;
        for (var floor = 1; floor <= 4; floor++)
        {
            for (var s = 0; s < 3; s++)
            {
                if (unitIndex >= 12) break;
                var owner = owners[unitIndex % owners.Length];
                var unit = new Unit
                {
                    CommunityId = community.Id,
                    BuildingId = building.Id,
                    UnitNumber = $"{(s == 2 ? "B" : "A")}-{floor}0{s + 1}",
                    StairwellNumber = s == 2 ? 2 : 1,
                    FloorNumber = floor,
                    VerticalStackCode = stacks[s],
                    FloorAreaM2 = 52m + (unitIndex % 4) * 6m,
                    StorageAreaM2 = 4.5m,
                    ShareNumerator = sharePlan[unitIndex],
                    ShareDenominator = 10000,
                    DeclaredOccupants = 1 + (unitIndex % 3),
                    PrimaryOwnerName = owner.Item1,
                    PrimaryOwnerEmail = owner.Item2,
                    PrimaryOwnerPhone = owner.Item3,
                    PreferredLanguage = unitIndex == 5 ? "UA" : "EN",
                    VirtualIbanOrLockboxCode = $"PL61 1090 1014 0000 0712 1981 {2800 + unitIndex}",
                    CurrentBalance = unitIndex == 2 ? 1240m : unitIndex == 7 ? 860m : 0m,
                    DaysPastDue = unitIndex == 2 ? 95 : unitIndex == 7 ? 45 : 0,
                    ArrearsStage = unitIndex == 2 ? ArrearsStage.ECourtDossierOrCountyLien90Days : unitIndex == 7 ? ArrearsStage.PreLitigationOrIntentToLien45Days : ArrearsStage.Current,
                    IsShortTermRental = unitIndex == 9,
                    ParkingBayNumber = $"G-{unitIndex + 1:00}"
                };
                unit.Meters.Add(new UtilityMeter { UnitId = unit.Id, Kind = UtilityKind.ColdWater, SerialNumber = $"CW-{unit.UnitNumber}-2023", PreviousReadingValue = 118.4m, LastReadingValue = 122.6m, RollingMonthlyAverage = 4.2m });
                unit.Meters.Add(new UtilityMeter { UnitId = unit.Id, Kind = UtilityKind.HotWater, SerialNumber = $"HW-{unit.UnitNumber}-2023", PreviousReadingValue = 64.1m, LastReadingValue = 66.9m, RollingMonthlyAverage = 2.8m });
                unit.Meters.Add(new UtilityMeter { UnitId = unit.Id, Kind = UtilityKind.DistrictHeating, SerialNumber = $"DH-{unit.UnitNumber}-2023", PreviousReadingValue = 8.2m, LastReadingValue = 8.75m, RollingMonthlyAverage = 0.55m });
                community.Units.Add(unit);
                unitIndex++;
            }
        }
        // Remaining share to reach 10000: commercial and garage units.
        community.Units.Add(new Unit { CommunityId = community.Id, BuildingId = building.Id, UnitNumber = "C-001", Kind = UnitKind.CommercialPremise, FloorNumber = 0, VerticalStackCode = "STACK-A2", FloorAreaM2 = 96m, ShareNumerator = 700, ShareDenominator = 10000, DeclaredOccupants = 4, PrimaryOwnerName = "Parkowa Retail Sp. z o.o.", PrimaryOwnerEmail = "retail@example.com", PrimaryOwnerPhone = "+48 22 555 02 00", VirtualIbanOrLockboxCode = "PL61 1090 1014 0000 0712 1981 2901" });
        community.Units.Add(new Unit { CommunityId = community.Id, BuildingId = building.Id, UnitNumber = "G-01", Kind = UnitKind.GarageBay, FloorNumber = 0, VerticalStackCode = "STACK-B1", FloorAreaM2 = 18m, ShareNumerator = 300, ShareDenominator = 10000, DeclaredOccupants = 0, PrimaryOwnerName = "Garage Bay Owner", PrimaryOwnerEmail = "garage@example.com", PrimaryOwnerPhone = "+48 600 000 001", VirtualIbanOrLockboxCode = "PL61 1090 1014 0000 0712 1981 2902" });

        var leakUnit = community.Units.First(u => u.UnitNumber == "A-301");
        var leakMeter = leakUnit.Meters.First(m => m.Kind == UtilityKind.ColdWater);
        leakMeter.LastReadingValue = 137.8m;
        leakMeter.RollingMonthlyAverage = 4.0m;
        leakMeter.HasSilentLeakAnomaly = true;
        leakMeter.AnomalyNote = "Consumption 15.4 m3 vs 4.0 m3 rolling average (3.8x) - silent toilet cistern valve leak suspected.";
        leakMeter.Readings.Add(new MeterReading { UtilityMeterId = leakMeter.Id, ReadingValue = 137.8m, ConsumptionDelta = 15.4m, IsAnomaly = true, AnomalyReason = "3.8x rolling average" });

        var atlas = new[]
        {
            ("Main Cold Water Shutoff Valve - Basement Corridor A (Door #2)", "WaterValve", "STACK-A1 and STACK-A2", "Basement corridor A, door 2", "Close the blue gate valve clockwise, then open the drain tap to depressurize the riser.", "#0284c7"),
            ("Vertical Riser STACK-A1 Hot Water and Heating Isolation Manifold", "HeatingValve", "STACK-A1", "Basement manifold room, red manifold", "Close both red isolation valves, note the pressure gauge reading, and tag the manifold.", "#dc2626"),
            ("Main Emergency Gas Shutoff Lever - Exterior East Wall Box", "GasValve", "Building", "Exterior east wall, yellow box", "Pull the yellow lever down 90 degrees and call gas emergency 992.", "#ca8a04"),
            ("Main Fire Electrical Breaker and Stairwell Board", "ElectricalBreaker", "Building", "Stairwell A electrical board", "Switch the main breaker to OFF and call power grid emergency 991.", "#ea580c")
        };
        foreach (var item in atlas)
        {
            var photo = renderer.CreateAnnotatedValvePhoto(item.Item2, item.Item1, item.Item6);
            community.InfrastructurePhotos.Add(new InfrastructurePhoto
            {
                CommunityId = community.Id, BuildingId = building.Id, Title = item.Item1, Category = item.Item2,
                RiserStackOrZone = item.Item3, PhysicalLocationNote = item.Item4, ShutoffInstructions = item.Item5,
                ColorTag = item.Item6, ImageDataUrl = photo, SamAnnotatedDataUrl = photo
            });
        }

        community.AccessCodes.Add(new BuildingAccessCode { CommunityId = community.Id, BuildingId = building.Id, TargetArea = "Stairwell A Intercom", ResidentCode = "#4812*", ContractorOrCourierCode = "#9021*", KeySafeLocation = "Stairwell A entrance panel" });
        community.AccessCodes.Add(new BuildingAccessCode { CommunityId = community.Id, BuildingId = building.Id, TargetArea = "Stairwell B Intercom", ResidentCode = "#5530*", ContractorOrCourierCode = "#9021*", KeySafeLocation = "Stairwell B entrance panel" });
        community.AccessCodes.Add(new BuildingAccessCode { CommunityId = community.Id, BuildingId = building.Id, TargetArea = "Waste Enclosure", ResidentCode = "2580#", ContractorOrCourierCode = "2580#", KeySafeLocation = "Courtyard waste enclosure gate" });
        community.AccessCodes.Add(new BuildingAccessCode { CommunityId = community.Id, BuildingId = building.Id, TargetArea = "Basement Boiler and Valve Room Key Safe", ResidentCode = "7741", ContractorOrCourierCode = "#9021*", KeySafeLocation = "Box #3, PIN 7741, basement corridor A" });

        var emergency = new[]
        {
            ("EmergencyService", "General emergency", "112", 1), ("EmergencyService", "Fire brigade", "998", 2),
            ("EmergencyService", "Ambulance", "999", 3), ("EmergencyService", "Police", "997", 4),
            ("UtilityEmergency", "Gas emergency", "992", 5), ("UtilityEmergency", "Power grid emergency", "991", 6),
            ("UtilityEmergency", "District heating emergency", "993", 7), ("UtilityEmergency", "Water and sewage emergency", "994", 8),
            ("LocalAuthority", "District Police Precinct #4", "+48 22 555 03 04", 9), ("LocalAuthority", "Municipal Guard Eco-Patrol", "+48 22 555 03 05", 10),
            ("LocalAuthority", "District Building Inspectorate (PINB)", "+48 22 555 03 06", 11), ("OnCallTechnician", "24/7 Otis Elevator Rescue", "+48 800 555 014", 12)
        };
        foreach (var e in emergency)
        {
            community.EmergencyContacts.Add(new EmergencyAndAuthorityContact { CommunityId = community.Id, Category = e.Item1, Name = e.Item2, PhoneNumber = e.Item3, SortOrder = e.Item4 });
        }

        community.UtilityProviderSources.Add(new UtilityProviderSource { CommunityId = community.Id, UtilityKind = UtilityKind.Electricity, ProviderName = "Regional Electricity DSO", OutageBulletinUrl = "https://example.com/outages/electricity", LiveFaultMapUrl = "https://example.com/maps/electricity", TariffScheduleUrl = "https://example.com/tariffs/electricity", EmergencyPhone = "991", CurrentUnitTariff = 0.88m, TariffUnit = "EUR/kWh" });
        community.UtilityProviderSources.Add(new UtilityProviderSource { CommunityId = community.Id, UtilityKind = UtilityKind.ColdWater, ProviderName = "Municipal Water and Sewage Works", OutageBulletinUrl = "https://example.com/outages/water", LiveFaultMapUrl = "https://example.com/maps/water", TariffScheduleUrl = "https://example.com/tariffs/water", EmergencyPhone = "994", CurrentUnitTariff = 11.50m, TariffUnit = "EUR/m3" });
        community.UtilityProviderSources.Add(new UtilityProviderSource { CommunityId = community.Id, UtilityKind = UtilityKind.DistrictHeating, ProviderName = "Municipal District Heating Network", OutageBulletinUrl = "https://example.com/outages/heating", LiveFaultMapUrl = "https://example.com/maps/heating", TariffScheduleUrl = "https://example.com/tariffs/heating", EmergencyPhone = "993", CurrentUnitTariff = 82.00m, TariffUnit = "EUR/GJ" });
        community.UtilityProviderSources.Add(new UtilityProviderSource { CommunityId = community.Id, UtilityKind = UtilityKind.Gas, ProviderName = "Regional Gas Distribution", OutageBulletinUrl = "https://example.com/outages/gas", LiveFaultMapUrl = "https://example.com/maps/gas", TariffScheduleUrl = "https://example.com/tariffs/gas", EmergencyPhone = "992", CurrentUnitTariff = 2.40m, TariffUnit = "EUR/m3" });
        community.UtilityProviderSources.Add(new UtilityProviderSource { CommunityId = community.Id, UtilityKind = UtilityKind.MunicipalWaste, ProviderName = "City Waste Management", OutageBulletinUrl = "https://example.com/waste/schedule", LiveFaultMapUrl = "https://example.com/waste/map", TariffScheduleUrl = "https://example.com/waste/tariffs", EmergencyPhone = "+48 22 555 01 00", CurrentUnitTariff = 28.00m, TariffUnit = "EUR/person/mo" });

        community.UtilityOutages.Add(new UtilityOutageNotice
        {
            CommunityId = community.Id, UtilityKind = UtilityKind.DistrictHeating, ProviderName = "Municipal District Heating Network",
            AffectedStreets = "Parkowa Ave 10-20", StartAt = DateTime.UtcNow.AddHours(-1), EstimatedEndAt = DateTime.UtcNow.AddHours(2.5),
            IsEmergency = true, Summary = "Emergency district heating and hot water shutoff for Parkowa Ave 10-20. Operator restoration ETA in 2.5 hours.", IsActive = true
        });

        var accounts = new[]
        {
            ("100-OP", "Operating Cash", FundKind.Operating, LedgerAccountKind.Asset, 18420m),
            ("110-REP", "Repair Fund Cash", FundKind.RepairReserve, LedgerAccountKind.Asset, 46280m),
            ("130-AR", "Unit Arrears Receivable", FundKind.Operating, LedgerAccountKind.Asset, 2100m),
            ("200-AP", "Contractor Payable", FundKind.Operating, LedgerAccountKind.Liability, -3200m),
            ("400-ASSESS", "Assessment Revenue", FundKind.Operating, LedgerAccountKind.Revenue, -52100m),
            ("450-COMM", "Ancillary Commercial Revenue", FundKind.CommercialAncillary, LedgerAccountKind.Revenue, -2600m),
            ("500-MAINT", "Maintenance Expense", FundKind.Operating, LedgerAccountKind.Expense, 8940m),
            ("520-UTIL", "Utilities Clearing", FundKind.UtilitiesClearing, LedgerAccountKind.Expense, 2240m)
        };
        foreach (var a in accounts)
        {
            community.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = a.Item1, Name = a.Item2, Fund = a.Item3, Kind = a.Item4, Balance = a.Item5 });
        }
        var cash = community.LedgerAccounts[0];
        var revenue = community.LedgerAccounts[4];
        community.JournalEntries.Add(new JournalEntry
        {
            CommunityId = community.Id, ReferenceNumber = "JE-2026-1101", Description = "November monthly assessments billed", Fund = FundKind.Operating,
            Lines =
            [
                new JournalLine { LedgerAccountId = cash.Id, AccountCode = cash.Code, AccountName = cash.Name, Debit = 5210m },
                new JournalLine { LedgerAccountId = revenue.Id, AccountCode = revenue.Code, AccountName = revenue.Name, Credit = 5210m }
            ]
        });

        community.CommercialContracts.Add(new AncillaryCommercialContract { CommunityId = community.Id, TenantOrAdvertiserName = "City Media Outdoor", AssetType = "FacadeBillboard", MonthlyGrossRevenue = 1450m });
        community.CommercialContracts.Add(new AncillaryCommercialContract { CommunityId = community.Id, TenantOrAdvertiserName = "Telecom Operator 5G", AssetType = "Rooftop5gAntenna", MonthlyGrossRevenue = 1150m });

        var vendorPlumber = new Vendor { CommunityId = community.Id, Name = "Hydro-Instal Plumbing Services", Trade = WorkOrderCategory.Plumbing, Phone = "+48 22 555 01 18", Email = "service@hydro-instal.example.com", Website = "https://example.com/hydro-instal", TaxId = "525-111-22-33", IbanOrRouting = "PL61 1090 1014 0000 0712 1981 4821", Rating = 4.8m, TypicalHourlyRate = 65m };
        var vendorFacade = new Vendor { CommunityId = community.Id, Name = "Dach-Bud Roofing and Facade", Trade = WorkOrderCategory.RoofAndFacade, Phone = "+48 22 555 01 31", Email = "quotes@dach-bud.example.com", Website = "https://example.com/dach-bud", TaxId = "525-222-33-44", IbanOrRouting = "PL61 1090 1014 0000 0712 1981 4930", Rating = 4.9m, TypicalHourlyRate = 75m };
        var vendorElectrical = new Vendor { CommunityId = community.Id, Name = "Elektro-Serwis Electrical", Trade = WorkOrderCategory.Electrical, Phone = "+48 22 555 01 24", Email = "office@elektro-serwis.example.com", Website = "https://example.com/elektro-serwis", TaxId = "525-333-44-55", IbanOrRouting = "PL61 1090 1014 0000 0712 1981 5012", Rating = 4.7m, TypicalHourlyRate = 70m };
        community.Vendors.AddRange([vendorPlumber, vendorFacade, vendorElectrical]);

        community.BankTransactions.Add(new BankTransactionMatch { CommunityId = community.Id, Amount = 416m, PayerName = "Anna Kowalska", RawTransferTitle = "November advance A-101", MatchedUnitId = community.Units[0].Id, MatchedUnitNumber = "A-101", MatchConfidence = 0.99, MatchRationale = "Virtual IBAN and unit number matched.", IsAppliedToLedger = true });
        community.BankTransactions.Add(new BankTransactionMatch { CommunityId = community.Id, Amount = 416m, PayerName = "Anna Nowak (grandmother)", RawTransferTitle = "Transfer from grandmother Anna for grandson flat 201", MatchedUnitId = community.Units.First(u => u.UnitNumber == "A-201").Id, MatchedUnitNumber = "A-201", MatchConfidence = 0.91, MatchRationale = "Flat number 201 extracted from unstructured title; payer surname differs from owner.", IsAppliedToLedger = false });
        community.BankTransactions.Add(new BankTransactionMatch { CommunityId = community.Id, Amount = 300m, PayerName = "Unknown tenant", RawTransferTitle = "Rent and charges October", MatchConfidence = 0.42, MatchRationale = "No unit reference found; requires manager review.", IsAppliedToLedger = false });

        community.ReserveComponents.Add(new ReserveStudyComponent { CommunityId = community.Id, ComponentName = "Flat Roof EPDM Membrane", InstallationYear = 2023, UsefulLifeYears = 20, RemainingLifeYears = 17, EstimatedReplacementCost = 68000m, ConditionScore = 4 });
        community.ReserveComponents.Add(new ReserveStudyComponent { CommunityId = community.Id, ComponentName = "District Heating Heat Exchanger", InstallationYear = 2023, UsefulLifeYears = 15, RemainingLifeYears = 12, EstimatedReplacementCost = 42000m, ConditionScore = 4 });
        community.ReserveComponents.Add(new ReserveStudyComponent { CommunityId = community.Id, ComponentName = "Elevator Modernization", InstallationYear = 2023, UsefulLifeYears = 25, RemainingLifeYears = 22, EstimatedReplacementCost = 95000m, ConditionScore = 5 });
        community.ReserveComponents.Add(new ReserveStudyComponent { CommunityId = community.Id, ComponentName = "Stairwell Acoustic and LED Retrofit", InstallationYear = 2023, UsefulLifeYears = 10, RemainingLifeYears = 7, EstimatedReplacementCost = 18000m, ConditionScore = 3 });

        community.GreenSubsidies.Add(new GreenSubsidyProgram { CommunityId = community.Id, ProgramName = "Thermal Modernization Grant", AuthorityName = "National Fund for Environmental Protection", UpgradeScope = "Facade insulation and window replacement", EstimatedGrossCapex = 180000m, GrantSubsidyPercent = 30m, GrantSubsidyAmount = 54000m, NetCommunityCapex = 126000m, EstimatedAnnualSavings = 18000m, PaybackYears = 7.0m });
        community.GreenSubsidies.Add(new GreenSubsidyProgram { CommunityId = community.Id, ProgramName = "Rooftop Solar PV Grant", AuthorityName = "Municipal Energy Office", UpgradeScope = "Rooftop solar PV 40 kWp", EstimatedGrossCapex = 72000m, GrantSubsidyPercent = 40m, GrantSubsidyAmount = 28800m, NetCommunityCapex = 43200m, EstimatedAnnualSavings = 9600m, PaybackYears = 4.5m });
        community.GreenSubsidies.Add(new GreenSubsidyProgram { CommunityId = community.Id, ProgramName = "Heat Pump and LED Retrofit Grant", AuthorityName = "Regional Energy Agency", UpgradeScope = "Heat pump assist and common-area LED", EstimatedGrossCapex = 54000m, GrantSubsidyPercent = 35m, GrantSubsidyAmount = 18900m, NetCommunityCapex = 35100m, EstimatedAnnualSavings = 7200m, PaybackYears = 4.9m });

        community.WasteSchedules.Add(new WasteCollectionSchedule { CommunityId = community.Id, WasteFraction = "Mixed", CollectionDaysSummary = "Tuesday and Friday", NextPickupDate = DateTime.UtcNow.AddDays(1) });
        community.WasteSchedules.Add(new WasteCollectionSchedule { CommunityId = community.Id, WasteFraction = "Plastics and metals", CollectionDaysSummary = "Wednesday", NextPickupDate = DateTime.UtcNow.AddDays(2), ContaminationWarningActive = true });
        community.WasteSchedules.Add(new WasteCollectionSchedule { CommunityId = community.Id, WasteFraction = "Paper", CollectionDaysSummary = "Thursday", NextPickupDate = DateTime.UtcNow.AddDays(3) });
        community.WasteSchedules.Add(new WasteCollectionSchedule { CommunityId = community.Id, WasteFraction = "Bio", CollectionDaysSummary = "Monday", NextPickupDate = DateTime.UtcNow.AddDays(4) });
        community.WasteSchedules.Add(new WasteCollectionSchedule { CommunityId = community.Id, WasteFraction = "Bulky furniture", CollectionDaysSummary = "First Saturday of the month", NextPickupDate = DateTime.UtcNow.AddDays(6) });

        community.Inspections.Add(new StatutoryInspection { CommunityId = community.Id, BuildingId = building.Id, Kind = InspectionKind.AnnualStructuralAndCommon, LastPerformedDate = DateTime.UtcNow.AddMonths(-8), NextDueDate = DateTime.UtcNow.AddMonths(4), InspectorCompany = "BuildSafe Inspections", Status = "Valid", SummaryOfDefects = "No structural defects; minor stairwell paint wear." });
        community.Inspections.Add(new StatutoryInspection { CommunityId = community.Id, BuildingId = building.Id, Kind = InspectionKind.AnnualChimneyAndVentilation, LastPerformedDate = DateTime.UtcNow.AddMonths(-11), NextDueDate = DateTime.UtcNow.AddDays(21), InspectorCompany = "Chimney Master Guild", Status = "DueWithin30Days", SummaryOfDefects = "Ventilation grille cleaning recommended in stairwell B." });
        community.Inspections.Add(new StatutoryInspection { CommunityId = community.Id, BuildingId = building.Id, Kind = InspectionKind.AnnualGasTightness, LastPerformedDate = DateTime.UtcNow.AddMonths(-6), NextDueDate = DateTime.UtcNow.AddMonths(6), InspectorCompany = "GasSafe Certified", Status = "Valid", SummaryOfDefects = "Gas installation tight; no leaks detected." });
        community.Inspections.Add(new StatutoryInspection { CommunityId = community.Id, BuildingId = building.Id, Kind = InspectionKind.FiveYearStructuralAndFacade, LastPerformedDate = DateTime.UtcNow.AddYears(-3), NextDueDate = DateTime.UtcNow.AddYears(2), InspectorCompany = "BuildSafe Inspections", Status = "Valid", SummaryOfDefects = "Facade hairline crack on north wing under observation." });
        community.Inspections.Add(new StatutoryInspection { CommunityId = community.Id, BuildingId = building.Id, Kind = InspectionKind.FiveYearElectricalAndLightning, LastPerformedDate = DateTime.UtcNow.AddYears(-3), NextDueDate = DateTime.UtcNow.AddYears(2), InspectorCompany = "Elektro-Serwis Electrical", Status = "Valid", SummaryOfDefects = "Insulation resistance within norms." });

        var (leakBase, leakOverlay, leakArea) = renderer.CreateDefectBeforeAndOverlay("water stain");
        var boq = BoqCostEstimator.EstimateRepairBoq("water stain", leakArea);
        var wo1 = new WorkOrder
        {
            CommunityId = community.Id, BuildingId = building.Id, UnitId = leakUnit.Id, UnitNumber = leakUnit.UnitNumber, VerticalStackCode = "STACK-A1",
            Title = "Ceiling water leak between A-401 and A-301 on STACK-A1", Description = "Water stain growing on the bathroom ceiling; riser tee joint suspected.",
            OriginalResidentMessage = "There is a water leak on the ceiling in my bathroom, the stain is growing since yesterday.", OriginalLanguage = "EN",
            Category = WorkOrderCategory.Plumbing, Priority = WorkOrderPriority.High, Boundary = ResponsibilityBoundary.CommonProperty,
            BoundaryRationale = "Vertical riser tee joint is common property serving multiple units.", CitedLegalDocumentCode = "ACT-OWN-1994",
            CitedVerbatimLegalExcerpt = "Art. 3 ust. 2: Common property includes vertical risers serving more than one unit.",
            Status = WorkOrderStatus.PendingSamVerification, DefectConcept = "water stain", MeasureAreaM2 = leakArea,
            InvestorBoqEstimateAmount = boq.TotalGrossEstimate, InvestorBoqSummary = $"Material {boq.TotalMaterialCost:N2} EUR, labor {boq.TotalLaborCost:N2} EUR.",
            AssignedVendorId = vendorPlumber.Id, AssignedVendorName = vendorPlumber.Name,
            AttachedAccessCodesSummary = "Contractor code #9021* (Stairwell A), never the permanent resident code.",
            AttachedShutoffValveNote = "Main Cold Water Shutoff Valve - Basement Corridor A (Door #2), blue valve.",
            BeforePhotoDataUrl = leakBase, SamBeforeOverlayDataUrl = leakOverlay
        };
        wo1.Quotes.Add(new VendorQuote { WorkOrderId = wo1.Id, VendorId = vendorPlumber.Id, VendorName = vendorPlumber.Name, QuotedGrossAmount = Math.Round(boq.TotalGrossEstimate * 1.06m, 2), WarrantyMonths = 24, LeadTimeDays = 2, VarianceVsBoqPercent = 6m, IsRecommended = true });
        wo1.Quotes.Add(new VendorQuote { WorkOrderId = wo1.Id, VendorId = vendorFacade.Id, VendorName = vendorFacade.Name, QuotedGrossAmount = Math.Round(boq.TotalGrossEstimate * 1.22m, 2), WarrantyMonths = 12, LeadTimeDays = 5, VarianceVsBoqPercent = 22m });
        community.WorkOrders.Add(wo1);
        community.VendorBills.Add(new VendorBill { CommunityId = community.Id, VendorId = vendorPlumber.Id, VendorName = vendorPlumber.Name, WorkOrderId = wo1.Id, InvoiceNumber = "INV-2026-118", GrossAmount = Math.Round(boq.TotalGrossEstimate * 1.06m, 2), TargetFund = FundKind.Operating, SuggestedLedgerAccountCode = "500-MAINT", IsRepairVerified = false, Status = "PendingSamVerification" });

        var (crackBase, crackOverlay, crackArea) = renderer.CreateDefectBeforeAndOverlay("wall crack");
        community.WorkOrders.Add(new WorkOrder
        {
            CommunityId = community.Id, BuildingId = building.Id, VerticalStackCode = "STACK-B1",
            Title = "Facade hairline crack on north wing", Description = "Hairline crack on the north facade, length approx. 2.4 m.",
            OriginalResidentMessage = "There is a crack on the exterior facade wall.", OriginalLanguage = "EN",
            Category = WorkOrderCategory.RoofAndFacade, Priority = WorkOrderPriority.Normal, Boundary = ResponsibilityBoundary.DeveloperWarranty,
            BoundaryRationale = "Facade is within the 5-year statutory warranty from handover.", CitedLegalDocumentCode = "CIV-WARR-568",
            CitedVerbatimLegalExcerpt = "Civil Code Art. 568 par. 1: The seller is liable under statutory warranty for physical defects of a building for 5 years from handover.",
            Status = WorkOrderStatus.RoutedToWarranty, DefectConcept = "wall crack", MeasureAreaM2 = crackArea,
            InvestorBoqEstimateAmount = BoqCostEstimator.EstimateRepairBoq("wall crack", crackArea).TotalGrossEstimate,
            InvestorBoqSummary = "BOQ computed for reference; cost borne by developer under warranty.",
            BeforePhotoDataUrl = crackBase, SamBeforeOverlayDataUrl = crackOverlay
        });

        var (fireBase, fireOverlay, fireArea) = renderer.CreateDefectBeforeAndOverlay("blocked fire lane");
        community.WorkOrders.Add(new WorkOrder
        {
            CommunityId = community.Id, BuildingId = building.Id, VerticalStackCode = "STACK-A2",
            Title = "Blocked fire access lane and overflowing bulk waste shelter", Description = "Vehicle blocking the fire lane; bulk waste shelter overflowing.",
            OriginalResidentMessage = "A car is blocking the fire access lane and the waste shelter is overflowing.", OriginalLanguage = "EN",
            Category = WorkOrderCategory.FireAndSafety, Priority = WorkOrderPriority.High, Boundary = ResponsibilityBoundary.CommonProperty,
            BoundaryRationale = "Fire lanes and waste enclosures are common property and safety obligations.", CitedLegalDocumentCode = "RULES-AMBER",
            CitedVerbatimLegalExcerpt = "House Rules par. 8: Fire access lanes must remain unobstructed at all times.",
            Status = WorkOrderStatus.Reported, DefectConcept = "blocked fire lane", MeasureAreaM2 = fireArea,
            InvestorBoqEstimateAmount = 240m, InvestorBoqSummary = "Towing coordination and additional bulky waste collection.",
            BeforePhotoDataUrl = fireBase, SamBeforeOverlayDataUrl = fireOverlay
        });

        var unitA202 = community.Units.First(u => u.UnitNumber == "A-202");
        var unitB301 = community.Units.First(u => u.UnitNumber == "B-303");
        community.ModificationRequests.Add(new ModificationOrArcRequest { CommunityId = community.Id, UnitId = unitA202.Id, UnitNumber = unitA202.UnitNumber, OwnerName = unitA202.PrimaryOwnerName, Kind = ModificationRequestKind.FacadeAirConditioner, Description = "Install split air conditioner on the facade with condensate drain to the balcony outlet.", StatutoryProtectionNote = "Board consent required for facade attachments under Statute par. 9.", CitedLegalDocumentCode = "STATUTE-AMBER", CitedVerbatimLegalExcerpt = "Statute par. 9: Facade and common elements may not be altered without board consent.", AiRecommendation = "ApproveWithConditions", TechnicalConditionsOrDecisionText = "Approve with conditions: match facade color, do not penetrate the insulation anchor zone, drain condensate to the balcony outlet.", Status = "PendingBoardReview" });
        community.ModificationRequests.Add(new ModificationOrArcRequest { CommunityId = community.Id, UnitId = unitB301.Id, UnitNumber = unitB301.UnitNumber, OwnerName = unitB301.PrimaryOwnerName, Kind = ModificationRequestKind.PartitionWallChange, Description = "Open a non-load-bearing partition wall between kitchen and living room.", StatutoryProtectionNote = "Non-load-bearing partitions require notification; load-bearing walls require a permit under Construction Law Art. 71.", CitedLegalDocumentCode = "BLD-LAW-62", CitedVerbatimLegalExcerpt = "Construction Law Art. 71: Alteration of load-bearing elements requires a permit and structural design.", AiRecommendation = "ApproveWithConditions", TechnicalConditionsOrDecisionText = "Approve with conditions: licensed contractor, weekday hours 08:00-18:00, freight elevator booking for debris.", Status = "PendingBoardReview" });

        var res1 = new Resolution { CommunityId = community.Id, Number = "Res 01/2026", Title = "Adoption of Annual Financial Report and Board Discharge", Content = "Adopt the closed-year financial statements and grant discharge to the board.", MajorityRule = VotingMajorityRule.ShareWeightedMajority, ComplianceStatus = "VerifiedClean" };
        var voteUnits = community.Units.Where(u => u.Kind == UnitKind.Apartment).Take(7).ToList();
        var choices = new[] { VoteChoice.For, VoteChoice.For, VoteChoice.For, VoteChoice.For, VoteChoice.For, VoteChoice.Against, VoteChoice.Abstain };
        for (var i = 0; i < voteUnits.Count; i++)
        {
            res1.Votes.Add(new Vote { ResolutionId = res1.Id, UnitId = voteUnits[i].Id, UnitNumber = voteUnits[i].UnitNumber, VoterName = voteUnits[i].PrimaryOwnerName, Choice = choices[i], OwnershipShareWeight = voteUnits[i].OwnershipShare, Channel = VoteSourceChannel.WebPortal });
        }
        community.Resolutions.Add(res1);

        var (optA, optB) = (renderer.CreateRenovationOptionPreview("Option A - Green courtyard", true), renderer.CreateRenovationOptionPreview("Option B - Bicycle shelter", false));
        var res2 = new Resolution { CommunityId = community.Id, Number = "Res 02/2026", Title = "Courtyard Green Modernization and Bicycle Shelter", Content = "Approve courtyard modernization financed from the repair fund.", MajorityRule = VotingMajorityRule.ShareWeightedMajority, ComplianceStatus = "VerifiedClean", OptionALabel = "Option A - Green courtyard", OptionAPreviewDataUrl = optA, OptionBLabel = "Option B - Bicycle shelter", OptionBPreviewDataUrl = optB, QrPosterDataUrl = renderer.CreateQrStairwellNoticePoster("https://commonground.example.com/vote/res-02-2026") };
        community.Resolutions.Add(res2);

        community.Resolutions.Add(new Resolution
        {
            CommunityId = community.Id, Number = "Res 03/2026", Title = "Retroactive Special Repair Levy and 3-Day Notice Change",
            Content = "Introduce a retroactive special repair levy allocated equally per unit with a 3-day notice period.",
            MajorityRule = VotingMajorityRule.ShareWeightedMajority, ComplianceStatus = "ConflictDetected",
            ComplianceConflictSummary = "Violates Statute par. 14(3) 14-day notice rule and par. 22(1) share allocation rule, and ACT-OWN-1994 Art. 12 ust. 2 and Art. 32.",
            CitedLegalDocumentCode = "ACT-OWN-1994",
            CitedVerbatimLegalExcerpt = "Art. 12 ust. 2: Owners bear common property costs in proportion to their shares. Art. 32: Draft resolutions require at least 14 days notice.",
            SuggestedCompliantRewrite = "Adopt the special repair levy effective the first day of the month following 14 days written notice, allocated in proportion to ownership shares, payable in three monthly installments."
        });

        community.LegalDocuments.AddRange(LegalCorpusFactory.Build(community));
        community.Meetings.Add(new MeetingRecord { CommunityId = community.Id, Title = "Annual Owners Assembly 2026", ScheduledAt = DateTime.UtcNow.AddDays(14), ConferenceUrl = "https://meet.example.com/amber-park-annual", DiarizedTranscriptText = "[Speaker 1 - Chair] We open the annual meeting and confirm quorum by shares. [Speaker 2 - Manager] The financial report and reserve projection are presented.", GeneratedMinutesMarkdown = "Quorum confirmed. Financial report presented. Resolutions voted by share weight.", ExtractedActionItemsSummary = "Publish adopted resolutions within 7 days; tender courtyard works; issue updated unit statements." });

        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Chief Property Manager", Email = "manager@amberpark.example.com", Provider = IdentityProvider.EmailPassword, ProviderSubjectId = "email-manager", PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("Manager-2026!"), Role = WorkspaceSecurityRole.PropertyManagerAdmin });
        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Property Manager (Facebook)", Email = "manager.fb@amberpark.example.com", Provider = IdentityProvider.Facebook, ProviderSubjectId = "fb-1029481", Role = WorkspaceSecurityRole.PropertyManagerAdmin });
        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Accounting Auditor (Google)", Email = "auditor@amberpark.example.com", Provider = IdentityProvider.Google, ProviderSubjectId = "google-oauth2-88412", Role = WorkspaceSecurityRole.AccountingAuditor });
        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Unit A-301 Owner (Apple)", Email = "owner.a301@privaterelay.example.com", Provider = IdentityProvider.Apple, ProviderSubjectId = "apple-id-001942", Role = WorkspaceSecurityRole.BoardMemberReadOnly, LinkedUnitNumber = "A-301" });
        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Maintenance Dispatcher", Email = "dispatch@amberpark.example.com", Provider = IdentityProvider.EmailPassword, ProviderSubjectId = "email-dispatch", PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2("Dispatch-2026!"), Role = WorkspaceSecurityRole.MaintenanceDispatcher });
        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Board Chair (Facebook)", Email = "board@amberpark.example.com", Provider = IdentityProvider.Facebook, ProviderSubjectId = "fb-2048812", Role = WorkspaceSecurityRole.BoardMemberReadOnly });

        community.AiActions.Add(new AiAction { CommunityId = community.Id, Kind = AiActionKind.DispatchContractorWorkOrder, AgentName = "IntakeAndTriageAgent", Title = "Dispatch plumber to STACK-A1 ceiling leak", Rationale = "Common riser tee joint; SAM measured 4.35 m2; BOQ estimate computed; contractor code #9021* attached.", ModelUsed = "muse-spark-1.3 + sam-3.1", Confidence = 0.94, TargetEntitySummary = "WorkOrder WO-101, Unit A-301", ProposedPayloadJson = "{\"workOrder\":\"WO-101\",\"vendor\":\"Hydro-Instal\"}" });
        community.AiActions.Add(new AiAction { CommunityId = community.Id, Kind = AiActionKind.SendWarrantyDefectClaim, AgentName = "WarrantyAndDefectClaimAgent", Title = "Send developer warranty claim for facade crack", Rationale = "Facade within 5-year statutory warranty (CIV-WARR-568 Art. 568 par. 1); community funds must not be billed.", ModelUsed = "muse-spark-1.3", Confidence = 0.96, TargetEntitySummary = "WorkOrder WO-102, north facade", ProposedPayloadJson = "{\"claim\":\"facade-crack\"}" });
        community.AiActions.Add(new AiAction { CommunityId = community.Id, Kind = AiActionKind.MatchBankTransferToUnit, AgentName = "InvoiceBankAndTariffAuditAgent", Title = "Match unstructured transfer to unit A-201", Rationale = "Title references flat 201; FIFO arrears allocation computed deterministically.", ModelUsed = "muse-spark-1.3", Confidence = 0.91, TargetEntitySummary = "BankTransactionMatch, 416.00 EUR", ProposedPayloadJson = "{\"unit\":\"A-201\",\"amount\":416}" });
        community.AiActions.Add(new AiAction { CommunityId = community.Id, Kind = AiActionKind.BroadcastUtilityOutageAlert, AgentName = "WebAutoEnrichmentOutageAndGrantAgent", Title = "Broadcast district heating outage alert", Rationale = "Active municipal outage for Parkowa Ave 10-20; contractor dispatch suppressed until operator ETA.", ModelUsed = "muse-wonder", Confidence = 0.98, TargetEntitySummary = "UtilityOutageNotice, DistrictHeating", ProposedPayloadJson = "{\"utility\":\"DistrictHeating\"}" });

        community.AgentRuns.Add(new AgentRun { CommunityId = community.Id, AgentName = "IntakeAndTriageAgent", TriggerSummary = "Resident leak report from A-301", ModelName = "muse-spark-1.3 + sam-3.1", TotalLatencyMs = 1240, TotalTokens = 1820, UsedLiveApi = false, Steps = [new AgentStep { StepOrder = 1, StepTitle = "Triage and boundary check", ModelOrToolName = "muse-spark-1.3", Detail = "CommonProperty riser; ACT-OWN-1994 Art. 3 ust. 2 cited.", LatencyMs = 420 }, new AgentStep { StepOrder = 2, StepTitle = "Defect segmentation", ModelOrToolName = "sam-3.1", Detail = "Water stain measured at 4.35 m2.", LatencyMs = 480 }, new AgentStep { StepOrder = 3, StepTitle = "BOQ estimate", ModelOrToolName = "BoqCostEstimator", Detail = "Investor BOQ computed deterministically.", LatencyMs = 40 }] });
        community.AgentRuns.Add(new AgentRun { CommunityId = community.Id, AgentName = "WebAutoEnrichmentOutageAndGrantAgent", TriggerSummary = "Public registry sync for 14 Parkowa Ave", ModelName = "muse-wonder", TotalLatencyMs = 2100, TotalTokens = 3400, UsedLiveApi = false, Steps = [new AgentStep { StepOrder = 1, StepTitle = "Registry and legal corpus acquisition", ModelOrToolName = "muse-wonder", Detail = "7 legal documents acquired verbatim.", LatencyMs = 1200 }] });
        community.AgentRuns.Add(new AgentRun { CommunityId = community.Id, AgentName = "MeetingAutopilotAndComplianceAgent", TriggerSummary = "Annual meeting package draft", ModelName = "muse-wonder + muse-spark-1.3", TotalLatencyMs = 2650, TotalTokens = 196000, UsedLiveApi = false, Steps = [new AgentStep { StepOrder = 1, StepTitle = "1M-context compliance check", ModelOrToolName = "muse-spark-1.3", Detail = "Res 03/2026 conflict detected against ACT-OWN-1994.", LatencyMs = 1450 }] });
        community.AgentRuns.Add(new AgentRun { CommunityId = community.Id, AgentName = "MeterReadingAndUtilityAgent", TriggerSummary = "Meter photo OCR for A-301", ModelName = "sam-3.1 + muse-spark-1.3", TotalLatencyMs = 790, TotalTokens = 640, UsedLiveApi = false, Steps = [new AgentStep { StepOrder = 1, StepTitle = "Meter OCR and anomaly check", ModelOrToolName = "muse-spark-1.3", Detail = "3.8x consumption spike flagged as silent leak.", LatencyMs = 310 }] });

        var previous = "GENESIS-0000000000000000";
        foreach (var (category, severity, resource, summary) in new[]
        {
            ("PII_REDACTION_APPLIED", "Info", "MetaModelClients outbound prompt", "PII redaction applied before external model call; tokens replaced with pseudonymous references."),
            ("STATUTE_CORPUS_ACQUIRED", "Info", "LegalDocumentSource corpus", "7 statutory and governing documents acquired verbatim from public registries."),
            ("SECRET_VAULT_SEALED", "Info", "BuildingAccessCode vault", "Resident intercom and key-safe codes sealed; role-gated reveal requires audit logging."),
            ("PROMPT_INJECTION_GUARD_ACTIVE", "Warning", "DataSecurityAndPrivacyGuard", "Prompt-injection sanitizer active for resident, OCR and bank inputs.")
        })
        {
            var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(community.Id, WorkspaceSecurityRole.PropertyManagerAdmin, category, severity, resource, summary, previous);
            previous = ev.EntrySha256Hash;
            community.SecurityAuditEvents.Add(ev);
        }

        return community;
    }

    private Community BuildOakridge()
    {
        var community = new Community
        {
            Name = "Oakridge Palms Condominiums",
            OperatingMode = SystemOperatingMode.UsHoa,
            StreetAddress = "1400 Brickell Bay Dr",
            City = "Miami, FL",
            PostalOrZipCode = "33131",
            CountryCode = "US",
            CurrencyCode = "USD",
            TaxIdOrEin = "65-0488123",
            RegistryNumber = "FL-CONDO-88412",
            LocalMunicipalityOrCounty = "Miami-Dade County",
            LocalTaxOffice = "IRS Form 1120-H",
            CompetentCourtOrRecorder = "Miami-Dade County Recorder",
            ConstructionHandoverDate = DateTime.UtcNow.AddYears(-6),
            OperatingRatePerM2 = 4.10m,
            RepairFundRatePerM2 = 2.30m,
            WasteFeePerOccupant = 32m,
            ColdWaterTariffPerM3 = 4.20m,
            HotWaterHeatingCostPerM3 = 18m,
            DistrictHeatingTariffPerGj = 18m,
            CommonElectricityTariffPerKwh = 0.14m,
            StatuteFullText = "Recorded Declaration of CC&Rs - Oakridge Palms. Art. VIII: Regular assessments may not increase more than 20 percent per year without a membership vote. Art. VI: Interior non-structural alterations require ARC notice.",
            HouseRulesFullText = "Bylaws and ARC Charter. Par. 3.1: Exterior attachments require ARC approval within 30 days; silence after 30 days is deemed approval. Par. 5: Replacement flooring must meet IIC 72 acoustic rating.",
            UtilityPolicyFullText = "RUBS Resolution 2024-04. Section 2: Master water and trash bills are allocated 50 percent by unit square footage and 50 percent by declared occupant count.",
            LastWebEnrichedAt = DateTime.UtcNow.AddDays(-1)
        };

        var building = new Building { CommunityId = community.Id, Name = "Oakridge Tower", StreetAddress = community.StreetAddress, FloorsCount = 6, StairwellsCount = 2, ConstructionYear = 2019, InsurancePolicyNumber = "POL-US-2026-4417", InsuranceProviderName = "Florida Property Insurance Co.", InsuranceDeductible = 2500m, RiserStacks = [new VerticalRiserStack { Code = "STACK-A1", Description = "West tower water riser", ShutoffValveLocation = "Basement mechanical room, west manifold" }] };
        community.Buildings.Add(building);

        for (var i = 0; i < 6; i++)
        {
            var unit = new Unit
            {
                CommunityId = community.Id, BuildingId = building.Id, UnitNumber = $"U-{101 + i}", FloorNumber = 1 + i / 2, VerticalStackCode = "STACK-A1",
                FloorAreaM2 = 78m + i * 4m, ShareNumerator = i < 4 ? 1500 : 2000, ShareDenominator = 10000, DeclaredOccupants = 2,
                PrimaryOwnerName = $"Oakridge Owner {i + 1}", PrimaryOwnerEmail = $"owner{i + 1}@oakridge.example.com", PrimaryOwnerPhone = $"+1 305 555 02{i:00}",
                VirtualIbanOrLockboxCode = $"LOCKBOX-U-{101 + i}", CurrentBalance = i == 1 ? 1450m : 0m, DaysPastDue = i == 1 ? 65 : 0,
                ArrearsStage = i == 1 ? ArrearsStage.PreLitigationOrIntentToLien45Days : ArrearsStage.Current
            };
            unit.Meters.Add(new UtilityMeter { UnitId = unit.Id, Kind = UtilityKind.ColdWater, SerialNumber = $"CW-U-{101 + i}", RollingMonthlyAverage = 6.1m, LastReadingValue = 210.4m });
            community.Units.Add(unit);
        }

        community.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "100-OP", Name = "Operating Cash", Fund = FundKind.Operating, Kind = LedgerAccountKind.Asset, Balance = 28400m });
        community.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "110-REP", Name = "Reserve Fund Cash (SIRS)", Fund = FundKind.RepairReserve, Kind = LedgerAccountKind.Asset, Balance = 86400m });
        community.LedgerAccounts.Add(new LedgerAccount { CommunityId = community.Id, Code = "400-ASSESS", Name = "Assessment Revenue", Fund = FundKind.Operating, Kind = LedgerAccountKind.Revenue, Balance = -61200m });

        community.UtilityProviderSources.Add(new UtilityProviderSource { CommunityId = community.Id, UtilityKind = UtilityKind.Electricity, ProviderName = "Florida Power and Light", OutageBulletinUrl = "https://example.com/fpl/outages", LiveFaultMapUrl = "https://example.com/fpl/map", TariffScheduleUrl = "https://example.com/fpl/tariffs", EmergencyPhone = "1-800-468-8243", CurrentUnitTariff = 0.14m, TariffUnit = "USD/kWh" });
        community.UtilityProviderSources.Add(new UtilityProviderSource { CommunityId = community.Id, UtilityKind = UtilityKind.ColdWater, ProviderName = "Miami-Dade Water and Sewer", OutageBulletinUrl = "https://example.com/miamidade/water", LiveFaultMapUrl = "https://example.com/miamidade/map", TariffScheduleUrl = "https://example.com/miamidade/tariffs", EmergencyPhone = "305-665-7477", CurrentUnitTariff = 4.20m, TariffUnit = "USD/CCF" });

        community.EmergencyContacts.Add(new EmergencyAndAuthorityContact { CommunityId = community.Id, Category = "EmergencyService", Name = "Emergency", PhoneNumber = "911", SortOrder = 1 });
        community.EmergencyContacts.Add(new EmergencyAndAuthorityContact { CommunityId = community.Id, Category = "LocalAuthority", Name = "City services and code enforcement", PhoneNumber = "311", SortOrder = 2 });
        community.EmergencyContacts.Add(new EmergencyAndAuthorityContact { CommunityId = community.Id, Category = "UtilityEmergency", Name = "Call before you dig", PhoneNumber = "811", SortOrder = 3 });
        community.EmergencyContacts.Add(new EmergencyAndAuthorityContact { CommunityId = community.Id, Category = "LocalAuthority", Name = "Miami-Dade Fire Marshal", PhoneNumber = "786-331-5000", SortOrder = 4 });

        community.ReserveComponents.Add(new ReserveStudyComponent { CommunityId = community.Id, ComponentName = "Roof Replacement (SIRS)", InstallationYear = 2019, UsefulLifeYears = 20, RemainingLifeYears = 13, EstimatedReplacementCost = 240000m, ConditionScore = 4 });
        community.ReserveComponents.Add(new ReserveStudyComponent { CommunityId = community.Id, ComponentName = "Concrete Restoration (SIRS)", InstallationYear = 2019, UsefulLifeYears = 25, RemainingLifeYears = 18, EstimatedReplacementCost = 380000m, ConditionScore = 3 });
        community.GreenSubsidies.Add(new GreenSubsidyProgram { CommunityId = community.Id, ProgramName = "Federal IRA Energy Credit", AuthorityName = "US Department of Energy", UpgradeScope = "Rooftop solar and heat pump water heaters", EstimatedGrossCapex = 150000m, GrantSubsidyPercent = 30m, GrantSubsidyAmount = 45000m, NetCommunityCapex = 105000m, EstimatedAnnualSavings = 15000m, PaybackYears = 7.0m });
        community.GreenSubsidies.Add(new GreenSubsidyProgram { CommunityId = community.Id, ProgramName = "FPL Energy Rebate", AuthorityName = "Florida Power and Light", UpgradeScope = "Common-area LED and EV charging", EstimatedGrossCapex = 38000m, GrantSubsidyPercent = 25m, GrantSubsidyAmount = 9500m, NetCommunityCapex = 28500m, EstimatedAnnualSavings = 6100m, PaybackYears = 4.7m });

        var arcUnit = community.Units[2];
        community.ModificationRequests.Add(new ModificationOrArcRequest { CommunityId = community.Id, UnitId = arcUnit.Id, UnitNumber = arcUnit.UnitNumber, OwnerName = arcUnit.PrimaryOwnerName, Kind = ModificationRequestKind.SolarPanels, Description = "Install rooftop solar collectors above the assigned parking structure.", StatutoryProtectionNote = "Fla. Stat. 163.04 prohibits denial of solar collectors.", CitedLegalDocumentCode = "FL-STAT-553-899", CitedVerbatimLegalExcerpt = "Fla. Stat. 163.04: A homeowners association may not prohibit a solar collector.", AiRecommendation = "ApproveWithConditions", TechnicalConditionsOrDecisionText = "Approve with conditions: licensed installer, insurance certificate, ARC color matching.", Status = "PendingBoardReview" });
        community.ModificationRequests.Add(new ModificationOrArcRequest { CommunityId = community.Id, UnitId = community.Units[3].Id, UnitNumber = community.Units[3].UnitNumber, OwnerName = community.Units[3].PrimaryOwnerName, Kind = ModificationRequestKind.EvChargerInstallation, Description = "Install Level-2 EV charger in assigned parking space.", StatutoryProtectionNote = "Fla. Stat. 718.113(8) protects unit owner EV charging stations.", CitedLegalDocumentCode = "FL-STAT-553-899", CitedVerbatimLegalExcerpt = "Fla. Stat. 718.113(8): An association may not prohibit a Level-2 EV charging station in an assigned space.", AiRecommendation = "ApproveWithConditions", TechnicalConditionsOrDecisionText = "Approve with conditions: dedicated meter, licensed electrician, owner bears installation and removal cost.", Status = "PendingBoardReview" });

        community.Resolutions.Add(new Resolution { CommunityId = community.Id, Number = "Res 01/2026", Title = "Adoption of Annual Budget and Reserve Funding", Content = "Adopt the annual budget with SIRS reserve funding as required by Fla. Stat. 718.112(2)(f).", MajorityRule = VotingMajorityRule.ShareWeightedMajority, ComplianceStatus = "VerifiedClean" });
        community.LegalDocuments.AddRange(LegalCorpusFactory.Build(community));
        community.Meetings.Add(new MeetingRecord { CommunityId = community.Id, Title = "Annual Members Meeting 2026", ScheduledAt = DateTime.UtcNow.AddDays(21), ConferenceUrl = "https://meet.example.com/oakridge-annual", DiarizedTranscriptText = "[Speaker 1 - President] We confirm quorum. [Speaker 2 - Manager] The SIRS reserve study is presented.", GeneratedMinutesMarkdown = "Quorum confirmed. Budget and SIRS reserves presented.", ExtractedActionItemsSummary = "Issue estoppel procedures update; tender roof reserve works." });

        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Oakridge Manager", Email = "manager@oakridge.example.com", Provider = IdentityProvider.Google, ProviderSubjectId = "google-oauth2-99120", Role = WorkspaceSecurityRole.PropertyManagerAdmin });
        community.UserAccounts.Add(new WorkspaceUserAccount { CommunityId = community.Id, FullName = "Oakridge Board President", Email = "president@oakridge.example.com", Provider = IdentityProvider.Facebook, ProviderSubjectId = "fb-3099112", Role = WorkspaceSecurityRole.BoardMemberReadOnly });
        community.AiActions.Add(new AiAction { CommunityId = community.Id, Kind = AiActionKind.IssueModificationOrArcDecision, AgentName = "WarrantyAndRenovationPermitAgent", Title = "Issue ARC decision for rooftop solar", Rationale = "State solar rights statute prohibits denial; conditions drafted.", ModelUsed = "muse-spark-1.3", Confidence = 0.95, TargetEntitySummary = $"ARC request, Unit {arcUnit.UnitNumber}", ProposedPayloadJson = "{\"decision\":\"ApproveWithConditions\"}" });

        var previous = "GENESIS-0000000000000000";
        var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(community.Id, WorkspaceSecurityRole.PropertyManagerAdmin, "STATUTE_CORPUS_ACQUIRED", "Info", "LegalDocumentSource corpus", "7 Florida and community governing documents acquired verbatim.", previous);
        community.SecurityAuditEvents.Add(ev);

        return community;
    }
}
