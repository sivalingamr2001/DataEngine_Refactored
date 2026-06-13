namespace DataEngine.Logging;

/// <summary>
/// Structured log entry written to the rolling log file for every
/// query execution and transaction operation.
/// </summary>
public class DataEngineLogEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Level { get; set; } = "INFO";
    public string OperationType { get; set; } = string.Empty;  // QUERY | INSERT | UPDATE | DELETE | TRANSACTION
    public string TransactionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string? QueryText { get; set; }
    public string? Parameters { get; set; }
    public bool Success { get; set; }
    public long ElapsedMs { get; set; }
    public string? ErrorMessage { get; set; }
    public string? StackTrace { get; set; }
    public string? ExceptionType { get; set; }
}
