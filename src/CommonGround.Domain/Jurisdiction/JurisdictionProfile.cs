using CommonGround.Domain.Enums;

namespace CommonGround.Domain.Jurisdiction;

public sealed record JurisdictionProfile(
    SystemOperatingMode Mode,
    string ModeLabel,
    string CurrencyCode,
    string CurrencySymbol,
    string GoverningDocumentLabel,
    string InternalStatuteLabel,
    string HouseRulesLabel,
    string UtilityPolicyLabel,
    string AssessmentLabel,
    string RepairFundLabel,
    string WarrantyLabel,
    string InspectionLabel,
    string ArrearsRecoveryLabel,
    string ModificationLabel,
    string NationalStatuteCode,
    string WarrantyCode,
    string BuildingCode,
    string MunicipalCode,
    IReadOnlyList<string> EmergencyNumbers,
    string VotingSwitchDescription);

public static class JurisdictionProfiles
{
    public static JurisdictionProfile For(SystemOperatingMode mode) => mode switch
    {
        SystemOperatingMode.UsHoa => new JurisdictionProfile(
            Mode: SystemOperatingMode.UsHoa,
            ModeLabel: "US HOA & Condo Mode",
            CurrencyCode: "USD",
            CurrencySymbol: "$",
            GoverningDocumentLabel: "CC&Rs, Bylaws and Rules",
            InternalStatuteLabel: "Declaration of CC&Rs",
            HouseRulesLabel: "Bylaws and ARC Charter",
            UtilityPolicyLabel: "RUBS Utility Policy",
            AssessmentLabel: "Regular assessment",
            RepairFundLabel: "Reserve fund (SIRS)",
            WarrantyLabel: "Builder right-to-repair (Ch. 558)",
            InspectionLabel: "Milestone and SIRS inspection",
            ArrearsRecoveryLabel: "Notice of intent to lien and county lien",
            ModificationLabel: "ARC / ACC application",
            NationalStatuteCode: "FL-STAT-718-112",
            WarrantyCode: "FL-STAT-718-116",
            BuildingCode: "FL-STAT-553-899",
            MunicipalCode: "MIAMI-CODE-FIRE",
            EmergencyNumbers: ["911", "311", "811"],
            VotingSwitchDescription: "Allocated interest voting with quorum and proxy rules"),
        _ => new JurisdictionProfile(
            Mode: SystemOperatingMode.HousingAssociation,
            ModeLabel: "Housing Association Mode",
            CurrencyCode: "EUR",
            CurrencySymbol: "EUR",
            GoverningDocumentLabel: "Statute, House Rules and Utility Policy",
            InternalStatuteLabel: "Community Statute",
            HouseRulesLabel: "House Rules and Building Order",
            UtilityPolicyLabel: "Sub-meter reconciliation policy",
            AssessmentLabel: "Monthly advance",
            RepairFundLabel: "Repair fund",
            WarrantyLabel: "5-year statutory warranty (Art. 568)",
            InspectionLabel: "c-KOB periodic inspection",
            ArrearsRecoveryLabel: "Pre-litigation demand and e-Court dossier",
            ModificationLabel: "Renovation consent request",
            NationalStatuteCode: "ACT-OWN-1994",
            WarrantyCode: "CIV-WARR-568",
            BuildingCode: "BLD-LAW-62",
            MunicipalCode: "MUN-WASTE-2025",
            EmergencyNumbers: ["112", "998", "999", "997", "992", "991", "993", "994"],
            VotingSwitchDescription: "Share-weighted voting with statutory 1-owner-1-vote switch at 20% share demand")
    };
}
