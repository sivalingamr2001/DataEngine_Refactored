using System.Text.Json;

namespace DataEngine.Services;

/// <summary>
/// Shared utility for unwrapping System.Text.Json JsonElement values
/// into native C# types that MySqlConnector understands.
///
/// Extracted from both DynamicReadEngine and DynamicTransactionProcessor
/// — previously duplicated in both classes.
/// </summary>
internal static class DataEngineJsonHelper
{
    /// <summary>
    /// Unwraps a JsonElement or any other value into a database-safe primitive.
    /// Booleans are mapped to 1/0 (MySQL TINYINT convention).
    /// </summary>
    public static object? GetRawValue(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDecimal(),
                JsonValueKind.True   => 1,   // MySQL TINYINT(1) convention
                JsonValueKind.False  => 0,
                JsonValueKind.Null   => null,
                _                    => element.GetRawText()
            };
        }

        // Handle native C# bool that slipped through without JSON wrapping
        if (value is bool boolVal)
            return boolVal ? 1 : 0;

        return value;
    }

    /// <summary>
    /// Scrubs all values in a row dictionary in-place.
    /// Called during payload normalization before validation and execution.
    /// </summary>
    public static void ScrubRow(Dictionary<string, object> row)
    {
        foreach (var key in row.Keys.ToList())
            row[key] = GetRawValue(row[key]) ?? DBNull.Value;
    }
}
