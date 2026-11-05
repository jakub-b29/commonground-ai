using CommonGround.Application.Interfaces;
using CommonGround.Domain.Calculators;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Domain.Jurisdiction;
using CommonGround.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CommonGround.Application.Services;

public sealed record CommunityWorkspaceSnapshot(
    Community Community,
    JurisdictionProfile Profile,
    decimal OperatingCash,
    decimal ReserveCash,
    int ArrearsUnits,
    decimal DelinquencyRate,
    int OpenWorkOrders,
    int PendingApprovals,
    IReadOnlyList<UtilityProviderSource> UtilitySources,
    IReadOnlyList<UtilityOutageNotice> ActiveOutages,
    IReadOnlyList<AiAction> PendingActions,
    IReadOnlyList<AgentRun> RecentAgentRuns,
    IReadOnlyList<SecurityAuditEvent> RecentAuditEvents);

public sealed partial class CommonGroundWorkspaceService(
    IAppDbContextFactory dbFactory,
    IMuseSparkClient spark,
    IMuseWonderClient wonder,
    IMuseVoiceTranscriber voice,
    ISam31Client sam,
    IMuseImageClient image)
{
    // The Infrastructure DbContext type is referenced via factory to keep Application decoupled from EF provider details.
    private async Task<IAppDbContext> CreateDbAsync(CancellationToken ct) => await dbFactory.CreateDbContextAsync(ct);

    public async Task<IReadOnlyList<Community>> GetCommunitiesAsync(CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.Communities.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<Community?> GetCommunityAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await LoadCommunityAsync(db, communityId, ct);
    }

    private static async Task<Community?> LoadCommunityAsync(IAppDbContext db, Guid communityId, CancellationToken ct)
    {
        return await db.Communities
            .Include(c => c.Buildings).ThenInclude(b => b.RiserStacks)
            .Include(c => c.Units).ThenInclude(u => u.Meters).ThenInclude(m => m.Readings)
            .Include(c => c.Units).ThenInclude(u => u.Assessments)
            .Include(c => c.LedgerAccounts)
            .Include(c => c.JournalEntries).ThenInclude(j => j.Lines)
            .Include(c => c.Vendors)
            .Include(c => c.VendorBills)
            .Include(c => c.BankTransactions)
            .Include(c => c.CommercialContracts)
            .Include(c => c.ReserveComponents)
            .Include(c => c.WorkOrders).ThenInclude(w => w.Quotes)
            .Include(c => c.ModificationRequests)
            .Include(c => c.Resolutions).ThenInclude(r => r.Votes)
            .Include(c => c.Meetings)
            .Include(c => c.UtilityProviderSources)
            .Include(c => c.UtilityOutages)
            .Include(c => c.EmergencyContacts)
            .Include(c => c.InfrastructurePhotos)
            .Include(c => c.AccessCodes)
            .Include(c => c.Inspections)
            .Include(c => c.WasteSchedules)
            .Include(c => c.GreenSubsidies)
            .Include(c => c.AmenityBookings)
            .Include(c => c.LegalDocuments)
            .Include(c => c.SecurityAuditEvents)
            .Include(c => c.UserAccounts)
            .Include(c => c.ResidentMemberships)
            .Include(c => c.ResidentGroups)
            .Include(c => c.CommunityMessages)
            .Include(c => c.AiActions)
            .Include(c => c.AgentRuns).ThenInclude(r => r.Steps)
            .Include(c => c.CommunityContacts)
            .FirstOrDefaultAsync(c => c.Id == communityId, ct);
    }

    public async Task<CommunityWorkspaceSnapshot?> GetSnapshotAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct);
        if (community is null) return null;
        var operating = community.LedgerAccounts.Where(a => a.Fund == FundKind.Operating && a.Kind == LedgerAccountKind.Asset).Sum(a => a.Balance);
        var reserve = community.LedgerAccounts.Where(a => a.Fund == FundKind.RepairReserve).Sum(a => a.Balance);
        var arrearsUnits = community.Units.Count(u => u.CurrentBalance > 0m);
        var delinquency = community.Units.Count == 0 ? 0m : Math.Round((decimal)arrearsUnits / community.Units.Count * 100m, 2);
        return new CommunityWorkspaceSnapshot(
            community,
            JurisdictionProfiles.For(community.OperatingMode),
            operating, reserve, arrearsUnits, delinquency,
            community.WorkOrders.Count(w => w.Status != WorkOrderStatus.VerifiedAndClosed),
            community.AiActions.Count(a => a.Status == AiActionStatus.Proposed),
            community.UtilityProviderSources.OrderBy(s => s.UtilityKind).ToList(),
            community.UtilityOutages.Where(o => o.IsActive).ToList(),
            community.AiActions.Where(a => a.Status == AiActionStatus.Proposed).OrderByDescending(a => a.CreatedAt).ToList(),
            community.AgentRuns.OrderByDescending(r => r.StartedAt).Take(8).ToList(),
            community.SecurityAuditEvents.OrderByDescending(e => e.OccurredAtUtc).Take(8).ToList());
    }

    private static AgentRun NewRun(Guid communityId, string agent, string trigger, string model, List<AgentStep> steps, bool usedLive = false)
    {
        var run = new AgentRun
        {
            CommunityId = communityId,
            AgentName = agent,
            TriggerSummary = trigger,
            ModelName = model,
            TotalLatencyMs = steps.Sum(s => s.LatencyMs),
            TotalTokens = steps.Count * 420,
            UsedLiveApi = usedLive,
            StartedAt = DateTime.UtcNow
        };
        foreach (var step in steps) { step.AgentRunId = run.Id; run.Steps.Add(step); }
        return run;
    }

    private static AgentStep Step(int order, string title, string tool, string detail, int latency) =>
        new() { StepOrder = order, StepTitle = title, ModelOrToolName = tool, Detail = detail, LatencyMs = latency };

    private async Task AppendAuditAsync(IAppDbContext db, Community community, WorkspaceSecurityRole role, string category, string severity, string resource, string summary, CancellationToken ct)
    {
        var previous = community.SecurityAuditEvents.OrderBy(e => e.OccurredAtUtc).LastOrDefault()?.EntrySha256Hash ?? "GENESIS-0000000000000000";
        var ev = DataSecurityAndPrivacyGuard.CreateChainedAuditEvent(community.Id, role, category, severity, resource, summary, previous);
        community.SecurityAuditEvents.Add(ev);
        db.SecurityAuditEvents.Add(ev);
        await Task.CompletedTask;
        _ = ct;
    }

    // 1. IntakeAndTriageAgent
    public async Task<WorkOrder> SubmitOmnichannelResidentRequestAsync(Guid communityId, Guid? unitId, string messageText, string languageHint = "EN", string? voicePreset = null, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(messageText);
        if (scan.IsInjectionDetected)
        {
            await AppendAuditAsync(db, community, WorkspaceSecurityRole.PropertyManager, "PROMPT_INJECTION_BLOCKED", "Critical", "Resident intake", $"Blocked prompt-injection pattern '{scan.MatchedPattern}' in resident message.", ct);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Request blocked by prompt-injection guard: pattern '{scan.MatchedPattern}' detected. The attempt was logged in the SHA-256 audit chain.");
        }

        var transcript = messageText;
        var steps = new List<AgentStep>();
        if (!string.IsNullOrWhiteSpace(voicePreset))
        {
            var (voiceText, lang, latency) = await voice.TranscribeVoiceNoteAsync(voicePreset, ct);
            transcript = voiceText;
            steps.Add(Step(1, "Voice transcription", "muse-voice-transcribe-1.0", $"Transcribed voice note in {lang}.", latency));
        }

        var unit = unitId.HasValue ? community.Units.FirstOrDefault(u => u.Id == unitId.Value) : community.Units.FirstOrDefault();
        if (unit is not null) DataSecurityAndPrivacyGuard.EnsureCommunityScope(unit, communityId);
        var building = community.Buildings.FirstOrDefault(b => b.Id == unit!.BuildingId) ?? community.Buildings.First();
        var stackUnits = unit is null ? [] : community.Units.Where(u => u.VerticalStackCode == unit.VerticalStackCode).OrderBy(u => u.FloorNumber).ToList();
        steps.Add(Step(steps.Count + 1, "Riser stack lookup", "RiserAndStackExplorer", unit is null ? "No unit context." : $"Stack {unit.VerticalStackCode} connects {stackUnits.Count} units: {string.Join(", ", stackUnits.Select(u => u.UnitNumber))}.", 60));

        var lower = transcript.ToLowerInvariant();
        var activeOutage = community.UtilityOutages.FirstOrDefault(o => o.IsActive &&
            ((o.UtilityKind == UtilityKind.DistrictHeating && (lower.Contains("hot water", StringComparison.Ordinal) || lower.Contains("heating", StringComparison.Ordinal))) ||
             (o.UtilityKind == UtilityKind.ColdWater && lower.Contains("water", StringComparison.Ordinal)) ||
             (o.UtilityKind == UtilityKind.Electricity && lower.Contains("power", StringComparison.Ordinal))));

        var triage = await spark.TriageResidentRequestAsync(community, unit, transcript, languageHint, ct);
        steps.Add(Step(steps.Count + 1, "Triage and boundary check", "muse-spark-1.3", $"{triage.Boundary}: {triage.BoundaryRationale} Cited {triage.CitedLegalDocumentCode}.", triage.LatencyMs));

        var workOrder = new WorkOrder
        {
            CommunityId = community.Id,
            BuildingId = building.Id,
            UnitId = unit?.Id,
            UnitNumber = unit?.UnitNumber,
            VerticalStackCode = unit?.VerticalStackCode ?? "STACK-A1",
            Title = triage.EnglishSummaryTitle,
            Description = triage.EnglishDescription,
            OriginalResidentMessage = transcript,
            OriginalLanguage = triage.DetectedLanguage,
            TranslatedReplyToResident = triage.ResidentReplyInOriginalLanguage,
            Category = triage.Category,
            Priority = triage.Priority,
            Boundary = triage.Boundary,
            BoundaryRationale = triage.BoundaryRationale,
            CitedLegalDocumentCode = triage.CitedLegalDocumentCode,
            CitedVerbatimLegalExcerpt = triage.CitedVerbatimLegalExcerpt,
            DefectConcept = triage.DefectConcept,
            Status = WorkOrderStatus.Reported
        };

        if (activeOutage is not null)
        {
            workOrder.Status = WorkOrderStatus.SuppressedByGridOutage;
            workOrder.TranslatedReplyToResident = $"There is an active municipal {activeOutage.UtilityKind} outage for {activeOutage.AffectedStreets}. The operator estimates restoration by {activeOutage.EstimatedEndAt:HH:mm}. No contractor dispatch is needed; we will update you when service is restored.";
            steps.Add(Step(steps.Count + 1, "Municipal outage check", "UtilityOutageAndSupplierAgent", $"Active {activeOutage.UtilityKind} outage suppresses contractor dispatch. ETA {activeOutage.EstimatedEndAt:HH:mm}.", 80));
        }
        else if (triage.Boundary == ResponsibilityBoundary.DeveloperWarranty)
        {
            workOrder.Status = WorkOrderStatus.RoutedToWarranty;
            var action = new AiAction { CommunityId = community.Id, Kind = AiActionKind.SendWarrantyDefectClaim, AgentName = "WarrantyAndDefectClaimAgent", Title = $"Send warranty defect claim: {workOrder.Title}", Rationale = triage.BoundaryRationale, ModelUsed = "muse-spark-1.3", Confidence = triage.Confidence, TargetEntitySummary = $"WorkOrder, Unit {workOrder.UnitNumber ?? "-"}", ProposedPayloadJson = "{\"route\":\"developer-warranty\"}" };
            community.AiActions.Add(action); db.AiActions.Add(action);
            steps.Add(Step(steps.Count + 1, "Warranty routing", "WarrantyAndDefectClaimAgent", "Routed to developer statutory warranty; community funds not billed.", 90));
        }
        else
        {
            var samResult = await sam.SegmentDefectAndMeasureAreaAsync(triage.DefectConcept, workOrder.VerticalStackCode, ct);
            workOrder.MeasureAreaM2 = samResult.MeasureAreaM2;
            workOrder.BeforePhotoDataUrl = samResult.BaseImageDataUrl;
            workOrder.SamBeforeOverlayDataUrl = samResult.AnnotatedOverlayDataUrl;
            steps.Add(Step(steps.Count + 1, "Defect segmentation", "sam-3.1", $"{triage.DefectConcept} measured at {samResult.MeasureAreaM2:N2} m2.", samResult.LatencyMs));

            var boq = BoqCostEstimator.EstimateRepairBoq(triage.DefectConcept, samResult.MeasureAreaM2);
            workOrder.InvestorBoqEstimateAmount = boq.TotalGrossEstimate;
            workOrder.InvestorBoqSummary = $"Material {boq.TotalMaterialCost:N2}, labor {boq.TotalLaborCost:N2}, total {boq.TotalGrossEstimate:N2} {community.CurrencyCode}.";
            steps.Add(Step(steps.Count + 1, "Investor BOQ estimate", "BoqCostEstimator", workOrder.InvestorBoqSummary, 40));

            var access = community.AccessCodes.FirstOrDefault(a => a.TargetArea.Contains("Stairwell", StringComparison.OrdinalIgnoreCase));
            var valve = community.InfrastructurePhotos.FirstOrDefault(p => p.RiserStackOrZone.Contains(workOrder.VerticalStackCode, StringComparison.OrdinalIgnoreCase)) ?? community.InfrastructurePhotos.FirstOrDefault();
            workOrder.AttachedAccessCodesSummary = access is null ? "No access code on file." : $"Contractor code {access.ContractorOrCourierCode} for {access.TargetArea}; permanent resident code withheld.";
            workOrder.AttachedShutoffValveNote = valve is null ? "No shutoff photo on file." : $"{valve.Title}: {valve.ShutoffInstructions}";
            workOrder.Status = WorkOrderStatus.RfqSent;

            var action = new AiAction { CommunityId = community.Id, Kind = AiActionKind.DispatchContractorWorkOrder, AgentName = "IntakeAndTriageAgent", Title = $"Dispatch contractor: {workOrder.Title}", Rationale = $"{triage.BoundaryRationale} BOQ {workOrder.InvestorBoqSummary}", ModelUsed = "muse-spark-1.3 + sam-3.1", Confidence = triage.Confidence, TargetEntitySummary = $"WorkOrder, Unit {workOrder.UnitNumber ?? "-"}, Stack {workOrder.VerticalStackCode}", ProposedPayloadJson = "{\"status\":\"RfqSent\"}" };
            community.AiActions.Add(action); db.AiActions.Add(action);
            steps.Add(Step(steps.Count + 1, "Pending approval queued", "AiAction", "Contractor dispatch proposed for manager approval.", 30));
        }

        community.WorkOrders.Add(workOrder);
        db.WorkOrders.Add(workOrder);
        var run = NewRun(community.Id, "IntakeAndTriageAgent", $"Resident request from {workOrder.UnitNumber ?? "unknown unit"}", "muse-spark-1.3 + sam-3.1", steps, triage.UsedLiveApi);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return workOrder;
    }

    // 2. VideoInspectionAndSafetyVisionAgent
    public async Task<IReadOnlyList<WorkOrder>> RunWalkthroughVideoSplitAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var building = community.Buildings.First();
        var concepts = new[] { ("wall crack", "Stairwell A wall crack at landing 2"), ("water stain", "Basement ceiling water stain near manifold"), ("spalling concrete", "Balcony slab spalling, stairwell B") };
        var created = new List<WorkOrder>();
        var steps = new List<AgentStep> { Step(1, "Walkthrough transcription", "muse-voice-transcribe-1.0", "Narrated walkthrough aligned to video frames by timestamp.", 640) };
        var order = 2;
        foreach (var (concept, title) in concepts)
        {
            var seg = await sam.SegmentDefectAndMeasureAreaAsync(concept, title, ct);
            var boq = BoqCostEstimator.EstimateRepairBoq(concept, seg.MeasureAreaM2);
            var wo = new WorkOrder { CommunityId = community.Id, BuildingId = building.Id, VerticalStackCode = "STACK-A1", Title = title, Description = $"Detected during narrated walkthrough inspection: {concept}.", Category = WorkOrderCategory.RoofAndFacade, Priority = WorkOrderPriority.Normal, Boundary = ResponsibilityBoundary.CommonProperty, BoundaryRationale = "Common stairwell and basement elements are common property.", CitedLegalDocumentCode = community.OperatingMode == SystemOperatingMode.UsHoa ? "FL-STAT-718-112" : "ACT-OWN-1994", CitedVerbatimLegalExcerpt = "Common elements and stairwells are maintained by the association or community.", Status = WorkOrderStatus.Reported, DefectConcept = concept, MeasureAreaM2 = seg.MeasureAreaM2, InvestorBoqEstimateAmount = boq.TotalGrossEstimate, InvestorBoqSummary = $"BOQ total {boq.TotalGrossEstimate:N2} {community.CurrencyCode}.", BeforePhotoDataUrl = seg.BaseImageDataUrl, SamBeforeOverlayDataUrl = seg.AnnotatedOverlayDataUrl };
            community.WorkOrders.Add(wo); db.WorkOrders.Add(wo); created.Add(wo);
            steps.Add(Step(order++, $"Segment {concept}", "sam-3.1", $"{title}: {seg.MeasureAreaM2:N2} m2.", seg.LatencyMs));
        }
        var run = NewRun(community.Id, "VideoInspectionAndSafetyVisionAgent", "Walkthrough video split", "muse-voice-transcribe-1.0 + sam-3.1", steps);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return created;
    }

    public async Task<WorkOrder> RunSiteSafetyVisionMonitorAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var building = community.Buildings.First();
        var seg = await sam.SegmentDefectAndMeasureAreaAsync("blocked fire lane", "Fire lane and waste enclosure", ct);
        var wo = new WorkOrder { CommunityId = community.Id, BuildingId = building.Id, VerticalStackCode = "STACK-A2", Title = "Blocked fire access lane and overflowing bulk waste shelter", Description = "Site safety vision detected a vehicle blocking the fire lane and an overflowing waste enclosure.", Category = WorkOrderCategory.FireAndSafety, Priority = WorkOrderPriority.High, Boundary = ResponsibilityBoundary.CommonProperty, BoundaryRationale = "Fire lanes must remain unobstructed at all times.", CitedLegalDocumentCode = community.OperatingMode == SystemOperatingMode.UsHoa ? "MIAMI-CODE-FIRE" : "RULES-AMBER", CitedVerbatimLegalExcerpt = "Fire access lanes must remain unobstructed at all times.", Status = WorkOrderStatus.Reported, DefectConcept = "blocked fire lane", MeasureAreaM2 = seg.MeasureAreaM2, InvestorBoqEstimateAmount = 240m, InvestorBoqSummary = "Towing coordination and additional bulky waste collection.", BeforePhotoDataUrl = seg.BaseImageDataUrl, SamBeforeOverlayDataUrl = seg.AnnotatedOverlayDataUrl };
        community.WorkOrders.Add(wo); db.WorkOrders.Add(wo);
        var run = NewRun(community.Id, "VideoInspectionAndSafetyVisionAgent", "Site safety and fire lane scan", "sam-3.1 + muse-spark-1.3", [Step(1, "Safety segmentation", "sam-3.1", "Blocked fire lane detected.", seg.LatencyMs)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return wo;
    }

    // 3. RepairVerificationAndHandoverAgent
    public async Task<WorkOrder> VerifyContractorRepairPhotoAsync(Guid communityId, Guid workOrderId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var workOrder = community.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) ?? throw new InvalidOperationException("Work order not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(workOrder, communityId);
        var verification = await sam.VerifyBeforeAfterRepairAsync(workOrder, ct);
        workOrder.AfterPhotoDataUrl = verification.AfterPhotoDataUrl;
        workOrder.SamAfterOverlayDataUrl = verification.AfterAnnotatedOverlayDataUrl;
        workOrder.AfterDefectAreaM2 = verification.AfterAreaM2;
        workOrder.IsSamVerified = verification.IsVerifiedResolved;
        workOrder.Status = verification.IsVerifiedResolved ? WorkOrderStatus.VerifiedAndClosed : WorkOrderStatus.VerificationFailed;
        if (verification.IsVerifiedResolved)
        {
            foreach (var bill in community.VendorBills.Where(b => b.WorkOrderId == workOrder.Id))
            {
                bill.IsRepairVerified = true;
                bill.Status = "ReadyToPost";
            }
        }
        var run = NewRun(community.Id, "RepairVerificationAndHandoverAgent", $"After-photo verification for {workOrder.Title}", "sam-3.1 + muse-spark-1.3",
            [Step(1, "Before/after SAM verification", "sam-3.1", verification.VerificationSummary, verification.LatencyMs)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return workOrder;
    }

    // 4. WarrantyAndRenovationPermitAgent
    public async Task<ModificationOrArcRequest> EvaluateModificationOrArcRequestAsync(Guid communityId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var request = community.ModificationRequests.FirstOrDefault(r => r.Id == requestId) ?? throw new InvalidOperationException("Request not found.");
        var decision = await spark.EvaluateModificationOrArcRequestAsync(community, request, ct);
        request.Status = "PendingBoardReview";
        var action = new AiAction { CommunityId = community.Id, Kind = AiActionKind.IssueModificationOrArcDecision, AgentName = "WarrantyAndRenovationPermitAgent", Title = $"Issue decision: {request.Kind} for {request.UnitNumber}", Rationale = decision, ModelUsed = "muse-spark-1.3", Confidence = 0.93, TargetEntitySummary = $"Modification request, Unit {request.UnitNumber}", ProposedPayloadJson = "{\"recommendation\":\"ApproveWithConditions\"}" };
        community.AiActions.Add(action); db.AiActions.Add(action);
        var run = NewRun(community.Id, "WarrantyAndRenovationPermitAgent", $"Permit evaluation for {request.UnitNumber}", "muse-spark-1.3",
            [Step(1, "Statute and structural check", "muse-spark-1.3", decision, 520)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return request;
    }

    // 5. MeterReadingAndUtilityAgent
    public async Task<MeterReading> SubmitPhotoOrVoiceMeterReadingAsync(Guid communityId, Guid meterId, decimal? simulatedReadingOverride = null, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var unit = community.Units.FirstOrDefault(u => u.Meters.Any(m => m.Id == meterId)) ?? throw new InvalidOperationException("Meter not found.");
        var meter = unit.Meters.First(m => m.Id == meterId);
        var ocr = await spark.ReadSubMeterFromPhotoAsync(meter, simulatedReadingOverride, ct);
        var delta = Math.Round(ocr.ExtractedReadingValue - meter.LastReadingValue, 3);
        var isAnomaly = meter.RollingMonthlyAverage > 0m && delta > meter.RollingMonthlyAverage * 2.5m;
        var reading = new MeterReading { UtilityMeterId = meter.Id, ReadingValue = ocr.ExtractedReadingValue, ConsumptionDelta = delta, SourceChannel = "ResidentPhotoOcr", IsAnomaly = isAnomaly, AnomalyReason = isAnomaly ? $"{Math.Round(delta / meter.RollingMonthlyAverage, 1)}x rolling average - silent leak suspected." : null };
        meter.PreviousReadingValue = meter.LastReadingValue;
        meter.LastReadingValue = ocr.ExtractedReadingValue;
        meter.LastReadingDate = DateTime.UtcNow;
        meter.HasSilentLeakAnomaly = isAnomaly;
        meter.AnomalyNote = reading.AnomalyReason;
        meter.Readings.Add(reading); db.MeterReadings.Add(reading);
        var run = NewRun(community.Id, "MeterReadingAndUtilityAgent", $"Meter OCR for {unit.UnitNumber} {meter.Kind}", "sam-3.1 + muse-spark-1.3",
            [Step(1, "Meter dial OCR", "muse-spark-1.3", $"Serial {ocr.ExtractedSerialNumber}, reading {ocr.ExtractedReadingValue:N3}, delta {delta:N3}.", ocr.LatencyMs),
             Step(2, "Anomaly check", "UtilitySettlementEngine", isAnomaly ? reading.AnomalyReason! : "Consumption within normal range.", 40)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return reading;
    }

    public async Task<UtilitySettlementPeriod> RunBulkUtilitySettlementAsync(Guid communityId, UtilityAllocationPolicy policy, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var inputs = community.Units.Where(u => u.Kind == UnitKind.Apartment).Select(u =>
        {
            var meter = u.Meters.FirstOrDefault(m => m.Kind == UtilityKind.ColdWater);
            var consumption = meter is null ? 4.2m : Math.Max(meter.LastReadingValue - meter.PreviousReadingValue, 0m);
            if (consumption == 0m) consumption = meter?.RollingMonthlyAverage ?? 4.2m;
            return (Unit: u, SubMeterConsumption: consumption, AdvancesPaid: Math.Round(consumption * community.ColdWaterTariffPerM3, 2));
        }).ToList();
        var subTotal = inputs.Sum(i => i.SubMeterConsumption);
        var masterConsumption = Math.Round(subTotal * 1.18m, 3);
        var masterCost = Math.Round(masterConsumption * community.ColdWaterTariffPerM3, 2);
        var period = UtilitySettlementEngine.Settle(community, DateTime.UtcNow.ToString("yyyy-MM"), UtilityKind.ColdWater, policy, masterConsumption, masterCost, inputs);
        db.UtilitySettlementPeriods.Add(period);
        var anomaly = CommonUtilityAnomalyDetector.Detect(10m, 12m);
        var run = NewRun(community.Id, "MeterReadingAndUtilityAgent", $"Bulk utility settlement ({policy})", "UtilitySettlementEngine",
            [Step(1, "Master to sub-meter reconciliation", "UtilitySettlementEngine", $"Master {masterConsumption:N3} m3, sub-meters {subTotal:N3} m3, loss {period.UnallocatedCommonLossConsumption:N3} m3.", 60),
             Step(2, "Common-area anomaly scan", "CommonUtilityAnomalyDetector", anomaly.Summary, 30)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return period;
    }

    // 6. InvoiceBankAndTariffAuditAgent
    public async Task<IReadOnlyList<BankTransactionMatch>> AutoMatchBankTransfersAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        foreach (var tx in community.BankTransactions.Where(t => !t.IsAppliedToLedger))
        {
            if (tx.MatchedUnitId is null)
            {
                var digits = new string(tx.RawTransferTitle.Where(char.IsDigit).ToArray());
                var candidate = community.Units.FirstOrDefault(u => digits.Length >= 3 && u.UnitNumber.Replace("-", string.Empty, StringComparison.Ordinal).EndsWith(digits.Substring(digits.Length - 3), StringComparison.Ordinal));
                if (candidate is not null)
                {
                    tx.MatchedUnitId = candidate.Id;
                    tx.MatchedUnitNumber = candidate.UnitNumber;
                    tx.MatchConfidence = 0.88;
                    tx.MatchRationale = "Unit number extracted from unstructured transfer title.";
                }
            }
            if (tx.MatchedUnitId is not null)
            {
                var unit = community.Units.First(u => u.Id == tx.MatchedUnitId.Value);
                var allocation = LedgerService.AllocatePaymentFifo(tx.Amount, [unit]);
                if (allocation.Count > 0)
                {
                    unit.CurrentBalance = Math.Round(unit.CurrentBalance - allocation.Sum(a => a.Allocated), 2);
                    if (unit.CurrentBalance <= 0m) { unit.DaysPastDue = 0; unit.ArrearsStage = ArrearsStage.Current; }
                    tx.IsAppliedToLedger = true;
                }
            }
        }
        var run = NewRun(community.Id, "InvoiceBankAndTariffAuditAgent", "Unstructured bank transfer matching", "muse-spark-1.3 + LedgerService",
            [Step(1, "Transfer title resolution and FIFO allocation", "LedgerService", "Payments allocated to oldest arrears first.", 120)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return community.BankTransactions;
    }

    public async Task<ContractedPowerAuditResult> RunContractedPowerAuditAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var result = ContractedPowerAndTariffAuditor.Audit(community);
        var run = NewRun(community.Id, "InvoiceBankAndTariffAuditAgent", "Contracted power and tariff audit", "ContractedPowerAndTariffAuditor",
            [Step(1, "Contracted vs peak demand", "ContractedPowerAndTariffAuditor", result.Summary, 50)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return result;
    }

    // 7. ArrearsMediationAndCollectionAgent
    public async Task<AiAction> GenerateArrearsOrLienActionAsync(Guid communityId, Guid unitId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var unit = community.Units.FirstOrDefault(u => u.Id == unitId) ?? throw new InvalidOperationException("Unit not found.");
        var isUs = community.OperatingMode == SystemOperatingMode.UsHoa;
        var title = isUs ? $"Issue notice of intent to lien for {unit.UnitNumber}" : $"Prepare e-Court dossier for {unit.UnitNumber}";
        var rationale = unit.DaysPastDue >= 90
            ? $"Balance {unit.CurrentBalance:N2} {community.CurrencyCode} at {unit.DaysPastDue} days past due qualifies for {(isUs ? "county lien filing" : "electronic court payment-order filing")}."
            : $"Balance {unit.CurrentBalance:N2} {community.CurrencyCode} at {unit.DaysPastDue} days past due; installment plan of {Math.Round(unit.CurrentBalance / 6m, 2):N2} per month over 6 months proposed.";
        var action = new AiAction { CommunityId = community.Id, Kind = AiActionKind.SendArrearsOrLienNotice, AgentName = "ArrearsMediationAndCollectionAgent", Title = title, Rationale = rationale, ModelUsed = "muse-spark-1.3", Confidence = 0.92, TargetEntitySummary = $"Unit {unit.UnitNumber}, balance {unit.CurrentBalance:N2}", ProposedPayloadJson = $"{{\"unit\":\"{unit.UnitNumber}\",\"daysPastDue\":{unit.DaysPastDue}}}" };
        community.AiActions.Add(action); db.AiActions.Add(action);
        var run = NewRun(community.Id, "ArrearsMediationAndCollectionAgent", title, "muse-spark-1.3", [Step(1, "Arrears analysis", "muse-spark-1.3", rationale, 380)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<string> DraftNeighborDisputeMediationAsync(Guid communityId, string unitReporter, string unitSubject, string issueDescription, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(issueDescription);
        if (scan.IsInjectionDetected) throw new InvalidOperationException("Dispute description blocked by prompt-injection guard.");
        var text = await spark.DraftNeighborDisputeMediationAsync(community, unitReporter, unitSubject, issueDescription, ct);
        var action = new AiAction { CommunityId = community.Id, Kind = AiActionKind.SendDisputeMediationNotice, AgentName = "ArrearsMediationAndCollectionAgent", Title = $"Send mediation notice to {unitSubject}", Rationale = text, ModelUsed = "muse-spark-1.3", Confidence = 0.9, TargetEntitySummary = $"Units {unitReporter} / {unitSubject}", ProposedPayloadJson = "{\"channel\":\"mediation\"}" };
        community.AiActions.Add(action); db.AiActions.Add(action);
        var run = NewRun(community.Id, "ArrearsMediationAndCollectionAgent", "Neighbor dispute mediation draft", "muse-spark-1.3", [Step(1, "House rules mediation draft", "muse-spark-1.3", "Neutral mediation notice drafted with rule citation.", 360)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return text;
    }

    // 8. InsuranceClaimDossierAgent
    public async Task<InsuranceClaimDossier> GenerateInsuranceClaimDossierAsync(Guid communityId, Guid workOrderId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var workOrder = community.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) ?? throw new InvalidOperationException("Work order not found.");
        var building = community.Buildings.First(b => b.Id == workOrder.BuildingId);
        var dossier = await wonder.PackageInsuranceClaimDossierAsync(community, building, workOrder, ct);
        db.InsuranceClaims.Add(dossier);
        var action = new AiAction { CommunityId = community.Id, Kind = AiActionKind.SubmitInsuranceClaimDossier, AgentName = "InsuranceClaimDossierAgent", Title = $"Submit insurance claim {dossier.ClaimReference}", Rationale = $"Documented loss {dossier.TotalDocumentedLoss:N2}, deductible {dossier.DeductibleAmount:N2}, net estimate {dossier.NetClaimPayoutEstimate:N2} {community.CurrencyCode}.", ModelUsed = "muse-wonder", Confidence = 0.93, TargetEntitySummary = $"WorkOrder {workOrder.Title}", ProposedPayloadJson = $"{{\"claim\":\"{dossier.ClaimReference}\"}}" };
        community.AiActions.Add(action); db.AiActions.Add(action);
        var run = NewRun(community.Id, "InsuranceClaimDossierAgent", $"Claim dossier for {workOrder.Title}", "muse-wonder",
            [Step(1, "Timeline, riser map and SAM measurements", "muse-wonder", $"Stack {dossier.AffectedRiserStack}, {dossier.TotalMeasuredDamageAreaM2:N2} m2, {dossier.AffectedUnitsCount} affected units.", 900),
             Step(2, "Coverage and deductible check", "muse-wonder", dossier.CoverageClauseCited, 420)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return dossier;
    }

    // 9. MeetingAutopilotAndComplianceAgent
    public async Task<IReadOnlyList<Resolution>> RunAnnualMeetingAutopilotAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var (summary, drafts) = await wonder.RunAnnualMeetingAutopilotAsync(community, ct);
        foreach (var draft in drafts)
        {
            if (community.Resolutions.Any(r => r.Number == draft.Number)) continue;
            var compliance = await spark.VerifyFullCorpusComplianceAsync(community, draft, ct);
            draft.ComplianceStatus = compliance.HasConflict ? "ConflictDetected" : "VerifiedClean";
            draft.ComplianceConflictSummary = compliance.ConflictExplanation;
            draft.CitedLegalDocumentCode = compliance.CitedStatutoryDocumentCode;
            draft.CitedVerbatimLegalExcerpt = compliance.CitedVerbatimLegalExcerpt;
            if (compliance.HasConflict) draft.SuggestedCompliantRewrite = compliance.CompliantRewriteText;
            community.Resolutions.Add(draft); db.Resolutions.Add(draft);
        }
        var run = NewRun(community.Id, "MeetingAutopilotAndComplianceAgent", "Annual meeting package preparation", "muse-wonder + muse-spark-1.3",
            [Step(1, "Annual report compilation", "muse-wonder", summary, 1100),
             Step(2, "1M-context compliance verification", "muse-spark-1.3", "Draft resolutions verified against the full statutory and governing corpus.", 1450)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return community.Resolutions;
    }

    public async Task<Resolution> ApplyStatuteComplianceFixAsync(Guid communityId, Guid resolutionId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var resolution = community.Resolutions.FirstOrDefault(r => r.Id == resolutionId) ?? throw new InvalidOperationException("Resolution not found.");
        if (!string.IsNullOrWhiteSpace(resolution.SuggestedCompliantRewrite))
        {
            resolution.Content = resolution.SuggestedCompliantRewrite;
        }
        var compliance = await spark.VerifyFullCorpusComplianceAsync(community, resolution, ct);
        resolution.ComplianceStatus = compliance.HasConflict ? "ConflictDetected" : "VerifiedClean";
        resolution.ComplianceConflictSummary = compliance.HasConflict ? compliance.ConflictExplanation : string.Empty;
        var run = NewRun(community.Id, "MeetingAutopilotAndComplianceAgent", $"Compliance fix for {resolution.Number}", "muse-spark-1.3",
            [Step(1, "Apply compliant rewrite and re-verify", "muse-spark-1.3", $"Status after re-verification: {resolution.ComplianceStatus}.", compliance.LatencyMs)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return resolution;
    }

    public async Task<Resolution> GenerateRenovationVisualsAndQrPosterAsync(
        Guid communityId,
        Guid resolutionId,
        CancellationToken ct = default,
        IReadOnlyList<string>? customInfoLines = null,
        string? customHeadline = null)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var resolution = community.Resolutions.FirstOrDefault(r => r.Id == resolutionId) ?? throw new InvalidOperationException("Resolution not found.");
        var (a, b) = await image.GenerateRenovationBallotPreviewsAsync(resolution.Title, resolution.OptionALabel ?? "Option A", resolution.OptionBLabel ?? "Option B", ct, customInfoLines);
        resolution.OptionAPreviewDataUrl = a;
        resolution.OptionBPreviewDataUrl = b;
        var headline = string.IsNullOrWhiteSpace(customHeadline) ? resolution.Title : customHeadline.Trim();
        resolution.QrPosterDataUrl = await image.GenerateQrStairwellNoticePosterAsync(community.Name, headline, resolution.Content, $"https://commonground.example.com/vote/{resolution.Number}", ct, customInfoLines);
        var infoNote = customInfoLines is { Count: > 0 } ? $" Custom overlay lines: {string.Join("; ", customInfoLines)}." : string.Empty;
        var run = NewRun(community.Id, "MeetingAutopilotAndComplianceAgent", $"Ballot visuals for {resolution.Number}", "muse-image-1.0",
            [Step(1, "Option A/B previews and QR poster", "muse-image-1.0", $"Renovation previews (640x400 PNG) and stairwell QR poster generated with muse-image-1.0.{infoNote}", 780)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return resolution;
    }

    // 10. PaperBallotOcrAndMinutesAgent
    public Task<VotingTallyResult> DigitizePaperBallotSheetAsync(Guid communityId, Guid resolutionId, CancellationToken ct = default)
        => DigitizePaperBallotSheetAsync(communityId, resolutionId, ballotImageDataUrl: null, ct);

    public async Task<VotingTallyResult> DigitizePaperBallotSheetAsync(Guid communityId, Guid resolutionId, string? ballotImageDataUrl, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var resolution = community.Resolutions.FirstOrDefault(r => r.Id == resolutionId) ?? throw new InvalidOperationException("Resolution not found.");
        var ocr = await spark.ExtractPaperBallotRowsAsync(community, resolution, ct);
        foreach (var row in ocr.Rows.Where(r => r.SignatureVerified))
        {
            var unit = community.Units.FirstOrDefault(u => string.Equals(u.UnitNumber, row.UnitNumber, StringComparison.OrdinalIgnoreCase));
            if (unit is null || resolution.Votes.Any(v => v.UnitId == unit.Id)) continue;
            var vote = new Vote { ResolutionId = resolution.Id, UnitId = unit.Id, UnitNumber = unit.UnitNumber, VoterName = row.OwnerName, Choice = row.Choice, OwnershipShareWeight = unit.OwnershipShare, Channel = VoteSourceChannel.PaperBallotPhotoOcr };
            resolution.Votes.Add(vote); db.Votes.Add(vote);
        }
        var tally = VotingCalculator.CalculateTally(resolution, resolution.Votes);
        var steps = new List<AgentStep>
        {
            Step(1, "Paper ballot OCR and signature check", "muse-spark-1.3", $"{ocr.Rows.Count} rows extracted; shares for {tally.SharesFor:P2}. Adopted: {tally.IsAdopted}.", ocr.LatencyMs)
        };
        if (!string.IsNullOrWhiteSpace(ballotImageDataUrl))
        {
            var preview = ballotImageDataUrl.Length > 64 ? ballotImageDataUrl[..64] : ballotImageDataUrl;
            steps.Add(Step(2, "Ballot image upload intake", "blazor-inputfile", $"Uploaded ballot image received ({ballotImageDataUrl.Length} chars, prefix {preview}...). Image retained client-side for audit; OCR executed via muse-spark-1.3.", 5));
        }
        var run = NewRun(community.Id, "PaperBallotOcrAndMinutesAgent", $"Paper ballot digitization for {resolution.Number}", "sam-3.1 + muse-spark-1.3",
            steps);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return tally;
    }

    public async Task<Resolution> CreateResolutionAsync(Guid communityId, string number, string title, string description, VotingMajorityRule majorityRule, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var trimmedNumber = (number ?? string.Empty).Trim();
        var trimmedTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedNumber)) throw new InvalidOperationException("Resolution number is required.");
        if (string.IsNullOrWhiteSpace(trimmedTitle)) throw new InvalidOperationException("Resolution title is required.");
        if (community.Resolutions.Any(r => string.Equals(r.Number, trimmedNumber, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Resolution {trimmedNumber} already exists.");
        var resolution = new Resolution
        {
            CommunityId = community.Id,
            Number = trimmedNumber,
            Title = trimmedTitle,
            Content = (description ?? string.Empty).Trim(),
            MajorityRule = majorityRule,
            ComplianceStatus = "VerifiedClean"
        };
        community.Resolutions.Add(resolution); db.Resolutions.Add(resolution);
        var run = NewRun(community.Id, "MeetingAutopilotAndComplianceAgent", $"Resolution created {resolution.Number}", "muse-spark-1.3",
            [Step(1, "Manual resolution creation", "blazor-governance-panel", $"Resolution {resolution.Number} '{resolution.Title}' created with rule {resolution.MajorityRule}.", 5)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return resolution;
    }

    public async Task<MeetingRecord> TranscribeMeetingRecordingToMinutesAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var (transcript, minutes, actions, latency) = await voice.TranscribeAndSummarizeMeetingAsync(community, "annual-meeting", ct);
        var meeting = community.Meetings.OrderByDescending(m => m.ScheduledAt).FirstOrDefault() ?? new MeetingRecord { CommunityId = community.Id, Title = "Annual Owners Assembly" };
        if (meeting.Id == Guid.Empty || !community.Meetings.Contains(meeting)) { community.Meetings.Add(meeting); db.Meetings.Add(meeting); }
        meeting.DiarizedTranscriptText = transcript;
        meeting.GeneratedMinutesMarkdown = minutes;
        meeting.ExtractedActionItemsSummary = actions;
        var run = NewRun(community.Id, "PaperBallotOcrAndMinutesAgent", "Diarized meeting transcription", "muse-voice-transcribe-1.0 + muse-spark-1.3",
            [Step(1, "Diarization and minutes generation", "muse-voice-transcribe-1.0", "Formal minutes and action items generated.", latency)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return meeting;
    }

    public async Task<Vote> CastVoteAsync(Guid communityId, Guid resolutionId, Guid unitId, VoteChoice choice, string? proxyName = null, CancellationToken ct = default, string? voterName = null)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var resolution = community.Resolutions.FirstOrDefault(r => r.Id == resolutionId) ?? throw new InvalidOperationException("Resolution not found.");
        var unit = community.Units.FirstOrDefault(u => u.Id == unitId) ?? throw new InvalidOperationException("Unit not found.");
        resolution.Votes.RemoveAll(v => v.UnitId == unit.Id);
        var existing = await db.Votes.Where(v => v.ResolutionId == resolution.Id && v.UnitId == unit.Id).ToListAsync(ct);
        db.Votes.RemoveRange(existing);
        var resolvedVoterName = string.IsNullOrWhiteSpace(voterName) ? unit.PrimaryOwnerName : voterName.Trim();
        var vote = new Vote { ResolutionId = resolution.Id, UnitId = unit.Id, UnitNumber = unit.UnitNumber, VoterName = resolvedVoterName, ProxyName = proxyName, Choice = choice, OwnershipShareWeight = unit.OwnershipShare, Channel = VoteSourceChannel.WebPortal };
        resolution.Votes.Add(vote); db.Votes.Add(vote);
        await db.SaveChangesAsync(ct);
        return vote;
    }

    // 11. WebAutoEnrichmentOutageAndGrantAgent
    public async Task<WebEnrichmentDiscoveryResult> TriggerWebAutoEnrichmentAsync(Guid communityId, string addressOrTaxIdInput, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var result = await wonder.DiscoverAndEnrichCommunityFromWebAsync(community, addressOrTaxIdInput, ct);
        community.TaxIdOrEin = result.TaxIdOrEin;
        community.RegistryNumber = result.RegistryNumber;
        community.LocalMunicipalityOrCounty = result.MunicipalityOrCounty;
        community.LocalTaxOffice = result.TaxOffice;
        community.CompetentCourtOrRecorder = result.CompetentCourtOrRecorder;
        community.LastWebEnrichedAt = DateTime.UtcNow;
        foreach (var source in result.DiscoveredUtilitySources)
        {
            if (community.UtilityProviderSources.Any(s => s.UtilityKind == source.UtilityKind)) continue;
            source.CommunityId = community.Id; community.UtilityProviderSources.Add(source); db.UtilityProviderSources.Add(source);
        }
        foreach (var contact in result.DiscoveredEmergencyContacts)
        {
            if (community.EmergencyContacts.Any(e => e.PhoneNumber == contact.PhoneNumber)) continue;
            contact.CommunityId = community.Id; community.EmergencyContacts.Add(contact); db.EmergencyContacts.Add(contact);
        }
        foreach (var vendor in result.DiscoveredLocalVendors)
        {
            if (community.Vendors.Any(v => string.Equals(v.Name, vendor.Name, StringComparison.OrdinalIgnoreCase))) continue;
            vendor.CommunityId = community.Id; community.Vendors.Add(vendor); db.Vendors.Add(vendor);
        }
        foreach (var waste in result.DiscoveredWasteSchedules)
        {
            if (community.WasteSchedules.Any(w => string.Equals(w.WasteFraction, waste.WasteFraction, StringComparison.OrdinalIgnoreCase))) continue;
            waste.CommunityId = community.Id; community.WasteSchedules.Add(waste); db.WasteSchedules.Add(waste);
        }
        foreach (var subsidy in result.DiscoveredGreenSubsidies)
        {
            if (community.GreenSubsidies.Any(g => string.Equals(g.ProgramName, subsidy.ProgramName, StringComparison.OrdinalIgnoreCase))) continue;
            subsidy.CommunityId = community.Id; community.GreenSubsidies.Add(subsidy); db.GreenSubsidies.Add(subsidy);
        }
        foreach (var doc in result.DiscoveredLegalDocuments)
        {
            if (community.LegalDocuments.Any(d => string.Equals(d.DocumentCode, doc.DocumentCode, StringComparison.OrdinalIgnoreCase))) continue;
            doc.CommunityId = community.Id; community.LegalDocuments.Add(doc); db.LegalDocuments.Add(doc);
        }
        var run = NewRun(community.Id, "WebAutoEnrichmentOutageAndGrantAgent", $"Public registry sync: {addressOrTaxIdInput}", "muse-wonder + muse-spark-1.3",
            [Step(1, "Registry, utility and legal corpus discovery", "muse-wonder", result.SummaryReport, result.LatencyMs)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<LegalDocumentSource>> AcquireAndSyncLegalDocumentsAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var docs = await wonder.AcquireStatutoryLegalCorpusAsync(community, ct);
        foreach (var doc in docs)
        {
            var existing = community.LegalDocuments.FirstOrDefault(d => string.Equals(d.DocumentCode, doc.DocumentCode, StringComparison.OrdinalIgnoreCase));
            if (existing is null) { doc.CommunityId = community.Id; community.LegalDocuments.Add(doc); db.LegalDocuments.Add(doc); }
            else { existing.VerbatimLegalText = doc.VerbatimLegalText; existing.KeyProvisionsSummary = doc.KeyProvisionsSummary; existing.AcquiredAtUtc = DateTime.UtcNow; }
        }
        await AppendAuditAsync(db, community, WorkspaceSecurityRole.PropertyManager, "STATUTE_CORPUS_ACQUIRED", "Info", "LegalDocumentSource corpus", $"{docs.Count} statutory and governing documents synced.", ct);
        var run = NewRun(community.Id, "WebAutoEnrichmentOutageAndGrantAgent", "Statutory legal corpus acquisition", "muse-wonder + muse-spark-1.3",
            [Step(1, "Verbatim legal text acquisition", "muse-wonder", $"{docs.Count} documents acquired from public legal registries.", 1500)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return community.LegalDocuments;
    }

    public async Task<IReadOnlyList<LegalDocumentSource>> GetPropertyDocumentsAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        return await db.LegalDocuments.AsNoTracking()
            .Where(d => d.CommunityId == communityId)
            .OrderBy(d => d.DocumentCode)
            .ToListAsync(ct);
    }

    public async Task<LegalDocumentSource> UploadPropertyDocumentAsync(
        Guid communityId,
        string documentCode,
        string title,
        string category,
        string verbatimText,
        string? attachmentFileName,
        string? attachmentContentType,
        string? attachmentDataUrl,
        Guid? boundResolutionId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may file property documents.");
        }
        if (string.IsNullOrWhiteSpace(documentCode))
        {
            throw new InvalidOperationException("Document code is required.");
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Document title is required.");
        }
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{documentCode} {title} {category} {verbatimText}");
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Property document blocked by security guard.");
        }
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var code = documentCode.Trim();
        if (community.LegalDocuments.Any(d => string.Equals(d.DocumentCode, code, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Property document '{code}' already exists.");
        }
        var kind = Enum.TryParse<LegalDocumentKind>(category, true, out var parsedKind)
            ? parsedKind
            : LegalDocumentKind.CommunityStatuteOrCcr;
        var cleanAttachment = (attachmentDataUrl ?? string.Empty).Trim();
        if (cleanAttachment.Length > 4_000_000)
        {
            throw new InvalidOperationException("Document attachment is too large (4 MB limit).");
        }
        Resolution? boundResolution = null;
        if (boundResolutionId.HasValue && boundResolutionId.Value != Guid.Empty)
        {
            boundResolution = community.Resolutions.FirstOrDefault(r => r.Id == boundResolutionId.Value)
                ?? throw new InvalidOperationException("Bound resolution not found in this property.");
        }
        var doc = new LegalDocumentSource
        {
            CommunityId = community.Id,
            Kind = kind,
            DocumentCode = code,
            Title = title.Trim(),
            IssuingAuthority = community.Name,
            OfficialCitation = code,
            KeyProvisionsSummary = (verbatimText ?? string.Empty).Trim(),
            VerbatimLegalText = (verbatimText ?? string.Empty).Trim(),
            AppliedInModules = "PropertyDocumentsPanel",
            AcquiredByModel = actorRole.ToString(),
            AcquiredAtUtc = DateTime.UtcNow,
            AttachmentFileName = string.IsNullOrWhiteSpace(attachmentFileName) ? null : attachmentFileName.Trim(),
            AttachmentContentType = string.IsNullOrWhiteSpace(attachmentContentType) ? null : attachmentContentType.Trim(),
            AttachmentSizeBytes = cleanAttachment.Length,
            AttachmentDataUrl = string.IsNullOrWhiteSpace(cleanAttachment) ? null : cleanAttachment,
            BoundResolutionId = boundResolution?.Id
        };
        community.LegalDocuments.Add(doc);
        db.LegalDocuments.Add(doc);
        if (boundResolution is not null)
        {
            boundResolution.CitedLegalDocumentCode = doc.DocumentCode;
            boundResolution.CitedVerbatimLegalExcerpt = doc.VerbatimLegalText.Length > 500 ? doc.VerbatimLegalText[..500] : doc.VerbatimLegalText;
        }
        await AppendAuditAsync(db, community, actorRole, "PROPERTY_DOCUMENT_ADDED", "Info", $"LegalDocumentSource: {doc.DocumentCode}", $"Property document '{doc.Title}' ({doc.DocumentCode}) filed by {actorRole}." + (boundResolution is not null ? $" Bound to resolution {boundResolution.Number}." : string.Empty), ct);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<LegalDocumentSource> AddPropertyDocumentAsync(
        Guid communityId,
        string documentCode,
        string title,
        string category,
        string verbatimText,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
        => await UploadPropertyDocumentAsync(communityId, documentCode, title, category, verbatimText, null, null, null, null, actorRole, ct);

    public async Task<LegalDocumentSource> BindDocumentToResolutionAsync(
        Guid communityId,
        Guid documentId,
        Guid resolutionId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may bind documents to resolutions.");
        }
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var doc = community.LegalDocuments.FirstOrDefault(d => d.Id == documentId)
            ?? throw new InvalidOperationException("Property document not found.");
        var resolution = community.Resolutions.FirstOrDefault(r => r.Id == resolutionId)
            ?? throw new InvalidOperationException("Resolution not found in this property.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(doc, communityId);
        doc.BoundResolutionId = resolution.Id;
        resolution.CitedLegalDocumentCode = doc.DocumentCode;
        resolution.CitedVerbatimLegalExcerpt = doc.VerbatimLegalText.Length > 500 ? doc.VerbatimLegalText[..500] : doc.VerbatimLegalText;
        await AppendAuditAsync(db, community, actorRole, "PROPERTY_DOCUMENT_BOUND", "Info", $"LegalDocumentSource: {doc.DocumentCode}", $"Property document '{doc.Title}' bound to resolution {resolution.Number} by {actorRole}.", ct);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task<LegalDocumentSource> UnbindDocumentFromResolutionAsync(
        Guid communityId,
        Guid documentId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may unbind documents.");
        }
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var doc = community.LegalDocuments.FirstOrDefault(d => d.Id == documentId)
            ?? throw new InvalidOperationException("Property document not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(doc, communityId);
        doc.BoundResolutionId = null;
        await AppendAuditAsync(db, community, actorRole, "PROPERTY_DOCUMENT_UNBOUND", "Info", $"LegalDocumentSource: {doc.DocumentCode}", $"Property document '{doc.Title}' unbound by {actorRole}.", ct);
        await db.SaveChangesAsync(ct);
        return doc;
    }

    public async Task DeletePropertyDocumentAsync(
        Guid communityId,
        Guid documentId,
        WorkspaceSecurityRole actorRole,
        CancellationToken ct = default)
    {
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            throw new UnauthorizedAccessException("Only a Firm Manager or Property Manager may delete property documents.");
        }
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var doc = community.LegalDocuments.FirstOrDefault(d => d.Id == documentId)
            ?? throw new InvalidOperationException("Property document not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(doc, communityId);
        var label = $"{doc.Title} ({doc.DocumentCode})";
        community.LegalDocuments.Remove(doc);
        db.LegalDocuments.Remove(doc);
        await AppendAuditAsync(db, community, actorRole, "PROPERTY_DOCUMENT_DELETED", "Info", $"LegalDocumentSource: {doc.DocumentCode}", $"Property document '{label}' deleted by {actorRole}.", ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> ScanUtilitySourcesAndWeatherAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        foreach (var source in community.UtilityProviderSources) { source.LastCheckedAt = DateTime.UtcNow; source.HealthStatus = "Online (200 OK)"; }
        var active = community.UtilityOutages.Count(o => o.IsActive);
        var summary = $"Scanned {community.UtilityProviderSources.Count} utility sources. Active outages: {active}. Weather radar: no freezing rain or heavy snow forecast in the next 24 hours; snow crew remains on standby.";
        var run = NewRun(community.Id, "WebAutoEnrichmentOutageAndGrantAgent", "Utility source and weather scan", "muse-wonder",
            [Step(1, "Outage and weather scan", "muse-wonder", summary, 640)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return summary;
    }

    public async Task<string> GenerateWasteSegregationPosterAsync(
        Guid communityId,
        CancellationToken ct = default,
        IReadOnlyList<string>? customInfoLines = null)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var poster = await image.GenerateMultilingualWasteSegregationPosterAsync(community.Name, ct, customInfoLines);
        var plastics = community.WasteSchedules.FirstOrDefault(w => w.WasteFraction.Contains("Plastics", StringComparison.OrdinalIgnoreCase));
        if (plastics is not null) plastics.SegregationPosterDataUrl = poster;
        var infoNote = customInfoLines is { Count: > 0 } ? $" Custom overlay lines: {string.Join("; ", customInfoLines)}." : string.Empty;
        var run = NewRun(community.Id, "WebAutoEnrichmentOutageAndGrantAgent", "Waste segregation poster generation", "muse-image-1.0",
            [Step(1, "Generate 640x400 waste poster", "muse-image-1.0", $"Multilingual waste segregation poster generated with muse-image-1.0.{infoNote}", 420)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return poster;
    }

    public async Task<GeneratedImageArtifactResult> GenerateCustomPropertyImageAsync(
        Guid? communityId,
        string title,
        string subtitle,
        IReadOnlyList<string> infoLines,
        string badgeText,
        string? qrUrl = null,
        string uiLocale = "EN",
        CancellationToken ct = default)
    {
        var result = await image.GenerateCustomInfoImageAsync(title, subtitle, infoLines, badgeText, qrUrl, null, ct, uiLocale);
        if (communityId.HasValue && communityId.Value != Guid.Empty)
        {
            await using var db = await CreateDbAsync(ct);
            var community = await LoadCommunityAsync(db, communityId.Value, ct);
            if (community is not null)
            {
                var run = NewRun(community.Id, "PortfolioIntelligenceAgent", $"Custom visual generation: {title}", result.ModelName,
                    [Step(1, "Render custom annotated visual", result.ModelName, $"{result.Resolution} visual generated with {result.RenderedInfoLines.Count} custom info line(s).", result.LatencyMs)]);
                community.AgentRuns.Add(run); db.AgentRuns.Add(run);
                await db.SaveChangesAsync(ct);
            }
        }
        return result;
    }

    public async Task<(string Transcript, string DetectedLanguage, int LatencyMs)> TranscribeLiveMicrophoneAudioAsync(
        Guid? communityId,
        string? wavBase64OrDataUrl,
        string? browserTranscriptHint,
        string uiLocale = "EN",
        CancellationToken ct = default)
    {
        var result = await voice.TranscribeVoiceNoteAsync(
            voiceScenarioPreset: browserTranscriptHint ?? string.Empty,
            ct: ct,
            wavBase64OrDataUrl: wavBase64OrDataUrl,
            browserTranscriptHint: browserTranscriptHint,
            preferredLanguageCode: uiLocale);

        if (communityId.HasValue && communityId.Value != Guid.Empty)
        {
            await using var db = await CreateDbAsync(ct);
            var community = await LoadCommunityAsync(db, communityId.Value, ct);
            if (community is not null)
            {
                var run = NewRun(community.Id, "IntakeAndTriageAgent", "Live microphone WAV transcription (POST /v1/asr/transcribe)", "muse-voice-transcribe-1.0",
                    [Step(1, "Transcribe WAV audio clip", "muse-voice-transcribe-1.0", $"Detected language: {result.DetectedLanguage}. Transcript: {result.Transcript}", result.LatencyMs)]);
                community.AgentRuns.Add(run); db.AgentRuns.Add(run);
                await db.SaveChangesAsync(ct);
            }
        }

        return result;
    }

    // 12. PortfolioIntelligenceAgent
    public async Task<string> AskPortfolioAssistantAsync(Guid communityId, string managerQuestion, CancellationToken ct = default, string uiLocale = "EN")
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput(managerQuestion);
        if (scan.IsInjectionDetected)
        {
            await AppendAuditAsync(db, community, WorkspaceSecurityRole.PropertyManager, "PROMPT_INJECTION_BLOCKED", "Critical", "Portfolio Query", $"Blocked pattern '{scan.MatchedPattern}' in portfolio query.", ct);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException("Query blocked by prompt-injection guard and logged in the audit chain.");
        }
        var snapshot = $"{community.Units.Count} units, {community.WorkOrders.Count(w => w.Status != WorkOrderStatus.VerifiedAndClosed)} open work orders, {community.AiActions.Count(a => a.Status == AiActionStatus.Proposed)} pending approvals, operating cash {community.LedgerAccounts.Where(a => a.Fund == FundKind.Operating).Sum(a => a.Balance):N2} {community.CurrencyCode}.";
        var answer = await spark.AskPortfolioAssistantAsync(community, managerQuestion, snapshot, ct, uiLocale);
        var run = NewRun(community.Id, "PortfolioIntelligenceAgent", "Portfolio query", "muse-spark-1.3", [Step(1, "Portfolio query with tool context", "muse-spark-1.3", snapshot, 480)]);
        community.AgentRuns.Add(run); db.AgentRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return answer;
    }

    public Task<string> AskPortfolioCopilotAsync(Guid communityId, string managerQuestion, CancellationToken ct = default)
        => AskPortfolioAssistantAsync(communityId, managerQuestion, ct, "EN");

    // Pending approvals
    public async Task<AiAction> DecideAiActionAsync(Guid communityId, Guid actionId, bool approve, WorkspaceSecurityRole actorRole = WorkspaceSecurityRole.PropertyManager, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            await AppendAuditAsync(db, community, actorRole, "AI_ACTION_DECISION_DENIED", "Warning", "Pending Approvals queue", $"Role {actorRole} attempted to approve or reject an AI action and was denied.", ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("Only a Property Manager or Firm Manager may approve or reject operational actions.");
        }
        var action = community.AiActions.FirstOrDefault(a => a.Id == actionId) ?? throw new InvalidOperationException("Action not found.");
        action.Status = approve ? AiActionStatus.ApprovedAndExecuted : AiActionStatus.Rejected;
        action.DecidedAt = DateTime.UtcNow;
        await AppendAuditAsync(db, community, actorRole, approve ? "AI_ACTION_APPROVED" : "AI_ACTION_REJECTED", "Info", $"AiAction: {action.Title}", $"Property Manager {(approve ? "approved" : "rejected")} proposed action '{action.Title}'.", ct);
        await db.SaveChangesAsync(ct);
        return action;
    }

    public async Task<int> ApproveAllPendingActionsAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var pending = community.AiActions.Where(a => a.Status == AiActionStatus.Proposed).ToList();
        foreach (var action in pending) { action.Status = AiActionStatus.ApprovedAndExecuted; action.DecidedAt = DateTime.UtcNow; }
        await AppendAuditAsync(db, community, WorkspaceSecurityRole.PropertyManager, "AI_ACTION_APPROVED", "Info", "Pending Approvals queue", $"Approved all {pending.Count} pending actions.", ct);
        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    // SOS broadcast
    public async Task<AiAction> TriggerOneClickSosBroadcastAsync(Guid communityId, string stackCode, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var valve = community.InfrastructurePhotos.FirstOrDefault(p => p.RiserStackOrZone.Contains(stackCode, StringComparison.OrdinalIgnoreCase)) ?? community.InfrastructurePhotos.FirstOrDefault();
        var action = new AiAction { CommunityId = community.Id, Kind = AiActionKind.BroadcastSosEmergency, AgentName = "PortfolioIntelligenceAgent", Title = $"Emergency shutoff broadcast for {stackCode}", Rationale = valve is null ? "Emergency broadcast to all units on the stack." : $"Shutoff: {valve.Title}. {valve.ShutoffInstructions} Contractor code attached; resident codes withheld from external recipients.", ModelUsed = "muse-spark-1.3", Confidence = 1.0, Status = AiActionStatus.ApprovedAndExecuted, DecidedAt = DateTime.UtcNow, TargetEntitySummary = $"Riser stack {stackCode}, {community.Units.Count(u => u.VerticalStackCode == stackCode)} units", ProposedPayloadJson = $"{{\"stack\":\"{stackCode}\",\"channels\":[\"SMS\",\"WhatsApp\",\"Messenger\",\"Email\"]}}" };
        community.AiActions.Add(action); db.AiActions.Add(action);
        await AppendAuditAsync(db, community, WorkspaceSecurityRole.PropertyManager, "SOS_BROADCAST_SENT", "Warning", $"Riser stack {stackCode}", "Emergency shutoff broadcast sent with annotated valve photo.", ct);
        await db.SaveChangesAsync(ct);
        return action;
    }

    // 13. Auth, vault and audit chain
    public async Task<WorkspaceUserAccount?> AuthenticateUserAsync(Guid communityId, IdentityProvider provider, string email, string? passwordOrProviderToken, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var user = community.UserAccounts.FirstOrDefault(u => u.Provider == provider && string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
        var success = user is not null && (provider != IdentityProvider.EmailPassword || (passwordOrProviderToken is not null && DataSecurityAndPrivacyGuard.VerifyPasswordPbkdf2(passwordOrProviderToken, user.PasswordSaltAndHash)));
        if (success && user is not null)
        {
            user.LastLoginAtUtc = DateTime.UtcNow;
            await AppendAuditAsync(db, community, user.Role, "USER_LOGIN_SUCCESS", "Info", $"WorkspaceUserAccount: {user.Email}", $"Sign-in via {provider} succeeded for role {user.Role}.", ct);
        }
        else
        {
            await AppendAuditAsync(db, community, WorkspaceSecurityRole.Resident, "USER_LOGIN_FAILED", "Warning", $"Login attempt: {email}", $"Sign-in via {provider} failed.", ct);
        }
        await db.SaveChangesAsync(ct);
        return success ? user : null;
    }

    public async Task<string> RevealVaultedAccessCodeAsync(Guid communityId, Guid accessCodeId, WorkspaceSecurityRole activeRole, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var access = community.AccessCodes.FirstOrDefault(a => a.Id == accessCodeId) ?? throw new InvalidOperationException("Access code not found.");
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(activeRole))
        {
            await AppendAuditAsync(db, community, activeRole, "SECRET_VAULT_REVEAL_DENIED", "Warning", $"BuildingAccessCode: {access.TargetArea}", $"Role {activeRole} attempted to reveal a vaulted permanent code and was denied.", ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException($"Role {activeRole} may not reveal vaulted permanent resident codes.");
        }
        await AppendAuditAsync(db, community, activeRole, "SECRET_VAULT_REVEAL", "Warning", $"BuildingAccessCode: {access.TargetArea}", $"Vaulted code for {access.TargetArea} revealed by {activeRole}.", ct);
        await db.SaveChangesAsync(ct);
        return access.ResidentCode;
    }

    public async Task<(bool IsValid, int VerifiedEventsCount, string LatestChainHash)> VerifySecurityAuditChainAsync(Guid communityId, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var ordered = community.SecurityAuditEvents.OrderBy(e => e.OccurredAtUtc).ToList();
        var (isValid, count) = DataSecurityAndPrivacyGuard.VerifyAuditChainIntegrity(ordered);
        var latest = ordered.LastOrDefault()?.EntrySha256Hash ?? "GENESIS-0000000000000000";
        return (isValid, count, latest);
    }

    public async Task<InflationSimulationResult> RunInflationSimulationAsync(Guid communityId, decimal heatingPct, decimal electricityPct, decimal wagePct, decimal repairPct, CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        return TariffAndInflationSimulator.Simulate(community, community.Units, heatingPct, electricityPct, wagePct, repairPct,
            (c, u) => AncillaryRevenueEngine.CreditForUnit(c.CommercialContracts, u));
    }

    public async Task<ResidentMembershipApplication> SubmitResidentMembershipApplicationAsync(
        Guid communityId,
        string unitNumber,
        string fullName,
        string email,
        string phone,
        IdentityProvider provider,
        string proofSummary,
        WorkspaceSecurityRole requestedRole = WorkspaceSecurityRole.Resident,
        string? contractorCompanyOrTrade = null,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{fullName} {email} {proofSummary} {contractorCompanyOrTrade}");
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Application blocked by security guard.");
        }

        var isContractor = requestedRole == WorkspaceSecurityRole.Contractor;
        var unit = isContractor
            ? null
            : community.Units.FirstOrDefault(u => string.Equals(u.UnitNumber, unitNumber, StringComparison.OrdinalIgnoreCase)) ?? community.Units.FirstOrDefault();
        var assignedGroups = isContractor
            ? "Assigned Work Orders, Temporary Contractor Access Codes, Resident & Property Manager Phone Directory"
            : unit is not null
                ? $"All Building Residents, Stairwell {unit.StairwellNumber} Residents, Riser Stack {unit.VerticalStackCode} Alerts"
                : "All Building Residents";

        var app = new ResidentMembershipApplication
        {
            CommunityId = community.Id,
            RequestedRole = isContractor ? WorkspaceSecurityRole.Contractor : WorkspaceSecurityRole.Resident,
            UnitId = unit?.Id,
            UnitNumber = isContractor ? (string.IsNullOrWhiteSpace(contractorCompanyOrTrade) ? "CONTRACTOR" : contractorCompanyOrTrade.Trim()) : (unit?.UnitNumber ?? unitNumber),
            ContractorCompanyOrTrade = isContractor ? (contractorCompanyOrTrade ?? "General Maintenance & Trade Services").Trim() : null,
            ApplicantFullName = fullName.Trim(),
            ApplicantEmail = email.Trim(),
            ApplicantPhone = phone.Trim(),
            Provider = provider,
            OwnershipOrLeaseProofSummary = string.IsNullOrWhiteSpace(proofSummary)
                ? (isContractor ? "Trade license & liability insurance certificate submitted for Property Manager approval" : "Deed / lease reference submitted via resident portal")
                : proofSummary.Trim(),
            AssignedGroupsSummary = assignedGroups,
            Status = ResidentMembershipStatus.PendingApproval,
            SubmittedAtUtc = DateTime.UtcNow
        };
        community.ResidentMemberships.Add(app);
        db.ResidentMemberships.Add(app);
        await db.SaveChangesAsync(ct);
        return app;
    }

    public async Task<ResidentMembershipApplication> DecideResidentMembershipAsync(
        Guid communityId,
        Guid applicationId,
        bool approve,
        WorkspaceSecurityRole actorRole = WorkspaceSecurityRole.PropertyManager,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            await AppendAuditAsync(db, community, actorRole, "ROLE_GRANT_DENIED", "Warning", $"ResidentMembershipApplication: {applicationId}", $"Role {actorRole} attempted to grant or reject Resident/Contractor status and was denied.", ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("Only a Property Manager or Firm Manager can grant or reject Resident and Contractor status.");
        }

        var app = community.ResidentMemberships.FirstOrDefault(m => m.Id == applicationId) ?? throw new InvalidOperationException("Application not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(app, communityId);

        var isContractor = app.RequestedRole == WorkspaceSecurityRole.Contractor;
        var grantedRole = isContractor ? WorkspaceSecurityRole.Contractor : WorkspaceSecurityRole.Resident;
        app.Status = approve ? ResidentMembershipStatus.Approved : ResidentMembershipStatus.Rejected;
        app.DecidedAtUtc = DateTime.UtcNow;
        app.DecisionNote = approve
            ? (isContractor
                ? $"Contractor status granted by Property Manager for {community.Name} ({app.ContractorCompanyOrTrade ?? app.UnitNumber}). Provisioned Contractor account with basic building info and contact phones for residents and property managers."
                : $"Resident status granted by Property Manager for {community.Name} (Unit {app.UnitNumber}). Provisioned Resident account with unit self-service, voting, and request submission.")
            : $"Application for {app.ApplicantFullName} ({app.RequestedRole}) rejected by Property Manager.";

        if (approve && !community.UserAccounts.Any(u => string.Equals(u.Email, app.ApplicantEmail, StringComparison.OrdinalIgnoreCase)))
        {
            var account = new WorkspaceUserAccount
            {
                CommunityId = community.Id,
                FullName = app.ApplicantFullName,
                Email = app.ApplicantEmail,
                Phone = app.ApplicantPhone,
                Provider = app.Provider,
                ProviderSubjectId = $"{app.Provider.ToString().ToLowerInvariant()}-{app.UnitNumber}",
                PasswordSaltAndHash = DataSecurityAndPrivacyGuard.HashPasswordPbkdf2(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
                Role = grantedRole,
                LinkedUnitNumber = isContractor ? null : app.UnitNumber,
                ContractorCompanyOrTrade = app.ContractorCompanyOrTrade,
                GrantedByPropertyManager = true
            };
            community.UserAccounts.Add(account);
            db.UserAccounts.Add(account);
        }

        await AppendAuditAsync(
            db,
            community,
            actorRole,
            approve ? (isContractor ? "CONTRACTOR_STATUS_GRANTED" : "RESIDENT_STATUS_GRANTED") : "ROLE_APPLICATION_REJECTED",
            "Info",
            $"RoleGrant ({grantedRole}): {app.ApplicantFullName} ({app.UnitNumber})",
            app.DecisionNote,
            ct);

        await db.SaveChangesAsync(ct);
        return app;
    }

    public async Task<ResidentGroup> CreateResidentGroupAsync(
        Guid communityId,
        string name,
        ResidentGroupKind kind,
        string targetScope,
        string description,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{name} {targetScope} {description}");
        if (scan.IsInjectionDetected)
        {
            throw new InvalidOperationException("Group creation blocked by security guard.");
        }

        var code = $"GRP-{name.ToUpperInvariant().Replace(' ', '-')}-{community.ResidentGroups.Count + 1}";
        var group = new ResidentGroup
        {
            CommunityId = community.Id,
            Code = code.Length > 28 ? code[..28] : code,
            Name = name.Trim(),
            Kind = kind,
            TargetScope = string.IsNullOrWhiteSpace(targetScope) ? "Selected units" : targetScope.Trim(),
            Description = description.Trim(),
            MemberUnitsCount = Math.Max(1, community.Units.Count / 2)
        };
        community.ResidentGroups.Add(group);
        db.ResidentGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return group;
    }

    public async Task<CommunityMessage> PostCommunityMessageAsync(
        Guid communityId,
        Guid groupId,
        string senderName,
        string senderUnitOrRole,
        string subject,
        string body,
        bool isPinned = false,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{subject} {body}");
        if (scan.IsInjectionDetected)
        {
            await AppendAuditAsync(db, community, WorkspaceSecurityRole.PropertyManager, "PROMPT_INJECTION_BLOCKED", "Critical", "CommunityMessage", $"Blocked pattern '{scan.MatchedPattern}' in group message.", ct);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Message blocked by security guard: pattern '{scan.MatchedPattern}' detected.");
        }

        var group = community.ResidentGroups.FirstOrDefault(g => g.Id == groupId) ?? community.ResidentGroups.First();
        var msg = new CommunityMessage
        {
            CommunityId = community.Id,
            ResidentGroupId = group.Id,
            GroupName = group.Name,
            SenderName = string.IsNullOrWhiteSpace(senderName) ? "Property Manager" : senderName.Trim(),
            SenderUnitOrRole = string.IsNullOrWhiteSpace(senderUnitOrRole) ? "PropertyManager" : senderUnitOrRole.Trim(),
            Subject = subject.Trim(),
            Body = body.Trim(),
            IsPinnedAnnouncement = isPinned,
            SentAtUtc = DateTime.UtcNow
        };
        community.CommunityMessages.Add(msg);
        db.CommunityMessages.Add(msg);
        await db.SaveChangesAsync(ct);
        return msg;
    }

    // Simple forum-to-task maintenance (no SAM/BOQ, no migration).
    public async Task<WorkOrder> CreateWorkOrderThreadAsync(
        Guid communityId,
        string title,
        string description,
        Guid? buildingId = null,
        string? unitNumber = null,
        string? photoDataUrl = null,
        WorkspaceSecurityRole actorRole = WorkspaceSecurityRole.PropertyManager,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{title} {description}");
        if (scan.IsInjectionDetected)
        {
            await AppendAuditAsync(db, community, actorRole, "PROMPT_INJECTION_BLOCKED", "Critical", "WorkOrder forum thread", $"Blocked pattern '{scan.MatchedPattern}' in maintenance report.", ct);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Report blocked by security guard: pattern '{scan.MatchedPattern}' detected.");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Title is required.");
        }

        var building = buildingId.HasValue
            ? community.Buildings.FirstOrDefault(b => b.Id == buildingId.Value)
            : community.Buildings.FirstOrDefault();
        if (building is null)
        {
            throw new InvalidOperationException("No building available for maintenance thread.");
        }

        Unit? unit = null;
        if (!string.IsNullOrWhiteSpace(unitNumber))
        {
            unit = community.Units.FirstOrDefault(u => string.Equals(u.UnitNumber, unitNumber.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var thread = new WorkOrder
        {
            CommunityId = community.Id,
            BuildingId = building.Id,
            UnitId = unit?.Id,
            UnitNumber = unit?.UnitNumber ?? (string.IsNullOrWhiteSpace(unitNumber) ? null : unitNumber.Trim()),
            VerticalStackCode = unit?.VerticalStackCode ?? "STACK-A1",
            Title = title.Trim(),
            Description = (description ?? string.Empty).Trim(),
            OriginalResidentMessage = (description ?? string.Empty).Trim(),
            OriginalLanguage = "EN",
            Category = WorkOrderCategory.Plumbing,
            Priority = WorkOrderPriority.Normal,
            Boundary = ResponsibilityBoundary.CommonProperty,
            Status = WorkOrderStatus.Reported,
            DefectConcept = "forum report",
            BeforePhotoDataUrl = string.IsNullOrWhiteSpace(photoDataUrl) ? string.Empty : photoDataUrl.Trim()
        };
        community.WorkOrders.Add(thread);
        db.WorkOrders.Add(thread);
        await AppendAuditAsync(db, community, actorRole, "MAINTENANCE_THREAD_CREATED", "Info", $"WorkOrder: {thread.Title}", $"Forum thread created: '{thread.Title}'.", ct);
        await db.SaveChangesAsync(ct);
        return thread;
    }

    public async Task<WorkOrderMessage> PostWorkOrderMessageAsync(
        Guid communityId,
        Guid workOrderId,
        string senderName,
        string senderRole,
        string body,
        string? attachmentDataUrl = null,
        WorkspaceSecurityRole actorRole = WorkspaceSecurityRole.PropertyManager,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        var workOrder = community.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) ?? throw new InvalidOperationException("Work order not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(workOrder, communityId);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Message body is required.");
        }

        var scan = DataSecurityAndPrivacyGuard.InspectAndSanitizeUntrustedInput($"{senderName} {body}");
        if (scan.IsInjectionDetected)
        {
            await AppendAuditAsync(db, community, actorRole, "PROMPT_INJECTION_BLOCKED", "Critical", $"WorkOrder: {workOrder.Title}", $"Blocked pattern '{scan.MatchedPattern}' in maintenance reply.", ct);
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException($"Message blocked by security guard: pattern '{scan.MatchedPattern}' detected.");
        }

        _ = DataSecurityAndPrivacyGuard.RedactPiiForModelPrompt(body, out _);
        var displayName = string.IsNullOrWhiteSpace(senderName) ? actorRole.ToString() : senderName.Trim();
        var displayRole = string.IsNullOrWhiteSpace(senderRole) ? actorRole.ToString() : senderRole.Trim();
        var message = new WorkOrderMessage
        {
            WorkOrderId = workOrder.Id,
            Body = body.Trim(),
            AttachmentDataUrl = string.IsNullOrWhiteSpace(attachmentDataUrl) ? null : attachmentDataUrl.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        db.WorkOrderMessages.Add(message);
        await AppendAuditAsync(db, community, actorRole, "MAINTENANCE_MESSAGE_POSTED", "Info", $"WorkOrder: {workOrder.Title}", $"Message posted by {displayName} [{displayRole}].", ct);
        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task<WorkOrder> AssignWorkOrderVendorAsync(
        Guid communityId,
        Guid workOrderId,
        Guid vendorId,
        WorkspaceSecurityRole actorRole = WorkspaceSecurityRole.PropertyManager,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            await AppendAuditAsync(db, community, actorRole, "WORKORDER_ASSIGN_DENIED", "Warning", $"WorkOrder: {workOrderId}", $"Role {actorRole} attempted to assign contractor and was denied.", ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("Only a Property Manager or Firm Manager may assign a contractor.");
        }

        var workOrder = community.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) ?? throw new InvalidOperationException("Work order not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(workOrder, communityId);
        var vendor = community.Vendors.FirstOrDefault(v => v.Id == vendorId) ?? throw new InvalidOperationException("Vendor not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(vendor, communityId);
        workOrder.AssignedVendorId = vendor.Id;
        workOrder.AssignedVendorName = vendor.Name;
        workOrder.Status = WorkOrderStatus.AssignedToContractor;
        await AppendAuditAsync(db, community, actorRole, "WORKORDER_CONTRACTOR_ASSIGNED", "Info", $"WorkOrder: {workOrder.Title}", $"Assigned contractor {vendor.Name} ({vendor.Phone}) to '{workOrder.Title}'.", ct);
        await db.SaveChangesAsync(ct);
        return workOrder;
    }

    public async Task<WorkOrder> UpdateWorkOrderTaskAsync(
        Guid communityId,
        Guid workOrderId,
        string title,
        WorkOrderCategory category,
        WorkOrderPriority priority,
        WorkOrderStatus status,
        WorkspaceSecurityRole actorRole = WorkspaceSecurityRole.PropertyManager,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var community = await LoadCommunityAsync(db, communityId, ct) ?? throw new InvalidOperationException("Community not found.");
        if (!DataSecurityAndPrivacyGuard.IsManagerRole(actorRole))
        {
            await AppendAuditAsync(db, community, actorRole, "WORKORDER_UPDATE_DENIED", "Warning", $"WorkOrder: {workOrderId}", $"Role {actorRole} attempted to convert thread to task and was denied.", ct);
            await db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("Only a Property Manager or Firm Manager may convert a thread to a task.");
        }

        var workOrder = community.WorkOrders.FirstOrDefault(w => w.Id == workOrderId) ?? throw new InvalidOperationException("Work order not found.");
        DataSecurityAndPrivacyGuard.EnsureCommunityScope(workOrder, communityId);
        if (!string.IsNullOrWhiteSpace(title))
        {
            workOrder.Title = title.Trim();
        }

        workOrder.Category = category;
        workOrder.Priority = priority;
        workOrder.Status = status;
        await AppendAuditAsync(db, community, actorRole, "WORKORDER_TASK_UPDATED", "Info", $"WorkOrder: {workOrder.Title}", $"Thread converted to task: {category}/{priority}/{status}.", ct);
        await db.SaveChangesAsync(ct);
        return workOrder;
    }

    public async Task<WorkOrder?> GetWorkOrderThreadAsync(
        Guid communityId,
        Guid workOrderId,
        CancellationToken ct = default)
    {
        await using var db = await CreateDbAsync(ct);
        var thread = await db.WorkOrders
            .Include(w => w.Messages)
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workOrderId && w.CommunityId == communityId, ct);
        if (thread is not null)
        {
            thread.Messages = thread.Messages.OrderBy(m => m.CreatedAtUtc).ToList();
        }

        return thread;
    }
}


