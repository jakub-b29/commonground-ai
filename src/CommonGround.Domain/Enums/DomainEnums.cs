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
    FirmManager = 3,
    Accountant = 4,
    MaintenanceDispatcher = 5,
    BoardMemberReadOnly = 6,
    ExternalAuditorView = 7
}

public enum MembershipUnitRelationshipKind
{
    Owner = 0,
    CoOwner = 1,
    Tenant = 2,
    Occupant = 3,
    AuthorizedRepresentative = 4
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

public enum WorkspaceAccountKind
{
    FirmAccount = 0,
    RegularAccount = 1
}

public enum InvitationStatus
{
    Pending = 0,
    Accepted = 1,
    Revoked = 2,
    Expired = 3
}

public enum JoinRequestStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2
}

public enum PropertyAnnouncementCategory
{
    General = 0,
    Maintenance = 1,
    Financial = 2,
    Governance = 3,
    Safety = 4,
    Utility = 5
}

public enum WorkspaceMembershipStatus
{
    PendingEmailConfirmation = 0,
    PendingApproval = 1,
    Approved = 2,
    Rejected = 3,
    Revoked = 4
}

[Flags]
public enum WorkspacePermissionFlags : long
{
    FirmFullAdministration = 1L << 0,
    ManageUnitsAndInfrastructure = 1L << 1,
    ManageWorkOrdersAndInspections = 1L << 2,
    ManageFinancialsAndLedger = 1L << 3,
    ManageUtilitiesAndTariffs = 1L << 4,
    ManageGovernanceAndMeetings = 1L << 5,
    ApproveAiActions = 1L << 6,
    InviteAndApproveMembers = 1L << 7,
    ManagePropertySettings = 1L << 8,
    ContractorWorkOrdersAndQuotes = 1L << 9,
    ContractorSamRepairVerification = 1L << 10,
    ContractorInfrastructureAndCodes = 1L << 11,
    ContractorDirectMessaging = 1L << 12,
    ResidentViewUnitAndLedger = 1L << 13,
    ResidentSubmitMeterReadings = 1L << 14,
    ResidentReportMaintenance = 1L << 15,
    ResidentVoteAndGovernance = 1L << 16,
    ResidentSubmitModificationRequests = 1L << 17,
    ResidentCommunityAndMessages = 1L << 18
}

public static class WorkspacePermissionPresets
{
    private const WorkspacePermissionFlags FirmManagerFlags = (WorkspacePermissionFlags)~0L;

    private const WorkspacePermissionFlags PropertyManagerFlags =
        WorkspacePermissionFlags.ManageUnitsAndInfrastructure
        | WorkspacePermissionFlags.ManageWorkOrdersAndInspections
        | WorkspacePermissionFlags.ManageFinancialsAndLedger
        | WorkspacePermissionFlags.ManageUtilitiesAndTariffs
        | WorkspacePermissionFlags.ManageGovernanceAndMeetings
        | WorkspacePermissionFlags.ApproveAiActions
        | WorkspacePermissionFlags.InviteAndApproveMembers
        | WorkspacePermissionFlags.ManagePropertySettings;

    public static WorkspacePermissionFlags DefaultForRole(WorkspaceSecurityRole role)
    {
        return role switch
        {
            WorkspaceSecurityRole.FirmManager => FirmManagerFlags,
            WorkspaceSecurityRole.PropertyManager => PropertyManagerFlags,
            WorkspaceSecurityRole.Accountant => WorkspacePermissionFlags.ManageFinancialsAndLedger | WorkspacePermissionFlags.ManageUtilitiesAndTariffs | WorkspacePermissionFlags.ManagePropertySettings,
            WorkspaceSecurityRole.MaintenanceDispatcher => WorkspacePermissionFlags.ManageUnitsAndInfrastructure | WorkspacePermissionFlags.ManageWorkOrdersAndInspections | WorkspacePermissionFlags.ManageUtilitiesAndTariffs | WorkspacePermissionFlags.ContractorWorkOrdersAndQuotes | WorkspacePermissionFlags.ContractorSamRepairVerification | WorkspacePermissionFlags.ContractorInfrastructureAndCodes,
            WorkspaceSecurityRole.BoardMemberReadOnly => 0,
            WorkspaceSecurityRole.Contractor => WorkspacePermissionFlags.ContractorWorkOrdersAndQuotes | WorkspacePermissionFlags.ContractorSamRepairVerification | WorkspacePermissionFlags.ContractorInfrastructureAndCodes | WorkspacePermissionFlags.ContractorDirectMessaging,
            WorkspaceSecurityRole.ExternalAuditorView => 0,
            WorkspaceSecurityRole.Resident => WorkspacePermissionFlags.ResidentViewUnitAndLedger | WorkspacePermissionFlags.ResidentSubmitMeterReadings | WorkspacePermissionFlags.ResidentReportMaintenance | WorkspacePermissionFlags.ResidentVoteAndGovernance | WorkspacePermissionFlags.ResidentSubmitModificationRequests | WorkspacePermissionFlags.ResidentCommunityAndMessages,
            _ => 0
        };
    }

    public static bool RequiresPropertyAccess(WorkspaceSecurityRole role)
    {
        return role switch
        {
            WorkspaceSecurityRole.PropertyManager => true,
            WorkspaceSecurityRole.Accountant => true,
            WorkspaceSecurityRole.MaintenanceDispatcher => true,
            WorkspaceSecurityRole.BoardMemberReadOnly => true,
            WorkspaceSecurityRole.Resident => true,
            WorkspaceSecurityRole.Contractor => true,
            WorkspaceSecurityRole.ExternalAuditorView => true,
            _ => false
        };
    }
}


public enum CommunityStatus
{
    Active = 0,
    Onboarding = 1,
    Archived = 2
}

public enum UnitOccupancyStatus
{
    Vacant = 0,
    OwnerOccupied = 1,
    TenantOccupied = 2,
    PendingRegistration = 3,
    UnderMaintenance = 4
}

public enum ResidentAccountStatus
{
    Active = 0,
    PendingVerification = 1,
    Suspended = 2,
    Archived = 3
}

public enum ResidentVerificationStatus
{
    Unverified = 0,
    PendingReview = 1,
    Verified = 2,
    Rejected = 3
}

public enum UnitAssignmentStatus
{
    PendingMoveIn = 0,
    Active = 1,
    MoveOutScheduled = 2,
    Ended = 3,
    Cancelled = 4
}

public enum ResidentAssignmentType
{
    PrimaryResident = 0,
    Tenant = 1,
    OwnerResident = 2,
    CoResident = 3,
    HouseholdMember = 4,
    AuthorizedOccupant = 5
}

public enum UnitOwnershipRole
{
    PrimaryOwner = 0,
    CoOwner = 1,
    CorporateOwner = 2,
    AuthorizedRepresentative = 3
}

public enum CommunityContactCategory
{
    ManagementOffice = 0,
    PropertyManager = 1,
    BoardAssociation = 2,
    Emergency = 3,
    Technical = 4,
    Accounting = 5,
    Maintenance = 6,
    Other = 7
}

public enum ResidentDocumentCategory
{
    OwnershipProof = 0,
    LeaseAgreement = 1,
    RegistrationDocument = 2,
    MoveInMoveOutProtocol = 3,
    SignedConsent = 4,
    PropertyNotice = 5,
    CommunityRuleAcknowledgement = 6,
    Other = 7
}

public enum RegistrationApprovalMode
{
    AutomaticAuthorized = 0,
    ManualFirmApproval = 1
}

public enum RegistrationTokenStatus
{
    Active = 0,
    Used = 1,
    Expired = 2,
    Revoked = 3
}

public enum RegistrationRequestStatus
{
    PendingApproval = 0,
    Approved = 1,
    Rejected = 2,
    AutoApproved = 3
}
