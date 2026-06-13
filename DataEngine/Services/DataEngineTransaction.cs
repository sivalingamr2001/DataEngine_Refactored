using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DataEngine.Abstractions;
using DataEngine.Logging;
using DataEngine.Model;
using DataEngine.Repository;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DataEngine.Services;

/// <summary>
/// Central DataEngine execution layer implementing ITransaction.
///
/// Provides:
///   TransactionProcess — all write operations (INSERT / UPDATE / DELETE) in one atomic transaction
///   ExecuteQuery       — all read operations (SELECT with filter/sort/paginate)
///
/// Design principles:
///   - Every code path is wrapped in try/catch with structured logging
///   - No exception is swallowed silently — all failures are logged with full context
///   - GetRawValue is centralised in DataEngineJsonHelper (no duplication)
///   - Backwards-compatible: IDynamicTransactionProcessor and IDynamicReadEngine
///     both delegate here so existing consumers need zero changes
/// </summary>
public class DataEngineTransaction(
    ApplicationTableMetadataRepository metadataRepository,
    ITransactionValidator transactionValidator,
    IQueryValidator queryValidator,
    DataEngineFileLogger fileLogger,
    IConfiguration configuration,
    ILogger<DataEngineTransaction> logger) : IDynamicEngine
{
    private readonly ApplicationTableMetadataRepository _metadataRepository = metadataRepository;
    private readonly ITransactionValidator _transactionValidator = transactionValidator;
    private readonly IQueryValidator _queryValidator = queryValidator;
    private readonly DataEngineFileLogger _fileLogger = fileLogger;
    private readonly ILogger<DataEngineTransaction> _logger = logger;
    private readonly string _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "DefaultConnection string not found. " +
                "Add ConnectionStrings:DefaultConnection to appsettings.json.");

    // ═════════════════════════════════════════════════════════════════
    //  ITransaction.TransactionProcess
    //  Central write pipeline: DELETE then INSERT/UPDATE, all atomic.
    // ═════════════════════════════════════════════════════════════════

    public async Task<TransactionResult> TransactionProcess(
        TransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw     = Stopwatch.StartNew();
        var result = new TransactionResult
        {
            TransactionId = request.TransactionId,
            Success       = false
        };

        // ── Guard: entity name is required ────────────────────────────
        if (string.IsNullOrWhiteSpace(request.TransactionEntityName))
        {
            result.Message = "TransactionEntityName cannot be blank.";
            _logger.LogWarning(
                "[TransactionProcess] Rejected — TransactionEntityName is blank. " +
                "TransactionId: {TransactionId}", request.TransactionId);
            return result;
        }

        _logger.LogInformation(
            "[TransactionProcess] Starting | TxId={TransactionId} | Entity={Entity} | User={User}",
            request.TransactionId, request.TransactionEntityName, request.UserId);

        // ── Step 1: Extract child table rows (RenProps) and delete rows ────────────────────
        // RenProps: child table records for INSERT or UPDATE
        // DelProps: child table records to DELETE
        request.RenProps.TryGetValue(request.TransactionEntityName, out var upsertRows);
        request.DelProps.TryGetValue(request.TransactionEntityName, out var deleteRows);

        // ── Step 2: Scrub JsonElement values out of all row dictionaries
        ScrubAllRows(upsertRows);
        ScrubAllRows(deleteRows);

        try
        {
            // ── Step 3: Load schema ───────────────────────────────────
            var schema = await _metadataRepository.GetTableSchemaMetadataAsync(
                request.TransactionEntityName, cancellationToken);

            if (schema.Count == 0)
            {
                result.Message =
                    $"Schema lookup returned no columns for '{request.TransactionEntityName}'. " +
                    $"Verify the table exists and the connection string points to the correct database.";

                _logger.LogError(
                    "[TransactionProcess] Schema empty for entity '{Entity}'. TxId={TxId}",
                    request.TransactionEntityName, request.TransactionId);

                await _fileLogger.WriteAsync(DataEngineFileLogger.ForTransaction(
                    request, "TRANSACTION", false, sw.ElapsedMilliseconds, result.Message));

                return result;
            }

            // ── Step 4: Validate payload ───────────────────────────────
            var (isValid, failureReason) = await _transactionValidator.ValidatePayloadAsync(
                request, schema, cancellationToken);

            if (!isValid)
            {
                result.Message = $"Validation failed: {failureReason}";
                _logger.LogWarning(
                    "[TransactionProcess] Validation failed | TxId={TxId} | Reason={Reason}",
                    request.TransactionId, failureReason);

                await _fileLogger.WriteAsync(DataEngineFileLogger.ForTransaction(
                    request, "TRANSACTION", false, sw.ElapsedMilliseconds, result.Message));

                return result;
            }

            // ── Step 5: Open connection + begin transaction ────────────
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                // ── Step 6: DELETE pass ───────────────────────────────
                if (deleteRows is not null)
                {
                    foreach (var row in deleteRows)
                    {
                        await ExecuteDeleteAsync(
                            connection, transaction,
                            request.TransactionEntityName,
                            schema, row, cancellationToken);

                        result.DeletedCount++;
                    }
                }

                // ── Step 7: UPSERT pass ───────────────────────────────
                if (upsertRows is not null)
                {
                    var pkColumn = schema.FirstOrDefault(c => c.IsPrimaryKey);
                    int index    = 0;

                    foreach (var row in upsertRows)
                    {
                        object? rawPkValue = pkColumn is not null &&
                                             row.TryGetValue(pkColumn.ColumnName, out var pkVal)
                            ? pkVal
                            : null;

                        bool isUpdate = pkColumn is not null
                                     && rawPkValue is not null
                                     && rawPkValue is not DBNull
                                     && !string.IsNullOrEmpty(rawPkValue.ToString());

                        if (isUpdate)
                        {
                            await ExecuteUpdateAsync(
                                connection, transaction,
                                request.TransactionEntityName,
                                schema, row, pkColumn!, cancellationToken);

                            result.UpdatedCount++;
                        }
                        else
                        {
                            long? newId = await ExecuteInsertAsync(
                                connection, transaction,
                                request.TransactionEntityName,
                                schema, row, cancellationToken);

                            if (newId.HasValue && pkColumn is not null)
                                result.Data[$"Inserted_{pkColumn.ColumnName}_{index}"] = newId.Value;

                            result.InsertedCount++;
                        }

                        index++;
                    }
                }

                // ── Step 8: Commit ────────────────────────────────────
                await transaction.CommitAsync(cancellationToken);

                result.Success = true;
                result.Message = $"Transaction committed. " +
                                 $"Inserted={result.InsertedCount} " +
                                 $"Updated={result.UpdatedCount} " +
                                 $"Deleted={result.DeletedCount}.";

                sw.Stop();

                _logger.LogInformation(
                    "[TransactionProcess] Committed | TxId={TxId} | Entity={Entity} | " +
                    "Inserted={I} Updated={U} Deleted={D} | {Ms}ms",
                    request.TransactionId, request.TransactionEntityName,
                    result.InsertedCount, result.UpdatedCount, result.DeletedCount,
                    sw.ElapsedMilliseconds);

                await _fileLogger.WriteAsync(DataEngineFileLogger.ForTransaction(
                    request, "TRANSACTION", true, sw.ElapsedMilliseconds));

                return result;
            }
            catch (Exception ex)
            {
                // Rollback on any mid-transaction failure
                await transaction.RollbackAsync(cancellationToken);
                sw.Stop();

                _logger.LogError(ex,
                    "[TransactionProcess] ROLLED BACK | TxId={TxId} | Entity={Entity} | " +
                    "Exception={ExType} | {Ms}ms",
                    request.TransactionId, request.TransactionEntityName,
                    ex.GetType().Name, sw.ElapsedMilliseconds);

                await _fileLogger.WriteAsync(DataEngineFileLogger.ForTransaction(
                    request, "TRANSACTION", false, sw.ElapsedMilliseconds,
                    exception: ex));

                throw; // Re-throw so outer catch can set result.Exception
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success   = false;
            result.Message   = $"Transaction aborted: {ex.Message}";
            result.Exception = ex;

            _logger.LogError(ex,
                "[TransactionProcess] Aborted | TxId={TxId} | Entity={Entity} | {Ms}ms",
                request.TransactionId, request.TransactionEntityName,
                sw.ElapsedMilliseconds);

            await _fileLogger.WriteAsync(DataEngineFileLogger.ForTransaction(
                request, "TRANSACTION", false, sw.ElapsedMilliseconds,
                exception: ex));

            return result;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  ITransaction.ExecuteQuery
    //  Central read pipeline: validate → build SQL → execute → stream rows
    // ═════════════════════════════════════════════════════════════════

    public async Task<FetchResult> ExecuteQuery(
        FetchConfig config,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var sw     = Stopwatch.StartNew();
        var result = new FetchResult { Success = false };

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            result.Message = "Connection string is required for ExecuteQuery.";
            _logger.LogWarning("[ExecuteQuery] Rejected — connection string is blank.");
            return result;
        }

        // ── Step 1: Validate config ────────────────────────────────────
        var (isValid, failureReason) = _queryValidator.ValidateQueryConfig(config);
        if (!isValid)
        {
            result.Message = $"Validation failed: {failureReason}";
            _logger.LogWarning("[ExecuteQuery] Validation failed: {Reason}", failureReason);
            return result;
        }

        if (string.IsNullOrWhiteSpace(config.QueryText))
        {
            result.Message = "QueryText cannot be null or whitespace.";
            _logger.LogWarning("[ExecuteQuery] Rejected — QueryText is blank.");
            return result;
        }

        using var command = new MySqlCommand();

        try
        {
            string baseSql = config.QueryText.Trim();

            // ── Step 2: Bind named input parameters ───────────────────
            if (config.InputParameters.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in config.InputParameters.EnumerateObject())
                {
                    command.Parameters.AddWithValue(
                        $"@{prop.Name}",
                        DataEngineJsonHelper.GetRawValue(prop.Value) ?? DBNull.Value);
                }
            }

            var queryBuilder  = new StringBuilder(baseSql);
            int paramCounter  = 0;

            // ── Step 3: Append filter conditions ──────────────────────
            if (config.EnableServerSideFiltering && config.FilterConditions is not null)
            {
                bool hasWhere = baseSql.Contains("WHERE", StringComparison.OrdinalIgnoreCase);

                foreach (var filter in config.FilterConditions)
                {
                    if (string.IsNullOrWhiteSpace(filter.Field)) continue;

                    string cleanField = filter.Field.Replace("`", "");
                    queryBuilder.Append(hasWhere ? " AND " : " WHERE ");
                    hasWhere = true;

                    string paramName  = $"@f_p_{paramCounter++}";
                    object? cleanVal  = DataEngineJsonHelper.GetRawValue(filter.Value);

                    queryBuilder.Append(filter.Operator.ToLowerInvariant() switch
                    {
                        "eq"         => $" `{cleanField}` = {paramName} ",
                        "neq"        => $" `{cleanField}` != {paramName} ",
                        "gt"         => $" `{cleanField}` > {paramName} ",
                        "lt"         => $" `{cleanField}` < {paramName} ",
                        "gte"        => $" `{cleanField}` >= {paramName} ",
                        "lte"        => $" `{cleanField}` <= {paramName} ",
                        "contains"   => $" `{cleanField}` LIKE {paramName} ",
                        "startswith" => $" `{cleanField}` LIKE {paramName} ",
                        "endswith"   => $" `{cleanField}` LIKE {paramName} ",
                        _            => $" `{cleanField}` = {paramName} "
                    });

                    // Wrap LIKE values
                    if (filter.Operator.Equals("contains", StringComparison.OrdinalIgnoreCase))
                        cleanVal = $"%{cleanVal}%";
                    else if (filter.Operator.Equals("startswith", StringComparison.OrdinalIgnoreCase))
                        cleanVal = $"{cleanVal}%";
                    else if (filter.Operator.Equals("endswith", StringComparison.OrdinalIgnoreCase))
                        cleanVal = $"%{cleanVal}";

                    command.Parameters.AddWithValue(paramName, cleanVal ?? DBNull.Value);
                }
            }

            // ── Step 4: Append ORDER BY ───────────────────────────────
            if (config.EnableServerSideSorting && !string.IsNullOrWhiteSpace(config.SortField))
            {
                string cleanSort = config.SortField.Replace("`", "");
                string dir       = config.SortDirection.ToLowerInvariant() == "desc" ? "DESC" : "ASC";
                queryBuilder.Append($" ORDER BY `{cleanSort}` {dir}");
            }

            // ── Step 5: Append LIMIT / OFFSET ─────────────────────────
            int offset = (Math.Max(1, config.PageNumber) - 1) * config.Count;
            queryBuilder.Append(" LIMIT @l_offset, @l_count;");
            command.Parameters.AddWithValue("@l_offset", offset);
            command.Parameters.AddWithValue("@l_count", config.Count);

            // ── Step 6: Execute ───────────────────────────────────────
            await using var connection = new MySqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            command.Connection  = connection;
            command.CommandText = queryBuilder.ToString();

            _logger.LogDebug(
                "[ExecuteQuery] Executing | SQL={Sql} | Params={ParamCount}",
                command.CommandText, command.Parameters.Count);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Rows.Add(row);
            }

            sw.Stop();
            result.Success    = true;
            result.TotalCount = result.Rows.Count;
            result.Message    = "Success";

            _logger.LogInformation(
                "[ExecuteQuery] Success | Rows={Count} | {Ms}ms",
                result.TotalCount, sw.ElapsedMilliseconds);

            await _fileLogger.WriteAsync(DataEngineFileLogger.ForQuery(
                config, connectionString, true, sw.ElapsedMilliseconds));

            return result;
        }
        catch (MySqlException ex)
        {
            sw.Stop();
            result.Success = false;
            result.Message = $"Database error: {ex.Message}";

            _logger.LogError(ex,
                "[ExecuteQuery] MySqlException | ErrorCode={Code} | {Ms}ms",
                ex.ErrorCode, sw.ElapsedMilliseconds);

            await _fileLogger.WriteAsync(DataEngineFileLogger.ForQuery(
                config, connectionString, false, sw.ElapsedMilliseconds,
                exception: ex));

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.Message = $"Query execution failed: {ex.Message}";

            _logger.LogError(ex,
                "[ExecuteQuery] Unhandled exception | {ExType} | {Ms}ms",
                ex.GetType().Name, sw.ElapsedMilliseconds);

            await _fileLogger.WriteAsync(DataEngineFileLogger.ForQuery(
                config, connectionString, false, sw.ElapsedMilliseconds,
                exception: ex));

            return result;
        }
    }

    // ═════════════════════════════════════════════════════════════════
    //  Private SQL execution helpers
    // ═════════════════════════════════════════════════════════════════

    private async Task<long?> ExecuteInsertAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        string tableName,
        List<ColumnMetadata> schema,
        Dictionary<string, object> row,
        CancellationToken ct)
    {
        var columnList = new List<string>();
        var paramList  = new List<string>();

        using var command = new MySqlCommand("", conn, tx);

        foreach (var col in schema)
        {
            if (col.IsAutoIncrement) continue;
            if (!row.TryGetValue(col.ColumnName, out var value)) continue;

            columnList.Add($"`{col.ColumnName}`");
            paramList.Add($"@{col.ColumnName}");
            command.Parameters.AddWithValue(
                $"@{col.ColumnName}",
                DataEngineJsonHelper.GetRawValue(value) ?? DBNull.Value);
        }

        if (columnList.Count == 0)
        {
            _logger.LogWarning(
                "[ExecuteInsert] No writable columns found for table '{Table}'. Row skipped.",
                tableName);
            return null;
        }

        command.CommandText =
            $"INSERT INTO `{tableName}` ({string.Join(", ", columnList)}) " +
            $"VALUES ({string.Join(", ", paramList)}); " +
            $"SELECT LAST_INSERT_ID();";

        _logger.LogDebug(
            "[ExecuteInsert] SQL={Sql}", command.CommandText);

        var res = await command.ExecuteScalarAsync(ct);
        return res is not null && res is not DBNull ? Convert.ToInt64(res) : null;
    }

    private async Task ExecuteUpdateAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        string tableName,
        List<ColumnMetadata> schema,
        Dictionary<string, object> row,
        ColumnMetadata pkColumn,
        CancellationToken ct)
    {
        var setFields = new List<string>();
        using var command = new MySqlCommand("", conn, tx);

        foreach (var col in schema)
        {
            if (col.IsPrimaryKey) continue;
            if (!row.TryGetValue(col.ColumnName, out var value)) continue;

            setFields.Add($"`{col.ColumnName}` = @{col.ColumnName}");
            command.Parameters.AddWithValue(
                $"@{col.ColumnName}",
                DataEngineJsonHelper.GetRawValue(value) ?? DBNull.Value);
        }

        if (setFields.Count == 0)
        {
            _logger.LogWarning(
                "[ExecuteUpdate] No updatable fields found for table '{Table}'. Row skipped.",
                tableName);
            return;
        }

        command.CommandText =
            $"UPDATE `{tableName}` SET {string.Join(", ", setFields)} " +
            $"WHERE `{pkColumn.ColumnName}` = @{pkColumn.ColumnName};";

        command.Parameters.AddWithValue(
            $"@{pkColumn.ColumnName}",
            DataEngineJsonHelper.GetRawValue(row[pkColumn.ColumnName]) ?? DBNull.Value);

        _logger.LogDebug(
            "[ExecuteUpdate] SQL={Sql}", command.CommandText);

        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task ExecuteDeleteAsync(
        MySqlConnection conn,
        MySqlTransaction tx,
        string tableName,
        List<ColumnMetadata> schema,
        Dictionary<string, object> row,
        CancellationToken ct)
    {
        var pkColumn = schema.FirstOrDefault(c => c.IsPrimaryKey)
            ?? throw new InvalidOperationException(
                $"DELETE failed: table '{tableName}' has no primary key defined.");

        if (!row.TryGetValue(pkColumn.ColumnName, out var pkValue) || pkValue is null || pkValue is DBNull)
            throw new InvalidOperationException(
                $"DELETE failed: primary key '{pkColumn.ColumnName}' value is missing or null in delete row.");

        string sql = $"DELETE FROM `{tableName}` WHERE `{pkColumn.ColumnName}` = @{pkColumn.ColumnName};";

        using var command = new MySqlCommand(sql, conn, tx);
        command.Parameters.AddWithValue(
            $"@{pkColumn.ColumnName}",
            DataEngineJsonHelper.GetRawValue(pkValue) ?? DBNull.Value);

        _logger.LogDebug("[ExecuteDelete] SQL={Sql}", sql);

        await command.ExecuteNonQueryAsync(ct);
    }

    // ── Payload scrubbing ─────────────────────────────────────────────

    private static void ScrubAllRows(List<Dictionary<string, object>>? rows)
    {
        if (rows is null) return;
        foreach (var row in rows)
            DataEngineJsonHelper.ScrubRow(row);
    }
}
