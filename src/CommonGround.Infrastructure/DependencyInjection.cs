using CommonGround.Application.Interfaces;
using CommonGround.Application.Persistence;
using CommonGround.Application.Services;
using CommonGround.Infrastructure.Documents;
using CommonGround.Infrastructure.MetaModels;
using CommonGround.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CommonGround.Infrastructure;

public sealed class AppDbContextFactoryAdapter(IDbContextFactory<AppDbContext> inner) : IAppDbContextFactory
{
    public async Task<IAppDbContext> CreateDbContextAsync(CancellationToken ct = default)
    {
        return await inner.CreateDbContextAsync(ct);
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddCommonGroundInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("CommonGround")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=CommonGroundV3;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

        services.AddDbContextFactory<AppDbContext>(options =>
        {
            try
            {
                options.UseSqlServer(connectionString, sql =>
                {
                    sql.CommandTimeout(120);
                    sql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                });
            }
            catch
            {
                options.UseInMemoryDatabase("CommonGroundFallback");
            }
        });
        services.AddSingleton<IAppDbContextFactory, AppDbContextFactoryAdapter>();

        services.Configure<MetaModelOptions>(options =>
        {
            var section = configuration.GetSection(MetaModelOptions.SectionName);
            section.Bind(options);
            var metaKey = configuration["MetaModels:ApiKey"]
                ?? configuration["META_KEY"]
                ?? Environment.GetEnvironmentVariable("META_KEY")
                ?? Environment.GetEnvironmentVariable("MODEL_API_KEY")
                ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(metaKey))
            {
                options.ApiKey = metaKey.Trim().Trim('"').Trim('\'');
            }
            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                options.BaseUrl = "https://api.meta.ai/v1";
            }
            options.SparkModel = "muse-spark-1.3-contributor";
            options.WonderModel = "muse-spark-1.3-contributor";
            options.VoiceModel = "muse-voice-transcribe-1.0";
            options.SamModel = "sam-3.1";
            options.ImageModel = "muse-image-1.0";
            options.UseFallbackWhenNoKey = false;
            options.UseSearchGrounding = true;
        });
        services.AddHttpClient("MetaModels").AddStandardResilienceHandler();
        services.AddSingleton<SamOverlayRenderer>();
        services.AddSingleton<MetaModelClients>();
        services.AddSingleton<IMuseSparkClient>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IMuseWonderClient>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IMuseVoiceTranscriber>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<ISam31Client>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IMuseImageClient>(sp => sp.GetRequiredService<MetaModelClients>());
        services.AddSingleton<IDocumentExporter, DocumentExporter>();
        services.AddScoped<CommonGroundWorkspaceService>();
        return services;
    }

    public static async Task EnsureCommonGroundDatabaseCreatedAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.EnsureCreatedAsync(ct);
        if (db.Database.IsSqlServer())
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'InvitationTokenHash' AND Object_ID = Object_ID(N'[WorkspaceInvitations]')) ALTER TABLE [WorkspaceInvitations] ADD [InvitationTokenHash] nvarchar(max) NOT NULL DEFAULT N'';",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE Name = N'ExpiresAtUtc' AND Object_ID = Object_ID(N'[WorkspaceInvitations]')) ALTER TABLE [WorkspaceInvitations] ADD [ExpiresAtUtc] datetime2 NOT NULL DEFAULT '2000-01-01T00:00:00';",
                    ct);
                await db.Database.ExecuteSqlRawAsync(
                    "UPDATE [WorkspaceInvitations] SET [ExpiresAtUtc] = DATEADD(day, 7, [CreatedAtUtc]) WHERE [ExpiresAtUtc] < '2001-01-01';",
                    ct);
            }
            catch
            {
            }
        }
    }
}
