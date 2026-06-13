# DataEngine — Part 2: Complete Orchestration & Wiring
## Principal Architect Implementation Document

> **Prerequisite:** Part 1 (Domain, Interfaces, DTOs, Compiler, Guards, Cache, Executors) is complete.
> **This document:** Every missing piece to make DataEngine fully runnable end-to-end.

---

## Table of Contents

1. [What Part 2 Covers](#1-what-part-2-covers)
2. [Missing Exception Types](#2-missing-exception-types)
3. [Connection Factory & Multi-Database Router](#3-connection-factory--multi-database-router)
4. [Null Object Implementations](#4-null-object-implementations)
5. [Bulk Write Executor](#5-bulk-write-executor)
6. [Schema Refresh Background Service](#6-schema-refresh-background-service)
7. [Optional Tables Detector (Runtime)](#7-optional-tables-detector-runtime)
8. [Query Orchestrator](#8-query-orchestrator)
9. [Write Orchestrator — Full Implementation](#9-write-orchestrator--full-implementation)
10. [Procedure Orchestrator — Full Implementation](#10-procedure-orchestrator--full-implementation)
11. [DataEngineService — The Main Entry Point](#11-dataengineservice--the-main-entry-point)
12. [DataEngineBuilder — Fluent Configuration](#12-dataenginebuilder--fluent-configuration)
13. [Complete DI Registration — Final Wiring](#13-complete-di-registration--final-wiring)
14. [DataTypeMapper Utility](#14-datatypemapper-utility)
15. [Transaction Result & Mutation Result Internal Models](#15-transaction-result--mutation-result-internal-models)
16. [Field Mapper Provider (Full)](#16-field-mapper-provider-full)
17. [Audit Writer (Full)](#17-audit-writer-full)
18. [Saved Query Definition Support](#18-saved-query-definition-support)
19. [Sample Consumer API — End-to-End Walkthrough](#19-sample-consumer-api--end-to-end-walkthrough)
20. [Complete File Manifest](#20-complete-file-manifest)
21. [Wiring Verification Checklist](#21-wiring-verification-checklist)

---

## 1. What Part 2 Covers

Part 1 gave you the skeleton: domain models, interfaces, DTOs, the query compiler, schema cache, and the two executors. What it left incomplete:

| Gap | Covered In |
|-----|-----------|
| `DataEngineException`, `SchemaValidationException`, `SecurityViolationException`, `TableNotFoundException` — referenced but not shown | Section 2 |
| `MySqlConnectionFactory` and `MultiDatabaseRouter` — referenced, never implemented | Section 3 |
| `NullFieldMapperProvider`, `NullAuditWriter` — referenced in DI, never implemented | Section 4 |
| `BulkWriteExecutor` — folder existed, no code | Section 5 |
| `SchemaRefreshService` (IHostedService) — registered but not implemented | Section 6 |
| `OptionalTablesDetector` — needed for runtime feature gating | Section 7 |
| `QueryOrchestrator` — mentioned in DI, never fully implemented | Section 8 |
| `WriteOrchestrator` — only a partial sketch in Part 1 | Section 9 |
| `ProcedureOrchestrator` — only a partial sketch in Part 1 | Section 10 |
| `DataEngineService` — the `IDataEngine` implementation — completely absent | Section 11 |
| `DataEngineBuilder` — referenced in folder structure, never built | Section 12 |
| `AddDataEngine()` final wiring — partial in Part 1 | Section 13 |
| `DataTypeMapper` — referenced in `ColumnMetadata.ResolvedDbType`, never built | Section 14 |
| `TransactionResult`, `MutationResult`, `ProcedureResult` — internal models used by executors | Section 15 |
| `FieldMapperProvider` (real impl using `de_field_mappings`) | Section 16 |
| `TableAuditWriter` (real impl using `de_transaction_audit`) | Section 17 |
| `SavedQueryDefinitionProvider` (real impl using `de_query_definitions`) | Section 18 |
| Consumer API sample — how all of it looks in use | Section 19 |

---

## 2. Missing Exception Types

These are referenced throughout the codebase. They must exist before anything compiles.

```csharp
// DataEngine.Core/Exceptions/DataEngineException.cs
namespace DataEngine.Core.Exceptions;

/// <summary>
/// Base exception for all DataEngine operational errors.
/// Consumers can catch this to handle any DataEngine failure uniformly.
/// </summary>
public class DataEngineException : Exception
{
    public DataEngineException(string message)
        : base(message) { }

    public DataEngineException(string message, Exception inner)
        : base(message, inner) { }
}
```

```csharp
// DataEngine.Core/Exceptions/SchemaValidationException.cs
namespace DataEngine.Core.Exceptions;

/// <summary>
/// Thrown when a column or table reference fails schema validation.
/// e.g., column doesn't exist, column is not writable.
/// </summary>
public sealed class SchemaValidationException : DataEngineException
{
    public string? ColumnName { get; }
    public string? TableName { get; }

    public SchemaValidationException(string message)
        : base(message) { }

    public SchemaValidationException(string columnName, string tableName)
        : base($"Column '{columnName}' does not exist in table '{tableName}'.")
    {
        ColumnName = columnName;
        TableName = tableName;
    }
}
```

```csharp
// DataEngine.Core/Exceptions/SecurityViolationException.cs
namespace DataEngine.Core.Exceptions;

/// <summary>
/// Thrown when a request violates DataEngine's safety rules.
/// e.g., access to system databases, invalid identifier characters.
/// This is a hard stop — never catch and continue.
/// </summary>
public sealed class SecurityViolationException : DataEngineException
{
    public SecurityViolationException(string message)
        : base(message) { }
}
```

```csharp
// DataEngine.Core/Exceptions/TableNotFoundException.cs
namespace DataEngine.Core.Exceptions;

/// <summary>
/// Thrown when a table name is not found in the schema cache.
/// Indicates either a typo in the request or a schema that hasn't been loaded.
/// </summary>
public sealed class TableNotFoundException : DataEngineException
{
    public string TableName { get; }
    public string DatabaseName { get; }

    public TableNotFoundException(string tableName, string databaseName)
        : base($"Table '{tableName}' was not found in database '{databaseName}'. " +
               $"Verify the table exists and the schema cache is current.")
    {
        TableName = tableName;
        DatabaseName = databaseName;
    }
}
```

```csharp
// DataEngine.Core/Exceptions/ProcedureNotFoundException.cs
namespace DataEngine.Core.Exceptions;

/// <summary>
/// Thrown when a stored procedure cannot be found or accessed.
/// </summary>
public sealed class ProcedureNotFoundException : DataEngineException
{
    public string ProcedureName { get; }

    public ProcedureNotFoundException(string procedureName, string databaseName)
        : base($"Stored procedure '{procedureName}' was not found in database '{databaseName}'.")
    {
        ProcedureName = procedureName;
    }
}
```

```csharp
// DataEngine.Core/Exceptions/ConnectionException.cs
namespace DataEngine.Core.Exceptions;

/// <summary>
/// Thrown when a database connection cannot be established or
/// a named database alias is not registered.
/// </summary>
public sealed class ConnectionException : DataEngineException
{
    public ConnectionException(string message)
        : base(message) { }

    public ConnectionException(string message, Exception inner)
        : base(message, inner) { }
}
```

---

## 3. Connection Factory & Multi-Database Router

### 3.1 MySqlConnectionFactory

```csharp
// DataEngine.Infrastructure/Connection/MySqlConnectionFactory.cs
namespace DataEngine.Infrastructure.Connection;

/// <summary>
/// Creates MySqlConnection instances for the default database
/// and any registered additional databases.
///
/// Design decision: connections are created on demand, not pooled here.
/// MySqlConnector manages the underlying connection pool via the connection string.
/// Never cache open connections — always create, use, dispose.
/// </summary>
internal sealed class MySqlConnectionFactory : IConnectionFactory
{
    // Key: alias (or "default"), Value: resolved connection string
    private readonly Dictionary<string, string> _connectionStrings;
    private readonly string _defaultDatabase;

    public MySqlConnectionFactory(
        IConfiguration configuration,
        DataEngineOptions options)
    {
        _connectionStrings = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        // Resolve default connection string
        var defaultCs = configuration.GetConnectionString(
            options.DefaultConnectionStringName);

        if (string.IsNullOrWhiteSpace(defaultCs))
            throw new ConnectionException(
                $"Connection string '{options.DefaultConnectionStringName}' not found " +
                $"in configuration. Add ConnectionStrings:{options.DefaultConnectionStringName} " +
                $"to your appsettings.json.");

        _connectionStrings["default"] = defaultCs;
        _defaultDatabase = ExtractDatabaseName(defaultCs);

        // Register additional named databases
        foreach (var (alias, csName) in options.AdditionalDatabases)
        {
            var cs = configuration.GetConnectionString(csName);
            if (string.IsNullOrWhiteSpace(cs))
                throw new ConnectionException(
                    $"Additional database '{alias}' references connection string '{csName}' " +
                    $"which was not found in configuration.");

            _connectionStrings[alias] = cs;
        }
    }

    public MySqlConnection CreateConnection(string? database = null)
    {
        var cs = GetConnectionString(database);
        return new MySqlConnection(cs);
    }

    public string GetConnectionString(string? database = null)
    {
        if (string.IsNullOrWhiteSpace(database) ||
            database.Equals(_defaultDatabase, StringComparison.OrdinalIgnoreCase))
            return _connectionStrings["default"];

        // Try alias match first, then database name match
        if (_connectionStrings.TryGetValue(database, out var cs))
            return cs;

        // Search by database name in connection strings
        foreach (var (_, connStr) in _connectionStrings)
        {
            if (ExtractDatabaseName(connStr)
                    .Equals(database, StringComparison.OrdinalIgnoreCase))
                return connStr;
        }

        throw new ConnectionException(
            $"No connection string found for database '{database}'. " +
            $"Registered aliases: {string.Join(", ", _connectionStrings.Keys)}. " +
            $"Register additional databases via DataEngineOptions.AdditionalDatabases.");
    }

    public IReadOnlyList<string> GetRegisteredDatabases()
    {
        return _connectionStrings.Keys.ToList().AsReadOnly();
    }

    public string GetDefaultDatabase() => _defaultDatabase;

    private static string ExtractDatabaseName(string connectionString)
    {
        // Parse Database= or Initial Catalog= from connection string
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return builder.Database
            ?? throw new ConnectionException(
                "Connection string must contain a 'Database' parameter.");
    }
}
```

### 3.2 Multi-Database Router

```csharp
// DataEngine.Infrastructure/Connection/MultiDatabaseRouter.cs
namespace DataEngine.Infrastructure.Connection;

/// <summary>
/// Routes requests to the correct database connection.
/// Resolves the effective database name from a request
/// (explicit Database field → default database fallback).
///
/// This is a thin coordination layer — it does NOT open connections,
/// it only resolves which connection string to use.
/// </summary>
internal sealed class MultiDatabaseRouter
{
    private readonly MySqlConnectionFactory _factory;

    public MultiDatabaseRouter(MySqlConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Resolve the effective database name for a request.
    /// If the request specifies a database, validate it's registered.
    /// Otherwise, return the default database name.
    /// </summary>
    public string ResolveDatabase(string? requestedDatabase)
    {
        if (string.IsNullOrWhiteSpace(requestedDatabase))
            return _factory.GetDefaultDatabase();

        // Validate it's a known database — GetConnectionString throws if not
        _ = _factory.GetConnectionString(requestedDatabase);
        return requestedDatabase;
    }

    public string ResolveConnectionString(string? requestedDatabase) =>
        _factory.GetConnectionString(ResolveDatabase(requestedDatabase));
}
```

---

## 4. Null Object Implementations

The Null Object pattern means optional features have zero-cost no-op implementations. No `if (auditWriter != null)` scattered everywhere — just register the null version when disabled.

### 4.1 NullAuditWriter

```csharp
// DataEngine.Infrastructure/Audit/NullAuditWriter.cs
namespace DataEngine.Infrastructure.Audit;

/// <summary>
/// No-op audit writer. Registered when EnableAudit = false.
/// Zero allocations, zero overhead.
/// </summary>
internal sealed class NullAuditWriter : IAuditWriter
{
    public bool IsEnabled => false;

    public ValueTask WriteAsync(AuditEntry entry, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
```

### 4.2 NullFieldMapperProvider

```csharp
// DataEngine.Infrastructure/FieldMapping/NullFieldMapperProvider.cs
namespace DataEngine.Infrastructure.FieldMapping;

/// <summary>
/// No-op field mapper. Registered when EnableFieldMapping = false.
/// All resolution calls return null (use original name).
/// </summary>
internal sealed class NullFieldMapperProvider : IFieldMapperProvider
{
    public bool IsAvailable => false;

    public ValueTask<string?> ResolveColumnAsync(
        string tableName,
        string alias,
        CancellationToken ct = default)
        => ValueTask.FromResult<string?>(null);

    public ValueTask<string?> ResolveAliasAsync(
        string tableName,
        string columnName,
        CancellationToken ct = default)
        => ValueTask.FromResult<string?>(null);
}
```

### 4.3 NullSavedQueryProvider

```csharp
// DataEngine.Infrastructure/Query/NullSavedQueryProvider.cs
namespace DataEngine.Infrastructure.Query;

/// <summary>
/// No-op saved query provider. Registered when EnableSavedQueryDefinitions = false.
/// </summary>
internal sealed class NullSavedQueryProvider : ISavedQueryProvider
{
    public bool IsAvailable => false;

    public ValueTask<QueryRequest?> ResolveAsync(
        string definitionKey,
        CancellationToken ct = default)
        => ValueTask.FromResult<QueryRequest?>(null);
}
```

---

## 5. Bulk Write Executor

```csharp
// DataEngine.Infrastructure/Write/BulkWriteExecutor.cs
namespace DataEngine.Infrastructure.Write;

/// <summary>
/// Executes bulk INSERT operations in configurable batches.
///
/// Design: splits large row sets into batches, executes each batch
/// in its own transaction. If one batch fails, previous batches
/// are already committed — this is intentional for large imports.
/// For atomic bulk insert, use TransactionCoordinator instead with
/// a smaller row set.
/// </summary>
internal sealed class BulkWriteExecutor
{
    private readonly ILogger<BulkWriteExecutor> _logger;

    public BulkWriteExecutor(ILogger<BulkWriteExecutor> logger)
    {
        _logger = logger;
    }

    public async Task<BulkMutationResult> ExecuteBulkInsertAsync(
        IReadOnlyList<CompiledQuery> batches,
        string connectionString,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int totalAffected = 0;
        int batchesSucceeded = 0;
        var insertedIds = new List<long>();

        for (int i = 0; i < batches.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await ExecuteBatchAsync(batches[i], connectionString, ct);
                totalAffected += result.AffectedRows;
                batchesSucceeded++;

                _logger.LogDebug(
                    "Bulk insert batch {Batch}/{Total}: {Rows} rows affected",
                    i + 1, batches.Count, result.AffectedRows);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex,
                    "Bulk insert failed at batch {Batch}/{Total}. " +
                    "{Committed} batches committed before failure.",
                    i + 1, batches.Count, batchesSucceeded);

                return new BulkMutationResult
                {
                    Success = false,
                    TotalAffectedRows = totalAffected,
                    BatchesExecuted = batchesSucceeded,
                    TotalBatches = batches.Count,
                    Error = ex.Message,
                    ExecutionTime = sw.Elapsed
                };
            }
        }

        sw.Stop();
        return new BulkMutationResult
        {
            Success = true,
            TotalAffectedRows = totalAffected,
            BatchesExecuted = batchesSucceeded,
            TotalBatches = batches.Count,
            ExecutionTime = sw.Elapsed
        };
    }

    private static async Task<(int AffectedRows, long LastInsertId)> ExecuteBatchAsync(
        CompiledQuery batch,
        string connectionString,
        CancellationToken ct)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var txn = await conn.BeginTransactionAsync(ct);

        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = batch.Sql;
            cmd.CommandType = CommandType.Text;

            foreach (var (name, value) in batch.Parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = $"@{name}";
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }

            int affected = await cmd.ExecuteNonQueryAsync(ct);
            long lastId = cmd.LastInsertedId;

            await txn.CommitAsync(ct);
            return (affected, lastId);
        }
        catch
        {
            await txn.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Split a BulkInsertRequest into multiple CompiledQuery batches.
    /// Each batch contains at most <paramref name="batchSize"/> rows.
    /// </summary>
    public static IReadOnlyList<BulkInsertRequest> SplitIntoBatches(
        BulkInsertRequest request,
        int batchSize)
    {
        if (request.Rows.Count <= batchSize)
            return [request];

        return request.Rows
            .Select((row, idx) => (row, idx))
            .GroupBy(t => t.idx / batchSize)
            .Select(g => request with
            {
                Rows = g.Select(t => t.row).ToList().AsReadOnly()
            })
            .ToList()
            .AsReadOnly();
    }
}
```

---

## 6. Schema Refresh Background Service

```csharp
// DataEngine.Infrastructure/Schema/SchemaRefreshService.cs
namespace DataEngine.Infrastructure.Schema;

/// <summary>
/// Background service that preloads schema on startup and optionally
/// refreshes it on a timer (if SchemaCacheTtl is configured).
///
/// Design: uses IServiceScopeFactory because ISchemaProvider is Scoped,
/// not Singleton. Never inject Scoped services directly into IHostedService.
/// </summary>
internal sealed class SchemaRefreshService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MySqlConnectionFactory _connectionFactory;
    private readonly DataEngineOptions _options;
    private readonly ILogger<SchemaRefreshService> _logger;

    public SchemaRefreshService(
        IServiceScopeFactory scopeFactory,
        MySqlConnectionFactory connectionFactory,
        DataEngineOptions options,
        ILogger<SchemaRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Preload schema for all registered databases at startup
        if (_options.PreloadSchemaOnStartup)
        {
            await PreloadAllAsync(stoppingToken);
        }

        // If TTL is configured, periodically refresh on 80% of TTL interval
        // (refresh before expiry to avoid cache miss on hot path)
        if (_options.SchemaCacheTtl.HasValue)
        {
            var refreshInterval = _options.SchemaCacheTtl.Value * 0.8;

            _logger.LogInformation(
                "Schema auto-refresh enabled. Interval: {Interval}",
                refreshInterval);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(refreshInterval, stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                    await PreloadAllAsync(stoppingToken);
            }
        }
    }

    private async Task PreloadAllAsync(CancellationToken ct)
    {
        var databases = _connectionFactory.GetRegisteredDatabases();

        foreach (var dbAlias in databases)
        {
            try
            {
                // Resolve actual database name from connection string
                var cs = _connectionFactory.GetConnectionString(dbAlias);
                var builder = new MySqlConnectionStringBuilder(cs);
                var dbName = builder.Database;

                if (string.IsNullOrWhiteSpace(dbName)) continue;

                await using var scope = _scopeFactory.CreateAsyncScope();
                var provider = scope.ServiceProvider
                    .GetRequiredService<ISchemaProvider>();

                _logger.LogInformation(
                    "Preloading schema for database '{Database}'...", dbName);

                await provider.LoadSchemaAsync(dbName, ct);

                _logger.LogInformation(
                    "Schema preload complete for '{Database}'.", dbName);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Log but don't crash the host — app should still start
                // even if schema preload fails (lazy loading will handle it)
                _logger.LogError(ex,
                    "Failed to preload schema for database alias '{Alias}'. " +
                    "Schema will be loaded on first request.", dbAlias);
            }
        }
    }
}
```

---

## 7. Optional Tables Detector (Runtime)

```csharp
// DataEngine.Infrastructure/Extensions/OptionalTablesDetector.cs
namespace DataEngine.Infrastructure.Extensions;

/// <summary>
/// Detects which optional DataEngine extension tables exist at runtime.
/// Result is cached for the lifetime of the application.
///
/// Called once during DI setup via a singleton AvailableExtensions instance.
/// </summary>
internal sealed class OptionalTablesDetector
{
    public sealed record AvailableExtensions(
        bool HasFieldMappings,
        bool HasQueryDefinitions,
        bool HasTransactionAudit,
        bool HasProceduresTable)
    {
        public static AvailableExtensions None => new(false, false, false, false);
    }

    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<OptionalTablesDetector> _logger;

    public OptionalTablesDetector(
        IConnectionFactory connectionFactory,
        ILogger<OptionalTablesDetector> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<AvailableExtensions> DetectAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
              AND TABLE_NAME IN (
                'de_field_mappings',
                'de_query_definitions',
                'de_transaction_audit',
                'de_procedures'
              );
            """;

        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);

            var tables = (await conn.QueryAsync<string>(sql))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var result = new AvailableExtensions(
                tables.Contains("de_field_mappings"),
                tables.Contains("de_query_definitions"),
                tables.Contains("de_transaction_audit"),
                tables.Contains("de_procedures"));

            _logger.LogInformation(
                "Optional extension tables detected — " +
                "FieldMappings:{FM} QueryDefs:{QD} Audit:{AU} Procedures:{PR}",
                result.HasFieldMappings,
                result.HasQueryDefinitions,
                result.HasTransactionAudit,
                result.HasProceduresTable);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not detect optional extension tables. " +
                "Defaulting to no optional features.");
            return AvailableExtensions.None;
        }
    }
}
```

---

## 8. Query Orchestrator

```csharp
// DataEngine.Application/QueryOrchestrator.cs
namespace DataEngine.Application;

/// <summary>
/// Orchestrates the full read pipeline:
/// Security → Schema → FieldMapping → Compile → Cache → Execute → Un-map
///
/// This class owns the read side of DataEngine.
/// All filtering, sorting, pagination, joins, projections flow through here.
/// </summary>
internal sealed class QueryOrchestrator
{
    private readonly ISchemaProvider _schemaProvider;
    private readonly IQueryCompiler _compiler;
    private readonly IQueryExecutor _executor;
    private readonly IFieldMapperProvider _fieldMapper;
    private readonly ISavedQueryProvider _savedQueryProvider;
    private readonly MultiDatabaseRouter _router;
    private readonly TableGuard _tableGuard;
    private readonly DataEngineOptions _options;

    // Compiled query cache — keyed by structural shape, not values
    private readonly ConcurrentDictionary<string, CompiledQuery> _queryCache = new();

    private readonly ILogger<QueryOrchestrator> _logger;

    public QueryOrchestrator(
        ISchemaProvider schemaProvider,
        IQueryCompiler compiler,
        IQueryExecutor executor,
        IFieldMapperProvider fieldMapper,
        ISavedQueryProvider savedQueryProvider,
        MultiDatabaseRouter router,
        TableGuard tableGuard,
        DataEngineOptions options,
        ILogger<QueryOrchestrator> logger)
    {
        _schemaProvider = schemaProvider;
        _compiler = compiler;
        _executor = executor;
        _fieldMapper = fieldMapper;
        _savedQueryProvider = savedQueryProvider;
        _router = router;
        _tableGuard = tableGuard;
        _options = options;
        _logger = logger;
    }

    public async Task<QueryResponse> ExecuteAsync(
        QueryRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // [1] Resolve database
        var database = _router.ResolveDatabase(request.Database);

        // [2] Security: reject system databases
        _tableGuard.AssertAllowed(request.Table, database);

        // [3] Schema resolution
        var snapshot = await _schemaProvider.EnsureSnapshotAsync(database, ct);
        var tableSchema = snapshot.GetTable(request.Table);

        // [4] Field mapping: translate incoming aliases to real column names
        var mappedRequest = await ApplyInboundMappingAsync(request, tableSchema, ct);

        // [5] Enforce page size cap
        mappedRequest = EnforcePaginationLimits(mappedRequest);

        // [6] Compile or retrieve from cache
        var cacheKey = QueryCacheKeyBuilder.BuildSelectKey(mappedRequest);
        var compiled = _queryCache.GetOrAdd(cacheKey,
            _ => _compiler.CompileSelect(mappedRequest, tableSchema));

        // IMPORTANT: update parameters on cached query — structure is cached, values are not
        compiled = compiled with { Parameters = ExtractParameters(mappedRequest, tableSchema) };

        // [7] Execute
        var connString = _router.ResolveConnectionString(database);
        var rows = await _executor.ExecuteQueryAsync(compiled, connString, ct);

        // [8] Count (if requested)
        long? totalCount = null;
        if (request.IncludeCount)
        {
            var countQuery = compiled with
            {
                Sql = BuildCountSql(compiled.Sql)
            };
            totalCount = await _executor.ExecuteCountAsync(countQuery, connString, ct);
        }

        // [9] Apply outbound field mapping (column name → alias)
        var mappedRows = await ApplyOutboundMappingAsync(rows, request.Table, ct);

        sw.Stop();

        _logger.LogDebug(
            "Query on {Table}: {RowCount} rows in {Ms}ms",
            request.Table, rows.Count, sw.ElapsedMilliseconds);

        return new QueryResponse
        {
            Data = mappedRows,
            TotalCount = totalCount,
            Pagination = request.Pagination,
            Success = true,
            ExecutionTime = sw.Elapsed
        };
    }

    public async Task<QueryResponse> ExecuteByDefinitionAsync(
        string definitionKey,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        if (!_savedQueryProvider.IsAvailable)
            throw new DataEngineException(
                "Saved query definitions are not enabled. " +
                "Set DataEngineOptions.EnableSavedQueryDefinitions = true and " +
                "ensure the 'de_query_definitions' table exists.");

        var definition = await _savedQueryProvider.ResolveAsync(definitionKey, ct)
            ?? throw new DataEngineException(
                $"Query definition '{definitionKey}' was not found or is inactive.");

        // Merge runtime parameters into the saved filter values
        if (parameters?.Count > 0 && definition.Filters is not null)
        {
            definition = MergeParameters(definition, parameters);
        }

        return await ExecuteAsync(definition, ct);
    }

    // ── Private helpers ────────────────────────────────────────────────

    private async Task<QueryRequest> ApplyInboundMappingAsync(
        QueryRequest request,
        TableMetadata schema,
        CancellationToken ct)
    {
        if (!_fieldMapper.IsAvailable) return request;

        // Map column projections
        List<string>? mappedColumns = null;
        if (request.Columns?.Count > 0)
        {
            mappedColumns = new List<string>(request.Columns.Count);
            foreach (var col in request.Columns)
            {
                var resolved = await _fieldMapper.ResolveColumnAsync(schema.TableName, col, ct);
                mappedColumns.Add(resolved ?? col);
            }
        }

        // Map filter columns
        List<FilterClause>? mappedFilters = null;
        if (request.Filters?.Count > 0)
        {
            mappedFilters = new List<FilterClause>(request.Filters.Count);
            foreach (var f in request.Filters)
            {
                var resolved = await _fieldMapper.ResolveColumnAsync(schema.TableName, f.Column, ct);
                mappedFilters.Add(resolved is not null ? f with { Column = resolved } : f);
            }
        }

        // Map sort columns
        List<SortClause>? mappedSort = null;
        if (request.Sort?.Count > 0)
        {
            mappedSort = new List<SortClause>(request.Sort.Count);
            foreach (var s in request.Sort)
            {
                var resolved = await _fieldMapper.ResolveColumnAsync(schema.TableName, s.Column, ct);
                mappedSort.Add(resolved is not null ? s with { Column = resolved } : s);
            }
        }

        return request with
        {
            Columns = mappedColumns ?? request.Columns,
            Filters = mappedFilters ?? request.Filters,
            Sort = mappedSort ?? request.Sort
        };
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ApplyOutboundMappingAsync(
        IReadOnlyList<IDictionary<string, object?>> rows,
        string tableName,
        CancellationToken ct)
    {
        if (!_fieldMapper.IsAvailable || rows.Count == 0)
            return rows.Select(r => (IReadOnlyDictionary<string, object?>)
                new ReadOnlyDictionary<string, object?>(r)).ToList().AsReadOnly();

        var result = new List<IReadOnlyDictionary<string, object?>>(rows.Count);

        // Build alias map once for all rows (avoid per-row async calls)
        var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in rows[0].Keys)
        {
            var alias = await _fieldMapper.ResolveAliasAsync(tableName, key, ct);
            if (alias is not null && alias != key)
                aliasMap[key] = alias;
        }

        foreach (var row in rows)
        {
            if (aliasMap.Count == 0)
            {
                result.Add(new ReadOnlyDictionary<string, object?>(
                    new Dictionary<string, object?>(row)));
                continue;
            }

            var mapped = new Dictionary<string, object?>(row.Count);
            foreach (var (k, v) in row)
                mapped[aliasMap.TryGetValue(k, out var alias) ? alias : k] = v;

            result.Add(new ReadOnlyDictionary<string, object?>(mapped));
        }

        return result.AsReadOnly();
    }

    private QueryRequest EnforcePaginationLimits(QueryRequest request)
    {
        if (request.Pagination is null) return request;

        var pageSize = Math.Min(request.Pagination.PageSize, _options.MaxPageSize);
        if (pageSize == request.Pagination.PageSize) return request;

        _logger.LogWarning(
            "Requested page size {Requested} exceeds MaxPageSize {Max}. Capping.",
            request.Pagination.PageSize, _options.MaxPageSize);

        return request with
        {
            Pagination = request.Pagination with { PageSize = pageSize }
        };
    }

    // Re-extract parameter values from request for use with cached compiled SQL
    private static IReadOnlyDictionary<string, object?> ExtractParameters(
        QueryRequest request,
        TableMetadata schema)
    {
        // Parameters are always re-extracted from the live request
        // The cached query only provides the SQL template
        // This method mirrors what CompileSelect would produce for parameters only
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        int idx = 0;

        if (request.Filters is not null)
        {
            foreach (var filter in request.Filters)
            {
                switch (filter.Operator)
                {
                    case FilterOperator.IsNull:
                    case FilterOperator.IsNotNull:
                        break;
                    case FilterOperator.In:
                    case FilterOperator.NotIn:
                        foreach (var v in (IEnumerable<object?>)filter.Value!)
                            parameters[$"p_f_{idx++}"] = v;
                        break;
                    case FilterOperator.Between:
                        var between = (object?[])filter.Value!;
                        parameters[$"p_f_{idx++}"] = between[0];
                        parameters[$"p_f_{idx++}"] = between[1];
                        break;
                    default:
                        parameters[$"p_f_{idx++}"] = filter.Value;
                        break;
                }
            }
        }

        if (request.Pagination is not null)
        {
            parameters["_limit"] = request.Pagination.PageSize;
            parameters["_offset"] = request.Pagination.Offset;
        }

        return parameters;
    }

    private static string BuildCountSql(string originalSql)
    {
        // Strip ORDER BY and LIMIT from count query — they're irrelevant and slow
        var orderByIdx = originalSql.LastIndexOf("\nORDER BY", StringComparison.OrdinalIgnoreCase);
        var limitIdx = originalSql.LastIndexOf("\nLIMIT", StringComparison.OrdinalIgnoreCase);

        var truncateAt = new[] { orderByIdx, limitIdx }
            .Where(i => i > 0)
            .DefaultIfEmpty(-1)
            .Min();

        var baseSql = truncateAt > 0
            ? originalSql[..truncateAt]
            : originalSql;

        return $"SELECT COUNT(*) FROM ({baseSql}) AS _de_count";
    }

    private static QueryRequest MergeParameters(
        QueryRequest definition,
        IReadOnlyDictionary<string, object?> runtimeParams)
    {
        if (definition.Filters is null) return definition;

        var merged = definition.Filters
            .Select(f => runtimeParams.TryGetValue(f.Column, out var val)
                ? f with { Value = val }
                : f)
            .ToList()
            .AsReadOnly();

        return definition with { Filters = merged };
    }
}
```

---

## 9. Write Orchestrator — Full Implementation

```csharp
// DataEngine.Application/WriteOrchestrator.cs
namespace DataEngine.Application;

/// <summary>
/// Orchestrates all write operations: INSERT, UPDATE, DELETE, BulkInsert, Transactions.
///
/// Pipeline for every write:
/// Security → Schema → FieldMapping → Validate → Compile → Execute → Audit
/// </summary>
internal sealed class WriteOrchestrator
{
    private readonly ISchemaProvider _schemaProvider;
    private readonly IQueryCompiler _compiler;
    private readonly AdoNetWriteExecutor _writeExecutor;
    private readonly BulkWriteExecutor _bulkExecutor;
    private readonly IFieldMapperProvider _fieldMapper;
    private readonly IAuditWriter _auditWriter;
    private readonly MultiDatabaseRouter _router;
    private readonly TableGuard _tableGuard;
    private readonly ColumnGuard _columnGuard;
    private readonly DataEngineOptions _options;
    private readonly ILogger<WriteOrchestrator> _logger;

    public WriteOrchestrator(
        ISchemaProvider schemaProvider,
        IQueryCompiler compiler,
        AdoNetWriteExecutor writeExecutor,
        BulkWriteExecutor bulkExecutor,
        IFieldMapperProvider fieldMapper,
        IAuditWriter auditWriter,
        MultiDatabaseRouter router,
        TableGuard tableGuard,
        ColumnGuard columnGuard,
        DataEngineOptions options,
        ILogger<WriteOrchestrator> logger)
    {
        _schemaProvider = schemaProvider;
        _compiler = compiler;
        _writeExecutor = writeExecutor;
        _bulkExecutor = bulkExecutor;
        _fieldMapper = fieldMapper;
        _auditWriter = auditWriter;
        _router = router;
        _tableGuard = tableGuard;
        _columnGuard = columnGuard;
        _options = options;
        _logger = logger;
    }

    // ── INSERT ─────────────────────────────────────────────────────────

    public async Task<MutationResponse> InsertAsync(
        InsertRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var database = _router.ResolveDatabase(request.Database);

        _tableGuard.AssertAllowed(request.Table, database);

        var snapshot = await _schemaProvider.EnsureSnapshotAsync(database, ct);
        var schema = snapshot.GetTable(request.Table);

        var mappedValues = await MapInboundValuesAsync(request.Values, schema, ct);
        ValidateWriteValues(mappedValues, schema, allowAutoIncrement: false);

        var mapped = request with { Values = mappedValues, Database = database };
        var compiled = _compiler.CompileInsert(mapped, schema);

        var connString = _router.ResolveConnectionString(database);
        var result = await _writeExecutor.ExecuteInsertAsync(compiled, connString, ct);

        sw.Stop();

        await WriteAuditAsync(new AuditEntry
        {
            Operation = "INSERT",
            TableName = request.Table,
            DatabaseName = database,
            RecordId = result.InsertedId.ToString(),
            NewValues = JsonSerializer.Serialize(request.Values),
            ExecutedBy = request.ExecutedBy,
            CorrelationId = request.CorrelationId
        }, ct);

        _logger.LogDebug("INSERT on {Table}: id={Id} in {Ms}ms",
            request.Table, result.InsertedId, sw.ElapsedMilliseconds);

        return new MutationResponse
        {
            Success = true,
            AffectedRows = result.AffectedRows,
            InsertedId = result.InsertedId,
            ExecutionTime = sw.Elapsed
        };
    }

    // ── UPDATE ─────────────────────────────────────────────────────────

    public async Task<MutationResponse> UpdateAsync(
        UpdateRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Hard guard — UPDATE with no filters is never allowed
        if (request.Filters is null || request.Filters.Count == 0)
            throw new DataEngineException(
                "UPDATE requires at least one filter clause. " +
                "Unfiltered updates are not permitted by DataEngine's safety policy.");

        var database = _router.ResolveDatabase(request.Database);
        _tableGuard.AssertAllowed(request.Table, database);

        var snapshot = await _schemaProvider.EnsureSnapshotAsync(database, ct);
        var schema = snapshot.GetTable(request.Table);

        var mappedValues = await MapInboundValuesAsync(request.Values, schema, ct);
        ValidateWriteValues(mappedValues, schema, allowAutoIncrement: false);

        var mapped = request with { Values = mappedValues, Database = database };
        var compiled = _compiler.CompileUpdate(mapped, schema);

        var connString = _router.ResolveConnectionString(database);
        int affected = await _writeExecutor.ExecuteNonQueryAsync(compiled, connString, ct);

        sw.Stop();

        await WriteAuditAsync(new AuditEntry
        {
            Operation = "UPDATE",
            TableName = request.Table,
            DatabaseName = database,
            NewValues = JsonSerializer.Serialize(request.Values),
            ExecutedBy = request.ExecutedBy,
            CorrelationId = request.CorrelationId
        }, ct);

        _logger.LogDebug("UPDATE on {Table}: {Rows} row(s) in {Ms}ms",
            request.Table, affected, sw.ElapsedMilliseconds);

        return new MutationResponse
        {
            Success = true,
            AffectedRows = affected,
            ExecutionTime = sw.Elapsed
        };
    }

    // ── DELETE ─────────────────────────────────────────────────────────

    public async Task<MutationResponse> DeleteAsync(
        DeleteRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Hard guard — DELETE with no filters is never allowed
        if (request.Filters is null || request.Filters.Count == 0)
            throw new DataEngineException(
                "DELETE requires at least one filter clause. " +
                "Unfiltered deletes are not permitted by DataEngine's safety policy.");

        var database = _router.ResolveDatabase(request.Database);
        _tableGuard.AssertAllowed(request.Table, database);

        var snapshot = await _schemaProvider.EnsureSnapshotAsync(database, ct);
        var schema = snapshot.GetTable(request.Table);

        var compiled = _compiler.CompileDelete(request with { Database = database }, schema);
        var connString = _router.ResolveConnectionString(database);
        int affected = await _writeExecutor.ExecuteNonQueryAsync(compiled, connString, ct);

        sw.Stop();

        await WriteAuditAsync(new AuditEntry
        {
            Operation = "DELETE",
            TableName = request.Table,
            DatabaseName = database,
            ExecutedBy = request.ExecutedBy,
            CorrelationId = request.CorrelationId
        }, ct);

        _logger.LogDebug("DELETE on {Table}: {Rows} row(s) in {Ms}ms",
            request.Table, affected, sw.ElapsedMilliseconds);

        return new MutationResponse
        {
            Success = true,
            AffectedRows = affected,
            ExecutionTime = sw.Elapsed
        };
    }

    // ── BULK INSERT ────────────────────────────────────────────────────

    public async Task<MutationResponse> BulkInsertAsync(
        BulkInsertRequest request,
        CancellationToken ct)
    {
        if (request.Rows.Count == 0)
            return new MutationResponse { Success = true, AffectedRows = 0 };

        var sw = Stopwatch.StartNew();
        var database = _router.ResolveDatabase(request.Database);
        _tableGuard.AssertAllowed(request.Table, database);

        var snapshot = await _schemaProvider.EnsureSnapshotAsync(database, ct);
        var schema = snapshot.GetTable(request.Table);

        // Split into batches
        int batchSize = request.BatchSize > 0
            ? request.BatchSize
            : _options.BulkInsertBatchSize;

        var batches = BulkWriteExecutor.SplitIntoBatches(request, batchSize);
        var connString = _router.ResolveConnectionString(database);

        // Compile each batch
        var compiledBatches = batches
            .Select(b => _compiler.CompileBulkInsert(b with { Database = database }, schema))
            .ToList()
            .AsReadOnly();

        var result = await _bulkExecutor.ExecuteBulkInsertAsync(compiledBatches, connString, ct);

        sw.Stop();

        _logger.LogInformation(
            "BulkInsert on {Table}: {Rows} rows in {Batches} batches, {Ms}ms",
            request.Table, result.TotalAffectedRows,
            result.BatchesExecuted, sw.ElapsedMilliseconds);

        return new MutationResponse
        {
            Success = result.Success,
            AffectedRows = result.TotalAffectedRows,
            Error = result.Error,
            ExecutionTime = sw.Elapsed
        };
    }

    // ── BULK UPDATE ────────────────────────────────────────────────────

    public async Task<MutationResponse> BulkUpdateAsync(
        BulkUpdateRequest request,
        CancellationToken ct)
    {
        if (request.Rows.Count == 0)
            return new MutationResponse { Success = true, AffectedRows = 0 };

        var sw = Stopwatch.StartNew();
        var database = _router.ResolveDatabase(request.Database);
        _tableGuard.AssertAllowed(request.Table, database);

        var snapshot = await _schemaProvider.EnsureSnapshotAsync(database, ct);
        var schema = snapshot.GetTable(request.Table);
        var connString = _router.ResolveConnectionString(database);

        // Compile all update statements and execute in one transaction
        var compiled = request.Rows
            .Select(row => _compiler.CompileUpdate(
                new UpdateRequest
                {
                    Table = request.Table,
                    Database = database,
                    Values = row.Values,
                    Filters = row.Filters
                },
                schema))
            .ToList()
            .AsReadOnly();

        var txResult = await _writeExecutor.ExecuteTransactionAsync(
            compiled, connString, ct: ct);

        sw.Stop();

        return new MutationResponse
        {
            Success = txResult.Success,
            AffectedRows = txResult.TotalAffectedRows,
            Error = txResult.Error,
            ExecutionTime = sw.Elapsed
        };
    }

    // ── TRANSACTION ────────────────────────────────────────────────────

    public async Task<TransactionResponse> ExecuteTransactionAsync(
        TransactionRequest request,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var database = _router.ResolveDatabase(request.Database);
        var connString = _router.ResolveConnectionString(database);

        // Sort by Order property
        var orderedOps = request.Operations.OrderBy(op => op.Order).ToList();

        // Pre-validate ALL operations before any DB call
        var compiledQueries = new List<CompiledQuery>(orderedOps.Count);

        foreach (var op in orderedOps)
        {
            _tableGuard.AssertAllowed(op.Table, database);

            var snapshot = await _schemaProvider.EnsureSnapshotAsync(database, ct);
            var schema = snapshot.GetTable(op.Table);

            CompiledQuery compiled = op.Type switch
            {
                OperationType.Insert when op.Values is not null =>
                    _compiler.CompileInsert(new InsertRequest
                    {
                        Table = op.Table, Database = database, Values = op.Values
                    }, schema),

                OperationType.Update when op.Values is not null && op.Filters?.Count > 0 =>
                    _compiler.CompileUpdate(new UpdateRequest
                    {
                        Table = op.Table, Database = database,
                        Values = op.Values, Filters = op.Filters
                    }, schema),

                OperationType.Delete when op.Filters?.Count > 0 =>
                    _compiler.CompileDelete(new DeleteRequest
                    {
                        Table = op.Table, Database = database, Filters = op.Filters
                    }, schema),

                OperationType.Update =>
                    throw new DataEngineException(
                        $"Transaction UPDATE on '{op.Table}' requires both Values and Filters."),

                OperationType.Delete =>
                    throw new DataEngineException(
                        $"Transaction DELETE on '{op.Table}' requires Filters."),

                _ => throw new DataEngineException(
                    $"Unknown operation type '{op.Type}' for table '{op.Table}'.")
            };

            compiledQueries.Add(compiled);
        }

        // Execute entire set as one DB transaction
        var txResult = await _writeExecutor.ExecuteTransactionAsync(
            compiledQueries, connString, request.IsolationLevel, ct);

        sw.Stop();

        // Audit all operations if transaction succeeded
        if (txResult.Success && _auditWriter.IsEnabled)
        {
            foreach (var op in orderedOps)
            {
                await WriteAuditAsync(new AuditEntry
                {
                    Operation = op.Type.ToString().ToUpperInvariant(),
                    TableName = op.Table,
                    DatabaseName = database,
                    NewValues = op.Values is not null
                        ? JsonSerializer.Serialize(op.Values)
                        : null,
                    ExecutedBy = request.ExecutedBy,
                    CorrelationId = request.CorrelationId
                }, ct);
            }
        }

        _logger.LogInformation(
            "Transaction: {Success} — {Ops} ops in {Ms}ms",
            txResult.Success ? "committed" : "rolled back",
            orderedOps.Count,
            sw.ElapsedMilliseconds);

        return new TransactionResponse
        {
            Success = txResult.Success,
            OperationsExecuted = txResult.Success ? orderedOps.Count : txResult.SucceededCount,
            Error = txResult.Error,
            ExecutionTime = sw.Elapsed
        };
    }

    // ── Private helpers ────────────────────────────────────────────────

    private async Task<IReadOnlyDictionary<string, object?>> MapInboundValuesAsync(
        IReadOnlyDictionary<string, object?> values,
        TableMetadata schema,
        CancellationToken ct)
    {
        if (!_fieldMapper.IsAvailable) return values;

        var mapped = new Dictionary<string, object?>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values)
        {
            var resolved = await _fieldMapper.ResolveColumnAsync(schema.TableName, key, ct);
            mapped[resolved ?? key] = value;
        }
        return mapped;
    }

    private void ValidateWriteValues(
        IReadOnlyDictionary<string, object?> values,
        TableMetadata schema,
        bool allowAutoIncrement)
    {
        foreach (var key in values.Keys)
        {
            if (!allowAutoIncrement)
                _columnGuard.AssertWritable(key, schema);
            else
                _columnGuard.AssertExists(key, schema);
        }
    }

    private async ValueTask WriteAuditAsync(AuditEntry entry, CancellationToken ct)
    {
        if (!_auditWriter.IsEnabled) return;

        try
        {
            await _auditWriter.WriteAsync(entry, ct);
        }
        catch (Exception ex)
        {
            // Audit failures must NEVER fail the primary operation
            _logger.LogError(ex,
                "Audit write failed for {Operation} on {Table}. " +
                "Primary operation was successful.",
                entry.Operation, entry.TableName);
        }
    }
}
```

---

## 10. Procedure Orchestrator — Full Implementation

```csharp
// DataEngine.Application/ProcedureOrchestrator.cs
namespace DataEngine.Application;

/// <summary>
/// Orchestrates stored procedure execution.
/// Routes to Dapper (reads) or ADO.NET (writes) based on ProcedureType.
/// Validates procedure names against injection patterns before executing.
/// </summary>
internal sealed class ProcedureOrchestrator
{
    private readonly IProcedureExecutor _executor;
    private readonly MultiDatabaseRouter _router;
    private readonly DataEngineOptions _options;
    private readonly TableGuard _tableGuard;
    private readonly ILogger<ProcedureOrchestrator> _logger;

    // Characters that are never valid in a stored procedure name
    private static readonly char[] InvalidProcNameChars = [';', ' ', '\'', '"', '`', '-', '\n', '\r'];

    public ProcedureOrchestrator(
        IProcedureExecutor executor,
        MultiDatabaseRouter router,
        DataEngineOptions options,
        TableGuard tableGuard,
        ILogger<ProcedureOrchestrator> logger)
    {
        _executor = executor;
        _router = router;
        _options = options;
        _tableGuard = tableGuard;
        _logger = logger;
    }

    public async Task<ProcedureResponse> ExecuteAsync(
        ProcedureRequest request,
        CancellationToken ct)
    {
        // Feature gate
        if (!_options.AllowStoredProcedures)
            throw new DataEngineException(
                "Stored procedure execution is disabled. " +
                "Set DataEngineOptions.AllowStoredProcedures = true to enable.");

        // Validate procedure name — no injection via proc name
        ValidateProcedureName(request.ProcedureName);

        var database = _router.ResolveDatabase(request.Database);
        var connString = _router.ResolveConnectionString(database);

        // Block access to system-level procedures
        _tableGuard.AssertAllowed(request.ProcedureName, database);

        var sw = Stopwatch.StartNew();

        try
        {
            ProcedureResult result;

            if (request.Type == ProcedureType.Read)
            {
                _logger.LogDebug("Executing READ procedure '{Proc}' on '{Database}'",
                    request.ProcedureName, database);
                result = await _executor.ExecuteReadAsync(request, connString, ct);
            }
            else
            {
                _logger.LogDebug("Executing WRITE procedure '{Proc}' on '{Database}'",
                    request.ProcedureName, database);
                result = await _executor.ExecuteWriteAsync(request, connString, ct);
            }

            sw.Stop();

            _logger.LogInformation(
                "Procedure '{Proc}': {Success} in {Ms}ms",
                request.ProcedureName,
                result.Success ? "success" : "failed",
                sw.ElapsedMilliseconds);

            return new ProcedureResponse
            {
                Success = result.Success,
                ResultSets = result.ResultSets,
                OutputParameters = result.OutputParameters,
                AffectedRows = result.AffectedRows,
                Error = result.Error,
                ExecutionTime = sw.Elapsed
            };
        }
        catch (MySqlException ex) when (ex.Number == 1305)
        {
            // MySQL error 1305 = PROCEDURE does not exist
            sw.Stop();
            throw new ProcedureNotFoundException(request.ProcedureName, database);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Procedure '{Proc}' failed after {Ms}ms",
                request.ProcedureName, sw.ElapsedMilliseconds);

            return new ProcedureResponse
            {
                Success = false,
                Error = ex.Message,
                ExecutionTime = sw.Elapsed
            };
        }
    }

    private static void ValidateProcedureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new SecurityViolationException(
                "Procedure name cannot be empty.");

        if (name.IndexOfAny(InvalidProcNameChars) >= 0)
            throw new SecurityViolationException(
                $"Procedure name '{name}' contains invalid characters. " +
                $"Only alphanumeric characters, underscores, and dots are permitted.");

        if (name.Length > 256)
            throw new SecurityViolationException(
                "Procedure name exceeds maximum length of 256 characters.");
    }
}
```

---

## 11. DataEngineService — The Main Entry Point

This is the class that implements `IDataEngine`. It is the **only** class consumers interact with. It does minimal logic itself — it delegates everything to the appropriate orchestrator. Think of it as a façade router.

```csharp
// DataEngine.Application/DataEngineService.cs
namespace DataEngine.Application;

/// <summary>
/// The primary implementation of IDataEngine.
/// Acts as a clean façade over the three orchestrators.
///
/// Design: this class contains NO business logic. It:
///   1. Routes calls to the correct orchestrator
///   2. Wraps exceptions in consistent DataEngineException
///   3. Provides the public API surface to consumers
///
/// Scoped lifetime: one instance per HTTP request / DI scope.
/// </summary>
internal sealed class DataEngineService : IDataEngine
{
    private readonly QueryOrchestrator _queryOrchestrator;
    private readonly WriteOrchestrator _writeOrchestrator;
    private readonly ProcedureOrchestrator _procedureOrchestrator;
    private readonly ISchemaProvider _schemaProvider;
    private readonly ISchemaCache _schemaCache;
    private readonly MultiDatabaseRouter _router;
    private readonly ILogger<DataEngineService> _logger;

    public DataEngineService(
        QueryOrchestrator queryOrchestrator,
        WriteOrchestrator writeOrchestrator,
        ProcedureOrchestrator procedureOrchestrator,
        ISchemaProvider schemaProvider,
        ISchemaCache schemaCache,
        MultiDatabaseRouter router,
        ILogger<DataEngineService> logger)
    {
        _queryOrchestrator = queryOrchestrator;
        _writeOrchestrator = writeOrchestrator;
        _procedureOrchestrator = procedureOrchestrator;
        _schemaProvider = schemaProvider;
        _schemaCache = schemaCache;
        _router = router;
        _logger = logger;
    }

    // ── Reads ──────────────────────────────────────────────────────────

    public Task<QueryResponse> QueryAsync(
        QueryRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Table);
        return _queryOrchestrator.ExecuteAsync(request, ct);
    }

    public Task<QueryResponse> QueryByDefinitionAsync(
        string definitionKey,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionKey);
        return _queryOrchestrator.ExecuteByDefinitionAsync(definitionKey, parameters, ct);
    }

    // ── Single-row writes ──────────────────────────────────────────────

    public Task<MutationResponse> InsertAsync(
        InsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Table);
        if (request.Values is null || request.Values.Count == 0)
            throw new DataEngineException("InsertRequest.Values cannot be null or empty.");
        return _writeOrchestrator.InsertAsync(request, ct);
    }

    public Task<MutationResponse> UpdateAsync(
        UpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Table);
        return _writeOrchestrator.UpdateAsync(request, ct);
    }

    public Task<MutationResponse> DeleteAsync(
        DeleteRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Table);
        return _writeOrchestrator.DeleteAsync(request, ct);
    }

    // ── Bulk operations ────────────────────────────────────────────────

    public Task<MutationResponse> BulkInsertAsync(
        BulkInsertRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Table);
        return _writeOrchestrator.BulkInsertAsync(request, ct);
    }

    public Task<MutationResponse> BulkUpdateAsync(
        BulkUpdateRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Table);
        return _writeOrchestrator.BulkUpdateAsync(request, ct);
    }

    // ── Transaction scope ──────────────────────────────────────────────

    public Task<TransactionResponse> ExecuteTransactionAsync(
        TransactionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operations is null || request.Operations.Count == 0)
            throw new DataEngineException(
                "TransactionRequest must contain at least one operation.");
        return _writeOrchestrator.ExecuteTransactionAsync(request, ct);
    }

    // ── Stored procedures ──────────────────────────────────────────────

    public Task<ProcedureResponse> ExecuteProcedureAsync(
        ProcedureRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProcedureName);
        return _procedureOrchestrator.ExecuteAsync(request, ct);
    }

    // ── Schema operations ──────────────────────────────────────────────

    public async Task<SchemaResponse> GetSchemaAsync(
        string tableName,
        string? database = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var resolvedDb = _router.ResolveDatabase(database);
        var snapshot = await _schemaProvider.EnsureSnapshotAsync(resolvedDb, ct);

        if (!snapshot.TableExists(tableName))
            return new SchemaResponse
            {
                Success = false,
                Error = $"Table '{tableName}' not found in database '{resolvedDb}'."
            };

        var table = snapshot.GetTable(tableName);

        return new SchemaResponse
        {
            Success = true,
            TableName = table.TableName,
            DatabaseName = table.DatabaseName,
            Columns = table.Columns.Values
                .OrderBy(c => c.OrdinalPosition)
                .Select(c => new ColumnInfo
                {
                    Name = c.ColumnName,
                    DataType = c.DataType,
                    IsNullable = c.IsNullable,
                    IsAutoIncrement = c.IsAutoIncrement,
                    IsPrimaryKey = c.IsPrimaryKey,
                    MaxLength = c.CharacterMaxLength,
                    DefaultValue = c.ColumnDefault
                })
                .ToList()
                .AsReadOnly(),
            PrimaryKeys = table.PrimaryKeys,
            CachedAt = table.CachedAt
        };
    }

    public async Task InvalidateSchemaAsync(
        string? tableName = null,
        string? database = null,
        CancellationToken ct = default)
    {
        var resolvedDb = _router.ResolveDatabase(database);

        if (tableName is not null)
        {
            // Invalidate entire database snapshot — schema cache stores per-database,
            // not per-table. A table change invalidates the whole DB snapshot.
            _logger.LogInformation(
                "Schema invalidation requested for table '{Table}' in '{Database}'. " +
                "Invalidating entire database snapshot.",
                tableName, resolvedDb);
        }

        _schemaCache.Invalidate(resolvedDb);

        // Immediately reload if invalidating for a specific reason
        // (prevents cold-start latency on next request)
        await _schemaProvider.LoadSchemaAsync(resolvedDb, ct);

        _logger.LogInformation(
            "Schema invalidated and reloaded for '{Database}'.", resolvedDb);
    }
}
```

---

## 12. DataEngineBuilder — Fluent Configuration

```csharp
// DataEngine.Extensions.DependencyInjection/DataEngineBuilder.cs
namespace DataEngine.Extensions.DependencyInjection;

/// <summary>
/// Fluent builder returned by AddDataEngine().
/// Allows chaining optional configuration after the core registration.
///
/// Usage:
///   builder.Services
///       .AddDataEngine(config)
///       .WithAudit()
///       .WithFieldMapping()
///       .WithSavedQueries()
///       .WithDatabase("reporting", "ReportingConnection");
/// </summary>
public sealed class DataEngineBuilder
{
    internal IServiceCollection Services { get; }
    internal DataEngineOptions Options { get; }
    internal IConfiguration Configuration { get; }

    internal DataEngineBuilder(
        IServiceCollection services,
        DataEngineOptions options,
        IConfiguration configuration)
    {
        Services = services;
        Options = options;
        Configuration = configuration;
    }

    /// <summary>
    /// Enable audit trail. Requires 'de_transaction_audit' table.
    /// </summary>
    public DataEngineBuilder WithAudit()
    {
        Options.EnableAudit = true;

        // Replace NullAuditWriter with real implementation
        var existing = Services.FirstOrDefault(
            s => s.ServiceType == typeof(IAuditWriter));
        if (existing != null) Services.Remove(existing);

        Services.AddScoped<IAuditWriter, TableAuditWriter>();
        return this;
    }

    /// <summary>
    /// Enable field alias mapping. Requires 'de_field_mappings' table.
    /// </summary>
    public DataEngineBuilder WithFieldMapping()
    {
        Options.EnableFieldMapping = true;

        var existing = Services.FirstOrDefault(
            s => s.ServiceType == typeof(IFieldMapperProvider));
        if (existing != null) Services.Remove(existing);

        Services.AddScoped<IFieldMapperProvider, FieldMapperProvider>();
        return this;
    }

    /// <summary>
    /// Enable saved query definitions. Requires 'de_query_definitions' table.
    /// </summary>
    public DataEngineBuilder WithSavedQueries()
    {
        Options.EnableSavedQueryDefinitions = true;

        var existing = Services.FirstOrDefault(
            s => s.ServiceType == typeof(ISavedQueryProvider));
        if (existing != null) Services.Remove(existing);

        Services.AddScoped<ISavedQueryProvider, SavedQueryDefinitionProvider>();
        return this;
    }

    /// <summary>
    /// Register an additional database by alias.
    /// </summary>
    public DataEngineBuilder WithDatabase(string alias, string connectionStringName)
    {
        Options.AdditionalDatabases[alias] = connectionStringName;
        return this;
    }

    /// <summary>
    /// Override the default page size.
    /// </summary>
    public DataEngineBuilder WithPageSize(int defaultSize, int maxSize)
    {
        Options.DefaultPageSize = defaultSize;
        Options.MaxPageSize = maxSize;
        return this;
    }

    /// <summary>
    /// Override the schema cache TTL.
    /// </summary>
    public DataEngineBuilder WithSchemaCacheTtl(TimeSpan ttl)
    {
        Options.SchemaCacheTtl = ttl;
        return this;
    }

    /// <summary>
    /// Disable schema preloading at startup.
    /// Schema will be loaded lazily on first request.
    /// </summary>
    public DataEngineBuilder WithLazySchemaLoading()
    {
        Options.PreloadSchemaOnStartup = false;
        return this;
    }
}
```

---

## 13. Complete DI Registration — Final Wiring

This is the definitive, complete `AddDataEngine()` with every service binding confirmed.

```csharp
// DataEngine.Extensions.DependencyInjection/DataEngineServiceCollectionExtensions.cs
namespace DataEngine.Extensions.DependencyInjection;

public static class DataEngineServiceCollectionExtensions
{
    /// <summary>
    /// Add DataEngine with default configuration.
    /// Reads ConnectionStrings:DefaultConnection from appsettings.
    /// Returns DataEngineBuilder for optional fluent configuration.
    /// </summary>
    public static DataEngineBuilder AddDataEngine(
        this IServiceCollection services,
        IConfiguration configuration)
        => services.AddDataEngine(configuration, _ => { });

    /// <summary>
    /// Add DataEngine with configuration delegate.
    /// </summary>
    public static DataEngineBuilder AddDataEngine(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DataEngineOptions> configure)
    {
        // ── Build options ──────────────────────────────────────────────
        var options = new DataEngineOptions();
        configuration.GetSection("DataEngine").Bind(options);
        configure(options);
        services.AddSingleton(options);

        // ── Infrastructure dependencies ────────────────────────────────
        services.AddMemoryCache();
        services.AddLogging();

        // ── Connection layer ───────────────────────────────────────────
        // Singleton: connection strings don't change at runtime
        services.AddSingleton<MySqlConnectionFactory>(sp =>
            new MySqlConnectionFactory(
                configuration,
                sp.GetRequiredService<DataEngineOptions>()));

        // IConnectionFactory points to MySqlConnectionFactory
        services.AddSingleton<IConnectionFactory>(sp =>
            sp.GetRequiredService<MySqlConnectionFactory>());

        // Router uses the factory — also Singleton
        services.AddSingleton<MultiDatabaseRouter>();

        // ── Schema layer ───────────────────────────────────────────────
        // Cache is Singleton — shared across all requests
        services.AddSingleton<ISchemaCache, InMemorySchemaCache>();

        // Provider is Scoped — creates connections per scope
        services.AddScoped<ISchemaProvider, MySqlSchemaProvider>();

        // ── Security ───────────────────────────────────────────────────
        // Guards are Singleton — stateless, thread-safe
        services.AddSingleton<TableGuard>();
        services.AddSingleton<ColumnGuard>();

        // ── Query layer ────────────────────────────────────────────────
        // Compiler is Singleton — pure functions, no state
        services.AddSingleton<IQueryCompiler, MySqlQueryCompiler>();

        // Executor is Scoped — creates connections per request
        services.AddScoped<IQueryExecutor, DapperQueryExecutor>();

        // ── Write layer ────────────────────────────────────────────────
        services.AddScoped<AdoNetWriteExecutor>();
        services.AddScoped<BulkWriteExecutor>();

        // ── Procedure layer ────────────────────────────────────────────
        services.AddScoped<IProcedureExecutor, MySqlProcedureExecutor>();

        // ── Optional features — Null Object defaults ───────────────────
        // These can be replaced by DataEngineBuilder.With*() calls
        services.AddSingleton<IAuditWriter, NullAuditWriter>();
        services.AddSingleton<IFieldMapperProvider, NullFieldMapperProvider>();
        services.AddSingleton<ISavedQueryProvider, NullSavedQueryProvider>();

        // ── Orchestrators ──────────────────────────────────────────────
        // Scoped: each request gets its own orchestrator chain
        services.AddScoped<QueryOrchestrator>();
        services.AddScoped<WriteOrchestrator>();
        services.AddScoped<ProcedureOrchestrator>();

        // ── Main entry point ───────────────────────────────────────────
        services.AddScoped<IDataEngine, DataEngineService>();

        // ── Background services ────────────────────────────────────────
        if (options.PreloadSchemaOnStartup)
            services.AddHostedService<SchemaRefreshService>();

        // ── Optional tables detector ───────────────────────────────────
        services.AddSingleton<OptionalTablesDetector>();

        return new DataEngineBuilder(services, options, configuration);
    }
}
```

### Complete appsettings.json with all supported keys

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Port=3306;Database=myapp;User=appuser;Password=secret;AllowBatch=true;",
    "ReportingConnection": "Server=localhost;Port=3306;Database=reports;User=appuser;Password=secret;"
  },
  "DataEngine": {
    "DefaultConnectionStringName": "DefaultConnection",
    "SchemaCacheTtl": "00:30:00",
    "PreloadSchemaOnStartup": true,
    "AllowStoredProcedures": true,
    "AllowRawSql": false,
    "EnableAudit": false,
    "EnableFieldMapping": false,
    "EnableSavedQueryDefinitions": false,
    "DefaultCommandTimeoutSeconds": 30,
    "DefaultPageSize": 20,
    "MaxPageSize": 1000,
    "BulkInsertBatchSize": 500
  }
}
```

---

## 14. DataTypeMapper Utility

This resolves `MySqlDbType` from the INFORMATION_SCHEMA `DATA_TYPE` string. Referenced in `ColumnMetadata.ResolvedDbType`.

```csharp
// DataEngine.Infrastructure/Schema/DataTypeMapper.cs
namespace DataEngine.Infrastructure.Schema;

/// <summary>
/// Maps MySQL INFORMATION_SCHEMA DATA_TYPE strings to MySqlDbType enum values.
/// Used when building MySqlParameters with explicit type hints for better performance.
/// </summary>
internal static class DataTypeMapper
{
    private static readonly Dictionary<string, MySqlDbType> TypeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Integer types
            ["tinyint"]   = MySqlDbType.Byte,
            ["smallint"]  = MySqlDbType.Int16,
            ["mediumint"] = MySqlDbType.Int24,
            ["int"]       = MySqlDbType.Int32,
            ["integer"]   = MySqlDbType.Int32,
            ["bigint"]    = MySqlDbType.Int64,

            // Floating point
            ["float"]     = MySqlDbType.Float,
            ["double"]    = MySqlDbType.Double,
            ["decimal"]   = MySqlDbType.Decimal,
            ["numeric"]   = MySqlDbType.Decimal,

            // String types
            ["char"]      = MySqlDbType.String,
            ["varchar"]   = MySqlDbType.VarChar,
            ["tinytext"]  = MySqlDbType.TinyText,
            ["text"]      = MySqlDbType.Text,
            ["mediumtext"]= MySqlDbType.MediumText,
            ["longtext"]  = MySqlDbType.LongText,
            ["enum"]      = MySqlDbType.Enum,
            ["set"]       = MySqlDbType.Set,

            // Binary types
            ["binary"]    = MySqlDbType.Binary,
            ["varbinary"] = MySqlDbType.VarBinary,
            ["tinyblob"]  = MySqlDbType.TinyBlob,
            ["blob"]      = MySqlDbType.Blob,
            ["mediumblob"]= MySqlDbType.MediumBlob,
            ["longblob"]  = MySqlDbType.LongBlob,

            // Date/time types
            ["date"]      = MySqlDbType.Date,
            ["datetime"]  = MySqlDbType.DateTime,
            ["timestamp"] = MySqlDbType.Timestamp,
            ["time"]      = MySqlDbType.Time,
            ["year"]      = MySqlDbType.Year,

            // Other
            ["bit"]       = MySqlDbType.Bit,
            ["json"]      = MySqlDbType.JSON,
            ["geometry"]  = MySqlDbType.Geometry,
        };

    public static MySqlDbType Resolve(string dataType, int? charMaxLength)
    {
        if (TypeMap.TryGetValue(dataType, out var dbType))
            return dbType;

        // Fallback: use VarChar for unknown string-like types
        return charMaxLength.HasValue
            ? MySqlDbType.VarChar
            : MySqlDbType.Text;
    }
}
```

---

## 15. Transaction Result & Mutation Result Internal Models

These are internal DTOs used between the executor layer and the orchestrator layer. They are never exposed publicly.

```csharp
// DataEngine.Infrastructure/Write/MutationResult.cs
namespace DataEngine.Infrastructure.Write;

internal sealed record MutationResult(
    int AffectedRows,
    long InsertedId);
```

```csharp
// DataEngine.Infrastructure/Write/BulkMutationResult.cs
namespace DataEngine.Infrastructure.Write;

internal sealed record BulkMutationResult
{
    public bool Success { get; init; }
    public int TotalAffectedRows { get; init; }
    public int BatchesExecuted { get; init; }
    public int TotalBatches { get; init; }
    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}
```

```csharp
// DataEngine.Infrastructure/Write/TransactionResult.cs
namespace DataEngine.Infrastructure.Write;

internal sealed record TransactionResult
{
    public bool Success { get; init; }
    public int TotalAffectedRows { get; init; }
    public int SucceededCount { get; init; }
    public string? Error { get; init; }

    public static TransactionResult Success(IReadOnlyList<int> perQueryAffectedRows) =>
        new()
        {
            Success = true,
            TotalAffectedRows = perQueryAffectedRows.Sum(),
            SucceededCount = perQueryAffectedRows.Count
        };

    public static TransactionResult Failed(string error, int succeededCount = 0) =>
        new()
        {
            Success = false,
            Error = error,
            SucceededCount = succeededCount
        };
}
```

```csharp
// DataEngine.Infrastructure/Procedure/ProcedureResult.cs
namespace DataEngine.Infrastructure.Procedure;

internal sealed record ProcedureResult
{
    public bool Success { get; init; }
    public IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<string, object?>>>? ResultSets { get; init; }
    public IReadOnlyDictionary<string, object?>? OutputParameters { get; init; }
    public int? AffectedRows { get; init; }
    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}
```

---

## 16. Field Mapper Provider (Full)

```csharp
// DataEngine.Infrastructure/FieldMapping/FieldMapperProvider.cs
namespace DataEngine.Infrastructure.FieldMapping;

/// <summary>
/// Live field mapper backed by the 'de_field_mappings' table.
/// Results are cached per table per application lifetime.
/// Cache is invalidated when schema is invalidated.
///
/// Table structure:
///   table_name  | column_name | alias     | direction
///   orders      | cust_id     | customerId| both
///   orders      | qty         | quantity  | both
/// </summary>
internal sealed class FieldMapperProvider : IFieldMapperProvider
{
    public bool IsAvailable => true;

    // Cache: tableName → (alias → columnName, columnName → alias)
    private readonly ConcurrentDictionary<string, TableMappings> _cache = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<FieldMapperProvider> _logger;

    private const string LoadSql = """
        SELECT column_name, alias, direction
        FROM de_field_mappings
        WHERE table_name = @TableName
          AND direction IN ('in', 'both', 'out');
        """;

    public FieldMapperProvider(
        IConnectionFactory connectionFactory,
        ILogger<FieldMapperProvider> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async ValueTask<string?> ResolveColumnAsync(
        string tableName,
        string alias,
        CancellationToken ct = default)
    {
        var mappings = await GetMappingsAsync(tableName, ct);
        return mappings.AliasToColumn.TryGetValue(alias, out var col) ? col : null;
    }

    public async ValueTask<string?> ResolveAliasAsync(
        string tableName,
        string columnName,
        CancellationToken ct = default)
    {
        var mappings = await GetMappingsAsync(tableName, ct);
        return mappings.ColumnToAlias.TryGetValue(columnName, out var alias) ? alias : null;
    }

    private async ValueTask<TableMappings> GetMappingsAsync(
        string tableName,
        CancellationToken ct)
    {
        if (_cache.TryGetValue(tableName, out var cached))
            return cached;

        var mappings = await LoadFromDbAsync(tableName, ct);
        _cache[tableName] = mappings;
        return mappings;
    }

    private async Task<TableMappings> LoadFromDbAsync(
        string tableName,
        CancellationToken ct)
    {
        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);

            var rows = await conn.QueryAsync<FieldMappingRow>(
                LoadSql, new { TableName = tableName });

            var aliasToColumn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var columnToAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row.Direction is "in" or "both")
                    aliasToColumn[row.Alias] = row.ColumnName;
                if (row.Direction is "out" or "both")
                    columnToAlias[row.ColumnName] = row.Alias;
            }

            _logger.LogDebug(
                "Loaded {Count} field mappings for table '{Table}'",
                aliasToColumn.Count, tableName);

            return new TableMappings(aliasToColumn, columnToAlias);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to load field mappings for '{Table}'. Using no mappings.",
                tableName);
            return TableMappings.Empty;
        }
    }

    private sealed record TableMappings(
        IReadOnlyDictionary<string, string> AliasToColumn,
        IReadOnlyDictionary<string, string> ColumnToAlias)
    {
        public static TableMappings Empty => new(
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }

    private sealed class FieldMappingRow
    {
        public string ColumnName { get; set; } = default!;
        public string Alias { get; set; } = default!;
        public string Direction { get; set; } = default!;
    }
}
```

---

## 17. Audit Writer (Full)

```csharp
// DataEngine.Infrastructure/Audit/TableAuditWriter.cs
namespace DataEngine.Infrastructure.Audit;

/// <summary>
/// Writes audit entries to the 'de_transaction_audit' table.
///
/// Design rules:
/// 1. Fire-and-forget style — audit failures must never surface to the caller.
///    The orchestrator catches exceptions from WriteAsync and logs them.
/// 2. Uses ADO.NET directly — no ORM, minimal allocation.
/// 3. Uses INSERT IGNORE to handle rare duplicate entry on retry.
/// </summary>
internal sealed class TableAuditWriter : IAuditWriter
{
    public bool IsEnabled => true;

    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<TableAuditWriter> _logger;

    private const string InsertSql = """
        INSERT IGNORE INTO de_transaction_audit
            (id, operation, table_name, database_name, record_id,
             old_values, new_values, executed_by, executed_at, correlation_id)
        VALUES
            (@Id, @Operation, @TableName, @DatabaseName, @RecordId,
             @OldValues, @NewValues, @ExecutedBy, @ExecutedAt, @CorrelationId);
        """;

    public TableAuditWriter(
        IConnectionFactory connectionFactory,
        ILogger<TableAuditWriter> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async ValueTask WriteAsync(AuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = InsertSql;
            cmd.CommandType = CommandType.Text;

            AddParam(cmd, "@Id",            entry.Id.ToString("N"));
            AddParam(cmd, "@Operation",     entry.Operation);
            AddParam(cmd, "@TableName",     entry.TableName);
            AddParam(cmd, "@DatabaseName",  entry.DatabaseName);
            AddParam(cmd, "@RecordId",      entry.RecordId);
            AddParam(cmd, "@OldValues",     entry.OldValues);
            AddParam(cmd, "@NewValues",     entry.NewValues);
            AddParam(cmd, "@ExecutedBy",    entry.ExecutedBy);
            AddParam(cmd, "@ExecutedAt",    entry.ExecutedAt.UtcDateTime);
            AddParam(cmd, "@CorrelationId", entry.CorrelationId);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // Log only — audit failure must not propagate
            _logger.LogError(ex,
                "Audit write failed for {Op} on {Table} [{Id}]",
                entry.Operation, entry.TableName, entry.Id);
        }
    }

    private static void AddParam(MySqlCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }
}
```

---

## 18. Saved Query Definition Support

### ISavedQueryProvider Interface

```csharp
// DataEngine.Core/Interfaces/ISavedQueryProvider.cs
namespace DataEngine.Core.Interfaces;

public interface ISavedQueryProvider
{
    bool IsAvailable { get; }

    ValueTask<QueryRequest?> ResolveAsync(
        string definitionKey,
        CancellationToken ct = default);
}
```

### SavedQueryDefinitionProvider

```csharp
// DataEngine.Infrastructure/Query/SavedQueryDefinitionProvider.cs
namespace DataEngine.Infrastructure.Query;

/// <summary>
/// Resolves named query definitions from the 'de_query_definitions' table.
/// Definitions are stored as JSON (serialized QueryRequest) and cached in memory.
///
/// A saved query definition lets consumers execute pre-approved queries by key:
///   engine.QueryByDefinitionAsync("active-npd-projects")
///
/// instead of constructing QueryRequest objects in application code.
/// </summary>
internal sealed class SavedQueryDefinitionProvider : ISavedQueryProvider
{
    public bool IsAvailable => true;

    private readonly ConcurrentDictionary<string, QueryRequest?> _cache = new(
        StringComparer.OrdinalIgnoreCase);

    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<SavedQueryDefinitionProvider> _logger;

    private const string LoadSql = """
        SELECT table_name, query_json
        FROM de_query_definitions
        WHERE definition_key = @Key
          AND is_active = 1
        LIMIT 1;
        """;

    public SavedQueryDefinitionProvider(
        IConnectionFactory connectionFactory,
        ILogger<SavedQueryDefinitionProvider> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async ValueTask<QueryRequest?> ResolveAsync(
        string definitionKey,
        CancellationToken ct = default)
    {
        // Check cache first — definitions don't change often
        if (_cache.TryGetValue(definitionKey, out var cached))
            return cached;

        var definition = await LoadFromDbAsync(definitionKey, ct);
        _cache[definitionKey] = definition;
        return definition;
    }

    private async Task<QueryRequest?> LoadFromDbAsync(
        string definitionKey,
        CancellationToken ct)
    {
        try
        {
            await using var conn = _connectionFactory.CreateConnection();
            await conn.OpenAsync(ct);

            var row = await conn.QueryFirstOrDefaultAsync<DefinitionRow>(
                LoadSql, new { Key = definitionKey });

            if (row is null)
            {
                _logger.LogWarning(
                    "Query definition '{Key}' not found or inactive.", definitionKey);
                return null;
            }

            // Deserialize the stored JSON back to QueryRequest
            // The stored JSON contains all query config except Table (stored separately)
            var partialRequest = JsonSerializer.Deserialize<QueryRequestJson>(row.QueryJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (partialRequest is null)
            {
                _logger.LogError(
                    "Failed to deserialize query definition '{Key}'.", definitionKey);
                return null;
            }

            return new QueryRequest
            {
                Table = row.TableName,
                Columns = partialRequest.Columns,
                Filters = partialRequest.Filters,
                Sort = partialRequest.Sort,
                Pagination = partialRequest.Pagination,
                IncludeCount = partialRequest.IncludeCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error loading query definition '{Key}'.", definitionKey);
            return null;
        }
    }

    // Internal DTO for JSON deserialization — matches the stored query_json schema
    private sealed class QueryRequestJson
    {
        public IReadOnlyList<string>? Columns { get; set; }
        public IReadOnlyList<FilterClause>? Filters { get; set; }
        public IReadOnlyList<SortClause>? Sort { get; set; }
        public PaginationClause? Pagination { get; set; }
        public bool IncludeCount { get; set; }
    }

    private sealed class DefinitionRow
    {
        public string TableName { get; set; } = default!;
        public string QueryJson { get; set; } = default!;
    }
}
```

---

## 19. Sample Consumer API — End-to-End Walkthrough

This sample shows every DataEngine feature in a single, runnable Minimal API. At Janatics this would map directly to your NPD project or budget portal.

```csharp
// Program.cs — Complete consumer app
using DataEngine.Core.Contracts.Requests;
using DataEngine.Core.Domain.Query;
using DataEngine.Core.Interfaces;
using DataEngine.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ── DataEngine setup — 2 lines ─────────────────────────────────────
builder.Services
    .AddDataEngine(builder.Configuration)
    .WithAudit()                              // enable de_transaction_audit
    .WithFieldMapping()                       // enable de_field_mappings
    .WithSavedQueries()                       // enable de_query_definitions
    .WithDatabase("reports", "ReportingDb")   // second database
    .WithPageSize(defaultSize: 20, maxSize: 500);

var app = builder.Build();

// ──────────────────────────────────────────────────────────────────
// READ — List with filter, sort, pagination, count
// ──────────────────────────────────────────────────────────────────
app.MapGet("/api/npd-projects", async (
    IDataEngine engine,
    string? status,
    int page = 1,
    int pageSize = 20,
    CancellationToken ct = default) =>
{
    var filters = new List<FilterClause>();

    if (!string.IsNullOrWhiteSpace(status))
        filters.Add(new FilterClause
        {
            Column = "status",
            Operator = FilterOperator.Equals,
            Value = status
        });

    var response = await engine.QueryAsync(new QueryRequest
    {
        Table = "npd_projects",
        Filters = filters.Count > 0 ? filters : null,
        Sort = [new SortClause { Column = "created_at", Direction = SortDirection.Descending }],
        Pagination = new PaginationClause { Page = page, PageSize = pageSize },
        IncludeCount = true
    }, ct);

    return Results.Ok(new
    {
        response.Data,
        response.TotalCount,
        response.PageCount,
        response.ExecutionTime
    });
});

// ──────────────────────────────────────────────────────────────────
// READ — By saved definition key
// ──────────────────────────────────────────────────────────────────
app.MapGet("/api/npd-projects/active", async (
    IDataEngine engine,
    CancellationToken ct) =>
{
    // 'active-npd-projects' is stored in de_query_definitions
    var response = await engine.QueryByDefinitionAsync(
        "active-npd-projects", ct: ct);
    return Results.Ok(response.Data);
});

// ──────────────────────────────────────────────────────────────────
// CREATE
// ──────────────────────────────────────────────────────────────────
app.MapPost("/api/npd-projects", async (
    IDataEngine engine,
    CreateProjectDto dto,
    CancellationToken ct) =>
{
    var response = await engine.InsertAsync(new InsertRequest
    {
        Table = "npd_projects",
        Values = new Dictionary<string, object?>
        {
            ["project_code"]  = dto.ProjectCode,
            ["project_name"]  = dto.ProjectName,
            ["phase"]         = dto.Phase,
            ["budget_amount"] = dto.BudgetAmount,
            ["status"]        = "active",
            ["created_at"]    = DateTime.UtcNow,
            ["created_by"]    = dto.CreatedBy
        },
        ExecutedBy = dto.CreatedBy,
        CorrelationId = Guid.NewGuid().ToString("N")
    }, ct);

    return response.Success
        ? Results.Created($"/api/npd-projects/{response.InsertedId}", new { response.InsertedId })
        : Results.Problem(response.Error);
});

// ──────────────────────────────────────────────────────────────────
// UPDATE
// ──────────────────────────────────────────────────────────────────
app.MapPut("/api/npd-projects/{id:int}/status", async (
    IDataEngine engine,
    int id,
    UpdateStatusDto dto,
    CancellationToken ct) =>
{
    var response = await engine.UpdateAsync(new UpdateRequest
    {
        Table = "npd_projects",
        Values = new Dictionary<string, object?>
        {
            ["status"]     = dto.Status,
            ["updated_at"] = DateTime.UtcNow
        },
        Filters = [new FilterClause
        {
            Column = "id",
            Operator = FilterOperator.Equals,
            Value = id
        }],
        ExecutedBy = dto.UpdatedBy
    }, ct);

    return response.AffectedRows > 0
        ? Results.NoContent()
        : Results.NotFound();
});

// ──────────────────────────────────────────────────────────────────
// DELETE (soft delete via update, or hard delete)
// ──────────────────────────────────────────────────────────────────
app.MapDelete("/api/npd-projects/{id:int}", async (
    IDataEngine engine,
    int id,
    CancellationToken ct) =>
{
    var response = await engine.DeleteAsync(new DeleteRequest
    {
        Table = "npd_projects",
        Filters = [new FilterClause
        {
            Column = "id",
            Operator = FilterOperator.Equals,
            Value = id
        }],
        ExecutedBy = "api"
    }, ct);

    return response.AffectedRows > 0
        ? Results.NoContent()
        : Results.NotFound();
});

// ──────────────────────────────────────────────────────────────────
// BULK INSERT — Import rows from CSV/Excel upload
// ──────────────────────────────────────────────────────────────────
app.MapPost("/api/npd-projects/bulk", async (
    IDataEngine engine,
    List<CreateProjectDto> dtos,
    CancellationToken ct) =>
{
    var rows = dtos.Select(dto =>
        (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
        {
            ["project_code"]  = dto.ProjectCode,
            ["project_name"]  = dto.ProjectName,
            ["phase"]         = dto.Phase,
            ["budget_amount"] = dto.BudgetAmount,
            ["status"]        = "active",
            ["created_at"]    = DateTime.UtcNow
        })
        .ToList()
        .AsReadOnly();

    var response = await engine.BulkInsertAsync(new BulkInsertRequest
    {
        Table = "npd_projects",
        Rows = rows,
        BatchSize = 200,
        OnConflict = ConflictResolution.Ignore
    }, ct);

    return Results.Ok(new { response.AffectedRows, response.Success });
});

// ──────────────────────────────────────────────────────────────────
// TRANSACTION — Multi-table operation
// ──────────────────────────────────────────────────────────────────
app.MapPost("/api/npd-projects/{id:int}/approve", async (
    IDataEngine engine,
    int id,
    ApproveProjectDto dto,
    CancellationToken ct) =>
{
    var correlationId = Guid.NewGuid().ToString("N");

    var response = await engine.ExecuteTransactionAsync(new TransactionRequest
    {
        Operations =
        [
            // 1. Update project status
            new TransactionOperation
            {
                Order = 1,
                Type = OperationType.Update,
                Table = "npd_projects",
                Values = new Dictionary<string, object?>
                {
                    ["status"] = "approved",
                    ["approved_by"] = dto.ApprovedBy,
                    ["approved_at"] = DateTime.UtcNow
                },
                Filters = [new FilterClause
                {
                    Column = "id",
                    Operator = FilterOperator.Equals,
                    Value = id
                }]
            },
            // 2. Insert approval record
            new TransactionOperation
            {
                Order = 2,
                Type = OperationType.Insert,
                Table = "project_approvals",
                Values = new Dictionary<string, object?>
                {
                    ["project_id"]   = id,
                    ["approved_by"]  = dto.ApprovedBy,
                    ["approved_at"]  = DateTime.UtcNow,
                    ["comments"]     = dto.Comments
                }
            }
        ],
        ExecutedBy = dto.ApprovedBy,
        CorrelationId = correlationId
    }, ct);

    return response.Success
        ? Results.Ok(new { response.OperationsExecuted, correlationId })
        : Results.Problem(response.Error);
});

// ──────────────────────────────────────────────────────────────────
// STORED PROCEDURE
// ──────────────────────────────────────────────────────────────────
app.MapGet("/api/npd-projects/{id:int}/budget-summary", async (
    IDataEngine engine,
    int id,
    CancellationToken ct) =>
{
    var response = await engine.ExecuteProcedureAsync(new ProcedureRequest
    {
        ProcedureName = "sp_get_project_budget_summary",
        Type = ProcedureType.Read,
        Parameters =
        [
            new ProcedureParameter { Name = "p_project_id", Value = id }
        ]
    }, ct);

    return response.Success
        ? Results.Ok(response.ResultSets?[0])
        : Results.Problem(response.Error);
});

// ──────────────────────────────────────────────────────────────────
// SCHEMA INSPECT — useful for dynamic UI (like AG Grid column defs)
// ──────────────────────────────────────────────────────────────────
app.MapGet("/api/schema/{tableName}", async (
    IDataEngine engine,
    string tableName,
    CancellationToken ct) =>
{
    var response = await engine.GetSchemaAsync(tableName, ct: ct);
    return response.Success ? Results.Ok(response) : Results.NotFound(response.Error);
});

// ──────────────────────────────────────────────────────────────────
// SCHEMA INVALIDATION — admin endpoint
// ──────────────────────────────────────────────────────────────────
app.MapPost("/api/admin/schema/refresh", async (
    IDataEngine engine,
    CancellationToken ct) =>
{
    await engine.InvalidateSchemaAsync(ct: ct);
    return Results.Ok(new { message = "Schema cache refreshed." });
});

app.Run();

// DTOs
record CreateProjectDto(
    string ProjectCode,
    string ProjectName,
    string Phase,
    decimal BudgetAmount,
    string CreatedBy);

record UpdateStatusDto(string Status, string UpdatedBy);
record ApproveProjectDto(string ApprovedBy, string? Comments);
```

---

## 20. Complete File Manifest

Every file that must exist for DataEngine to compile and run:

```
DataEngine.Core/
  Domain/
    Schema/
      ColumnMetadata.cs           ← Part 1
      TableMetadata.cs            ← Part 1
      PrimaryKeyMetadata.cs       ← Part 1
      SchemaMetadataSnapshot.cs   ← Part 1
    Query/
      CompiledQuery.cs            ← Part 1
      FilterClause.cs             ← Part 1
      SortClause.cs               ← Part 1 (inside QueryRequest)
      PaginationClause.cs         ← Part 1 (inside QueryRequest)
      ProjectionClause.cs         ← Part 1
      JoinClause.cs               ← Part 1 (inside QueryRequest)
    Execution/
      ExecutionContext.cs         ← (simple record, add as needed)
    Audit/
      AuditEntry.cs               ← Part 1
  Interfaces/
    IDataEngine.cs                ← Part 1
    ISchemaProvider.cs            ← Part 1
    ISchemaCache.cs               ← Part 1
    IQueryExecutor.cs             ← Part 1
    IQueryCompiler.cs             ← Part 1
    ITransactionExecutor.cs       ← Part 1
    IProcedureExecutor.cs         ← Part 1
    IFieldMapperProvider.cs       ← Part 1
    IAuditWriter.cs               ← Part 1
    IConnectionFactory.cs         ← Part 1
    ISavedQueryProvider.cs        ← Part 2 (Section 18)
  Contracts/
    Requests/
      QueryRequest.cs             ← Part 1
      InsertRequest.cs            ← Part 1
      UpdateRequest.cs            ← Part 1
      DeleteRequest.cs            ← Part 1
      BulkInsertRequest.cs        ← Part 1
      BulkUpdateRequest.cs        ← (add: same shape as BulkInsertRequest with Filters per row)
      ProcedureRequest.cs         ← Part 1
      TransactionRequest.cs       ← Part 1
    Responses/
      QueryResponse.cs            ← Part 1
      MutationResponse.cs         ← Part 1
      ProcedureResponse.cs        ← Part 1
      SchemaResponse.cs           ← Part 1
      TransactionResponse.cs      ← (add: Section 9)
  Exceptions/
    DataEngineException.cs        ← Part 2 Section 2
    SchemaValidationException.cs  ← Part 2 Section 2
    SecurityViolationException.cs ← Part 2 Section 2
    TableNotFoundException.cs     ← Part 2 Section 2
    ProcedureNotFoundException.cs ← Part 2 Section 2
    ConnectionException.cs        ← Part 2 Section 2

DataEngine.Infrastructure/
  Schema/
    MySqlSchemaProvider.cs        ← Part 1
    InMemorySchemaCache.cs        ← Part 1
    SchemaRefreshService.cs       ← Part 2 Section 6
    DataTypeMapper.cs             ← Part 2 Section 14
  Query/
    MySqlQueryCompiler.cs         ← Part 1
    DapperQueryExecutor.cs        ← Part 1
    FilterBuilder.cs              ← (extracted helpers, optional)
    QueryCacheKeyBuilder.cs       ← Part 1
    NullSavedQueryProvider.cs     ← Part 2 Section 4
    SavedQueryDefinitionProvider.cs ← Part 2 Section 18
  Write/
    AdoNetWriteExecutor.cs        ← Part 1
    BulkWriteExecutor.cs          ← Part 2 Section 5
    TransactionCoordinator.cs     ← (merged into AdoNetWriteExecutor.ExecuteTransactionAsync)
    MutationResult.cs             ← Part 2 Section 15
    BulkMutationResult.cs         ← Part 2 Section 15
    TransactionResult.cs          ← Part 2 Section 15
  Procedure/
    MySqlProcedureExecutor.cs     ← Part 1
    ProcedureResult.cs            ← Part 2 Section 15
  Connection/
    MySqlConnectionFactory.cs     ← Part 2 Section 3
    MultiDatabaseRouter.cs        ← Part 2 Section 3
  FieldMapping/
    FieldMapperProvider.cs        ← Part 2 Section 16
    NullFieldMapperProvider.cs    ← Part 2 Section 4
  Audit/
    TableAuditWriter.cs           ← Part 2 Section 17
    NullAuditWriter.cs            ← Part 2 Section 4
  Security/
    TableGuard.cs                 ← Part 1
    ColumnGuard.cs                ← Part 1
  Extensions/
    OptionalTablesDetector.cs     ← Part 2 Section 7

DataEngine.Application/
  DataEngineService.cs            ← Part 2 Section 11
  QueryOrchestrator.cs            ← Part 2 Section 8
  WriteOrchestrator.cs            ← Part 2 Section 9
  ProcedureOrchestrator.cs        ← Part 2 Section 10

DataEngine.Extensions.DependencyInjection/
  DataEngineServiceCollectionExtensions.cs ← Part 2 Section 13 (FINAL)
  DataEngineOptions.cs            ← Part 1
  DataEngineBuilder.cs            ← Part 2 Section 12
```

---

## 21. Wiring Verification Checklist

Run through this before marking Part 2 complete. Every item is a compilation/runtime failure if missed.

### Compilation checks

- [ ] All exception types in `DataEngine.Core.Exceptions` match usages across Infrastructure + Application
- [ ] `ISavedQueryProvider` is defined in Core.Interfaces and referenced correctly in DI
- [ ] `TransactionResponse` record exists in Core.Contracts.Responses with `Success`, `OperationsExecuted`, `Error`, `ExecutionTime`
- [ ] `BulkUpdateRequest` record exists with `Table`, `Database`, and `IReadOnlyList<BulkUpdateRow>` where `BulkUpdateRow` has `Values` + `Filters`
- [ ] `SchemaResponse` record has `TableName`, `DatabaseName`, `Columns`, `PrimaryKeys`, `CachedAt`, `Success`, `Error`
- [ ] `ColumnInfo` record exists in Core.Contracts.Responses for SchemaResponse.Columns
- [ ] `MySqlConnectionFactory` is registered as **Singleton** and also registered as `IConnectionFactory`
- [ ] `MultiDatabaseRouter` is registered as **Singleton**
- [ ] `SchemaRefreshService` receives `MySqlConnectionFactory` (concrete), not `IConnectionFactory`

### DI lifetime checks

| Service | Correct Lifetime | Why |
|---------|-----------------|-----|
| `DataEngineOptions` | Singleton | Read-only config |
| `ISchemaCache` | Singleton | Cross-request shared cache |
| `MySqlConnectionFactory` | Singleton | Connection strings immutable |
| `MultiDatabaseRouter` | Singleton | Stateless |
| `TableGuard`, `ColumnGuard` | Singleton | Stateless |
| `IQueryCompiler` | Singleton | Pure functions |
| `ISchemaProvider` | **Scoped** | Creates connections |
| `IQueryExecutor` | **Scoped** | Creates connections |
| `AdoNetWriteExecutor` | **Scoped** | Creates connections |
| `BulkWriteExecutor` | **Scoped** | Creates connections |
| `IProcedureExecutor` | **Scoped** | Creates connections |
| `IAuditWriter` (Null) | Singleton | No-op |
| `IAuditWriter` (Real) | **Scoped** | Creates connections |
| `IFieldMapperProvider` (Null) | Singleton | No-op |
| `IFieldMapperProvider` (Real) | **Scoped** | Creates connections |
| `ISavedQueryProvider` (Null) | Singleton | No-op |
| `ISavedQueryProvider` (Real) | **Scoped** | Creates connections |
| `QueryOrchestrator` | **Scoped** | Holds scoped deps |
| `WriteOrchestrator` | **Scoped** | Holds scoped deps |
| `ProcedureOrchestrator` | **Scoped** | Holds scoped deps |
| `IDataEngine` | **Scoped** | Public API |

### Runtime checks (verify with integration tests)

- [ ] Cold start: schema loaded automatically for default database
- [ ] Unknown table name returns `TableNotFoundException` (not NullRef)
- [ ] System database access (`mysql`, `information_schema`) throws `SecurityViolationException`
- [ ] UPDATE with empty Filters throws `DataEngineException`
- [ ] DELETE with empty Filters throws `DataEngineException`
- [ ] Bulk insert with 1200 rows uses 3 batches of 400 (with BatchSize=400)
- [ ] Transaction with 3 ops rolls back all on second op failure
- [ ] Schema invalidate + reload completes without error
- [ ] Procedure with OUTPUT parameter returns value in `OutputParameters`
- [ ] Second database query routes to correct connection string
- [ ] Audit entry written after successful INSERT (when enabled)
- [ ] Audit failure does not surface to caller

---

*End of DataEngine Architecture — Part 2*
*Parts 1 + 2 together represent the complete design and implementation of the DataEngine framework.*
*Next: write the actual .cs files, wire the .csproj references, and run the integration test suite against a real MySQL container.*
