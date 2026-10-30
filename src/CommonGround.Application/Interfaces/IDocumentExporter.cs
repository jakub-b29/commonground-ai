using CommonGround.Domain.Entities;

namespace CommonGround.Application.Interfaces;

public interface IDocumentExporter
{
    byte[] GenerateAnnualMeetingPackPdf(Community community);
    byte[] GenerateMeetingMinutesPdf(Community community, MeetingRecord meeting, IReadOnlyList<Resolution> resolutions);
    byte[] GenerateMeetingMinutesDocx(Community community, MeetingRecord meeting, IReadOnlyList<Resolution> resolutions);
    byte[] GenerateInsuranceClaimDossierPdf(Community community, WorkOrder workOrder, InsuranceClaimDossier claim);
    byte[] GenerateLegalOrEstoppelPackagePdf(Community community, Unit unit);
}
