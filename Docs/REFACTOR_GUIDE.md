# DataEngine — Refactor Analysis & Migration Guide

---

## 1. Current Structure Analysis (What I Found)

### Project layout
```
DataEngine/
  Abstractions/
    IDynamicReadEngine.cs          ← Read contract
    IDynamicTransactionProcessor.cs ← Write contract
    IQueryValidator.cs
    ITransactionValidator.cs
  Model/
    ColumnMetadata.cs
    FetchConfig.cs                 ← Query config + FilterCondition + FetchResult
    TransactionRequest.cs
    TransactionResult.cs
  Repository/
    ApplicationTableMetadataRepository.cs
  Services/
    DynamicReadEngine.cs           ← Full read logic (~120 lines)
    DynamicTransactionProcessor.cs ← Full write logic (~180 lines)
    QueryValidator.cs
    TransactionValidator.cs
  Extensions/
    ServiceCollectionExtensions.cs
```

### What the project already does well
- Clean interface/implementation separation
- Validators decoupled from executors
- MySqlCommand parameters properly escaped
- JsonElement unwrapping via GetRawValue()
- Transaction wrapping with rollback in DynamicTransactionProcessor

---

## 2. What Needed to Change (Gap Analysis)

### Problem 1: No single central contract
`IDynamicReadEngine` and `IDynamicTransactionProcessor` exist as two separate,
disconnected interfaces. There is no unified entry point. New callers must inject
two separate services to do a read and a write.

**Fix:** Added `ITransaction` with `TransactionProcess` and `ExecuteQuery`
as the single central contract. Both old interfaces still work — they delegate.

---

### Problem 2: GetRawValue duplicated in two services
The JSON→primitive unwrapping logic (GetRawValue) was copy-pasted identically
into both `DynamicReadEngine` and `DynamicTransactionProcessor`.

**Fix:** Extracted to `DataEngineJsonHelper.GetRawValue()` and `ScrubRow()`.
Both services use the shared helper. No more duplication.

---

### Problem 3: Hardcoded database name in repository
```csharp
// BEFORE — hardcoded, breaks every other project
WHERE TABLE_SCHEMA = 'jan_itaccessreq_db'
```
This is a library — hardcoding a specific project's database name means
it cannot work anywhere else without editing source code.

**Fix:** Database name extracted from the connection string at constructor time:
```csharp
var builder = new MySqlConnectionStringBuilder(_connectionString);
_databaseName = builder.Database;
```
Now it works with any database automatically.

---

### Problem 4: Silent exception swallowing in DynamicReadEngine
```csharp
// BEFORE
catch (Exception ex)
{
    result.Success = false;
    result.Message = ex.Message;  // swallowed — no logging, no stack trace
    return result;
}
```
Errors were returned as message strings with no structured logging,
no stack trace preservation, no file output.

**Fix:** Every catch block now:
- Logs via `ILogger` with full context (operation type, entity, elapsed time)
- Writes to the rolling file log via `DataEngineFileLogger`
- Preserves the exception type and stack trace
- Returns meaningful, specific messages

---

### Problem 5: No file logging
The project had `ILogger` injected but no output to a file.
For a data engine running background database operations,
file logs are essential for post-incident investigation.

**Fix:** Added `DataEngineFileLogger` which writes one JSON line per operation
to `logs/dataengine-{yyyy-MM-dd}.log`. Each line contains:
- Timestamp, operation type, transaction ID
- Entity name, user ID
- Query text, parameter count
- Success/failure, elapsed milliseconds
- Error message, exception type, stack trace (on failure)

File path is configurable via `DataEngine:LogDirectory` in appsettings.

---

### Problem 6: TransactionResult had no operation counts
After a transaction, callers had no way to know how many rows were
inserted vs updated vs deleted — just a flat success/failure boolean.

**Fix:** Added `InsertedCount`, `UpdatedCount`, `DeletedCount` to `TransactionResult`.
The `Message` field now includes these in its text too.

---

### Problem 7: DI registration lifetime inconsistency
`ApplicationTableMetadataRepository` was `AddSingleton` but `DynamicTransactionProcessor`
was `AddScoped`. The processor holds a connection string (fine for singleton) but
previous registrations were mismatched.

**Fix:** Consistent lifetime model:
- Stateless validators → `AddSingleton`
- `ApplicationTableMetadataRepository` → `AddSingleton` (reads config once, no state)
- `DataEngineFileLogger` → `AddSingleton` (manages file locks)
- `DataEngineTransaction` and all engine interfaces → `AddScoped` (one per request)

---

## 3. Refactored File Summary

| File | Status | Change |
|------|--------|--------|
| `Abstractions/ITransaction.cs` | **NEW** | Central contract with TransactionProcess + ExecuteQuery |
| `Abstractions/IDynamicReadEngine.cs` | Kept | Unchanged — backwards compat |
| `Abstractions/IDynamicTransactionProcessor.cs` | Kept | Unchanged — backwards compat |
| `Abstractions/IQueryValidator.cs` | Kept | Unchanged |
| `Abstractions/ITransactionValidator.cs` | Kept | Unchanged |
| `Model/FetchConfig.cs` | Kept | Unchanged |
| `Model/TransactionRequest.cs` | Kept | Comments improved, no logic change |
| `Model/TransactionResult.cs` | **UPDATED** | Added InsertedCount, UpdatedCount, DeletedCount |
| `Model/ColumnMetadata.cs` | Kept | Unchanged |
| `Logging/DataEngineLogEntry.cs` | **NEW** | Structured log entry model |
| `Logging/DataEngineFileLogger.cs` | **NEW** | Rolling daily JSON file logger |
| `Repository/ApplicationTableMetadataRepository.cs` | **UPDATED** | Database name from connection string, not hardcoded |
| `Services/DataEngineJsonHelper.cs` | **NEW** | Shared GetRawValue + ScrubRow (was duplicated) |
| `Services/DataEngineTransaction.cs` | **NEW** | Central engine implementing ITransaction + both old interfaces |
| `Services/DynamicReadEngine.cs` | **UPDATED** | Thin shim → delegates to DataEngineTransaction |
| `Services/DynamicTransactionProcessor.cs` | **UPDATED** | Thin shim → delegates to DataEngineTransaction |
| `Services/QueryValidator.cs` | Kept | Logic unchanged, comments improved |
| `Services/TransactionValidator.cs` | Kept | Logic unchanged, error messages improved |
| `Extensions/ServiceCollectionExtensions.cs` | **UPDATED** | Registers new services, all interfaces resolve to DataEngineTransaction |
| `DataEngine.csproj` | **UPDATED** | Explicit versions pinned |

---

## 4. Migration Steps

### Step 1 — Replace files
Copy the refactored files into your project, replacing originals.
The folder structure is identical — no moves needed.

### Step 2 — Existing callers: zero changes required
Any code injecting `IDynamicTransactionProcessor`:
```csharp
// BEFORE — still works, no change needed
public class MyController(IDynamicTransactionProcessor processor) { ... }
await processor.ProcessTransactionAsync(request);

// AFTER — new preferred way (optional upgrade)
public class MyController(ITransaction engine) { ... }
await engine.TransactionProcess(request);
```

Any code injecting `IDynamicReadEngine`:
```csharp
// BEFORE — still works, no change needed
public class MyController(IDynamicReadEngine reader) { ... }
await reader.ExecuteQueryAsync(config, connString);

// AFTER — new preferred way (optional upgrade)
public class MyController(ITransaction engine) { ... }
await engine.ExecuteQuery(config, connString);
```

### Step 3 — DI registration: unchanged
```csharp
// Program.cs — no change
services.AddDynamicDataEngine();
```

### Step 4 — Optional: Configure log directory
```json
// appsettings.json
{
  "DataEngine": {
    "LogDirectory": "logs/dataengine"
  }
}
```
Default is `logs/` relative to the application root if not set.

### Step 5 — Optional: Use new operation counts
```csharp
var result = await engine.TransactionProcess(request);
Console.WriteLine($"Inserted: {result.InsertedCount}");
Console.WriteLine($"Updated:  {result.UpdatedCount}");
Console.WriteLine($"Deleted:  {result.DeletedCount}");
```

---

## 5. Potential Risks & Compatibility Concerns

### Risk 1: TransactionResult model change
`InsertedCount`, `UpdatedCount`, `DeletedCount` are new properties with default value 0.
Existing code reading `TransactionResult` will not break — new fields are additive.
If you serialize `TransactionResult` to JSON and send it to a client, the client
will see three new fields. This is a non-breaking addition.

### Risk 2: DynamicReadEngine and DynamicTransactionProcessor shim classes
These are retained but now delegate to `DataEngineTransaction`. If any code
references the concrete class directly (e.g. `new DynamicReadEngine(...)`)
rather than via the interface, the constructor signature has changed.
Always inject via the interface — `IDynamicReadEngine`, `IDynamicTransactionProcessor`.

### Risk 3: Database name resolution
The repository previously had `jan_itaccessreq_db` hardcoded.
After this refactor it reads from the connection string.
**Verify your connection string includes the `Database=` parameter.**
If it does, behaviour is identical. If it was missing the Database parameter
and relying on the hardcode, add it now.

### Risk 4: Log directory permissions
`DataEngineFileLogger` creates the log directory if it doesn't exist.
In containerised/cloud deployments, ensure the application has write
permission to the configured `DataEngine:LogDirectory` path.

---

## 6. Architecture After Refactor

```
Consumer (Controller / Service)
         │
         ▼
    ITransaction                ← new central contract
  ┌──────┴──────┐
  │             │
TransactionProcess    ExecuteQuery
  │             │
  └──────┬──────┘
         │
  DataEngineTransaction        ← single implementation
  ├── also implements IDynamicTransactionProcessor  (backwards compat)
  ├── also implements IDynamicReadEngine            (backwards compat)
  ├── uses ApplicationTableMetadataRepository
  ├── uses ITransactionValidator
  ├── uses IQueryValidator
  ├── uses DataEngineJsonHelper   (shared, static)
  └── uses DataEngineFileLogger   (file log output)
```

Old callers using `IDynamicTransactionProcessor` or `IDynamicReadEngine`
resolve to the same `DataEngineTransaction` instance via DI —
no behaviour change, no code change required.
```
