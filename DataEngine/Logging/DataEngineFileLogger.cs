using System.Text.Json;
using DataEngine.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DataEngine.Logging;

/// <summary>
/// Writes structured DataEngine log entries to a rolling daily log file.
/// Complements ILogger — does not replace it.
///
/// File path: {LogDirectory}/dataengine-{yyyy-MM-dd}.log
/// Default directory: logs/  (relative to application root)
/// Override via config: DataEngine:LogDirectory
///
/// Each line is a single JSON object — easy to grep and parse.
/// </summary>
public class DataEngineFileLogger
{
    private readonly string _logDirectory;
    private readonly ILogger<DataEngineFileLogger> _logger;

    // One SemaphoreSlim per log file path — prevents concurrent write corruption
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        _fileLocks = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public DataEngineFileLogger(IConfiguration configuration, ILogger<DataEngineFileLogger> logger)
    {
        _logger = logger;
        _logDirectory = configuration["DataEngine:LogDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "logs");
    }

    /// <summary>
    /// Write a structured log entry to today's rolling log file.
    /// Never throws — file write failures are swallowed and logged to ILogger.
    /// </summary>
    public async Task WriteAsync(DataEngineLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);

            var fileName = $"dataengine-{entry.Timestamp:yyyy-MM-dd}.log";
            var filePath = Path.Combine(_logDirectory, fileName);

            var line = JsonSerializer.Serialize(entry, _jsonOptions);

            var fileLock = _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
            await fileLock.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(filePath, line + Environment.NewLine);
            }
            finally
            {
                fileLock.Release();
            }
        }
        catch (Exception ex)
        {
            // File logging must NEVER break the primary execution path
            _logger.LogWarning(ex,
                "[DataEngineFileLogger] Failed to write log entry for transaction {TransactionId}",
                entry.TransactionId);
        }
    }

    // ── Convenience factory methods ────────────────────────────────────

    public static DataEngineLogEntry ForQuery(
        FetchConfig? config,
        string connectionString,
        bool success,
        long elapsedMs,
        string? errorMessage = null,
        Exception? exception = null)
    {
        return new DataEngineLogEntry
        {
            Level         = success ? "INFO" : "ERROR",
            OperationType = "QUERY",
            TransactionId = Guid.NewGuid().ToString("N")[..12],
            QueryText     = config?.QueryText,
            Parameters    = config?.InputParameters.ValueKind != System.Text.Json.JsonValueKind.Undefined
                ? config?.InputParameters.ToString()
                : null,
            Success       = success,
            ElapsedMs     = elapsedMs,
            ErrorMessage  = errorMessage ?? exception?.Message,
            StackTrace    = exception?.StackTrace,
            ExceptionType = exception?.GetType().FullName
        };
    }

    public static DataEngineLogEntry ForTransaction(
        TransactionRequest request,
        string operationType,
        bool success,
        long elapsedMs,
        string? errorMessage = null,
        Exception? exception = null)
    {
        return new DataEngineLogEntry
        {
            Level         = success ? "INFO" : "ERROR",
            OperationType = operationType,
            TransactionId = request.TransactionId,
            UserId        = request.UserId,
            EntityName    = request.TransactionEntityName,
            Success       = success,
            ElapsedMs     = elapsedMs,
            ErrorMessage  = errorMessage ?? exception?.Message,
            StackTrace    = exception?.StackTrace,
            ExceptionType = exception?.GetType().FullName
        };
    }
}
