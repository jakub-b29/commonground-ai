using CommonGround.Domain.Enums;

namespace CommonGround.Domain.Entities;

public sealed class LedgerAccount : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public FundKind Fund { get; set; }
    public LedgerAccountKind Kind { get; set; }
    public decimal Balance { get; set; }
}

public sealed class JournalEntry : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public DateTime EntryDate { get; set; } = DateTime.UtcNow;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public FundKind Fund { get; set; }
    public List<JournalLine> Lines { get; set; } = [];

    public bool IsBalanced() =>
        Lines.Count >= 2 &&
        Lines.Sum(l => l.Debit) > 0m &&
        Math.Round(Lines.Sum(l => l.Debit), 2) == Math.Round(Lines.Sum(l => l.Credit), 2);
}

public sealed class JournalLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JournalEntryId { get; set; }
    public Guid LedgerAccountId { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public Guid? UnitId { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

public sealed class MonthlyAssessment : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid UnitId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public string Period { get; set; } = "2026-11";
    public decimal OperatingFee { get; set; }
    public decimal RepairFundFee { get; set; }
    public decimal WasteFee { get; set; }
    public decimal ColdWaterAdvance { get; set; }
    public decimal HotWaterAdvance { get; set; }
    public decimal HeatingAdvance { get; set; }
    public decimal CommercialRevenueCredit { get; set; }
    public decimal TotalAssessment { get; set; }
}

public sealed class UtilitySettlementPeriod : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string PeriodLabel { get; set; } = string.Empty;
    public UtilityKind UtilityKind { get; set; }
    public UtilityAllocationPolicy Policy { get; set; }
    public decimal MasterInvoiceConsumption { get; set; }
    public decimal MasterInvoiceTotalCost { get; set; }
    public decimal SubMeterTotalConsumption { get; set; }
    public decimal UnallocatedCommonLossConsumption { get; set; }
    public decimal UnallocatedCommonLossCost { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<UtilitySettlementUnitResult> UnitResults { get; set; } = [];
}

public sealed class UtilitySettlementUnitResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SettlementPeriodId { get; set; }
    public Guid UnitId { get; set; }
    public string UnitNumber { get; set; } = string.Empty;
    public decimal SubMeterConsumption { get; set; }
    public decimal DirectMeterCost { get; set; }
    public decimal AllocatedCommonLossCost { get; set; }
    public decimal TotalActualCost { get; set; }
    public decimal AdvancesPaid { get; set; }
    public decimal NetBalanceDelta { get; set; }
}

public sealed class Vendor : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public WorkOrderCategory Trade { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string TaxId { get; set; } = string.Empty;
    public string IbanOrRouting { get; set; } = string.Empty;
    public decimal Rating { get; set; } = 4.8m;
    public decimal TypicalHourlyRate { get; set; } = 65.0m;
    public bool IsAiDiscovered { get; set; } = true;
}

public sealed class VendorBill : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public Guid VendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public Guid? WorkOrderId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime IssueDate { get; set; } = DateTime.UtcNow.AddDays(-3);
    public DateTime DueDate { get; set; } = DateTime.UtcNow.AddDays(11);
    public decimal GrossAmount { get; set; }
    public FundKind TargetFund { get; set; } = FundKind.Operating;
    public string SuggestedLedgerAccountCode { get; set; } = "500-MAINT";
    public bool IsAnomaly { get; set; }
    public string? AnomalyReason { get; set; }
    public bool IsRepairVerified { get; set; }
    public string Status { get; set; } = "PendingSamVerification";
}

public sealed class BankTransactionMatch : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public DateTime TransactionDate { get; set; } = DateTime.UtcNow.AddDays(-1);
    public decimal Amount { get; set; }
    public string PayerName { get; set; } = string.Empty;
    public string RawTransferTitle { get; set; } = string.Empty;
    public Guid? MatchedUnitId { get; set; }
    public string? MatchedUnitNumber { get; set; }
    public double MatchConfidence { get; set; }
    public string MatchRationale { get; set; } = string.Empty;
    public bool IsAppliedToLedger { get; set; }
}

public sealed class AncillaryCommercialContract : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string TenantOrAdvertiserName { get; set; } = string.Empty;
    public string AssetType { get; set; } = "FacadeBillboard";
    public decimal MonthlyGrossRevenue { get; set; }
    public bool OffsetRepairFundDirectly { get; set; } = true;
}

public sealed class ReserveStudyComponent : ICommunityScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommunityId { get; set; }
    public string ComponentName { get; set; } = string.Empty;
    public int InstallationYear { get; set; }
    public int UsefulLifeYears { get; set; }
    public int RemainingLifeYears { get; set; }
    public decimal EstimatedReplacementCost { get; set; }
    public int ConditionScore { get; set; } = 3;
}
