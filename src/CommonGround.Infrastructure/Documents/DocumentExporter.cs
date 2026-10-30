using CommonGround.Application.Interfaces;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using PdfDocument = QuestPDF.Fluent.Document;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
using DomainUnit = CommonGround.Domain.Entities.Unit;

namespace CommonGround.Infrastructure.Documents;

public sealed class DocumentExporter : IDocumentExporter
{
    static DocumentExporter()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    private static string Money(Community c, decimal value) => $"{value:N2} {c.CurrencyCode}";

    public byte[] GenerateAnnualMeetingPackPdf(Community community)
    {
        var reserve = ReserveStudyCalculator.Project(
            community.LedgerAccounts.Where(a => a.Fund == FundKind.RepairReserve).Sum(a => a.Balance),
            community.Units.Sum(u => u.FloorAreaM2),
            community.RepairFundRatePerM2,
            community.ReserveComponents, DateTime.UtcNow.Year);

        return PdfDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Lato"));
                page.Header().Column(col =>
                {
                    col.Item().Text(community.Name).FontSize(16).Bold().FontColor("1E3A8A");
                    col.Item().Text($"Annual meeting package - {community.StreetAddress}, {community.City} {community.PostalOrZipCode}").FontSize(9).FontColor("475569");
                });
                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Text("Annual financial summary").FontSize(12).Bold();
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); });
                        table.Header(h =>
                        {
                            h.Cell().Background("F1F5F9").Padding(4).Text("Ledger account").Bold();
                            h.Cell().Background("F1F5F9").Padding(4).Text("Fund").Bold();
                            h.Cell().Background("F1F5F9").Padding(4).AlignRight().Text("Balance").Bold();
                        });
                        foreach (var account in community.LedgerAccounts)
                        {
                            table.Cell().Padding(4).Text($"{account.Code} - {account.Name}");
                            table.Cell().Padding(4).Text(account.Fund.ToString());
                            table.Cell().Padding(4).AlignRight().Text(Money(community, account.Balance));
                        }
                    });
                    var ancillary = AncillaryRevenueEngine.TotalMonthlyRevenue(community.CommercialContracts);
                    col.Item().Text($"Ancillary commercial revenue offset: {Money(community, ancillary)} per month from {community.CommercialContracts.Count} contracts.");
                    col.Item().Text("Utility settlement summary").FontSize(12).Bold();
                    col.Item().Text($"Cold water tariff {Money(community, community.ColdWaterTariffPerM3)} per m3; district heating {Money(community, community.DistrictHeatingTariffPerGj)} per GJ; waste {Money(community, community.WasteFeePerOccupant)} per occupant.");
                    col.Item().Text("10-year reserve study").FontSize(12).Bold();
                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(c => { c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); c.RelativeColumn(); });
                        table.Header(h =>
                        {
                            foreach (var title in new[] { "Year", "Starting", "Inflow", "Capex", "Ending" })
                            {
                                h.Cell().Background("F1F5F9").Padding(4).Text(title).Bold();
                            }
                        });
                        foreach (var year in reserve.Years)
                        {
                            table.Cell().Padding(4).Text(year.Year.ToString());
                            table.Cell().Padding(4).AlignRight().Text(Money(community, year.StartingBalance));
                            table.Cell().Padding(4).AlignRight().Text(Money(community, year.AnnualInflow));
                            table.Cell().Padding(4).AlignRight().Text(Money(community, year.AnnualCapexOutflow));
                            table.Cell().Padding(4).AlignRight().Text(Money(community, year.EndingBalance));
                        }
                    });
                    col.Item().Text($"Reserve percent funded: {reserve.PercentFunded:N2} percent. Minimum monthly rate to avoid deficit: {reserve.MinimumMonthlyRatePerM2ToAvoidDeficit:N2} per m2.");
                    col.Item().Text("Draft resolutions").FontSize(12).Bold();
                    foreach (var resolution in community.Resolutions)
                    {
                        var tally = VotingCalculator.CalculateTally(resolution, resolution.Votes);
                        col.Item().Text($"{resolution.Number} - {resolution.Title}").Bold();
                        col.Item().Text(resolution.Content);
                        col.Item().Text($"Compliance: {resolution.ComplianceStatus}. Majority rule: {resolution.MajorityRule}. Shares for {tally.SharesFor:P2}, against {tally.SharesAgainst:P2}, abstain {tally.SharesAbstain:P2}. Adopted: {tally.IsAdopted}.");
                        if (!string.IsNullOrWhiteSpace(resolution.ComplianceConflictSummary))
                        {
                            col.Item().Text($"Conflict: {resolution.ComplianceConflictSummary} Cited: {resolution.CitedLegalDocumentCode}. {resolution.CitedVerbatimLegalExcerpt}").FontColor("B91C1C");
                        }
                    }
                });
                page.Footer().AlignCenter().Text(x => { x.Span("CommonGround - Annual meeting package - Page "); x.CurrentPageNumber(); });
            });
        }).GeneratePdf();
    }

    public byte[] GenerateMeetingMinutesPdf(Community community, MeetingRecord meeting, IReadOnlyList<Resolution> resolutions)
    {
        return PdfDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Lato"));
                page.Header().Column(col =>
                {
                    col.Item().Text(community.Name).FontSize(16).Bold().FontColor("1E3A8A");
                    col.Item().Text($"Formal meeting minutes - {meeting.Title} - {meeting.ScheduledAt:yyyy-MM-dd}").FontColor("475569");
                });
                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(10);
                    col.Item().Text("Speaker-diarized transcript excerpts").FontSize(12).Bold();
                    col.Item().Text(string.IsNullOrWhiteSpace(meeting.DiarizedTranscriptText) ? "Transcript pending." : meeting.DiarizedTranscriptText);
                    col.Item().Text("Minutes").FontSize(12).Bold();
                    col.Item().Text(string.IsNullOrWhiteSpace(meeting.GeneratedMinutesMarkdown) ? "Minutes pending." : meeting.GeneratedMinutesMarkdown);
                    col.Item().Text("Share-weighted vote results").FontSize(12).Bold();
                    foreach (var resolution in resolutions)
                    {
                        var tally = VotingCalculator.CalculateTally(resolution, resolution.Votes);
                        col.Item().Text($"{resolution.Number} - {resolution.Title}: For {tally.SharesFor:P2} ({tally.OwnersFor} owners), Against {tally.SharesAgainst:P2} ({tally.OwnersAgainst} owners), Abstain {tally.SharesAbstain:P2}. Adopted: {tally.IsAdopted}.");
                    }
                    col.Item().Text("Extracted manager action items").FontSize(12).Bold();
                    col.Item().Text(string.IsNullOrWhiteSpace(meeting.ExtractedActionItemsSummary) ? "No action items extracted." : meeting.ExtractedActionItemsSummary);
                });
                page.Footer().AlignCenter().Text(x => { x.Span("CommonGround - Formal meeting minutes - Page "); x.CurrentPageNumber(); });
            });
        }).GeneratePdf();
    }

    public byte[] GenerateMeetingMinutesDocx(Community community, MeetingRecord meeting, IReadOnlyList<Resolution> resolutions)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new WordDocument(new Body());
            var body = main.Document.Body!;
            void Add(string text, bool bold = false, int size = 22)
            {
                var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
                run.RunProperties = new RunProperties(new FontSize { Val = size.ToString() });
                if (bold) run.RunProperties.Append(new Bold());
                body.Append(new Paragraph(run));
            }
            Add($"{community.Name} - Formal meeting minutes", true, 32);
            Add($"{meeting.Title} - {meeting.ScheduledAt:yyyy-MM-dd}");
            Add("Speaker-diarized transcript excerpts", true, 26);
            Add(string.IsNullOrWhiteSpace(meeting.DiarizedTranscriptText) ? "Transcript pending." : meeting.DiarizedTranscriptText);
            Add("Minutes", true, 26);
            Add(string.IsNullOrWhiteSpace(meeting.GeneratedMinutesMarkdown) ? "Minutes pending." : meeting.GeneratedMinutesMarkdown);
            Add("Share-weighted vote results", true, 26);
            foreach (var resolution in resolutions)
            {
                var tally = VotingCalculator.CalculateTally(resolution, resolution.Votes);
                Add($"{resolution.Number} - {resolution.Title}: For {tally.SharesFor:P2}, Against {tally.SharesAgainst:P2}, Abstain {tally.SharesAbstain:P2}. Adopted: {tally.IsAdopted}.");
            }
            Add("Extracted manager action items", true, 26);
            Add(string.IsNullOrWhiteSpace(meeting.ExtractedActionItemsSummary) ? "No action items extracted." : meeting.ExtractedActionItemsSummary);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    public byte[] GenerateInsuranceClaimDossierPdf(Community community, WorkOrder workOrder, InsuranceClaimDossier claim)
    {
        return PdfDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Lato"));
                page.Header().Column(col =>
                {
                    col.Item().Text("Insurance claim dossier").FontSize(16).Bold().FontColor("1E3A8A");
                    col.Item().Text($"{community.Name} - Claim {claim.ClaimReference} - Policy {claim.PolicyNumber} - {claim.InsurerName}").FontColor("475569");
                });
                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Text("Incident timeline").FontSize(12).Bold();
                    col.Item().Text($"Reported {workOrder.CreatedAt:yyyy-MM-dd HH:mm} - {workOrder.Title}. Status: {workOrder.Status}. Riser stack: {claim.AffectedRiserStack}. Affected units: {claim.AffectedUnitsCount}.");
                    col.Item().Text("Root cause").FontSize(12).Bold();
                    col.Item().Text(claim.RootCauseSummary);
                    col.Item().Text("SAM 3.1 measurements and BOQ").FontSize(12).Bold();
                    col.Item().Text($"Measured damage area: {claim.TotalMeasuredDamageAreaM2:N2} m2. Before area: {workOrder.MeasureAreaM2:N2} m2. After area: {(workOrder.AfterDefectAreaM2 ?? 0m):N2} m2. Investor BOQ estimate: {Money(community, workOrder.InvestorBoqEstimateAmount)}. {workOrder.InvestorBoqSummary}");
                    col.Item().Text("Coverage and deductible").FontSize(12).Bold();
                    col.Item().Text(claim.CoverageClauseCited);
                    col.Item().Text($"Total documented loss: {Money(community, claim.TotalDocumentedLoss)}. Deductible: {Money(community, claim.DeductibleAmount)}. Net claim payout estimate: {Money(community, claim.NetClaimPayoutEstimate)}.");
                    col.Item().Text($"Cited provision: {workOrder.CitedLegalDocumentCode}. {workOrder.CitedVerbatimLegalExcerpt}");
                });
                page.Footer().AlignCenter().Text(x => { x.Span("CommonGround - Insurance claim dossier - Page "); x.CurrentPageNumber(); });
            });
        }).GeneratePdf();
    }

    public byte[] GenerateLegalOrEstoppelPackagePdf(Community community, DomainUnit unit)
    {
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var interest = Math.Round(unit.CurrentBalance * 0.08m * Math.Max(unit.DaysPastDue, 0) / 365m, 2);
        return PdfDocument.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Lato"));
                page.Header().Column(col =>
                {
                    col.Item().Text(isUs ? "Resale estoppel certificate and notice of intent to record assessment lien" : "Electronic court payment order dossier and pre-litigation demand").FontSize(14).Bold().FontColor("1E3A8A");
                    col.Item().Text($"{community.Name} - Unit {unit.UnitNumber} - {unit.PrimaryOwnerName}").FontColor("475569");
                });
                page.Content().PaddingVertical(12).Column(col =>
                {
                    col.Spacing(8);
                    col.Item().Text($"Unit: {unit.UnitNumber}. Ownership share: {unit.ShareNumerator} / {unit.ShareDenominator} ({unit.OwnershipShare:P2}). Floor area: {unit.FloorAreaM2:N2} m2.");
                    col.Item().Text($"Certified ledger balance owed: {Money(community, unit.CurrentBalance)}. Days past due: {unit.DaysPastDue}. Arrears stage: {unit.ArrearsStage}. Statutory interest estimate: {Money(community, interest)}. Total demanded: {Money(community, unit.CurrentBalance + interest)}.");
                    if (isUs)
                    {
                        col.Item().Text("Under Fla. Stat. 718.116, this package serves as the resale estoppel certificate and 30-day notice of intent to record a claim of lien with the county recorder if the balance remains unpaid. Regular and special assessments, late fees and reserve status are stated as of the issue date.");
                    }
                    else
                    {
                        col.Item().Text("Under ACT-OWN-1994 Art. 15, advances are due by the 10th day of each month and unpaid advances may be recovered in electronic payment-order proceedings (e-Court). This dossier certifies the ledger balance, share fraction and delivery proofs for filing.");
                    }
                    col.Item().Text($"Virtual IBAN / lockbox: {unit.VirtualIbanOrLockboxCode}. Issue date: {DateTime.UtcNow:yyyy-MM-dd}.");
                });
                page.Footer().AlignCenter().Text(x => { x.Span("CommonGround - Legal package - Page "); x.CurrentPageNumber(); });
            });
        }).GeneratePdf();
    }
}

