using DataEngine.Abstractions;
using DataEngine.Cache;
using DataEngine.Logging;
using DataEngine.Repository;
using DataEngine.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DataEngine.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all DataEngine services.
    ///
    /// Single call — unchanged for consumers.
    /// Internally adds the new ITransaction central contract
    /// while keeping IDynamicTransactionProcessor and IDynamicReadEngine
    /// available for existing callers.
    ///
    /// Usage (unchanged):
    ///   services.AddDynamicDataEngine();
    /// </summary>
    public static IServiceCollection AddDynamicDataEngine(this IServiceCollection services)
    {
        // ── Infrastructure ─────────────────────────────────────────────
        services.AddOptions();
        services.AddSingleton<ISchemaCache, InMemorySchemaCache>();
        services.AddSingleton<ApplicationTableMetadataRepository>();
        services.AddSingleton<DataEngineFileLogger>();

        // ── Validators ─────────────────────────────────────────────────
        services.AddSingleton<ITransactionValidator, TransactionValidator>();
        services.AddSingleton<IQueryValidator, QueryValidator>();

        // ── Central transaction engine ─────────────────────────────────
        // DataEngineTransaction implements ITransaction, IDynamicTransactionProcessor,
        // and IDynamicReadEngine. Registered as Scoped — creates one instance per
        // HTTP request/DI scope. All three interfaces resolve to the same instance.
        services.AddScoped<DataEngineTransaction>();
        services.AddScoped<IDynamicEngine>(sp => sp.GetRequiredService<DataEngineTransaction>());

        return services;
    }
}
