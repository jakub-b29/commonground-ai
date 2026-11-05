using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;

namespace CommonGround.Application.Interfaces;

[Flags]
public enum DossierSectionFlags
{
    None = 0,
    IdentityAndTariffs = 1 << 0,
    Authorities = 1 << 1,
    UtilityProviders = 1 << 2,
    WasteSchedule = 1 << 3,
    Vendors = 1 << 4,
    Inspections = 1 << 5,
    GreenSubsidies = 1 << 6,
    LegalCorpus = 1 << 7,
    All = IdentityAndTariffs | Authorities | UtilityProviders | WasteSchedule | Vendors | Inspections | GreenSubsidies | LegalCorpus
}

public sealed record PropertyListItem(
    Guid Id,
    string Name,
    string City,
    string StreetAddress);

public interface IPropertyOnboardingService
{
    Task<PropertyAiDiscoveryDossier> DiscoverPropertyDossierFromAddressAsync(
        AddressLookupRequest request,
        CancellationToken ct = default,
        IProgress<DiscoveryProgressStep>? progress = null,
        string uiLocale = "EN");

    Task<Community> CreatePropertyManualAsync(
        Guid firmId,
        Guid actorMembershipId,
        string name,
        string streetAddress,
        string postalOrZipCode,
        string city,
        string countryOrState,
        SystemOperatingMode operatingMode,
        CancellationToken ct = default);

    Task<Community> CreatePropertyFromDossierAsync(
        Guid firmId,
        Guid actorMembershipId,
        PropertyAiDiscoveryDossier dossier,
        DossierSectionFlags selectedSections,
        string streetAddress,
        string postalOrZipCode,
        string city,
        string countryOrState,
        CancellationToken ct = default);

    Task<Community> ApplyDiscoveryDossierToPropertyAsync(
        Guid communityId,
        Guid actorMembershipId,
        PropertyAiDiscoveryDossier dossier,
        DossierSectionFlags selectedSections,
        CancellationToken ct = default);

    Task<Community> UpdatePropertySettingsAsync(
        Guid communityId,
        Guid actorMembershipId,
        string name,
        string streetAddress,
        string city,
        string taxIdOrEin,
        decimal operatingRatePerM2,
        decimal repairFundRatePerM2,
        decimal wasteFeePerOccupant,
        decimal coldWaterTariff,
        decimal hotWaterTariff,
        decimal heatingTariff,
        decimal electricityTariff,
        CancellationToken ct = default);

    Task<IReadOnlyList<PropertyListItem>> GetPropertyListAsync(
        Guid firmId,
        CancellationToken ct = default);

    Task<Community?> GetPropertyDetailAsync(
        Guid id,
        CancellationToken ct = default);
}
