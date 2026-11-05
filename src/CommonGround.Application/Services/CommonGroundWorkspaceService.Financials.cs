using CommonGround.Application.Persistence;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

/// <summary>
/// Editable per-unit czynsz (rent / maintenance fee) line kinds.
/// Credit is tracked via <see cref="CzynszLineItem.IsCredit"/> so totals stay as sum(charges) minus sum(credits).
/// </summary>
public enum CzynszLineKind
{
    PerM2 = 0,
    PerPerson = 1,
    Fixed = 2,
    Advance = 3
}

/// <summary>
/// Single editable czynsz generator line. Amount is always recomputed server-side as Round(Quantity * UnitPrice, 2).
/// </summary>
public sealed record CzynszLineItem(
    string Key,
    string Label,
    CzynszLineKind Kind,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    bool IsCredit);

public sealed partial class CommonGroundWorkspaceService
{
    private static readonly string[] StandardCzynszKeys =
        ["operating", "repair", "waste", "coldWater", "cold", "hotWater", "hot", "heating", "commercialCredit"];

    private static bool IsStandardCzynszKey(string key)
    {
        foreach (var k in StandardCzynszKeys)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyList<CzynszLineItem> BuildDefaultCzynszLines(
        decimal floorAreaM2,
        int occupantsCount,
        decimal operatingRatePerM2,
        decimal repairFundRatePerM2,
        decimal wasteFeePerOccupant,
        decimal coldWaterTariffPerM3,
        decimal hotWaterTariffPerM3,
        decimal heatingTariffPerGj,
        decimal coldAvgM3 = 4.2m,
        decimal hotAvgM3 = 2.8m,
        decimal heatAvgGj = 0.55m)
    {
        var area = Math.Max(0m, floorAreaM2);
        var occupants = Math.Clamp(occupantsCount, 0, 50);

        CzynszLineItem Line(string key, string label, CzynszLineKind kind, decimal qty, decimal price, bool credit = false) =>
            new(key, label, kind, Math.Round(qty, 3), Math.Round(price, 4), Math.Round(qty * price, 2), credit);

        return
        [
            Line("operating", "Operating fee (eksploatacja)", CzynszLineKind.PerM2, area, operatingRatePerM2),
            Line("repair", "Repair fund (fundusz remontowy)", CzynszLineKind.PerM2, area, repairFundRatePerM2),
            Line("waste", "Waste (smieci, per person)", CzynszLineKind.PerPerson, occupants, wasteFeePerOccupant),
            Line("coldWater", "Cold water advance", CzynszLineKind.Advance, coldAvgM3, coldWaterTariffPerM3),
            Line("hotWater", "Hot water advance", CzynszLineKind.Advance, hotAvgM3, hotWaterTariffPerM3),
            Line("heating", "Heating advance (CO)", CzynszLineKind.Advance, heatAvgGj, heatingTariffPerGj),
            Line("parking", "Parking", CzynszLineKind.Fixed, 1m, 0m),
            Line("elevator", "Elevator (winda)", CzynszLineKind.Fixed, 1m, 0m),
            Line("security", "Security (ochrona)", CzynszLineKind.Fixed, 1m, 0m),
            Line("commercialCredit", "Commercial revenue credit", CzynszLineKind.Fixed, 1m, 0m, true),
        ];
    }

    public static IReadOnlyList<CzynszLineItem> RecomputeCzynszLines(IEnumerable<CzynszLineItem> lines)
    {
        var result = new List<CzynszLineItem>();
        foreach (var l in lines)
        {
            var qty = Math.Max(0m, Math.Round(l.Quantity, 3));
            var price = Math.Max(0m, Math.Round(l.UnitPrice, 4));
            result.Add(l with { Quantity = qty, UnitPrice = price, Amount = Math.Round(qty * price, 2) });
        }

        return result;
    }

    public static decimal ComputeCzynszTotal(IEnumerable<CzynszLineItem> lines)
    {
        var recomputed = RecomputeCzynszLines(lines);
        var charges = recomputed.Where(l => !l.IsCredit).Sum(l => l.Amount);
        var credits = recomputed.Where(l => l.IsCredit).Sum(l => l.Amount);
        return Math.Round(charges - credits, 2);
    }

    private static bool IsValidPeriodLabel(string period)
    {
        if (string.IsNullOrWhiteSpace(period) || period.Length != 7 || period[4] != '-')
        {
            return false;
        }

        return int.TryParse(period.AsSpan(0, 4), out var year)
            && int.TryParse(period.AsSpan(5, 2), out var month)
            && year is >= 2000 and <= 2100
            && month is >= 1 and <= 12;
    }

    public async Task<MonthlyAssessment> SaveCzynszAssessmentAsync(
        Guid communityId,
        Guid unitId,
        string period,
        IReadOnlyList<CzynszLineItem> lines,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may save czynsz assessments.");
        }

        var normalizedPeriod = (period ?? string.Empty).Trim();
        if (!IsValidPeriodLabel(normalizedPeriod))
        {
            throw new InvalidOperationException("Period must use yyyy-MM format (for example 2026-11).");
        }

        if (lines is null || lines.Count == 0)
        {
            throw new InvalidOperationException("At least one czynsz line is required.");
        }

        if (lines.Count > 50)
        {
            throw new InvalidOperationException("Too many czynsz lines (maximum 50).");
        }

        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Key) || l.Key.Length > 64)
            {
                throw new InvalidOperationException("Each czynsz line requires a key (max 64 characters).");
            }

            if (string.IsNullOrWhiteSpace(l.Label) || l.Label.Length > 120)
            {
                throw new InvalidOperationException("Each czynsz line requires a label (max 120 characters).");
            }

            if (l.Quantity < 0m || l.Quantity > 100000m || l.UnitPrice < 0m || l.UnitPrice > 100000m)
            {
                throw new InvalidOperationException($"Line '{l.Key}' has out-of-range quantity or unit price.");
            }
        }

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(
            string.Join(" | ", lines.Select(l => $"{l.Key} {l.Label}")));
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Czynsz save blocked by prompt-injection guard.");
        }

        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct)
            ?? throw new InvalidOperationException("Community not found.");
        var unit = community.Units.FirstOrDefault(u => u.Id == unitId)
            ?? throw new InvalidOperationException("Unit not found in this community.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(unit, communityId);

        var recomputed = RecomputeCzynszLines(lines);
        var total = ComputeCzynszTotal(recomputed);

        decimal SumKeys(params string[] keys) =>
            Math.Round(recomputed
                .Where(l => !l.IsCredit && keys.Any(k => string.Equals(k, l.Key, StringComparison.OrdinalIgnoreCase)))
                .Sum(l => l.Amount), 2);

        var operating = SumKeys("operating");
        var repair = SumKeys("repair");
        var waste = SumKeys("waste");
        var cold = SumKeys("coldWater", "cold");
        var hot = SumKeys("hotWater", "hot");
        var heating = SumKeys("heating");
        var credit = Math.Round(recomputed.Where(l => l.IsCredit).Sum(l => l.Amount), 2);
        var customPositive = Math.Round(recomputed
            .Where(l => !l.IsCredit && !IsStandardCzynszKey(l.Key))
            .Sum(l => l.Amount), 2);

        // Custom lines (parking, elevator, security and user-defined rows) have no dedicated
        // MonthlyAssessment column, so they fold into OperatingFee to preserve Total = sum - credits.
        var operatingFee = Math.Round(operating + customPositive, 2);

        var existing = await db.MonthlyAssessments
            .FirstOrDefaultAsync(a => a.CommunityId == communityId && a.UnitId == unitId && a.Period == normalizedPeriod, ct);

        MonthlyAssessment assessment;
        if (existing is null)
        {
            assessment = new MonthlyAssessment
            {
                CommunityId = communityId,
                UnitId = unitId,
                UnitNumber = unit.UnitNumber,
                Period = normalizedPeriod,
                OperatingFee = operatingFee,
                RepairFundFee = repair,
                WasteFee = waste,
                ColdWaterAdvance = cold,
                HotWaterAdvance = hot,
                HeatingAdvance = heating,
                CommercialRevenueCredit = credit,
                TotalAssessment = total
            };
            db.MonthlyAssessments.Add(assessment);
        }
        else
        {
            existing.UnitNumber = unit.UnitNumber;
            existing.OperatingFee = operatingFee;
            existing.RepairFundFee = repair;
            existing.WasteFee = waste;
            existing.ColdWaterAdvance = cold;
            existing.HotWaterAdvance = hot;
            existing.HeatingAdvance = heating;
            existing.CommercialRevenueCredit = credit;
            existing.TotalAssessment = total;
            assessment = existing;
        }

        await AppendChainedAuditByCommunityIdAsync(
            db,
            communityId,
            actorRole,
            "CZYNSZ_ASSESSMENT_SAVED",
            "Info",
            $"Unit: {unit.UnitNumber} ({normalizedPeriod})",
            $"Czynsz assessment saved for unit {unit.UnitNumber} period {normalizedPeriod}: total {total:N2} {community.CurrencyCode} from {recomputed.Count} lines.",
            ct);

        await db.SaveChangesAsync(ct);
        return assessment;
    }
}
