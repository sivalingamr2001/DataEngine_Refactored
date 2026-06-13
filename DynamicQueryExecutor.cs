using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;

// ─────────────────────────────────────────────────────────────────────────────
// Enums & DTOs
// ─────────────────────────────────────────────────────────────────────────────

public enum DbProvider
{
    SqlServer,
    Oracle
}

public sealed class QueryRequest
{
    public string            ConnectionString { get; init; } = string.Empty;
    public DbProvider        Provider         { get; init; } = DbProvider.SqlServer;
    public string            Sql              { get; init; } = string.Empty;

    /// <summary>
    /// Optional parameters – pass an anonymous object or DynamicParameters.
    /// e.g. new { p_ou_id = 10, p_year = 2024 }
    /// </summary>
    public object?           Parameters       { get; init; }

    /// <summary>
    /// Command type: Text (default), StoredProcedure, TableDirect.
    /// </summary>
    public CommandType       CommandType      { get; init; } = CommandType.Text;

    /// <summary>Timeout in seconds; null = Dapper default (30 s).</summary>
    public int?              TimeoutSeconds   { get; init; }
}

public sealed class QueryResult<T>
{
    public bool              Success          { get; init; }
    public IEnumerable<T>?   Data             { get; init; }
    public int?              RowsAffected     { get; init; }
    public string?           ErrorMessage     { get; init; }
    public string?           ErrorType        { get; init; }
    public TimeSpan          Elapsed          { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
// Connection factory
// ─────────────────────────────────────────────────────────────────────────────

public static class DbConnectionFactory
{
    /// <summary>
    /// Creates and OPENS a connection for the specified provider.
    /// Caller is responsible for disposal (use 'await using').
    /// </summary>
    public static async Task<IDbConnection> CreateAsync(
        string      connectionString,
        DbProvider  provider,
        int?        timeoutSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));

        IDbConnection conn = provider switch
        {
            DbProvider.SqlServer => new SqlConnection(connectionString),
            DbProvider.Oracle    => new OracleConnection(connectionString),
            _                    => throw new NotSupportedException($"Provider '{provider}' is not supported.")
        };

        try
        {
            // OracleConnection.OpenAsync is available; SqlConnection also supports it.
            if (conn is SqlConnection sql)
                await sql.OpenAsync();
            else if (conn is OracleConnection ora)
                await ora.OpenAsync();
            else
                conn.Open();

            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Dynamic query executor
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DynamicQueryExecutor
{
    // ── SELECT / multi-result ────────────────────────────────────────────────

    /// <summary>
    /// Executes a raw SELECT query and returns typed rows.
    /// Maps each column dynamically when T = IDictionary&lt;string, object&gt;.
    /// </summary>
    public async Task<QueryResult<T>> QueryAsync<T>(QueryRequest request)
    {
        ValidateRequest(request);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await using var conn = (System.IAsyncDisposable)
                await DbConnectionFactory.CreateAsync(request.ConnectionString, request.Provider, request.TimeoutSeconds);

            var rows = await ((IDbConnection)conn).QueryAsync<T>(
                sql:            request.Sql,
                param:          NormalizeParams(request.Parameters, request.Provider),
                commandType:    request.CommandType,
                commandTimeout: request.TimeoutSeconds
            );

            sw.Stop();
            return new QueryResult<T>
            {
                Success = true,
                Data    = rows,
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail<T>(ex, sw.Elapsed);
        }
    }

    /// <summary>
    /// Returns rows as dynamic (ExpandoObject), column names preserved.
    /// Useful when schema is not known at compile time.
    /// </summary>
    public async Task<QueryResult<dynamic>> QueryDynamicAsync(QueryRequest request)
        => await QueryAsync<dynamic>(request);

    // ── Single scalar ────────────────────────────────────────────────────────

    public async Task<QueryResult<T>> QueryScalarAsync<T>(QueryRequest request)
    {
        ValidateRequest(request);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await using var conn = (System.IAsyncDisposable)
                await DbConnectionFactory.CreateAsync(request.ConnectionString, request.Provider, request.TimeoutSeconds);

            var value = await ((IDbConnection)conn).ExecuteScalarAsync<T>(
                sql:            request.Sql,
                param:          NormalizeParams(request.Parameters, request.Provider),
                commandType:    request.CommandType,
                commandTimeout: request.TimeoutSeconds
            );

            sw.Stop();
            return new QueryResult<T>
            {
                Success = true,
                Data    = value is null ? [] : [value],
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail<T>(ex, sw.Elapsed);
        }
    }

    // ── INSERT / UPDATE / DELETE ─────────────────────────────────────────────

    public async Task<QueryResult<int>> ExecuteAsync(QueryRequest request)
    {
        ValidateRequest(request);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await using var conn = (System.IAsyncDisposable)
                await DbConnectionFactory.CreateAsync(request.ConnectionString, request.Provider, request.TimeoutSeconds);

            var affected = await ((IDbConnection)conn).ExecuteAsync(
                sql:            request.Sql,
                param:          NormalizeParams(request.Parameters, request.Provider),
                commandType:    request.CommandType,
                commandTimeout: request.TimeoutSeconds
            );

            sw.Stop();
            return new QueryResult<int>
            {
                Success      = true,
                RowsAffected = affected,
                Elapsed      = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail<int>(ex, sw.Elapsed);
        }
    }

    // ── Oracle cursor / multi-result set ─────────────────────────────────────

    /// <summary>
    /// Executes an Oracle stored procedure that returns a SYS_REFCURSOR
    /// bound to the named output parameter.
    /// 
    /// Example:
    ///   var dp = new OracleDynamicParameters();
    ///   dp.Add("p_ou_id",  10,   OracleMappingType.Int32,    ParameterDirection.Input);
    ///   dp.Add("p_cursor", null, OracleMappingType.RefCursor, ParameterDirection.Output);
    ///   var result = await executor.QueryOracleCursorAsync&lt;SalesDto&gt;(request, dp, "p_cursor");
    /// </summary>
    public async Task<QueryResult<T>> QueryOracleCursorAsync<T>(
        QueryRequest            request,
        OracleDynamicParameters oracleParams,
        string                  cursorParamName)
    {
        ValidateRequest(request, requireOracle: true);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await using var conn = (System.IAsyncDisposable)
                await DbConnectionFactory.CreateAsync(request.ConnectionString, DbProvider.Oracle, request.TimeoutSeconds);

            await ((IDbConnection)conn).ExecuteAsync(
                sql:            request.Sql,
                param:          oracleParams,
                commandType:    CommandType.StoredProcedure,
                commandTimeout: request.TimeoutSeconds
            );

            var rows = oracleParams.Get<IEnumerable<T>>(cursorParamName);

            sw.Stop();
            return new QueryResult<T>
            {
                Success = true,
                Data    = rows,
                Elapsed = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return Fail<T>(ex, sw.Elapsed);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void ValidateRequest(QueryRequest request, bool requireOracle = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            throw new ArgumentException("ConnectionString is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Sql))
            throw new ArgumentException("Sql must not be empty.", nameof(request));

        if (requireOracle && request.Provider != DbProvider.Oracle)
            throw new InvalidOperationException("This method requires DbProvider.Oracle.");
    }

    /// <summary>
    /// Oracle uses :param notation; Dapper handles this transparently when
    /// OracleConnection is used, BUT anonymous objects need no special handling.
    /// This hook is reserved for future provider-specific normalization.
    /// </summary>
    private static object? NormalizeParams(object? parameters, DbProvider provider)
        => parameters; // Dapper + OracleConnection resolves : prefix automatically.

    private static QueryResult<T> Fail<T>(Exception ex, TimeSpan elapsed)
    {
        // Unwrap common Oracle/SQL exception messages for clean logging.
        var message = ex is OracleException ora
            ? $"ORA-{ora.Number}: {ora.Message}"
            : ex.Message;

        return new QueryResult<T>
        {
            Success      = false,
            ErrorMessage = message,
            ErrorType    = ex.GetType().Name,
            Elapsed      = elapsed
        };
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// OracleDynamicParameters  (Dapper doesn't ship Oracle-specific params)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Lightweight wrapper that lets you add Oracle-typed parameters
/// (including REF CURSOR) to a Dapper call.
/// </summary>
public sealed class OracleDynamicParameters : SqlMapper.IDynamicParameters
{
    private readonly List<OracleParamDef> _params = [];

    public void Add(
        string              name,
        object?             value,
        OracleMappingType   dbType,
        ParameterDirection  direction,
        int?                size = null)
    {
        _params.Add(new OracleParamDef(name, value, dbType, direction, size));
    }

    public T Get<T>(string name)
    {
        var p = _params.Find(x => x.Name == name)
                ?? throw new KeyNotFoundException($"Parameter '{name}' not found.");
        return (T)(p.OracleParam!.Value ?? default(T)!);
    }

    void SqlMapper.IDynamicParameters.AddParameters(IDbCommand command, SqlMapper.Identity identity)
    {
        foreach (var def in _params)
        {
            var p = ((OracleCommand)command).CreateParameter();
            p.ParameterName = def.Name;
            p.OracleDbType  = (OracleDbType)(int)def.DbType;   // enum value mapping
            p.Direction     = def.Direction;
            if (def.Value is not null)  p.Value = def.Value;
            if (def.Size  is not null)  p.Size  = def.Size.Value;
            ((OracleCommand)command).Parameters.Add(p);
            def.OracleParam = p;
        }
    }

    private sealed class OracleParamDef(
        string name, object? value, OracleMappingType dbType,
        ParameterDirection direction, int? size)
    {
        public string             Name        { get; } = name;
        public object?            Value       { get; } = value;
        public OracleMappingType  DbType      { get; } = dbType;
        public ParameterDirection Direction   { get; } = direction;
        public int?               Size        { get; } = size;
        public OracleParameter?   OracleParam { get; set; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// OracleMappingType  (mirror of Oracle.ManagedDataAccess OracleDbType)
// ─────────────────────────────────────────────────────────────────────────────

public enum OracleMappingType
{
    BFile       = 101,
    Blob        = 102,
    Byte        = 103,
    Char        = 104,
    Clob        = 105,
    Date        = 106,
    Decimal     = 107,
    Double      = 108,
    Long        = 109,
    LongRaw     = 110,
    Int16       = 111,
    Int32       = 112,
    Int64       = 113,
    IntervalDS  = 114,
    IntervalYM  = 115,
    NClob       = 116,
    NChar       = 117,
    NVarchar2   = 119,
    Raw         = 120,
    RefCursor   = 121,
    Single      = 122,
    TimeStamp   = 123,
    TimeStampLTZ= 124,
    TimeStampTZ = 125,
    Varchar2    = 126,
    XmlType     = 127,
    Array       = 128,
    Object      = 129,
    Ref         = 130,
    BinaryFloat = 132,
    BinaryDouble= 133
}

// ─────────────────────────────────────────────────────────────────────────────
// USAGE EXAMPLES  (remove in production)
// ─────────────────────────────────────────────────────────────────────────────

public static class UsageExamples
{
    private static readonly DynamicQueryExecutor Executor = new();

    // ── 1. SQL Server – raw SELECT ───────────────────────────────────────────
    public static async Task SqlServerSelectExample()
    {
        var result = await Executor.QueryDynamicAsync(new QueryRequest
        {
            ConnectionString = "Server=.;Database=JanaticsDB;Integrated Security=True;",
            Provider         = DbProvider.SqlServer,
            Sql              = "SELECT TOP 10 * FROM SalesOrders WHERE OrgId = @orgId",
            Parameters       = new { orgId = 5 },
            TimeoutSeconds   = 30
        });

        if (!result.Success)
        {
            Console.Error.WriteLine($"[{result.ErrorType}] {result.ErrorMessage}");
            return;
        }

        foreach (var row in result.Data!)
            Console.WriteLine(row);

        Console.WriteLine($"Elapsed: {result.Elapsed.TotalMilliseconds:F1} ms");
    }

    // ── 2. Oracle – raw SELECT ───────────────────────────────────────────────
    public static async Task OracleSelectExample()
    {
        var result = await Executor.QueryAsync<SalesRow>(new QueryRequest
        {
            ConnectionString = "User Id=apps;Password=secret;Data Source=ERPPROD;",
            Provider         = DbProvider.Oracle,
            Sql              = @"SELECT ou_name, net_sales, target
                                 FROM   v_ou_sales_perf
                                 WHERE  fiscal_year = :p_year",
            Parameters       = new { p_year = 2024 },
            TimeoutSeconds   = 60
        });

        if (!result.Success)
        {
            Console.Error.WriteLine($"Oracle error – {result.ErrorMessage}");
            return;
        }

        foreach (var row in result.Data!)
            Console.WriteLine($"{row.OuName} | {row.NetSales} | {row.Target}");
    }

    // ── 3. Oracle – stored proc with SYS_REFCURSOR ──────────────────────────
    public static async Task OracleCursorExample()
    {
        var dp = new OracleDynamicParameters();
        dp.Add("p_ou_id",  10,   OracleMappingType.Int32,     ParameterDirection.Input);
        dp.Add("p_year",   2024, OracleMappingType.Int32,     ParameterDirection.Input);
        dp.Add("p_cursor", null, OracleMappingType.RefCursor,  ParameterDirection.Output);

        var result = await Executor.QueryOracleCursorAsync<SalesRow>(
            new QueryRequest
            {
                ConnectionString = "User Id=apps;Password=secret;Data Source=ERPPROD;",
                Provider         = DbProvider.Oracle,
                Sql              = "JAN_GET_OU_SALES_PERFORMANCE",
                TimeoutSeconds   = 90
            },
            dp,
            cursorParamName: "p_cursor"
        );

        if (!result.Success)
        {
            Console.Error.WriteLine(result.ErrorMessage);
            return;
        }

        foreach (var row in result.Data!)
            Console.WriteLine($"{row.OuName}: {row.NetSales:N0}");
    }

    // ── 4. SQL Server – execute (INSERT/UPDATE/DELETE) ───────────────────────
    public static async Task ExecuteExample()
    {
        var result = await Executor.ExecuteAsync(new QueryRequest
        {
            ConnectionString = "Server=.;Database=JanaticsDB;Integrated Security=True;",
            Provider         = DbProvider.SqlServer,
            Sql              = "UPDATE FileRequests SET Status = @status WHERE Id = @id",
            Parameters       = new { status = "Approved", id = 42 }
        });

        Console.WriteLine(result.Success
            ? $"Updated {result.RowsAffected} row(s) in {result.Elapsed.TotalMilliseconds:F1} ms"
            : $"Failed: {result.ErrorMessage}");
    }
}

public sealed class SalesRow
{
    public string? OuName   { get; init; }
    public decimal NetSales  { get; init; }
    public decimal Target    { get; init; }
}
