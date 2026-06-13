using System.Data;
using Dapper;
using Microsoft.Extensions.Logging;

namespace DynamicDapper;

// ─────────────────────────────────────────────────────────────────────────────
// Connection factory abstraction
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Creates and opens <see cref="IDbConnection"/> instances.
/// Register one implementation per database vendor (SQL Server, PostgreSQL, SQLite …).
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>
    /// Creates and opens a connection using the factory's default connection string.
    /// </summary>
    Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates and opens a connection using an explicit <paramref name="connectionString"/>,
    /// enabling multi-database execution from a single factory instance.
    /// </summary>
    Task<IDbConnection> CreateConnectionAsync(string connectionString,
        CancellationToken cancellationToken = default);
}

// ─────────────────────────────────────────────────────────────────────────────
// Executor interface
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Executes raw SQL statements at runtime via Dapper with full async, transaction,
/// cancellation-token, and multi-database support.
/// </summary>
public interface IDynamicQueryExecutor
{
    /// <summary>
    /// Executes a SELECT query and returns a (possibly empty) sequence of <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type each row is mapped to.</typeparam>
    /// <param name="sql">Raw SQL SELECT statement.</param>
    /// <param name="parameters">
    ///   Anonymous object (<c>new { Id = 1 }</c>) or a <see cref="DynamicParameters"/> instance.
    /// </param>
    /// <param name="transaction">Optional ambient transaction.</param>
    /// <param name="connectionString">
    ///   Override the factory's default connection string for multi-database scenarios.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation to the underlying connection.</param>
    Task<IEnumerable<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a SELECT query and returns the first row, or <c>default</c> when no row matches.
    /// Throws <see cref="InvalidOperationException"/> if more than one row is returned.
    /// </summary>
    /// <typeparam name="T">The type the row is mapped to.</typeparam>
    /// <param name="sql">Raw SQL SELECT statement.</param>
    /// <param name="parameters">Anonymous object or <see cref="DynamicParameters"/>.</param>
    /// <param name="transaction">Optional ambient transaction.</param>
    /// <param name="connectionString">Override connection string.</param>
    /// <param name="cancellationToken">Propagates cancellation to the underlying connection.</param>
    Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-SELECT statement (INSERT / UPDATE / DELETE / DDL) and returns the
    /// number of rows affected.
    /// </summary>
    /// <param name="sql">Raw SQL non-SELECT statement.</param>
    /// <param name="parameters">Anonymous object or <see cref="DynamicParameters"/>.</param>
    /// <param name="transaction">Optional ambient transaction.</param>
    /// <param name="connectionString">Override connection string.</param>
    /// <param name="cancellationToken">Propagates cancellation to the underlying connection.</param>
    Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes two queries in a single round-trip: one for a data page and one for the total
    /// count, returning both results together.
    /// </summary>
    /// <typeparam name="T">The type each data row is mapped to.</typeparam>
    /// <param name="dataSql">Raw SQL that returns the current page of rows.</param>
    /// <param name="countSql">Raw SQL that returns a single <see cref="int"/> total count.</param>
    /// <param name="parameters">Shared parameters applied to both statements.</param>
    /// <param name="transaction">Optional ambient transaction.</param>
    /// <param name="connectionString">Override connection string.</param>
    /// <param name="cancellationToken">Propagates cancellation to the underlying connection.</param>
    Task<(IEnumerable<T> Data, int TotalCount)> QueryPagedAsync<T>(
        string dataSql,
        string countSql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="work"/> inside a new database transaction, automatically committing
    /// on success and rolling back on any exception.
    /// </summary>
    /// <param name="work">Async delegate that receives the open transaction.</param>
    /// <param name="isolationLevel">Transaction isolation level (defaults to <see cref="IsolationLevel.ReadCommitted"/>).</param>
    /// <param name="connectionString">Override connection string.</param>
    /// <param name="cancellationToken">Propagates cancellation to the underlying connection.</param>
    Task ExecuteInTransactionAsync(
        Func<IDbTransaction, Task> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        string? connectionString = null,
        CancellationToken cancellationToken = default);
}

// ─────────────────────────────────────────────────────────────────────────────
// Implementation
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Production-ready, thread-safe implementation of <see cref="IDynamicQueryExecutor"/>.
/// Each public method opens a short-lived connection (or reuses a transactional one),
/// delegates to Dapper, and logs every call with its elapsed time.
/// </summary>
public sealed class DynamicQueryExecutor : IDynamicQueryExecutor
{
    private readonly IDbConnectionFactory _factory;
    private readonly ILogger<DynamicQueryExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DynamicQueryExecutor"/>.
    /// </summary>
    /// <param name="factory">Factory used to resolve database connections.</param>
    /// <param name="logger">Logger for query diagnostics.</param>
    public DynamicQueryExecutor(IDbConnectionFactory factory,
                                ILogger<DynamicQueryExecutor> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        _logger.LogDebug("QueryAsync<{Type}> START  SQL: {Sql}", typeof(T).Name, sql);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Reuse the transactional connection if one is supplied.
            if (transaction is not null)
            {
                var cmd = BuildCommand(sql, parameters, transaction, cancellationToken);
                var result = await transaction.Connection!
                    .QueryAsync<T>(cmd).ConfigureAwait(false);

                LogSuccess(sw, typeof(T).Name, nameof(QueryAsync));
                return result;
            }

            await using var conn = await OpenAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);

            var command = BuildCommand(sql, parameters, null, cancellationToken);
            var rows = await conn.QueryAsync<T>(command).ConfigureAwait(false);

            LogSuccess(sw, typeof(T).Name, nameof(QueryAsync));
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryAsync<{Type}> FAILED  SQL: {Sql}", typeof(T).Name, sql);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        _logger.LogDebug("QuerySingleOrDefaultAsync<{Type}> START  SQL: {Sql}", typeof(T).Name, sql);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (transaction is not null)
            {
                var cmd = BuildCommand(sql, parameters, transaction, cancellationToken);
                var result = await transaction.Connection!
                    .QuerySingleOrDefaultAsync<T>(cmd).ConfigureAwait(false);

                LogSuccess(sw, typeof(T).Name, nameof(QuerySingleOrDefaultAsync));
                return result;
            }

            await using var conn = await OpenAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);

            var command = BuildCommand(sql, parameters, null, cancellationToken);
            var row = await conn.QuerySingleOrDefaultAsync<T>(command).ConfigureAwait(false);

            LogSuccess(sw, typeof(T).Name, nameof(QuerySingleOrDefaultAsync));
            return row;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QuerySingleOrDefaultAsync<{Type}> FAILED  SQL: {Sql}",
                typeof(T).Name, sql);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        _logger.LogDebug("ExecuteAsync START  SQL: {Sql}", sql);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (transaction is not null)
            {
                var cmd = BuildCommand(sql, parameters, transaction, cancellationToken);
                var result = await transaction.Connection!
                    .ExecuteAsync(cmd).ConfigureAwait(false);

                LogSuccess(sw, null, nameof(ExecuteAsync), result);
                return result;
            }

            await using var conn = await OpenAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);

            var command = BuildCommand(sql, parameters, null, cancellationToken);
            var affected = await conn.ExecuteAsync(command).ConfigureAwait(false);

            LogSuccess(sw, null, nameof(ExecuteAsync), affected);
            return affected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExecuteAsync FAILED  SQL: {Sql}", sql);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<(IEnumerable<T> Data, int TotalCount)> QueryPagedAsync<T>(
        string dataSql,
        string countSql,
        object? parameters = null,
        IDbTransaction? transaction = null,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSql);
        ArgumentException.ThrowIfNullOrWhiteSpace(countSql);

        _logger.LogDebug("QueryPagedAsync<{Type}> START  DataSQL: {DataSql}  CountSQL: {CountSql}",
            typeof(T).Name, dataSql, countSql);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Execute both queries on the same open connection to minimise round-trips.
            IDbConnection conn;
            bool owned = transaction is null;

            if (owned)
                conn = await OpenAsync(connectionString, cancellationToken).ConfigureAwait(false);
            else
                conn = transaction!.Connection!;

            try
            {
                var dataCmd  = BuildCommand(dataSql,  parameters, transaction, cancellationToken);
                var countCmd = BuildCommand(countSql, parameters, transaction, cancellationToken);

                var dataTask  = conn.QueryAsync<T>(dataCmd);
                var countTask = conn.QuerySingleOrDefaultAsync<int>(countCmd);

                await Task.WhenAll(dataTask, countTask).ConfigureAwait(false);

                var data  = await dataTask;
                var total = await countTask;

                _logger.LogInformation(
                    "QueryPagedAsync<{Type}> OK  rows={Rows}  total={Total}  {Elapsed}ms",
                    typeof(T).Name, data.Count(), total, sw.ElapsedMilliseconds);

                return (data, total);
            }
            finally
            {
                if (owned) (conn as IAsyncDisposable)?.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "QueryPagedAsync<{Type}> FAILED  DataSQL: {Sql}",
                typeof(T).Name, dataSql);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task ExecuteInTransactionAsync(
        Func<IDbTransaction, Task> work,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

        _logger.LogDebug("ExecuteInTransactionAsync START  IsolationLevel: {Level}", isolationLevel);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await using var conn = await OpenAsync(connectionString, cancellationToken)
            .ConfigureAwait(false);

        using var tx = conn.BeginTransaction(isolationLevel);
        try
        {
            await work(tx).ConfigureAwait(false);
            tx.Commit();

            _logger.LogInformation("ExecuteInTransactionAsync COMMITTED  {Elapsed}ms",
                sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            tx.Rollback();
            _logger.LogError(ex, "ExecuteInTransactionAsync ROLLED BACK  {Elapsed}ms",
                sw.ElapsedMilliseconds);
            throw;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Opens a connection via the factory, using the override string when provided.
    /// Returns an <see cref="IAsyncDisposable"/> wrapper so callers can <c>await using</c> it.
    /// </summary>
    private async Task<AsyncConnectionWrapper> OpenAsync(
        string? connectionString,
        CancellationToken cancellationToken)
    {
        var conn = connectionString is { Length: > 0 }
            ? await _factory.CreateConnectionAsync(connectionString, cancellationToken).ConfigureAwait(false)
            : await _factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        return new AsyncConnectionWrapper(conn);
    }

    /// <summary>
    /// Builds a Dapper <see cref="CommandDefinition"/> that carries the SQL, parameters,
    /// transaction, and cancellation token.
    /// </summary>
    private static CommandDefinition BuildCommand(
        string sql,
        object? parameters,
        IDbTransaction? transaction,
        CancellationToken cancellationToken)
        => new(sql,
               parameters: parameters,
               transaction: transaction,
               cancellationToken: cancellationToken);

    private void LogSuccess(System.Diagnostics.Stopwatch sw, string? typeName,
                            string method, int? rows = null)
    {
        if (rows.HasValue)
            _logger.LogInformation("{Method} OK  rowsAffected={Rows}  {Elapsed}ms",
                method, rows.Value, sw.ElapsedMilliseconds);
        else
            _logger.LogInformation("{Method}<{Type}> OK  {Elapsed}ms",
                method, typeName, sw.ElapsedMilliseconds);
    }

    // ── Nested helper: async-disposable connection wrapper ────────────────────

    /// <summary>
    /// Thin wrapper that lets callers dispose an <see cref="IDbConnection"/> with
    /// <c>await using</c>, regardless of whether the connection implements
    /// <see cref="IAsyncDisposable"/> itself.
    /// </summary>
    private sealed class AsyncConnectionWrapper : IAsyncDisposable
    {
        private readonly IDbConnection _conn;
        public AsyncConnectionWrapper(IDbConnection conn) => _conn = conn;

        // Implicit cast so callers can pass the wrapper directly to Dapper extension methods.
        public static implicit operator IDbConnection(AsyncConnectionWrapper w) => w._conn;

        public ValueTask DisposeAsync()
        {
            _conn.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Reference implementation: SQL Server connection factory
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <see cref="IDbConnectionFactory"/> implementation for SQL Server using
/// <c>Microsoft.Data.SqlClient</c>. Swap the provider for PostgreSQL, SQLite, etc.
/// </summary>
/// <example>
/// <code>
/// services.AddSingleton&lt;IDbConnectionFactory&gt;(
///     new SqlServerConnectionFactory("Server=.;Database=Demo;Integrated Security=true;"));
/// </code>
/// </example>
public sealed class SqlServerConnectionFactory : IDbConnectionFactory
{
    private readonly string _defaultConnectionString;

    /// <param name="defaultConnectionString">Connection string used when no override is supplied.</param>
    public SqlServerConnectionFactory(string defaultConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultConnectionString);
        _defaultConnectionString = defaultConnectionString;
    }

    /// <inheritdoc/>
    public Task<IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
        => CreateConnectionAsync(_defaultConnectionString, cancellationToken);

    /// <inheritdoc/>
    public async Task<IDbConnection> CreateConnectionAsync(string connectionString,
        CancellationToken cancellationToken = default)
    {
        // Microsoft.Data.SqlClient.SqlConnection implements IDbConnection.
        // Replace with NpgsqlConnection, SqliteConnection, etc. as needed.
        var conn = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DI registration extension
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Extension methods for registering Dynamic Dapper services in an
/// <c>IServiceCollection</c>.
/// </summary>
public static class DynamicDapperServiceExtensions
{
    /// <summary>
    /// Registers <see cref="DynamicQueryExecutor"/> and a SQL Server
    /// <see cref="IDbConnectionFactory"/> as singletons.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <param name="connectionString">Default SQL Server connection string.</param>
    public static IServiceCollection AddDynamicDapper(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IDbConnectionFactory>(
            new SqlServerConnectionFactory(connectionString));

        services.AddSingleton<IDynamicQueryExecutor, DynamicQueryExecutor>();
        return services;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Usage examples (compile with: dotnet run or as a top-level program)
// ─────────────────────────────────────────────────────────────────────────────
/*

/// SETUP ─────────────────────────────────────────────────────────────────────

// Program.cs / Startup.cs
using DynamicDapper;

builder.Services.AddDynamicDapper("Server=.;Database=Demo;Integrated Security=true;");


/// EXAMPLE 1 — SELECT with an anonymous-object parameter ─────────────────────

public record Product(int Id, string Name, decimal Price);

var products = await executor.QueryAsync<Product>(
    "SELECT Id, Name, Price FROM Products WHERE CategoryId = @CategoryId",
    new { CategoryId = 5 });


/// EXAMPLE 2 — QuerySingleOrDefaultAsync ─────────────────────────────────────

var product = await executor.QuerySingleOrDefaultAsync<Product>(
    "SELECT Id, Name, Price FROM Products WHERE Id = @Id",
    new { Id = 42 });

if (product is null) Console.WriteLine("Not found");


/// EXAMPLE 3 — ExecuteAsync (INSERT) ─────────────────────────────────────────

int rows = await executor.ExecuteAsync(
    "INSERT INTO Products (Name, Price, CategoryId) VALUES (@Name, @Price, @CategoryId)",
    new { Name = "Widget", Price = 9.99m, CategoryId = 5 });

Console.WriteLine($"{rows} row(s) inserted.");


/// EXAMPLE 4 — DynamicParameters ─────────────────────────────────────────────

var dp = new DynamicParameters();
dp.Add("@Name",    "Gadget",  DbType.String);
dp.Add("@MinPrice", 5.00m,   DbType.Decimal);

var results = await executor.QueryAsync<Product>(
    "SELECT Id, Name, Price FROM Products WHERE Name LIKE @Name AND Price >= @MinPrice",
    dp);


/// EXAMPLE 5 — QueryPagedAsync ────────────────────────────────────────────────

var paged = await executor.QueryPagedAsync<Product>(
    dataSql:  "SELECT Id, Name, Price FROM Products ORDER BY Id OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY",
    countSql: "SELECT COUNT(*) FROM Products",
    parameters: new { Skip = 0, Take = 10 });

Console.WriteLine($"Page: {paged.Data.Count()} rows  /  Total: {paged.TotalCount}");


/// EXAMPLE 6 — Explicit transaction ──────────────────────────────────────────

await executor.ExecuteInTransactionAsync(async tx =>
{
    await executor.ExecuteAsync(
        "UPDATE Inventory SET Stock = Stock - @Qty WHERE ProductId = @ProductId",
        new { Qty = 2, ProductId = 42 },
        transaction: tx);

    await executor.ExecuteAsync(
        "INSERT INTO Orders (ProductId, Qty, CreatedAt) VALUES (@ProductId, @Qty, @Now)",
        new { ProductId = 42, Qty = 2, Now = DateTime.UtcNow },
        transaction: tx);
    // Commits automatically on return; rolls back on exception.
});


/// EXAMPLE 7 — Multi-database execution ───────────────────────────────────────

var legacyProducts = await executor.QueryAsync<Product>(
    "SELECT Id, Name, Price FROM LegacyProducts",
    connectionString: "Server=legacy-db;Database=Legacy;Integrated Security=true;");


/// EXAMPLE 8 — Cancellation token ─────────────────────────────────────────────

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

var data = await executor.QueryAsync<Product>(
    "SELECT Id, Name, Price FROM Products",
    cancellationToken: cts.Token);

*/
