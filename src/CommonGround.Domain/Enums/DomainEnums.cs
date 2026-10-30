namespace CommonGround.Domain.Enums;

public enum SystemOperatingMode
{
    HousingAssociation = 0,
    UsHoa = 1
}

public enum UnitKind
{
    Apartment = 0,
    CommercialPremise = 1,
    GarageBay = 2,
    StorageCell = 3
}

public enum UtilityKind
{
    Electricity = 0,
    ColdWater = 1,
    HotWater = 2,
    DistrictHeating = 3,
    Gas = 4,
    MunicipalWaste = 5
}

public enum FundKind
{
    Operating = 0,
    RepairReserve = 1,
    UtilitiesClearing = 2,
    CommercialAncillary = 3
}

public enum LedgerAccountKind
{
    Asset = 0,
    Liability = 1,
    Equity = 2,
    Revenue = 3,
    Expense = 4
}

public enum ResponsibilityBoundary
{
    CommonProperty = 0,
    PrivateUnit = 1,
    DeveloperWarranty = 2,
    ContractorGuarantee = 3
}

public enum WorkOrderCategory
{
    Plumbing = 0,
    Electrical = 1,
    HeatingAndHvac = 2,
    Elevator = 3,
    RoofAndFacade = 4,
    FireAndSafety = 5,
    WasteAndCleaning = 6,
    GatesAndIntercom = 7,
    SnowAndLandscaping = 8
}

public enum WorkOrderPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Emergency = 3
}

public enum WorkOrderStatus
{
    Reported = 0,
    SuppressedByGridOutage = 1,
    RoutedToWarranty = 2,
    RfqSent = 3,
    AssignedToContractor = 4,
    PendingSamVerification = 5,
    VerificationFailed = 6,
    VerifiedAndClosed = 7
}

public enum InspectionKind
{
    AnnualStructuralAndCommon = 0,
    AnnualChimneyAndVentilation = 1,
    AnnualGasTightness = 2,
    FiveYearStructuralAndFacade = 3,
    FiveYearElectricalAndLightning = 4,
    ElevatorSafetyCertification = 5,
    FireSafetyAndHydrants = 6,
    UsCondoMilestoneAndSirs = 7
}

public enum VotingMajorityRule
{
    ShareWeightedMajority = 0,
    OneOwnerOneVote = 1,
    SupermajorityTwoThirds = 2
}

public enum VoteChoice
{
    For = 0,
    Against = 1,
    Abstain = 2
}

public enum VoteSourceChannel
{
    WebPortal = 0,
    Messenger = 1,
    WhatsApp = 2,
    PaperBallotPhotoOcr = 3,
    InPersonMeeting = 4
}

public enum ArrearsStage
{
    Current = 0,
    FriendlyReminder15Days = 1,
    InstallmentPlanActive = 2,
    PreLitigationOrIntentToLien45Days = 3,
    ECourtDossierOrCountyLien90Days = 4
}

public enum ModificationRequestKind
{
    PartitionWallChange = 0,
    FacadeAirConditioner = 1,
    BalconyGlazing = 2,
    SolarPanels = 3,
    EvChargerInstallation = 4,
    ExteriorPaintOrFence = 5,
    DroughtTolerantLandscaping = 6
}

public enum AiActionKind
{
    DispatchContractorWorkOrder = 0,
    SendWarrantyDefectClaim = 1,
    PostContractorInvoiceToLedger = 2,
    MatchBankTransferToUnit = 3,
    PublishResolutionPackage = 4,
    BroadcastUtilityOutageAlert = 5,
    BroadcastSosEmergency = 6,
    SendArrearsOrLienNotice = 7,
    SendDisputeMediationNotice = 8,
    IssueModificationOrArcDecision = 9,
    SubmitInsuranceClaimDossier = 10,
    DispatchWeatherSnowCrew = 11
}

public enum AiActionStatus
{
    Proposed = 0,
    ApprovedAndExecuted = 1,
    Rejected = 2
}

public enum UtilityAllocationPolicy
{
    SubMeterPlusShareLoss = 0,
    SubMeterPlusAreaLoss = 1,
    UsRubsByFloorArea = 2,
    UsRubsByOccupantCount = 3,
    UsRubsHybrid50Area50Occupants = 4
}

public enum LegalDocumentKind
{
    NationalPropertyStatute = 0,
    CivilWarrantyAndLiabilityCode = 1,
    BuildingAndSafetyRegulation = 2,
    MunicipalBylawAndOrdinance = 3,
    CommunityStatuteOrCcr = 4,
    HouseRulesAndOrder = 5,
    UtilityAndMeteringPolicy = 6
}

public enum WorkspaceSecurityRole
{
    PropertyManager = 0,
    Resident = 1,
    Contractor = 2,
    FirmManager = 3
}

public enum InvitationAssignmentMode
{
    BlankManualAssign = 0,
    AutoAssignSpecificProperty = 1,
    AutoAssignAllFirmProperties = 2
}

public enum WorkspaceInvitationStatus
{
    Pending = 0,
    Accepted = 1,
    Revoked = 2
}

public enum ContractorPropertyScope
{
    SpecificProperty = 0,
    AllFirmProperties = 1
}

public enum IdentityProvider
{
    EmailPassword = 0,
    Facebook = 1,
    Google = 2,
    Apple = 3
}

public enum ResidentMembershipStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2
}

public enum ResidentGroupKind
{
    BuildingWide = 0,
    Stairwell = 1,
    RiserStack = 2,
    BoardAndOwners = 3,
    InterestGroup = 4
}
