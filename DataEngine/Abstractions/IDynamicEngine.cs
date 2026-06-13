using DataEngine.Model;

namespace DataEngine.Abstractions;

/// <summary>
/// Central DataEngine contract.
/// All CRUD operations and query execution route through this interface.
/// TransactionProcess handles all write operations (Create, Update, Delete).
/// ExecuteQuery handles all read operations (Select, Filter, Sort, Paginate).
/// </summary>
public interface IDynamicEngine
{
    /// <summary>
    /// Processes all write operations: INSERT, UPDATE, DELETE.
    /// Routes internally based on presence/absence of primary key in the payload.
    /// All operations are atomic — commit on full success, rollback on any failure.
    /// </summary>
    Task<TransactionResult> TransactionProcess(
        TransactionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a read/fetch operation against the target database.
    /// Supports filtering, sorting, pagination, and direct query execution.
    /// </summary>
    Task<FetchResult> ExecuteQuery(
        FetchConfig config,
        string connectionString,
        CancellationToken cancellationToken = default);
}
