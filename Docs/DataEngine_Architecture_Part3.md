# DataEngine — Part 3: Final Compilation Closures
## Missing DTOs, Project Files & Solution Scaffold

> **Prerequisite:** Parts 1 and 2 complete.
> **This document:** Everything needed to make the solution compile and run — 4 missing DTO records, 3 .csproj files, GlobalUsings per project, solution scaffold, and test project setup.

---

## Table of Contents

1. [Missing DTO Records](#1-missing-dto-records)
2. [All .csproj Files](#2-all-csproj-files)
3. [GlobalUsings Per Project](#3-globalusings-per-project)
4. [Solution Scaffold](#4-solution-scaffold)
5. [Test Project Setup](#5-test-project-setup)
6. [Final Compilation Checklist](#6-final-compilation-checklist)

---

## 1. Missing DTO Records

### 1.1 TransactionResponse

```csharp
// DataEngine.Core/Contracts/Responses/TransactionResponse.cs
namespace DataEngine.Core.Contracts.Responses;

/// <summary>
/// Result of a multi-operation transaction execution.
/// </summary>
public sealed record TransactionResponse
{
    public bool Success { get; init; }

    /// <summary>
    /// Number of individual operations that executed before commit (or before failure).
    /// On success this equals the total number of operations submitted.
    /// On failure this equals the number that ran before the rollback.
    /// </summary>
    public int OperationsExecuted { get; init; }

    public string? Error { get; init; }
    public TimeSpan ExecutionTime { get; init; }

    public static TransactionResponse Succeeded(int operationCount, TimeSpan elapsed) =>
        new()
        {
            Success = true,
            OperationsExecuted = operationCount,
            ExecutionTime = elapsed
        };

    public static TransactionResponse Failed(string error, int succeededCount, TimeSpan elapsed) =>
        new()
        {
            Success = false,
            Error = error,
            OperationsExecuted = succeededCount,
            ExecutionTime = elapsed
        };
}
```

### 1.2 SchemaResponse + ColumnInfo

```csharp
// DataEngine.Core/Contracts/Responses/SchemaResponse.cs
namespace DataEngine.Core.Contracts.Responses;

/// <summary>
/// Schema metadata for a single table.
/// Useful for dynamic UI generation — e.g. building AG Grid column definitions
/// from live database schema without hardcoding field names.
/// </summary>
public sealed record SchemaResponse
{
    public bool Success { get; init; }
    public string? Error { get; init; }

    public string? TableName { get; init; }
    public string? DatabaseName { get; init; }

    public IReadOnlyList<ColumnInfo>? Columns { get; init; }
    public IReadOnlyList<string>? PrimaryKeys { get; init; }

    public DateTimeOffset? CachedAt { get; init; }

    public static SchemaResponse NotFound(string tableName, string database) =>
        new()
        {
            Success = false,
            Error = $"Table '{tableName}' not found in database '{database}'."
        };
}
```

```csharp
// DataEngine.Core/Contracts/Responses/ColumnInfo.cs
namespace DataEngine.Core.Contracts.Responses;

/// <summary>
/// Public-facing column descriptor returned by GetSchemaAsync().
/// Deliberately separate from the internal ColumnMetadata domain object —
/// the public contract should not expose internals like ResolvedDbType.
/// </summary>
public sealed record ColumnInfo
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public required bool IsNullable { get; init; }
    public required bool IsAutoIncrement { get; init; }
    public required bool IsPrimaryKey { get; init; }
    public int? MaxLength { get; init; }
    public string? DefaultValue { get; init; }
}
```

### 1.3 BulkUpdateRequest + BulkUpdateRow

```csharp
// DataEngine.Core/Contracts/Requests/BulkUpdateRequest.cs
namespace DataEngine.Core.Contracts.Requests;

/// <summary>
/// Update multiple rows in a single atomic transaction.
/// Each row specifies its own Values (what to set) and Filters (which row to target).
///
/// Unlike BulkInsert which batches for performance,
/// BulkUpdate always executes as a single transaction — all succeed or all roll back.
/// </summary>
public sealed record BulkUpdateRequest
{
    public required string Table { get; init; }
    public string? Database { get; init; }

    /// <summary>
    /// Each entry is one UPDATE statement: Values = SET clause, Filters = WHERE clause.
    /// Every row MUST have at least one filter — enforced at orchestrator level.
    /// </summary>
    public required IReadOnlyList<BulkUpdateRow> Rows { get; init; }

    public string? ExecutedBy { get; init; }
    public string? CorrelationId { get; init; }
}

/// <summary>
/// A single row in a BulkUpdateRequest.
/// Maps to: UPDATE {Table} SET {Values} WHERE {Filters}
/// </summary>
public sealed record BulkUpdateRow
{
    /// <summary>Column → value pairs to SET.</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    /// <summary>
    /// Filter clauses for the WHERE clause.
    /// At least one required — enforced in WriteOrchestrator.
    /// </summary>
    public required IReadOnlyList<FilterClause> Filters { get; init; }
}
```

---

## 2. All .csproj Files

### 2.1 DataEngine.Core.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>

    <!-- NuGet packaging -->
    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <PackageId>DataEngine.Core</PackageId>
    <Version>1.0.0</Version>
    <Authors>Sivalingam Rajendran</Authors>
    <Description>Domain interfaces, contracts, and exceptions for DataEngine — the metadata-driven .NET backend framework.</Description>
    <PackageTags>dataengine;dynamic;metadata;mysql;dapper;orm-free</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/yourorg/dataengine</RepositoryUrl>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>

  <!--
    INTENTIONAL: Core has only MySqlConnector for MySqlDbType in ColumnMetadata.
    No Dapper. No EF Core. No Microsoft.Extensions.*.
    This keeps the domain layer dependency-free for library consumers.
  -->
  <ItemGroup>
    <PackageReference Include="MySqlConnector" Version="2.3.7" />
  </ItemGroup>

  <!-- DDL scripts for optional extension tables — shipped as embedded resources -->
  <ItemGroup>
    <EmbeddedResource Include="Scripts\de_field_mappings.sql" />
    <EmbeddedResource Include="Scripts\de_query_definitions.sql" />
    <EmbeddedResource Include="Scripts\de_transaction_audit.sql" />
  </ItemGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>

</Project>
```

### 2.2 DataEngine.Infrastructure.MySql.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>

    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <PackageId>DataEngine.Infrastructure.MySql</PackageId>
    <Version>1.0.0</Version>
    <Authors>Sivalingam Rajendran</Authors>
    <Description>MySQL infrastructure implementations for DataEngine: schema provider, query executor, write executor, connection factory, audit writer, field mapper.</Description>
    <PackageTags>dataengine;mysql;dapper;mysqlconnector;infrastructure</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <!-- Core domain reference — no circular deps -->
    <ProjectReference Include="..\DataEngine.Core\DataEngine.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Data access -->
    <PackageReference Include="MySqlConnector"                          Version="2.3.7" />
    <PackageReference Include="Dapper"                                  Version="2.1.35" />

    <!-- DI + logging abstractions only — no concrete implementations -->
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions"         Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Caching.Abstractions"         Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Caching.Memory"               Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions"   Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions"         Version="8.0.1" />
  </ItemGroup>

</Project>
```

### 2.3 DataEngine.Application.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>

    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <PackageId>DataEngine.Application</PackageId>
    <Version>1.0.0</Version>
    <Authors>Sivalingam Rajendran</Authors>
    <Description>Orchestration layer for DataEngine: QueryOrchestrator, WriteOrchestrator, ProcedureOrchestrator, DataEngineService.</Description>
    <PackageTags>dataengine;orchestration;application</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\DataEngine.Core\DataEngine.Core.csproj" />
    <ProjectReference Include="..\DataEngine.Infrastructure.MySql\DataEngine.Infrastructure.MySql.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions"             Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.2" />
  </ItemGroup>

</Project>
```

### 2.4 DataEngine.Extensions.DependencyInjection.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>

    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <PackageId>DataEngine.Extensions.DependencyInjection</PackageId>
    <Version>1.0.0</Version>
    <Authors>Sivalingam Rajendran</Authors>
    <Description>ASP.NET Core DI registration for DataEngine. Provides AddDataEngine() and DataEngineBuilder.</Description>
    <PackageTags>dataengine;di;aspnetcore;extensions</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\DataEngine.Core\DataEngine.Core.csproj" />
    <ProjectReference Include="..\DataEngine.Infrastructure.MySql\DataEngine.Infrastructure.MySql.csproj" />
    <ProjectReference Include="..\DataEngine.Application\DataEngine.Application.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!--
      Full DI package (not just abstractions) — this project owns registration.
      Consumers get this transitively via the meta-package.
    -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection"          Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Abstractions"   Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions"         Version="8.0.1" />
  </ItemGroup>

</Project>
```

### 2.5 DataEngine.csproj (Meta-package — the one consumers install)

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
    <PackageId>DataEngine</PackageId>
    <Version>1.0.0</Version>
    <Authors>Sivalingam Rajendran</Authors>
    <Description>
      DataEngine — plug-and-play metadata-driven backend framework for .NET 8.
      Install, add connection string, use immediately.
      No entity classes. No migrations. No EF Core.
      Dynamic CRUD, bulk ops, transactions, stored procedures — all driven by INFORMATION_SCHEMA.
    </Description>
    <PackageTags>dataengine;dynamic;metadata;mysql;dapper;crud;orm-free;plug-and-play</PackageTags>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/yourorg/dataengine</RepositoryUrl>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageIcon>icon.png</PackageIcon>
  </PropertyGroup>

  <!--
    Meta-package: no code, just pulls in all required sub-packages.
    Consumer installs only "DataEngine" and gets everything.
  -->
  <ItemGroup>
    <ProjectReference Include="..\DataEngine.Core\DataEngine.Core.csproj" />
    <ProjectReference Include="..\DataEngine.Infrastructure.MySql\DataEngine.Infrastructure.MySql.csproj" />
    <ProjectReference Include="..\DataEngine.Application\DataEngine.Application.csproj" />
    <ProjectReference Include="..\DataEngine.Extensions.DependencyInjection\DataEngine.Extensions.DependencyInjection.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="icon.png"  Pack="true" PackagePath="\" Condition="Exists('icon.png')" />
  </ItemGroup>

</Project>
```

### 2.6 Sample API .csproj

```xml
<!-- samples/DataEngine.Sample.Api/DataEngine.Sample.Api.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <!-- Reference the meta-package directly during development -->
    <ProjectReference Include="..\..\src\DataEngine\DataEngine.csproj" />
  </ItemGroup>

</Project>
```

---

## 3. GlobalUsings Per Project

Without these, every `.cs` file needs 8–12 `using` directives at the top. Define them once per project.

### 3.1 DataEngine.Core — GlobalUsings.cs

```csharp
// DataEngine.Core/GlobalUsings.cs
global using System.Collections.Concurrent;
global using System.Collections.ObjectModel;
global using System.Text.Json;
global using MySqlConnector;
global using DataEngine.Core.Contracts.Requests;
global using DataEngine.Core.Contracts.Responses;
global using DataEngine.Core.Domain.Audit;
global using DataEngine.Core.Domain.Query;
global using DataEngine.Core.Domain.Schema;
global using DataEngine.Core.Exceptions;
global using DataEngine.Core.Interfaces;
```

### 3.2 DataEngine.Infrastructure.MySql — GlobalUsings.cs

```csharp
// DataEngine.Infrastructure.MySql/GlobalUsings.cs
global using System.Collections.Concurrent;
global using System.Collections.ObjectModel;
global using System.Data;
global using System.Diagnostics;
global using System.Text;
global using System.Text.Json;
global using Dapper;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
global using MySqlConnector;
global using DataEngine.Core.Contracts.Requests;
global using DataEngine.Core.Contracts.Responses;
global using DataEngine.Core.Domain.Audit;
global using DataEngine.Core.Domain.Query;
global using DataEngine.Core.Domain.Schema;
global using DataEngine.Core.Exceptions;
global using DataEngine.Core.Interfaces;
global using DataEngine.Infrastructure.Connection;
global using DataEngine.Infrastructure.Security;
```

### 3.3 DataEngine.Application — GlobalUsings.cs

```csharp
// DataEngine.Application/GlobalUsings.cs
global using System.Collections.Concurrent;
global using System.Collections.ObjectModel;
global using System.Diagnostics;
global using System.Text.Json;
global using Microsoft.Extensions.Logging;
global using MySqlConnector;
global using DataEngine.Core.Contracts.Requests;
global using DataEngine.Core.Contracts.Responses;
global using DataEngine.Core.Domain.Audit;
global using DataEngine.Core.Domain.Query;
global using DataEngine.Core.Domain.Schema;
global using DataEngine.Core.Exceptions;
global using DataEngine.Core.Interfaces;
global using DataEngine.Extensions.DependencyInjection;
global using DataEngine.Infrastructure.Connection;
global using DataEngine.Infrastructure.Security;
global using DataEngine.Infrastructure.Write;
global using DataEngine.Infrastructure.Procedure;
```

### 3.4 DataEngine.Extensions.DependencyInjection — GlobalUsings.cs

```csharp
// DataEngine.Extensions.DependencyInjection/GlobalUsings.cs
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using DataEngine.Application;
global using DataEngine.Core.Interfaces;
global using DataEngine.Infrastructure.Audit;
global using DataEngine.Infrastructure.Connection;
global using DataEngine.Infrastructure.Extensions;
global using DataEngine.Infrastructure.FieldMapping;
global using DataEngine.Infrastructure.Query;
global using DataEngine.Infrastructure.Schema;
global using DataEngine.Infrastructure.Security;
global using DataEngine.Infrastructure.Write;
global using DataEngine.Infrastructure.Procedure;
```

---

## 4. Solution Scaffold

### 4.1 Create the solution and projects

Run these commands from the repo root in order:

```bash
# 1. Create solution
dotnet new sln -n DataEngine

# 2. Create source projects
dotnet new classlib -n DataEngine.Core                          -o src/DataEngine.Core                          -f net8.0
dotnet new classlib -n DataEngine.Infrastructure.MySql          -o src/DataEngine.Infrastructure.MySql          -f net8.0
dotnet new classlib -n DataEngine.Application                   -o src/DataEngine.Application                   -f net8.0
dotnet new classlib -n DataEngine.Extensions.DependencyInjection -o src/DataEngine.Extensions.DependencyInjection -f net8.0
dotnet new classlib -n DataEngine                               -o src/DataEngine                               -f net8.0

# 3. Create test projects
dotnet new xunit -n DataEngine.Core.Tests           -o tests/DataEngine.Core.Tests           -f net8.0
dotnet new xunit -n DataEngine.Infrastructure.Tests -o tests/DataEngine.Infrastructure.Tests -f net8.0
dotnet new xunit -n DataEngine.Integration.Tests    -o tests/DataEngine.Integration.Tests    -f net8.0

# 4. Create sample project
dotnet new webapi -n DataEngine.Sample.Api -o samples/DataEngine.Sample.Api -f net8.0 --no-openapi

# 5. Add all projects to solution
dotnet sln add src/DataEngine.Core/DataEngine.Core.csproj
dotnet sln add src/DataEngine.Infrastructure.MySql/DataEngine.Infrastructure.MySql.csproj
dotnet sln add src/DataEngine.Application/DataEngine.Application.csproj
dotnet sln add src/DataEngine.Extensions.DependencyInjection/DataEngine.Extensions.DependencyInjection.csproj
dotnet sln add src/DataEngine/DataEngine.csproj
dotnet sln add tests/DataEngine.Core.Tests/DataEngine.Core.Tests.csproj
dotnet sln add tests/DataEngine.Infrastructure.Tests/DataEngine.Infrastructure.Tests.csproj
dotnet sln add tests/DataEngine.Integration.Tests/DataEngine.Integration.Tests.csproj
dotnet sln add samples/DataEngine.Sample.Api/DataEngine.Sample.Api.csproj

# 6. Add project references (enforce the dependency graph)
dotnet add src/DataEngine.Infrastructure.MySql/DataEngine.Infrastructure.MySql.csproj \
    reference src/DataEngine.Core/DataEngine.Core.csproj

dotnet add src/DataEngine.Application/DataEngine.Application.csproj \
    reference src/DataEngine.Core/DataEngine.Core.csproj

dotnet add src/DataEngine.Application/DataEngine.Application.csproj \
    reference src/DataEngine.Infrastructure.MySql/DataEngine.Infrastructure.MySql.csproj

dotnet add src/DataEngine.Extensions.DependencyInjection/DataEngine.Extensions.DependencyInjection.csproj \
    reference src/DataEngine.Core/DataEngine.Core.csproj

dotnet add src/DataEngine.Extensions.DependencyInjection/DataEngine.Extensions.DependencyInjection.csproj \
    reference src/DataEngine.Infrastructure.MySql/DataEngine.Infrastructure.MySql.csproj

dotnet add src/DataEngine.Extensions.DependencyInjection/DataEngine.Extensions.DependencyInjection.csproj \
    reference src/DataEngine.Application/DataEngine.Application.csproj

dotnet add src/DataEngine/DataEngine.csproj \
    reference src/DataEngine.Core/DataEngine.Core.csproj
dotnet add src/DataEngine/DataEngine.csproj \
    reference src/DataEngine.Infrastructure.MySql/DataEngine.Infrastructure.MySql.csproj
dotnet add src/DataEngine/DataEngine.csproj \
    reference src/DataEngine.Application/DataEngine.Application.csproj
dotnet add src/DataEngine/DataEngine.csproj \
    reference src/DataEngine.Extensions.DependencyInjection/DataEngine.Extensions.DependencyInjection.csproj

# 7. Add test project references
dotnet add tests/DataEngine.Core.Tests/DataEngine.Core.Tests.csproj \
    reference src/DataEngine.Core/DataEngine.Core.csproj

dotnet add tests/DataEngine.Infrastructure.Tests/DataEngine.Infrastructure.Tests.csproj \
    reference src/DataEngine.Core/DataEngine.Core.csproj
dotnet add tests/DataEngine.Infrastructure.Tests/DataEngine.Infrastructure.Tests.csproj \
    reference src/DataEngine.Infrastructure.MySql/DataEngine.Infrastructure.MySql.csproj

dotnet add tests/DataEngine.Integration.Tests/DataEngine.Integration.Tests.csproj \
    reference src/DataEngine.Core/DataEngine.Core.csproj
dotnet add tests/DataEngine.Integration.Tests/DataEngine.Integration.Tests.csproj \
    reference src/DataEngine.Infrastructure.MySql/DataEngine.Infrastructure.MySql.csproj
dotnet add tests/DataEngine.Integration.Tests/DataEngine.Integration.Tests.csproj \
    reference src/DataEngine.Application/DataEngine.Application.csproj
dotnet add tests/DataEngine.Integration.Tests/DataEngine.Integration.Tests.csproj \
    reference src/DataEngine.Extensions.DependencyInjection/DataEngine.Extensions.DependencyInjection.csproj

dotnet add samples/DataEngine.Sample.Api/DataEngine.Sample.Api.csproj \
    reference src/DataEngine/DataEngine.csproj

# 8. Verify build
dotnet build DataEngine.sln
```

### 4.2 Remove auto-generated boilerplate

`dotnet new classlib` generates a `Class1.cs` in each project. Delete them all:

```bash
rm src/DataEngine.Core/Class1.cs
rm src/DataEngine.Infrastructure.MySql/Class1.cs
rm src/DataEngine.Application/Class1.cs
rm src/DataEngine.Extensions.DependencyInjection/Class1.cs
rm src/DataEngine/Class1.cs
```

### 4.3 Directory structure after scaffold

```
DataEngine/
├── DataEngine.sln
├── src/
│   ├── DataEngine.Core/
│   │   ├── DataEngine.Core.csproj
│   │   ├── GlobalUsings.cs
│   │   ├── Domain/...
│   │   ├── Interfaces/...
│   │   ├── Contracts/...
│   │   ├── Exceptions/...
│   │   └── Scripts/
│   │       ├── de_field_mappings.sql
│   │       ├── de_query_definitions.sql
│   │       └── de_transaction_audit.sql
│   ├── DataEngine.Infrastructure.MySql/
│   │   ├── DataEngine.Infrastructure.MySql.csproj
│   │   ├── GlobalUsings.cs
│   │   ├── Schema/...
│   │   ├── Query/...
│   │   ├── Write/...
│   │   ├── Procedure/...
│   │   ├── Connection/...
│   │   ├── FieldMapping/...
│   │   ├── Audit/...
│   │   ├── Security/...
│   │   └── Extensions/...
│   ├── DataEngine.Application/
│   │   ├── DataEngine.Application.csproj
│   │   ├── GlobalUsings.cs
│   │   ├── DataEngineService.cs
│   │   ├── QueryOrchestrator.cs
│   │   ├── WriteOrchestrator.cs
│   │   └── ProcedureOrchestrator.cs
│   ├── DataEngine.Extensions.DependencyInjection/
│   │   ├── DataEngine.Extensions.DependencyInjection.csproj
│   │   ├── GlobalUsings.cs
│   │   ├── DataEngineOptions.cs
│   │   ├── DataEngineBuilder.cs
│   │   └── DataEngineServiceCollectionExtensions.cs
│   └── DataEngine/
│       └── DataEngine.csproj
├── tests/
│   ├── DataEngine.Core.Tests/
│   ├── DataEngine.Infrastructure.Tests/
│   └── DataEngine.Integration.Tests/
└── samples/
    └── DataEngine.Sample.Api/
```

---

## 5. Test Project Setup

### 5.1 DataEngine.Core.Tests.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\DataEngine.Core\DataEngine.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk"   Version="17.10.0" />
    <PackageReference Include="xunit"                    Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions"         Version="6.12.0" />
  </ItemGroup>

</Project>
```

### 5.2 DataEngine.Infrastructure.Tests.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\DataEngine.Core\DataEngine.Core.csproj" />
    <ProjectReference Include="..\..\src\DataEngine.Infrastructure.MySql\DataEngine.Infrastructure.MySql.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk"    Version="17.10.0" />
    <PackageReference Include="xunit"                     Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio"  Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions"          Version="6.12.0" />
    <PackageReference Include="Moq"                       Version="4.20.70" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.2" />
  </ItemGroup>

</Project>
```

### 5.3 DataEngine.Integration.Tests.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\DataEngine.Core\DataEngine.Core.csproj" />
    <ProjectReference Include="..\..\src\DataEngine.Infrastructure.MySql\DataEngine.Infrastructure.MySql.csproj" />
    <ProjectReference Include="..\..\src\DataEngine.Application\DataEngine.Application.csproj" />
    <ProjectReference Include="..\..\src\DataEngine.Extensions.DependencyInjection\DataEngine.Extensions.DependencyInjection.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk"    Version="17.10.0" />
    <PackageReference Include="xunit"                     Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio"  Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="FluentAssertions"          Version="6.12.0" />

    <!-- TestContainers for real MySQL in CI -->
    <PackageReference Include="Testcontainers"            Version="3.9.0" />
    <PackageReference Include="Testcontainers.MySql"      Version="3.9.0" />

    <!-- Full DI stack for integration wiring -->
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Logging"             Version="8.0.1" />
    <PackageReference Include="Microsoft.Extensions.Configuration"       Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Memory" Version="8.0.0" />
  </ItemGroup>

</Project>
```

### 5.4 Integration Test GlobalUsings.cs

```csharp
// tests/DataEngine.Integration.Tests/GlobalUsings.cs
global using System.Collections.Generic;
global using FluentAssertions;
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using MySqlConnector;
global using Testcontainers.MySql;
global using Xunit;
global using DataEngine.Core.Contracts.Requests;
global using DataEngine.Core.Domain.Query;
global using DataEngine.Core.Exceptions;
global using DataEngine.Core.Interfaces;
global using DataEngine.Extensions.DependencyInjection;
```

### 5.5 Integration Test Fixture — Final Form

```csharp
// tests/DataEngine.Integration.Tests/DataEngineIntegrationFixture.cs
namespace DataEngine.Integration.Tests;

/// <summary>
/// Shared fixture: spins up a real MySQL container once per test collection.
/// Seeds a minimal schema for all tests to use.
/// </summary>
public sealed class DataEngineIntegrationFixture : IAsyncLifetime
{
    private MySqlContainer _container = default!;
    private ServiceProvider _serviceProvider = default!;

    public IDataEngine Engine { get; private set; } = default!;
    public string ConnectionString { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        // Start MySQL container
        _container = new MySqlBuilder()
            .WithDatabase("de_test")
            .WithUsername("de_user")
            .WithPassword("de_pass")
            .WithImage("mysql:8.0")
            .Build();

        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();

        // Seed test schema
        await SeedDatabaseAsync();

        // Wire up full DI stack
        var services = new ServiceCollection();
        services.AddLogging();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ConnectionString
            })
            .Build();

        services
            .AddDataEngine(config)
            .WithSchemaCacheTtl(TimeSpan.FromMinutes(60))
            .WithPageSize(defaultSize: 10, maxSize: 100);

        _serviceProvider = services.BuildServiceProvider();

        // Resolve IDataEngine from scope
        var scope = _serviceProvider.CreateScope();
        Engine = scope.ServiceProvider.GetRequiredService<IDataEngine>();
    }

    public async Task DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task SeedDatabaseAsync()
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS products (
                id          INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                sku         VARCHAR(50) NOT NULL UNIQUE,
                name        VARCHAR(200) NOT NULL,
                price       DECIMAL(10,2) NOT NULL DEFAULT 0.00,
                stock       INT NOT NULL DEFAULT 0,
                category    VARCHAR(100),
                is_active   TINYINT(1) NOT NULL DEFAULT 1,
                created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS orders (
                id          INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
                product_id  INT UNSIGNED NOT NULL,
                quantity    INT NOT NULL,
                status      VARCHAR(50) NOT NULL DEFAULT 'pending',
                ordered_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            INSERT INTO products (sku, name, price, stock, category) VALUES
                ('SKU-001', 'Pneumatic Cylinder A', 1500.00, 100, 'cylinders'),
                ('SKU-002', 'Pneumatic Cylinder B', 2200.00, 50,  'cylinders'),
                ('SKU-003', 'Solenoid Valve X',     850.00,  200, 'valves'),
                ('SKU-004', 'Solenoid Valve Y',     950.00,  150, 'valves'),
                ('SKU-005', 'Air Filter Unit',      400.00,  300, 'filters');

            INSERT INTO orders (product_id, quantity, status) VALUES
                (1, 5, 'pending'),
                (2, 2, 'shipped'),
                (3, 10, 'pending'),
                (1, 1, 'delivered');
            """;

        await cmd.ExecuteNonQueryAsync();
    }
}
```

### 5.6 Core Integration Tests

```csharp
// tests/DataEngine.Integration.Tests/QueryIntegrationTests.cs
namespace DataEngine.Integration.Tests;

[Collection("DataEngine")]
public sealed class QueryIntegrationTests : IClassFixture<DataEngineIntegrationFixture>
{
    private readonly IDataEngine _engine;

    public QueryIntegrationTests(DataEngineIntegrationFixture fixture)
    {
        _engine = fixture.Engine;
    }

    [Fact]
    public async Task QueryAsync_NoFilters_ReturnsAllRows()
    {
        var response = await _engine.QueryAsync(new QueryRequest { Table = "products" });

        response.Success.Should().BeTrue();
        response.Data.Count.Should().Be(5);
    }

    [Fact]
    public async Task QueryAsync_WithEqualFilter_ReturnsMatchingRows()
    {
        var response = await _engine.QueryAsync(new QueryRequest
        {
            Table = "products",
            Filters = [new FilterClause
            {
                Column = "category",
                Operator = FilterOperator.Equals,
                Value = "cylinders"
            }]
        });

        response.Success.Should().BeTrue();
        response.Data.Count.Should().Be(2);
        response.Data.Should().OnlyContain(r => r["category"]!.ToString() == "cylinders");
    }

    [Fact]
    public async Task QueryAsync_WithPagination_ReturnsCorrectPage()
    {
        var response = await _engine.QueryAsync(new QueryRequest
        {
            Table = "products",
            Pagination = new PaginationClause { Page = 1, PageSize = 2 },
            IncludeCount = true
        });

        response.Success.Should().BeTrue();
        response.Data.Count.Should().Be(2);
        response.TotalCount.Should().Be(5);
        response.PageCount.Should().Be(3);
    }

    [Fact]
    public async Task QueryAsync_WithSort_ReturnsOrderedRows()
    {
        var response = await _engine.QueryAsync(new QueryRequest
        {
            Table = "products",
            Sort = [new SortClause { Column = "price", Direction = SortDirection.Descending }]
        });

        var prices = response.Data
            .Select(r => Convert.ToDecimal(r["price"]))
            .ToList();

        prices.Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task QueryAsync_InvalidTable_ThrowsTableNotFoundException()
    {
        var act = async () => await _engine.QueryAsync(
            new QueryRequest { Table = "nonexistent_table" });

        await act.Should().ThrowAsync<TableNotFoundException>()
            .WithMessage("*nonexistent_table*");
    }

    [Fact]
    public async Task QueryAsync_SystemDatabase_ThrowsSecurityViolationException()
    {
        var act = async () => await _engine.QueryAsync(new QueryRequest
        {
            Table = "user",
            Database = "mysql"
        });

        await act.Should().ThrowAsync<SecurityViolationException>();
    }

    [Fact]
    public async Task QueryAsync_InvalidColumn_ThrowsSchemaValidationException()
    {
        var act = async () => await _engine.QueryAsync(new QueryRequest
        {
            Table = "products",
            Filters = [new FilterClause
            {
                Column = "nonexistent_column",
                Operator = FilterOperator.Equals,
                Value = "x"
            }]
        });

        await act.Should().ThrowAsync<SchemaValidationException>();
    }

    [Fact]
    public async Task QueryAsync_InFilter_ReturnsMatchingRows()
    {
        var response = await _engine.QueryAsync(new QueryRequest
        {
            Table = "products",
            Filters = [new FilterClause
            {
                Column = "category",
                Operator = FilterOperator.In,
                Value = new[] { "cylinders", "valves" }
            }]
        });

        response.Data.Count.Should().Be(4);
    }
}

[Collection("DataEngine")]
public sealed class WriteIntegrationTests : IClassFixture<DataEngineIntegrationFixture>
{
    private readonly IDataEngine _engine;

    public WriteIntegrationTests(DataEngineIntegrationFixture fixture)
    {
        _engine = fixture.Engine;
    }

    [Fact]
    public async Task InsertAsync_ValidData_ReturnsInsertedId()
    {
        var response = await _engine.InsertAsync(new InsertRequest
        {
            Table = "products",
            Values = new Dictionary<string, object?>
            {
                ["sku"]      = $"SKU-TEST-{Guid.NewGuid():N}"[..20],
                ["name"]     = "Test Product",
                ["price"]    = 999.99m,
                ["stock"]    = 10,
                ["category"] = "test"
            }
        });

        response.Success.Should().BeTrue();
        response.InsertedId.Should().BeGreaterThan(0);
        response.AffectedRows.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_WithFilter_UpdatesCorrectRow()
    {
        // First insert a row we can safely update
        var insert = await _engine.InsertAsync(new InsertRequest
        {
            Table = "orders",
            Values = new Dictionary<string, object?>
            {
                ["product_id"] = 1,
                ["quantity"]   = 3,
                ["status"]     = "pending"
            }
        });

        var response = await _engine.UpdateAsync(new UpdateRequest
        {
            Table = "orders",
            Values = new Dictionary<string, object?> { ["status"] = "shipped" },
            Filters = [new FilterClause
            {
                Column = "id",
                Operator = FilterOperator.Equals,
                Value = insert.InsertedId
            }]
        });

        response.Success.Should().BeTrue();
        response.AffectedRows.Should().Be(1);
    }

    [Fact]
    public async Task UpdateAsync_EmptyFilters_ThrowsDataEngineException()
    {
        var act = async () => await _engine.UpdateAsync(new UpdateRequest
        {
            Table = "products",
            Values = new Dictionary<string, object?> { ["stock"] = 0 },
            Filters = []
        });

        await act.Should().ThrowAsync<DataEngineException>()
            .WithMessage("*filter*");
    }

    [Fact]
    public async Task DeleteAsync_EmptyFilters_ThrowsDataEngineException()
    {
        var act = async () => await _engine.DeleteAsync(new DeleteRequest
        {
            Table = "products",
            Filters = []
        });

        await act.Should().ThrowAsync<DataEngineException>()
            .WithMessage("*filter*");
    }

    [Fact]
    public async Task BulkInsertAsync_MultipleRows_InsertsAllRows()
    {
        var rows = Enumerable.Range(1, 15)
            .Select(i => (IReadOnlyDictionary<string, object?>)
                new Dictionary<string, object?>
                {
                    ["sku"]      = $"BULK-{Guid.NewGuid():N}"[..20],
                    ["name"]     = $"Bulk Product {i}",
                    ["price"]    = 100m * i,
                    ["stock"]    = i * 10,
                    ["category"] = "bulk-test"
                })
            .ToList()
            .AsReadOnly();

        var response = await _engine.BulkInsertAsync(new BulkInsertRequest
        {
            Table = "products",
            Rows = rows,
            BatchSize = 5   // forces 3 batches
        });

        response.Success.Should().BeTrue();
        response.AffectedRows.Should().Be(15);
    }

    [Fact]
    public async Task ExecuteTransactionAsync_AllOpsSucceed_CommitsAll()
    {
        var sku = $"TXN-{Guid.NewGuid():N}"[..20];

        var response = await _engine.ExecuteTransactionAsync(new TransactionRequest
        {
            Operations =
            [
                new TransactionOperation
                {
                    Order = 1,
                    Type = OperationType.Insert,
                    Table = "products",
                    Values = new Dictionary<string, object?>
                    {
                        ["sku"] = sku, ["name"] = "Txn Product",
                        ["price"] = 500m, ["stock"] = 5
                    }
                },
                new TransactionOperation
                {
                    Order = 2,
                    Type = OperationType.Insert,
                    Table = "orders",
                    Values = new Dictionary<string, object?>
                    {
                        ["product_id"] = 1, ["quantity"] = 1, ["status"] = "pending"
                    }
                }
            ]
        });

        response.Success.Should().BeTrue();
        response.OperationsExecuted.Should().Be(2);
    }
}

[Collection("DataEngine")]
public sealed class SchemaIntegrationTests : IClassFixture<DataEngineIntegrationFixture>
{
    private readonly IDataEngine _engine;

    public SchemaIntegrationTests(DataEngineIntegrationFixture fixture)
    {
        _engine = fixture.Engine;
    }

    [Fact]
    public async Task GetSchemaAsync_ExistingTable_ReturnsColumns()
    {
        var response = await _engine.GetSchemaAsync("products");

        response.Success.Should().BeTrue();
        response.TableName.Should().Be("products");
        response.Columns.Should().NotBeNullOrEmpty();
        response.Columns!.Should().Contain(c => c.Name == "id" && c.IsAutoIncrement && c.IsPrimaryKey);
        response.Columns!.Should().Contain(c => c.Name == "sku" && !c.IsNullable);
        response.PrimaryKeys.Should().Contain("id");
    }

    [Fact]
    public async Task GetSchemaAsync_NonExistentTable_ReturnsFailure()
    {
        var response = await _engine.GetSchemaAsync("ghost_table");

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("ghost_table");
    }

    [Fact]
    public async Task InvalidateSchemaAsync_ThenQuery_ReloadsSchema()
    {
        // Invalidate and ensure query still works (lazy reload)
        await _engine.InvalidateSchemaAsync();

        var response = await _engine.QueryAsync(new QueryRequest
        {
            Table = "products",
            Pagination = new PaginationClause { Page = 1, PageSize = 1 }
        });

        response.Success.Should().BeTrue();
    }
}

// Required for xunit collection fixture sharing
[CollectionDefinition("DataEngine")]
public sealed class DataEngineCollection : ICollectionFixture<DataEngineIntegrationFixture> { }
```

---

## 6. Final Compilation Checklist

Work through this list top-to-bottom before running `dotnet build`. Every item is a guaranteed compile or runtime error if skipped.

### 6.1 New files added in Part 3

- [ ] `Core/Contracts/Responses/TransactionResponse.cs` created
- [ ] `Core/Contracts/Responses/SchemaResponse.cs` created
- [ ] `Core/Contracts/Responses/ColumnInfo.cs` created
- [ ] `Core/Contracts/Requests/BulkUpdateRequest.cs` created (includes `BulkUpdateRow`)
- [ ] `GlobalUsings.cs` created in all 4 source projects
- [ ] `GlobalUsings.cs` created in `DataEngine.Integration.Tests`
- [ ] All 5 `.csproj` files replaced with the versions in Section 2
- [ ] All 3 test `.csproj` files created with the versions in Section 5

### 6.2 Cross-reference checks against existing code

- [ ] `WriteOrchestrator.BulkUpdateAsync()` references `BulkUpdateRow.Values` and `BulkUpdateRow.Filters` — verify property names match Section 1.3
- [ ] `DataEngineService.GetSchemaAsync()` constructs `new ColumnInfo { Name, DataType, IsNullable, IsAutoIncrement, IsPrimaryKey, MaxLength, DefaultValue }` — verify all 7 properties exist in `ColumnInfo`
- [ ] `DataEngineService.GetSchemaAsync()` returns `new SchemaResponse { TableName, DatabaseName, Columns, PrimaryKeys, CachedAt, Success, Error }` — verify all 7 properties exist in `SchemaResponse`
- [ ] `WriteOrchestrator.ExecuteTransactionAsync()` returns `new TransactionResponse { Success, OperationsExecuted, Error, ExecutionTime }` — verify all 4 properties exist
- [ ] `AdoNetWriteExecutor.ExecuteTransactionAsync()` returns `TransactionResult` (internal) not `TransactionResponse` (public) — these are different types, confirm no mix-up
- [ ] `BulkWriteExecutor.SplitIntoBatches()` is `static` — verify it's called as `BulkWriteExecutor.SplitIntoBatches(request, batchSize)` in `WriteOrchestrator`

### 6.3 Namespace consistency check

Every file must have a namespace matching its folder path. Common mistakes:

| File location | Correct namespace |
|--------------|-------------------|
| `Core/Contracts/Responses/TransactionResponse.cs` | `DataEngine.Core.Contracts.Responses` |
| `Core/Contracts/Responses/SchemaResponse.cs` | `DataEngine.Core.Contracts.Responses` |
| `Core/Contracts/Responses/ColumnInfo.cs` | `DataEngine.Core.Contracts.Responses` |
| `Core/Contracts/Requests/BulkUpdateRequest.cs` | `DataEngine.Core.Contracts.Requests` |
| `Infrastructure.MySql/Write/TransactionResult.cs` | `DataEngine.Infrastructure.Write` |
| `Infrastructure.MySql/Procedure/ProcedureResult.cs` | `DataEngine.Infrastructure.Procedure` |
| `Application/DataEngineService.cs` | `DataEngine.Application` |
| `Extensions.DI/DataEngineBuilder.cs` | `DataEngine.Extensions.DependencyInjection` |

### 6.4 Build and test commands

```bash
# Full build
dotnet build DataEngine.sln

# Unit tests only (no Docker needed)
dotnet test tests/DataEngine.Core.Tests
dotnet test tests/DataEngine.Infrastructure.Tests

# Integration tests (requires Docker)
dotnet test tests/DataEngine.Integration.Tests

# All tests with output
dotnet test DataEngine.sln --logger "console;verbosity=normal"

# Pack NuGet packages (when ready)
dotnet pack src/DataEngine/DataEngine.csproj -c Release -o ./nupkgs
```

---

## Summary — All Three Parts

| Part | Content | Status |
|------|---------|--------|
| Part 1 | Domain models, all interfaces, all DTOs (partial), query compiler, schema cache, executors, security guards, DI skeleton | ✅ Complete |
| Part 2 | All orchestrators, DataEngineService, DataEngineBuilder, final DI wiring, ConnectionFactory, MultiDatabaseRouter, Null objects, BulkWriteExecutor, SchemaRefreshService, FieldMapperProvider, AuditWriter, SavedQueryProvider, DataTypeMapper, internal result models, sample API | ✅ Complete |
| Part 3 | 4 missing DTO records, all 5 .csproj files, GlobalUsings per project, solution scaffold commands, test project setup, integration test suite | ✅ Complete |

**DataEngine is now fully specified.** `dotnet build` should produce zero errors against the complete file set from all three parts.

---

*End of DataEngine Architecture — Part 3 (Final)*
