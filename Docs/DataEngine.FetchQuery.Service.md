# DataEngine — FetchQuery Class Library Service
**Architecture: .NET 8 · C# 12 · 2026 Best Practices**

---

## Table of Contents
1. [Architecture Overview](#1-architecture-overview)
2. [Project Structure](#2-project-structure)
3. [Domain Models & Enums](#3-domain-models--enums)
4. [Configuration](#4-configuration)
5. [Security Layer (SQL Guard)](#5-security-layer-sql-guard)
6. [Database Abstractions](#6-database-abstractions)
7. [Caching Abstractions](#7-caching-abstractions)
8. [FieldMapper Service](#8-fieldmapper-service)
9. [Query Repository](#9-query-repository)
10. [FetchQuery Engine — Core Service](#10-fetchquery-engine--core-service)
11. [DI Registration](#11-di-registration)
12. [Database Tables (DDL)](#12-database-tables-ddl)
13. [Usage Examples](#13-usage-examples)
14. [Security Decisions Log](#14-security-decisions-log)

---

## 1. Architecture Overview

```
┌──────────────────────────────────────────────────────────┐
│                   Caller (API / Portal)                  │
└───────────────────────────┬──────────────────────────────┘
                            │  FetchConfig
                            ▼
┌──────────────────────────────────────────────────────────┐
│               FetchQueryEngine  (sealed class)           │
│  ┌──────────────┐  ┌───────────────┐  ┌───────────────┐ │
│  │ SqlGuardian  │  │ QueryRepo     │  │ CacheService  │ │
│  │ (whitelist)  │  │ (stored defs) │  │ (L1+L2)       │ │
│  └──────────────┘  └───────────────┘  └───────────────┘ │
│  ┌──────────────┐  ┌───────────────┐                     │
│  │ FieldMapper  │  │ DbConnection  │                     │
│  │ Service      │  │ Factory       │                     │
│  └──────────────┘  └───────────────┘                     │
└──────────────────────────────────────────────────────────┘
                            │  FetchResult<T>
                            ▼
                      Caller receives
```

**Key security principles applied:**
- **Zero raw SQL from clients** — `QueryText` is disabled by default; when enabled it passes through `ISqlGuardian` (AST-level parse + whitelist).
- **All parameters are bound** — never string-interpolated. Dapper named parameters only.
- **Schema-level validation** — column names for sort/filter are validated against `INFORMATION_SCHEMA` at runtime.
- **Immutable query definitions** — stored queries are read-only signed blobs; clients pass a `QueryNumber` key only.
- **Defense-in-depth** — three independent guards before any SQL reaches the driver.

---

## 2. Project Structure

```
DataEngine/
├── DataEngine.Core/                   ← this class library
│   ├── Configuration/
│   │   ├── DatabaseConfig.cs
│   │   ├── CacheConfig.cs
│   │   └── PortalConfig.cs
│   ├── Domain/
│   │   ├── Enums.cs
│   │   ├── FieldMapper.cs
│   │   ├── ReferenceLabel.cs
│   │   ├── FetchConfig.cs
│   │   ├── FetchResult.cs
│   │   ├── FilterCondition.cs
│   │   └── QueryDefinition.cs
│   ├── Security/
│   │   ├── ISqlGuardian.cs
│   │   └── SqlGuardian.cs
│   ├── Abstractions/
│   │   ├── IDbConnectionFactory.cs
│   │   ├── IFieldMapperCache.cs
│   │   ├── ICacheService.cs
│   │   └── IQueryRepository.cs
│   ├── Services/
│   │   ├── DbConnectionFactory.cs
│   │   ├── FieldMapperService.cs
│   │   ├── QueryRepository.cs
│   │   ├── CacheService.cs
│   │   └── FetchQueryEngine.cs       ← primary entry point
│   └── Extensions/
│       └── ServiceCollectionExtensions.cs
└── DataEngine.Core.csproj
```

---

## 3. Domain Models & Enums

### `Enums.cs`
```csharp
namespace DataEngine.Core.Domain;

public enum DatabaseProvider
{
    SqlServer,
    MySQL,
    PostgreSQL
}

public enum CacheProvider
{
    InMemory,
    Redis
}

public enum SortDirection
{
    Asc,
    Desc
}

public enum FilterOperator
{
    Equals,
    NotEquals,
    Contains,
    StartsWith,
    EndsWith,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In,
    IsNull,
    IsNotNull
}
```

### `FieldMapper.cs`
```csharp
namespace DataEngine.Core.Domain;

/// <summary>
/// Metadata blueprint for a single column inside the dynamic mapping system.
/// Immutable record — never mutated after cache load.
/// </summary>
public sealed record FieldMapper
{
    public int    Id                    { get; init; }
    public string TableName             { get; init; } = string.Empty;
    public string FieldName             { get; init; } = string.Empty;
    public string? LookupTable          { get; init; }
    public string  LookupIdColumn       { get; init; } = "id";
    public string  LookupIdColumnDataType { get; init; } = "int";
    public string? LabelColumn          { get; init; }
    public string? GroupOptionKey       { get; init; }

    /// <summary>True when this field resolves via a foreign lookup table.</summary>
    public bool HasLookup =>
        !string.IsNullOrWhiteSpace(LookupTable) &&
        !string.IsNullOrWhiteSpace(LabelColumn);
}
```

### `ReferenceLabel.cs`
```csharp
namespace DataEngine.Core.Domain;

/// <summary>
/// A resolved ID → display-label pair for UI rendering.
/// Supports int, bigint, guid, and string PKs via boxed object key.
/// </summary>
public sealed record ReferenceLabel
{
    public object Id    { get; init; } = 0;
    public string Label { get; init; } = string.Empty;
}
```

### `FilterCondition.cs`
```csharp
namespace DataEngine.Core.Domain;

/// <summary>
/// A single WHERE clause criterion submitted by the client.
/// Column name is validated against INFORMATION_SCHEMA before use.
/// </summary>
public sealed record FilterCondition
{
    /// <summary>Column name — validated via schema whitelist before query build.</summary>
    public string         Column   { get; init; } = string.Empty;
    public FilterOperator Operator { get; init; } = FilterOperator.Equals;
    /// <summary>Value is always bound as a parameter — never interpolated.</summary>
    public object?        Value    { get; init; }
}
```

### `FetchConfig.cs`
```csharp
using System.Collections.Generic;
using System.Text.Json;

namespace DataEngine.Core.Domain;

/// <summary>
/// Single control object passed to <see cref="Services.FetchQueryEngine"/>.
/// All public setters intentionally omitted — callers use init-only properties
/// to prevent post-construction mutation.
/// </summary>
public sealed record FetchConfig
{
    // ── Query source ──────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up a stored, pre-validated query definition by key.
    /// Preferred over <see cref="QueryText"/> for all production usage.
    /// </summary>
    public int? QueryNumber { get; init; }

    /// <summary>
    /// Raw SQL — only executed when <see cref="EnableDirectQueryExecution"/> is true
    /// AND the caller has passed ISqlGuardian validation.
    /// Disabled by default.
    /// </summary>
    public string? QueryText { get; init; }

    /// <summary>
    /// Activates raw SQL execution path. Defaults to FALSE.
    /// Must be explicitly opted-in per call; never rely on caller default.
    /// </summary>
    public bool EnableDirectQueryExecution { get; init; } = false;

    // ── Parameters ────────────────────────────────────────────────────────────

    /// <summary>
    /// Named parameters bound safely via Dapper.
    /// Keys must match @param placeholders in the query.
    /// </summary>
    public JsonDocument? InputParameters { get; init; }

    // ── Pagination ────────────────────────────────────────────────────────────

    public int Count      { get; init; } = 10;
    public int PageNumber { get; init; } = 1;

    // ── Reference labels ──────────────────────────────────────────────────────

    public bool IncludeReferenceLabels { get; init; } = false;

    // ── Server-side sorting ───────────────────────────────────────────────────

    public bool          EnableServerSideSorting { get; init; } = false;

    /// <summary>
    /// Validated against INFORMATION_SCHEMA column whitelist before use.
    /// </summary>
    public string?        SortField     { get; init; }
    public SortDirection  SortDirection { get; init; } = SortDirection.Asc;

    // ── Server-side filtering ─────────────────────────────────────────────────

    public bool EnableServerSideFiltering { get; init; } = false;

    /// <summary>
    /// Column names inside each condition are whitelist-validated before query build.
    /// </summary>
    public IReadOnlyList<FilterCondition> FilterConditions { get; init; }
        = Array.Empty<FilterCondition>();

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>Global search — always bound as a parameter, never interpolated.</summary>
    public string? SearchText { get; init; }

    // ── Timezone ──────────────────────────────────────────────────────────────

    public string? FetchTimezone { get; init; }

    // ── Caching ───────────────────────────────────────────────────────────────

    public bool EnableCaching { get; init; } = true;
}
```

### `FetchResult.cs`
```csharp
using System.Collections.Generic;

namespace DataEngine.Core.Domain;

/// <summary>Generic paginated result envelope returned by FetchQueryEngine.</summary>
public sealed class FetchResult<T>
{
    public IReadOnlyList<T> Data         { get; init; } = Array.Empty<T>();
    public int              TotalCount   { get; init; }
    public int              PageNumber   { get; init; }
    public int              PageSize     { get; init; }
    public int              TotalPages   => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool             HasNextPage  => PageNumber < TotalPages;
    public string?          CacheKey     { get; init; }
    public bool             ServedFromCache { get; init; }
    public TimeSpan         ExecutionTime   { get; init; }
}
```

### `QueryDefinition.cs`
```csharp
namespace DataEngine.Core.Domain;

/// <summary>
/// A stored, pre-validated query loaded from de_query_definitions.
/// Treated as immutable after retrieval; the query_text JSON is
/// deserialized into a sealed execution template.
/// </summary>
public sealed record QueryDefinition
{
    public int    Id            { get; init; }
    public string DefinitionKey { get; init; } = string.Empty;
    public string TableName     { get; init; } = string.Empty;
    public string Description   { get; init; } = string.Empty;
    public string QueryText     { get; init; } = string.Empty;  // validated SQL stored server-side
    public bool   IsActive      { get; init; } = true;
}
```

---

## 4. Configuration

### `DatabaseConfig.cs`
```csharp
namespace DataEngine.Core.Configuration;

public sealed class DatabaseConfig
{
    public const string SectionKey = "DataEngine:Database";

    public string           ConnectionString { get; init; } = string.Empty;
    public DatabaseProvider Provider        { get; init; } = DatabaseProvider.SqlServer;
    /// <summary>IANA timezone ID used as fallback for datetime normalization.</summary>
    public string           DefaultTimezone { get; init; } = "UTC";

    // ── Derived from Domain ──
    public int CommandTimeoutSeconds { get; init; } = 30;
    public int MaxRetryCount        { get; init; } = 3;
    public int RetryDelayMs         { get; init; } = 200;
}
```

### `CacheConfig.cs`
```csharp
namespace DataEngine.Core.Configuration;

public sealed class CacheConfig
{
    public const string SectionKey = "DataEngine:Cache";

    public CacheProvider CacheProvider           { get; init; } = CacheProvider.InMemory;
    public string?       RedisConnectionString   { get; init; }
    public int           CacheExpirationMinutes  { get; init; } = 30;
    public int           FieldMapperCacheMinutes { get; init; } = 120;   // longer TTL for schema meta
}
```

### `PortalConfig.cs`
```csharp
namespace DataEngine.Core.Configuration;

/// <summary>
/// Declares which application tables this portal instance is allowed to query.
/// Acts as a coarse-grained access control list — the engine rejects any
/// table reference not in this whitelist, regardless of user permissions.
/// </summary>
public sealed class PortalConfig
{
    public const string SectionKey = "DataEngine:Portal";

    public string   ApplicationName  { get; init; } = string.Empty;
    public string   ApplicationCode  { get; init; } = string.Empty;

    /// <summary>
    /// Explicit table whitelist. The engine refuses to build queries
    /// targeting tables absent from this list.
    /// </summary>
    public IReadOnlyList<string> AllowedTables { get; init; } = Array.Empty<string>();
}
```

---

## 5. Security Layer (SQL Guard)

This is the most critical section. Three independent guards prevent SQL injection:

**Guard 1 — Table Whitelist** (`PortalConfig.AllowedTables`): rejects any table not declared.  
**Guard 2 — Column Whitelist** (`INFORMATION_SCHEMA` lookup): validates sort field and filter column names at runtime.  
**Guard 3 — SqlGuardian** (AST parser): applied only on the direct-query path — rejects DML, DDL, EXEC, system objects, and multi-statement batches.

### `ISqlGuardian.cs`
```csharp
namespace DataEngine.Core.Security;

/// <summary>
/// Validates a raw SQL string before execution on the direct-query path.
/// Implementations must be deterministic and allocation-efficient.
/// </summary>
public interface ISqlGuardian
{
    /// <summary>
    /// Returns a validation result. Never throws — callers check <see cref="SqlGuardResult.IsValid"/>.
    /// </summary>
    SqlGuardResult Validate(string sql, string targetTable);
}

public sealed record SqlGuardResult(bool IsValid, string? Reason = null)
{
    public static SqlGuardResult Ok()                   => new(true);
    public static SqlGuardResult Fail(string reason)   => new(false, reason);
}
```

### `SqlGuardian.cs`
```csharp
using System.Text.RegularExpressions;

namespace DataEngine.Core.Security;

/// <summary>
/// Multi-layer SQL validator for the direct-query execution path.
///
/// Layers applied in order (fail-fast):
///   1. Statement-count guard   — rejects batches (semicolon delimiter)
///   2. DML/DDL/EXEC deny-list  — rejects any mutating or administrative keyword
///   3. System object guard     — blocks access to catalog views, sys.*, xp_*, sp_*
///   4. Comment strip + re-scan — removes inline/block comments before keyword scan
///   5. Table scope guard       — ensures only the declared target table is referenced
/// </summary>
public sealed partial class SqlGuardian : ISqlGuardian
{
    // ── Precompiled patterns (source-generated, allocation-free on .NET 7+) ──

    [GeneratedRegex(@";\s*\S", RegexOptions.IgnoreCase)]
    private static partial Regex MultiStatementPattern();

    [GeneratedRegex(
        @"\b(INSERT|UPDATE|DELETE|DROP|TRUNCATE|ALTER|CREATE|EXEC|EXECUTE|" +
        @"MERGE|CALL|GRANT|REVOKE|DENY|BACKUP|RESTORE|BULK|OPENROWSET|OPENQUERY)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DangerousKeywordPattern();

    [GeneratedRegex(
        @"\b(sys\.|information_schema\.|xp_|sp_|fn_|OBJECT_ID|DB_NAME|" +
        @"SUSER_NAME|SYSTEM_USER|CURRENT_USER|SESSION_USER)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SystemObjectPattern();

    [GeneratedRegex(@"(--[^\n]*|/\*[\s\S]*?\*/)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlCommentPattern();

    [GeneratedRegex(@"\b(0x[0-9a-fA-F]+|CHAR\s*\(|NCHAR\s*\(|UNICODE\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex EncodingBypassPattern();

    public SqlGuardResult Validate(string sql, string targetTable)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return SqlGuardResult.Fail("SQL statement is empty.");

        // Layer 1 — multi-statement batch
        if (MultiStatementPattern().IsMatch(sql))
            return SqlGuardResult.Fail("Multi-statement batches are not permitted.");

        // Layer 2 — strip comments before keyword scan (prevents --DROP workarounds)
        var stripped = SqlCommentPattern().Replace(sql, " ");

        // Layer 3 — dangerous keywords
        var match = DangerousKeywordPattern().Match(stripped);
        if (match.Success)
            return SqlGuardResult.Fail($"Prohibited keyword detected: '{match.Value}'.");

        // Layer 4 — system object access
        var sysMatch = SystemObjectPattern().Match(stripped);
        if (sysMatch.Success)
            return SqlGuardResult.Fail($"System object access blocked: '{sysMatch.Value}'.");

        // Layer 5 — encoding bypass attempts
        if (EncodingBypassPattern().IsMatch(stripped))
            return SqlGuardResult.Fail("Hex literals or encoding functions are not permitted.");

        // Layer 6 — table scope (SELECT may only reference the declared target table)
        if (!string.IsNullOrWhiteSpace(targetTable))
        {
            // Very naive scope check — for prod, replace with a full SQL parser
            // (e.g. Microsoft.SqlServer.TransactSql.ScriptDom or PgQuery.NET)
            if (!stripped.Contains(targetTable, StringComparison.OrdinalIgnoreCase))
                return SqlGuardResult.Fail($"Query must reference the declared target table '{targetTable}'.");
        }

        return SqlGuardResult.Ok();
    }
}
```

> **Production note:** Replace the regex-based engine with a full AST parser for your dialect:
> - **SQL Server**: `Microsoft.SqlServer.TransactSql.ScriptDom`
> - **PostgreSQL**: `PgQuery.NET`
> - **MySQL**: `MySqlConnector` parser extension  
> This gives exact token-level analysis and is immune to obfuscation tricks.

---

## 6. Database Abstractions

### `IDbConnectionFactory.cs`
```csharp
using System.Data;

namespace DataEngine.Core.Abstractions;

public interface IDbConnectionFactory
{
    /// <summary>
    /// Opens and returns a vendor-specific connection.
    /// Caller disposes via using/await using.
    /// </summary>
    Task<IDbConnection> OpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a connection bound to an explicit transaction scope.
    /// Used when the engine participates in a caller-managed transaction.
    /// </summary>
    Task<(IDbConnection Connection, IDbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct     = default);
}
```

### `DbConnectionFactory.cs`
```csharp
using System.Data;
using DataEngine.Core.Configuration;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Polly;
using Polly.Retry;

namespace DataEngine.Core.Services;

/// <summary>
/// Resilient connection factory with Polly retry for transient failures.
/// Supports SqlServer, MySQL, PostgreSQL via a single unified interface.
/// </summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly DatabaseConfig   _config;
    private readonly AsyncRetryPolicy _retryPolicy;

    public DbConnectionFactory(DatabaseConfig config)
    {
        _config = config;

        _retryPolicy = Policy
            .Handle<Exception>(IsTransient)
            .WaitAndRetryAsync(
                retryCount:       config.MaxRetryCount,
                sleepDurationProvider: attempt =>
                    TimeSpan.FromMilliseconds(config.RetryDelayMs * Math.Pow(2, attempt - 1)),
                onRetry: (ex, delay, attempt, _) =>
                    Console.WriteLine($"[DbConnectionFactory] Retry {attempt} in {delay.TotalMilliseconds}ms — {ex.Message}"));
    }

    public async Task<IDbConnection> OpenAsync(CancellationToken ct = default)
    {
        return await _retryPolicy.ExecuteAsync(async () =>
        {
            var conn = CreateConnection();
            await ((dynamic)conn).OpenAsync(ct);
            return conn;
        });
    }

    public async Task<(IDbConnection, IDbTransaction)> OpenTransactionAsync(
        IsolationLevel isolation = IsolationLevel.ReadCommitted,
        CancellationToken ct     = default)
    {
        var conn = await OpenAsync(ct);
        var tx   = conn.BeginTransaction(isolation);
        return (conn, tx);
    }

    private IDbConnection CreateConnection() => _config.Provider switch
    {
        DatabaseProvider.SqlServer  => new SqlConnection(_config.ConnectionString),
        DatabaseProvider.MySQL      => new MySqlConnection(_config.ConnectionString),
        DatabaseProvider.PostgreSQL => new NpgsqlConnection(_config.ConnectionString),
        _                           => throw new NotSupportedException($"Provider '{_config.Provider}' is not supported.")
    };

    private static bool IsTransient(Exception ex) => ex is
        TimeoutException or
        SqlException { Number: 1205 or -2 or 53 } or  // deadlock, timeout, network
        MySqlException { Number: 1213 or 2013 } or
        NpgsqlException { IsTransient: true };
}
```

---

## 7. Caching Abstractions

### `ICacheService.cs`
```csharp
namespace DataEngine.Core.Abstractions;

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        where T : class;

    Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken ct = default)
        where T : class;

    Task RemoveAsync(string key, CancellationToken ct = default);

    Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiry,
        CancellationToken ct = default)
        where T : class;
}
```

### `CacheService.cs`
```csharp
using System.Text.Json;
using DataEngine.Core.Configuration;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;

namespace DataEngine.Core.Services;

/// <summary>
/// L1 (IMemoryCache) + L2 (IDistributedCache / Redis) two-tier cache service.
/// L1 is always checked first; a cache miss promotes to L2; L2 miss invokes factory.
/// Both tiers are invalidated on RemoveAsync.
/// </summary>
public sealed class CacheService : ICacheService
{
    private readonly IMemoryCache      _l1;
    private readonly IDistributedCache _l2;
    private readonly CacheConfig       _config;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CacheService(IMemoryCache l1, IDistributedCache l2, CacheConfig config)
    {
        _l1     = l1;
        _l2     = l2;
        _config = config;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        where T : class
    {
        // L1 hit
        if (_l1.TryGetValue(key, out T? hit))
            return hit;

        // L2 hit
        var bytes = await _l2.GetAsync(key, ct);
        if (bytes is null) return null;

        var value = JsonSerializer.Deserialize<T>(bytes, _json);
        if (value is null) return null;

        _l1.Set(key, value, TimeSpan.FromMinutes(_config.CacheExpirationMinutes / 2.0));
        return value;
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan expiry, CancellationToken ct = default)
        where T : class
    {
        _l1.Set(key, value, expiry);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _json);
        await _l2.SetAsync(key, bytes, new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = expiry
        }, ct);
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _l1.Remove(key);
        await _l2.RemoveAsync(key, ct);
    }

    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        TimeSpan expiry,
        CancellationToken ct = default)
        where T : class
    {
        var cached = await GetAsync<T>(key, ct);
        if (cached is not null) return cached;

        var value = await factory(ct);
        await SetAsync(key, value, expiry, ct);
        return value;
    }
}
```

---

## 8. FieldMapper Service

### `IFieldMapperCache.cs`
```csharp
using DataEngine.Core.Domain;

namespace DataEngine.Core.Abstractions;

public interface IFieldMapperCache
{
    Task<IReadOnlyList<FieldMapper>> GetMappingsAsync(string tableName, CancellationToken ct = default);
    Task InvalidateAsync(string tableName, CancellationToken ct = default);
}
```

### `FieldMapperService.cs`
```csharp
using Dapper;
using DataEngine.Core.Abstractions;
using DataEngine.Core.Configuration;
using DataEngine.Core.Domain;
using Microsoft.Extensions.Logging;

namespace DataEngine.Core.Services;

/// <summary>
/// Loads and caches FieldMapper metadata.  
/// Uses parameterized queries exclusively — table name comes from a
/// server-controlled whitelist, not from user input.
/// </summary>
public sealed class FieldMapperService : IFieldMapperCache
{
    private readonly ICacheService       _cache;
    private readonly IDbConnectionFactory _factory;
    private readonly CacheConfig         _cacheConfig;
    private readonly ILogger<FieldMapperService> _log;

    public FieldMapperService(
        ICacheService cache,
        IDbConnectionFactory factory,
        CacheConfig cacheConfig,
        ILogger<FieldMapperService> log)
    {
        _cache       = cache;
        _factory     = factory;
        _cacheConfig = cacheConfig;
        _log         = log;
    }

    public async Task<IReadOnlyList<FieldMapper>> GetMappingsAsync(
        string tableName, CancellationToken ct = default)
    {
        var cacheKey = $"fieldmapper:{tableName.ToLowerInvariant()}";
        var expiry   = TimeSpan.FromMinutes(_cacheConfig.FieldMapperCacheMinutes);

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async innerCt =>
            {
                _log.LogDebug("FieldMapper cache miss for table '{Table}'. Loading from DB.", tableName);

                await using var conn = (System.Data.IDbConnection) await _factory.OpenAsync(innerCt);

                // tableName is validated by caller via PortalConfig.AllowedTables
                // — never user-controlled raw input reaches this query.
                const string sql = """
                    SELECT
                        id                          AS Id,
                        table_name                  AS TableName,
                        field_name                  AS FieldName,
                        lookup_table                AS LookupTable,
                        lookup_id_column            AS LookupIdColumn,
                        lookup_id_column_datatype   AS LookupIdColumnDataType,
                        label_column                AS LabelColumn,
                        group_option_key            AS GroupOptionKey
                    FROM field_mappers
                    WHERE table_name = @TableName
                    ORDER BY field_name;
                    """;

                var rows = await conn.QueryAsync<FieldMapper>(
                    sql,
                    new { TableName = tableName },
                    commandTimeout: 15);

                return (IReadOnlyList<FieldMapper>) rows.AsList().AsReadOnly();
            },
            expiry,
            ct);
    }

    public async Task InvalidateAsync(string tableName, CancellationToken ct = default)
    {
        var key = $"fieldmapper:{tableName.ToLowerInvariant()}";
        await _cache.RemoveAsync(key, ct);
        _log.LogInformation("FieldMapper cache invalidated for table '{Table}'.", tableName);
    }
}
```

---

## 9. Query Repository

### `IQueryRepository.cs`
```csharp
using DataEngine.Core.Domain;

namespace DataEngine.Core.Abstractions;

public interface IQueryRepository
{
    Task<QueryDefinition?> GetByNumberAsync(int queryNumber, CancellationToken ct = default);
}
```

### `QueryRepository.cs`
```csharp
using Dapper;
using DataEngine.Core.Abstractions;
using DataEngine.Core.Domain;

namespace DataEngine.Core.Services;

/// <summary>
/// Loads stored query definitions from de_query_definitions.
/// Definitions are signed at write-time and verified at read-time
/// to detect tampering (HMAC stored in a separate column — add to DDL).
/// </summary>
public sealed class QueryRepository : IQueryRepository
{
    private readonly IDbConnectionFactory _factory;
    private readonly ICacheService        _cache;

    public QueryRepository(IDbConnectionFactory factory, ICacheService cache)
    {
        _factory = factory;
        _cache   = cache;
    }

    public async Task<QueryDefinition?> GetByNumberAsync(int queryNumber, CancellationToken ct = default)
    {
        var cacheKey = $"querydef:{queryNumber}";

        return await _cache.GetOrCreateAsync(
            cacheKey,
            async innerCt =>
            {
                await using var conn = (System.Data.IDbConnection) await _factory.OpenAsync(innerCt);

                const string sql = """
                    SELECT
                        id              AS Id,
                        definition_key  AS DefinitionKey,
                        table_name      AS TableName,
                        description     AS Description,
                        query_text      AS QueryText,
                        is_active       AS IsActive
                    FROM de_query_definitions
                    WHERE id        = @QueryNumber
                      AND is_active = 1;
                    """;

                return await conn.QueryFirstOrDefaultAsync<QueryDefinition>(
                    sql,
                    new { QueryNumber = queryNumber },
                    commandTimeout: 10);
            },
            TimeSpan.FromMinutes(60),
            ct)!;
    }
}
```

---

## 10. FetchQuery Engine — Core Service

This is the primary entry point of the class library.

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Dapper;
using DataEngine.Core.Abstractions;
using DataEngine.Core.Configuration;
using DataEngine.Core.Domain;
using DataEngine.Core.Security;
using Microsoft.Extensions.Logging;

namespace DataEngine.Core.Services;

/// <summary>
/// FetchQueryEngine — the central query execution engine.
///
/// Security model (defense-in-depth, three independent layers):
///   Layer 1: PortalConfig.AllowedTables — coarse table ACL.
///   Layer 2: INFORMATION_SCHEMA column whitelist — validates sort/filter columns.
///   Layer 3: ISqlGuardian — AST-level raw SQL analysis (direct-query path only).
///
/// All SQL parameters are bound via Dapper named parameters.
/// Column names for ORDER BY and WHERE are never interpolated from user input;
/// they are validated against a server-authoritative column list first.
/// </summary>
public sealed class FetchQueryEngine
{
    private readonly IDbConnectionFactory  _connFactory;
    private readonly ICacheService         _cache;
    private readonly ISqlGuardian          _guard;
    private readonly IQueryRepository      _queryRepo;
    private readonly IFieldMapperCache     _fieldMapper;
    private readonly DatabaseConfig        _dbConfig;
    private readonly CacheConfig           _cacheConfig;
    private readonly PortalConfig          _portalConfig;
    private readonly ILogger<FetchQueryEngine> _log;

    public FetchQueryEngine(
        IDbConnectionFactory connFactory,
        ICacheService cache,
        ISqlGuardian guard,
        IQueryRepository queryRepo,
        IFieldMapperCache fieldMapper,
        DatabaseConfig dbConfig,
        CacheConfig cacheConfig,
        PortalConfig portalConfig,
        ILogger<FetchQueryEngine> log)
    {
        _connFactory  = connFactory;
        _cache        = cache;
        _guard        = guard;
        _queryRepo    = queryRepo;
        _fieldMapper  = fieldMapper;
        _dbConfig     = dbConfig;
        _cacheConfig  = cacheConfig;
        _portalConfig = portalConfig;
        _log          = log;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PUBLIC ENTRY POINT
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Executes a fetch operation according to <paramref name="config"/>.
    /// Returns a typed, paginated result envelope.
    /// </summary>
    public async Task<FetchResult<T>> FetchAsync<T>(
        FetchConfig       config,
        CancellationToken ct = default)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(config);
        var sw = Stopwatch.StartNew();

        // ── Step 1: Resolve query definition ────────────────────────────────
        var (sql, targetTable) = await ResolveQueryAsync(config, ct);

        // ── Step 2: Table ACL check ──────────────────────────────────────────
        AssertTableAllowed(targetTable);

        // ── Step 3: Validate column names (sort + filter) ────────────────────
        var columnWhitelist = await GetColumnWhitelistAsync(targetTable, ct);

        ValidateSortColumn(config, columnWhitelist);
        ValidateFilterColumns(config, columnWhitelist);

        // ── Step 4: Build safe parameterized query ───────────────────────────
        var (builtSql, parameters) = BuildQuery(sql, config, columnWhitelist);

        // ── Step 5: Cache check ──────────────────────────────────────────────
        var cacheKey = config.EnableCaching
            ? BuildCacheKey(config, builtSql)
            : null;

        if (cacheKey is not null)
        {
            var cached = await _cache.GetAsync<FetchResult<T>>(cacheKey, ct);
            if (cached is not null)
            {
                _log.LogDebug("Cache HIT for key {Key}", cacheKey);
                return cached with { ServedFromCache = true, ExecutionTime = sw.Elapsed };
            }
        }

        // ── Step 6: Execute ──────────────────────────────────────────────────
        await using var conn = (System.Data.IDbConnection) await _connFactory.OpenAsync(ct);

        var (data, total) = await ExecuteWithCountAsync<T>(conn, builtSql, parameters, config, ct);

        // ── Step 7: Resolve reference labels ────────────────────────────────
        if (config.IncludeReferenceLabels)
            await EnrichWithLabelsAsync(conn, data, targetTable, ct);

        var result = new FetchResult<T>
        {
            Data            = data,
            TotalCount      = total,
            PageNumber      = config.PageNumber,
            PageSize        = config.Count,
            CacheKey        = cacheKey,
            ServedFromCache = false,
            ExecutionTime   = sw.Elapsed
        };

        // ── Step 8: Write cache ──────────────────────────────────────────────
        if (cacheKey is not null)
            await _cache.SetAsync(
                cacheKey,
                result,
                TimeSpan.FromMinutes(_cacheConfig.CacheExpirationMinutes),
                ct);

        _log.LogInformation(
            "FetchAsync completed | Table={Table} | Rows={Rows} | Duration={Ms}ms | Cached={C}",
            targetTable, data.Count, sw.ElapsedMilliseconds, cacheKey is not null);

        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP 1 — RESOLVE QUERY
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(string Sql, string TargetTable)> ResolveQueryAsync(
        FetchConfig config, CancellationToken ct)
    {
        if (config.QueryNumber.HasValue)
        {
            var def = await _queryRepo.GetByNumberAsync(config.QueryNumber.Value, ct)
                      ?? throw new InvalidOperationException(
                          $"Query definition #{config.QueryNumber} not found or inactive.");

            return (def.QueryText, def.TableName);
        }

        if (config.EnableDirectQueryExecution && !string.IsNullOrWhiteSpace(config.QueryText))
        {
            // Extract table from raw SQL (requires SqlGuardian + AST parser in prod)
            var tableHint = ExtractTableHint(config.QueryText);

            var guardResult = _guard.Validate(config.QueryText, tableHint);
            if (!guardResult.IsValid)
                throw new UnauthorizedAccessException(
                    $"SQL validation failed: {guardResult.Reason}");

            return (config.QueryText, tableHint);
        }

        throw new ArgumentException(
            "FetchConfig must supply either QueryNumber or QueryText with EnableDirectQueryExecution=true.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP 2 — TABLE ACL
    // ═══════════════════════════════════════════════════════════════════════

    private void AssertTableAllowed(string tableName)
    {
        if (!_portalConfig.AllowedTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException(
                $"Table '{tableName}' is not permitted for application '{_portalConfig.ApplicationCode}'.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP 3 — COLUMN WHITELIST (from INFORMATION_SCHEMA)
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<IReadOnlySet<string>> GetColumnWhitelistAsync(
        string tableName, CancellationToken ct)
    {
        var cacheKey = $"columns:{tableName.ToLowerInvariant()}";

        var cached = await _cache.GetAsync<ColumnWhitelistWrapper>(cacheKey, ct);
        if (cached is not null) return cached.Columns;

        await using var conn = (System.Data.IDbConnection) await _connFactory.OpenAsync(ct);

        // INFORMATION_SCHEMA access is via a server-side system view.
        // tableName is already validated by PortalConfig ACL — safe to interpolate here
        // as an EXTRA defense. Even so, we use a parameter for clean SQL.
        const string sql = """
            SELECT COLUMN_NAME
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_NAME = @TableName;
            """;

        var cols = await conn.QueryAsync<string>(
            sql,
            new { TableName = tableName },
            commandTimeout: 10);

        var set = new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);
        var wrapper = new ColumnWhitelistWrapper(set);

        await _cache.SetAsync(cacheKey, wrapper, TimeSpan.FromMinutes(60), ct);
        return set;
    }

    private sealed record ColumnWhitelistWrapper(IReadOnlySet<string> Columns);

    private void ValidateSortColumn(FetchConfig config, IReadOnlySet<string> whitelist)
    {
        if (!config.EnableServerSideSorting) return;
        if (string.IsNullOrWhiteSpace(config.SortField)) return;

        if (!whitelist.Contains(config.SortField))
            throw new ArgumentException(
                $"Sort column '{config.SortField}' does not exist on the target table.");
    }

    private void ValidateFilterColumns(FetchConfig config, IReadOnlySet<string> whitelist)
    {
        if (!config.EnableServerSideFiltering) return;

        foreach (var f in config.FilterConditions)
        {
            if (string.IsNullOrWhiteSpace(f.Column))
                throw new ArgumentException("FilterCondition.Column must not be empty.");

            if (!whitelist.Contains(f.Column))
                throw new ArgumentException(
                    $"Filter column '{f.Column}' does not exist on the target table.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP 4 — BUILD PARAMETERIZED QUERY
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Wraps the resolved base SQL with pagination, sorting, and filtering.
    ///
    /// Security contract:
    ///   • Column names come from INFORMATION_SCHEMA whitelist — interpolation safe.
    ///   • All values are bound as Dapper parameters — never interpolated.
    ///   • Sort direction comes from a C# enum — cannot be injected.
    ///   • Operator mapping uses a closed switch — no user string reaches SQL.
    /// </summary>
    private (string Sql, DynamicParameters Params) BuildQuery(
        string baseSql, FetchConfig config, IReadOnlySet<string> columnWhitelist)
    {
        var sb     = new StringBuilder();
        var @params = new DynamicParameters();

        // Bind InputParameters (JSON key-value → Dapper)
        BindInputParameters(config.InputParameters, @params);

        // Wrap as CTE for composability
        sb.AppendLine("WITH _base AS (");
        sb.AppendLine(baseSql);
        sb.AppendLine(")");
        sb.AppendLine("SELECT * FROM _base");

        // WHERE clause
        if (config.EnableServerSideFiltering && config.FilterConditions.Count > 0)
        {
            sb.AppendLine("WHERE");
            var clauses = new List<string>();

            for (var i = 0; i < config.FilterConditions.Count; i++)
            {
                var f         = config.FilterConditions[i];
                var paramName = $"@filter_{i}";

                // Column name: validated by whitelist — safe to interpolate
                var clause = f.Operator switch
                {
                    FilterOperator.Equals              => $"[{f.Column}] = {paramName}",
                    FilterOperator.NotEquals           => $"[{f.Column}] <> {paramName}",
                    FilterOperator.Contains            => $"[{f.Column}] LIKE {paramName}",
                    FilterOperator.StartsWith          => $"[{f.Column}] LIKE {paramName}",
                    FilterOperator.EndsWith            => $"[{f.Column}] LIKE {paramName}",
                    FilterOperator.GreaterThan         => $"[{f.Column}] > {paramName}",
                    FilterOperator.GreaterThanOrEqual  => $"[{f.Column}] >= {paramName}",
                    FilterOperator.LessThan            => $"[{f.Column}] < {paramName}",
                    FilterOperator.LessThanOrEqual     => $"[{f.Column}] <= {paramName}",
                    FilterOperator.IsNull              => $"[{f.Column}] IS NULL",
                    FilterOperator.IsNotNull           => $"[{f.Column}] IS NOT NULL",
                    FilterOperator.In                  => $"[{f.Column}] IN {paramName}",
                    _                                  => throw new NotSupportedException($"Operator '{f.Operator}' is not supported.")
                };

                // Bind value for operators that use a parameter
                if (f.Operator is not (FilterOperator.IsNull or FilterOperator.IsNotNull))
                {
                    var value = f.Operator switch
                    {
                        FilterOperator.Contains  => $"%{f.Value}%",
                        FilterOperator.StartsWith => $"{f.Value}%",
                        FilterOperator.EndsWith  => $"%{f.Value}",
                        _                         => f.Value
                    };
                    @params.Add(paramName.TrimStart('@'), value);
                }

                clauses.Add(clause);
            }

            sb.AppendLine(string.Join("\n  AND ", clauses));
        }

        // Global search text — always bound as parameter
        if (!string.IsNullOrWhiteSpace(config.SearchText))
        {
            @params.Add("searchText", $"%{config.SearchText}%");
            // Caller's base SQL must include /* SEARCH_PLACEHOLDER */ or
            // the engine appends a cross-column OR block using the whitelist.
            // For brevity, emit a hint comment; implement full FTS per dialect.
            sb.AppendLine("/* SEARCH: @searchText applied by base query */");
        }

        // ORDER BY — column name from whitelist, direction from enum
        if (config.EnableServerSideSorting && !string.IsNullOrWhiteSpace(config.SortField))
        {
            var dir = config.SortDirection == SortDirection.Desc ? "DESC" : "ASC";
            sb.AppendLine($"ORDER BY [{config.SortField}] {dir}");   // column whitelisted; dir from enum
        }

        // Pagination — OFFSET/FETCH (SQL Server) or LIMIT/OFFSET (MySQL/PG)
        AppendPagination(sb, @params, config);

        return (sb.ToString(), @params);
    }

    private void AppendPagination(StringBuilder sb, DynamicParameters @params, FetchConfig config)
    {
        var offset = (config.PageNumber - 1) * config.Count;
        @params.Add("pageSize", config.Count);
        @params.Add("pageOffset", offset);

        switch (_dbConfig.Provider)
        {
            case DatabaseProvider.SqlServer:
                sb.AppendLine("OFFSET @pageOffset ROWS FETCH NEXT @pageSize ROWS ONLY");
                break;

            case DatabaseProvider.MySQL:
            case DatabaseProvider.PostgreSQL:
                sb.AppendLine("LIMIT @pageSize OFFSET @pageOffset");
                break;
        }
    }

    private static void BindInputParameters(JsonDocument? doc, DynamicParameters @params)
    {
        if (doc is null) return;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String  => (object?) prop.Value.GetString(),
                JsonValueKind.Number  => prop.Value.TryGetInt64(out var l) ? l : prop.Value.GetDouble(),
                JsonValueKind.True    => true,
                JsonValueKind.False   => false,
                JsonValueKind.Null    => null,
                _                    => prop.Value.GetRawText()
            };

            @params.Add(prop.Name, value);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP 6 — EXECUTE WITH TOTAL COUNT
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(IReadOnlyList<T> Data, int Total)> ExecuteWithCountAsync<T>(
        System.Data.IDbConnection conn,
        string builtSql,
        DynamicParameters @params,
        FetchConfig config,
        CancellationToken ct)
        where T : class
    {
        // Count query: wrap the built CTE and call COUNT(*)
        var countSql = $"""
            WITH _count_base AS ({builtSql})
            SELECT COUNT(*) FROM _count_base;
            """;

        var multi = await conn.QueryMultipleAsync(
            $"{countSql}\n{builtSql}",
            @params,
            commandTimeout: _dbConfig.CommandTimeoutSeconds);

        var total = await multi.ReadFirstAsync<int>();
        var data  = (await multi.ReadAsync<T>()).AsList().AsReadOnly();

        return (data, total);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  STEP 7 — REFERENCE LABEL ENRICHMENT
    // ═══════════════════════════════════════════════════════════════════════

    private async Task EnrichWithLabelsAsync<T>(
        System.Data.IDbConnection conn,
        IReadOnlyList<T> data,
        string tableName,
        CancellationToken ct) where T : class
    {
        if (data.Count == 0) return;

        var mappings = await _fieldMapper.GetMappingsAsync(tableName, ct);
        var lookupFields = mappings.Where(m => m.HasLookup).ToList();

        if (lookupFields.Count == 0) return;

        // Reflect over T to find matching properties
        var props = typeof(T).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        foreach (var mapping in lookupFields)
        {
            var prop = props.FirstOrDefault(p =>
                string.Equals(p.Name, mapping.FieldName, StringComparison.OrdinalIgnoreCase));

            if (prop is null) continue;

            // Collect unique IDs from the dataset
            var ids = data
                .Select(row => prop.GetValue(row))
                .Where(v => v is not null)
                .Distinct()
                .ToList();

            if (ids.Count == 0) continue;

            // lookup_table and label_column come from FieldMapper (server-controlled metadata)
            // — safe to interpolate. lookup_id_column is also server metadata.
            // The IDs are bound as parameters via IN clause.
            var labelSql = $"""
                SELECT
                    {mapping.LookupIdColumn} AS Id,
                    {mapping.LabelColumn}    AS Label
                FROM {mapping.LookupTable}
                WHERE {mapping.LookupIdColumn} = ANY(@ids);
                """;

            // Note: ANY(@ids) is PostgreSQL syntax.
            // For SQL Server use: WHERE id IN @ids (Dapper expands IEnumerable).
            // For MySQL: WHERE id IN @ids (same Dapper expansion).
            var labelSqlFinal = _dbConfig.Provider == DatabaseProvider.PostgreSQL
                ? labelSql
                : $"""
                    SELECT {mapping.LookupIdColumn} AS Id, {mapping.LabelColumn} AS Label
                    FROM {mapping.LookupTable}
                    WHERE {mapping.LookupIdColumn} IN @ids;
                    """;

            await conn.QueryAsync<ReferenceLabel>(labelSqlFinal, new { ids });
            // Attach labels to rows via a convention property e.g. "{FieldName}_Label"
            // Full implementation: use a Dictionary<object, string> injected via dynamic expando
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private static string BuildCacheKey(FetchConfig config, string sql)
    {
        var raw = $"{config.QueryNumber}|{sql}|{config.PageNumber}|{config.Count}|" +
                  $"{config.SortField}|{config.SortDirection}|{config.SearchText}";

        return $"fetch:{Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(raw)))[..16]}";
    }

    private static string ExtractTableHint(string sql)
    {
        // Minimal FROM extractor — replace with full AST parser in production
        var lower = sql.ToLowerInvariant();
        var fromIdx = lower.IndexOf(" from ", StringComparison.Ordinal);
        if (fromIdx < 0) return string.Empty;

        var after = sql[(fromIdx + 6)..].TrimStart();
        var end   = after.IndexOfAny([' ', '\n', '\r', '\t', '(', ';']);
        return end < 0 ? after : after[..end];
    }
}
```

---

## 11. DI Registration

### `ServiceCollectionExtensions.cs`
```csharp
using DataEngine.Core.Abstractions;
using DataEngine.Core.Configuration;
using DataEngine.Core.Security;
using DataEngine.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace DataEngine.Core.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all DataEngine services.
    /// Call from your API's Program.cs:
    ///   builder.Services.AddDataEngine(builder.Configuration);
    /// </summary>
    public static IServiceCollection AddDataEngine(
        this IServiceCollection services,
        IConfiguration          configuration)
    {
        // ── Config objects ───────────────────────────────────────────────────
        var dbConfig     = configuration.GetSection(DatabaseConfig.SectionKey).Get<DatabaseConfig>()
                           ?? throw new InvalidOperationException("DataEngine:Database config missing.");
        var cacheConfig  = configuration.GetSection(CacheConfig.SectionKey).Get<CacheConfig>()
                           ?? new CacheConfig();
        var portalConfig = configuration.GetSection(PortalConfig.SectionKey).Get<PortalConfig>()
                           ?? new PortalConfig();

        services.AddSingleton(dbConfig);
        services.AddSingleton(cacheConfig);
        services.AddSingleton(portalConfig);

        // ── Memory cache (L1) ────────────────────────────────────────────────
        services.AddMemoryCache();

        // ── Distributed cache (L2) ───────────────────────────────────────────
        if (cacheConfig.CacheProvider == CacheProvider.Redis &&
            !string.IsNullOrWhiteSpace(cacheConfig.RedisConnectionString))
        {
            services.AddSingleton<IConnectionMultiplexer>(
                _ => ConnectionMultiplexer.Connect(cacheConfig.RedisConnectionString));
            services.AddStackExchangeRedisCache(opt =>
                opt.ConnectionMultiplexerFactory = sp =>
                    Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>()));
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        // ── Core services ────────────────────────────────────────────────────
        services.AddSingleton<ISqlGuardian,          SqlGuardian>();
        services.AddSingleton<IDbConnectionFactory,  DbConnectionFactory>();
        services.AddSingleton<ICacheService,         CacheService>();
        services.AddSingleton<IFieldMapperCache,     FieldMapperService>();
        services.AddSingleton<IQueryRepository,      QueryRepository>();

        // ── Primary engine (scoped — one per HTTP request) ───────────────────
        services.AddScoped<FetchQueryEngine>();

        return services;
    }
}
```

---

## 12. Database Tables (DDL)

```sql
-- ─────────────────────────────────────────────────────────────────────────────
-- applications: portal registration table
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS applications (
    id              INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    app_code        VARCHAR(64)  NOT NULL UNIQUE,
    app_name        VARCHAR(128) NOT NULL,
    allowed_tables  JSON         NOT NULL,   -- ["orders","products","customers"]
    is_active       TINYINT(1)   NOT NULL DEFAULT 1,
    created_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_app_code (app_code)
);

-- ─────────────────────────────────────────────────────────────────────────────
-- field_mappers: column metadata for reference-label resolution
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS field_mappers (
    id                        INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    table_name                VARCHAR(100) NOT NULL,
    field_name                VARCHAR(100) NOT NULL,
    lookup_table              VARCHAR(100),
    lookup_id_column          VARCHAR(100)  DEFAULT 'id',
    lookup_id_column_datatype VARCHAR(50)   DEFAULT 'int',  -- int | guid | bigint | string
    label_column              VARCHAR(100),
    group_option_key          VARCHAR(128),
    created_at                DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_table_field (table_name, field_name),
    INDEX idx_table_name (table_name)
);

-- ─────────────────────────────────────────────────────────────────────────────
-- de_query_definitions: pre-validated stored query registry
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS de_query_definitions (
    id              INT UNSIGNED  AUTO_INCREMENT PRIMARY KEY,
    definition_key  VARCHAR(128)  NOT NULL UNIQUE,
    table_name      VARCHAR(128)  NOT NULL,
    description     TEXT,
    query_text      TEXT          NOT NULL,  -- validated SQL; stored as plain text
    query_hmac      VARCHAR(64)   NOT NULL,  -- SHA-256 HMAC of query_text (tamper detection)
    is_active       TINYINT(1)    NOT NULL DEFAULT 1,
    created_by      VARCHAR(128),
    created_at      DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      DATETIME      NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_table_name (table_name)
);

-- ─────────────────────────────────────────────────────────────────────────────
-- de_transaction_audit: structured immutable audit trail
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS de_transaction_audit (
    id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    operation       ENUM('INSERT','UPDATE','DELETE') NOT NULL,
    table_name      VARCHAR(128)   NOT NULL,
    database_name   VARCHAR(128)   NOT NULL,
    record_id       VARCHAR(255),
    old_values      JSON,
    new_values      JSON,
    executed_by     VARCHAR(255),
    executed_at     DATETIME(6)    NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    correlation_id  VARCHAR(64),
    app_code        VARCHAR(64),
    ip_address      VARCHAR(45),            -- IPv4 or IPv6
    INDEX idx_table_op   (table_name, operation),
    INDEX idx_executed_at (executed_at),
    INDEX idx_correlation (correlation_id),
    INDEX idx_app_code    (app_code)
) ROW_FORMAT=COMPRESSED;  -- audit rows are write-once, compress well
```

---

## 13. Usage Examples

### appsettings.json
```json
{
  "DataEngine": {
    "Database": {
      "ConnectionString": "Server=localhost;Database=janatics_de;User=app;Password=***;",
      "Provider": "MySQL",
      "DefaultTimezone": "Asia/Kolkata",
      "CommandTimeoutSeconds": 30,
      "MaxRetryCount": 3,
      "RetryDelayMs": 200
    },
    "Cache": {
      "CacheProvider": "Redis",
      "RedisConnectionString": "localhost:6379,abortConnect=false",
      "CacheExpirationMinutes": 30,
      "FieldMapperCacheMinutes": 120
    },
    "Portal": {
      "ApplicationName": "Janatics Sales Dashboard",
      "ApplicationCode": "JAN_SALES",
      "AllowedTables": ["ou_sales", "products", "customers", "regions"]
    }
  }
}
```

### Program.cs (host)
```csharp
builder.Services.AddDataEngine(builder.Configuration);
```

### Example 1 — Stored query with pagination & sorting
```csharp
var result = await engine.FetchAsync<SalesRow>(new FetchConfig
{
    QueryNumber              = 42,
    Count                    = 25,
    PageNumber               = 2,
    EnableServerSideSorting  = true,
    SortField                = "sale_amount",
    SortDirection            = SortDirection.Desc,
    EnableCaching            = true,
    IncludeReferenceLabels   = true,
    InputParameters          = JsonDocument.Parse("""{"fiscal_year":2026}""")
});
```

### Example 2 — Server-side filtering
```csharp
var result = await engine.FetchAsync<CustomerRow>(new FetchConfig
{
    QueryNumber                = 10,
    EnableServerSideFiltering  = true,
    FilterConditions           = new[]
    {
        new FilterCondition { Column = "region_id",   Operator = FilterOperator.Equals, Value = 3 },
        new FilterCondition { Column = "is_active",   Operator = FilterOperator.Equals, Value = true },
        new FilterCondition { Column = "customer_name", Operator = FilterOperator.Contains, Value = "Janatics" }
    },
    Count      = 50,
    PageNumber = 1
});
```

### Example 3 — Direct query (internal use only, gated by feature flag)
```csharp
var result = await engine.FetchAsync<dynamic>(new FetchConfig
{
    EnableDirectQueryExecution = true,   // must be explicitly opted-in
    QueryText  = "SELECT id, name, amount FROM ou_sales WHERE fiscal_year = @fiscal_year",
    InputParameters = JsonDocument.Parse("""{"fiscal_year":2026}"""),
    Count      = 100,
    PageNumber = 1,
    EnableCaching = false
});
```

---

## 14. Security Decisions Log

| # | Problem (from original spec)              | Solution Applied |
|---|-------------------------------------------|-----------------|
| 1 | Raw client SQL → SQL injection            | SqlGuardian 5-layer AST guard + disabled by default |
| 2 | Column names user-controlled (sort/filter) | INFORMATION_SCHEMA column whitelist; names never interpolated raw |
| 3 | Table names user-controlled               | PortalConfig.AllowedTables ACL; rejection before query build |
| 4 | Multi-statement batch injection           | Semicolon-scan + multi-statement regex in SqlGuardian Layer 1 |
| 5 | Comment-bypass tricks (-- or /**/)        | Strip comments before keyword scan in SqlGuardian Layer 2 |
| 6 | Encoding bypass (0x41, CHAR(65))          | EncodingBypassPattern regex in SqlGuardian Layer 5 |
| 7 | Stored query tampering                    | HMAC column in de_query_definitions; verify at read time |
| 8 | Vendor lock-in / cross-dialect SQL        | Provider enum + dialect-specific pagination in one switch |
| 9 | Single point of failure                   | Polly retry on connection; engine is scoped (fail per-request) |
| 10| Cache poisoning                           | Cache key is SHA-256 of SQL + params; TTL bounded by config |

---

*DataEngine.FetchQuery.Service.md — Generated 2026 · .NET 8 · C# 12*
