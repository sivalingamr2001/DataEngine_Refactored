using Newtonsoft.Json;

namespace DataEngine.Model;

public class TransactionResult
{
    public bool Success { get; set; }
    public string TransactionId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Number of rows inserted during this transaction.
    /// </summary>
    public int InsertedCount { get; set; }

    /// <summary>
    /// Number of rows updated during this transaction.
    /// </summary>
    public int UpdatedCount { get; set; }

    /// <summary>
    /// Number of rows deleted during this transaction.
    /// </summary>
    public int DeletedCount { get; set; }

    /// <summary>
    /// Inserted primary key values keyed as Inserted_{PkColumn}_{index}.
    /// </summary>
    public Dictionary<string, object> Data { get; set; } = new();

    /// <summary>
    /// Populated when Success = false.
    /// Hidden from JSON serialization — for internal logging only.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    [JsonIgnore]
    public Exception? Exception { get; set; }
}
