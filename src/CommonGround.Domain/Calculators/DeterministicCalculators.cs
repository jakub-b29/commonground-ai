using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Domain.Calculators;

public sealed record UnitInflationDelta(
    Guid UnitId,
    string UnitNumber,
    decimal BeforeTotal,
    decimal AfterTotal,
    decimal DeltaAmount,
    decimal DeltaPercent);

public sealed record InflationSimulationResult(
    IReadOnlyList<UnitInflationDelta> UnitDeltas,
    decimal AverageIncreasePct,
    bool ExceedsUsHoaStatutory20PercentCap);

public sealed record CommonUtilityAnomalyResult(
    bool IsNightTimeBaseLoadAnomaly,
    decimal NightDrawRatio,
    string Summary);

public sealed record ContractedPowerAuditResult(
    bool HeatingOverProvisioned,
    decimal RecommendedHeatingMw,
    decimal HeatingAnnualSavings,
    bool ElectricalOverProvisioned,
    decimal RecommendedElectricalKw,
    decimal ElectricalAnnualSavings,
    string Summary);

public sealed record ReserveYearProjection(
    int Year,
    decimal StartingBalance,
    decimal AnnualInflow,
    decimal AnnualCapexOutflow,
    decimal EndingBalance);

public sealed record ReserveStudyResult(
    IReadOnlyList<ReserveYearProjection> Years,
    decimal PercentFunded,
    decimal MinimumMonthlyRatePerM2ToAvoidDeficit);

public sealed record GreenGrantRoiResult(
    decimal GrantSubsidyAmount,
    decimal NetCommunityCapex,
    decimal PaybackYears);

public sealed record BoqLineItem(string Description, decimal Quantity, string Unit, decimal UnitRate, decimal Total);

public sealed record BoqEstimateResult(
    decimal TotalMaterialCost,
    decimal TotalLaborCost,
    decimal TotalGrossEstimate,
    IReadOnlyList<BoqLineItem> LineItems);

public sealed record VotingTallyResult(
    decimal SharesFor,
    decimal SharesAgainst,
    decimal SharesAbstain,
    int OwnersFor,
    int OwnersAgainst,
    int OwnersAbstain,
    bool OneOwnerOneVoteSwitchTriggered,
    VotingMajorityRule EffectiveRule,
    bool IsAdopted);

public sealed record PromptSecurityScanResult(
    bool IsInjectionDetected,
    string MatchedPattern,
    string SafeDelimitedPayload);

public static class AssessmentCalculator
{
    public static MonthlyAssessment Compute(Community community, Unit unit, string period, decimal ancillaryCreditForUnit)
    {
        var operating = Math.Round(unit.FloorAreaM2 * community.OperatingRatePerM2, 2);
        var repair = Math.Round(unit.FloorAreaM2 * community.RepairFundRatePerM2, 2);
        var wasteBase = unit.DeclaredOccupants * community.WasteFeePerOccupant;
        var waste = Math.Round(unit.IsShortTermRental ? wasteBase * 1.5m : wasteBase, 2);

        decimal ColdAvg(Unit u) => u.Meters.Where(m => m.Kind == UtilityKind.ColdWater).Select(m => m.RollingMonthlyAverage).DefaultIfEmpty(4.2m).First();
        decimal HotAvg(Unit u) => u.Meters.Where(m => m.Kind == UtilityKind.HotWater).Select(m => m.RollingMonthlyAverage).DefaultIfEmpty(2.8m).First();
        decimal HeatAvg(Unit u) => u.Meters.Where(m => m.Kind == UtilityKind.DistrictHeating).Select(m => m.RollingMonthlyAverage).DefaultIfEmpty(0.55m).First();

        var cold = Math.Round(ColdAvg(unit) * community.ColdWaterTariffPerM3, 2);
        var hot = Math.Round(HotAvg(unit) * community.HotWaterHeatingCostPerM3, 2);
        var heating = Math.Round(HeatAvg(unit) * community.DistrictHeatingTariffPerGj, 2);
        var credit = Math.Round(ancillaryCreditForUnit, 2);
        var total = Math.Round(operating + repair + waste + cold + hot + heating - credit, 2);

        return new MonthlyAssessment
        {
            CommunityId = community.Id,
            UnitId = unit.Id,
            UnitNumber = unit.UnitNumber,
            Period = period,
            OperatingFee = operating,
            RepairFundFee = repair,
            WasteFee = waste,
            ColdWaterAdvance = cold,
            HotWaterAdvance = hot,
            HeatingAdvance = heating,
            CommercialRevenueCredit = credit,
            TotalAssessment = total
        };
    }
}

public static class TariffAndInflationSimulator
{
    public static InflationSimulationResult Simulate(
        Community community,
        IReadOnlyList<Unit> units,
        decimal heatingChangePct,
        decimal electricityChangePct,
        decimal operatingWageChangePct,
        decimal repairFundChangePct,
        Func<Community, Unit, decimal> ancillaryCredit)
    {
        var deltas = new List<UnitInflationDelta>();
        foreach (var unit in units)
        {
            var before = AssessmentCalculator.Compute(community, unit, "2026-11", ancillaryCredit(community, unit));

            var adjusted = new Community
            {
                Id = community.Id,
                OperatingRatePerM2 = community.OperatingRatePerM2 * (1m + operatingWageChangePct / 100m),
                RepairFundRatePerM2 = community.RepairFundRatePerM2 * (1m + repairFundChangePct / 100m),
                WasteFeePerOccupant = community.WasteFeePerOccupant,
                ColdWaterTariffPerM3 = community.ColdWaterTariffPerM3 * (1m + electricityChangePct / 100m),
                HotWaterHeatingCostPerM3 = community.HotWaterHeatingCostPerM3 * (1m + heatingChangePct / 100m),
                DistrictHeatingTariffPerGj = community.DistrictHeatingTariffPerGj * (1m + heatingChangePct / 100m),
                CommonElectricityTariffPerKwh = community.CommonElectricityTariffPerKwh * (1m + electricityChangePct / 100m)
            };
            var after = AssessmentCalculator.Compute(adjusted, unit, "2026-11", ancillaryCredit(community, unit));
            var delta = Math.Round(after.TotalAssessment - before.TotalAssessment, 2);
            var pct = before.TotalAssessment == 0m ? 0m : Math.Round(delta / before.TotalAssessment * 100m, 2);
            deltas.Add(new UnitInflationDelta(unit.Id, unit.UnitNumber, before.TotalAssessment, after.TotalAssessment, delta, pct));
        }

        var avg = deltas.Count == 0 ? 0m : Math.Round(deltas.Average(d => d.DeltaPercent), 2);
        return new InflationSimulationResult(deltas, avg, avg > 20.0m);
    }
}

public static class UtilitySettlementEngine
{
    public static UtilitySettlementPeriod Settle(
        Community community,
        string periodLabel,
        UtilityKind kind,
        UtilityAllocationPolicy policy,
        decimal masterConsumption,
        decimal masterCost,
        IReadOnlyList<(Unit Unit, decimal SubMeterConsumption, decimal AdvancesPaid)> inputs)
    {
        var period = new UtilitySettlementPeriod
        {
            CommunityId = community.Id,
            PeriodLabel = periodLabel,
            UtilityKind = kind,
            Policy = policy,
            MasterInvoiceConsumption = masterConsumption,
            MasterInvoiceTotalCost = masterCost,
            SubMeterTotalConsumption = inputs.Sum(i => i.SubMeterConsumption)
        };
        period.UnallocatedCommonLossConsumption = Math.Round(masterConsumption - period.SubMeterTotalConsumption, 4);
        if (period.UnallocatedCommonLossConsumption < 0m) period.UnallocatedCommonLossConsumption = 0m;
        var unitCost = masterConsumption > 0m ? masterCost / masterConsumption : 0m;
        period.UnallocatedCommonLossCost = Math.Round(period.UnallocatedCommonLossConsumption * unitCost, 2);

        var totalArea = inputs.Sum(i => i.Unit.FloorAreaM2);
        var totalOccupants = inputs.Sum(i => i.Unit.DeclaredOccupants);
        var totalShare = inputs.Sum(i => i.Unit.OwnershipShare);

        foreach (var (unit, consumption, advances) in inputs)
        {
            decimal direct;
            decimal allocatedLoss;
            if (policy is UtilityAllocationPolicy.UsRubsByFloorArea or UtilityAllocationPolicy.UsRubsByOccupantCount or UtilityAllocationPolicy.UsRubsHybrid50Area50Occupants)
            {
                decimal weight = policy switch
                {
                    UtilityAllocationPolicy.UsRubsByFloorArea => totalArea > 0m ? unit.FloorAreaM2 / totalArea : 0m,
                    UtilityAllocationPolicy.UsRubsByOccupantCount => totalOccupants > 0 ? (decimal)unit.DeclaredOccupants / totalOccupants : 0m,
                    _ => (totalArea > 0m ? 0.5m * unit.FloorAreaM2 / totalArea : 0m) + (totalOccupants > 0 ? 0.5m * (decimal)unit.DeclaredOccupants / totalOccupants : 0m)
                };
                var totalForUnit = Math.Round(masterCost * weight, 2);
                direct = totalForUnit;
                allocatedLoss = 0m;
                period.UnitResults.Add(new UtilitySettlementUnitResult
                {
                    SettlementPeriodId = period.Id,
                    UnitId = unit.Id,
                    UnitNumber = unit.UnitNumber,
                    SubMeterConsumption = consumption,
                    DirectMeterCost = direct,
                    AllocatedCommonLossCost = allocatedLoss,
                    TotalActualCost = totalForUnit,
                    AdvancesPaid = advances,
                    NetBalanceDelta = Math.Round(totalForUnit - advances, 2)
                });
            }
            else
            {
                direct = Math.Round(consumption * unitCost, 2);
                decimal lossWeight = policy == UtilityAllocationPolicy.SubMeterPlusAreaLoss
                    ? (totalArea > 0m ? unit.FloorAreaM2 / totalArea : 0m)
                    : (totalShare > 0m ? unit.OwnershipShare / totalShare : 0m);
                allocatedLoss = Math.Round(period.UnallocatedCommonLossCost * lossWeight, 2);
                var total = Math.Round(direct + allocatedLoss, 2);
                period.UnitResults.Add(new UtilitySettlementUnitResult
                {
                    SettlementPeriodId = period.Id,
                    UnitId = unit.Id,
                    UnitNumber = unit.UnitNumber,
                    SubMeterConsumption = consumption,
                    DirectMeterCost = direct,
                    AllocatedCommonLossCost = allocatedLoss,
                    TotalActualCost = total,
                    AdvancesPaid = advances,
                    NetBalanceDelta = Math.Round(total - advances, 2)
                });
            }
        }
        return period;
    }
}

public static class CommonUtilityAnomalyDetector
{
    public static CommonUtilityAnomalyResult Detect(decimal expectedNightBaselineKwh, decimal observedNightKwh)
    {
        var ratio = expectedNightBaselineKwh <= 0m ? 0m : Math.Round(observedNightKwh / expectedNightBaselineKwh, 2);
        var anomaly = ratio > 1.65m;
        return new CommonUtilityAnomalyResult(
            anomaly,
            ratio,
            anomaly
                ? $"Night-time base load is {ratio}x the expected baseline, indicating unauthorized basement power draw."
                : $"Night-time base load ratio {ratio}x is within the normal range.");
    }
}

public static class AncillaryRevenueEngine
{
    public static decimal TotalMonthlyRevenue(IReadOnlyList<AncillaryCommercialContract> contracts) =>
        Math.Round(contracts.Sum(c => c.MonthlyGrossRevenue), 2);

    public static decimal CreditForUnit(IReadOnlyList<AncillaryCommercialContract> contracts, Unit unit) =>
        Math.Round(TotalMonthlyRevenue(contracts.Where(c => c.OffsetRepairFundDirectly).ToList()) * unit.OwnershipShare, 2);
}

public static class ContractedPowerAndTariffAuditor
{
    public static ContractedPowerAuditResult Audit(Community community, decimal heatingFixedChargePerMwYear = 42000m, decimal electricalFixedChargePerKwYear = 96m)
    {
        var heatingOver = community.PeakMeasuredHeatingPowerMw > 0m && community.ContractedHeatingPowerMw > community.PeakMeasuredHeatingPowerMw * 1.20m;
        var recommendedHeating = Math.Round(community.PeakMeasuredHeatingPowerMw * 1.10m, 2);
        var heatingSavings = heatingOver ? Math.Round((community.ContractedHeatingPowerMw - recommendedHeating) * heatingFixedChargePerMwYear, 2) : 0m;

        var electricalOver = community.PeakMeasuredElectricalPowerKw > 0m && community.ContractedElectricalPowerKw > community.PeakMeasuredElectricalPowerKw * 1.20m;
        var recommendedElectrical = Math.Round(community.PeakMeasuredElectricalPowerKw * 1.10m, 2);
        var electricalSavings = electricalOver ? Math.Round((community.ContractedElectricalPowerKw - recommendedElectrical) * electricalFixedChargePerKwYear, 2) : 0m;

        var summary = (heatingOver, electricalOver) switch
        {
            (true, true) => $"Heating contracted power exceeds peak by more than 20 percent; recommended {recommendedHeating} MW. Electrical contracted power exceeds peak; recommended {recommendedElectrical} kW.",
            (true, false) => $"Heating contracted power exceeds peak by more than 20 percent; recommended {recommendedHeating} MW.",
            (false, true) => $"Electrical contracted power exceeds peak by more than 20 percent; recommended {recommendedElectrical} kW.",
            _ => "Contracted power is within 20 percent of measured peak demand."
        };
        return new ContractedPowerAuditResult(heatingOver, recommendedHeating, heatingSavings, electricalOver, recommendedElectrical, electricalSavings, summary);
    }
}

public static class ReserveStudyCalculator
{
    public static ReserveStudyResult Project(
        decimal startingBalance,
        decimal totalFloorAreaM2,
        decimal monthlyRatePerM2,
        IReadOnlyList<ReserveStudyComponent> components,
        int currentYear)
    {
        var annualInflow = Math.Round(totalFloorAreaM2 * monthlyRatePerM2 * 12m, 2);
        var years = new List<ReserveYearProjection>();
        var balance = startingBalance;
        for (var i = 1; i <= 10; i++)
        {
            var capex = components.Where(c => c.RemainingLifeYears == i).Sum(c => c.EstimatedReplacementCost);
            var start = balance;
            balance = Math.Round(balance + annualInflow - capex, 2);
            years.Add(new ReserveYearProjection(currentYear + i, start, annualInflow, Math.Round(capex, 2), balance));
        }

        var totalLiability = components.Sum(c => c.EstimatedReplacementCost);
        var percentFunded = totalLiability <= 0m ? 100m : Math.Round(startingBalance / totalLiability * 100m, 2);

        var requiredRate = monthlyRatePerM2;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var testInflow = Math.Round(totalFloorAreaM2 * requiredRate * 12m, 2);
            var testBalance = startingBalance;
            var ok = true;
            for (var i = 1; i <= 10; i++)
            {
                var capex = components.Where(c => c.RemainingLifeYears == i).Sum(c => c.EstimatedReplacementCost);
                testBalance += testInflow - capex;
                if (testBalance < 0m) { ok = false; break; }
            }
            if (ok) break;
            requiredRate += 0.05m;
        }
        return new ReserveStudyResult(years, percentFunded, Math.Round(requiredRate, 2));
    }

    public static GreenGrantRoiResult ComputeGrantRoi(decimal grossCapex, decimal subsidyPercent, decimal annualSavings)
    {
        var subsidy = Math.Round(grossCapex * subsidyPercent / 100m, 2);
        var net = Math.Round(grossCapex - subsidy, 2);
        var payback = annualSavings <= 0m ? 0m : Math.Round(net / annualSavings, 2);
        return new GreenGrantRoiResult(subsidy, net, payback);
    }
}

public static class BoqCostEstimator
{
    public static BoqEstimateResult EstimateRepairBoq(string defectConcept, decimal measuredAreaM2)
    {
        var concept = defectConcept.ToLowerInvariant();
        decimal prepRate = 18m, materialRate = 42m, finishRate = 28m, laborHoursPerM2 = 1.6m, laborRate = 65m;
        if (concept.Contains("crack", StringComparison.Ordinal)) { prepRate = 14m; materialRate = 30m; finishRate = 24m; laborHoursPerM2 = 1.2m; }
        if (concept.Contains("spalling", StringComparison.Ordinal)) { prepRate = 26m; materialRate = 58m; finishRate = 32m; laborHoursPerM2 = 2.4m; }
        if (concept.Contains("water", StringComparison.Ordinal) || concept.Contains("stain", StringComparison.Ordinal)) { prepRate = 18m; materialRate = 46m; finishRate = 28m; }

        var items = new List<BoqLineItem>
        {
            new("Surface preparation and protection", measuredAreaM2, "m2", prepRate, Math.Round(measuredAreaM2 * prepRate, 2)),
            new("Waterproofing, priming and repair material", measuredAreaM2, "m2", materialRate, Math.Round(measuredAreaM2 * materialRate, 2)),
            new("Plaster, tile or finish material", measuredAreaM2, "m2", finishRate, Math.Round(measuredAreaM2 * finishRate, 2)),
            new("Certified trade labor", Math.Round(measuredAreaM2 * laborHoursPerM2, 2), "h", laborRate, Math.Round(measuredAreaM2 * laborHoursPerM2 * laborRate, 2))
        };
        var material = items.Take(3).Sum(i => i.Total);
        var labor = items.Skip(3).Sum(i => i.Total);
        return new BoqEstimateResult(Math.Round(material, 2), Math.Round(labor, 2), Math.Round(material + labor, 2), items);
    }
}

public static class VotingCalculator
{
    public static VotingTallyResult CalculateTally(Resolution resolution, IReadOnlyList<Vote> votes)
    {
        var deduped = votes.GroupBy(v => v.UnitId).Select(g => g.OrderByDescending(v => v.CastAt).First()).ToList();
        var sharesFor = deduped.Where(v => v.Choice == VoteChoice.For).Sum(v => v.OwnershipShareWeight);
        var sharesAgainst = deduped.Where(v => v.Choice == VoteChoice.Against).Sum(v => v.OwnershipShareWeight);
        var sharesAbstain = deduped.Where(v => v.Choice == VoteChoice.Abstain).Sum(v => v.OwnershipShareWeight);
        var ownersFor = deduped.Count(v => v.Choice == VoteChoice.For);
        var ownersAgainst = deduped.Count(v => v.Choice == VoteChoice.Against);
        var ownersAbstain = deduped.Count(v => v.Choice == VoteChoice.Abstain);

        var totalSharesCast = sharesFor + sharesAgainst + sharesAbstain;
        // Statutory switch: holders of at least 20% of all shares may demand 1-owner-1-vote.
        var switchTriggered = resolution.SwitchToOneOwnerOneVoteTriggered || totalSharesCast >= 0.20m && resolution.SwitchToOneOwnerOneVoteTriggered;
        var effective = switchTriggered ? VotingMajorityRule.OneOwnerOneVote : resolution.MajorityRule;

        var totalOwners = ownersFor + ownersAgainst + ownersAbstain;
        bool adopted = effective switch
        {
            VotingMajorityRule.OneOwnerOneVote => totalOwners > 0 && ownersFor > totalOwners / 2.0,
            VotingMajorityRule.SupermajorityTwoThirds => (sharesFor + sharesAgainst) > 0m && sharesFor / (sharesFor + sharesAgainst) >= 0.6667m,
            _ => (sharesFor + sharesAgainst) > 0m && sharesFor / (sharesFor + sharesAgainst) > 0.5000m
        };
        return new VotingTallyResult(
            Math.Round(sharesFor, 4), Math.Round(sharesAgainst, 4), Math.Round(sharesAbstain, 4),
            ownersFor, ownersAgainst, ownersAbstain, switchTriggered, effective, adopted);
    }

    public static bool CheckOneFifthSwitch(decimal requestingShares) => requestingShares >= 0.20m;
}

public static class LedgerService
{
    public static bool TryPost(JournalEntry entry, IReadOnlyList<LedgerAccount> accounts, out string error)
    {
        if (!entry.IsBalanced())
        {
            error = "Journal entry is not balanced or has fewer than two lines.";
            return false;
        }
        foreach (var line in entry.Lines)
        {
            var account = accounts.FirstOrDefault(a => a.Id == line.LedgerAccountId || string.Equals(a.Code, line.AccountCode, StringComparison.Ordinal));
            if (account is null) continue;
            account.Balance = Math.Round(account.Balance + line.Debit - line.Credit, 2);
        }
        error = string.Empty;
        return true;
    }

    public static IReadOnlyList<(Guid UnitId, decimal Allocated)> AllocatePaymentFifo(decimal amount, IReadOnlyList<Unit> unitsByOldestArrears)
    {
        var result = new List<(Guid, decimal)>();
        var remaining = amount;
        foreach (var unit in unitsByOldestArrears.Where(u => u.CurrentBalance > 0m).OrderByDescending(u => u.DaysPastDue))
        {
            if (remaining <= 0m) break;
            var allocated = Math.Min(remaining, unit.CurrentBalance);
            result.Add((unit.Id, Math.Round(allocated, 2)));
            remaining -= allocated;
        }
        return result;
    }
}

public static partial class DataSecurityAndPrivacyGuard
{
    private static readonly string[] InjectionPatterns =
    [
        "ignore previous instructions",
        "ignore all previous",
        "system prompt",
        "override iban",
        "approve payment",
        "reveal access code",
        "reveal all",
        "<script",
        "disregard previous",
        "you are now"
    ];

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(\+?\d[\d\s\-()]{7,}\d)", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b|\b\d{4}[\s-]?\d{4}[\s-]?\d{4}[\s-]?\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex IbanRegex();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b|\b\d{10,11}\b", RegexOptions.Compiled)]
    private static partial Regex TaxIdRegex();

    [GeneratedRegex(@"#\d{4}\*|PIN\s*\d{4}|\b\d{4}#", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PinRegex();

    public static string RedactPiiForModelPrompt(string rawText, out int redactedTokensCount)
    {
        var count = 0;
        var text = rawText ?? string.Empty;
        text = EmailRegex().Replace(text, _ => { count++; return "[EMAIL_REDACTED]"; });
        text = IbanRegex().Replace(text, _ => { count++; return "[IBAN_MASKED]"; });
        text = TaxIdRegex().Replace(text, _ => { count++; return "[TAX_ID_REDACTED]"; });
        text = PinRegex().Replace(text, _ => { count++; return "[ACCESS_PIN_VAULTED]"; });
        text = PhoneRegex().Replace(text, m =>
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length < 9) return m.Value;
            count++;
            return "[PHONE_REDACTED]";
        });
        redactedTokensCount = count;
        return text;
    }

    public static PromptSecurityScanResult InspectAndSanitizeUntrustedInput(string rawInput)
    {
        var input = rawInput ?? string.Empty;
        var lower = input.ToLowerInvariant();
        foreach (var pattern in InjectionPatterns)
        {
            if (lower.Contains(pattern, StringComparison.Ordinal))
            {
                return new PromptSecurityScanResult(true, pattern, string.Empty);
            }
        }
        var safe = input.Replace("<untrusted_input>", string.Empty, StringComparison.OrdinalIgnoreCase)
                        .Replace("</untrusted_input>", string.Empty, StringComparison.OrdinalIgnoreCase);
        return new PromptSecurityScanResult(false, string.Empty, $"<untrusted_input>{safe}</untrusted_input>");
    }

    public static string MaskSecretForRole(string secretValue, WorkspaceSecurityRole role, bool isExplicitlyRevealed, bool isContractorCode = false)
    {
        if (string.IsNullOrEmpty(secretValue)) return string.Empty;
        if (role == WorkspaceSecurityRole.Contractor)
        {
            return isContractorCode ? secretValue : "#••••*";
        }
        if (isExplicitlyRevealed && role == WorkspaceSecurityRole.PropertyManager)
        {
            return secretValue;
        }
        return secretValue.StartsWith('#') ? "#••••*" : "PIN ••••";
    }

    public static string MaskIban(string iban, WorkspaceSecurityRole role, bool isRevealed, bool isOwnUnit = false)
    {
        if (string.IsNullOrWhiteSpace(iban)) return string.Empty;
        if (role == WorkspaceSecurityRole.PropertyManager && isRevealed)
        {
            return iban;
        }
        if (role == WorkspaceSecurityRole.Resident && isOwnUnit && isRevealed)
        {
            return iban;
        }
        var compact = iban.Replace(" ", string.Empty, StringComparison.Ordinal);
        var last4 = compact.Length >= 4 ? compact[^4..] : compact;
        var prefix = compact.Length >= 4 ? compact[..4] : compact;
        return $"{prefix} •••• •••• {last4}";
    }

    public static string MaskContactPii(string contact, WorkspaceSecurityRole role, bool isOwnUnit = false)
    {
        if (string.IsNullOrWhiteSpace(contact)) return string.Empty;
        if (role == WorkspaceSecurityRole.PropertyManager)
        {
            return contact;
        }
        if (role == WorkspaceSecurityRole.Resident && isOwnUnit)
        {
            return contact;
        }
        if (contact.Contains('@', StringComparison.Ordinal))
        {
            var parts = contact.Split('@');
            var name = parts[0].Length <= 2 ? "**" : $"{parts[0][..2]}***";
            return $"{name}@{parts[1]}";
        }
        if (role == WorkspaceSecurityRole.Contractor)
        {
            return contact;
        }
        var digits = new string(contact.Where(char.IsDigit).ToArray());
        return digits.Length >= 3 ? $"*** *** {digits[^3..]}" : "***";
    }

    public static bool CanViewUnitPrivateDetails(WorkspaceSecurityRole role, string? currentUserLinkedUnitNumber, string targetUnitNumber)
    {
        if (role == WorkspaceSecurityRole.PropertyManager)
        {
            return true;
        }
        if (role == WorkspaceSecurityRole.Resident &&
            !string.IsNullOrWhiteSpace(currentUserLinkedUnitNumber) &&
            string.Equals(currentUserLinkedUnitNumber, targetUnitNumber, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    public static SecurityAuditEvent CreateChainedAuditEvent(
        Guid communityId,
        WorkspaceSecurityRole actorRole,
        string actionCategory,
        string severity,
        string resourceDescriptor,
        string summary,
        string previousChainHash)
    {
        var ev = new SecurityAuditEvent
        {
            CommunityId = communityId,
            ActorRole = actorRole,
            ActionCategory = actionCategory,
            Severity = severity,
            ResourceDescriptor = resourceDescriptor,
            Summary = summary,
            PreviousChainHash = previousChainHash,
            OccurredAtUtc = DateTime.UtcNow
        };
        ev.EntrySha256Hash = ComputeEntryHash(ev);
        return ev;
    }

    public static string ComputeEntryHash(SecurityAuditEvent ev)
    {
        var payload = $"{ev.CommunityId}|{ev.ActorRole}|{ev.ActionCategory}|{ev.ResourceDescriptor}|{ev.Summary}|{ev.PreviousChainHash}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static (bool IsValid, int VerifiedCount) VerifyAuditChainIntegrity(IReadOnlyList<SecurityAuditEvent> orderedEvents)
    {
        var previous = "GENESIS-0000000000000000";
        var count = 0;
        foreach (var ev in orderedEvents.OrderBy(e => e.OccurredAtUtc))
        {
            if (!string.Equals(ev.PreviousChainHash, previous, StringComparison.Ordinal)) return (false, count);
            if (!string.Equals(ev.EntrySha256Hash, ComputeEntryHash(ev), StringComparison.Ordinal)) return (false, count);
            previous = ev.EntrySha256Hash;
            count++;
        }
        return (true, count);
    }

    public static string HashPasswordPbkdf2(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPasswordPbkdf2(string password, string storedSaltAndHash)
    {
        if (string.IsNullOrWhiteSpace(storedSaltAndHash)) return false;
        var parts = storedSaltAndHash.Split('.', 2);
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static void EnsureCommunityScope(ICommunityScoped entity, Guid expectedCommunityId)
    {
        if (entity.CommunityId != expectedCommunityId)
        {
            throw new InvalidOperationException("Community scope violation: entity does not belong to the active community.");
        }
    }
}
