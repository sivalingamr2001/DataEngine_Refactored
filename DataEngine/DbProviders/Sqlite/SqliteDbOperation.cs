using DataEngine.Abstractions;
using DataEngine.Model;
using DataEngine.Services;
using Microsoft.Data.Sqlite;

namespace DataEngine.DbProviders.Sqlite;

public sealed class SqliteDynamicEngine : IDynamicEngine
{
    private readonly string _connectionString;

    public SqliteDynamicEngine(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<TransactionResult> TransactionProcess(TransactionRequest request, CancellationToken cancellationToken = default)
    {
        var result = new TransactionResult
        {
            TransactionId = request.TransactionId,
            Success = false
        };

        if (string.IsNullOrWhiteSpace(request.TransactionEntityName))
        {
            result.Message = "TransactionEntityName cannot be blank.";
            return result;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction) await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var tableName = request.TransactionEntityName.Trim();
            var pkColumn = await GetPrimaryKeyColumnAsync(connection, tableName, cancellationToken);
            if (pkColumn is null)
            {
                result.Message = $"Table '{tableName}' has no primary key.";
                return result;
            }

            // Process DELETE operations on child table records
            if (request.DelProps.TryGetValue(tableName, out var deleteRows))
            {
                foreach (var row in deleteRows)
                {
                    var pkValue = GetRowValue(row, pkColumn);
                    if (pkValue is null)
                    {
                        result.Message = $"Delete row missing primary key '{pkColumn}'.";
                        return result;
                    }

                    await ExecuteDeleteAsync(connection, transaction, tableName, pkColumn, pkValue, cancellationToken);
                    result.DeletedCount++;
                }
            }

            // Process UPSERT operations on child table records (RenProps)
            if (request.RenProps.TryGetValue(tableName, out var upsertRows))
            {
                var index = 0;
                foreach (var row in upsertRows)
                {
                    var pkValue = GetRowValue(row, pkColumn);
                    var isUpdate = pkValue is not null && !string.IsNullOrEmpty(pkValue.ToString());

                    if (isUpdate)
                    {
                        await ExecuteUpdateAsync(connection, transaction, tableName, row, pkColumn, pkValue!, cancellationToken);
                        result.UpdatedCount++;
                    }
                    else
                    {
                        long? newId = await ExecuteInsertAsync(connection, transaction, tableName, row, cancellationToken);
                        if (newId.HasValue)
                            result.Data[$"Inserted_{pkColumn}_{index}"] = newId.Value;
                        result.InsertedCount++;
                    }

                    index++;
                }
            }

            await transaction.CommitAsync(cancellationToken);
            result.Success = true;
            result.Message = $"Transaction committed. Inserted={result.InsertedCount} Updated={result.UpdatedCount} Deleted={result.DeletedCount}.";
            return result;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(cancellationToken);
            result.Message = $"Transaction failed: {ex.Message}";
            result.Exception = ex;
            return result;
        }
    }

    public async Task<FetchResult> ExecuteQuery(FetchConfig config, string connectionString, CancellationToken cancellationToken = default)
    {
        var result = new FetchResult { Success = false };
        if (string.IsNullOrWhiteSpace(config.QueryText))
        {
            result.Message = "QueryText cannot be blank.";
            return result;
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = config.QueryText;

        if (config.InputParameters.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in config.InputParameters.EnumerateObject())
                command.Parameters.AddWithValue($"@{prop.Name}", DataEngineJsonHelper.GetRawValue(prop.Value) ?? DBNull.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            result.Rows.Add(row);
        }

        result.Success = true;
        result.TotalCount = result.Rows.Count;
        result.Message = "Success";
        return result;
    }

    private static object? GetRowValue(Dictionary<string, object> row, string columnName)
    {
        if (row.TryGetValue(columnName, out var value))
            return value;

        return row.FirstOrDefault(kvp => string.Equals(kvp.Key, columnName, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static async Task<string?> GetPrimaryKeyColumnAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var pk = reader.GetInt32(5);
            if (pk == 1)
                return reader.GetString(1);
        }

        return null;
    }

    private static async Task<long?> ExecuteInsertAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string tableName, Dictionary<string, object> row, CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        var parameters = new List<string>();

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;

        foreach (var kvp in row)
        {
            if (string.Equals(kvp.Key, "Id", StringComparison.OrdinalIgnoreCase))
                continue;

            columns.Add($"\"{kvp.Key}\"");
            var parameterName = $"@{kvp.Key}";
            parameters.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, DataEngineJsonHelper.GetRawValue(kvp.Value) ?? DBNull.Value);
        }

        command.CommandText = $"INSERT INTO \"{tableName}\" ({string.Join(", ", columns)}) VALUES ({string.Join(", ", parameters)}); SELECT last_insert_rowid();";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null && result is long id ? id : null;
    }

    private static async Task ExecuteUpdateAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string tableName, Dictionary<string, object> row, string pkColumn, object pkValue, CancellationToken cancellationToken)
    {
        var assignments = new List<string>();

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;

        foreach (var kvp in row)
        {
            if (string.Equals(kvp.Key, pkColumn, StringComparison.OrdinalIgnoreCase))
                continue;

            assignments.Add($"\"{kvp.Key}\" = @{kvp.Key}");
            command.Parameters.AddWithValue($"@{kvp.Key}", DataEngineJsonHelper.GetRawValue(kvp.Value) ?? DBNull.Value);
        }

        command.Parameters.AddWithValue($"@{pkColumn}", DataEngineJsonHelper.GetRawValue(pkValue) ?? DBNull.Value);
        command.CommandText = $"UPDATE \"{tableName}\" SET {string.Join(", ", assignments)} WHERE \"{pkColumn}\" = @{pkColumn};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteDeleteAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, string tableName, string pkColumn, object pkValue, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"DELETE FROM \"{tableName}\" WHERE \"{pkColumn}\" = @{pkColumn};";
        command.Parameters.AddWithValue($"@{pkColumn}", DataEngineJsonHelper.GetRawValue(pkValue) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
