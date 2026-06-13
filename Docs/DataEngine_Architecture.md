# DataEngine — Enterprise-Grade Metadata-Driven Backend Framework
## Principal Architect Design Document

> **Version:** 1.0 | **Target:** .NET 8 (forward-compatible to .NET 9) | **Author:** Principal Architecture Review

---

## Table of Contents

1. [Architect's Vision & Core Opinions](#1-architects-vision--core-opinions)
2. [Folder Structure & Solution Layout](#2-folder-structure--solution-layout)
3. [Domain Model Design](#3-domain-model-design)
4. [Interface Design (API Surface)](#4-interface-design-api-surface)
5. [Request/Response DTOs & API Contracts](#5-requestresponse-dtos--api-contracts)
6. [Schema Cache Implementation](#6-schema-cache-implementation)
7. [Service Implementations](#7-service-implementations)
8. [Query Compilation Strategy](#8-query-compilation-strategy)
9. [Transaction Orchestration](#9-transaction-orchestration)
10. [Procedure Execution](#10-procedure-execution)
11. [DI Registration & Plug-and-Play Setup](#11-di-registration--plug-and-play-setup)
12. [Security Model](#12-security-model)
13. [Optional Extensions](#13-optional-extensions)
14. [NuGet Packaging Strategy](#14-nuget-packaging-strategy)
15. [Unit Testing Strategy](#15-unit-testing-strategy)
16. [Integration Testing Strategy](#16-integration-testing-strategy)
17. [Build-Now vs Defer Roadmap](#17-build-now-vs-defer-roadmap)
18. [Best Practices & Architectural Decisions](#18-best-practices--architectural-decisions)

---

## 1. Architect's Vision & Core Opinions

### The Mental Model

DataEngine is **not an ORM**. It is a **dynamic SQL execution engine** with a contract-first, metadata-driven safety layer. The consumer never writes a model class, never writes a repository, never touches a migration. They describe *what they want* in a plain object, and DataEngine figures out *how to safely do it*.

Think of it as the backend equivalent of what AG Grid is on the frontend: a plug-in grid that handles everything — you just give it data and config.

### Strong Opinions

**Opinion 1: Schema cache is the single source of truth.**
Every table name and column name in a request MUST pass through the schema cache before a single SQL character is emitted. No exceptions. This is non-negotiable even for performance — a cache miss costs ~2ms; an injection attack costs your job.

**Opinion 2: Dapper for reads, ADO.NET for writes — strictly.**
Dapper's `dynamic` mapping is a legitimate use here since we have no entity classes. For writes, ADO.NET gives us full control over parameter types, command reuse, and transaction scope. Never mix them.

**Opinion 3: Zero magic strings in SQL.**
All SQL is built by a `QueryCompiler` that uses schema metadata. Column names come from `ISchemaProvider`, not from user input. User input maps *to* column names, never becomes column names directly.

**Opinion 4: Optional tables must be truly optional.**
The framework must boot and operate fully if `de_field_mappings`, `de_query_definitions`, `de_transaction_audit`, and `de_procedures` don't exist. Detect their presence at startup, cache the result, and gate all related code behind that flag.

**Opinion 5: The developer experience IS the product.**
A junior developer should be able to do a full CRUD operation in 10 minutes of reading. The API surface should feel like a well-designed REST client library, not a query builder. Fluent is better than verbose. Defaults should be safe and sensible.

**Opinion 6: Never use `object` or `dynamic` for the public API surface.**
`DataEngineRequest`, `DataEngineResponse`, and all DTOs are strongly typed C# records. Internally, Dapper's `IDictionary<string, object>` is acceptable. The public boundary is clean.

---

## 2. Folder Structure & Solution Layout

```
DataEngine/                                  ← Solution root
├── src/
│   ├── DataEngine.Core/                     ← Domain + Interfaces (no dependencies)
│   │   ├── Domain/
│   │   │   ├── Schema/
│   │   │   │   ├── TableMetadata.cs
│   │   │   │   ├── ColumnMetadata.cs
│   │   │   │   ├── PrimaryKeyMetadata.cs
│   │   │   │   └── SchemaMetadataSnapshot.cs
│   │   │   ├── Query/
│   │   │   │   ├── CompiledQuery.cs
│   │   │   │   ├── FilterClause.cs
│   │   │   │   ├── SortClause.cs
│   │   │   │   ├── PaginationClause.cs
│   │   │   │   ├── ProjectionClause.cs
│   │   │   │   └── JoinClause.cs
│   │   │   ├── Execution/
│   │   │   │   ├── ExecutionContext.cs
│   │   │   │   └── TransactionScope.cs
│   │   │   └── Audit/
│   │   │       └── AuditEntry.cs
│   │   ├── Interfaces/
│   │   │   ├── IDataEngine.cs
│   │   │   ├── ISchemaProvider.cs
│   │   │   ├── IQueryExecutor.cs
│   │   │   ├── IQueryCompiler.cs
│   │   │   ├── ITransactionExecutor.cs
│   │   │   ├── IProcedureExecutor.cs
│   │   │   ├── IFieldMapperProvider.cs
│   │   │   ├── IAuditWriter.cs
│   │   │   ├── IConnectionFactory.cs
│   │   │   └── ISchemaCache.cs
│   │   ├── Contracts/
│   │   │   ├── Requests/
│   │   │   │   ├── QueryRequest.cs
│   │   │   │   ├── InsertRequest.cs
│   │   │   │   ├── UpdateRequest.cs
│   │   │   │   ├── DeleteRequest.cs
│   │   │   │   ├── BulkInsertRequest.cs
│   │   │   │   ├── BulkUpdateRequest.cs
│   │   │   │   └── ProcedureRequest.cs
│   │   │   └── Responses/
│   │   │       ├── QueryResponse.cs
│   │   │       ├── MutationResponse.cs
│   │   │       ├── ProcedureResponse.cs
│   │   │       └── SchemaResponse.cs
│   │   └── Exceptions/
│   │       ├── DataEngineException.cs
│   │       ├── SchemaValidationException.cs
│   │       ├── SecurityViolationException.cs
│   │       └── TableNotFoundException.cs
│   │
│   ├── DataEngine.Infrastructure/           ← All implementations
│   │   ├── Schema/
│   │   │   ├── MySqlSchemaProvider.cs
│   │   │   ├── InMemorySchemaCache.cs
│   │   │   └── SchemaRefreshService.cs      ← IHostedService
│   │   ├── Query/
│   │   │   ├── MySqlQueryCompiler.cs
│   │   │   ├── DapperQueryExecutor.cs
│   │   │   ├── FilterBuilder.cs
│   │   │   ├── SortBuilder.cs
│   │   │   └── PaginationBuilder.cs
│   │   ├── Write/
│   │   │   ├── AdoNetWriteExecutor.cs
│   │   │   ├── BulkWriteExecutor.cs
│   │   │   └── TransactionCoordinator.cs
│   │   ├── Procedure/
│   │   │   └── MySqlProcedureExecutor.cs
│   │   ├── Connection/
│   │   │   ├── MySqlConnectionFactory.cs
│   │   │   └── MultiDatabaseRouter.cs
│   │   ├── FieldMapping/
│   │   │   ├── FieldMapperProvider.cs
│   │   │   └── NullFieldMapperProvider.cs
│   │   ├── Audit/
│   │   │   ├── TableAuditWriter.cs
│   │   │   └── NullAuditWriter.cs
│   │   ├── Extensions/
│   │   │   └── OptionalTablesDetector.cs
│   │   └── Security/
│   │       ├── TableGuard.cs
│   │       └── ColumnGuard.cs
│   │
│   ├── DataEngine.Application/              ← Orchestration layer
│   │   ├── DataEngineService.cs             ← IDataEngine implementation
│   │   ├── QueryOrchestrator.cs
│   │   ├── WriteOrchestrator.cs
│   │   └── ProcedureOrchestrator.cs
│   │
│   └── DataEngine.Extensions.DependencyInjection/   ← NuGet entrypoint
│       ├── DataEngineServiceCollectionExtensions.cs
│       ├── DataEngineOptions.cs
│       └── DataEngineBuilder.cs             ← Fluent builder for optional features
│
├── tests/
│   ├── DataEngine.Core.Tests/               ← Pure unit tests, no DB
│   ├── DataEngine.Infrastructure.Tests/     ← Unit tests with mocks
│   └── DataEngine.Integration.Tests/        ← Real MySQL via TestContainers
│
├── samples/
│   └── DataEngine.Sample.Api/              ← ASP.NET Core minimal API demo
│
└── DataEngine.sln
```

### Dependency Graph (strictly enforced)

```
DataEngine.Core           ← NO dependencies (pure domain)
        ↑
DataEngine.Infrastructure ← MySqlConnector, Dapper, Microsoft.Extensions.*
        ↑
DataEngine.Application    ← references Core + Infrastructure
        ↑
DataEngine.Extensions.DI  ← references Application, exposes AddDataEngine()
```

**Rule:** Core never references Infrastructure. Infrastructure never references Application. This is enforced by `.csproj` references only — no circular deps.

---

## 3. Domain Model Design

### 3.1 Schema Domain

```csharp
// DataEngine.Core/Domain/Schema/ColumnMetadata.cs
namespace DataEngine.Core.Domain.Schema;

public sealed record ColumnMetadata
{
    public required string ColumnName { get; init; }
    public required string DataType { get; init; }
    public required bool IsNullable { get; init; }
    public required bool IsAutoIncrement { get; init; }
    public required bool IsPrimaryKey { get; init; }
    public int? CharacterMaxLength { get; init; }
    public string? ColumnDefault { get; init; }
    public required int OrdinalPosition { get; init; }
    
    // Derived: safe for parameterized use
    public MySqlDbType ResolvedDbType => DataTypeMapper.Resolve(DataType, CharacterMaxLength);
}
```

```csharp
// DataEngine.Core/Domain/Schema/TableMetadata.cs
namespace DataEngine.Core.Domain.Schema;

public sealed record TableMetadata
{
    public required string TableName { get; init; }
    public required string DatabaseName { get; init; }
    public required IReadOnlyDictionary<string, ColumnMetadata> Columns { get; init; }
    public required IReadOnlyList<string> PrimaryKeys { get; init; }
    public DateTimeOffset CachedAt { get; init; } = DateTimeOffset.UtcNow;
    
    public bool HasColumn(string name) =>
        Columns.ContainsKey(name);

    public ColumnMetadata GetColumn(string name) =>
        Columns.TryGetValue(name, out var col)
            ? col
            : throw new SchemaValidationException($"Column '{name}' not found in table '{TableName}'.");
    
    public IEnumerable<ColumnMetadata> WritableColumns =>
        Columns.Values.Where(c => !c.IsAutoIncrement);
    
    public IEnumerable<ColumnMetadata> NonPkWritableColumns =>
        WritableColumns.Where(c => !c.IsPrimaryKey);
}
```

```csharp
// DataEngine.Core/Domain/Schema/SchemaMetadataSnapshot.cs
namespace DataEngine.Core.Domain.Schema;

/// <summary>
/// Immutable point-in-time snapshot of all known tables.
/// Replaced atomically on refresh — never mutated.
/// </summary>
public sealed record SchemaMetadataSnapshot
{
    public required IReadOnlyDictionary<string, TableMetadata> Tables { get; init; }
    public required string DatabaseName { get; init; }
    public DateTimeOffset SnapshotAt { get; init; } = DateTimeOffset.UtcNow;
    
    public bool TableExists(string tableName) =>
        Tables.ContainsKey(tableName.ToLowerInvariant());

    public TableMetadata GetTable(string tableName) =>
        Tables.TryGetValue(tableName.ToLowerInvariant(), out var t)
            ? t
            : throw new TableNotFoundException(tableName, DatabaseName);
}
```

### 3.2 Query Domain

```csharp
// DataEngine.Core/Domain/Query/FilterClause.cs
namespace DataEngine.Core.Domain.Query;

public sealed record FilterClause
{
    public required string Column { get; init; }
    public required FilterOperator Operator { get; init; }
    public required object? Value { get; init; }
    public LogicalOperator Logic { get; init; } = LogicalOperator.And;
}

public enum FilterOperator
{
    Equals, NotEquals,
    GreaterThan, GreaterThanOrEqual,
    LessThan, LessThanOrEqual,
    Like, NotLike,
    In, NotIn,
    IsNull, IsNotNull,
    Between
}

public enum LogicalOperator { And, Or }
```

```csharp
// DataEngine.Core/Domain/Query/CompiledQuery.cs
namespace DataEngine.Core.Domain.Query;

/// <summary>
/// Result of query compilation. Immutable. Safe to cache.
/// </summary>
public sealed record CompiledQuery
{
    public required string Sql { get; init; }
    public required IReadOnlyDictionary<string, object?> Parameters { get; init; }
    public required string TableName { get; init; }
    public DateTimeOffset CompiledAt { get; init; } = DateTimeOffset.UtcNow;
    
    // Cache key for compiled query store
    public string CacheKey { get; init; } = string.Empty;
}
```

### 3.3 Audit Domain

```csharp
// DataEngine.Core/Domain/Audit/AuditEntry.cs
namespace DataEngine.Core.Domain.Audit;

public sealed record AuditEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Operation { get; init; }   // INSERT | UPDATE | DELETE
    public required string TableName { get; init; }
    public required string DatabaseName { get; init; }
    public string? RecordId { get; init; }
    public string? OldValues { get; init; }           // JSON
    public string? NewValues { get; init; }           // JSON
    public string? ExecutedBy { get; init; }
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
}
```

---

## 4. Interface Design (API Surface)

### 4.1 Core Engine Interface

```csharp
// DataEngine.Core/Interfaces/IDataEngine.cs
namespace DataEngine.Core.Interfaces;

/// <summary>
/// Primary entry point for all DataEngine operations.
/// This is what consumer apps inject and use.
/// </summary>
public interface IDataEngine
{
    // ── Reads ──────────────────────────────────────────────────────────
    Task<QueryResponse> QueryAsync(
        QueryRequest request,
        CancellationToken ct = default);
    
    Task<QueryResponse> QueryByDefinitionAsync(
        string definitionKey,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default);
    
    // ── Single-row writes ──────────────────────────────────────────────
    Task<MutationResponse> InsertAsync(
        InsertRequest request,
        CancellationToken ct = default);
    
    Task<MutationResponse> UpdateAsync(
        UpdateRequest request,
        CancellationToken ct = default);
    
    Task<MutationResponse> DeleteAsync(
        DeleteRequest request,
        CancellationToken ct = default);
    
    // ── Bulk operations ────────────────────────────────────────────────
    Task<MutationResponse> BulkInsertAsync(
        BulkInsertRequest request,
        CancellationToken ct = default);
    
    Task<MutationResponse> BulkUpdateAsync(
        BulkUpdateRequest request,
        CancellationToken ct = default);
    
    // ── Transaction scope ──────────────────────────────────────────────
    Task<TransactionResponse> ExecuteTransactionAsync(
        TransactionRequest request,
        CancellationToken ct = default);
    
    // ── Stored procedures ─────────────────────────────────────────────
    Task<ProcedureResponse> ExecuteProcedureAsync(
        ProcedureRequest request,
        CancellationToken ct = default);
    
    // ── Schema operations ─────────────────────────────────────────────
    Task<SchemaResponse> GetSchemaAsync(
        string tableName,
        string? database = null,
        CancellationToken ct = default);
    
    Task InvalidateSchemaAsync(
        string? tableName = null,    // null = full refresh
        string? database = null,
        CancellationToken ct = default);
}
```

### 4.2 Schema Provider Interface

```csharp
// DataEngine.Core/Interfaces/ISchemaProvider.cs
namespace DataEngine.Core.Interfaces;

public interface ISchemaProvider
{
    /// <summary>
    /// Load schema from INFORMATION_SCHEMA for a given database.
    /// Called on first use and on manual invalidation.
    /// </summary>
    Task<SchemaMetadataSnapshot> LoadSchemaAsync(
        string databaseName,
        CancellationToken ct = default);
    
    /// <summary>
    /// Get currently cached snapshot. Never goes to DB.
    /// </summary>
    SchemaMetadataSnapshot? GetCachedSnapshot(string databaseName);
    
    /// <summary>
    /// Ensure a snapshot is loaded (load if not cached).
    /// </summary>
    Task<SchemaMetadataSnapshot> EnsureSnapshotAsync(
        string databaseName,
        CancellationToken ct = default);
}
```

### 4.3 Schema Cache Interface

```csharp
// DataEngine.Core/Interfaces/ISchemaCache.cs
namespace DataEngine.Core.Interfaces;

public interface ISchemaCache
{
    SchemaMetadataSnapshot? Get(string databaseName);
    void Set(string databaseName, SchemaMetadataSnapshot snapshot);
    void Invalidate(string databaseName);
    void InvalidateAll();
    IEnumerable<string> GetCachedDatabases();
}
```

### 4.4 Query Compiler Interface

```csharp
// DataEngine.Core/Interfaces/IQueryCompiler.cs
namespace DataEngine.Core.Interfaces;

public interface IQueryCompiler
{
    CompiledQuery CompileSelect(QueryRequest request, TableMetadata schema);
    CompiledQuery CompileInsert(InsertRequest request, TableMetadata schema);
    CompiledQuery CompileUpdate(UpdateRequest request, TableMetadata schema);
    CompiledQuery CompileDelete(DeleteRequest request, TableMetadata schema);
    CompiledQuery CompileBulkInsert(BulkInsertRequest request, TableMetadata schema);
}
```

### 4.5 Executor Interfaces

```csharp
// DataEngine.Core/Interfaces/IQueryExecutor.cs
namespace DataEngine.Core.Interfaces;

public interface IQueryExecutor
{
    Task<IReadOnlyList<IDictionary<string, object?>>> ExecuteQueryAsync(
        CompiledQuery query,
        string connectionString,
        CancellationToken ct = default);
    
    Task<long> ExecuteCountAsync(
        CompiledQuery query,
        string connectionString,
        CancellationToken ct = default);
}
```

```csharp
// DataEngine.Core/Interfaces/ITransactionExecutor.cs
namespace DataEngine.Core.Interfaces;

public interface ITransactionExecutor
{
    Task<TransactionResult> ExecuteAsync(
        IReadOnlyList<CompiledQuery> queries,
        string connectionString,
        CancellationToken ct = default);
}
```

```csharp
// DataEngine.Core/Interfaces/IProcedureExecutor.cs
namespace DataEngine.Core.Interfaces;

public interface IProcedureExecutor
{
    Task<ProcedureResult> ExecuteReadAsync(
        ProcedureRequest request,
        string connectionString,
        CancellationToken ct = default);
    
    Task<ProcedureResult> ExecuteWriteAsync(
        ProcedureRequest request,
        string connectionString,
        CancellationToken ct = default);
}
```

### 4.6 Field Mapper Interface

```csharp
// DataEngine.Core/Interfaces/IFieldMapperProvider.cs
namespace DataEngine.Core.Interfaces;

public interface IFieldMapperProvider
{
    /// <summary>
    /// Map an incoming field alias to the real column name.
    /// Returns null if no mapping exists (use original key).
    /// </summary>
    ValueTask<string?> ResolveColumnAsync(
        string tableName,
        string alias,
        CancellationToken ct = default);
    
    /// <summary>
    /// Map outgoing column name to alias for response.
    /// Returns null if no mapping (use original column name).
    /// </summary>
    ValueTask<string?> ResolveAliasAsync(
        string tableName,
        string columnName,
        CancellationToken ct = default);
    
    bool IsAvailable { get; }
}
```

### 4.7 Connection Factory Interface

```csharp
// DataEngine.Core/Interfaces/IConnectionFactory.cs
namespace DataEngine.Core.Interfaces;

public interface IConnectionFactory
{
    MySqlConnection CreateConnection(string? database = null);
    string GetConnectionString(string? database = null);
    IReadOnlyList<string> GetRegisteredDatabases();
}
```

### 4.8 Audit Writer Interface

```csharp
// DataEngine.Core/Interfaces/IAuditWriter.cs
namespace DataEngine.Core.Interfaces;

public interface IAuditWriter
{
    ValueTask WriteAsync(AuditEntry entry, CancellationToken ct = default);
    bool IsEnabled { get; }
}
```

---

## 5. Request/Response DTOs & API Contracts

### 5.1 Query Request

```csharp
// DataEngine.Core/Contracts/Requests/QueryRequest.cs
namespace DataEngine.Core.Contracts.Requests;

public sealed record QueryRequest
{
    public required string Table { get; init; }
    public string? Database { get; init; }
    
    // Projections — null = SELECT *
    public IReadOnlyList<string>? Columns { get; init; }
    
    // Filtering
    public IReadOnlyList<FilterClause>? Filters { get; init; }
    
    // Sorting
    public IReadOnlyList<SortClause>? Sort { get; init; }
    
    // Pagination
    public PaginationClause? Pagination { get; init; }
    
    // Joins
    public IReadOnlyList<JoinClause>? Joins { get; init; }
    
    // Optimization hints
    public bool IncludeCount { get; init; } = false;
    public int? CommandTimeoutSeconds { get; init; }
}

public sealed record SortClause
{
    public required string Column { get; init; }
    public SortDirection Direction { get; init; } = SortDirection.Ascending;
}

public enum SortDirection { Ascending, Descending }

public sealed record PaginationClause
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    
    public int Offset => (Page - 1) * PageSize;
    
    public static PaginationClause Default => new() { Page = 1, PageSize = 20 };
}

public sealed record JoinClause
{
    public required string Table { get; init; }
    public required string LeftColumn { get; init; }
    public required string RightColumn { get; init; }
    public JoinType Type { get; init; } = JoinType.Inner;
    public string? Alias { get; init; }
}

public enum JoinType { Inner, Left, Right }
```

### 5.2 Mutation Requests

```csharp
// DataEngine.Core/Contracts/Requests/InsertRequest.cs
namespace DataEngine.Core.Contracts.Requests;

public sealed record InsertRequest
{
    public required string Table { get; init; }
    public string? Database { get; init; }
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
    public bool ReturnInsertedId { get; init; } = true;
    public string? ExecutedBy { get; init; }
    public string? CorrelationId { get; init; }
}
```

```csharp
// DataEngine.Core/Contracts/Requests/UpdateRequest.cs
namespace DataEngine.Core.Contracts.Requests;

public sealed record UpdateRequest
{
    public required string Table { get; init; }
    public string? Database { get; init; }
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
    public required IReadOnlyList<FilterClause> Filters { get; init; }  // REQUIRED — no blind updates
    public bool CaptureOldValues { get; init; } = false;  // for audit
    public string? ExecutedBy { get; init; }
    public string? CorrelationId { get; init; }
}
```

```csharp
// DataEngine.Core/Contracts/Requests/DeleteRequest.cs
namespace DataEngine.Core.Contracts.Requests;

public sealed record DeleteRequest
{
    public required string Table { get; init; }
    public string? Database { get; init; }
    public required IReadOnlyList<FilterClause> Filters { get; init; }  // REQUIRED — no blind deletes
    public string? ExecutedBy { get; init; }
    public string? CorrelationId { get; init; }
}
```

```csharp
// DataEngine.Core/Contracts/Requests/BulkInsertRequest.cs
namespace DataEngine.Core.Contracts.Requests;

public sealed record BulkInsertRequest
{
    public required string Table { get; init; }
    public string? Database { get; init; }
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; init; }
    
    /// <summary>Batch size per transaction. Default 500.</summary>
    public int BatchSize { get; init; } = 500;
    
    public ConflictResolution OnConflict { get; init; } = ConflictResolution.Abort;
    public string? ExecutedBy { get; init; }
}

public enum ConflictResolution
{
    Abort,          // Default: fail the batch
    Ignore,         // INSERT IGNORE
    Replace         // REPLACE INTO (use carefully)
}
```

### 5.3 Transaction Request

```csharp
// DataEngine.Core/Contracts/Requests/TransactionRequest.cs
namespace DataEngine.Core.Contracts.Requests;

public sealed record TransactionRequest
{
    public required IReadOnlyList<TransactionOperation> Operations { get; init; }
    public string? Database { get; init; }
    public string? ExecutedBy { get; init; }
    public string? CorrelationId { get; init; }
    public IsolationLevel IsolationLevel { get; init; } = IsolationLevel.ReadCommitted;
}

public sealed record TransactionOperation
{
    public required OperationType Type { get; init; }
    public required string Table { get; init; }
    public IReadOnlyDictionary<string, object?>? Values { get; init; }
    public IReadOnlyList<FilterClause>? Filters { get; init; }
    public int Order { get; init; }  // execution sequence
}

public enum OperationType { Insert, Update, Delete }
```

### 5.4 Procedure Request

```csharp
// DataEngine.Core/Contracts/Requests/ProcedureRequest.cs
namespace DataEngine.Core.Contracts.Requests;

public sealed record ProcedureRequest
{
    public required string ProcedureName { get; init; }
    public string? Database { get; init; }
    public IReadOnlyList<ProcedureParameter>? Parameters { get; init; }
    public ProcedureType Type { get; init; } = ProcedureType.Read;
    public int? CommandTimeoutSeconds { get; init; }
}

public sealed record ProcedureParameter
{
    public required string Name { get; init; }
    public required object? Value { get; init; }
    public ParameterDirection Direction { get; init; } = ParameterDirection.Input;
    public MySqlDbType? DbType { get; init; }
    public int? Size { get; init; }
}

public enum ProcedureType { Read, Write }
```

### 5.5 Response Types

```csharp
// DataEngine.Core/Contracts/Responses/QueryResponse.cs
namespace DataEngine.Core.Contracts.Responses;

public sealed record QueryResponse
{
    public required IReadOnlyList<IReadOnlyDictionary<string, object?>> Data { get; init; }
    public long? TotalCount { get; init; }
    public int PageCount => Pagination is null ? 1
        : TotalCount is null ? 0
        : (int)Math.Ceiling((double)TotalCount.Value / Pagination.PageSize);
    public PaginationClause? Pagination { get; init; }
    public bool Success { get; init; } = true;
    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    
    public static QueryResponse Empty => new()
    {
        Data = Array.Empty<IReadOnlyDictionary<string, object?>>(),
        Success = true
    };
}
```

```csharp
// DataEngine.Core/Contracts/Responses/MutationResponse.cs
namespace DataEngine.Core.Contracts.Responses;

public sealed record MutationResponse
{
    public bool Success { get; init; }
    public int AffectedRows { get; init; }
    public long? InsertedId { get; init; }     // last_insert_id()
    public IReadOnlyList<long>? InsertedIds { get; init; }  // bulk
    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    
    public static MutationResponse Failed(string error) =>
        new() { Success = false, Error = error };
}
```

```csharp
// DataEngine.Core/Contracts/Responses/ProcedureResponse.cs
namespace DataEngine.Core.Contracts.Responses;

public sealed record ProcedureResponse
{
    public bool Success { get; init; }
    
    /// <summary>Multiple result sets (Dapper QueryMultiple)</summary>
    public IReadOnlyList<IReadOnlyList<IReadOnlyDictionary<string, object?>>>? ResultSets { get; init; }
    
    /// <summary>OUTPUT parameter values keyed by parameter name</summary>
    public IReadOnlyDictionary<string, object?>? OutputParameters { get; init; }
    
    public int? AffectedRows { get; init; }
    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }
}
```

---

## 6. Schema Cache Implementation

### 6.1 In-Memory Schema Cache

```csharp
// DataEngine.Infrastructure/Schema/InMemorySchemaCache.cs
namespace DataEngine.Infrastructure.Schema;

/// <summary>
/// Thread-safe in-memory schema cache.
/// Uses ConcurrentDictionary for lock-free reads.
/// Snapshots are immutable records replaced atomically.
/// </summary>
internal sealed class InMemorySchemaCache : ISchemaCache
{
    private readonly ConcurrentDictionary<string, SchemaMetadataSnapshot> _store = new(
        StringComparer.OrdinalIgnoreCase);
    
    private readonly DataEngineOptions _options;
    private readonly ILogger<InMemorySchemaCache> _logger;
    
    public InMemorySchemaCache(
        DataEngineOptions options,
        ILogger<InMemorySchemaCache> logger)
    {
        _options = options;
        _logger = logger;
    }
    
    public SchemaMetadataSnapshot? Get(string databaseName)
    {
        if (!_store.TryGetValue(databaseName, out var snapshot))
            return null;
        
        // TTL check — treat as miss if expired
        if (_options.SchemaCacheTtl.HasValue &&
            DateTimeOffset.UtcNow - snapshot.SnapshotAt > _options.SchemaCacheTtl.Value)
        {
            _logger.LogDebug(
                "Schema cache expired for {Database} (age: {Age}s). Will reload.",
                databaseName,
                (DateTimeOffset.UtcNow - snapshot.SnapshotAt).TotalSeconds);
            return null;
        }
        
        return snapshot;
    }
    
    public void Set(string databaseName, SchemaMetadataSnapshot snapshot)
    {
        _store[databaseName] = snapshot;
        _logger.LogInformation(
            "Schema cached for {Database}: {TableCount} tables at {Time}",
            databaseName, snapshot.Tables.Count, snapshot.SnapshotAt);
    }
    
    public void Invalidate(string databaseName)
    {
        _store.TryRemove(databaseName, out _);
        _logger.LogInformation("Schema cache invalidated for {Database}", databaseName);
    }
    
    public void InvalidateAll()
    {
        _store.Clear();
        _logger.LogInformation("Full schema cache invalidated");
    }
    
    public IEnumerable<string> GetCachedDatabases() => _store.Keys;
}
```

### 6.2 MySQL Schema Provider

```csharp
// DataEngine.Infrastructure/Schema/MySqlSchemaProvider.cs
namespace DataEngine.Infrastructure.Schema;

internal sealed class MySqlSchemaProvider : ISchemaProvider
{
    private readonly ISchemaCache _cache;
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<MySqlSchemaProvider> _logger;
    
    // SemaphoreSlim per database to prevent thundering herd on cold start
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadLocks = new();
    
    // SQL against INFORMATION_SCHEMA — parameterized, read-only, safe
    private const string ColumnsSql = """
        SELECT
            c.TABLE_NAME         AS TableName,
            c.COLUMN_NAME        AS ColumnName,
            c.DATA_TYPE          AS DataType,
            c.IS_NULLABLE        AS IsNullable,
            c.COLUMN_DEFAULT     AS ColumnDefault,
            c.CHARACTER_MAXIMUM_LENGTH AS CharacterMaxLength,
            c.ORDINAL_POSITION   AS OrdinalPosition,
            CASE WHEN c.EXTRA LIKE '%auto_increment%' THEN 1 ELSE 0 END AS IsAutoIncrement,
            CASE WHEN k.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IsPrimaryKey
        FROM INFORMATION_SCHEMA.COLUMNS c
        LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            ON  k.TABLE_SCHEMA = c.TABLE_SCHEMA
            AND k.TABLE_NAME   = c.TABLE_NAME
            AND k.COLUMN_NAME  = c.COLUMN_NAME
            AND k.CONSTRAINT_NAME = 'PRIMARY'
        WHERE c.TABLE_SCHEMA = @DatabaseName
        ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION;
        """;
    
    public MySqlSchemaProvider(
        ISchemaCache cache,
        IConnectionFactory connectionFactory,
        ILogger<MySqlSchemaProvider> logger)
    {
        _cache = cache;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }
    
    public SchemaMetadataSnapshot? GetCachedSnapshot(string databaseName) =>
        _cache.Get(databaseName);
    
    public async Task<SchemaMetadataSnapshot> EnsureSnapshotAsync(
        string databaseName,
        CancellationToken ct = default)
    {
        var cached = _cache.Get(databaseName);
        if (cached is not null) return cached;
        
        return await LoadSchemaAsync(databaseName, ct);
    }
    
    public async Task<SchemaMetadataSnapshot> LoadSchemaAsync(
        string databaseName,
        CancellationToken ct = default)
    {
        // Per-database load lock — prevents concurrent DB queries for same schema
        var semaphore = _loadLocks.GetOrAdd(databaseName, _ => new SemaphoreSlim(1, 1));
        
        await semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            var cached = _cache.Get(databaseName);
            if (cached is not null) return cached;
            
            _logger.LogInformation("Loading schema from INFORMATION_SCHEMA for {Database}", databaseName);
            
            await using var conn = _connectionFactory.CreateConnection(databaseName);
            await conn.OpenAsync(ct);
            
            // Use Dapper for schema read (safe — parameterized, not user input)
            var rows = await conn.QueryAsync<SchemaRow>(
                ColumnsSql,
                new { DatabaseName = databaseName },
                commandTimeout: 30);
            
            var snapshot = BuildSnapshot(databaseName, rows);
            _cache.Set(databaseName, snapshot);
            
            return snapshot;
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    private static SchemaMetadataSnapshot BuildSnapshot(
        string databaseName,
        IEnumerable<SchemaRow> rows)
    {
        var tables = rows
            .GroupBy(r => r.TableName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var columns = g
                    .Select(r => new ColumnMetadata
                    {
                        ColumnName = r.ColumnName,
                        DataType = r.DataType,
                        IsNullable = r.IsNullable == "YES",
                        IsAutoIncrement = r.IsAutoIncrement,
                        IsPrimaryKey = r.IsPrimaryKey,
                        CharacterMaxLength = r.CharacterMaxLength,
                        ColumnDefault = r.ColumnDefault,
                        OrdinalPosition = r.OrdinalPosition
                    })
                    .ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
                
                var primaryKeys = columns.Values
                    .Where(c => c.IsPrimaryKey)
                    .OrderBy(c => c.OrdinalPosition)
                    .Select(c => c.ColumnName)
                    .ToList();
                
                return new TableMetadata
                {
                    TableName = g.Key,
                    DatabaseName = databaseName,
                    Columns = columns,
                    PrimaryKeys = primaryKeys
                };
            })
            .ToDictionary(t => t.TableName, StringComparer.OrdinalIgnoreCase);
        
        return new SchemaMetadataSnapshot
        {
            DatabaseName = databaseName,
            Tables = tables
        };
    }
    
    // Internal DTO for Dapper — never exposed
    private sealed class SchemaRow
    {
        public string TableName { get; set; } = default!;
        public string ColumnName { get; set; } = default!;
        public string DataType { get; set; } = default!;
        public string IsNullable { get; set; } = default!;
        public string? ColumnDefault { get; set; }
        public int? CharacterMaxLength { get; set; }
        public int OrdinalPosition { get; set; }
        public bool IsAutoIncrement { get; set; }
        public bool IsPrimaryKey { get; set; }
    }
}
```

---

## 7. Service Implementations

### 7.1 MySQL Query Compiler

```csharp
// DataEngine.Infrastructure/Query/MySqlQueryCompiler.cs
namespace DataEngine.Infrastructure.Query;

/// <summary>
/// Compiles validated requests into parameterized SQL.
/// All SQL output uses backtick-quoted identifiers to handle reserved words.
/// Parameters are @p_columnname_index to avoid collisions.
/// </summary>
internal sealed class MySqlQueryCompiler : IQueryCompiler
{
    private readonly TableGuard _tableGuard;
    private readonly ColumnGuard _columnGuard;
    
    public MySqlQueryCompiler(TableGuard tableGuard, ColumnGuard columnGuard)
    {
        _tableGuard = tableGuard;
        _columnGuard = columnGuard;
    }
    
    public CompiledQuery CompileSelect(QueryRequest request, TableMetadata schema)
    {
        var sb = new StringBuilder(256);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        
        // SELECT clause
        sb.Append("SELECT ");
        AppendProjection(sb, request.Columns, schema);
        
        // FROM clause
        sb.Append($"\nFROM `{schema.DatabaseName}`.`{schema.TableName}`");
        
        // JOIN clauses
        if (request.Joins?.Count > 0)
            AppendJoins(sb, request.Joins, schema);
        
        // WHERE clause
        if (request.Filters?.Count > 0)
            AppendWhere(sb, request.Filters, schema, parameters);
        
        // ORDER BY clause
        if (request.Sort?.Count > 0)
            AppendOrderBy(sb, request.Sort, schema);
        
        // LIMIT / OFFSET
        if (request.Pagination is not null)
            AppendPagination(sb, request.Pagination, parameters);
        
        return new CompiledQuery
        {
            Sql = sb.ToString(),
            Parameters = parameters,
            TableName = schema.TableName
        };
    }
    
    public CompiledQuery CompileInsert(InsertRequest request, TableMetadata schema)
    {
        var writableValues = request.Values
            .Where(kv => schema.HasColumn(kv.Key) &&
                         !schema.GetColumn(kv.Key).IsAutoIncrement)
            .ToList();
        
        if (writableValues.Count == 0)
            throw new DataEngineException("No writable columns found in insert request.");
        
        var columns = writableValues.Select(kv => $"`{kv.Key}`");
        var paramNames = writableValues.Select((kv, i) => $"@p_{i}");
        var parameters = writableValues
            .Select((kv, i) => ($"p_{i}", kv.Value))
            .ToDictionary(t => t.Item1, t => t.Value);
        
        var sql = $"""
            INSERT INTO `{schema.DatabaseName}`.`{schema.TableName}`
            ({string.Join(", ", columns)})
            VALUES ({string.Join(", ", paramNames)});
            SELECT LAST_INSERT_ID();
            """;
        
        return new CompiledQuery { Sql = sql, Parameters = parameters, TableName = schema.TableName };
    }
    
    public CompiledQuery CompileUpdate(UpdateRequest request, TableMetadata schema)
    {
        var sb = new StringBuilder(256);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        
        sb.Append($"UPDATE `{schema.DatabaseName}`.`{schema.TableName}` SET ");
        
        var setClauses = new List<string>();
        int paramIndex = 0;
        foreach (var (key, value) in request.Values)
        {
            _columnGuard.AssertWritable(key, schema);
            var paramName = $"p_set_{paramIndex++}";
            setClauses.Add($"`{key}` = @{paramName}");
            parameters[paramName] = value;
        }
        sb.Append(string.Join(", ", setClauses));
        
        AppendWhere(sb, request.Filters, schema, parameters, startIndex: paramIndex);
        
        return new CompiledQuery { Sql = sb.ToString(), Parameters = parameters, TableName = schema.TableName };
    }
    
    public CompiledQuery CompileDelete(DeleteRequest request, TableMetadata schema)
    {
        var sb = new StringBuilder(128);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        
        sb.Append($"DELETE FROM `{schema.DatabaseName}`.`{schema.TableName}`");
        AppendWhere(sb, request.Filters, schema, parameters);
        
        return new CompiledQuery { Sql = sb.ToString(), Parameters = parameters, TableName = schema.TableName };
    }
    
    public CompiledQuery CompileBulkInsert(BulkInsertRequest request, TableMetadata schema)
    {
        if (request.Rows.Count == 0)
            throw new DataEngineException("BulkInsert requires at least one row.");
        
        // Derive column set from first row (all rows must share same columns)
        var firstRow = request.Rows[0];
        var writableKeys = firstRow.Keys
            .Where(k => schema.HasColumn(k) && !schema.GetColumn(k).IsAutoIncrement)
            .ToList();
        
        var columns = string.Join(", ", writableKeys.Select(k => $"`{k}`"));
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var valueSets = new List<string>(request.Rows.Count);
        
        for (int rowIdx = 0; rowIdx < request.Rows.Count; rowIdx++)
        {
            var paramNames = writableKeys
                .Select((k, colIdx) =>
                {
                    var pName = $"r{rowIdx}_c{colIdx}";
                    parameters[pName] = request.Rows[rowIdx].TryGetValue(k, out var v) ? v : null;
                    return $"@{pName}";
                });
            valueSets.Add($"({string.Join(", ", paramNames)})");
        }
        
        var verb = request.OnConflict switch
        {
            ConflictResolution.Ignore => "INSERT IGNORE",
            ConflictResolution.Replace => "REPLACE",
            _ => "INSERT"
        };
        
        var sql = $"""
            {verb} INTO `{schema.DatabaseName}`.`{schema.TableName}`
            ({columns})
            VALUES {string.Join(",\n", valueSets)};
            """;
        
        return new CompiledQuery { Sql = sql, Parameters = parameters, TableName = schema.TableName };
    }
    
    // ── Private helpers ────────────────────────────────────────────────
    
    private void AppendProjection(StringBuilder sb, IReadOnlyList<string>? columns, TableMetadata schema)
    {
        if (columns is null || columns.Count == 0)
        {
            // SELECT all — use explicit column list (no SELECT *)
            sb.Append(string.Join(", ",
                schema.Columns.Values
                    .OrderBy(c => c.OrdinalPosition)
                    .Select(c => $"`{schema.TableName}`.`{c.ColumnName}`")));
        }
        else
        {
            foreach (var col in columns)
                _columnGuard.AssertExists(col, schema);
            
            sb.Append(string.Join(", ", columns.Select(c => $"`{schema.TableName}`.`{c}`")));
        }
    }
    
    private void AppendWhere(
        StringBuilder sb,
        IReadOnlyList<FilterClause> filters,
        TableMetadata schema,
        Dictionary<string, object?> parameters,
        int startIndex = 0)
    {
        sb.Append("\nWHERE ");
        int idx = startIndex;
        
        for (int i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            _columnGuard.AssertExists(filter.Column, schema);
            
            if (i > 0)
                sb.Append(filter.Logic == LogicalOperator.Or ? " OR " : " AND ");
            
            AppendFilterExpression(sb, filter, parameters, ref idx);
        }
    }
    
    private static void AppendFilterExpression(
        StringBuilder sb,
        FilterClause filter,
        Dictionary<string, object?> parameters,
        ref int idx)
    {
        var col = $"`{filter.Column}`";
        
        switch (filter.Operator)
        {
            case FilterOperator.IsNull:
                sb.Append($"{col} IS NULL");
                break;
            case FilterOperator.IsNotNull:
                sb.Append($"{col} IS NOT NULL");
                break;
            case FilterOperator.In:
            case FilterOperator.NotIn:
            {
                var values = (IEnumerable<object?>)filter.Value!;
                var paramNames = values.Select(v =>
                {
                    var pName = $"p_f_{idx++}";
                    parameters[pName] = v;
                    return $"@{pName}";
                }).ToList();
                var not = filter.Operator == FilterOperator.NotIn ? "NOT " : "";
                sb.Append($"{col} {not}IN ({string.Join(", ", paramNames)})");
                break;
            }
            case FilterOperator.Between:
            {
                var between = (object?[])filter.Value!;
                var p1 = $"p_f_{idx++}"; var p2 = $"p_f_{idx++}";
                parameters[p1] = between[0]; parameters[p2] = between[1];
                sb.Append($"{col} BETWEEN @{p1} AND @{p2}");
                break;
            }
            default:
            {
                var op = filter.Operator switch
                {
                    FilterOperator.Equals => "=",
                    FilterOperator.NotEquals => "<>",
                    FilterOperator.GreaterThan => ">",
                    FilterOperator.GreaterThanOrEqual => ">=",
                    FilterOperator.LessThan => "<",
                    FilterOperator.LessThanOrEqual => "<=",
                    FilterOperator.Like => "LIKE",
                    FilterOperator.NotLike => "NOT LIKE",
                    _ => throw new DataEngineException($"Unknown operator: {filter.Operator}")
                };
                var pName = $"p_f_{idx++}";
                parameters[pName] = filter.Value;
                sb.Append($"{col} {op} @{pName}");
                break;
            }
        }
    }
    
    private void AppendOrderBy(StringBuilder sb, IReadOnlyList<SortClause> sort, TableMetadata schema)
    {
        sb.Append("\nORDER BY ");
        sb.Append(string.Join(", ", sort.Select(s =>
        {
            _columnGuard.AssertExists(s.Column, schema);
            return $"`{s.Column}` {(s.Direction == SortDirection.Descending ? "DESC" : "ASC")}";
        })));
    }
    
    private static void AppendPagination(
        StringBuilder sb, PaginationClause pagination,
        Dictionary<string, object?> parameters)
    {
        parameters["_limit"] = pagination.PageSize;
        parameters["_offset"] = pagination.Offset;
        sb.Append("\nLIMIT @_limit OFFSET @_offset");
    }
    
    private static void AppendJoins(StringBuilder sb, IReadOnlyList<JoinClause> joins, TableMetadata schema)
    {
        foreach (var join in joins)
        {
            var joinType = join.Type switch
            {
                JoinType.Left => "LEFT JOIN",
                JoinType.Right => "RIGHT JOIN",
                _ => "INNER JOIN"
            };
            var alias = join.Alias is not null ? $" AS `{join.Alias}`" : "";
            sb.Append($"\n{joinType} `{join.Table}`{alias} ON `{schema.TableName}`.`{join.LeftColumn}` = `{join.Table}`.`{join.RightColumn}`");
        }
    }
}
```

### 7.2 Security Guards

```csharp
// DataEngine.Infrastructure/Security/TableGuard.cs
namespace DataEngine.Infrastructure.Security;

internal sealed class TableGuard
{
    private static readonly HashSet<string> ForbiddenDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "mysql", "information_schema", "performance_schema", "sys"
    };
    
    public void AssertAllowed(string tableName, string databaseName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new SecurityViolationException("Table name cannot be empty.");
        
        if (ForbiddenDatabases.Contains(databaseName))
            throw new SecurityViolationException(
                $"Access to system database '{databaseName}' is forbidden.");
        
        // Prevent any injection attempts via table name
        if (tableName.Contains('`') || tableName.Contains(';') ||
            tableName.Contains(' ') || tableName.Contains('-'))
            throw new SecurityViolationException(
                $"Table name '{tableName}' contains invalid characters.");
    }
    
    public void AssertExists(string tableName, SchemaMetadataSnapshot snapshot)
    {
        if (!snapshot.TableExists(tableName))
            throw new TableNotFoundException(tableName, snapshot.DatabaseName);
    }
}
```

```csharp
// DataEngine.Infrastructure/Security/ColumnGuard.cs
namespace DataEngine.Infrastructure.Security;

internal sealed class ColumnGuard
{
    public void AssertExists(string columnName, TableMetadata schema)
    {
        if (!schema.HasColumn(columnName))
            throw new SchemaValidationException(
                $"Column '{columnName}' does not exist in table '{schema.TableName}'.");
    }
    
    public void AssertWritable(string columnName, TableMetadata schema)
    {
        AssertExists(columnName, schema);
        var col = schema.GetColumn(columnName);
        if (col.IsAutoIncrement)
            throw new SchemaValidationException(
                $"Column '{columnName}' is auto-increment and cannot be written explicitly.");
    }
}
```

### 7.3 Dapper Query Executor

```csharp
// DataEngine.Infrastructure/Query/DapperQueryExecutor.cs
namespace DataEngine.Infrastructure.Query;

internal sealed class DapperQueryExecutor : IQueryExecutor
{
    private readonly ILogger<DapperQueryExecutor> _logger;
    
    public DapperQueryExecutor(ILogger<DapperQueryExecutor> logger)
    {
        _logger = logger;
    }
    
    public async Task<IReadOnlyList<IDictionary<string, object?>>> ExecuteQueryAsync(
        CompiledQuery query,
        string connectionString,
        CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        
        _logger.LogDebug("Executing SELECT on {Table}: {Sql}", query.TableName, query.Sql);
        
        // Dapper returns IDictionary<string, object> for dynamic mapping
        var results = await conn.QueryAsync(
            query.Sql,
            query.Parameters.Count > 0 ? query.Parameters : null,
            commandType: CommandType.Text);
        
        return results
            .Select(row => (IDictionary<string, object?>)
                ((IDictionary<string, object>)row)
                .ToDictionary(k => k.Key, k => (object?)k.Value))
            .ToList()
            .AsReadOnly();
    }
    
    public async Task<long> ExecuteCountAsync(
        CompiledQuery query,
        string connectionString,
        CancellationToken ct = default)
    {
        // Wrap existing query as subquery for COUNT — avoids recompilation
        var countSql = $"SELECT COUNT(*) FROM ({query.Sql}) AS _count_wrapper";
        
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        
        return await conn.ExecuteScalarAsync<long>(
            countSql,
            query.Parameters.Count > 0 ? query.Parameters : null);
    }
}
```

### 7.4 ADO.NET Write Executor

```csharp
// DataEngine.Infrastructure/Write/AdoNetWriteExecutor.cs
namespace DataEngine.Infrastructure.Write;

internal sealed class AdoNetWriteExecutor
{
    private readonly ILogger<AdoNetWriteExecutor> _logger;
    
    public AdoNetWriteExecutor(ILogger<AdoNetWriteExecutor> logger)
    {
        _logger = logger;
    }
    
    public async Task<MutationResult> ExecuteInsertAsync(
        CompiledQuery query,
        string connectionString,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = query.Sql;
        cmd.CommandType = CommandType.Text;
        BindParameters(cmd, query.Parameters);
        
        long insertedId = 0;
        int affectedRows = 0;
        
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        affectedRows = reader.RecordsAffected;
        
        if (await reader.ReadAsync(ct))
            insertedId = reader.GetInt64(0);
        
        sw.Stop();
        _logger.LogDebug("INSERT on {Table}: {AffectedRows} row(s), id={Id} in {Ms}ms",
            query.TableName, affectedRows, insertedId, sw.ElapsedMilliseconds);
        
        return new MutationResult(affectedRows, insertedId);
    }
    
    public async Task<int> ExecuteNonQueryAsync(
        CompiledQuery query,
        string connectionString,
        CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = query.Sql;
        cmd.CommandType = CommandType.Text;
        BindParameters(cmd, query.Parameters);
        
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    
    /// <summary>
    /// Execute multiple commands within one transaction.
    /// Rolls back all on any failure.
    /// </summary>
    public async Task<TransactionResult> ExecuteTransactionAsync(
        IReadOnlyList<CompiledQuery> queries,
        string connectionString,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var txn = await conn.BeginTransactionAsync(isolationLevel, ct);
        
        var results = new List<int>(queries.Count);
        
        try
        {
            foreach (var query in queries)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = txn;
                cmd.CommandText = query.Sql;
                BindParameters(cmd, query.Parameters);
                results.Add(await cmd.ExecuteNonQueryAsync(ct));
            }
            
            await txn.CommitAsync(ct);
            return TransactionResult.Success(results);
        }
        catch (Exception ex)
        {
            await txn.RollbackAsync(ct);
            _logger.LogError(ex, "Transaction rolled back after executing {Count}/{Total} queries",
                results.Count, queries.Count);
            return TransactionResult.Failed(ex.Message);
        }
    }
    
    private static void BindParameters(MySqlCommand cmd, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = $"@{name}";
            param.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(param);
        }
    }
}
```

### 7.5 Procedure Executor

```csharp
// DataEngine.Infrastructure/Procedure/MySqlProcedureExecutor.cs
namespace DataEngine.Infrastructure.Procedure;

internal sealed class MySqlProcedureExecutor : IProcedureExecutor
{
    private readonly ILogger<MySqlProcedureExecutor> _logger;
    
    public MySqlProcedureExecutor(ILogger<MySqlProcedureExecutor> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Use Dapper for read procedures — handles multiple result sets cleanly.
    /// </summary>
    public async Task<ProcedureResult> ExecuteReadAsync(
        ProcedureRequest request,
        string connectionString,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        
        var dp = new DynamicParameters();
        foreach (var p in request.Parameters ?? [])
        {
            if (p.Direction == ParameterDirection.Input)
                dp.Add(p.Name, p.Value);
            else
                dp.Add(p.Name, p.Value, p.DbType,
                    direction: p.Direction, size: p.Size);
        }
        
        using var multi = await conn.QueryMultipleAsync(
            request.ProcedureName,
            dp,
            commandType: CommandType.StoredProcedure,
            commandTimeout: request.CommandTimeoutSeconds ?? 30);
        
        var resultSets = new List<IReadOnlyList<IReadOnlyDictionary<string, object?>>>();
        
        while (!multi.IsConsumed)
        {
            var rows = (await multi.ReadAsync())
                .Select(row => (IReadOnlyDictionary<string, object?>)
                    ((IDictionary<string, object>)row)
                    .ToDictionary(k => k.Key, k => (object?)k.Value)
                    .AsReadOnly())
                .ToList()
                .AsReadOnly();
            resultSets.Add(rows);
        }
        
        // Collect OUTPUT parameters
        var outputParams = request.Parameters?
            .Where(p => p.Direction != ParameterDirection.Input)
            .ToDictionary(p => p.Name, p => dp.Get<object?>(p.Name));
        
        sw.Stop();
        return new ProcedureResult
        {
            Success = true,
            ResultSets = resultSets,
            OutputParameters = outputParams,
            ExecutionTime = sw.Elapsed
        };
    }
    
    /// <summary>
    /// Use ADO.NET for write procedures — full control over transaction scope.
    /// </summary>
    public async Task<ProcedureResult> ExecuteWriteAsync(
        ProcedureRequest request,
        string connectionString,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = request.ProcedureName;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandTimeout = request.CommandTimeoutSeconds ?? 30;
        
        var outputParamMap = new Dictionary<string, MySqlParameter>();
        
        foreach (var p in request.Parameters ?? [])
        {
            var mp = cmd.CreateParameter();
            mp.ParameterName = p.Name.StartsWith("@") ? p.Name : $"@{p.Name}";
            mp.Value = p.Value ?? DBNull.Value;
            mp.Direction = p.Direction;
            if (p.DbType.HasValue) mp.MySqlDbType = p.DbType.Value;
            if (p.Size.HasValue) mp.Size = p.Size.Value;
            cmd.Parameters.Add(mp);
            
            if (p.Direction != ParameterDirection.Input)
                outputParamMap[p.Name] = mp;
        }
        
        int affectedRows = await cmd.ExecuteNonQueryAsync(ct);
        
        var outputValues = outputParamMap
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Value == DBNull.Value ? (object?)null : kv.Value.Value);
        
        sw.Stop();
        return new ProcedureResult
        {
            Success = true,
            AffectedRows = affectedRows,
            OutputParameters = outputValues,
            ExecutionTime = sw.Elapsed
        };
    }
}
```

---

## 8. Query Compilation Strategy

### Compilation Pipeline

```
QueryRequest
     │
     ▼
[1] Security Guard    ← AssertTableAllowed(databaseName)
     │
     ▼
[2] Schema Resolution ← EnsureSnapshotAsync(databaseName)
     │
     ▼
[3] Field Mapping     ← ResolveColumnAsync() for each field alias
     │
     ▼
[4] Schema Validation ← AssertColumnExists() for all referenced columns
     │
     ▼
[5] Query Compilation ← MySqlQueryCompiler.CompileSelect()
     │
     ▼
[6] Query Cache Check ← Hash(table + columns + filters + sort + pagination)
     │
     ▼
[7] Execution         ← DapperQueryExecutor / AdoNetWriteExecutor
     │
     ▼
[8] Field Un-mapping  ← ResolveAliasAsync() for response columns (optional)
     │
     ▼
QueryResponse
```

### Query Cache Key Strategy

```csharp
// DataEngine.Infrastructure/Query/QueryCacheKeyBuilder.cs
namespace DataEngine.Infrastructure.Query;

internal static class QueryCacheKeyBuilder
{
    /// <summary>
    /// Deterministic cache key for compiled queries.
    /// Only structural elements — NOT values (values are parameters, never in cache key).
    /// </summary>
    public static string BuildSelectKey(QueryRequest req)
    {
        var sb = new StringBuilder(128);
        sb.Append($"SEL:{req.Database}:{req.Table}");
        
        if (req.Columns?.Count > 0)
            sb.Append($"|cols:{string.Join(",", req.Columns.OrderBy(c => c))}");
        
        if (req.Filters?.Count > 0)
            sb.Append($"|filters:{string.Join(",", req.Filters.Select(f => $"{f.Column}{f.Operator}"))}");
        
        if (req.Sort?.Count > 0)
            sb.Append($"|sort:{string.Join(",", req.Sort.Select(s => $"{s.Column}{s.Direction}"))}");
        
        if (req.Joins?.Count > 0)
            sb.Append($"|joins:{string.Join(",", req.Joins.Select(j => $"{j.Table}:{j.Type}"))}");
        
        if (req.Pagination is not null)
            sb.Append("|paged");
        
        return sb.ToString();
    }
}
```

**Architect's Note:** Cache the *structure* (SQL template), not the *values* (which are always parameterized). A `SELECT * FROM orders WHERE status = @p_f_0 LIMIT @_limit OFFSET @_offset` is cached. The values `'pending'`, `20`, `0` are never in the cache key, only the structural shape.

---

## 9. Transaction Orchestration

```csharp
// DataEngine.Application/WriteOrchestrator.cs
namespace DataEngine.Application;

internal sealed class WriteOrchestrator
{
    private readonly ISchemaProvider _schema;
    private readonly IQueryCompiler _compiler;
    private readonly AdoNetWriteExecutor _executor;
    private readonly IFieldMapperProvider _fieldMapper;
    private readonly IAuditWriter _audit;
    private readonly IConnectionFactory _connFactory;
    private readonly TableGuard _tableGuard;
    
    public WriteOrchestrator( /* inject all above */ ) { /* assign */ }
    
    public async Task<TransactionResponse> ExecuteTransactionAsync(
        TransactionRequest request,
        CancellationToken ct)
    {
        var database = request.Database
            ?? _connFactory.GetConnectionString().ExtractDatabase();
        
        // Sort by Order property before executing
        var orderedOps = request.Operations
            .OrderBy(op => op.Order)
            .ToList();
        
        // Pre-validate ALL operations before touching the DB
        var compiledQueries = new List<CompiledQuery>(orderedOps.Count);
        
        foreach (var op in orderedOps)
        {
            _tableGuard.AssertAllowed(op.Table, database);
            var snapshot = await _schema.EnsureSnapshotAsync(database, ct);
            var tableSchema = snapshot.GetTable(op.Table);
            
            CompiledQuery compiled = op.Type switch
            {
                OperationType.Insert => _compiler.CompileInsert(
                    new InsertRequest { Table = op.Table, Database = database, Values = op.Values! },
                    tableSchema),
                OperationType.Update => _compiler.CompileUpdate(
                    new UpdateRequest { Table = op.Table, Database = database,
                        Values = op.Values!, Filters = op.Filters! },
                    tableSchema),
                OperationType.Delete => _compiler.CompileDelete(
                    new DeleteRequest { Table = op.Table, Database = database,
                        Filters = op.Filters! },
                    tableSchema),
                _ => throw new DataEngineException($"Unknown operation type: {op.Type}")
            };
            
            compiledQueries.Add(compiled);
        }
        
        // Execute all as one DB transaction
        var connString = _connFactory.GetConnectionString(database);
        var result = await _executor.ExecuteTransactionAsync(
            compiledQueries, connString, request.IsolationLevel, ct);
        
        // Write audit if enabled
        if (_audit.IsEnabled && result.Success)
        {
            foreach (var op in orderedOps)
            {
                await _audit.WriteAsync(new AuditEntry
                {
                    Operation = op.Type.ToString().ToUpper(),
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
        
        return new TransactionResponse
        {
            Success = result.Success,
            OperationsExecuted = result.Success ? orderedOps.Count : result.SucceededCount,
            Error = result.Error
        };
    }
}
```

---

## 10. Procedure Execution Implementation

See Section 7.5 for the core executor. Here is the orchestration layer:

```csharp
// DataEngine.Application/ProcedureOrchestrator.cs
namespace DataEngine.Application;

internal sealed class ProcedureOrchestrator
{
    private readonly IProcedureExecutor _executor;
    private readonly IConnectionFactory _connFactory;
    private readonly DataEngineOptions _options;
    
    public async Task<ProcedureResponse> ExecuteAsync(
        ProcedureRequest request,
        CancellationToken ct)
    {
        if (!_options.AllowStoredProcedures)
            throw new DataEngineException(
                "Stored procedure execution is disabled. " +
                "Set DataEngineOptions.AllowStoredProcedures = true to enable.");
        
        // Validate procedure name — no injection via proc name
        if (request.ProcedureName.Contains(';') ||
            request.ProcedureName.Contains(' '))
            throw new SecurityViolationException(
                $"Invalid procedure name: '{request.ProcedureName}'");
        
        var connString = _connFactory.GetConnectionString(request.Database);
        var sw = Stopwatch.StartNew();
        
        try
        {
            var result = request.Type == ProcedureType.Read
                ? await _executor.ExecuteReadAsync(request, connString, ct)
                : await _executor.ExecuteWriteAsync(request, connString, ct);
            
            sw.Stop();
            
            return new ProcedureResponse
            {
                Success = result.Success,
                ResultSets = result.ResultSets,
                OutputParameters = result.OutputParameters,
                AffectedRows = result.AffectedRows,
                ExecutionTime = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ProcedureResponse
            {
                Success = false,
                Error = ex.Message,
                ExecutionTime = sw.Elapsed
            };
        }
    }
}
```

---

## 11. DI Registration & Plug-and-Play Setup

### 11.1 Options Model

```csharp
// DataEngine.Extensions.DependencyInjection/DataEngineOptions.cs
namespace DataEngine.Extensions.DependencyInjection;

public sealed class DataEngineOptions
{
    // ── Connection ─────────────────────────────────────────────────────
    
    /// <summary>Default connection string key in IConfiguration.</summary>
    public string DefaultConnectionStringName { get; set; } = "DefaultConnection";
    
    /// <summary>Named additional databases. Key = alias, Value = conn string name.</summary>
    public Dictionary<string, string> AdditionalDatabases { get; set; } = [];
    
    // ── Schema cache ───────────────────────────────────────────────────
    
    public TimeSpan? SchemaCacheTtl { get; set; } = TimeSpan.FromMinutes(30);
    
    /// <summary>Preload schema for default database at startup.</summary>
    public bool PreloadSchemaOnStartup { get; set; } = true;
    
    // ── Security ───────────────────────────────────────────────────────
    
    /// <summary>Allow raw SQL. FALSE by default — never set true in production.</summary>
    public bool AllowRawSql { get; set; } = false;
    
    public bool AllowStoredProcedures { get; set; } = true;
    
    // ── Optional features ──────────────────────────────────────────────
    
    public bool EnableAudit { get; set; } = false;
    public bool EnableFieldMapping { get; set; } = false;
    public bool EnableSavedQueryDefinitions { get; set; } = false;
    
    // ── Performance ────────────────────────────────────────────────────
    
    public int DefaultCommandTimeoutSeconds { get; set; } = 30;
    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 1000;
    public int BulkInsertBatchSize { get; set; } = 500;
}
```

### 11.2 Service Registration

```csharp
// DataEngine.Extensions.DependencyInjection/DataEngineServiceCollectionExtensions.cs
namespace DataEngine.Extensions.DependencyInjection;

public static class DataEngineServiceCollectionExtensions
{
    /// <summary>
    /// Add DataEngine with default configuration.
    /// Reads ConnectionStrings:DefaultConnection from appsettings.
    /// </summary>
    public static IServiceCollection AddDataEngine(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddDataEngine(configuration, _ => { });
    }
    
    /// <summary>
    /// Add DataEngine with fluent configuration.
    /// </summary>
    public static IServiceCollection AddDataEngine(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<DataEngineOptions> configure)
    {
        var options = new DataEngineOptions();
        configuration.GetSection("DataEngine").Bind(options);
        configure(options);
        
        services.AddSingleton(options);
        services.AddMemoryCache();
        
        // Core infrastructure
        services.AddSingleton<ISchemaCache, InMemorySchemaCache>();
        services.AddSingleton<IConnectionFactory>(sp =>
            new MySqlConnectionFactory(
                configuration,
                sp.GetRequiredService<DataEngineOptions>()));
        
        services.AddScoped<ISchemaProvider, MySqlSchemaProvider>();
        services.AddScoped<IQueryCompiler, MySqlQueryCompiler>();
        services.AddScoped<IQueryExecutor, DapperQueryExecutor>();
        services.AddScoped<AdoNetWriteExecutor>();
        services.AddScoped<IProcedureExecutor, MySqlProcedureExecutor>();
        
        // Security
        services.AddSingleton<TableGuard>();
        services.AddSingleton<ColumnGuard>();
        
        // Optional: Audit
        if (options.EnableAudit)
            services.AddScoped<IAuditWriter, TableAuditWriter>();
        else
            services.AddSingleton<IAuditWriter, NullAuditWriter>();
        
        // Optional: Field mapping
        if (options.EnableFieldMapping)
            services.AddScoped<IFieldMapperProvider, FieldMapperProvider>();
        else
            services.AddSingleton<IFieldMapperProvider, NullFieldMapperProvider>();
        
        // Orchestrators
        services.AddScoped<QueryOrchestrator>();
        services.AddScoped<WriteOrchestrator>();
        services.AddScoped<ProcedureOrchestrator>();
        
        // Main entry point
        services.AddScoped<IDataEngine, DataEngineService>();
        
        // Background schema preload
        if (options.PreloadSchemaOnStartup)
            services.AddHostedService<SchemaRefreshService>();
        
        return services;
    }
}
```

### 11.3 Consumer Setup (complete)

```csharp
// ConsumerApp/Program.cs — THE ENTIRE SETUP REQUIRED

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataEngine(builder.Configuration);

// Optional — override defaults:
// builder.Services.AddDataEngine(builder.Configuration, opt =>
// {
//     opt.EnableAudit = true;
//     opt.EnableFieldMapping = true;
//     opt.SchemaCacheTtl = TimeSpan.FromHours(1);
// });

var app = builder.Build();
app.Run();
```

```json
// appsettings.json — THE ENTIRE CONFIG REQUIRED
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=myapp;User=app;Password=secret;"
  }
}
```

That is the complete setup. Two lines of code and one config key.

### 11.4 Consumer Usage Examples

```csharp
// In a minimal API or controller:
app.MapGet("/orders", async (IDataEngine engine) =>
{
    var response = await engine.QueryAsync(new QueryRequest
    {
        Table = "orders",
        Filters = [new FilterClause
        {
            Column = "status",
            Operator = FilterOperator.Equals,
            Value = "pending"
        }],
        Sort = [new SortClause { Column = "created_at", Direction = SortDirection.Descending }],
        Pagination = new PaginationClause { Page = 1, PageSize = 25 },
        IncludeCount = true
    });
    return Results.Ok(response);
});

app.MapPost("/orders", async (IDataEngine engine, CreateOrderDto dto) =>
{
    var response = await engine.InsertAsync(new InsertRequest
    {
        Table = "orders",
        Values = new Dictionary<string, object?>
        {
            ["customer_id"] = dto.CustomerId,
            ["total_amount"] = dto.TotalAmount,
            ["status"] = "pending",
            ["created_at"] = DateTime.UtcNow
        },
        ExecutedBy = "api-user"
    });
    return Results.Created($"/orders/{response.InsertedId}", response);
});
```

---

## 12. Security Model

### Security Decision Matrix

| Threat | Mitigation | Enforcement Level |
|--------|-----------|-------------------|
| SQL Injection via table name | Schema cache lookup — table must exist | Hard block |
| SQL Injection via column name | Schema cache lookup — column must exist | Hard block |
| SQL Injection via values | All values parameterized via `@param` | Always applied |
| System table access | Blocklist: mysql, information_schema, etc. | Hard block |
| Invalid identifier chars | Regex/char validation on table/column names | Hard block |
| Blind DELETE/UPDATE | Filters required, min 1 clause enforced | API contract |
| Raw SQL execution | Disabled by default, opt-in flag required | Default off |
| Auto-increment write | ColumnGuard rejects explicitly | Always applied |
| Excessive page size | MaxPageSize enforced in QueryOrchestrator | Configurable cap |

### Null Filter Guard (for UPDATE and DELETE)

```csharp
// In WriteOrchestrator, before compilation:
if (request.Filters is null || request.Filters.Count == 0)
    throw new DataEngineException(
        "UPDATE/DELETE without filter clauses is not permitted. " +
        "If you intend to affect all rows, use a filter with IsNotNull on the primary key.");
```

---

## 13. Optional Extensions

### 13.1 Optional Tables Detection

```csharp
// DataEngine.Infrastructure/Extensions/OptionalTablesDetector.cs
namespace DataEngine.Infrastructure.Extensions;

internal sealed class OptionalTablesDetector
{
    public record AvailableExtensions(
        bool HasFieldMappings,
        bool HasQueryDefinitions,
        bool HasTransactionAudit,
        bool HasProceduresTable);
    
    public async Task<AvailableExtensions> DetectAsync(
        string connectionString,
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
              )
            """;
        
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        
        var tables = (await conn.QueryAsync<string>(sql)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        return new AvailableExtensions(
            tables.Contains("de_field_mappings"),
            tables.Contains("de_query_definitions"),
            tables.Contains("de_transaction_audit"),
            tables.Contains("de_procedures"));
    }
}
```

### 13.2 Extension Table DDL (provided as embedded SQL, not auto-run)

```sql
-- de_field_mappings: optional column alias mapping
CREATE TABLE de_field_mappings (
    id          INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    table_name  VARCHAR(128) NOT NULL,
    column_name VARCHAR(128) NOT NULL,
    alias       VARCHAR(128) NOT NULL,
    direction   ENUM('in', 'out', 'both') NOT NULL DEFAULT 'both',
    created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY uq_mapping (table_name, alias, direction)
);

-- de_query_definitions: saved named queries
CREATE TABLE de_query_definitions (
    id              INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    definition_key  VARCHAR(128) NOT NULL UNIQUE,
    table_name      VARCHAR(128) NOT NULL,
    description     TEXT,
    query_json      JSON NOT NULL,  -- serialized QueryRequest (minus table)
    is_active       TINYINT(1) NOT NULL DEFAULT 1,
    created_at      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
);

-- de_transaction_audit: audit trail
CREATE TABLE de_transaction_audit (
    id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    operation     ENUM('INSERT','UPDATE','DELETE') NOT NULL,
    table_name    VARCHAR(128) NOT NULL,
    database_name VARCHAR(128) NOT NULL,
    record_id     VARCHAR(255),
    old_values    JSON,
    new_values    JSON,
    executed_by   VARCHAR(255),
    executed_at   DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    correlation_id VARCHAR(64),
    INDEX idx_table_op (table_name, operation),
    INDEX idx_executed_at (executed_at),
    INDEX idx_correlation (correlation_id)
);
```

**Architect's note:** These DDL scripts are shipped as embedded resources inside the package. The consumer runs them manually or via their own migration tooling. DataEngine never auto-creates these tables. That's by design — you don't want a framework silently creating database objects.

---

## 14. NuGet Packaging Strategy

### Package Structure

```
DataEngine                          ← Meta-package (installs all below)
DataEngine.Core                     ← Domain + interfaces only (no impl deps)
DataEngine.Infrastructure.MySql     ← MySql-specific impl
DataEngine.Extensions.DI            ← AddDataEngine() extension
```

### Project Structure for Packaging

```xml
<!-- DataEngine.Core.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
    <PackageId>DataEngine.Core</PackageId>
    <Version>1.0.0</Version>
    <Authors>YourName</Authors>
    <Description>Domain interfaces and contracts for DataEngine</Description>
    <PackageTags>orm;dapper;mysql;dynamic;metadata</PackageTags>
    <RepositoryUrl>https://github.com/yourorg/dataengine</RepositoryUrl>
  </PropertyGroup>
  
  <!-- No external dependencies intentionally -->
  <ItemGroup>
    <PackageReference Include="MySqlConnector" Version="2.*" />
  </ItemGroup>
</Project>
```

```xml
<!-- DataEngine.csproj (meta-package) -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>DataEngine</PackageId>
    <Version>1.0.0</Version>
    <Description>Plug-and-play metadata-driven backend framework for .NET 8</Description>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="DataEngine.Core" Version="1.0.0" />
    <PackageReference Include="DataEngine.Infrastructure.MySql" Version="1.0.0" />
    <PackageReference Include="DataEngine.Extensions.DI" Version="1.0.0" />
  </ItemGroup>
</Project>
```

### Versioning Strategy

Follow **SemVer strictly**:
- `1.x.x` — Stable, no breaking changes
- `2.0.0` — Breaking changes (e.g., adding mandatory parameter to interface)
- Use `[Obsolete]` with one minor version before removing

### Embedding DDL Scripts

```xml
<!-- In DataEngine.Core.csproj -->
<ItemGroup>
  <EmbeddedResource Include="Scripts\de_field_mappings.sql" />
  <EmbeddedResource Include="Scripts\de_query_definitions.sql" />
  <EmbeddedResource Include="Scripts\de_transaction_audit.sql" />
</ItemGroup>
```

```csharp
// Expose via utility method:
public static class DataEngineScripts
{
    public static string GetExtensionDdl(string tableName)
    {
        var asm = typeof(DataEngineScripts).Assembly;
        using var stream = asm.GetManifestResourceStream($"DataEngine.Core.Scripts.{tableName}.sql")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

---

## 15. Unit Testing Strategy

### What to Unit Test

```
DataEngine.Core.Tests/
├── Security/
│   ├── TableGuardTests.cs           ← forbidden DB names, char validation
│   └── ColumnGuardTests.cs          ← missing column, auto-increment protection
├── Query/
│   ├── MySqlQueryCompilerTests.cs   ← SQL output correctness (string assertions)
│   ├── FilterBuilderTests.cs        ← each FilterOperator produces correct SQL
│   ├── PaginationBuilderTests.cs    ← LIMIT/OFFSET math
│   └── QueryCacheKeyBuilderTests.cs ← deterministic, no value leakage
├── Schema/
│   ├── TableMetadataTests.cs        ← WritableColumns, PrimaryKeys logic
│   └── SchemaMetadataSnapshotTests.cs
└── Domain/
    ├── PaginationClauseTests.cs     ← Offset calculation
    └── AuditEntryTests.cs
```

### Sample Unit Test

```csharp
// MySqlQueryCompilerTests.cs
public sealed class MySqlQueryCompilerTests
{
    private readonly MySqlQueryCompiler _compiler;
    private readonly TableMetadata _testSchema;
    
    public MySqlQueryCompilerTests()
    {
        _compiler = new MySqlQueryCompiler(new TableGuard(), new ColumnGuard());
        _testSchema = BuildTestSchema();
    }
    
    [Fact]
    public void CompileSelect_NoFilters_ProducesCorrectSql()
    {
        var request = new QueryRequest { Table = "orders", Database = "myapp" };
        var compiled = _compiler.CompileSelect(request, _testSchema);
        
        Assert.Contains("SELECT", compiled.Sql);
        Assert.Contains("`orders`", compiled.Sql);
        Assert.DoesNotContain("WHERE", compiled.Sql);
        Assert.Empty(compiled.Parameters);
    }
    
    [Fact]
    public void CompileSelect_WithFilter_ParameterizesValue()
    {
        var request = new QueryRequest
        {
            Table = "orders",
            Database = "myapp",
            Filters = [new FilterClause
            {
                Column = "status",
                Operator = FilterOperator.Equals,
                Value = "'; DROP TABLE orders; --"  // injection attempt
            }]
        };
        
        var compiled = _compiler.CompileSelect(request, _testSchema);
        
        // SQL should contain param placeholder, NEVER the raw value
        Assert.DoesNotContain("DROP", compiled.Sql);
        Assert.Contains("@p_f_0", compiled.Sql);
        Assert.Equal("'; DROP TABLE orders; --", compiled.Parameters["p_f_0"]);
    }
    
    [Fact]
    public void CompileInsert_AutoIncrementColumn_IsExcluded()
    {
        var request = new InsertRequest
        {
            Table = "orders",
            Database = "myapp",
            Values = new Dictionary<string, object?>
            {
                ["id"] = 999,         // auto-increment — should be excluded
                ["status"] = "pending"
            }
        };
        
        var compiled = _compiler.CompileInsert(request, _testSchema);
        
        Assert.DoesNotContain("`id`", compiled.Sql);
        Assert.Contains("`status`", compiled.Sql);
    }
    
    [Fact]
    public void CompileDelete_NonExistentColumn_ThrowsSchemaValidationException()
    {
        var request = new DeleteRequest
        {
            Table = "orders",
            Database = "myapp",
            Filters = [new FilterClause
            {
                Column = "nonexistent_column",
                Operator = FilterOperator.Equals,
                Value = "x"
            }]
        };
        
        Assert.Throws<SchemaValidationException>(
            () => _compiler.CompileDelete(request, _testSchema));
    }
    
    private static TableMetadata BuildTestSchema() => new()
    {
        TableName = "orders",
        DatabaseName = "myapp",
        PrimaryKeys = ["id"],
        Columns = new Dictionary<string, ColumnMetadata>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = new() { ColumnName = "id", DataType = "int", IsNullable = false,
                IsAutoIncrement = true, IsPrimaryKey = true, OrdinalPosition = 1 },
            ["status"] = new() { ColumnName = "status", DataType = "varchar", IsNullable = false,
                IsAutoIncrement = false, IsPrimaryKey = false, OrdinalPosition = 2, CharacterMaxLength = 50 },
            ["created_at"] = new() { ColumnName = "created_at", DataType = "datetime", IsNullable = false,
                IsAutoIncrement = false, IsPrimaryKey = false, OrdinalPosition = 3 }
        }
    };
}
```

---

## 16. Integration Testing Strategy

### TestContainers Setup

```csharp
// DataEngine.Integration.Tests/DataEngineIntegrationFixture.cs
public sealed class DataEngineIntegrationFixture : IAsyncLifetime
{
    private MySqlContainer _container = default!;
    public IDataEngine Engine { get; private set; } = default!;
    
    public async Task InitializeAsync()
    {
        _container = new MySqlBuilder()
            .WithDatabase("testdb")
            .WithUsername("testuser")
            .WithPassword("testpass")
            .Build();
        
        await _container.StartAsync();
        
        // Seed test schema
        await using var conn = new MySqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await conn.ExecuteAsync("""
            CREATE TABLE orders (
                id INT AUTO_INCREMENT PRIMARY KEY,
                status VARCHAR(50) NOT NULL,
                total DECIMAL(10,2),
                created_at DATETIME NOT NULL DEFAULT NOW()
            );
            INSERT INTO orders (status, total) VALUES ('pending', 100.00), ('shipped', 200.00);
            """);
        
        // Wire up DI
        var services = new ServiceCollection()
            .AddLogging()
            .AddDataEngine(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _container.GetConnectionString()
                })
                .Build());
        
        Engine = services.BuildServiceProvider().GetRequiredService<IDataEngine>();
    }
    
    public async Task DisposeAsync() => await _container.DisposeAsync();
}
```

```csharp
// DataEngine.Integration.Tests/QueryIntegrationTests.cs
public sealed class QueryIntegrationTests : IClassFixture<DataEngineIntegrationFixture>
{
    private readonly IDataEngine _engine;
    
    public QueryIntegrationTests(DataEngineIntegrationFixture fixture)
    {
        _engine = fixture.Engine;
    }
    
    [Fact]
    public async Task QueryAsync_WithFilter_ReturnsMatchingRows()
    {
        var response = await _engine.QueryAsync(new QueryRequest
        {
            Table = "orders",
            Filters = [new FilterClause
            {
                Column = "status",
                Operator = FilterOperator.Equals,
                Value = "pending"
            }]
        });
        
        Assert.True(response.Success);
        Assert.Single(response.Data);
        Assert.Equal("pending", response.Data[0]["status"]);
    }
    
    [Fact]
    public async Task InsertAsync_ValidData_ReturnsInsertedId()
    {
        var response = await _engine.InsertAsync(new InsertRequest
        {
            Table = "orders",
            Values = new Dictionary<string, object?>
            {
                ["status"] = "new",
                ["total"] = 350.00m
            }
        });
        
        Assert.True(response.Success);
        Assert.True(response.InsertedId > 0);
    }
    
    [Fact]
    public async Task DeleteAsync_WithoutFilter_ThrowsDataEngineException()
    {
        await Assert.ThrowsAsync<DataEngineException>(() =>
            _engine.DeleteAsync(new DeleteRequest
            {
                Table = "orders",
                Filters = []  // empty — must throw
            }));
    }
}
```

---

## 17. Build-Now vs Defer Roadmap

### Phase 1 — MVP (Build Now) — Sprint 1-3

| Component | Priority | Notes |
|-----------|----------|-------|
| `DataEngine.Core` — all domain models | P0 | Foundation for everything |
| `IDataEngine`, `ISchemaProvider`, `IQueryCompiler` interfaces | P0 | Contract-first |
| `InMemorySchemaCache` | P0 | Core to safety model |
| `MySqlSchemaProvider` (INFORMATION_SCHEMA read) | P0 | No framework works without this |
| `MySqlQueryCompiler` (SELECT, INSERT, UPDATE, DELETE) | P0 | Core CRUD |
| `TableGuard`, `ColumnGuard` | P0 | Non-negotiable safety |
| `DapperQueryExecutor` | P0 | Reads |
| `AdoNetWriteExecutor` | P0 | Writes |
| `AddDataEngine()` DI extension | P0 | Plug-and-play contract |
| `DataEngineOptions` | P0 | Configuration |
| Unit tests for compiler + guards | P0 | Safety net from day 1 |
| Integration tests with TestContainers | P1 | Before any "done" claim |

### Phase 2 — Core Extensions (Build Next) — Sprint 4-6

| Component | Priority | Notes |
|-----------|----------|-------|
| `MySqlProcedureExecutor` | P1 | High demand for SP execution |
| `TransactionCoordinator` multi-op | P1 | Required for real workflows |
| `BulkInsertRequest` / batch execution | P1 | Performance requirement |
| `SchemaRefreshService` (background IHostedService) | P1 | Production readiness |
| `NullAuditWriter` / `TableAuditWriter` | P1 | Wire up now, make optional |
| Pagination + count query | P1 | Standard requirement |
| `QueryCacheKeyBuilder` + compiled query cache | P1 | Performance |

### Phase 3 — Advanced Features (Defer) — Sprint 7+

| Component | Reason to Defer |
|-----------|----------------|
| `de_field_mappings` + `FieldMapperProvider` | Nice-to-have; adds DB dependency |
| `de_query_definitions` saved queries | Useful but not core |
| Multi-database router | Wait for real use case |
| JOIN support in compiler | Complex; cover with SPs for now |
| UNION support | Rare requirement |
| Raw SQL mode (even opt-in) | High risk, low reward |
| Query plan hints | Premature optimization |
| Distributed cache (IDistributedCache) | Only if multi-instance needed |
| Source generation for faster param binding | .NET 9 enhancement |

---

## 18. Best Practices & Architectural Decisions

### Decision Log

**D1: Why Dapper + ADO.NET instead of unified approach?**
Dapper is unbeatable for reading dynamic results into `IDictionary<string, object>` — zero ceremony, full SQL control. But for writes, Dapper adds no value over ADO.NET and its transaction support is weaker. Separating concerns means each tool is used where it excels.

**D2: Why `record` types for domain models?**
Records give value equality, immutability, and `with` expressions for free. Schema metadata is read-once, compare-often — `record` is the perfect type. It also prevents accidental mutation of cached schema objects.

**D3: Why `ConcurrentDictionary` for schema cache instead of `IMemoryCache`?**
`IMemoryCache` is designed for ASP.NET cache eviction policies (size limits, sliding expiry). Schema metadata is different — you want *explicit* invalidation, not size-based eviction. Using `ConcurrentDictionary` with an explicit TTL check gives deterministic behavior. The framework registers `IMemoryCache` to avoid duplicate registrations, but the schema cache itself uses `ConcurrentDictionary`.

**D4: Why `SemaphoreSlim` per database for schema loading?**
Without it, 50 concurrent requests on cold start would all try to load schema simultaneously — 50 identical INFORMATION_SCHEMA queries. The per-database semaphore + double-check pattern reduces this to exactly one query per database on cold start. This is the "thundering herd" problem, and it's real in containerized environments.

**D5: Why no entity classes?**
They're unnecessary overhead for a dynamic framework and break the "no setup" contract. `IDictionary<string, object?>` is the universal container for dynamic row data. Consumer apps can map to their own types trivially: `JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(row))` or via AutoMapper. Don't make DataEngine do their job.

**D6: Why backtick-quoting all identifiers?**
MySQL reserved words (e.g., `order`, `rank`, `status`, `group`) are extremely common column and table names in real schemas. Always quoting means `SELECT \`status\` FROM \`orders\`` works regardless of MySQL version or SQL mode. No surprises.

**D7: Why require filters for UPDATE and DELETE at the API contract level?**
A `DeleteRequest` with an empty filter list would delete the entire table. Making `Filters` required (non-nullable) and enforcing `Count > 0` at runtime means a developer literally cannot accidentally delete all rows — they have to explicitly opt into it, which is impossible through normal usage.

**D8: Why ship DDL as embedded resources rather than auto-running migrations?**
DataEngine doesn't own the consumer's database. Auto-running DDL is a serious trust boundary violation. Ship the scripts, let developers review and run them. This is what Hangfire, Quartz.NET, and every serious .NET package does.

### Code Quality Rules

1. **All public methods are async with `CancellationToken`** — no blocking sync wrappers
2. **All SQL is parameterized** — zero string interpolation of user-provided values
3. **All SQL uses backtick-quoted identifiers** — reserved word safety
4. **All domain objects are immutable records** — thread-safe by design
5. **All infrastructure classes are `internal sealed`** — only interfaces are public
6. **No `static` state outside of `ConcurrentDictionary` in cache** — DI-friendly
7. **Logging at every significant boundary** — Debug for SQL, Info for schema loads, Error for failures
8. **Stopwatch on every executor** — `ExecutionTime` in every response for observability
9. **`ConfigureAwait(false)`** on all infrastructure-level awaits — library best practice
10. **`sealed` on all non-inherited classes** — prevents accidental subclassing

### Performance Notes

- **Never `SELECT *` in generated SQL.** Always enumerate columns from schema. This prevents issues with added columns breaking downstream deserialization and is more efficient.
- **Reuse `MySqlCommand` objects for bulk operations** — only rebind parameters per row
- **Batch bulk inserts at 500 rows max per statement** — beyond that, MySQL parameter limits and memory spikes become problematic
- **Schema cache read is lock-free** (`ConcurrentDictionary.TryGetValue`) — zero contention on hot path
- **Compiled query cache** eliminates repeated `StringBuilder` allocation for identical query shapes

---

*End of DataEngine Architecture Document v1.0*
*Review cadence: revisit after Phase 1 completion; update Defer list based on real consumer feedback.*
