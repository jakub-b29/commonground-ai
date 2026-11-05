using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public sealed partial class CommonGroundWorkspaceService
{
    private static void EnsureBuilderManagerRole(WorkspaceSecurityRole actorRole)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            throw new UnauthorizedAccessException("Only the Property Manager or Firm Manager may edit the building structure.");
        }
    }

    private static string NormalizeStackCode(string code)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        normalized = new string(normalized.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (normalized.Length > 32)
        {
            normalized = normalized[..32];
        }

        return normalized;
    }

    public async Task<Building> EnsureDefaultBuildingStructureAsync(Guid communityId, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        EnsureBuilderManagerRole(actorRole);
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var existing = community.Buildings.OrderBy(b => b.Name).FirstOrDefault();
        if (existing is not null)
        {
            if (existing.RiserStacks.Count == 0)
            {
                foreach (var code in new[] { "STACK-A1", "STACK-A2", "STACK-B1" })
                {
                    var stack = new VerticalRiserStack
                    {
                        BuildingId = existing.Id,
                        Code = code,
                        Description = code.StartsWith("STACK-B", StringComparison.OrdinalIgnoreCase) ? "Heating riser" : "Cold water riser",
                        ShutoffValveLocation = "Basement manifold"
                    };
                    existing.RiserStacks.Add(stack);
                    db.RiserStacks.Add(stack);
                }

                await db.SaveChangesAsync(ct);
            }

            return existing;
        }

        var building = new Building
        {
            CommunityId = community.Id,
            Name = "Building A",
            StreetAddress = community.StreetAddress,
            FloorsCount = 4,
            StairwellsCount = 2
        };
        community.Buildings.Add(building);
        db.Buildings.Add(building);
        foreach (var code in new[] { "STACK-A1", "STACK-A2", "STACK-B1" })
        {
            var stack = new VerticalRiserStack
            {
                BuildingId = building.Id,
                Code = code,
                Description = code.StartsWith("STACK-B", StringComparison.OrdinalIgnoreCase) ? "Heating riser" : "Cold water riser",
                ShutoffValveLocation = "Basement manifold"
            };
            building.RiserStacks.Add(stack);
            db.RiserStacks.Add(stack);
        }

        await AppendAuditAsync(db, community, actorRole, "BUILDING_STRUCTURE_CREATED", "Info", $"Building: {building.Name}", "Default building with STACK-A1, STACK-A2, STACK-B1 created via interactive builder.", ct);
        await db.SaveChangesAsync(ct);
        return building;
    }

    public async Task<Building> UpdateBuildingStructureAsync(Guid communityId, Guid buildingId, int floorsCount, int stairwellsCount, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        EnsureBuilderManagerRole(actorRole);
        if (floorsCount < 1 || floorsCount > 20)
        {
            throw new InvalidOperationException("Floors count must be between 1 and 20.");
        }

        if (stairwellsCount < 1 || stairwellsCount > 10)
        {
            throw new InvalidOperationException("Stairwells count must be between 1 and 10.");
        }

        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var building = community.Buildings.FirstOrDefault(b => b.Id == buildingId) ?? throw new InvalidOperationException("Building not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(building, communityId);
        building.FloorsCount = floorsCount;
        building.StairwellsCount = stairwellsCount;
        await AppendAuditAsync(db, community, actorRole, "BUILDING_STRUCTURE_UPDATED", "Info", $"Building: {building.Name}", $"Structure updated to {floorsCount} floors, {stairwellsCount} stairwells.", ct);
        await db.SaveChangesAsync(ct);
        return building;
    }

    public async Task<VerticalRiserStack> UpsertRiserStackAsync(Guid communityId, Guid buildingId, string stackCode, string shutoffLocation, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        EnsureBuilderManagerRole(actorRole);
        var code = NormalizeStackCode(stackCode);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Stack code is required.");
        }

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{code} {shutoffLocation}");
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Stack update blocked by security guard.");
        }

        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var building = community.Buildings.FirstOrDefault(b => b.Id == buildingId) ?? throw new InvalidOperationException("Building not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(building, communityId);
        var existing = building.RiserStacks.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(shutoffLocation))
            {
                existing.ShutoffValveLocation = shutoffLocation.Trim();
            }

            await db.SaveChangesAsync(ct);
            return existing;
        }

        var stack = new VerticalRiserStack
        {
            BuildingId = building.Id,
            Code = code,
            Description = code.Contains('B') ? "Heating riser" : "Water riser",
            ShutoffValveLocation = string.IsNullOrWhiteSpace(shutoffLocation) ? "Basement manifold" : shutoffLocation.Trim()
        };
        building.RiserStacks.Add(stack);
        db.RiserStacks.Add(stack);
        await AppendAuditAsync(db, community, actorRole, "RISER_STACK_ADDED", "Info", $"Riser stack {code}", $"Stack {code} added to {building.Name}.", ct);
        await db.SaveChangesAsync(ct);
        return stack;
    }

    public async Task RemoveRiserStackAsync(Guid communityId, Guid buildingId, string stackCode, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        EnsureBuilderManagerRole(actorRole);
        var code = NormalizeStackCode(stackCode);
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var building = community.Buildings.FirstOrDefault(b => b.Id == buildingId) ?? throw new InvalidOperationException("Building not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(building, communityId);
        var stack = building.RiserStacks.FirstOrDefault(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Stack not found.");
        var inUse = community.Units.Any(u => u.BuildingId == building.Id && string.Equals(u.VerticalStackCode, stack.Code, StringComparison.OrdinalIgnoreCase));
        if (inUse)
        {
            throw new InvalidOperationException($"Stack {stack.Code} is in use by existing units. Remove or reassign units first.");
        }

        building.RiserStacks.Remove(stack);
        db.RiserStacks.Remove(stack);
        await AppendAuditAsync(db, community, actorRole, "RISER_STACK_REMOVED", "Info", $"Riser stack {code}", $"Stack {code} removed from {building.Name}.", ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Unit> UpsertUnitCellAsync(Guid communityId, Guid buildingId, int floorNumber, string stackCode, string unitNumber, decimal floorAreaM2, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        EnsureBuilderManagerRole(actorRole);
        var code = NormalizeStackCode(stackCode);
        var number = (unitNumber ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("Stack code is required.");
        }

        if (string.IsNullOrWhiteSpace(number))
        {
            throw new InvalidOperationException("Unit number is required.");
        }

        if (number.Length > 32)
        {
            throw new InvalidOperationException("Unit number must be 32 characters or fewer.");
        }

        if (floorNumber < 0 || floorNumber > 20)
        {
            throw new InvalidOperationException("Floor must be between 0 (ground) and 20.");
        }

        if (floorAreaM2 < 2m || floorAreaM2 > 2000m)
        {
            throw new InvalidOperationException("Floor area must be between 2 and 2000 m2.");
        }

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{number} {code}");
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Unit update blocked by security guard.");
        }

        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var building = community.Buildings.FirstOrDefault(b => b.Id == buildingId)
            ?? community.Buildings.OrderBy(b => b.Name).FirstOrDefault();
        if (building is null)
        {
            building = new Building
            {
                CommunityId = community.Id,
                Name = "Building A",
                StreetAddress = community.StreetAddress,
                FloorsCount = Math.Max(4, floorNumber),
                StairwellsCount = 2
            };
            community.Buildings.Add(building);
            db.Buildings.Add(building);
        }
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(building, communityId);
        if (!building.RiserStacks.Any(r => string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase)))
        {
            var autoStack = new VerticalRiserStack
            {
                BuildingId = building.Id,
                Code = code,
                Description = code.Contains('B') ? "Heating riser" : "Water riser",
                ShutoffValveLocation = "Basement manifold"
            };
            building.RiserStacks.Add(autoStack);
            db.RiserStacks.Add(autoStack);
        }

        var cellUnit = community.Units.FirstOrDefault(u => u.BuildingId == building.Id && u.FloorNumber == floorNumber && string.Equals(u.VerticalStackCode, code, StringComparison.OrdinalIgnoreCase));
        var duplicateNumber = community.Units.FirstOrDefault(u => string.Equals(u.UnitNumber, number, StringComparison.OrdinalIgnoreCase) && (cellUnit is null || u.Id != cellUnit.Id));
        if (duplicateNumber is not null)
        {
            throw new InvalidOperationException($"Unit number {number} already exists in this property.");
        }

        if (cellUnit is not null)
        {
            cellUnit.UnitNumber = number;
            cellUnit.FloorAreaM2 = floorAreaM2;
            await AppendAuditAsync(db, community, actorRole, "UNIT_CELL_UPDATED", "Info", $"Unit {number}", $"Unit at floor {floorNumber}, stack {code} updated.", ct);
            await db.SaveChangesAsync(ct);
            return cellUnit;
        }

        var stairwell = code.Contains('B') ? 2 : 1;
        if (stairwell > building.StairwellsCount)
        {
            stairwell = 1;
        }

        var unit = new Unit
        {
            CommunityId = community.Id,
            BuildingId = building.Id,
            UnitNumber = number,
            StairwellNumber = stairwell,
            FloorNumber = floorNumber,
            VerticalStackCode = code,
            Kind = floorNumber == 0 ? UnitKind.CommercialPremise : UnitKind.Apartment,
            FloorAreaM2 = floorAreaM2,
            StorageAreaM2 = 4.5m,
            ShareNumerator = 625,
            ShareDenominator = 10000,
            DeclaredOccupants = 2,
            PrimaryOwnerName = "To be assigned",
            PreferredLanguage = "EN",
            VirtualIbanOrLockboxCode = $"LOCK-{number.ToUpperInvariant()}"
        };
        community.Units.Add(unit);
        db.Units.Add(unit);
        await AppendAuditAsync(db, community, actorRole, "UNIT_CELL_CREATED", "Info", $"Unit {number}", $"Unit {number} created at floor {floorNumber}, stack {code}.", ct);
        await db.SaveChangesAsync(ct);
        return unit;
    }

    public async Task RemoveUnitCellAsync(Guid communityId, Guid unitId, WorkspaceSecurityRole actorRole, CancellationToken ct = default)
    {
        EnsureBuilderManagerRole(actorRole);
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var unit = community.Units.FirstOrDefault(u => u.Id == unitId) ?? throw new InvalidOperationException("Unit not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(unit, communityId);
        var label = unit.UnitNumber;
        community.Units.Remove(unit);
        db.Units.Remove(unit);
        await AppendAuditAsync(db, community, actorRole, "UNIT_CELL_REMOVED", "Info", $"Unit {label}", $"Unit {label} removed from floor {unit.FloorNumber}, stack {unit.VerticalStackCode}.", ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteBuildingAsync(Guid communityId, Guid buildingId, WorkspaceSecurityRole actorRole, bool force = false, CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            throw new UnauthorizedAccessException("Only the Property Manager or Firm Manager may delete buildings.");
        }

        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Property not found.");
        var building = community.Buildings.FirstOrDefault(b => b.Id == buildingId) ?? throw new InvalidOperationException("Building not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(building, communityId);

        var unitCount = community.Units.Count(u => u.BuildingId == building.Id);
        if (unitCount > 0 && !force)
        {
            throw new InvalidOperationException($"Building has {unitCount} units, confirm force delete. Delete units first before deleting building '{building.Name}'.");
        }

        if (unitCount > 0 && force)
        {
            throw new InvalidOperationException($"Building has {unitCount} units, delete units first before deleting building '{building.Name}'. Remove or reassign units first.");
        }

        var buildingName = building.Name;

        foreach (var stack in building.RiserStacks.ToList())
        {
            db.RiserStacks.Remove(stack);
        }

        building.RiserStacks.Clear();

        foreach (var photo in community.InfrastructurePhotos.Where(p => p.BuildingId == building.Id).ToList())
        {
            db.InfrastructurePhotos.Remove(photo);
        }

        foreach (var code in community.AccessCodes.Where(c => c.BuildingId == building.Id).ToList())
        {
            db.AccessCodes.Remove(code);
        }

        foreach (var inspection in community.Inspections.Where(i => i.BuildingId == building.Id).ToList())
        {
            db.Inspections.Remove(inspection);
        }

        community.Buildings.Remove(building);
        db.Buildings.Remove(building);

        await AppendAuditAsync(db, community, actorRole, "BUILDING_DELETED", "Info", $"Building: {buildingName}", $"Building '{buildingName}' deleted.", ct);
        await db.SaveChangesAsync(ct);
    }
}
