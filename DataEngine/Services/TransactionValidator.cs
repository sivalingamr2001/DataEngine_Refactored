using DataEngine.Abstractions;
using DataEngine.Model;
using System.Globalization;

namespace DataEngine.Services;

/// <summary>
/// Validates TransactionRequest payload against the live table schema
/// before any database write is attempted.
///
/// Logic unchanged from original.
/// Error messages improved for clarity.
/// </summary>
public class TransactionValidator : ITransactionValidator
{
    public Task<(bool IsValid, string FailureReason)> ValidatePayloadAsync(
        TransactionRequest request,
        List<ColumnMetadata> schema,
        CancellationToken cancellationToken = default)
    {
        // O(1) column lookup map
        var schemaMap = schema.ToDictionary(
            c => c.ColumnName.ToLowerInvariant(),
            c => c);

        // ── Validate DELETE payloads ───────────────────────────────────
        if (request.DelProps.TryGetValue(request.TransactionEntityName, out var deleteRows))
        {
            var pkColumn = schema.FirstOrDefault(c => c.IsPrimaryKey);
            if (pkColumn is null)
                return Task.FromResult((false,
                    $"DELETE requested but table '{request.TransactionEntityName}' " +
                    $"has no primary key defined in INFORMATION_SCHEMA."));

            foreach (var row in deleteRows)
            {
                if (!row.TryGetValue(pkColumn.ColumnName, out var pkValue) || pkValue is null)
                    return Task.FromResult((false,
                        $"DELETE payload is missing primary key field '{pkColumn.ColumnName}'. " +
                        $"Every delete row must include the primary key value."));
            }
        }

        // ── Validate UPSERT payloads ───────────────────────────────────
        if (request.RenProps.TryGetValue(request.TransactionEntityName, out var upsertRows))
        {
            foreach (var row in upsertRows)
            {
                foreach (var field in row)
                {
                    // Block columns not present in the physical table
                    if (!schemaMap.TryGetValue(field.Key.ToLowerInvariant(), out var columnMeta))
                        return Task.FromResult((false,
                            $"Column '{field.Key}' does not exist on table " +
                            $"'{request.TransactionEntityName}'. " +
                            $"Check for typos or stale client schemas."));

                    // Null check on non-nullable columns
                    if (field.Value is null || field.Value is DBNull)
                    {
                        if (!columnMeta.IsNullable &&
                            !columnMeta.IsAutoIncrement &&
                            !columnMeta.HasDefaultValue)
                        {
                            return Task.FromResult((false,
                                $"Column '{field.Key}' is non-nullable and has no default value. " +
                                $"A null value is not permitted."));
                        }
                        continue;
                    }

                    // Type compatibility check
                    if (!IsValidTypeMapping(field.Value, columnMeta))
                        return Task.FromResult((false,
                            $"Type mismatch for column '{field.Key}': " +
                            $"received '{field.Value.GetType().Name}' " +
                            $"which is incompatible with database type '{columnMeta.DataType}'."));
                }
            }
        }

        return Task.FromResult((true, string.Empty));
    }

    private static bool IsValidTypeMapping(object value, ColumnMetadata column)
    {
        string raw = value.ToString() ?? string.Empty;

        return column.DataType.ToLowerInvariant() switch
        {
            "int" or "integer" or "bigint" or "smallint" or "tinyint" =>
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),

            "decimal" or "numeric" or "double" or "float" =>
                decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _),

            "datetime" or "timestamp" or "date" =>
                DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),

            "bit" or "boolean" =>
                bool.TryParse(raw, out _) || raw == "1" || raw == "0",

            "varchar" or "char" or "text" or "longtext" or "mediumtext" =>
                column.MaxCharacterLength is null || raw.Length <= column.MaxCharacterLength,

            _ => true // Unknown/custom types pass through
        };
    }
}
