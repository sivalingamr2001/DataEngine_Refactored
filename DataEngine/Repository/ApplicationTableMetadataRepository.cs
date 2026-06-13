using DataEngine.Cache;
using DataEngine.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace DataEngine.Repository;

/// <summary>
/// Reads table column metadata from INFORMATION_SCHEMA.
///
/// Changes from original:
/// - Database name is no longer hardcoded to 'jan_itaccessreq_db'.
///   It is read from the connection string itself using MySqlConnectionStringBuilder.
///   This makes the library reusable across any database, not just one project.
/// - Schema metadata is cached via ISchemaCache to reduce repeated INFORMATION_SCHEMA hits.
/// - Logging improved with structured context.
/// </summary>
public class ApplicationTableMetadataRepository
{
    private readonly string _connectionString;
    private readonly string _databaseName;
    private readonly ISchemaCache _schemaCache;
    private readonly ILogger<ApplicationTableMetadataRepository> _logger;

    public ApplicationTableMetadataRepository(
        IConfiguration configuration,
        ISchemaCache schemaCache,
        ILogger<ApplicationTableMetadataRepository> logger)
    {
        _schemaCache = schemaCache;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "DefaultConnection string not found in configuration. " +
                "Add ConnectionStrings:DefaultConnection to appsettings.json.");

        // Extract database name from connection string — no more hardcoding
        var builder = new MySqlConnectionStringBuilder(_connectionString);
        _databaseName = builder.Database
            ?? throw new InvalidOperationException(
                "The DefaultConnection string must include a 'Database' parameter.");

        _logger = logger;
    }

    /// <summary>
    /// Loads column metadata for a table from INFORMATION_SCHEMA.
    /// Returns empty list (not null) when the table is not found.
    /// </summary>
    public async Task<List<ColumnMetadata>> GetTableSchemaMetadataAsync(
        string entityName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            throw new ArgumentException("Entity name cannot be null or empty.", nameof(entityName));

        var cacheKey = entityName.Trim();
        var cachedMetadata = await _schemaCache.GetAsync(cacheKey);
        if (cachedMetadata is not null)
        {
            _logger.LogDebug("[MetadataRepository] Schema cache hit for table '{Entity}'.", entityName);
            return cachedMetadata.Columns;
        }

        const string query = @"
            SELECT
                COLUMN_NAME,
                DATA_TYPE,
                IS_NULLABLE,
                COLUMN_KEY,
                EXTRA,
                CHARACTER_MAXIMUM_LENGTH,
                COLUMN_DEFAULT
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @databaseName
              AND LOWER(TABLE_NAME) = LOWER(@entityName)
            ORDER BY ORDINAL_POSITION";

        try
        {
            await using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new MySqlCommand(query, connection);
            command.Parameters.AddWithValue("@databaseName", _databaseName);
            command.Parameters.AddWithValue("@entityName", entityName);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = new List<ColumnMetadata>();

            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new ColumnMetadata
                {
                    ColumnName      = reader.GetString(0),
                    DataType        = reader.GetString(1),
                    IsNullable      = reader.GetString(2) == "YES",
                    IsPrimaryKey    = reader.GetString(3) == "PRI",
                    IsAutoIncrement = !reader.IsDBNull(4) &&
                                      reader.GetString(4).Contains("auto_increment",
                                          StringComparison.OrdinalIgnoreCase),
                    MaxCharacterLength = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    HasDefaultValue    = !reader.IsDBNull(6),
                    DefaultValue       = reader.IsDBNull(6) ? null : reader.GetString(6)
                });
            }

            if (columns.Count == 0)
            {
                _logger.LogWarning(
                    "[MetadataRepository] No columns found for table '{Entity}' in database '{Database}'. " +
                    "Table may not exist.",
                    entityName, _databaseName);
            }
            else
            {
                _logger.LogDebug(
                    "[MetadataRepository] Loaded {Count} column(s) for table '{Entity}'.",
                    columns.Count, entityName);

                await _schemaCache.SetAsync(cacheKey, new TableMetadata
                {
                    TableName = entityName,
                    Columns   = columns
                });
            }

            return columns;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[MetadataRepository] Failed to load schema for table '{Entity}' in database '{Database}'.",
                entityName, _databaseName);
            throw;
        }
    }
}
