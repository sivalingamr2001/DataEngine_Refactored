using Newtonsoft.Json;

namespace DataEngine.Model;

public class TransactionRequest
{
    /// <summary>
    /// Main table row data for INSERT or UPDATE operations.
    /// Keyed by entity/table name. Presence of a non-empty primary key value triggers UPDATE; absence triggers INSERT.
    /// Used for primary/main table operations.
    /// </summary>
    [JsonProperty("extendedProperties")]
    public Dictionary<string, object> ExtendedProperties { get; set; } = new();

    /// <summary>
    /// Child table rows to INSERT or UPDATE, keyed by entity/table name.
    /// Presence of a non-empty primary key value triggers UPDATE; absence triggers INSERT.
    /// Used for dependent/child table operations.
    /// </summary>
    [JsonProperty("renProps")]
    public Dictionary<string, List<Dictionary<string, object>>> RenProps { get; set; } = new();

    /// <summary>
    /// Rows to DELETE, keyed by entity/table name.
    /// Primary key must be present in every delete row.
    /// </summary>
    [JsonProperty("delProps")]
    public Dictionary<string, List<Dictionary<string, object>>> DelProps { get; set; } = new();

    /// <summary>
    /// The target database table name for this transaction.
    /// </summary>
    [JsonProperty("transactionEntityName")]
    public string TransactionEntityName { get; set; } = string.Empty;

    /// <summary>
    /// Caller-supplied correlation/idempotency ID. Written to all logs.
    /// </summary>
    [JsonProperty("transactionId")]
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>
    /// The user initiating the transaction. Written to audit logs.
    /// </summary>
    [JsonProperty("userId")]
    public string UserId { get; set; } = string.Empty;
}
