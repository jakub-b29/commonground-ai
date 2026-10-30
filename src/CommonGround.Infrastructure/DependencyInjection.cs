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
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=CommonGround;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

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

        services.Configure<MetaModelOptions>(configuration.GetSection(MetaModelOptions.SectionName));
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
        services.AddScoped<DemoSeeder>();
        return services;
    }

    public static async Task EnsureCommonGroundSeededAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);
        try
        {
            await db.Database.EnsureCreatedAsync(ct);
        }
        catch
        {
            // InMemory fallback or LocalDB unavailable: EnsureCreated on the configured provider still applies; seeding proceeds in memory.
        }
        var renderer = scope.ServiceProvider.GetRequiredService<SamOverlayRenderer>();
        var seeder = new DemoSeeder(db, renderer);
        await seeder.SeedAsync(ct);
    }
}
