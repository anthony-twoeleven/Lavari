using Lavari.Application.Abstractions;
using Lavari.Application.Formatting;
using Lavari.Application.Locking;
using Lavari.Application.Security;
using Lavari.Application.Services;
using Lavari.Domain.Errors;
using Lavari.Infrastructure.Caching;
using Lavari.Infrastructure.Documents;
using Lavari.Infrastructure.Integrations;
using Lavari.Infrastructure.Locking;
using Lavari.Infrastructure.Media;
using Lavari.Infrastructure.Persistence;
using Lavari.Infrastructure.Scheduling;
using Lavari.Infrastructure.Security;
using Lavari.Infrastructure.Services;
using Lavari.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TickerQ.DependencyInjection;

namespace Lavari.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddLavariInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool enableTickerQ = true)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IIdGenerator, TimeOrderedIdGenerator>();
        services.AddSingleton<IFormattingService, FormattingService>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<IImageProcessingService, SkiaImageProcessingService>();
        services.AddSingleton<IWordTemplateService, OpenXmlWordTemplateService>();
        services.AddSingleton<IPdfRenderer, SkiaPdfRenderer>();

        services.Configure<AppCacheOptions>(configuration.GetSection("Lavari:Cache"));
        services.Configure<LavariStorageOptions>(configuration.GetSection("Lavari"));
        services.Configure<LavariIntegrationsOptions>(configuration.GetSection("Lavari:Integrations"));

        services.AddSingleton<IAppCacheService, AppCacheService>();
        services.AddSingleton<IPrivateFileStore, LocalPrivateFileStore>();
        services.AddSingleton<GeminiApiClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LavariIntegrationsOptions>>().Value.Gemini;
            if (!options.Enabled)
                throw new ExternalIntegrationServiceException(
                    "The AI provider is disabled.",
                    possibleCause: "Ask an administrator to configure the integration.");
            if (!GeminiApiClient.IsValidApiKey(options.ApiKey))
                throw new ExternalIntegrationServiceException(
                    "The AI provider is not configured.",
                    possibleCause: "Ask an administrator to check the integration credentials.");
            return new GeminiApiClient(options.ApiKey);
        });
        services.TryAddSingleton<IGenerativeTextProvider>(sp =>
            new GeminiTextProvider(sp.GetRequiredService<GeminiApiClient>()));
        services.TryAddSingleton<IGenerativeImageProvider>(sp =>
            new GeminiImageProvider(sp.GetRequiredService<GeminiApiClient>()));
        services.AddSingleton<ISupplierPageFetcher, PublicSupplierPageFetcher>();

        services.AddDbContext<LavariDbContext>((serviceProvider, options) =>
        {
            var currentConfiguration = serviceProvider.GetRequiredService<IConfiguration>();
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = ResolveDatabasePath(currentConfiguration["Lavari:DatabasePath"]),
                ForeignKeys = true,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString();
            options.UseSqlite(connectionString, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(LavariDbContext).Assembly.FullName);
            });
        });

        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<DatabaseSeeder>();
        services.AddScoped<IEditLockService, EditLockService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountAppearanceService, AccountAppearanceService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IClientAndJobService, ClientAndJobService>();
        services.AddScoped<IJobDateService, JobDateService>();
        services.AddScoped<ITimeTrackingService, TimeTrackingService>();
        services.AddScoped<IProductAndLineItemService, ProductAndLineItemService>();
        services.AddScoped<IImageLibraryService, ImageLibraryService>();
        services.AddScoped<IMoodBoardService, MoodBoardService>();
        services.AddScoped<IFinancialDocumentService, FinancialDocumentService>();
        services.AddScoped<IForecastingService, ForecastingService>();
        services.AddScoped<IIntegrationService, IntegrationService>();
        services.AddScoped<SupplierAiAuthorization>();
        services.AddScoped<SupplierAiSpendReservations>();

        if (enableTickerQ)
        {
            services.AddTickerQ();
            services.AddScoped<IBackgroundJobScheduler, TickerQSchedulerAdapter>();
            services.AddScoped<ScheduledMaintenanceJobs>();
        }
        else
        {
            services.AddScoped<IBackgroundJobScheduler, NullBackgroundJobScheduler>();
        }

        return services;
    }

    public static string ResolveDatabasePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (configuredPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return ":memory:";
            }
            var full = Path.GetFullPath(configuredPath.Trim());
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return full;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var baseDir = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lavari")
            : Path.Combine(localAppData, "Lavari");
        var dbDir = Path.Combine(baseDir, "Data");
        Directory.CreateDirectory(dbDir);
        return Path.Combine(dbDir, "lavari.db");
    }
}
