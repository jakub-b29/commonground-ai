using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CommonGround.Application.Interfaces;
using CommonGround.Application.Services;
using CommonGround.Domain.Entities;
using CommonGround.Domain.Enums;
using CommonGround.Infrastructure.MetaModels;
using CommonGround.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CommonGround.UnitTests;

public sealed class AssistantVoiceImageAndLocaleTests
{
    [Fact]
    public async Task ResolveContextAsync_SelectsHighestPermissionForTargetPropertyMentionedInQuestion()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"HighestPerm_{Guid.NewGuid():N}")
            .Options;

        await using var db = new AppDbContext(dbOptions);
        var firm = new ManagementFirm { Id = Guid.NewGuid(), Name = "Apex Property Group" };
        var amberPark = new Community
        {
            Id = Guid.NewGuid(),
            ManagementFirmId = firm.Id,
            Name = "Amber Park Residence",
            CommunityAccountNumber = "COM-1001",
            City = "Warsaw",
            StreetAddress = "Złota 44"
        };
        var oakridge = new Community
        {
            Id = Guid.NewGuid(),
            ManagementFirmId = firm.Id,
            Name = "Oakridge Palms Condominium",
            CommunityAccountNumber = "COM-2002",
            City = "Miami",
            StreetAddress = "Brickell Ave 500"
        };

        var userId = Guid.NewGuid();
        var residentMembership = new FirmMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ManagementFirmId = firm.Id,
            Role = WorkspaceSecurityRole.Resident,
            Status = WorkspaceMembershipStatus.Approved,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.Resident),
            PropertyAccesses = [new MembershipPropertyAccess { CommunityId = amberPark.Id }]
        };
        var managerMembership = new FirmMembership
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ManagementFirmId = firm.Id,
            Role = WorkspaceSecurityRole.PropertyManager,
            Status = WorkspaceMembershipStatus.Approved,
            GrantedPermissions = WorkspacePermissionPresets.DefaultForRole(WorkspaceSecurityRole.PropertyManager),
            PropertyAccesses = [new MembershipPropertyAccess { CommunityId = oakridge.Id }]
        };

        db.ManagementFirms.Add(firm);
        db.Communities.AddRange(amberPark, oakridge);
        db.FirmMemberships.AddRange(residentMembership, managerMembership);
        await db.SaveChangesAsync();

        // User's active session claim is the lower-privilege Resident membership on Amber Park,
        // but they ask a question specifically about Oakridge Palms where they hold PropertyManager rights.
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("user_id", userId.ToString()),
            new Claim("firm_id", firm.Id.ToString()),
            new Claim("membership_id", residentMembership.Id.ToString()),
            new Claim(ClaimTypes.Role, nameof(WorkspaceSecurityRole.Resident)),
            new Claim("community_id", amberPark.Id.ToString())
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var evaluator = new SparkAgentPermissionEvaluator();
        var context = await evaluator.ResolveContextAsync(
            principal,
            db,
            ct: CancellationToken.None,
            targetCommunityId: amberPark.Id,
            userGoal: "Run utility settlement and check open work orders in Oakridge Palms Condominium");

        Assert.Equal(WorkspaceSecurityRole.PropertyManager, context.EffectiveRole);
        Assert.Equal(managerMembership.Id, context.ActiveMembershipId);
        Assert.Contains(oakridge.Id, context.AllowedCommunityIds);
        Assert.True(context.HasFlag(WorkspacePermissionFlags.ManageFinancialsAndLedger));
        Assert.True(context.HasFlag(WorkspacePermissionFlags.ManageUtilitiesAndTariffs));
    }

    [Theory]
    [InlineData("pl", "Polish (Polski, PL)")]
    [InlineData("es", "Spanish (Español, ES)")]
    [InlineData("fr", "French (Français, FR)")]
    [InlineData("de", "German (Deutsch, DE)")]
    public void Prompts_IncludeSelectedLanguageResponseAndSearchInstructions(string locale, string expectedLanguageName)
    {
        var community = new Community
        {
            Id = Guid.NewGuid(),
            Name = "Amber Park Residence",
            City = "Warsaw",
            CountryCode = "PL",
            CurrencyCode = "PLN"
        };

        var assistantPrompt = MuseSparkDiscoveryPrompts.BuildPortfolioAssistantPrompt(
            community,
            "Show open work orders",
            "24 units, 2 open work orders",
            locale);

        Assert.Contains(expectedLanguageName, assistantPrompt, StringComparison.Ordinal);
        Assert.Contains("search for data in", assistantPrompt, StringComparison.OrdinalIgnoreCase);

        var agenticPrompt = MuseSparkDiscoveryPrompts.BuildSparkAgenticTurnPrompt(
            "Check reserve study",
            "{\"role\":\"PropertyManager\"}",
            "{\"activeCommunityName\":\"Amber Park\"}",
            "[]",
            "[]",
            locale);

        Assert.Contains(expectedLanguageName, agenticPrompt, StringComparison.Ordinal);
        Assert.Contains("search for data in", agenticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never print raw JSON", agenticPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VoiceTranscriber_DecodesOrSynthesizesValidRiffWaveAndTranscribesInLocale()
    {
        var wavBytes = MetaModelClients.DecodeOrSynthesizeWavBytes(null);
        Assert.True(wavBytes.Length > 44);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wavBytes, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wavBytes, 8, 4));

        var base64Wav = Convert.ToBase64String(wavBytes);
        var decodedBytes = MetaModelClients.DecodeOrSynthesizeWavBytes($"data:audio/wav;base64,{base64Wav}");
        Assert.Equal(wavBytes.Length, decodedBytes.Length);

        var clients = new MetaModelClients(
            Options.Create(new MetaModelOptions { ApiKey = string.Empty, UseFallbackWhenNoKey = true }),
            new SamOverlayRenderer());

        var (transcript, detectedLang, latencyMs) = await clients.TranscribeVoiceNoteAsync(
            voiceScenarioPreset: string.Empty,
            ct: CancellationToken.None,
            wavBase64OrDataUrl: base64Wav,
            browserTranscriptHint: "Przeciek wody w pionie STACK-A1",
            preferredLanguageCode: "pl");

        Assert.Equal("Przeciek wody w pionie STACK-A1", transcript);
        Assert.Equal("PL", detectedLang);
        Assert.True(latencyMs > 0);
    }

    [Fact]
    public async Task GenerateCustomInfoImageAsync_RendersPngWithCustomInfoLinesAndMetadata()
    {
        var clients = new MetaModelClients(
            Options.Create(new MetaModelOptions { ApiKey = string.Empty, UseFallbackWhenNoKey = true }),
            new SamOverlayRenderer());

        var infoLines = new[]
        {
            "Shutoff Valve V-A1-01 verified",
            "Contractor temporary PIN: #9021*",
            "24/7 emergency dispatch active"
        };

        var result = await clients.GenerateCustomInfoImageAsync(
            title: "Stairwell A Emergency Notice",
            subtitle: "Amber Park Residence • STACK-A1",
            infoLines: infoLines,
            badgeText: "muse-image-1.0",
            qrUrl: "https://commonground.local/notice",
            uiLocale: "pl");

        Assert.Equal("muse-image-1.0", result.ModelName);
        Assert.Equal("640x400 PNG", result.Resolution);
        Assert.StartsWith("data:image/png;base64,", result.ImageDataUrl, StringComparison.Ordinal);
        Assert.Equal(3, result.RenderedInfoLines.Count);
        Assert.Contains("Shutoff Valve V-A1-01 verified", result.RenderedInfoLines);
    }

    [Fact]
    public void LocalizationDictionaries_ContainAllRequiredTranslatedKeysAcrossAllFiveLocales()
    {
        var requiredKeys = new[]
        {
            "action.syncRegistry",
            "action.acquireLegal",
            "msg.legalSynced",
            "msg.registryFailed",
            "page.unitsLead",
            "unit.metersTitle",
            "admin.assistant.apply",
            "admin.assistant.harvest",
            "admin.assistant.lead",
            "admin.assistant.offlineFallback",
            "admin.assistant.stepContact",
            "admin.assistant.stepMerge",
            "admin.assistant.stepParse",
            "admin.assistant.stepReady",
            "admin.assistant.stepSanitize",
            "admin.assistant.stepSearch",
            "admin.assistant.stepTitle",
            "copilot.grounded",
            "assistant.grounded",
            "assistant.synthesis",
            "mic.startRecording",
            "mic.stopAndTranscribe",
            "image.studioTitle",
            "image.generateBtn",
            "image.metaLabel"
        };

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var i18nDir = Path.Combine(repoRoot, "src", "CommonGround.Web", "wwwroot", "i18n");

        foreach (var lang in new[] { "en", "pl", "es", "fr", "de" })
        {
            var path = Path.Combine(i18nDir, $"{lang}.json");
            Assert.True(File.Exists(path), $"Missing locale file: {path}");
            var json = File.ReadAllText(path, Encoding.UTF8);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            Assert.NotNull(dict);

            foreach (var key in requiredKeys)
            {
                Assert.True(dict!.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val), $"Missing or empty key '{key}' in {lang}.json");
            }

            Assert.DoesNotContain("Synteza Copilot", dict!["copilot.synthesis"], StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Síntesis de Copilot", dict!["copilot.synthesis"], StringComparison.OrdinalIgnoreCase);
        }
    }
}
